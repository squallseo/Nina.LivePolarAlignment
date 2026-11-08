using System;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
namespace Nina.LivePolarAlignment.UI;
public sealed partial class PolarAlignmentDockableVM {
    private RelayCommand? exposureUp,exposureDown;
    private double? notifiedExposure;
    private static readonly double[] ExposureSteps={.02,.05,.1,.2,.25,.5,.75,1,1.5,2,3,5,10};
    public string CaptureExposureLabel=>UiText.Get("CaptureExposureMs");
    public double CaptureExposureValue {get=>CaptureExposureSeconds*1000;set=>CaptureExposureSeconds=value/1000;}
    public double CaptureExposureSeconds {get=>UseStreaming?StreamingExposureSeconds:ExposureSeconds;set{if(!CanChangeCaptureControls || !double.IsFinite(value) || value is <.02 or >10)return;ExposureSeconds=value;StreamingExposureSeconds=value;if(PoleMasterMode && !captureService.IsSynthetic)profiles.ActiveProfile.PluginSettings?.SetValue(Plugin.SettingsId,"PoleMaster.Exposure",value);RaisePropertyChanged();RaisePropertyChanged(nameof(ExposureSeconds));}}
    public RelayCommand IncreaseExposureCommand=>exposureUp??=new(()=>StepExposure(1),()=>CanChangeCaptureControls);
    public RelayCommand DecreaseExposureCommand=>exposureDown??=new(()=>StepExposure(-1),()=>CanChangeCaptureControls);
    private void StepExposure(int direction) {
        var next=PoleMasterMode?Core.PoleMasterControls.NextExposure(CaptureExposureSeconds,direction)
            :direction>0?ExposureSteps.FirstOrDefault(x=>x>CaptureExposureSeconds+.0001,10):ExposureSteps.LastOrDefault(x=>x<CaptureExposureSeconds-.0001,.02);
        CaptureExposureSeconds=next;
        RaisePropertyChanged(nameof(CaptureExposureValue));
    }
    private bool poleMasterCaptureLocked;
    public bool CaptureSettingsLocked=>PoleMasterMode && poleMasterCaptureLocked;
    public string CaptureSettingsLockHint=>UiText.Get(CaptureSettingsLocked?"CaptureSettingsLocked":"CaptureSettingsBeforeStart");
    public bool CanChangeCaptureControls => CanConfigureLive && !CaptureSettingsLocked;
    private void NotifyCaptureControls() {
        // Refresh values only when they change. Live status updates must not
        // overwrite an unfinished edit before its LostFocus binding commits.
        double exposure=CaptureExposureValue;
        if(notifiedExposure!=exposure){notifiedExposure=exposure;RaisePropertyChanged(nameof(CaptureExposureValue));}
        RaisePropertyChanged(nameof(CaptureExposureLabel));
        RaisePropertyChanged(nameof(CaptureSettingsLocked));RaisePropertyChanged(nameof(CaptureSettingsLockHint));
        RaisePropertyChanged(nameof(CanChangeCaptureControls));
        exposureUp?.NotifyCanExecuteChanged();exposureDown?.NotifyCanExecuteChanged();
    }
}
