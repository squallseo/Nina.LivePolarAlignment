using System.Text;
using System.Text.Json;

namespace Nina.LivePolarAlignment.Core;

public sealed record FieldSolution(double RightAscensionDegrees, double DeclinationDegrees, double PositionAngleDegrees,
    double PixelScaleArcseconds, int Width, int Height, bool Flipped, DateTimeOffset CapturedAtUtc, double? Dut1Seconds);
public sealed record FieldFrameRecord(long FrameId, string Kind, bool Accepted, FieldSolution Solution, string Acquisition,
    double ExposureSeconds, double CycleMilliseconds, double FrameAgeSeconds, double? RotationDegrees,
    bool ApproximateEarthRotation, double? AltitudeArcseconds, double? AzimuthArcseconds, double? TotalArcseconds,
    double? AxisX, double? AxisY, double? PoleX, double? PoleY, string? Failure);
public sealed record FieldEntry(long Sequence, DateTimeOffset RecordedAtUtc, string Type, JsonElement Data);

/// <summary>Append-only local evidence; every completed line is flushed independently.</summary>
public sealed class FieldJournal : IDisposable {
    public static JsonSerializerOptions Json { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object gate = new();
    private readonly StreamWriter writer;
    private long sequence;
    public string DirectoryPath { get; }
    public int FrameCount { get; private set; }
    public FieldJournal(string root, object metadata) {
        DirectoryPath = Path.Combine(Path.GetFullPath(root), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath, "session.json"), JsonSerializer.Serialize(new {
            schemaVersion = 1, startedAtUtc = DateTimeOffset.UtcNow, metadata
        }, new JsonSerializerOptions(Json) { WriteIndented = true }), new UTF8Encoding(false));
        writer = new StreamWriter(new FileStream(Path.Combine(DirectoryPath, "events.jsonl"), FileMode.CreateNew,
            FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }
    public long WriteFrame(FieldFrameRecord frame) {
        lock (gate) {
            var id = ++sequence;
            Append(id, "frame", frame with { FrameId = id }); FrameCount++; return id;
        }
    }
    public long WriteEvent(string type, object data) {
        lock (gate) { var id = ++sequence; Append(id, type, data); return id; }
    }
    private void Append(long id, string type, object data) => writer.WriteLine(JsonSerializer.Serialize(
        new FieldEntry(id, DateTimeOffset.UtcNow, type, JsonSerializer.SerializeToElement(data, Json)), Json));
    public void Dispose() { lock (gate) writer.Dispose(); }
    public static IReadOnlyList<FieldEntry> Read(string directory, out bool incompleteLastLine) {
        using var input = new FileStream(Path.Combine(directory, "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(input);
        var lines = reader.ReadToEnd().TrimEnd('\r', '\n').Split('\n');
        var entries = new List<FieldEntry>(); incompleteLastLine = false;
        for (int i = 0; i < lines.Length; i++) {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            try { entries.Add(JsonSerializer.Deserialize<FieldEntry>(lines[i], Json) ?? throw new JsonException("Empty entry")); }
            catch (JsonException) when (i == lines.Length - 1) { incompleteLastLine = true; }
        }
        return entries;
    }
}

public sealed record StabilityResult(int Count, double MeanAltitudeArcseconds, double MeanAzimuthArcseconds,
    double SigmaAltitudeArcseconds, double SigmaAzimuthArcseconds, double RangeAltitudeArcseconds,
    double RangeAzimuthArcseconds, long FirstFrameId, long LastFrameId, DateTimeOffset StartedAtUtc, DateTimeOffset EndedAtUtc);

/// <summary>Sample scatter while the operator holds the mount still, not an accuracy estimate.</summary>
public sealed class StabilityWindow {
    private readonly List<(long Id, DateTimeOffset Time, double Alt, double Az)> samples = new();
    public int RequiredSamples { get; }
    public int Count => samples.Count;
    public bool Complete => Count >= RequiredSamples;
    public StabilityWindow(int requiredSamples = 20) {
        if (requiredSamples < 2) throw new ArgumentOutOfRangeException(nameof(requiredSamples));
        RequiredSamples = requiredSamples;
    }
    public void Add(long frameId, DateTimeOffset capturedAtUtc, double altitudeArcseconds, double azimuthArcseconds) {
        if (Complete) throw new InvalidOperationException("Stability window is complete.");
        if (!double.IsFinite(altitudeArcseconds) || !double.IsFinite(azimuthArcseconds)) throw new ArgumentException("Finite offsets are required.");
        if (Count > 0 && (frameId <= samples[^1].Id || capturedAtUtc <= samples[^1].Time))
            throw new ArgumentException("A new, later exposure is required.");
        samples.Add((frameId, capturedAtUtc, altitudeArcseconds, azimuthArcseconds));
    }
    public StabilityResult Result() {
        if (!Complete) throw new InvalidOperationException("Collect the full stability window first.");
        double alt = samples.Average(p => p.Alt), az = samples.Average(p => p.Az);
        return new(Count, alt, az, Math.Sqrt(samples.Sum(p => Math.Pow(p.Alt - alt, 2)) / (Count - 1)),
            Math.Sqrt(samples.Sum(p => Math.Pow(p.Az - az, 2)) / (Count - 1)),
            samples.Max(p => p.Alt) - samples.Min(p => p.Alt), samples.Max(p => p.Az) - samples.Min(p => p.Az),
            samples[0].Id, samples[^1].Id, samples[0].Time, samples[^1].Time);
    }
}
