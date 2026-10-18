using System;
using System.ComponentModel.Composition;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using Nina.LivePolarAlignment.Core;

namespace Nina.LivePolarAlignment.Capture;

public sealed partial class NinaPolarAlignmentCaptureService {
    [Import(AllowDefault = true)] public IGuiderMediator? DriftGuider { get; set; }
    public object? DriftMountIdentity => telescope.GetDevice();
    public void ValidateDriftTracking() {
        var device = telescope.GetDevice() as ITelescope;
        if (device == null || !device.Connected || device.AtPark || device.Slewing || device.IsPulseGuiding || !device.TrackingEnabled
            || device.TrackingMode != TrackingMode.Sidereal
            || device.AlignmentMode is not (AlignmentMode.Polar or AlignmentMode.GermanPolar))
            throw new InvalidOperationException("Drift verification needs an idle, unparked equatorial mount tracking at sidereal rate. Do not move the mount during a recording.");
        if (DriftGuider?.GetDevice() is IGuider guider && guider.Connected
            && (guider.ShiftEnabled || guider.State is not ("Stopped" or "Looping" or "Selected" or "Idle")))
            throw new InvalidOperationException("Stop guiding, calibration, dithering and shift tracking before drift verification.");
    }
    public DriftSample GetDriftSample(SolvedPolarFrame frame) {
        var site = profiles.ActiveProfile.AstrometrySettings;
        double dut1;
        try { dut1 = frame.Dut1Seconds ?? AstroUtil.DeltaUT(frame.CapturedAtUtc.UtcDateTime); }
        catch { dut1 = 0; }
        if (!double.IsFinite(dut1)) dut1 = 0;
        double az = 0, zd = 0, ha = 0, dec = 0, ra = 0, origins = 0;
        const double radians = Math.PI / 180;
        SOFA.CelestialToTopocentric(frame.RightAscensionDegrees * radians, frame.DeclinationDegrees * radians,
            0, 0, 0, 0, AstroUtil.GetJulianDate(frame.CapturedAtUtc.UtcDateTime), 0, dut1,
            site.Longitude * radians, site.Latitude * radians, 0, 0, 0, 0, 0, 0, 0,
            ref az, ref zd, ref ha, ref dec, ref ra, ref origins);
        return new(frame.CapturedAtUtc, dec / radians, RaRotation.WrapDegrees(ha / radians), 90 - zd / radians, frame.PixelScaleArcseconds);
    }
}
