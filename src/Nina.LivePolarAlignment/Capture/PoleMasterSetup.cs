using System;
using NINA.Astrometry;

namespace Nina.LivePolarAlignment.Capture;

/// <summary>Stock PoleMaster lens and sensor, independent of the main telescope profile.</summary>
public static class PoleMasterSetup {
    public const double FocalLengthMillimeters = 25;
    public const double PixelSizeMicrons = 3.75;
    public static Coordinates PoleHint(double latitude) {
        if (!double.IsFinite(latitude) || Math.Abs(latitude) < .01 || Math.Abs(latitude) > 90)
            throw new InvalidOperationException("Set the observing latitude before PoleMaster alignment.");
        return new(0, Math.CopySign(89.9, latitude), Epoch.J2000, Coordinates.RAType.Degrees);
    }
    public static void ValidateCamera(bool nativeQhy, int width, int height) {
        if (!nativeQhy || width != 1280 || height != 960)
            throw new InvalidOperationException(UI.UiText.Get("PoleMasterWrongCamera"));
    }
    public static void ValidateSolution(SolvedPolarFrame frame, double latitude) {
        // Reject a wrong lens, camera, hemisphere or a misplaced polar camera before motion.
        // The nominal full-frame scale is 30.94 arcsec/pixel. Stock mode always captures 1x.
        if (frame.Width != 1280 || frame.Height != 960 || !double.IsFinite(frame.PixelScaleArcseconds)
            || frame.PixelScaleArcseconds < 24 || frame.PixelScaleArcseconds > 40
            || !double.IsFinite(frame.DeclinationDegrees) || Math.Abs(frame.DeclinationDegrees) > 90
            || Math.Sign(frame.DeclinationDegrees) != Math.Sign(latitude) || Math.Abs(frame.DeclinationDegrees) < 82)
            throw new InvalidOperationException(UI.UiText.Get("PoleMasterWrongField"));
    }
}
