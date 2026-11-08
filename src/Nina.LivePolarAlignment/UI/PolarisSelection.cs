using System;
using Nina.LivePolarAlignment.Core;
namespace Nina.LivePolarAlignment.UI;
public sealed partial class PolarAlignmentDockableVM {
    private PixelPoint? polarisCandidate;
    private bool polarisClickRequired;
    public PixelPoint? PolarisCandidate => calibrationSamples==0?polarisCandidate:null;
    public string PolarisSelectionHelp => UiText.Get("SkyPatternHelp");
    public void SelectPolarisCandidate(double x,double y) {
        if(!CanConfigureLive || !polarisClickRequired || CaptureSettingsLocked || !PoleMasterMode || poleMasterNeedsHome || x is <0 or >1 || y is <0 or >1)return;
        operationError=null;
        polarisCandidate=new(x,y);captureService.PolarisCandidate=polarisCandidate;captureService.ResetSolveHint();RaisePropertyChanged(nameof(PolarisCandidate));NotifyWorkflow();
    }
}
