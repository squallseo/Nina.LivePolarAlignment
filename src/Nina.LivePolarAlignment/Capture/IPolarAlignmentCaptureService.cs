using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using NINA.Core.Model;

namespace Nina.LivePolarAlignment.Capture;

public interface IPolarAlignmentCaptureService {
    bool SupportsStreaming => false;
    bool IsSynthetic => false;
    bool SupportsAutoRaRotation => false;
    object? DriftMountIdentity => null;
    void ValidateDriftTracking() => throw new NotSupportedException("Drift verification is unavailable.");
    Core.DriftSample GetDriftSample(SolvedPolarFrame frame) => throw new NotSupportedException("Drift verification is unavailable.");
    Task RotateRaAsync(bool reverse, IProgress<double>? progress, CancellationToken token) => throw new NotSupportedException("Automatic RA rotation is unavailable. Use manual rotation.");
    System.Collections.Generic.IAsyncEnumerable<SolvedPolarFrame> StreamAndSolveAsync(double exposureSeconds, CancellationToken token) => throw new NotSupportedException("Streaming is unavailable for this camera.");
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
