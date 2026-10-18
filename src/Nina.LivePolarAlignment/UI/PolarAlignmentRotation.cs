using System;
using CommunityToolkit.Mvvm.Input;
using System.Threading;
using System.Threading.Tasks;

namespace Nina.LivePolarAlignment.UI;

public sealed partial class PolarAlignmentDockableVM {
    private bool autoRaRotation = true, reverseRaRotation = true, rotationInProgress, automaticRotationCompleted;
    private double rotationProgress;
    private int rotationAngleDegrees = 90;
    public int RotationAngleDegrees {
        get => rotationAngleDegrees;
        set {
            if (!(CanConfigureRotation || CanConfigureVerification)) return;
            rotationAngleDegrees = (int)Math.Clamp(Math.Round(value / 10d) * 10, 60, 90);
            NotifyRotation(); NotifyVerification(); NotifyWorkflow();
        }
    }
    private RelayCommand? decreaseRotationAngleCommand, increaseRotationAngleCommand;
    public RelayCommand DecreaseRotationAngleCommand => decreaseRotationAngleCommand ??= new(() => RotationAngleDegrees -= 10);
    public RelayCommand IncreaseRotationAngleCommand => increaseRotationAngleCommand ??= new(() => RotationAngleDegrees += 10);
    public bool AutoRaRotation {
        get => autoRaRotation;
        set { if (!CanConfigureRotation) return; autoRaRotation = value; NotifyRotation(); NotifyWorkflow(); }
    }
    public bool ReverseRaRotation {
        get => reverseRaRotation;
        set { if (!CanConfigureRotation) return; reverseRaRotation = value; NotifyRotation(); }
    }
    public bool CanConfigureRotation => !IndependentDriftEnabled && !liveBusy && calibrationSamples < 2 && !automaticRotationCompleted;
    public bool CanConfigureAutoRotation => AutoRaRotation && CanConfigureRotation;
    public bool CanUseAutoRotation => CanConfigureRotation;
    public bool IsRotating => rotationInProgress;
    public string RotationStatus => rotationInProgress ? UiText.Format("AutoRaProgress", rotationProgress, RotationAngleDegrees)
        : UiText.Get(captureService.SupportsAutoRaRotation ? "AutoRaHint" : "AutoRaUnsupported");

    private async Task RotateForReferenceAsync(CancellationToken token) {
        if (automaticRotationCompleted) return;
        if (!captureService.SupportsAutoRaRotation)
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
            rotationInProgress = false; NotifyLive();
        }
    }
    private void NotifyRotation() {
        foreach (var name in new[] { nameof(RotationAngleDegrees), nameof(AutoRaRotation), nameof(ReverseRaRotation),
            nameof(CanConfigureAutoRotation), nameof(CanConfigureRotation), nameof(CanUseAutoRotation), nameof(IsRotating), nameof(RotationStatus) }) RaisePropertyChanged(name);
    }
}
