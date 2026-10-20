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
    public bool CanUseAutoRotation => CanConfigureRotation && captureService.CanPrepareAutoRaRotation;
    public bool IsRotating => rotationInProgress;
    public string RotationStatus => rotationInProgress ? UiText.Format("AutoRaProgress", rotationProgress, RotationAngleDegrees)
        : captureService.AutoRaAvailability == global::Nina.LivePolarAlignment.Capture.AutoRaAvailability.Parked && captureService.CanPrepareAutoRaRotation ? UiText.Get("AutoRaUnparkHint") : UiText.Get(captureService.AutoRaAvailability == global::Nina.LivePolarAlignment.Capture.AutoRaAvailability.Ready ? "AutoRaHint" : "AutoRa" + captureService.AutoRaAvailability);

    private async Task RotateForReferenceAsync(CancellationToken token,
        Func<IProgress<double>, CancellationToken, Task>? captureAndRotate = null) {
        if (automaticRotationCompleted) return;
        if (!captureService.SupportsAutoRaRotation)
            throw new InvalidOperationException(UiText.Get("AutoRaCheckPath"));
        rotationInProgress = captureAndRotate == null; rotationProgress = 0; NotifyLive();
        bool acceptingProgress = true, motionStarted = false;
        try {
            var progress = new Progress<double>(angle => {
                if (!acceptingProgress || token.IsCancellationRequested) return;
                if (!motionStarted) {
                    motionStarted = true;
                    RecordFieldEvent("ra_rotation_start", new { targetDegrees = RotationAngleDegrees, reverse = ReverseRaRotation });
                }
                rotationInProgress = true; rotationProgress = angle;
                NotifyLive();
            });
            if (captureAndRotate == null)
                await captureService.RotateRaAsync(ReverseRaRotation, progress, token, RotationAngleDegrees);
            else await captureAndRotate(progress, token);
            token.ThrowIfCancellationRequested();
            ValidateSite(); automaticRotationCompleted = true;
            RecordFieldEvent("ra_rotation_complete", new { reportedDegrees = rotationProgress });
        } catch {
            // A partial rotation must never be retried against an old reference automatically.
            session = null; calibrationSamples = 0; guidance = null;
            EndFieldSession("ra_rotation_failed_or_cancelled");
            throw;
        } finally {
            acceptingProgress = false;
            rotationInProgress = false; NotifyLive();
        }
    }
    private void NotifyRotation() {
        foreach (var name in new[] { nameof(RotationAngleDegrees), nameof(AutoRaRotation), nameof(ReverseRaRotation),
            nameof(CanConfigureAutoRotation), nameof(CanConfigureRotation), nameof(CanUseAutoRotation), nameof(IsRotating), nameof(RotationStatus) }) RaisePropertyChanged(name);
    }
}
