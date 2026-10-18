using System;
using NINA.Astrometry;
using Nina.LivePolarAlignment.Core;

namespace Nina.LivePolarAlignment.Capture;

public sealed record PolarGuidance(PolarError Error, PixelPoint? MountAxis, PixelPoint? CelestialPole,
    int Width, int Height);

/// <summary>Two manually separated RA positions, then repeated solved frames at fixed RA/Dec.
/// All orientations are converted to the local unrefracted horizon at exposure midpoint.</summary>
public sealed class PolarAlignmentSession {
    private readonly double latitude;
    private readonly double longitude;
    private SolvedPolarFrame? baseline;
    private SolvedPolarFrame? calibration;
    private SkyOrientation first;
    private SkyOrientation second;
    private Vector3d axis;

    public PolarAlignmentSession(double latitudeDegrees, double longitudeDegrees) {
        if (!double.IsFinite(latitudeDegrees) || Math.Abs(latitudeDegrees) >= 89.5 || Math.Abs(latitudeDegrees) < 1)
            throw new ArgumentOutOfRangeException(nameof(latitudeDegrees), "Set a site latitude between 1 and 89.5 degrees north or south.");
        if (!double.IsFinite(longitudeDegrees) || Math.Abs(longitudeDegrees) > 180)
            throw new ArgumentOutOfRangeException(nameof(longitudeDegrees));
        latitude = latitudeDegrees;
        longitude = longitudeDegrees;
    }

    public bool IsCalibrated => calibration != null;
    public bool UsedApproximateEarthRotation { get; private set; }
    public void Reset() { baseline = null; calibration = null; }

    public int AddCalibration(SolvedPolarFrame frame) {
        if (IsCalibrated) throw new InvalidOperationException("Restart before taking another calibration.");
        ValidateFrame(frame);
        var orientation = ToHorizon(frame);
        if (baseline == null) {
            baseline = frame;
            first = orientation;
            return 1;
        }
        ValidateCompatible(baseline, frame);
        var estimate = PolarAlignmentGeometry.EstimateAxis(first, orientation);
        if (estimate.Conditioning < Math.Sin(15 * Math.PI / 180))
            throw new InvalidOperationException("Rotate the RA axis at least 30 degrees; 60 to 90 degrees is recommended. Leave altitude and azimuth unchanged.");
        var pole = Pole;
        var selectedAxis = estimate.Axis.Dot(pole) < 0 ? estimate.Axis * -1 : estimate.Axis;
        if (PolarAlignmentGeometry.CalculateError(selectedAxis, latitude).TotalArcminutes > 15 * 60)
            throw new InvalidOperationException("Calibration is more than 15 degrees from the pole. Check camera rigidity and rotate only the RA axis.");
        second = orientation;
        axis = selectedAxis;
        calibration = frame;
        return 2;
    }

    public PolarError Measure(SolvedPolarFrame frame) => GetGuidance(frame).Error;

    public PolarGuidance GetGuidance(SolvedPolarFrame frame) {
        if (calibration == null) throw new InvalidOperationException("Take both calibration frames first.");
        ValidateFrame(frame);
        ValidateCompatible(calibration, frame);
        var current = ToHorizon(frame);
        var currentAxis = PolarAlignmentGeometry.TransformDirection(axis, second, current);
        var error = PolarAlignmentGeometry.CalculateError(currentAxis, latitude);
        var focalPixels = 206264.80624709636 / frame.PixelScaleArcseconds;
        return new(error,
            PolarAlignmentGeometry.ProjectDirection(currentAxis, current, focalPixels, frame.Width / 2.0, frame.Height / 2.0),
            PolarAlignmentGeometry.ProjectDirection(Pole, current, focalPixels, frame.Width / 2.0, frame.Height / 2.0),
            frame.Width, frame.Height);
    }

    private Vector3d Pole {
        get {
            double lat = latitude * Math.PI / 180;
            return new(0, Math.Cos(lat) * Math.Sign(lat), Math.Abs(Math.Sin(lat)));
        }
    }

    private void ValidateFrame(SolvedPolarFrame frame) {
        if (!double.IsFinite(frame.RightAscensionDegrees) || !double.IsFinite(frame.DeclinationDegrees)
            || Math.Abs(frame.DeclinationDegrees) > 90 || !double.IsFinite(frame.PositionAngleDegrees)
            || frame.CapturedAtUtc == default)
            throw new ArgumentException("A finite sky position, orientation, and exposure timestamp are required.");
        if (!double.IsFinite(frame.PixelScaleArcseconds) || frame.PixelScaleArcseconds <= 0 || frame.Width <= 0 || frame.Height <= 0)
            throw new ArgumentException("A valid full-frame plate solution is required.");
        if (Math.Abs(frame.DeclinationDegrees) < 75 || Math.Sign(frame.DeclinationDegrees) != Math.Sign(latitude))
            throw new InvalidOperationException("Point the camera within 15 degrees of the visible celestial pole before starting.");
    }

    private static void ValidateCompatible(SolvedPolarFrame reference, SolvedPolarFrame frame) {
        if (reference.Width != frame.Width || reference.Height != frame.Height || reference.Flipped != frame.Flipped
            || Math.Abs(frame.PixelScaleArcseconds / reference.PixelScaleArcseconds - 1) > .02)
            throw new InvalidOperationException("Image size, parity, or scale changed. Keep the camera configuration fixed and restart calibration.");
        if (frame.CapturedAtUtc < reference.CapturedAtUtc)
            throw new InvalidOperationException("Frame timestamps must follow the calibration exposure.");
    }

    private SkyOrientation ToHorizon(SolvedPolarFrame frame) {
        var eq = PolarAlignmentGeometry.FromEquatorial(frame.RightAscensionDegrees, frame.DeclinationDegrees,
            frame.PositionAngleDegrees, frame.Flipped);
        var utc = frame.CapturedAtUtc.UtcDateTime;
        var julianUtc = AstroUtil.GetJulianDate(utc);
        double dut1;
        try { dut1 = AstroUtil.DeltaUT(utc); }
        catch { dut1 = double.NaN; }
        UsedApproximateEarthRotation = !double.IsFinite(dut1);
        if (UsedApproximateEarthRotation) dut1 = 0;
        // Transform tiny tangent offsets with NINA's SOFA path so J2000 precession, Earth
        // rotation, and tangent orientation use the same exposure time. No atmospheric
        // refraction model is enabled in this prototype.
        Vector3d Convert(Vector3d v) {
            v = v.Normalized();
            double az = 0, zenithDistance = 0, hourAngle = 0, observedDec = 0, observedRa = 0, equationOrigins = 0;
            SOFA.CelestialToTopocentric(Math.Atan2(v.Y, v.X), Math.Asin(Math.Clamp(v.Z, -1, 1)),
                0, 0, 0, 0, julianUtc, 0, dut1, longitude * Math.PI / 180, latitude * Math.PI / 180,
                0, 0, 0, 0, 0, 0, 0, ref az, ref zenithDistance, ref hourAngle, ref observedDec,
                ref observedRa, ref equationOrigins);
            var alt = Math.PI / 2 - zenithDistance;
            return new(Math.Cos(alt) * Math.Sin(az), Math.Cos(alt) * Math.Cos(az), Math.Sin(alt));
        }
        var center = Convert(eq.Center);
        var upSample = Convert(eq.Center + eq.Up * .0001);
        var up = (upSample - center * upSample.Dot(center)).Normalized();
        var rightSample = Convert(eq.Center + eq.Right * .0001);
        var right = (rightSample - center * rightSample.Dot(center) - up * rightSample.Dot(up)).Normalized();
        return new(center, right, up);
    }
}
