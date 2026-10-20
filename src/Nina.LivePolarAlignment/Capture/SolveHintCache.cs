using System;
using NINA.Astrometry;

namespace Nina.LivePolarAlignment.Capture;

public readonly record struct SolveHintContext(Guid ProfileId, string CameraId, double FocalLength, double PixelSize);

/// <summary>A search hint only. Every frame must still receive a successful plate solution.</summary>
public sealed class SolveHintCache {
    private readonly Func<DateTimeOffset> now;
    private readonly object gate = new();
    private SolveHintContext context;
    private Coordinates? coordinates;
    private DateTimeOffset storedAt;
    public SolveHintCache(Func<DateTimeOffset>? now = null) => this.now = now ?? (() => DateTimeOffset.UtcNow);
    public Coordinates? Get(SolveHintContext requested) {
        lock (gate) {
            var age = now() - storedAt;
            if (coordinates == null || context != requested || age < TimeSpan.Zero || age > TimeSpan.FromMinutes(10)) return null;
            return coordinates.Clone();
        }
    }
    public void Store(SolveHintContext requested, Coordinates solved) {
        if (!double.IsFinite(solved.RADegrees) || !double.IsFinite(solved.Dec)
            || solved.RADegrees is < 0 or >= 360 || Math.Abs(solved.Dec) > 90)
            throw new ArgumentOutOfRangeException(nameof(solved));
        lock (gate) { context = requested; coordinates = solved.Clone(); storedAt = now(); }
    }
    public void Clear() { lock (gate) { coordinates = null; } }
}
