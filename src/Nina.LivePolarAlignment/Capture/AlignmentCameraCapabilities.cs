using System;
using NINA.Equipment.Equipment.MyCamera;

namespace Nina.LivePolarAlignment.Capture;

public sealed record AlignmentCameraCapabilities(string CameraId,bool IsPoleMaster,bool PatternSupported,
    bool SupportsVideo,bool HasOptics,double? FieldWidthDegrees,double? FieldHeightDegrees);

public sealed partial class NinaPolarAlignmentCaptureService {
    private bool IsPoleMasterDevice=>camera.GetInfo()?.DeviceId?.StartsWith("POLEMASTER",StringComparison.OrdinalIgnoreCase)==true;
    public AlignmentCameraCapabilities? AlignmentCamera {
        get {
            var info=camera.GetInfo();if(info?.Connected!=true)return null;
            bool poleMaster=IsPoleMasterDevice;
            double focal=poleMaster?PoleMasterSetup.FocalLengthMillimeters:profiles.ActiveProfile.TelescopeSettings.FocalLength;
            double pixels=poleMaster?PoleMasterSetup.PixelSizeMicrons:profiles.ActiveProfile.CameraSettings.PixelSize;
            bool optics=double.IsFinite(focal)&&focal>0&&double.IsFinite(pixels)&&pixels>0;
            double? Fov(int size)=>optics&&size>0?2*Math.Atan(size*pixels/1000/(2*focal))*180/Math.PI:null;
            return new(info.DeviceId??string.Empty,poleMaster,poleMaster&&camera.GetDevice() is QHYCamera&&info.XSize==1280&&info.YSize==960,
                SupportsStreaming,optics,Fov(info.XSize),Fov(info.YSize));
        }
    }
}
