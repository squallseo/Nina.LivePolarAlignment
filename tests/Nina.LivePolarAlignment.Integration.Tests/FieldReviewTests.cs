using System.Text;
using System.IO;
using NINA.Image.ImageAnalysis;
using NINA.Core.Enum;
using NINA.Image.FileFormat;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using Nina.LivePolarAlignment.Capture;
using Nina.LivePolarAlignment.Core;
using Xunit;

namespace Nina.LivePolarAlignment.Integration.Tests;

[Collection("Isolated NINA environment")]
public sealed class FieldReviewTests {
    [Fact]
    public void RecordedSolutionsReplayWithoutRelyingOnCurrentEarthRotationDatabase() {
        var root = Path.Combine(Path.GetTempPath(), "NinaPolarReviewTests", Guid.NewGuid().ToString("N"));
        using var journal = new FieldJournal(root, new { synthetic = true, latitudeDegrees = 37.5, longitudeDegrees = 127d });
        var session = new PolarAlignmentSession(37.5, 127);
        var time = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        void Record(SolvedPolarFrame f, string kind) {
            PolarGuidance? guidance = null;
            if (kind == "reference") session.AddCalibration(f); else guidance = session.GetGuidance(f);
            journal.WriteFrame(new(0, kind, true, new(f.RightAscensionDegrees, f.DeclinationDegrees, f.PositionAngleDegrees,
                f.PixelScaleArcseconds, f.Width, f.Height, f.Flipped, f.CapturedAtUtc, session.LastDut1Seconds),
                "single_frames", 1, 1000, 1, session.RotationDegrees, session.UsedApproximateEarthRotation,
                guidance?.Error.AltitudeArcminutes * 60, guidance?.Error.AzimuthArcminutes * 60,
                guidance?.Error.TotalArcminutes * 60, null, null, null, null, null));
        }
        Record(new(0, 89.6, 0, 6, 800, 600, false, time, null, Dut1Seconds: .123), "reference");
        Record(new(0, 89.6, 72, 6, 800, 600, false, time.AddSeconds(2), null, Dut1Seconds: .123), "reference");
        Record(new(0, 89.7, 72, 6, 800, 600, false, time.AddSeconds(3), null, Dut1Seconds: .123), "measurement");
        journal.Dispose();
        var output = Path.Combine(root, "review"); var report = FieldSessionReview.Review(journal.DirectoryPath, output);
        Assert.Equal(2, report.ReferenceFrames); Assert.Equal(1, report.MeasurementFrames);
        Assert.InRange(report.MaximumReplayDifferenceArcseconds!.Value, 0, 1e-8);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("replay failed"));
        Assert.Equal(4, File.ReadAllLines(Path.Combine(output, "frames.csv")).Length);
    }
    [Fact]
    public async Task NativeFitsWriterPreservesAnUnstretchedRawImageFile() {
        var detection = InterfaceStub.Create<IStarDetection>((m, _) => m.Name == "CreateAnalysis" ? new StarDetectionAnalysis() : null);
        var data = new BaseImageData(new ushort[] { 0, 1000, 40000, 65535 }, 2, 2, 16, false, new ImageMetaData(), null!, detection, null!);
        var root = Path.Combine(Path.GetTempPath(), "NinaPolarFitsTests", Guid.NewGuid().ToString("N"));
        var path = await data.SaveToDisk(new FileSaveInfo { FilePath = root, FilePattern = "raw", FileType = FileTypeEnum.FITS, FITSUseLegacyWriter = true }, CancellationToken.None, true);
        var bytes = File.ReadAllBytes(path);
        Assert.Contains("SIMPLE", Encoding.ASCII.GetString(bytes, 0, 80));
        Assert.Equal(0, bytes.Length % 2880);
        Assert.Equal(new ushort[] { 0, 1000, 40000, 65535 }, data.Data.FlatArray);
    }
}
