using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using Nina.LivePolarAlignment.Core;

namespace Nina.LivePolarAlignment.Capture;

public sealed partial class NinaPolarAlignmentCaptureService {
    // QHY's native driver implements LiveView even when CanShowLiveView is false.
    // Detect the driver type, never a camera model or display-name allowlist.
    public bool SupportsStreaming {
        get {
            var info = camera.GetInfo();
            if (info?.Connected != true) return false;
            if (info.DeviceId?.StartsWith("ZWOptical_", StringComparison.OrdinalIgnoreCase) == true
                || info.DeviceId?.StartsWith("ToupTek_", StringComparison.OrdinalIgnoreCase) == true) return true;
            if (camera.GetDevice() is not QHYCamera qhy) return false;
            // The host applies the snapshot readout mode to LiveView, which can differ
            // from CameraInfo.ReadoutMode. Keep the known Bin3 video-mode exception.
            var modes = info.ReadoutModes?.ToArray();
            int index = qhy.ReadoutModeForSnapImages;
            var mode = modes != null && index >= 0 && index < modes.Length ? modes[index] ?? string.Empty : string.Empty;
            var normalized = new string(mode.Replace("*", "x").Replace("×", "x").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return !normalized.Contains("bin3x3") && !normalized.Contains("3x3bin");
        }
    }
    private DateTimeOffset lastPatternFailureLog;

    public IAsyncEnumerable<SolvedPolarFrame> StreamAndSolveAsync(double exposureSeconds, CancellationToken token) => StreamCoreAsync(exposureSeconds, false, token);
    public async Task RunPreviewAsync(double exposureSeconds, CancellationToken token) {
        await foreach (var _ in StreamCoreAsync(exposureSeconds, true, token)) { }
    }
    private async IAsyncEnumerable<SolvedPolarFrame> StreamCoreAsync(double exposureSeconds, bool previewOnly,
        [EnumeratorCancellation] CancellationToken token, bool gateAlreadyHeld = false, bool rotationPreview = false, Action? previewReady = null) {
        if (!double.IsFinite(exposureSeconds) || exposureSeconds <= 0 || exposureSeconds > 60)
            throw new ArgumentOutOfRangeException(nameof(exposureSeconds));
        bool reserved = false;
        using var solveStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<(SolvedPolarFrame Frame, AstrometricTracker Tracker)>? pendingSolve = null;
        var info = camera.GetInfo();
        string id = info?.DeviceId ?? string.Empty;
        short binX = info?.BinX ?? 1, binY = info?.BinY ?? 1, readout = info?.ReadoutMode ?? 0;
        int gain = PoleMasterMode?CameraGain:info?.Gain ?? -1, offset = info?.Offset ?? -1;
        LiveControls? liveControls=null;
        bool wasSubSampleEnabled = info?.IsSubSampleEnabled == true;
        var rectangle = info?.IsSubSampleEnabled == true
            ? new ObservableRectangle(info.SubSampleX, info.SubSampleY, info.SubSampleWidth, info.SubSampleHeight)
            : new ObservableRectangle(0, 0, info?.XSize ?? 0, info?.YSize ?? 0);
        if (!gateAlreadyHeld && !await captureGate.WaitAsync(0, token)) throw new InvalidOperationException("Another alignment capture is running.");
        try {
            if (!SupportsStreaming) throw new InvalidOperationException("Streaming requires a supported native QHY, ZWO ASI or ToupTek driver and readout mode. Use single frames when unavailable.");
            if (info!.IsExposing || info.LiveViewEnabled || !camera.IsFreeToCapture(this))
                throw new InvalidOperationException("The camera is busy. Stop other capture or live view first.");
            if (exposureSeconds < info.ExposureMin || info.ExposureMax > 0 && exposureSeconds > info.ExposureMax)
                throw new InvalidOperationException("Streaming exposure is outside the camera range.");
            if (previewOnly) ValidatePoleMasterHardware(); else ValidateAlignmentCamera();
            var settings = previewOnly ? null : profiles.ActiveProfile.PlateSolveSettings;
            var (focal, pixels) = previewOnly && !rotationPreview ? (1d, 1d) : AlignmentOptics;
            if (!double.IsFinite(focal) || focal <= 0 || !double.IsFinite(pixels) || pixels <= 0)
                throw new InvalidOperationException("Set focal length and camera pixel size before streaming.");
            short bin = 1;
            if (!PoleMasterMode && camera.GetDevice() is ICamera native && native.BinningModes.Any(b => b.X == StreamingBinning && b.Y == StreamingBinning))
                bin = (short)StreamingBinning;
            var sequence = new CaptureSequence(exposureSeconds, CaptureSequence.ImageTypes.SNAPSHOT, null!, new BinningMode(bin, bin), 1) {
                Gain = PoleMasterMode ? gain : settings?.Gain ?? gain, Offset = offset, EnableSubSample = false
            };
            var solver = previewOnly || UsePolarisPattern ? null : new ImageSolver(PlateSolverFactory.GetPlateSolver(settings!), PlateSolverFactory.GetBlindSolver(settings!));
            var hintContext = new SolveHintContext(profiles.ActiveProfile.Id,
                info.DeviceId ?? profiles.ActiveProfile.CameraSettings.Id ?? string.Empty, focal, pixels);
            var parameter = previewOnly ? null : new PlateSolveParameter {
                Binning = bin, FocalLength = focal, PixelSize = pixels,
                DownSampleFactor = PoleMasterMode ? 1 : settings!.DownSampleFactor, MaxObjects = settings!.MaxObjects,
                Regions = settings!.Regions, SearchRadius = PoleMasterMode ? Math.Max(15, settings!.SearchRadius) : settings!.SearchRadius,
                BlindFailoverEnabled = settings!.BlindFailoverEnabled, DisableNotifications = true,
                Coordinates = InitialAlignmentHint()!
            };
            camera.RegisterCaptureBlock(this); reserved = true;
            if(camera.GetDevice() is ICamera liveDevice){lock(controlsLock)activeLiveControls=liveControls=new(liveDevice,exposureSeconds);}
            Logger.Info($"Polar native streaming: driver={camera.GetDevice()?.GetType().Name}, camera={id}, exposure={exposureSeconds:G8}s, bin={bin}");
            await using var buffer = new LatestFrameBuffer<StreamFrame>(Receive,
                TimeSpan.FromSeconds(Math.Max(15, exposureSeconds * 3 + 5)), token);
            AstrometricTracker? tracker = null;
            var boxTracker=new StarBoxTracker();
            SolvedPolarFrame? anchor = null;
            var lastSolveAttempt = DateTimeOffset.MinValue;
            if (!previewOnly && streamingAnchor is { } saved && saved.Context == hintContext && saved.Frame.RawImage != null
                && DateTimeOffset.UtcNow - saved.Frame.CapturedAtUtc < TimeSpan.FromMinutes(2)) {
                try {
                    tracker = await Task.Run(() => CreateTracker(saved.Frame, solveStop.Token), solveStop.Token);
                    anchor = saved.Frame;
                } catch (InvalidOperationException) { }
                  catch (ArgumentException) { }
            }
            while (true) {
                var frame = await buffer.ReadAsync(token);
                token.ThrowIfCancellationRequested();
                if (profiles.ActiveProfile.Id != hintContext.ProfileId
                    || (!previewOnly && AlignmentOptics != (focal, pixels)))
                    throw new InvalidOperationException("Profile or optics changed during streaming. Reset calibration.");
                var preparedPreview = await Task.Run(() => QuickLookPreview.Prepare(frame.Image, token, PreviewStretchStrength,PreviewStretchEnabled), token);
                lastPreparedPreview = preparedPreview;
                var preview = preparedPreview.Image;
                if(!previewOnly && UsePolarisPattern && manualPolarisAdjustment) {
                    // Matching is optional evidence for a distance readout only.
                    // Never yield a new pose, move the fixed goal or hold live pixels.
                    ManualPolarisOffset? offsetMeasurement=null;
                    var properties=frame.Image.Properties;
                    if(fixedPolarisSeed is {} seed && calibratedPatternScale is {} lensScale) {
                        var goal=new PixelPoint(seed.X*properties.Width,seed.Y*properties.Height);
                        if(SkyRegionSelection.ConfirmStarNear(frame.Image.Data.FlatArray,properties.Width,properties.Height,goal.X,goal.Y,24) is {} localStar) {
                            try {
                                await SolveAlignmentImageAsync(frame.Image,parameter!,null,token,followTemplate:true);
                                var identified=patternMatchedStars!.Single(s=>s.Id==11767).Position;
                                if(Math.Abs(identified.X-localStar.X)<3 && Math.Abs(identified.Y-localStar.Y)<3)
                                    offsetMeasurement=new(new(identified.X/properties.Width,identified.Y/properties.Height),
                                        PoleMasterTarget.AngularDistance(identified,goal,properties.Width,properties.Height,lensScale));
                            } catch(InvalidOperationException) { } catch(ArgumentException) { }
                        }
                    }
                    StreamingPreviewAvailable?.Invoke(new(preview,frame.CapturedAt,string.Empty,IsManualPolarisAdjustment:true,ManualOffset:offsetMeasurement));
                    continue;
                }
                var detected = await Task.Run(()=>FindAlignmentStars(frame.Image,preparedPreview,token),token);
                var detectedOverlay = new StarOverlay(detected.Select((p,i)=>new TrackedStar(i+1,p)).ToArray(),frame.Image.Properties.Width,frame.Image.Properties.Height,false,frame.CapturedAt);
                if (previewOnly) {
                    var boxes=await Task.Run(()=>boxTracker.Update(detected,frame.Image.Properties.Width,frame.Image.Properties.Height),token);
                    var stars=new StarOverlay(boxes.Markers,frame.Image.Properties.Width,frame.Image.Properties.Height,boxes.Verified,frame.CapturedAt);
                    previewReady?.Invoke();
                    StreamingPreviewAvailable?.Invoke(new(preview, frame.CapturedAt, string.Empty, IsAimingPreview:!rotationPreview,IsRotationPreview:rotationPreview,Stars:stars));
                    continue;
                }
                if(UsePolarisPattern) {
                    // Fixed-template matching also works with only Polaris and
                    // one visible neighbor; the generic eight-star tracker is
                    // not a prerequisite for this mode.
                    SolvedPolarFrame? matched=null;string patternFailure=string.Empty;
                    try {
                        var solution=await SolveAlignmentImageAsync(frame.Image,parameter!,null,token,followTemplate:true);
                        matched=new(solution.Coordinates.RADegrees,solution.Coordinates.Dec,solution.PositionAngle,
                            solution.Pixscale,frame.Image.Properties.Width,frame.Image.Properties.Height,solution.Flipped,
                            frame.CapturedAt,preview,frame.Image,Stars:new(patternMatchedStars!,frame.Image.Properties.Width,frame.Image.Properties.Height,true,frame.CapturedAt,patternTemplateStars));
                        PoleMasterSetup.ValidateSolution(matched,profiles.ActiveProfile.AstrometrySettings.Latitude);
                        streamingAnchor=(hintContext,matched);
                    } catch(System.InvalidOperationException error){patternFailure=error.Message;}
                      catch(System.ArgumentException error){patternFailure=error.Message;}
                    if(matched!=null)yield return matched;
                    else {
                        if(DateTimeOffset.UtcNow-lastPatternFailureLog>TimeSpan.FromSeconds(5)) {
                            lastPatternFailureLog=DateTimeOffset.UtcNow;
                            Logger.Warning("Polar live pattern not matched: "+patternFailure+"; detected="+detected.Length);
                        }
                        StreamingPreviewAvailable?.Invoke(new(preview,frame.CapturedAt,patternFailure,Stars:detectedOverlay));
                    }
                    continue;
                }
                if (pendingSolve?.IsCompleted == true) {
                    try {
                        var verified = await pendingSolve;
                        anchor = verified.Frame; tracker = verified.Tracker;
                        streamingAnchor = (hintContext, anchor);
                        Logger.Info("Polar streaming: absolute solve refreshed tracking anchor");
                    } catch (OperationCanceledException) { throw; }
                      catch (Exception error) { Logger.Warning("Polar streaming verification: " + error.Message); }
                    pendingSolve = null;
                }
                SolvedPolarFrame? tracked = null;
                string failure = "Tracking reference unavailable";
                if (tracker != null && anchor != null && frame.CapturedAt - anchor.CapturedAtUtc < TimeSpan.FromSeconds(30)) {
                    try {
                        var pose = await Task.Run(() => tracker.Track(detected, frame.Image.Properties.Width, frame.Image.Properties.Height), token);
                        var c = pose.Orientation.Center; var up = pose.Orientation.Up;
                        double ra = (Math.Atan2(c.Y,c.X)*180/Math.PI+360)%360, dec = Math.Asin(Math.Clamp(c.Z,-1,1))*180/Math.PI;
                        var northEast = PolarAlignmentGeometry.FromEquatorial(ra,dec,0);
                        double angle = Math.Atan2(up.Dot(northEast.Right),up.Dot(northEast.Up))*180/Math.PI;
                        double pixelScale = anchor.PixelScaleArcseconds * anchor.Width / frame.Image.Properties.Width;
                        tracked = new(ra,dec,angle,pixelScale,frame.Image.Properties.Width,frame.Image.Properties.Height,
                            anchor.Flipped,frame.CapturedAt,preview,frame.Image, IsTracked:true,TrackedStars:pose.Stars,TrackingRmsPixels:pose.RmsPixels, Stars:new(pose.Markers!,frame.Image.Properties.Width,frame.Image.Properties.Height,true,frame.CapturedAt));
                    } catch (InvalidOperationException error) { failure = error.Message; }
                      catch (ArgumentException error) { failure = error.Message; }
                }
                // Solving never blocks camera acquisition, preview, or valid tracked guidance.
                bool refresh = tracked == null || anchor == null || frame.CapturedAt-anchor.CapturedAtUtc > TimeSpan.FromSeconds(10);
                if (refresh && pendingSolve == null && DateTimeOffset.UtcNow-lastSolveAttempt > TimeSpan.FromSeconds(3)) {
                    lastSolveAttempt = DateTimeOffset.UtcNow;
                    var input = frame; var display = preview;
                    parameter!.Coordinates = solveHints.Get(hintContext)
                        ?? InitialAlignmentHint()!;
                    pendingSolve = Task.Run(async () => {
                        var solution = await SolveAlignmentImageAsync(input.Image,parameter!,solver!,solveStop.Token);
                        solveStop.Token.ThrowIfCancellationRequested();
                        if (!solution.Success || solution.Coordinates == null || !double.IsFinite(solution.Pixscale) || solution.Pixscale <= 0)
                            throw new InvalidOperationException("Plate solve failed; check stars, focus and streaming exposure.");
                        solveHints.Store(hintContext,solution.Coordinates);
                        var solved = new SolvedPolarFrame(solution.Coordinates.RADegrees,solution.Coordinates.Dec,solution.PositionAngle,
                            solution.Pixscale,input.Image.Properties.Width,input.Image.Properties.Height,solution.Flipped,input.CapturedAt,display,input.Image);
                        if (PoleMasterMode) PoleMasterSetup.ValidateSolution(solved, profiles.ActiveProfile.AstrometrySettings.Latitude);
                        return (solved,CreateTracker(solved,solveStop.Token));
                    },solveStop.Token);
                }
                if (tracked != null) yield return tracked;
                else StreamingPreviewAvailable?.Invoke(new(preview,frame.CapturedAt,failure,Stars:detectedOverlay));
            }

            async IAsyncEnumerable<StreamFrame> Receive([EnumeratorCancellation] CancellationToken ct) {
                // This task alone advances and disposes the NINA enumerator, including StopLiveView.
                await foreach (var exposure in camera.LiveView(sequence, ct).WithCancellation(ct).ConfigureAwait(false)) {
                    ct.ThrowIfCancellationRequested();
                    if (exposure == null) throw new InvalidOperationException("Camera returned an empty streaming frame.");
                    var currentCamera = camera.GetInfo();
                    if (currentCamera?.Connected != true || currentCamera.DeviceId != id)
                        throw new InvalidOperationException("Camera disconnected or changed during streaming.");
                    double frameExposure=exposureSeconds;
                    ApplyLiveControls(liveControls,ref gain,ref exposureSeconds);
                    var receivedAt = DateTimeOffset.UtcNow;
                    var raw = await exposure.ToImageData(SilentProgress.Instance, ct).ConfigureAwait(false);
                    if (raw?.Data?.FlatArray == null || raw.Properties.Width <= 0 || raw.Properties.Height <= 0
                        || (long)raw.Properties.Width * raw.Properties.Height != raw.Data.FlatArray.Length)
                        throw new InvalidOperationException("Invalid RAW streaming frame.");
                    // Native video drivers timestamp download, not the sensor shutter. Use a
                    // receive-time midpoint estimate; this option cannot certify hardware timing.
                    yield return new(raw, receivedAt.AddSeconds(-frameExposure / 2));
                }
            }
        } finally {
            try {
                solveStop.Cancel();
                if (pendingSolve != null) {
                    try { await pendingSolve.ConfigureAwait(false); }
                    catch (Exception error) { Logger.Debug("Polar background solve stopped: " + error.Message); }
                }
                if (reserved) {
                    // Some native ToupTek versions restore still mode asynchronously after StopLiveView.
                    if (id.StartsWith("ToupTek_", StringComparison.OrdinalIgnoreCase) && camera.GetDevice() is ICamera device && device.Id == id) {
                        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(15, exposureSeconds * 3 + 5));
                        while (device.Connected && device.LiveViewEnabled) {
                            if (DateTime.UtcNow > deadline) throw new TimeoutException("Video mode did not stop. Reconnect the camera before capturing again.");
                            await Task.Delay(25);
                        }
                    }
                    var current = camera.GetInfo();
                    if (current?.Connected == true && current.DeviceId == id) {
                        ApplyLiveControls(liveControls,ref gain,ref exposureSeconds,liveFrame:false);
                        camera.SetReadoutMode(readout); camera.SetBinning(binX, binY);
                        if (camera.GetDevice() is ICamera restored) {
                            if (restored.CanSetGain) restored.Gain = gain;
                            if (restored.CanSetOffset) restored.Offset = offset;
                        }
                        if (current.CanSubSample) {
                            camera.SetSubSambleRectangle(rectangle);
                            if (camera.GetDevice() is ICamera restoredRoi) restoredRoi.EnableSubSample = wasSubSampleEnabled;
                        }
                    }
                }
            } catch (Exception error) {
                throw new StreamingCleanupException(error);
            } finally {
                lock(controlsLock){if(ReferenceEquals(activeLiveControls,liveControls))activeLiveControls=null;}
                try { if (reserved) camera.ReleaseCaptureBlock(this); }
                finally { if(!gateAlreadyHeld)captureGate.Release(); }
            }
        }
    }
    private sealed class SilentProgress : IProgress<ApplicationStatus> {
        public static SilentProgress Instance { get; } = new();
        public void Report(ApplicationStatus value) { }
    }
    private sealed record StreamFrame(IImageData Image, DateTimeOffset CapturedAt);
    private AstrometricTracker CreateTracker(SolvedPolarFrame frame, CancellationToken token) {
        if (frame.RawImage == null) throw new InvalidOperationException("Tracking anchor has no raw pixels.");
        var prepared = QuickLookPreview.Prepare(frame.RawImage, token);
        return new(PolarAlignmentGeometry.FromEquatorial(frame.RightAscensionDegrees,frame.DeclinationDegrees,frame.PositionAngleDegrees,frame.Flipped),
            FindAlignmentStars(frame.RawImage,prepared,token),frame.Width,frame.Height,frame.PixelScaleArcseconds,frame.Flipped);
    }
}
