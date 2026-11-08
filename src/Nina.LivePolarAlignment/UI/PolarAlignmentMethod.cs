using System;

namespace Nina.LivePolarAlignment.UI;

public sealed partial class PolarAlignmentDockableVM {
    private string? automaticallySelectedCamera;
    private void RefreshCameraMethod() {
        var camera=captureService.AlignmentCamera;
        if(camera==null){automaticallySelectedCamera=null;return;}
        if(liveBusy || fieldSaving || camera.CameraId==automaticallySelectedCamera)return;
        // Default once per connected camera; a user's subsequent choice stays put.
        automaticallySelectedCamera=camera.CameraId;
        PoleMasterMode=camera.IsPoleMaster;
    }
    public bool CanUseSelectedMethod {
        get {
            var camera=captureService.AlignmentCamera;
            return camera==null || (PoleMasterMode?camera.PatternSupported&&camera.SupportsVideo:camera.HasOptics);
        }
    }
    public string CameraFieldOfView {
        get {
            var c=captureService.AlignmentCamera;
            return c?.FieldWidthDegrees is {} w && c.FieldHeightDegrees is {} h?UiText.Format("CameraFieldOfView",w,h):string.Empty;
        }
    }
    public string MethodCompatibilityWarning {
        get {
            var c=captureService.AlignmentCamera;if(c==null)return string.Empty;
            if(PoleMasterMode&&!c.PatternSupported)return UiText.Get("MethodPatternUnsupported")+" "+CameraFieldOfView;
            if(PoleMasterMode&&!c.SupportsVideo)return UiText.Get("MethodPatternNeedsVideo");
            if(!PoleMasterMode&&!c.HasOptics)return UiText.Get("MethodOpticsRequired");
            if(!PoleMasterMode&&!c.SupportsVideo)return UiText.Get("MethodSnapshotFallback");
            return string.Empty;
        }
    }
    public bool HasMethodWarning=>MethodCompatibilityWarning.Length>0;
}
