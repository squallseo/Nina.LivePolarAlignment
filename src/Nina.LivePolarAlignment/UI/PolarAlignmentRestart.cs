using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Nina.LivePolarAlignment.Capture;
namespace Nina.LivePolarAlignment.UI;
public sealed partial class PolarAlignmentDockableVM {
    private bool poleMasterNeedsHome,homingForRestart;
    private bool awaitingInitialHomeStatus;
    private void RefreshInitialHomeStatus() {
        if(!awaitingInitialHomeStatus || !PoleMasterMode || liveBusy || !captureService.MountAtHome)return;
        awaitingInitialHomeStatus=false;poleMasterNeedsHome=false;NotifyWorkflow();
    }
    private AsyncRelayCommand? homeRestartCommand;
    public bool IsHomingForRestart=>homingForRestart;
    public bool ShowLegacyReset=>!PoleMasterMode;
    public bool ShowOptionalHome=>PoleMasterMode;
    public string FriendlyOperationError=>OperationError.Contains("Mount state, Dec or pier side changed")?UiText.Get("RestartCoordinateError")
        : OperationError.Contains("Calibration is more than 15 degrees")?UiText.Get("RestartAxisFar"):OperationError;
    public bool PolarisSelectionReady=>!PoleMasterMode || IsAdjustmentStep || !polarisClickRequired || polarisCandidate!=null;
    public AsyncRelayCommand HomeRestartCommand=>homeRestartCommand??=new(HomeRestartAsync,
        ()=>!homingForRestart && !IsStopping && !IsRotating && (!liveBusy || IsAdjustmentStep && !verificationInProgress && !driftInProgress)
            && !fieldSaving && MountConnected && captureService.CanHomeForRestart);
    public string RestartPlan=>UiText.Get(homingForRestart?"RestartHomingPlan":poleMasterNeedsHome && MountConnected && !captureService.CanHomeForRestart?"RestartHomeUnsupported":poleMasterNeedsHome?"RestartHomePlan":PolarisSelectionReady?"RestartReadyPlan":"RestartSelectPlan");
    public string RestartStepText=>UiText.Get(homingForRestart?"RestartStepHoming":poleMasterNeedsHome?"RestartStepHome":PolarisSelectionReady?"RestartStepReady":"RestartStepSelect");
    private async Task HomeRestartAsync() {
        if(liveBusy) {
            var completed=liveOperationFinished?.Task;
            RequestStopLive();
            if(completed!=null)await completed;
            if(liveBusy || HasOperationError)return;
        }
        await WithLiveOperation(async token=>{
            homingForRestart=true;NotifyLive();
            try {await captureService.HomeForRestartAsync(token);ResetLive();poleMasterNeedsHome=false;awaitingInitialHomeStatus=false;}
            finally {homingForRestart=false;}
        });
        if(poleMasterPreviewFailure is not StreamingCleanupException){previewAttempted=false;previewManuallyPaused=false;UpdatePoleMasterPreview();}NotifyLive();
    }
    private void NotifyRestart() {
        RaisePropertyChanged(nameof(RestartPlan));RaisePropertyChanged(nameof(PolarisSelectionReady));RaisePropertyChanged(nameof(IsHomingForRestart));
        RaisePropertyChanged(nameof(RestartStepText));
        RaisePropertyChanged(nameof(FriendlyOperationError));
        RaisePropertyChanged(nameof(ShowLegacyReset));RaisePropertyChanged(nameof(ShowOptionalHome));
        homeRestartCommand?.NotifyCanExecuteChanged();
    }
}
