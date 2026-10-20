namespace Nina.LivePolarAlignment.Core;

public readonly record struct UnguidedDriftBound(double ArcsecondsPerMinute, double ExposureArcseconds);

/// <summary>Pointing-independent upper bound from two sidereal angular-velocity vectors.
/// This excludes guiding, seeing, periodic error, flexure and field-rotation prediction.</summary>
public static class PolarAlignmentImpact {
    public static UnguidedDriftBound Estimate(double polarErrorArcseconds, double exposureSeconds) {
        if (!double.IsFinite(polarErrorArcseconds) || polarErrorArcseconds is < 0 or > 648000)
            throw new ArgumentOutOfRangeException(nameof(polarErrorArcseconds));
        if (!double.IsFinite(exposureSeconds) || exposureSeconds is < 0 or > 86400)
            throw new ArgumentOutOfRangeException(nameof(exposureSeconds));

        const double radiansToArcseconds = 180 * 3600 / Math.PI;
        double angle = polarErrorArcseconds / radiansToArcseconds;
        // |omega*a - omega*p| = 2*omega*sin(error/2). Integrating this speed
        // bounds angular displacement at every sky position without claiming a guiding RMS.
        double arcsecondsPerSecond = 2 * DriftValidation.SiderealRadiansPerSecond
            * Math.Sin(angle / 2) * radiansToArcseconds;
        return new(arcsecondsPerSecond * 60, arcsecondsPerSecond * exposureSeconds);
    }
}
