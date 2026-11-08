using System;
using NINA.Astrometry;
using NINA.Equipment.Equipment.MyCamera;

namespace Nina.LivePolarAlignment.Capture;

public sealed partial class NinaPolarAlignmentCaptureService {
    private bool poleMasterMode;
    public bool PoleMasterMode {
        get => poleMasterMode;
        set {
            if (poleMasterMode == value) return;
            if (!captureGate.Wait(0)) throw new InvalidOperationException("Stop alignment before changing camera mode.");
            try { poleMasterMode = value; ResetSolveHint(); ResetRaReturn(); }
            finally { captureGate.Release(); }
        }
    }
    public void ValidateAlignmentCamera() {
        if (!PoleMasterMode) return;
        ValidatePoleMasterHardware();
        _ = PoleMasterSetup.PoleHint(profiles.ActiveProfile.AstrometrySettings.Latitude);
    }
    private void ValidatePoleMasterHardware() {
        if (!PoleMasterMode) return;
        var info = camera.GetInfo();
        PoleMasterSetup.ValidateCamera(info?.Connected == true && camera.GetDevice() is QHYCamera,
            info?.XSize ?? 0, info?.YSize ?? 0);
    }
    private (double FocalLength, double PixelSize) AlignmentOptics => PoleMasterMode || IsPoleMasterDevice
        ? (PoleMasterSetup.FocalLengthMillimeters, PoleMasterSetup.PixelSizeMicrons)
        : (profiles.ActiveProfile.TelescopeSettings.FocalLength, profiles.ActiveProfile.CameraSettings.PixelSize);
    private Coordinates? InitialAlignmentHint() => PoleMasterMode
        ? PoleMasterSetup.PoleHint(profiles.ActiveProfile.AstrometrySettings.Latitude)
        : telescope.GetInfo()?.Connected == true ? telescope.GetCurrentPosition() : null;
}
