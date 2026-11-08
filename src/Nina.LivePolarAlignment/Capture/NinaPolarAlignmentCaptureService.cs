using System;
using System.Linq;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.PlateSolving;
using NINA.Profile.Interfaces;

namespace Nina.LivePolarAlignment.Capture;

/// <summary>Uses NINA capture/solve settings with separately requested, opt-in primary-axis rotation. Never syncs or GoTo.</summary>
[Export(typeof(IPolarAlignmentCaptureService))]
[PartCreationPolicy(CreationPolicy.Shared)]
public sealed partial class NinaPolarAlignmentCaptureService : IPolarAlignmentCaptureService {
    private readonly IProfileService profiles;
    private readonly ICameraMediator camera;
    private readonly IImagingMediator imaging;
    private readonly ITelescopeMediator telescope;
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly SolveHintCache solveHints = new();
    public Core.PixelPoint? PolarisCandidate { get; set; }
    private volatile bool manualPolarisAdjustment;
    private Core.PixelPoint? fixedPolarisSeed;
    public void SetManualPolarisAdjustment(Core.PixelPoint? normalizedTarget) {
        if(normalizedTarget is {} target)fixedPolarisSeed=target;
        manualPolarisAdjustment=normalizedTarget!=null;
    }
    private (SolveHintContext Context, SolvedPolarFrame Frame)? streamingAnchor;
    public int StreamingBinning { get; set; } = 2;
    public event Action<StreamingPreview>? StreamingPreviewAvailable;
    public void ResetSolveHint() { solveHints.Clear(); streamingAnchor = null; calibratedPatternScale=null;manualPolarisAdjustment=false;fixedPolarisSeed=null; }
    public bool CameraConnected => camera.GetInfo()?.Connected == true;
    public bool MountConnected => telescope.GetInfo()?.Connected == true;

    [ImportingConstructor]
    public NinaPolarAlignmentCaptureService(IProfileService profiles, ICameraMediator camera,
        IImagingMediator imaging, ITelescopeMediator telescope) {
        this.profiles = profiles;
        this.camera = camera;
        this.imaging = imaging;
        this.telescope = telescope;
    }

    public event Action<BitmapSource>? PreviewAvailable;
    private BitmapSource? PublishPreview(BitmapSource? preview) {
        if (preview == null) return null;
        if (!preview.IsFrozen) {
            preview = preview.Clone();
            if (!preview.CanFreeze) return null;
            preview.Freeze();
        }
        PreviewAvailable?.Invoke(preview);
        return preview;
    }

    public Task<SolvedPolarFrame> CaptureAndSolveAsync(double exposureSeconds, CancellationToken token,
        IProgress<ApplicationStatus>? progress = null) => CaptureAndSolveCoreAsync(exposureSeconds, token, progress);

    private async Task<SolvedPolarFrame> CaptureAndSolveCoreAsync(double exposureSeconds, CancellationToken token,
        IProgress<ApplicationStatus>? progress, Func<CancellationToken, Task>? afterDownload = null,
        Func<SolvedPolarFrame, Task>? referenceSolved = null) {
        progress ??= SilentProgress.Instance;
        if (!double.IsFinite(exposureSeconds) || exposureSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(exposureSeconds), "Exposure must be a positive number of seconds.");
        if (!await captureGate.WaitAsync(0, token))
            throw new InvalidOperationException("A polar alignment capture is already running.");
        var registered = false;
        try {
            token.ThrowIfCancellationRequested();
            var info = camera.GetInfo();
            if (info == null || !info.Connected)
                throw new InvalidOperationException("Connect a camera in NINA before capturing an alignment frame.");
            if (info.IsExposing || info.LiveViewEnabled || !camera.IsFreeToCapture(this))
                throw new InvalidOperationException("The camera is busy. Stop the other capture or sequence first.");
            if ((info.ExposureMin > 0 && exposureSeconds < info.ExposureMin)
                || (info.ExposureMax > 0 && exposureSeconds > info.ExposureMax))
                throw new InvalidOperationException("The requested exposure is outside the camera's supported range.");

            ValidateAlignmentCamera();
            var profile = profiles.ActiveProfile;
            var settings = profile.PlateSolveSettings;
            var (focalLength, pixelSize) = AlignmentOptics;
            if (!double.IsFinite(focalLength) || focalLength <= 0 || !double.IsFinite(pixelSize) || pixelSize <= 0)
                throw new InvalidOperationException("Set the telescope focal length and camera pixel size in the active NINA profile.");

            // Request full-frame captures at the configured solve binning.
            // No filter or readout-mode changes are made by this service.
            var binning = PoleMasterMode ? (short)1 : (short)Math.Max(1, (int)settings.Binning);
            var sequence = new CaptureSequence(exposureSeconds, CaptureSequence.ImageTypes.SNAPSHOT,
                null!, new BinningMode(binning, binning), 1) { Gain = PoleMasterMode ? CameraGain : settings.Gain };
            var hintContext = new SolveHintContext(profile.Id, info.DeviceId ?? profile.CameraSettings.Id ?? string.Empty, focalLength, pixelSize);
            var previousSolution = solveHints.Get(hintContext);
            NINA.Core.Utility.Logger.Info($"Polar solve hint: {(previousSolution != null ? "previous solved frame" : "mount or blind search")}");
            var parameter = new PlateSolveParameter {
                Binning = binning,
                Coordinates = previousSolution ?? InitialAlignmentHint()!,
                FocalLength = focalLength,
                PixelSize = pixelSize,
                DownSampleFactor = PoleMasterMode ? 1 : settings.DownSampleFactor,
                MaxObjects = settings.MaxObjects,
                Regions = settings.Regions,
                SearchRadius = PoleMasterMode ? Math.Max(15, settings.SearchRadius) : settings.SearchRadius,
                BlindFailoverEnabled = settings.BlindFailoverEnabled,
                DisableNotifications = true
            };
            var solver = UsePolarisPattern ? null : new ImageSolver(PlateSolverFactory.GetPlateSolver(settings),
                PlateSolverFactory.GetBlindSolver(settings));
            camera.RegisterCaptureBlock(this);
            registered = true;
            progress?.Report(new ApplicationStatus { Status = "Capturing polar alignment frame" });
            var started = DateTimeOffset.UtcNow;
            var timing = System.Diagnostics.Stopwatch.StartNew();
            // CaptureImage skips NINA's full image-processing/statistics pipeline.
            var exposure = await imaging.CaptureImage(sequence, token, progress!);
            token.ThrowIfCancellationRequested();
            if (exposure == null) throw new InvalidOperationException("NINA did not return an image from the camera.");
            NINA.Core.Utility.Logger.Info($"Polar preview: download complete at {timing.ElapsedMilliseconds} ms");
            var image = await Task.Run(() => exposure.ToImageData(progress!, token), token);
            token.ThrowIfCancellationRequested();
            var preparedPreview = await Task.Run(() => QuickLookPreview.Prepare(image, token, PreviewStretchStrength,PreviewStretchEnabled), token);
            lastPreparedPreview = preparedPreview;
            var preview = preparedPreview.Image;
            PublishPreview(preview);
            // Data binding and WPF rendering must run before any solver preparation.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null)
                await dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle, token);
            NINA.Core.Utility.Logger.Info($"Polar preview: published before solve at {timing.ElapsedMilliseconds} ms ({preview.PixelWidth}x{preview.PixelHeight})");
            var midpoint = image.MetaData.Image.ExposureMidPoint;
            var capturedAt = midpoint == DateTime.MinValue
                ? started.AddSeconds(exposureSeconds / 2)
                : new DateTimeOffset(midpoint.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(midpoint, DateTimeKind.Utc) : midpoint).ToUniversalTime();
            if (afterDownload != null)
                return await Core.ConcurrentReference.RunAsync(SolveAsync, afterDownload, token);
            return await SolveAsync(token);

            async Task<SolvedPolarFrame> SolveAsync(CancellationToken solveToken) {
                progress?.Report(new ApplicationStatus { Status = "Solving polar alignment frame" });
                var result = await SolveAlignmentImageAsync(image,parameter,solver,solveToken);
                NINA.Core.Utility.Logger.Info($"Polar preview: solve finished at {timing.ElapsedMilliseconds} ms");
                solveToken.ThrowIfCancellationRequested();
                if (!result.Success || result.Coordinates == null)
                    throw new InvalidOperationException("Plate solve failed. Check focus, stars, exposure, and the configured NINA solver.");
                if (!double.IsFinite(result.Coordinates.RADegrees) || !double.IsFinite(result.Coordinates.Dec)
                    || !double.IsFinite(result.PositionAngle) || !double.IsFinite(result.Pixscale) || result.Pixscale <= 0)
                    throw new InvalidOperationException("The solver returned an invalid position or image scale.");
                solveHints.Store(hintContext, result.Coordinates);
                preview = await Task.Run(() => preparedPreview.CreateFullResolution(solveToken), solveToken);
                NINA.Core.Utility.Logger.Info($"Polar preview: full-resolution display ready at {timing.ElapsedMilliseconds} ms");
                var dimensions = image.Properties;
                var stars=await Task.Run(()=>FindAlignmentStars(image,preparedPreview,solveToken),solveToken);
                var solved = new SolvedPolarFrame(result.Coordinates.RADegrees, result.Coordinates.Dec,
                    result.PositionAngle, result.Pixscale, dimensions.Width, dimensions.Height,
                    result.Flipped, capturedAt, preview, image, Stars:new(UsePolarisPattern?patternMatchedStars!:stars.Select((p,i)=>new Core.TrackedStar(i+1,p)).ToArray(),dimensions.Width,dimensions.Height,UsePolarisPattern,capturedAt,UsePolarisPattern?patternTemplateStars:null));
                if (PoleMasterMode) PoleMasterSetup.ValidateSolution(solved, profile.AstrometrySettings.Latitude);
                streamingAnchor = (hintContext, solved);
                solveToken.ThrowIfCancellationRequested();
                if (referenceSolved != null) await referenceSolved(solved);
                return solved;
            }

        } finally {
            try {
                if (registered) camera.ReleaseCaptureBlock(this);
            } finally {
                captureGate.Release();
                progress?.Report(new ApplicationStatus { Status = string.Empty });
            }
        }
    }
}
