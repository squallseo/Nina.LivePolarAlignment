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
    private IProfileService profiles = null!;
    private IPolarAlignmentCaptureService captureService = null!;
    private PolarAlignmentSession? session;
    private CancellationTokenSource? liveCancellation;
    private bool liveBusy;
    private double calibrationLatitude, calibrationLongitude;
    private string liveStatus = "카메라 연결과 관측지 · plate solver 설정을 확인하세요.";
    private string liveError = "—";
    private BitmapSource? livePreview;
    private PolarGuidance? guidance;
    private PolarError? latestError;
    private DateTimeOffset? measuredAt;
    private double lastCycleMilliseconds;
    private bool measurementStale = true;
    private readonly DispatcherTimer freshnessTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    public double ExposureSeconds { get; set; } = 1;
    public ICommand CaptureReferenceCommand { get; private set; } = null!;
    public ICommand StartLiveCommand { get; private set; } = null!;
    public ICommand StopLiveCommand { get; private set; } = null!;
    public ICommand ResetLiveCommand { get; private set; } = null!;
    public string LiveStatus => liveStatus;
    public string LiveError => liveError;
    public string LiveTotalError => latestError is { } error ? $"{error.TotalArcminutes:0.00}′" : "—";
    public string LiveAltitudeError => latestError is { } error
        ? $"Altitude {error.AltitudeArcminutes:+0.00;-0.00;0.00}′ · {(Math.Abs(error.AltitudeArcminutes) < .05 ? "on target" : error.AltitudeArcminutes > 0 ? "lower the polar axis" : "raise the polar axis")}" : "Altitude —";
    public string LiveAzimuthError => latestError is { } error ? $"Azimuth {error.AzimuthArcminutes:+0.00;-0.00;0.00}′" : "Azimuth —";
    public bool IsMeasurementStale => measurementStale || measuredAt is null || (DateTimeOffset.UtcNow - measuredAt.Value).TotalSeconds > Math.Max(15, ExposureSeconds * 3 + 5);
    public string FrameFreshness => measuredAt is { } time
        ? latestError == null ? $"기준 영상 · {Math.Max(0, (DateTimeOffset.UtcNow - time).TotalSeconds):0}초 전 촬영"
        : $"{(IsMeasurementStale ? "이전 측정 · " : "")}프레임 {Math.Max(0, (DateTimeOffset.UtcNow - time).TotalSeconds):0}초 전 · 촬영 + 해석 {lastCycleMilliseconds / 1000:0.0}초"
        : "첫 측정을 기다리고 있습니다";
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
    public bool CanConfigureLive => !liveBusy;

    private void InitializeLive(IProfileService profileService, IPolarAlignmentCaptureService service) {
        profiles = profileService; captureService = service;
        CaptureReferenceCommand = new AsyncRelayCommand(CaptureReferenceAsync, () => !liveBusy && session?.IsCalibrated != true);
        StartLiveCommand = new AsyncRelayCommand(RunLiveAsync, () => !liveBusy && session?.IsCalibrated == true);
        StopLiveCommand = new RelayCommand(RequestStopLive, () => liveBusy);
        ResetLiveCommand = new RelayCommand(ResetLive, () => !liveBusy);
        PropertyChanged += (_, change) => {
            if ((change.PropertyName == nameof(IsClosed) && IsClosed)
                || (change.PropertyName == nameof(IsVisible) && !IsVisible)) RequestStopLive();
        };
        freshnessTimer.Tick += (_, _) => {
            RaisePropertyChanged(nameof(FrameFreshness)); RaisePropertyChanged(nameof(IsMeasurementStale));
            RaisePropertyChanged(nameof(HasVisibleGuidance)); RaisePropertyChanged(nameof(GuidanceText));
            NotifyWorkflow();
        };
        freshnessTimer.Start();
    }
    private void ResetLive() {
        session = null; livePreview = null; guidance = null; liveError = "—";
        calibrationSamples = 0; pixelScale = 0; operationError = null;
        latestError = null; measuredAt = null; measurementStale = true;
        liveStatus = "기준을 초기화했습니다. 첫 촬영부터 다시 시작하세요.";
        NotifyLive();
    }
    private void RequestStopLive() {
        if (liveCancellation == null) return;
        measurementStale = true; guidance = null;
        liveStatus = "촬영을 중지하고 있습니다…";
        liveCancellation.Cancel(); NotifyLive();
    }
    private async Task CaptureReferenceAsync() {
        await WithLiveOperation(async token => {
            if (session == null) {
                var site = profiles.ActiveProfile.AstrometrySettings;
                calibrationLatitude = site.Latitude; calibrationLongitude = site.Longitude;
                session = new PolarAlignmentSession(calibrationLatitude, calibrationLongitude);
            }
            ValidateSite();
            liveStatus = "기준 영상을 촬영하고 별 패턴을 해석하고 있습니다…"; NotifyLive();
            var frame = await captureService.CaptureAndSolveAsync(ExposureSeconds, token);
            token.ThrowIfCancellationRequested();
            ValidateSite();
            livePreview = frame.Preview;
            pixelScale = frame.PixelScaleArcseconds;
            int count = session.AddCalibration(frame);
            calibrationSamples = count;
            guidance = session.IsCalibrated ? session.GetGuidance(frame) : session.GetReferenceGuidance(frame);
            measuredAt = frame.CapturedAtUtc;
            measurementStale = false;
            liveStatus = session.IsCalibrated
                ? "두 기준 촬영 완료 · 실시간 조정 시작 후 고도·방위각 볼트를 조정하세요."
                : "첫 기준 해석 성공 · RA만 회전한 뒤 다음 기준을 촬영하세요.";
        });
    }
    private async Task RunLiveAsync() {
        await WithLiveOperation(async token => {
            while (!token.IsCancellationRequested) {
                ValidateSite();
                liveStatus = "다음 영상을 촬영 · 해석 중입니다…"; NotifyLive();
                var cycle = Stopwatch.StartNew();
                var frame = await captureService.CaptureAndSolveAsync(ExposureSeconds, token);
                token.ThrowIfCancellationRequested();
                ValidateSite();
                guidance = session!.GetGuidance(frame);
                var error = guidance.Error;
                cycle.Stop(); lastCycleMilliseconds = cycle.Elapsed.TotalMilliseconds;
                latestError = error; measuredAt = frame.CapturedAtUtc; measurementStale = false;
                livePreview = frame.Preview;
                pixelScale = frame.PixelScaleArcseconds;
                liveError = $"{error.TotalArcminutes:0.00}′ total · altitude {error.AltitudeArcminutes:+0.00;-0.00;0.00}′ · azimuth {error.AzimuthArcminutes:+0.00;-0.00;0.00}′";
                liveStatus = $"{frame.CapturedAtUtc.ToLocalTime():HH:mm:ss} 측정 완료 · 볼트를 조금씩 움직이고 다음 결과를 확인하세요.";
                NotifyLive();
                await Task.Delay(250, token);
            }
        });
    }
    private async Task WithLiveOperation(Func<CancellationToken, Task> operation) {
        if (liveBusy) return;
        liveBusy = true; operationError = null; liveCancellation = new CancellationTokenSource(); NotifyLive();
        try { await operation(liveCancellation.Token); }
        catch (OperationCanceledException) { liveStatus = "측정을 멈췄습니다. 다시 시작하거나 기준을 초기화할 수 있습니다."; measurementStale = true; guidance = null; }
        catch (Exception ex) { operationError = ex.Message; liveStatus = "측정을 완료하지 못했습니다. 아래 내용을 확인한 뒤 다시 시도하세요."; liveError = "—"; guidance = null; measurementStale = true; }
        finally { liveCancellation.Dispose(); liveCancellation = null; liveBusy = false; NotifyLive(); }
    }
    private void ValidateSite() {
        var site = profiles.ActiveProfile.AstrometrySettings;
        if (site.Latitude != calibrationLatitude || site.Longitude != calibrationLongitude) {
            session = null;
            calibrationSamples = 0;
            throw new InvalidOperationException("Observing site changed. Reset and repeat calibration.");
        }
        if (!double.IsFinite(ExposureSeconds) || ExposureSeconds <= 0 || ExposureSeconds > 60)
            throw new InvalidOperationException("Exposure must be greater than 0 and no more than 60 seconds.");
    }
    private void NotifyLive() {
        NotifyWorkflow();
        foreach (var name in new[] { nameof(LiveStatus), nameof(LiveError), nameof(LivePreview), nameof(CanConfigureLive), nameof(ImageWidth), nameof(ImageHeight), nameof(MarkerSize), nameof(AxisLeft), nameof(AxisTop), nameof(PoleLeft), nameof(PoleTop), nameof(HasVisibleGuidance), nameof(GuidanceText) }) RaisePropertyChanged(name);
        foreach (var name in new[] { nameof(LiveTotalError), nameof(LiveAltitudeError), nameof(LiveAzimuthError), nameof(FrameFreshness), nameof(IsMeasurementStale) }) RaisePropertyChanged(name);
        (CaptureReferenceCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (StartLiveCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (StopLiveCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (ResetLiveCommand as IRelayCommand)?.NotifyCanExecuteChanged();
    }
    public void Dispose() { RequestStopLive(); freshnessTimer.Stop(); }
}
