using System.Collections.Generic;
using System.Linq;

namespace Nina.LivePolarAlignment.UI;

public sealed record PolarWorkflowStage(string Label, string Marker, bool IsCurrent, bool IsCompleted);

public sealed partial class PolarAlignmentDockableVM {
    // Read the same state used by capture/return/monitoring; no separate UI timer
    // or clickable navigation can advance the equipment workflow.
    public int CurrentPoleMasterStage => IsAdjustmentStep
        ? returningToStart || liveStatus == "AutoRaReturnSolving" || latestError == null ? 3 : poleMasterMonitoring ? 5 : 4
        : System.Math.Clamp(poleMasterStage, 0, 3);
    public IReadOnlyList<PolarWorkflowStage> PoleMasterSteps => Enumerable.Range(0,6)
        .Select(i=>new PolarWorkflowStage(UiText.Get("ProgressStep"+i),
            i<CurrentPoleMasterStage ? "✓" : (i+1).ToString(), i==CurrentPoleMasterStage, i<CurrentPoleMasterStage)).ToArray();

    public int CurrentWorkflowStage {
        get {
            if(PoleMasterMode)return CurrentPoleMasterStage;
            int stage=returningToStart || liveStatus=="AutoRaReturnSolving" ? 3
                : IsAdjustmentStep ? AutoRaRotation && latestError==null ? 3 : poleMasterMonitoring?5:4
                : IsRotating || WaitingForManualRotation && liveStatus!="ReferenceSecondCapture" ? 1
                : calibrationSamples==1 && (automaticRotationCompleted || liveStatus=="ReferenceSecondCapture") ? 2
                : calibrationSamples==1 ? 1 : 0;
            return !AutoRaRotation && stage>3?stage-1:stage;
        }
    }
    public IReadOnlyList<PolarWorkflowStage> WorkflowStages {
        get {
            if(PoleMasterMode)return PoleMasterSteps;
            var keys=AutoRaRotation
                ?new[]{"GeneralStep0","GeneralStep1","GeneralStep2","ProgressStep3","ProgressStep4","ProgressStep5"}
                :new[]{"GeneralStep0","GeneralStepManualRa","GeneralStep2","ProgressStep4","ProgressStep5"};
            return keys.Select((key,i)=>new PolarWorkflowStage(UiText.Get(key),
                i<CurrentWorkflowStage?"✓":(i+1).ToString(),i==CurrentWorkflowStage,i<CurrentWorkflowStage)).ToArray();
        }
    }
}
