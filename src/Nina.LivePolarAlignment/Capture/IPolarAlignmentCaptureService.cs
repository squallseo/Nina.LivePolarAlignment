using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using NINA.Core.Model;

namespace Nina.LivePolarAlignment.Capture;

public sealed class PolarisIdentificationException : InvalidOperationException {
    public PolarisIdentificationException(Exception inner) : base("Polaris identification failed before RA rotation.",inner) { }
}

public sealed class StreamingCleanupException : InvalidOperationException {
    public StreamingCleanupException(Exception inner) : base("Could not restore the camera after streaming. Reconnect before continuing.", inner) { }
}

public enum AutoRaAvailability { Ready, Disconnected, Parked, Busy, NotEquatorial, AxisUnsupported, TrackingUnsupported, RateUnsupported, InvalidCoordinates, DriverError }

public interface IPolarAlignmentCaptureService {
    AlignmentCameraCapabilities? AlignmentCamera=>null;
    bool CanHomeForRestart => false;
    bool MountAtHome => false;
    void SetPatternScale(double scale) { }
    void SetManualPolarisAdjustment(Core.PixelPoint? normalizedTarget) { }
    Task HomeForRestartAsync(CancellationToken token) => throw new NotSupportedException("The mount does not support FindHome.");
    int CameraGain => -1;
    bool TryQueueLiveExposure(double seconds) => false;
    Core.PixelPoint? PolarisCandidate { get => null; set { } }
    event Action<BitmapSource>? PreviewAvailable { add { } remove { } }
    event Action<StreamingPreview>? StreamingPreviewAvailable { add { } remove { } }
    Task RunPreviewAsync(double exposureSeconds, CancellationToken token) => throw new NotSupportedException("Live preview is unavailable.");
    double PreviewStretchStrength { get => 1; set { } }
    bool PreviewStretchEnabled {get=>true;set{}}
    Task<BitmapSource?> RefreshPreviewAsync(CancellationToken token) => Task.FromResult<BitmapSource?>(null);
    bool PoleMasterMode { get => false; set { } }
    void ValidateAlignmentCamera() { }
    int StreamingBinning { get => 2; set { } }
    AutoRaAvailability AutoRaAvailability => SupportsAutoRaRotation ? AutoRaAvailability.Ready : AutoRaAvailability.DriverError;
    bool CameraConnected => false;
    bool MountConnected => false;
    bool SupportsStreaming => false;
    bool IsSynthetic => false;
    void ResetSolveHint() { }
    bool SupportsAutoRaRotation => false;
    bool CanPrepareAutoRaRotation => SupportsAutoRaRotation;
    Task PrepareAutoRaAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    object? DriftMountIdentity => null;
    void ValidateDriftTracking() => throw new NotSupportedException("Drift verification is unavailable.");
    Core.DriftSample GetDriftSample(SolvedPolarFrame frame) => throw new NotSupportedException("Drift verification is unavailable.");
    Task RotateRaAsync(bool reverse, IProgress<double>? progress, CancellationToken token, double targetDegrees = 90) => throw new NotSupportedException("Automatic RA rotation is unavailable. Use manual rotation.");
    Task ContinueRaAsync(bool reverse, IProgress<double>? progress, CancellationToken token, double targetDegrees = 40) => throw new NotSupportedException("Continued RA rotation is unavailable.");
    async Task RotateRaWithPreviewAsync(bool reverse, bool continuation, double exposureSeconds, IProgress<double>? progress, CancellationToken token, double targetDegrees = 40) {
        if(continuation) await ContinueRaAsync(reverse,progress,token,targetDegrees);
        else await RotateRaAsync(reverse,progress,token,targetDegrees);
    }
    void ResetRaReturn() { }
    Task ReturnRaWithPreviewAsync(double exposureSeconds, IProgress<double>? progress, CancellationToken token) => ReturnRaToStartAsync(progress,token);
    Task ReturnRaToStartAsync(IProgress<double>? progress, CancellationToken token) => throw new NotSupportedException("RA return is unavailable.");
    System.Collections.Generic.IAsyncEnumerable<SolvedPolarFrame> StreamAndSolveAsync(double exposureSeconds, CancellationToken token) => throw new NotSupportedException("Streaming is unavailable for this camera.");
    async Task<SolvedPolarFrame> CaptureAndSolveWithRotationAsync(double exposureSeconds, bool reverse,
        IProgress<double>? progress, CancellationToken token, double targetDegrees = 90,
        Func<SolvedPolarFrame, Task>? referenceSolved = null) {
        var frame = await CaptureAndSolveAsync(exposureSeconds, token);
        if (referenceSolved != null) await referenceSolved(frame);
        progress?.Report(0);
        await RotateRaAsync(reverse, progress, token, targetDegrees);
        return frame;
    }
    Task<SolvedPolarFrame> CaptureAndSolveAsync(double exposureSeconds, CancellationToken token,
        IProgress<ApplicationStatus>? progress = null);
}

/// <summary>A full-frame J2000 solution. Position angle is north through east, as supplied by NINA.</summary>
public sealed record SolvedPolarFrame(
    double RightAscensionDegrees,
    double DeclinationDegrees,
    double PositionAngleDegrees,
    double PixelScaleArcseconds,
    int Width,
    int Height,
    bool Flipped,
    DateTimeOffset CapturedAtUtc,
    BitmapSource? Preview,
    NINA.Image.Interfaces.IImageData? RawImage = null,
    double? Dut1Seconds = null,
    bool IsTracked = false, int TrackedStars = 0, double TrackingRmsPixels = 0, StarOverlay? Stars = null);

public sealed record ManualPolarisOffset(Core.PixelPoint NormalizedPosition,double DistanceArcseconds);
public sealed record StreamingPreview(BitmapSource Image, DateTimeOffset CapturedAt, string Reason, bool IsAimingPreview = false, bool IsRotationPreview = false, StarOverlay? Stars = null,bool IsManualPolarisAdjustment=false,ManualPolarisOffset? ManualOffset=null);

public sealed record StarOverlay(Core.TrackedStar[] Markers, int Width, int Height, bool Verified, DateTimeOffset CapturedAt = default, Core.TrackedStar[]? TemplateMarkers = null);
