using System;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Equipment.MyCamera;
namespace Nina.LivePolarAlignment.Capture;
public sealed partial class NinaPolarAlignmentCaptureService {
    private sealed class LiveControls(ICamera device,double exposure) {
        public readonly ICamera Device=device;
        public double Exposure=exposure;
        public bool ExposureDirty,ExposureLogged;
    }
    private readonly object controlsLock=new();
    private LiveControls? activeLiveControls;
    // The equipment panel owns gain. Ignore the retired plugin-specific gain keys.
    public int CameraGain => camera.GetInfo()?.Gain ?? -1;
    public bool TryQueueLiveExposure(double seconds) {
        lock(controlsLock) {
            if(activeLiveControls is not {Device:QHYCamera {Connected:true}} owner)return false;
            owner.Exposure=seconds;owner.ExposureDirty=true;return true;
        }
    }
    private void ApplyLiveControls(LiveControls? owner,ref int gain,ref double exposure,bool liveFrame=true) {
        if(owner==null)return;
        lock(controlsLock) {
            if(!owner.ExposureDirty && owner.ExposureLogged)return;
            if(!ReferenceEquals(owner,activeLiveControls)||!ReferenceEquals(owner.Device,camera.GetDevice())||!owner.Device.Connected)return;
            if(owner.ExposureDirty && owner.Device is QHYCamera qhy) {
                if(!qhy.Sdk.SetControlValue(QHYCCD.QhySdk.CONTROL_ID.CONTROL_EXPOSURE,owner.Exposure*1e6))throw new InvalidOperationException("Camera rejected live exposure.");
                exposure=owner.Exposure;owner.ExposureDirty=false;owner.ExposureLogged=false;
            }
            if(liveFrame && !owner.ExposureLogged && owner.Device is QHYCamera cameraDevice) {
                double sdkMicroseconds=cameraDevice.Sdk.GetControlValue(QHYCCD.QhySdk.CONTROL_ID.CONTROL_EXPOSURE);
                NINA.Core.Utility.Logger.Info($"Polar live exposure: requested={owner.Exposure*1000:G8} ms; sent={owner.Exposure*1e6:G8} us; SDK reported={sdkMicroseconds:G8} us.");
                owner.ExposureLogged=true;
            }
        }
    }
}
