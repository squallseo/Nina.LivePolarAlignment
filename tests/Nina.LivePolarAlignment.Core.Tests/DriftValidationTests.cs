using Nina.LivePolarAlignment.Core;
using Xunit;

namespace Nina.LivePolarAlignment.Core.Tests;

public sealed class DriftValidationTests {
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private const double Rad = Math.PI / 180;
    private static List<DriftSample> PhysicalSamples(double latitude, double hour, double altitudeError, double eastError, int offset = 0) {
        var k = new Vector3d(0, Math.Cos(latitude * Rad), Math.Sin(latitude * Rad));
        var u = new Vector3d(0, -Math.Sin(latitude * Rad), Math.Cos(latitude * Rad));
        var axis = (k + u * (Math.Sign(latitude) * altitudeError / 3600 * Rad) + new Vector3d(eastError / 3600 * Rad, 0, 0)).Normalized();
        var r0 = new Vector3d(-Math.Sin(hour * Rad), -Math.Cos(hour * Rad) * Math.Sin(latitude * Rad), Math.Cos(hour * Rad) * Math.Cos(latitude * Rad));
        var samples = new List<DriftSample>();
        for (int i = 0; i <= 90; i++) {
            double seconds = i * 6, angle = -DriftValidation.SiderealRadiansPerSecond * seconds;
            var r = r0 * Math.Cos(angle) + axis.Cross(r0) * Math.Sin(angle) + axis * axis.Dot(r0) * (1 - Math.Cos(angle));
            samples.Add(new(Start.AddSeconds(offset + seconds), Math.Asin(r.Dot(k)) / Rad,
                hour - angle / Rad, Math.Asin(r.Z) / Rad, 2));
        }
        return samples;
    }
    [Theory]
    [InlineData(37.5, 90, 120)] [InlineData(-33.9, -90, 120)]
    [InlineData(37.5, -90, -120)] [InlineData(-33.9, 90, -120)]
    public void RecoversInjectedAxisFromIndependentTrackingKinematics(double latitude, double altitude, double east) {
        var first = DriftValidation.Fit(PhysicalSamples(latitude, 0, altitude, east), true);
        var second = DriftValidation.Fit(PhysicalSamples(latitude, -50, altitude, east, 600), false);
        var result = DriftValidation.Combine(first, second, latitude);
        Assert.InRange(Math.Abs(result.AltitudeErrorArcseconds - altitude), 0, 3);
        Assert.InRange(Math.Abs(result.AzimuthTangentErrorArcseconds - east), 0, 3);
        Assert.InRange(result.TotalArcseconds, 147, 153);
        Assert.Equal(ValidationVerdict.Realign, AlignmentAssessment.Evaluate(60, null, null, result));
    }
    [Fact]
    public void ZeroDriftHasANonzeroGuardAndCannotCertifySubGuardAccuracy() {
        var result = DriftValidation.Combine(DriftValidation.Fit(PhysicalSamples(37.5, 0, 0, 0), true),
            DriftValidation.Fit(PhysicalSamples(37.5, -50, 0, 0, 600), false), 37.5);
        Assert.InRange(result.TotalArcseconds, 0, .01);
        Assert.True(result.ConfidenceRadiusArcseconds >= 30);
        Assert.Equal(ValidationVerdict.Inconclusive, AlignmentAssessment.Evaluate(15, null, null, result));
        Assert.Equal(ValidationVerdict.WithinAvailableChecks, AlignmentAssessment.Evaluate(60, null, null, result));
    }
    [Theory]
    [InlineData("pole")] [InlineData("horizon")] [InlineData("short")] [InlineData("duplicate")]
    [InlineData("scale")] [InlineData("jump")] [InlineData("badHour")]
    public void RejectsInvalidObservations(string reason) {
        var data = PhysicalSamples(37.5, 0, 0, 0);
        if (reason == "pole") data[0] = data[0] with { DeclinationDegrees = 89 };
        if (reason == "horizon") data[0] = data[0] with { AltitudeDegrees = 10 };
        if (reason == "short") data = data.Take(20).ToList();
        if (reason == "duplicate") data[1] = data[1] with { Time = data[0].Time };
        if (reason == "scale") data[1] = data[1] with { PixelScaleArcseconds = 4 };
        if (reason == "jump") data[15] = data[15] with { DeclinationDegrees = 1 };
        if (reason == "badHour") data[0] = data[0] with { HourAngleDegrees = 60 };
        Assert.ThrowsAny<ArgumentException>(() => DriftValidation.Fit(data, true));
    }
    [Fact]
    public void ShortLowDriftIsNotEnoughToPassATightTarget() {
        var first = DriftValidation.Fit(PhysicalSamples(37.5, 0, 0, 0).Take(31).ToArray(), true);
        var second = DriftValidation.Fit(PhysicalSamples(37.5, -50, 0, 0, 600).Take(31).ToArray(), false);
        var result = DriftValidation.Combine(first, second, 37.5);
        Assert.Equal(ValidationVerdict.Inconclusive, AlignmentAssessment.Evaluate(60, null, null, result));
    }
    [Fact]
    public void QualityAndDisagreementNeverProduceAnUnqualifiedPass() {
        var good = new DriftResult(0, 0, 0, 35, 0, 35);
        Assert.Equal(ValidationVerdict.Pending, AlignmentAssessment.Evaluate(60, 20, 1, null));
        Assert.Equal(ValidationVerdict.Inconclusive, AlignmentAssessment.Evaluate(60, 20, 30, good));
        Assert.Equal(ValidationVerdict.Inconsistent, AlignmentAssessment.Evaluate(60, 200, 1, good));
        Assert.Equal(ValidationVerdict.Realign, AlignmentAssessment.Evaluate(60, 200, 1, null));
        Assert.Equal(ValidationVerdict.WithinAvailableChecks, AlignmentAssessment.Evaluate(60, 20, 1, good));
        Assert.Equal(ValidationVerdict.Inconclusive, AlignmentAssessment.Evaluate(double.NaN, 20, 1, good));
    }
    [Fact]
    public void RejectsExpiredOrInsufficientlySeparatedFields() {
        var first = DriftValidation.Fit(PhysicalSamples(37.5, 0, 0, 0), true);
        var second = DriftValidation.Fit(PhysicalSamples(37.5, -50, 0, 0, 600), false);
        Assert.Throws<ArgumentException>(() => DriftValidation.Combine(first, second with { HourAngleDegrees = 5 }, 37.5));
        Assert.Throws<ArgumentException>(() => DriftValidation.Combine(first, second with { EndedAtUtc = Start.AddHours(1) }, 37.5));
    }
}
