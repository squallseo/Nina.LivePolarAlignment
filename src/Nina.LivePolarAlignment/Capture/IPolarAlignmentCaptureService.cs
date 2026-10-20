using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using NINA.Core.Model;

namespace Nina.LivePolarAlignment.Capture;

public sealed class StreamingCleanupException : InvalidOperationException {
    public StreamingCleanupException(Exception inner) : base("Could not restore the camera after streaming. Reconnect before continuing.", inner) { }
}

public enum AutoRaAvailability { Ready, Disconnected, Parked, Busy, NotEquatorial, AxisUnsupported, TrackingUnsupported, RateUnsupported, InvalidCoordinates, DriverError }

public interface IPolarAlignmentCaptureService {
    event Action<BitmapSource>? PreviewAvailable { add { } remove { } }
    AutoRaAvailability AutoRaAvailability => SupportsAutoRaRotation ? AutoRaAvailability.Ready : AutoRaAvailability.DriverError;
    bool CameraConnected => false;
    bool MountConnected => false;
    bool SupportsStreaming => false;
    bool IsSynthetic => false;
    bool SupportsAutoRaRotation => false;
    bool CanPrepareAutoRaRotation => SupportsAutoRaRotation;
    Task PrepareAutoRaAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    object? DriftMountIdentity => null;
    void ValidateDriftTracking() => throw new NotSupportedException("Drift verification is unavailable.");
    Core.DriftSample GetDriftSample(SolvedPolarFrame frame) => throw new NotSupportedException("Drift verification is unavailable.");
    Task RotateRaAsync(bool reverse, IProgress<double>? progress, CancellationToken token, double targetDegrees = 90) => throw new NotSupportedException("Automatic RA rotation is unavailable. Use manual rotation.");
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
    double? Dut1Seconds = null);
