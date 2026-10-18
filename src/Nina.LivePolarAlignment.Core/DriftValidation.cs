using System;
using System.Collections.Generic;
using System.Linq;

namespace Nina.LivePolarAlignment.Core;

public sealed record DriftSample(DateTimeOffset Time, double DeclinationDegrees, double HourAngleDegrees,
    double AltitudeDegrees, double PixelScaleArcseconds);
public sealed record DriftFit(DateTimeOffset StartedAtUtc, DateTimeOffset EndedAtUtc, int Count,
    double HourAngleDegrees, double SlopeArcsecondsPerSecond, double SlopeStandardError,
    double ScatterArcseconds);
public sealed record DriftResult(double AltitudeErrorArcseconds, double AzimuthTangentErrorArcseconds,
    double TotalArcseconds, double ConfidenceRadiusArcseconds, double LowerArcseconds, double UpperArcseconds);

/// <summary>Differential declination drift, independent of the two-position rotation-axis estimator.</summary>
public static class DriftValidation {
    public const double SiderealRadiansPerSecond = 2 * Math.PI / 86164.0905;
    private const double Radians = Math.PI / 180;
    public static void ValidateField(DriftSample sample, bool meridian) {
        if (sample.Time == default || !double.IsFinite(sample.DeclinationDegrees)
            || Math.Abs(sample.DeclinationDegrees) > 25 || !double.IsFinite(sample.HourAngleDegrees)
            || !double.IsFinite(sample.AltitudeDegrees) || sample.AltitudeDegrees < 25
            || !double.IsFinite(sample.PixelScaleArcseconds) || sample.PixelScaleArcseconds <= 0)
            throw new ArgumentException("Use a solved field within 25 degrees of the celestial equator and above 25 degrees altitude.");
        double hour = Math.Abs(sample.HourAngleDegrees);
        if (meridian ? hour > 15 : hour < 45 || hour > 75)
            throw new ArgumentException(meridian ? "First field: hour angle within 15 degrees of the meridian."
                : "Second field: absolute hour angle between 45 and 75 degrees, east or west.");
    }
    public static DriftFit Fit(IReadOnlyList<DriftSample> samples, bool meridian) {
        if (samples.Count < 20) throw new ArgumentException("At least 20 independent solved exposures are required.");
        foreach (var point in samples) ValidateField(point, meridian);
        var start = samples[0]; var last = samples[^1];
        double duration = (last.Time - start.Time).TotalSeconds;
        if (duration < 120 || duration > 600) throw new ArgumentException("Drift observations must span 120 to 600 seconds.");
        for (int i = 1; i < samples.Count; i++) {
            double gap = (samples[i].Time - samples[i - 1].Time).TotalSeconds;
            if (gap < 1 || gap > 30) throw new ArgumentException("Use fresh exposures with gaps between 1 and 30 seconds.");
            if (Math.Abs(RaRotation.WrapDegrees(samples[i].HourAngleDegrees - start.HourAngleDegrees)) > 3
                || Math.Abs(samples[i].PixelScaleArcseconds / start.PixelScaleArcseconds - 1) > .02)
                throw new ArgumentException("Pointing or image scale changed during the drift observation.");
            double expectedHour = start.HourAngleDegrees + (samples[i].Time - start.Time).TotalSeconds * SiderealRadiansPerSecond / Radians;
            if (Math.Abs(RaRotation.WrapDegrees(samples[i].HourAngleDegrees - expectedHour)) > .1)
                throw new ArgumentException("Unexpected RA movement or tracking rate during drift recording.");
        }
        var times = samples.Select(p => (p.Time - start.Time).TotalSeconds).ToArray();
        var positions = samples.Select(p => (p.DeclinationDegrees - start.DeclinationDegrees) * 3600).ToArray();
        double meanTime = times.Average(), meanPosition = positions.Average();
        double xx = times.Sum(t => (t - meanTime) * (t - meanTime));
        double slope = times.Select((t, i) => (t - meanTime) * (positions[i] - meanPosition)).Sum() / xx;
        var residual = times.Select((t, i) => positions[i] - meanPosition - slope * (t - meanTime)).ToArray();
        double scatter = Math.Sqrt(residual.Sum(r => r * r) / (samples.Count - 2));
        if (residual.Max(r => Math.Abs(r)) > Math.Max(5, 6 * scatter) || scatter > 5)
            throw new ArgumentException("Excessive scatter or a pointing jump: repeat in steady conditions.");
        // Inflate for positive lag-one correlation and retain a per-observation noise floor.
        double denom = residual.Sum(r => r * r);
        double floor = Math.Max(.3, start.PixelScaleArcseconds * .1);
        double rho = denom > 0 && scatter >= floor ? Math.Clamp(residual.Skip(1).Select((r, i) => r * residual[i]).Sum() / denom, 0, .9) : 0;
        double noise = Math.Max(scatter, floor);
        double slopeError = noise / Math.Sqrt(xx) * Math.Sqrt((1 + rho) / (1 - rho));
        return new(start.Time, last.Time, samples.Count, samples.Average(p => p.HourAngleDegrees), slope, slopeError, scatter);
    }
    public static DriftResult Combine(DriftFit first, DriftFit second, double latitudeDegrees) {
        foreach (var fit in new[] { first, second }) {
            if (!double.IsFinite(fit.SlopeArcsecondsPerSecond) || !double.IsFinite(fit.SlopeStandardError)
                || fit.SlopeStandardError < 0 || !double.IsFinite(fit.HourAngleDegrees) || fit.Count < 20)
                throw new ArgumentException("Invalid drift fit.");
        }
        if (!double.IsFinite(latitudeDegrees) || Math.Abs(latitudeDegrees) is < 1 or >= 89.5
            || second.StartedAtUtc <= first.EndedAtUtc || (second.EndedAtUtc - first.StartedAtUtc).TotalMinutes > 30)
            throw new ArgumentException("Complete both fields at the same site within 30 minutes, without adjusting the mount.");
        double h1 = first.HourAngleDegrees * Radians, h2 = second.HourAngleDegrees * Radians;
        double a = Math.Sin(h1), b = Math.Cos(h1), c = Math.Sin(h2), d = Math.Cos(h2);
        double determinant = a * d - b * c;
        if (Math.Abs(determinant) < .5) throw new ArgumentException("The two hour angles are insufficiently separated. Repeat with a different second field.");
        // Pointing Dec drift = omega * (north-axis altitude component*sin(H) + east component*cos(H)).
        double scale = 1 / SiderealRadiansPerSecond;
        double x = (d * first.SlopeArcsecondsPerSecond - b * second.SlopeArcsecondsPerSecond) / determinant * scale;
        double y = (-c * first.SlopeArcsecondsPerSecond + a * second.SlopeArcsecondsPerSecond) / determinant * scale;
        double variance = ((d*d + c*c) * first.SlopeStandardError * first.SlopeStandardError
            + (b*b + a*a) * second.SlopeStandardError * second.SlopeStandardError) / (determinant * determinant) * scale * scale;
        double total = Math.Sqrt(x*x + y*y);
        if (!double.IsFinite(total) || total > 5 * 3600) throw new ArgumentException("Drift is outside the small-angle model. Check tracking and pointing.");
        // An engineering guard, NOT a calibrated bound on atmospheric/flexure/solver systematics.
        double radius = 3 * Math.Sqrt(variance) + 30;
        if (!double.IsFinite(radius)) throw new ArgumentException("Drift uncertainty is not finite. Repeat with valid camera scale and steady observations.");
        return new(Math.Sign(latitudeDegrees) * x, y, total, radius, Math.Max(0, total - radius), total + radius);
    }
}

public enum ValidationVerdict { Pending, Inconclusive, Realign, Inconsistent, WithinAvailableChecks }
public static class AlignmentAssessment {
    public static ValidationVerdict Evaluate(double targetArcseconds, double? remeasuredArcseconds,
        double? repeatabilityArcseconds, DriftResult? drift) {
        if (!double.IsFinite(targetArcseconds) || targetArcseconds <= 0) return ValidationVerdict.Inconclusive;
        if (drift != null && drift.LowerArcseconds > targetArcseconds) return ValidationVerdict.Realign;
        if (repeatabilityArcseconds is { } noise && (!double.IsFinite(noise) || noise < 0 || noise > targetArcseconds / 3)) return ValidationVerdict.Inconclusive;
        if (remeasuredArcseconds is { } residual) {
            if (!double.IsFinite(residual) || residual < 0) return ValidationVerdict.Inconclusive;
            if (drift != null && Math.Abs(residual - drift.TotalArcseconds) > Math.Max(targetArcseconds, drift.ConfidenceRadiusArcseconds))
                return ValidationVerdict.Inconsistent;
            if (residual > targetArcseconds) return ValidationVerdict.Realign;
        }
        if (drift == null) return ValidationVerdict.Pending;
        if (drift.UpperArcseconds > targetArcseconds) return ValidationVerdict.Inconclusive;
        return ValidationVerdict.WithinAvailableChecks;
    }
}
