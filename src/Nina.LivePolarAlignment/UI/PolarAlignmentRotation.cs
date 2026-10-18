using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nina.LivePolarAlignment.UI;

public sealed partial class PolarAlignmentDockableVM {
    private bool autoRaRotation = true, reverseRaRotation, rotationPathClear, rotationInProgress, automaticRotationCompleted;
    private double rotationProgress;
    private int rotationAngleDegrees = 90;
    public int[] RotationAngleChoices { get; } = { 90, 60 };
    public int RotationAngleDegrees {
        get => rotationAngleDegrees;
        set {
            if (!(CanConfigureRotation || CanConfigureVerification) || value is not (60 or 90)) return;
            rotationAngleDegrees = value; rotationPathClear = false; verificationPathClear = false;
            NotifyRotation(); NotifyVerification(); NotifyWorkflow();
        }
    }
    public string RotationPathLabel => UiText.Format("AutoRaClear", RotationAngleDegrees);
    public bool AutoRaRotation {
        get => autoRaRotation;
        set { if (!CanConfigureRotation) return; autoRaRotation = value; rotationPathClear = false; NotifyRotation(); NotifyWorkflow(); }
    }
    public bool ReverseRaRotation {
        get => reverseRaRotation;
        set { if (!CanConfigureRotation) return; reverseRaRotation = value; rotationPathClear = false; NotifyRotation(); }
    }
    public bool RotationPathClear {
        get => rotationPathClear;
        set { if (CanConfigureRotation) { rotationPathClear = value; RaisePropertyChanged(); } }
    }
    public bool CanConfigureRotation => !IndependentDriftEnabled && !liveBusy && calibrationSamples < 2 && !automaticRotationCompleted;
    public bool CanUseAutoRotation => CanConfigureRotation;
    public bool IsRotating => rotationInProgress;
    public string RotationStatus => rotationInProgress ? UiText.Format("AutoRaProgress", rotationProgress, RotationAngleDegrees)
        : UiText.Get(captureService.SupportsAutoRaRotation ? "AutoRaHint" : "AutoRaUnsupported");

    private async Task RotateForReferenceAsync(CancellationToken token) {
        if (automaticRotationCompleted) return;
        if (!RotationPathClear || !captureService.SupportsAutoRaRotation)
            throw new InvalidOperationException(UiText.Get("AutoRaCheckPath"));
        rotationInProgress = true; rotationProgress = 0; NotifyLive();
        RecordFieldEvent("ra_rotation_start", new { targetDegrees = RotationAngleDegrees, reverse = ReverseRaRotation });
        try {
            await captureService.RotateRaAsync(ReverseRaRotation, new Progress<double>(angle => {
                if (!rotationInProgress) return;
                rotationProgress = angle; RaisePropertyChanged(nameof(RotationStatus)); RaisePropertyChanged(nameof(LiveStatus)); RaisePropertyChanged(nameof(WorkflowDescription));
            }), token, RotationAngleDegrees);
            token.ThrowIfCancellationRequested();
            ValidateSite(); automaticRotationCompleted = true;
            RecordFieldEvent("ra_rotation_complete", new { reportedDegrees = rotationProgress });
        } catch {
            autoRaRotation = false;
            // A partial rotation must never be retried against an old reference automatically.
            session = null; calibrationSamples = 0; guidance = null;
            EndFieldSession("ra_rotation_failed_or_cancelled");
            throw;
        } finally {
            rotationInProgress = false; rotationPathClear = false; NotifyLive();
        }
    }
    private void NotifyRotation() {
        foreach (var name in new[] { nameof(RotationAngleDegrees), nameof(RotationPathLabel), nameof(AutoRaRotation), nameof(ReverseRaRotation), nameof(RotationPathClear),
            nameof(CanConfigureRotation), nameof(CanUseAutoRotation), nameof(IsRotating), nameof(RotationStatus) }) RaisePropertyChanged(name);
    }
}
