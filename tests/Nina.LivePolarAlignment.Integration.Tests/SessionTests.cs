using System.IO;
using NINA.Astrometry;
using Nina.LivePolarAlignment.Capture;
using Nina.LivePolarAlignment.Core;
using Xunit;

namespace Nina.LivePolarAlignment.Integration.Tests;

[Collection("Isolated NINA environment")]
public sealed class SessionTests {
    private static readonly DateTimeOffset Start = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private const double Longitude = 127;
    private const double Radians = Math.PI / 180;

    [Theory]
    [InlineData(37.5, false)]
    [InlineData(37.5, true)]
    [InlineData(-33.9, false)]
    [InlineData(-33.9, true)]
    public void RecoversKnownAxisAndCorrectionAcrossExposureTimes(double latitude, bool flipped) {
        var initialAxis = Axis(latitude, altitudeError: .6, azimuthError: -.8);
        var initial = Camera(initialAxis, flipped);
        var rotated = Rotate(initial, initialAxis, 72 * Radians);
        var first = Frame(initial, latitude, flipped, Start);
        var second = Frame(rotated, latitude, flipped, Start.AddMinutes(3));
        var session = new PolarAlignmentSession(latitude, Longitude);

        Assert.Equal(1, session.AddCalibration(first));
        Assert.False(session.IsCalibrated);
        var reference = session.GetReferenceGuidance(first);
        Assert.Null(reference.MountAxis);
        Assert.NotNull(reference.CelestialPole);
        Assert.Equal(2, session.AddCalibration(second));
        Assert.True(session.IsCalibrated);
        Assert.InRange(session.RotationDegrees!.Value, 71.97, 72.03);
        Assert.True(session.UsedApproximateEarthRotation);
        AssertError(session.Measure(second), initialAxis, latitude);

        // A rigid camera and mount move together during altitude/azimuth adjustment.
        var correctedAxis = Axis(latitude, altitudeError: .2, azimuthError: .3);
        var correctionAxis = initialAxis.Cross(correctedAxis).Normalized();
        var correctionAngle = Math.Atan2(initialAxis.Cross(correctedAxis).Length, initialAxis.Dot(correctedAxis));
        var corrected = Rotate(rotated, correctionAxis, correctionAngle);
        var later = Frame(corrected, latitude, flipped, Start.AddHours(1));
        var guidance = session.GetGuidance(later);
        AssertError(guidance.Error, correctedAxis, latitude);
        Assert.InRange(guidance.Error.AltitudeArcminutes, 11.97, 12.03);
        Assert.InRange(guidance.Error.AzimuthArcminutes, 17.97, 18.03);
        Assert.Equal(2000, guidance.Width);
        Assert.Equal(1500, guidance.Height);
        var expectedAxisPixel = PolarAlignmentGeometry.ProjectDirection(correctedAxis, corrected, 206264.80624709636 / 4, 1000, 750);
        Assert.NotNull(guidance.MountAxis);
        Assert.InRange(Math.Abs(guidance.MountAxis!.Value.X - expectedAxisPixel!.Value.X), 0, .5);
        Assert.InRange(Math.Abs(guidance.MountAxis.Value.Y - expectedAxisPixel.Value.Y), 0, .5);

        // Same physical orientation an hour later must retain the same mount error,
        // despite its different J2000 right ascension and tangent angle.
        var still = Frame(corrected, latitude, flipped, Start.AddHours(2));
        AssertError(session.Measure(still), correctedAxis, latitude);
        AssertError(session.Measure(still with { CapturedAtUtc = still.CapturedAtUtc.ToOffset(TimeSpan.FromHours(9)) }), correctedAxis, latitude);
    }

    [Theory]
    [InlineData(37.5, false)] [InlineData(37.5, true)]
    [InlineData(-33.9, false)] [InlineData(-33.9, true)]
    public void FreshPairInTheNextRaIntervalConfirmsTheAdjustedAxis(double latitude, bool flipped) {
        var oldAxis = Axis(latitude, .6, -.8);
        var camera = Camera(oldAxis, flipped);
        var oldSecond = Rotate(camera, oldAxis, 60 * Radians);
        var original = new PolarAlignmentSession(latitude, Longitude);
        original.AddCalibration(Frame(camera, latitude, flipped, Start));
        original.AddCalibration(Frame(oldSecond, latitude, flipped, Start.AddMinutes(2)));
        var alignedAxis = Axis(latitude, .02, -.03);
        var correctionAxis = oldAxis.Cross(alignedAxis).Normalized();
        var correctionAngle = Math.Atan2(oldAxis.Cross(alignedAxis).Length, oldAxis.Dot(alignedAxis));
        var adjusted = Rotate(oldSecond, correctionAxis, correctionAngle);
        var verificationFirst = Frame(adjusted, latitude, flipped, Start.AddMinutes(10));
        var verificationSecond = Frame(Rotate(adjusted, alignedAxis, 60 * Radians), latitude, flipped, Start.AddMinutes(12));
        var before = original.Measure(verificationFirst);
        var independentPair = new PolarAlignmentSession(latitude, Longitude);
        independentPair.AddCalibration(verificationFirst); independentPair.AddCalibration(verificationSecond);
        var after = independentPair.Measure(verificationSecond);
        AssertError(before, alignedAxis, latitude); AssertError(after, alignedAxis, latitude);
        Assert.InRange(Math.Abs(before.TotalArcminutes - after.TotalArcminutes), 0, .03);
    }

    [Theory]
    [InlineData(37.5, 0)] [InlineData(37.5, -50)] [InlineData(-33.9, -50)]
    public void DriftUsesObservedDeclinationAndHourAngleWithoutThePolarAxisEstimator(double latitude, double hour) {
        var pole = new Vector3d(0, Math.Cos(latitude * Radians), Math.Sin(latitude * Radians));
        var center = new Vector3d(-Math.Sin(hour * Radians), -Math.Cos(hour * Radians) * Math.Sin(latitude * Radians),
            Math.Cos(hour * Radians) * Math.Cos(latitude * Radians));
        var frame = Frame(new(center, pole.Cross(center), pole), latitude, false, Start);
        var site = InterfaceStub.Create<NINA.Profile.Interfaces.IAstrometrySettings>((m, _) => m.Name switch {
            "get_Latitude" => latitude, "get_Longitude" => Longitude, _ => throw new Exception(m.Name)
        });
        var profile = InterfaceStub.Create<NINA.Profile.Interfaces.IProfile>((m, _) => m.Name == "get_AstrometrySettings" ? site : throw new Exception(m.Name));
        var profiles = InterfaceStub.Create<NINA.Profile.Interfaces.IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : throw new Exception(m.Name));
        var capture = new NinaPolarAlignmentCaptureService(profiles, null!, null!, null!);
        var point = capture.GetDriftSample(frame);
        Assert.InRange(Math.Abs(point.DeclinationDegrees), 0, .0001);
        Assert.InRange(Math.Abs(point.HourAngleDegrees - hour), 0, .0001);
        Assert.InRange(Math.Abs(point.AltitudeDegrees - Math.Asin(center.Z) / Radians), 0, .0001);
    }
    [Fact]
    public void CalibrationStateAndInvalidTransitionsAreExplicit() {
        var session = new PolarAlignmentSession(37.5, Longitude);
        var axis = Axis(37.5, .2, .3);
        var initial = Camera(axis, false);
        var first = Frame(initial, 37.5, false, Start);
        Assert.Throws<InvalidOperationException>(() => session.Measure(first));
        Assert.Equal(1, session.AddCalibration(first));
        var tooClose = Frame(Rotate(initial, axis, 20 * Radians), 37.5, false, Start.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(() => session.AddCalibration(tooClose));
        Assert.False(session.IsCalibrated);
        var second = Frame(Rotate(initial, axis, 65 * Radians), 37.5, false, Start.AddMinutes(2));
        Assert.Equal(2, session.AddCalibration(second));
        Assert.Throws<InvalidOperationException>(() => session.AddCalibration(second));
        Assert.Throws<InvalidOperationException>(() => session.Measure(second with { Flipped = true }));
        Assert.Throws<InvalidOperationException>(() => session.Measure(second with { Width = 1000 }));
        Assert.Throws<InvalidOperationException>(() => session.Measure(second with { PixelScaleArcseconds = 5 }));
        Assert.Throws<InvalidOperationException>(() => session.Measure(first));
        Assert.Throws<InvalidOperationException>(() => session.Measure(second with { DeclinationDegrees = -85 }));
        Assert.Throws<ArgumentException>(() => session.Measure(second with { PixelScaleArcseconds = double.NaN }));
        session.Reset();
        Assert.False(session.IsCalibrated);
        Assert.Throws<InvalidOperationException>(() => session.Measure(second));
        Assert.Equal(1, session.AddCalibration(first));
    }

    [Theory]
    [InlineData(0, 127)]
    [InlineData(90, 127)]
    [InlineData(37, 181)]
    public void RejectsUnsupportedSites(double latitude, double longitude) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PolarAlignmentSession(latitude, longitude));

    private static void AssertError(PolarError actual, Vector3d trueAxis, double latitude) {
        var expected = PolarAlignmentGeometry.CalculateError(trueAxis, latitude);
        Assert.InRange(Math.Abs(actual.AltitudeArcminutes - expected.AltitudeArcminutes), 0, .03);
        Assert.InRange(Math.Abs(actual.AzimuthArcminutes - expected.AzimuthArcminutes), 0, .03);
        Assert.InRange(Math.Abs(actual.TotalArcminutes - expected.TotalArcminutes), 0, .03);
    }

    private static Vector3d Axis(double latitude, double altitudeError, double azimuthError) {
        double altitude = (Math.Abs(latitude) + altitudeError) * Radians;
        double azimuth = (latitude > 0 ? azimuthError : 180 + azimuthError) * Radians;
        return new(Math.Cos(altitude) * Math.Sin(azimuth), Math.Cos(altitude) * Math.Cos(azimuth), Math.Sin(altitude));
    }

    private static SkyOrientation Camera(Vector3d axis, bool flipped) {
        var offset = new Vector3d(1, 0, 0);
        var center = (axis + offset * .06).Normalized();
        var up = (new Vector3d(0, 0, 1) - center * center.Z).Normalized();
        return new(center, up.Cross(center) * (flipped ? -1 : 1), up);
    }

    private static SkyOrientation Rotate(SkyOrientation frame, Vector3d axis, double angle) {
        Vector3d Turn(Vector3d v) => v * Math.Cos(angle) + axis.Cross(v) * Math.Sin(angle)
            + axis * axis.Dot(v) * (1 - Math.Cos(angle));
        return new(Turn(frame.Center), Turn(frame.Right), Turn(frame.Up));
    }

    // Independently synthesize a solver result using the inverse observed->ICRS SOFA
    // transform to the true horizon boresight and camera-up tangent direction. Use the
    // same documented zero DUT1 fallback when the standalone test lacks NINA's database.
    private static SolvedPolarFrame Frame(SkyOrientation horizon, double latitude, bool flipped, DateTimeOffset time) {
        Coordinates Convert(Vector3d v) {
            v = v.Normalized();
            double dut1 = AstroUtil.DeltaUT(time.UtcDateTime);
            if (!double.IsFinite(dut1)) dut1 = 0;
            double ra = 0, dec = 0;
            SOFA.TopocentricToCelestial("A", Math.Atan2(v.X, v.Y), Math.PI / 2 - Math.Asin(v.Z),
                AstroUtil.GetJulianDate(time.UtcDateTime), 0, dut1, Longitude * Radians,
                latitude * Radians, 0, 0, 0, 0, 0, 0, 0, ref ra, ref dec);
            return new Coordinates(Angle.ByRadians(ra), Angle.ByRadians(dec), Epoch.J2000);
        }
        var center = Convert(horizon.Center);
        Assert.True(double.IsFinite(center.RADegrees) && double.IsFinite(center.Dec),
            $"Inverse NINA transform returned RA={center.RADegrees}, Dec={center.Dec}; JD={AstroUtil.GetJulianDate(time.UtcDateTime)}, DUT1={AstroUtil.DeltaUT(time.UtcDateTime)}");
        var upOffset = Convert(horizon.Center + horizon.Up * .0001);
        var equatorial = PolarAlignmentGeometry.FromEquatorial(center.RADegrees, center.Dec, 0);
        var offset = PolarAlignmentGeometry.FromEquatorial(upOffset.RADegrees, upOffset.Dec, 0).Center;
        var tangent = (offset - equatorial.Center * offset.Dot(equatorial.Center)).Normalized();
        double angle = Math.Atan2(tangent.Dot(equatorial.Right), tangent.Dot(equatorial.Up)) / Radians;
        return new(center.RADegrees, center.Dec, angle, 4, 2000, 1500, flipped, time, null);
    }
}

// NINA resolves its Earth-rotation database from LOCALAPPDATA. Keep that lookup and
// any NINA log/database initialization inside a temporary test-process directory.
[CollectionDefinition("Isolated NINA environment", DisableParallelization = true)]
public sealed class IsolatedNinaCollection : ICollectionFixture<IsolatedNinaEnvironment> { }

public sealed class IsolatedNinaEnvironment : IDisposable {
    private readonly string? previous = Environment.GetEnvironmentVariable("LOCALAPPDATA");
    public IsolatedNinaEnvironment() {
        var directory = Path.Combine(Path.GetTempPath(), "Nina.LivePolarAlignment.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "NINA"));
        Environment.SetEnvironmentVariable("LOCALAPPDATA", directory);
    }
    public void Dispose() => Environment.SetEnvironmentVariable("LOCALAPPDATA", previous);
}
