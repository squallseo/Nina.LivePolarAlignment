using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Nina.LivePolarAlignment.Capture;
using Nina.LivePolarAlignment.Core;
using NINA.Profile.Interfaces;

namespace Nina.LivePolarAlignment.UI;

public sealed partial class PolarAlignmentDockableVM {
    private readonly NINA.WPF.Base.Interfaces.Mediator.IApplicationStatusMediator? applicationStatusMediator;
    private string lastHostStatus = string.Empty;
    private IProfileService profiles = null!;
    private IPolarAlignmentCaptureService captureService = null!;
    private PolarAlignmentSession? session;
    private CancellationTokenSource? liveCancellation;
    private bool liveBusy;
    private double calibrationLatitude, calibrationLongitude;
    private string liveStatus = "Live0";
    private string liveError = "—";
    private BitmapSource? livePreview;
    private PolarGuidance? guidance;
    private PolarError? latestError;
    private DateTimeOffset? measuredAt;
    private double lastCycleMilliseconds;
    private bool measurementStale = true;
    // MEF can create this VM on a loader thread whose Dispatcher is never pumped.
    private readonly Dispatcher uiDispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer freshnessTimer;
    private bool useStreaming = true;
    public bool UseStreaming {
        get => useStreaming;
        set { if (liveBusy || useStreaming == value) return; useStreaming = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(AcquisitionMode)); }
    }
    public bool CanUseStreaming => !liveBusy;
    public string AcquisitionMode => UiText.Get(UseStreaming ? "StreamingMode" : "SingleMode");
    public string StreamingAvailability => UiText.Get(captureService.SupportsStreaming ? "StreamingHint" : "StreamingUnsupported");
    public double ExposureSeconds { get; set; } = 1;
    public ICommand CaptureReferenceCommand { get; private set; } = null!;
    public ICommand StartLiveCommand { get; private set; } = null!;
    public ICommand StopLiveCommand { get; private set; } = null!;
    public ICommand ResetLiveCommand { get; private set; } = null!;
    public string LiveStatus => driftInProgress ? IndependentDriftStatus : IsRotating ? RotationStatus : liveStatus == "MeasurementComplete" ? UiText.Format(liveStatus, measuredAt?.ToLocalTime() ?? DateTimeOffset.Now) : UiText.Get(liveStatus);
    public string LiveError => liveError;
    public string LiveTotalError => latestError is { } error ? $"{error.TotalArcminutes:0.00}′" : "—";
    public string LiveAltitudeError => latestError is { } error
        ? $"Altitude {error.AltitudeArcminutes:+0.00;-0.00;0.00}′ · {(Math.Abs(error.AltitudeArcminutes) < .05 ? "on target" : error.AltitudeArcminutes > 0 ? "lower the polar axis" : "raise the polar axis")}" : "Altitude —";
    public string LiveAzimuthError => latestError is { } error ? $"Azimuth {error.AzimuthArcminutes:+0.00;-0.00;0.00}′" : "Azimuth —";
    public bool IsMeasurementStale => measurementStale || measuredAt is null || (DateTimeOffset.UtcNow - measuredAt.Value).TotalSeconds > Math.Max(15, ExposureSeconds * 3 + 5);
    public string FrameFreshness => measuredAt is { } time
        ? latestError == null ? UiText.Format("ReferenceAge", Math.Max(0, (DateTimeOffset.UtcNow - time).TotalSeconds))
        : UiText.Format("FrameAge", IsMeasurementStale ? UiText.Get("StalePrefix") : "", Math.Max(0, (DateTimeOffset.UtcNow - time).TotalSeconds), lastCycleMilliseconds / 1000)
        : UiText.Get("WaitingFrame");
    public BitmapSource? LivePreview => livePreview;
    public double ImageWidth => guidance?.Width ?? livePreview?.PixelWidth ?? 800;
    public double ImageHeight => guidance?.Height ?? livePreview?.PixelHeight ?? 600;
    public double MarkerSize => ImageWidth / 35;
    public double AxisLeft => (guidance?.MountAxis?.X ?? 0) - MarkerSize / 2;
    public double AxisTop => (guidance?.MountAxis?.Y ?? 0) - MarkerSize / 2;
    public double PoleLeft => (guidance?.CelestialPole?.X ?? 0) - MarkerSize / 2;
    public double PoleTop => (guidance?.CelestialPole?.Y ?? 0) - MarkerSize / 2;
    public bool HasVisibleGuidance => !IsMeasurementStale && guidance?.MountAxis is { } axis && guidance.CelestialPole is { } pole
        && axis.X >= 0 && axis.Y >= 0 && axis.X < ImageWidth && axis.Y < ImageHeight
        && pole.X >= 0 && pole.Y >= 0 && pole.X < ImageWidth && pole.Y < ImageHeight;
    public string GuidanceText => measuredAt != null && IsMeasurementStale ? "Guidance paused. Resume live measurement before adjusting the mount." : guidance == null ? "Calibrate to display axis guidance." : HasVisibleGuidance
        ? "Orange: mount axis · green: celestial pole. Adjust bolts until the rings overlap."
        : "Axis or pole is outside this frame. Use the measured offsets; a wider field near the pole helps.";
    public bool CanConfigureLive => !liveBusy && !fieldSaving;

    private void InitializeLive(IProfileService profileService, IPolarAlignmentCaptureService service) {
        profiles = profileService; captureService = service;
        captureService.PreviewAvailable += CapturePreviewAvailable;
        UiText.Configure(() => profileService.ActiveProfile.ApplicationSettings?.Culture);
        UiText.Instance.PropertyChanged += LocaleChanged;
        CaptureReferenceCommand = new AsyncRelayCommand(CaptureReferenceAsync, () => EquipmentReady && !liveBusy && session?.IsCalibrated != true);
        StartLiveCommand = new AsyncRelayCommand(RunLiveAsync, () => EquipmentReady && !liveBusy && session?.IsCalibrated == true);
        StopLiveCommand = new RelayCommand(RequestStopLive, () => liveBusy && !IsStopping);
        ResetLiveCommand = new RelayCommand(ResetLive, () => !liveBusy && !fieldSaving);
        InitializeField(); InitializeVerification(); InitializeDrift();
        PropertyChanged += (_, change) => {
            if ((change.PropertyName == nameof(IsClosed) && IsClosed)
                || (change.PropertyName == nameof(IsVisible) && !IsVisible)) RequestStopLive();
        };
        freshnessTimer.Tick += (_, _) => {
            UiText.Refresh(); RaisePropertyChanged(nameof(LiveStatus));
            RaisePropertyChanged(nameof(CanUseStreaming)); RaisePropertyChanged(nameof(StreamingAvailability));
            NotifyRotation();
            RaisePropertyChanged(nameof(FrameFreshness)); RaisePropertyChanged(nameof(IsMeasurementStale));
            RaisePropertyChanged(nameof(HasVisibleGuidance)); RaisePropertyChanged(nameof(GuidanceText));
            if (IsMeasurementStale) AbortStability("measurement became stale");
            NotifyWorkflow(); NotifyField();
        };
        freshnessTimer.Start();
    }
    private void CapturePreviewAvailable(BitmapSource preview) {
        if (!uiDispatcher.CheckAccess()) {
            uiDispatcher.BeginInvoke(() => CapturePreviewAvailable(preview));
            return;
        }
        if (fieldDisposed || !liveBusy || IsStopping) return;
        // A solved image and its overlay are one display unit. Keep that pair while
        // the next frame is being solved; old geometry must not be drawn on new pixels.
        if (!IndependentDriftEnabled && guidance != null && livePreview != null) {
            liveStatus = "SolvingFrame"; NotifyLive();
            return;
        }
        livePreview = preview; guidance = null;
        liveStatus = "SolvingFrame"; NotifyLive();
        NINA.Core.Utility.Logger.Info($"Polar preview: applied to UI ({preview.PixelWidth}x{preview.PixelHeight}), rotating={IsRotating}");
    }
    private void ResetLive() {
        ResetDriftEvidence();
        verificationResult = null;
        automaticRotationCompleted = false; rotationProgress = 0;
        EndFieldSession("reset"); fieldFailure = null; stabilityResult = null;
        session = null; livePreview = null; guidance = null; liveError = "—";
        calibrationSamples = 0; pixelScale = 0; operationError = null;
        latestError = null; measuredAt = null; measurementStale = true;
        liveStatus = "Live1";
        NotifyLive();
    }
    private void RequestStopLive() {
        if (liveCancellation == null) return;
        AbortStability("stop requested"); RecordFieldEvent("stop_requested", new { });
        measurementStale = true; guidance = null;
        liveStatus = "Live2";
        liveCancellation.Cancel(); NotifyLive();
    }
    private async Task CaptureReferenceAsync() {
        await WithLiveOperation(async token => {
            if (session == null && calibrationSamples == 0) { verificationResult = null; stabilityResult = null; }
            if (AutoRaRotation) {
                liveStatus = "AutoRaPreparing"; NotifyLive();
                await captureService.PrepareAutoRaAsync(token);
                token.ThrowIfCancellationRequested();
            }
            if (calibrationSamples == 1 && AutoRaRotation) await RotateForReferenceAsync(token);
            await CaptureReferenceFrameAsync(token, calibrationSamples == 0 && AutoRaRotation);
            if (calibrationSamples == 1 && AutoRaRotation) {
                await RotateForReferenceAsync(token);
                await CaptureReferenceFrameAsync(token);
            }
        });
    }
    private async Task<SolvedPolarFrame> CaptureReferenceFrameAsync(CancellationToken token, bool rotateDuringSolve = false) {
        EnsureFieldSession();
        RecordFieldEvent("reference_start", new { exposureSeconds = ExposureSeconds });
        if (calibrationSamples == 0)
            NINA.Core.Utility.Logger.Info($"Polar workflow: first reference requested; angle={RotationAngleDegrees}");
        if (session == null) {
            var site = profiles.ActiveProfile.AstrometrySettings;
            calibrationLatitude = site.Latitude; calibrationLongitude = site.Longitude;
            session = new PolarAlignmentSession(calibrationLatitude, calibrationLongitude);
        }
        ValidateSite();
        liveStatus = calibrationSamples == 0 ? "ReferenceFirstCapture" : "ReferenceSecondCapture"; NotifyLive();
        SolvedPolarFrame frame;
        if (rotateDuringSolve) {
            SolvedPolarFrame? captured = null;
            await RotateForReferenceAsync(token, async (progress, ct) => {
                captured = await captureService.CaptureAndSolveWithRotationAsync(ExposureSeconds, ReverseRaRotation, progress, ct, RotationAngleDegrees, solved =>
                    uiDispatcher.InvokeAsync(() => {
                        ct.ThrowIfCancellationRequested();
                        ValidateSite();
                        livePreview = solved.Preview; pixelScale = solved.PixelScaleArcseconds;
                        guidance = session!.GetReferenceGuidance(solved);
                        measuredAt = solved.CapturedAtUtc; measurementStale = false;
                        liveStatus = "Live5"; NotifyLive();
                        NINA.Core.Utility.Logger.Info("Polar preview: first solved reference overlay applied before rotation join");
                    }, DispatcherPriority.Normal, ct).Task);
            });
            frame = captured ?? throw new InvalidOperationException("No reference image was returned.");
        } else frame = await captureService.CaptureAndSolveAsync(ExposureSeconds, token);
        token.ThrowIfCancellationRequested();
        ValidateSite();
        livePreview = frame.Preview;
        pixelScale = frame.PixelScaleArcseconds;
        int count;
        try { count = session.AddCalibration(frame); }
        catch (Exception error) { RecordFieldFrame(frame, "reference", false, failure: error); throw; }
        calibrationSamples = count;
        guidance = session.IsCalibrated ? session.GetGuidance(frame) : session.GetReferenceGuidance(frame);
        measuredAt = frame.CapturedAtUtc;
        measurementStale = false;
        RecordFieldFrame(frame, "reference", true);
        liveStatus = session.IsCalibrated ? "Live4" : "Live5";
        NotifyLive(); // Show the solved overlay before optional evidence files finish saving.
        await SaveReferenceEvidenceAsync(frame);
        return frame;

    }
    private async Task RunLiveAsync() {
        await WithLiveOperation(async token => {
            RecordFieldEvent("live_start", new { acquisition = UseStreaming ? "native_streaming" : "single_frames", exposureSeconds = ExposureSeconds });
            var cycle = Stopwatch.StartNew();
            if (UseStreaming) {
                ValidateSite();
                if (captureService.SupportsStreaming) {
                    liveStatus = "StreamingStarting"; NotifyLive();
                    // Dispose the native stream before starting ordinary exposures.
                    await using var frames = captureService.StreamAndSolveAsync(ExposureSeconds, token).GetAsyncEnumerator(token);
                    while (true) {
                        bool available;
                        try { available = await frames.MoveNextAsync(); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error) when (error is not StreamingCleanupException) {
                            token.ThrowIfCancellationRequested();
                            RecordFieldEvent("streaming_fallback", new { reason = error.ToString() }); break;
                        }
                        token.ThrowIfCancellationRequested();
                        if (!available) break;
                        ValidateSite(); PublishMeasurement(frames.Current, cycle.Elapsed.TotalMilliseconds);
                        cycle.Restart();
                    }
                }
                token.ThrowIfCancellationRequested();
                useStreaming = false; RaisePropertyChanged(nameof(UseStreaming)); NotifyLive();
            }
            while (!token.IsCancellationRequested) {
                ValidateSite(); liveStatus = "Live6"; NotifyLive(); cycle.Restart();
                var frame = await captureService.CaptureAndSolveAsync(ExposureSeconds, token);
                token.ThrowIfCancellationRequested(); ValidateSite();
                PublishMeasurement(frame, cycle.Elapsed.TotalMilliseconds);
                await Task.Delay(250, token);
            }
        });
    }
    private void PublishMeasurement(SolvedPolarFrame frame, double cycleMilliseconds) {
        try { guidance = session!.GetGuidance(frame); }
        catch (Exception geometryError) { RecordFieldFrame(frame, "measurement", false, cycleMilliseconds, geometryError); throw; }
        var error = guidance.Error;
        lastCycleMilliseconds = cycleMilliseconds;
        latestError = error; measuredAt = frame.CapturedAtUtc; measurementStale = false;
        livePreview = frame.Preview; pixelScale = frame.PixelScaleArcseconds;
        liveError = $"{error.TotalArcminutes:0.00} total";
        RecordFieldFrame(frame, "measurement", true, cycleMilliseconds);
        UpdateStability(frame);
        liveStatus = "MeasurementComplete"; NotifyLive();
    }
    private async Task WithLiveOperation(Func<CancellationToken, Task> operation) {
        if (liveBusy) return;
        liveBusy = true; operationError = null; liveCancellation = new CancellationTokenSource(); NotifyLive();
        try { await operation(liveCancellation.Token); }
        catch (OperationCanceledException) { AbortStability("cancelled"); RecordFieldEvent("operation_cancelled", new { }); liveStatus = "Live7"; measurementStale = true; guidance = null; }
        catch (Exception ex) { AbortStability("operation failed"); RecordFieldEvent("operation_failed", new { error = ex.ToString() }); operationError = ex.Message; liveStatus = "Live8"; liveError = "—"; guidance = null; measurementStale = true; }
        finally { liveCancellation.Dispose(); liveCancellation = null; liveBusy = false; NotifyLive(); if (fieldDisposed && !fieldSaving) EndFieldSession("disposed"); }
    }
    private void ValidateSite() {
        if (!captureService.CameraConnected)
            throw new InvalidOperationException(UiText.Get("CameraRequired"));
        var site = profiles.ActiveProfile.AstrometrySettings;
        if (site.Latitude != calibrationLatitude || site.Longitude != calibrationLongitude) {
            session = null;
            calibrationSamples = 0;
            throw new InvalidOperationException("Observing site changed. Reset and repeat calibration.");
        }
        if (!double.IsFinite(ExposureSeconds) || ExposureSeconds <= 0 || ExposureSeconds > 60)
            throw new InvalidOperationException("Exposure must be greater than 0 and no more than 60 seconds.");
    }
    private void ReportHostStatus() {
        var message = !fieldDisposed && (liveBusy || WaitingForManualRotation) ? LiveStatus : string.Empty;
        if (message == lastHostStatus) return;
        lastHostStatus = message;
        applicationStatusMediator?.StatusUpdate(new NINA.Core.Model.ApplicationStatus { Source = "Live Polar Alignment", Status = message });
    }
    private void NotifyLive() {
        ReportHostStatus();
        NotifyWorkflow(); NotifyField(); NotifyRotation(); NotifyVerification(); NotifyDrift();
        RaisePropertyChanged(nameof(CanUseStreaming)); RaisePropertyChanged(nameof(AcquisitionMode)); RaisePropertyChanged(nameof(StreamingAvailability));
        foreach (var name in new[] { nameof(LiveStatus), nameof(LiveError), nameof(LivePreview), nameof(CanConfigureLive), nameof(ImageWidth), nameof(ImageHeight), nameof(MarkerSize), nameof(AxisLeft), nameof(AxisTop), nameof(PoleLeft), nameof(PoleTop), nameof(HasVisibleGuidance), nameof(GuidanceText) }) RaisePropertyChanged(name);
        foreach (var name in new[] { nameof(LiveTotalError), nameof(LiveAltitudeError), nameof(LiveAzimuthError), nameof(FrameFreshness), nameof(IsMeasurementStale) }) RaisePropertyChanged(name);
        (CaptureReferenceCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (StartLiveCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (StopLiveCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (ResetLiveCommand as IRelayCommand)?.NotifyCanExecuteChanged();
    }
    private void LocaleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => NotifyLive();
    public void Dispose() { captureService.PreviewAvailable -= CapturePreviewAvailable; fieldDisposed = true; ReportHostStatus(); RequestStopLive(); freshnessTimer.Stop(); UiText.Instance.PropertyChanged -= LocaleChanged; if (!liveBusy && !fieldSaving) EndFieldSession("disposed"); }
}
