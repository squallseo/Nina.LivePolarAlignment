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
    private string liveStatus = "Connect a camera and configure a plate solver in N.I.N.A. Then capture a reference near the celestial pole.";
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
        ? $"{(IsMeasurementStale ? "STALE · " : "")}Last frame {Math.Max(0, (DateTimeOffset.UtcNow - time).TotalSeconds):0}s ago · capture + solve {lastCycleMilliseconds:0} ms"
        : "Awaiting the first live measurement";
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
        };
        freshnessTimer.Start();
    }
    private void ResetLive() {
        session = null; livePreview = null; guidance = null; liveError = "—";
        latestError = null; measuredAt = null; measurementStale = true;
        liveStatus = "Calibration reset. Capture a reference near the celestial pole.";
        NotifyLive();
    }
    private void RequestStopLive() {
        if (liveCancellation == null) return;
        measurementStale = true; guidance = null;
        liveStatus = "Stopping capture…";
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
            liveStatus = "Exposing and plate solving reference…"; NotifyLive();
            var frame = await captureService.CaptureAndSolveAsync(ExposureSeconds, token);
            token.ThrowIfCancellationRequested();
            ValidateSite();
            livePreview = frame.Preview;
            int count = session.AddCalibration(frame);
            liveStatus = session.IsCalibrated
                ? "Calibration ready. Keep RA fixed. Start live measurement, then adjust the mount's altitude and azimuth bolts."
                : $"Reference {count} captured. Rotate only RA by 60–90° (at least 30°), let the mount settle, then capture the rotated reference.";
        });
    }
    private async Task RunLiveAsync() {
        await WithLiveOperation(async token => {
            while (!token.IsCancellationRequested) {
                ValidateSite();
                liveStatus = "Live: exposing and plate solving…"; NotifyLive();
                var cycle = Stopwatch.StartNew();
                var frame = await captureService.CaptureAndSolveAsync(ExposureSeconds, token);
                token.ThrowIfCancellationRequested();
                ValidateSite();
                guidance = session!.GetGuidance(frame);
                var error = guidance.Error;
                cycle.Stop(); lastCycleMilliseconds = cycle.Elapsed.TotalMilliseconds;
                latestError = error; measuredAt = frame.CapturedAtUtc; measurementStale = false;
                livePreview = frame.Preview;
                liveError = $"{error.TotalArcminutes:0.00}′ total · altitude {error.AltitudeArcminutes:+0.00;-0.00;0.00}′ · azimuth {error.AzimuthArcminutes:+0.00;-0.00;0.00}′";
                liveStatus = $"Measured {frame.CapturedAtUtc.ToLocalTime():HH:mm:ss}. Adjust gently and wait for the next measurement. Keep RA and camera orientation fixed.";
                NotifyLive();
                await Task.Delay(250, token);
            }
        });
    }
    private async Task WithLiveOperation(Func<CancellationToken, Task> operation) {
        if (liveBusy) return;
        liveBusy = true; liveCancellation = new CancellationTokenSource(); NotifyLive();
        try { await operation(liveCancellation.Token); }
        catch (OperationCanceledException) { liveStatus = "Stopped. Resume live measurement or reset calibration."; measurementStale = true; guidance = null; }
        catch (Exception ex) { liveStatus = $"Measurement stopped: {ex.Message}"; liveError = "—"; guidance = null; measurementStale = true; }
        finally { liveCancellation.Dispose(); liveCancellation = null; liveBusy = false; NotifyLive(); }
    }
    private void ValidateSite() {
        var site = profiles.ActiveProfile.AstrometrySettings;
        if (site.Latitude != calibrationLatitude || site.Longitude != calibrationLongitude) {
            session = null;
            throw new InvalidOperationException("Observing site changed. Reset and repeat calibration.");
        }
        if (!double.IsFinite(ExposureSeconds) || ExposureSeconds <= 0 || ExposureSeconds > 60)
            throw new InvalidOperationException("Exposure must be greater than 0 and no more than 60 seconds.");
    }
    private void NotifyLive() {
        foreach (var name in new[] { nameof(LiveStatus), nameof(LiveError), nameof(LivePreview), nameof(CanConfigureLive), nameof(ImageWidth), nameof(ImageHeight), nameof(MarkerSize), nameof(AxisLeft), nameof(AxisTop), nameof(PoleLeft), nameof(PoleTop), nameof(HasVisibleGuidance), nameof(GuidanceText) }) RaisePropertyChanged(name);
        foreach (var name in new[] { nameof(LiveTotalError), nameof(LiveAltitudeError), nameof(LiveAzimuthError), nameof(FrameFreshness), nameof(IsMeasurementStale) }) RaisePropertyChanged(name);
        (CaptureReferenceCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (StartLiveCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (StopLiveCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (ResetLiveCommand as IRelayCommand)?.NotifyCanExecuteChanged();
    }
    public void Dispose() { RequestStopLive(); freshnessTimer.Stop(); }
}
