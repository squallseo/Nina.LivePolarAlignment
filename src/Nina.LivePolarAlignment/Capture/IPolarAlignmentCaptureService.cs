using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using NINA.Core.Model;

namespace Nina.LivePolarAlignment.Capture;

public interface IPolarAlignmentCaptureService {
    bool SupportsStreaming => false;
    bool IsSynthetic => false;
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
