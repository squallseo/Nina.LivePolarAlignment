using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Nina.LivePolarAlignment.Core;

namespace Nina.LivePolarAlignment.Capture;

public sealed record FieldReviewReport(bool Synthetic, int ReferenceFrames, int MeasurementFrames, int RejectedFrames,
    int MarkedSamples, int RawFitsFiles, bool IncompleteLastLine, double? MaximumReplayDifferenceArcseconds,
    IReadOnlyList<string> Warnings);

public static class FieldSessionReview {
    /// <summary>Recomputes geometry from recorded WCS and DUT1; never connects equipment or solves FITS again.</summary>
    public static FieldReviewReport Review(string directory, string outputDirectory) {
        directory = Path.GetFullPath(directory);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "session.json")));
        if (manifest.RootElement.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported field-session schema.");
        var metadata = manifest.RootElement.GetProperty("metadata");
        var synthetic = metadata.GetProperty("synthetic").GetBoolean();
        var session = new PolarAlignmentSession(metadata.GetProperty("latitudeDegrees").GetDouble(), metadata.GetProperty("longitudeDegrees").GetDouble());
        var entries = FieldJournal.Read(directory, out var incomplete);
        var warnings = new List<string>();
        if (synthetic) warnings.Add("Synthetic input: not evidence of physical accuracy.");
        if (incomplete) warnings.Add("Ignored incomplete final journal line; preceding records were recovered.");
        int references = 0, measurements = 0, rejected = 0, marks = 0, fits = 0;
        double? largestDifference = null;
        var csv = new StringBuilder("frame_id,kind,accepted,acquisition,captured_utc,ra_degrees,dec_degrees,position_angle_degrees,pixel_scale_arcseconds,dut1_seconds,altitude_arcseconds,azimuth_arcseconds,total_arcseconds,cycle_ms,frame_age_seconds,replayed_total_arcseconds,replay_max_difference_arcseconds\n");
        foreach (var entry in entries) {
            if (entry.Type == "sample_saved") {
                if (entry.Data.GetProperty("kind").GetString() == "mark") marks++;
                if (entry.Data.TryGetProperty("fits", out var raw) && raw.ValueKind == JsonValueKind.String) {
                    string name = raw.GetString()!;
                    if (Path.GetFileName(name) != name || !File.Exists(Path.Combine(directory, name))) warnings.Add($"Missing or invalid RAW image name at entry {entry.Sequence}.");
                    else fits++;
                }
                continue;
            }
            if (entry.Type != "frame") continue;
            var frame = entry.Data.Deserialize<FieldFrameRecord>(FieldJournal.Json) ?? throw new InvalidDataException("Invalid frame record.");
            double? replayedTotal = null, difference = null;
            if (!frame.Accepted) rejected++;
            else {
                try {
                    var solution = frame.Solution;
                    if (solution.Dut1Seconds == null) throw new InvalidDataException("Frame lacks the recorded Earth-rotation correction.");
                    var solved = new SolvedPolarFrame(solution.RightAscensionDegrees, solution.DeclinationDegrees, solution.PositionAngleDegrees,
                        solution.PixelScaleArcseconds, solution.Width, solution.Height, solution.Flipped,
                        solution.CapturedAtUtc, null, Dut1Seconds: solution.Dut1Seconds);
                    if (frame.Kind == "reference") { session.AddCalibration(solved); references++; }
                    else if (frame.Kind == "measurement") {
                        var error = session.Measure(solved); measurements++; replayedTotal = error.TotalArcminutes * 60;
                        if (frame.AltitudeArcseconds is { } alt && frame.AzimuthArcseconds is { } az && frame.TotalArcseconds is { } total) {
                            difference = new[] { Math.Abs(error.AltitudeArcminutes * 60 - alt), Math.Abs(error.AzimuthArcminutes * 60 - az), Math.Abs(replayedTotal.Value - total) }.Max();
                            largestDifference = Math.Max(largestDifference ?? 0, difference.Value);
                        }
                    } else warnings.Add($"Unknown frame kind at {frame.FrameId}.");
                } catch (Exception error) { warnings.Add($"Frame {frame.FrameId} replay failed: {error.Message}"); }
            }
            var f = frame.Solution;
            csv.AppendLine(string.Join(",", new object?[] { frame.FrameId, frame.Kind, frame.Accepted, frame.Acquisition,
                f.CapturedAtUtc.ToString("O"), f.RightAscensionDegrees, f.DeclinationDegrees, f.PositionAngleDegrees,
                f.PixelScaleArcseconds, f.Dut1Seconds, frame.AltitudeArcseconds, frame.AzimuthArcseconds, frame.TotalArcseconds,
                frame.CycleMilliseconds, frame.FrameAgeSeconds, replayedTotal, difference }.Select(Cell)));
        }
        var report = new FieldReviewReport(synthetic, references, measurements, rejected, marks, fits, incomplete, largestDifference, warnings);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "frames.csv"), csv.ToString(), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(outputDirectory, "review.json"), JsonSerializer.Serialize(report,
            new JsonSerializerOptions(FieldJournal.Json) { WriteIndented = true }), new UTF8Encoding(false));
        return report;
    }
    private static string Cell(object? value) {
        string text = value is IFormattable format ? format.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "";
        // Text fields remain literal when a CSV is opened in a spreadsheet.
        if (text.Length > 0 && "=+@".Contains(text[0])) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
