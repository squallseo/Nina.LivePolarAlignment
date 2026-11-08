using System;
using CommunityToolkit.Mvvm.Input;
using System.Threading;
using System.Threading.Tasks;

namespace Nina.LivePolarAlignment.UI;

public sealed partial class PolarAlignmentDockableVM {
    private bool autoRaRotation = true, reverseRaRotation, rotationInProgress, automaticRotationCompleted;
    private double rotationProgress, rotationActualDegrees;
    private int rotationAngleDegrees = 90;
    private bool returningToStart;
    public int RotationAngleDegrees {
        get => rotationAngleDegrees;
        set {
            if (PoleMasterMode || !(CanConfigureRotation || CanConfigureVerification)) return;
            rotationAngleDegrees = (int)Math.Clamp(Math.Round(value / 10d) * 10, 60, 90);
            NotifyRotation(); NotifyVerification(); NotifyWorkflow();
        }
    }
    private RelayCommand? decreaseRotationAngleCommand, increaseRotationAngleCommand;
    public RelayCommand DecreaseRotationAngleCommand => decreaseRotationAngleCommand ??= new(() => RotationAngleDegrees -= 10);
    public RelayCommand IncreaseRotationAngleCommand => increaseRotationAngleCommand ??= new(() => RotationAngleDegrees += 10);
    public bool AutoRaRotation {
        get => autoRaRotation;
        set { if (!CanConfigureRotation || PoleMasterMode) return; autoRaRotation = value; NotifyRotation(); NotifyWorkflow(); }
    }
    public bool ReverseRaRotation {
        get => reverseRaRotation;
        set { if (!CanConfigureRotation) return; reverseRaRotation = value; NotifyRotation(); }
    }
    public bool CanConfigureRotation => !IndependentDriftEnabled && !liveBusy && calibrationSamples < 2 && !automaticRotationCompleted;
    public bool CanToggleAutomaticRotation => CanConfigureRotation && !PoleMasterMode;
    public bool CanConfigureAutoRotation => AutoRaRotation && CanConfigureRotation;
    public bool ShowRotationSettings => AutoRaRotation && !IsAdjustmentStep;
    public bool CanUseAutoRotation => CanConfigureRotation && captureService.CanPrepareAutoRaRotation;
    public bool IsRotating => rotationInProgress;
    public string RotationStatus => PoleMasterMode && rotationInProgress && !returningToStart ? UiText.Format("PoleMasterRotating", poleMasterStage, rotationProgress) : rotationInProgress ? UiText.Format(returningToStart ? "AutoRaReturnProgress" : "AutoRaProgress", rotationProgress, RotationAngleDegrees)
        : captureService.AutoRaAvailability == global::Nina.LivePolarAlignment.Capture.AutoRaAvailability.Parked && captureService.CanPrepareAutoRaRotation ? UiText.Get("AutoRaUnparkHint") : UiText.Get(captureService.AutoRaAvailability == global::Nina.LivePolarAlignment.Capture.AutoRaAvailability.Ready ? "AutoRaHint" : "AutoRa" + captureService.AutoRaAvailability);

    private async Task RotateForReferenceAsync(CancellationToken token,
        Func<IProgress<double>, CancellationToken, Task>? captureAndRotate = null) {
        if (automaticRotationCompleted) return;
        if (!captureService.SupportsAutoRaRotation)
            throw new InvalidOperationException(UiText.Get("AutoRaCheckPath"));
        rotationInProgress = captureAndRotate == null; rotationProgress = 0; rotationActualDegrees = 0; NotifyLive();
        bool acceptingProgress = true, motionStarted = false;
        try {
            var progress = new Progress<double>(angle => {
                if (!acceptingProgress || token.IsCancellationRequested) return;
                if (!motionStarted) {
                    motionStarted = true;
                    RecordFieldEvent("ra_rotation_start", new { targetDegrees = RotationAngleDegrees, reverse = ReverseRaRotation });
                }
                rotationInProgress = true; rotationActualDegrees = angle; rotationProgress = Math.Min(angle, RotationAngleDegrees);
                NotifyLive();
            });
            if (captureAndRotate == null)
                await captureService.RotateRaAsync(ReverseRaRotation, progress, token, RotationAngleDegrees);
            else await captureAndRotate(progress, token);
            token.ThrowIfCancellationRequested();
            ValidateSite(); automaticRotationCompleted = true;
            RecordFieldEvent("ra_rotation_complete", new { reportedDegrees = rotationActualDegrees, targetDegrees = RotationAngleDegrees });
        } catch {
            // A partial rotation must never be retried against an old reference automatically.
            session = null; calibrationSamples = 0; guidance = null;
            automaticRotationCompleted = false; captureService.ResetRaReturn();
            EndFieldSession("ra_rotation_failed_or_cancelled");
            throw;
        } finally {
            acceptingProgress = false;
            rotationInProgress = false; NotifyLive();
        }
    }
    private async Task ReturnForAdjustmentAsync(CancellationToken token) {
        returningToStart = true; rotationInProgress = true; rotationProgress = 0;
        guidance = null; latestError = null; measurementStale = true; NotifyLive();
        bool acceptingProgress = true;
        try {
            RecordFieldEvent("ra_return_start", new { });
            var progress = new Progress<double>(angle => {
                if (!acceptingProgress || token.IsCancellationRequested) return;
                rotationProgress = angle; NotifyLive();
            });
            if(PoleMasterMode)await captureService.ReturnRaWithPreviewAsync(StreamingExposureSeconds,progress,token);
            else await captureService.ReturnRaToStartAsync(progress,token);
            token.ThrowIfCancellationRequested(); ValidateSite();
            returningToStart = false; rotationInProgress = false;
            liveStatus = "AutoRaReturnSolving"; NotifyLive();
            var cycle = System.Diagnostics.Stopwatch.StartNew();
            var frame = await captureService.CaptureAndSolveAsync(ExposureSeconds, token);
            token.ThrowIfCancellationRequested(); ValidateSite();
            session!.RebaseAdjustment(frame);
            RecordFieldFrame(frame, "adjustment_reference", true);
            PublishMeasurement(frame, cycle.Elapsed.TotalMilliseconds);
            RecordFieldEvent("ra_return_complete", new { });
        } catch {
            session = null; calibrationSamples = 0; automaticRotationCompleted = false;
            guidance = null; latestError = null; measurementStale = true;
            captureService.ResetRaReturn(); EndFieldSession("ra_return_failed_or_cancelled");
            throw;
        } finally {
            acceptingProgress = false; returningToStart = false; rotationInProgress = false; NotifyLive();
        }
    }
    private void NotifyRotation() {
        foreach (var name in new[] { nameof(RotationAngleDegrees), nameof(AutoRaRotation), nameof(ShowRotationSettings), nameof(ReverseRaRotation),
            nameof(CanConfigureAutoRotation), nameof(CanConfigureRotation), nameof(CanUseAutoRotation), nameof(IsRotating), nameof(RotationStatus) }) RaisePropertyChanged(name);
    }
}
