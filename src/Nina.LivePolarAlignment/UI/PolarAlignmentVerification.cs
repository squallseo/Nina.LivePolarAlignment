using System;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Nina.LivePolarAlignment.Core;

namespace Nina.LivePolarAlignment.UI;

public sealed record VerificationResult(DateTimeOffset CompletedAtUtc, PolarError Before, PolarError Remeasured) {
    public double AltitudeDifferenceArcseconds => (Remeasured.AltitudeArcminutes - Before.AltitudeArcminutes) * 60;
    public double AzimuthDifferenceArcseconds => (Remeasured.AzimuthArcminutes - Before.AzimuthArcminutes) * 60;
}

public sealed partial class PolarAlignmentDockableVM {
    private bool verificationInProgress, verificationReverse, autoVerificationStability = true;
    private VerificationResult? verificationResult;
    public ICommand VerifyAlignmentCommand { get; private set; } = null!;
    public bool CanConfigureVerification => !IndependentDriftEnabled && !liveBusy && !fieldSaving && session?.IsCalibrated == true;
    public bool ShowVerification => !IndependentDriftEnabled && (IsAdjustmentStep || verificationInProgress);
    public bool HasVerificationResult => verificationResult != null;
    public bool VerificationReverse { get => verificationReverse; set { if (CanConfigureVerification) { verificationReverse = value; NotifyVerification(); } } }
    public bool AutoVerificationStability { get => autoVerificationStability; set { if (!liveBusy) { autoVerificationStability = value; RaisePropertyChanged(); } } }
    public string VerificationSummary => verificationResult is { } result
        ? UiText.Format("VerificationResult", result.Before.TotalArcminutes, result.Remeasured.TotalArcminutes,
            result.AltitudeDifferenceArcseconds, result.AzimuthDifferenceArcseconds)
        : UiText.Get("VerificationHint");
    private void InitializeVerification() {
        VerifyAlignmentCommand = new AsyncRelayCommand(VerifyAlignmentAsync,
            () => CanConfigureVerification && captureService.SupportsAutoRaRotation);
    }
    private async Task VerifyAlignmentAsync() {
        await WithLiveOperation(async token => {
            if (fieldSaving || !captureService.SupportsAutoRaRotation || session?.IsCalibrated != true)
                throw new InvalidOperationException(UiText.Get("AutoRaCheckPath"));
            ValidateSite();
            var previousSession = session;
            var previousDirectory = fieldDirectory;
            var previousReverse = reverseRaRotation;
            verificationInProgress = true; verificationResult = null;
            AbortStability("verification started"); stabilityResult = null;
            EndFieldSession("verification_started");
            session = null; calibrationSamples = 0; automaticRotationCompleted = false;
            reverseRaRotation = verificationReverse;
            latestError = null; guidance = null; NotifyLive();
            try {
                var first = await CaptureReferenceFrameAsync(token);
                var before = previousSession.Measure(first);
                RecordFieldEvent("verification_start", new { previousDirectory, before, reverse = verificationReverse });
                await RotateForReferenceAsync(token);
                var second = await CaptureReferenceFrameAsync(token);
                PublishMeasurement(second, 0);
                verificationResult = new(DateTimeOffset.UtcNow, before, latestError!.Value);
                RecordFieldEvent("verification_result", verificationResult); NotifyVerification();
                if (AutoVerificationStability) {
                    StartStability();
                    for (int i = 0; i < 20; i++) {
                        token.ThrowIfCancellationRequested(); ValidateSite();
                        var cycle = System.Diagnostics.Stopwatch.StartNew();
                        var frame = await captureService.CaptureAndSolveAsync(ExposureSeconds, token);
                        token.ThrowIfCancellationRequested(); ValidateSite();
                        PublishMeasurement(frame, cycle.Elapsed.TotalMilliseconds);
                        if (stability == null && stabilityResult == null)
                            throw new InvalidOperationException("Stability collection was interrupted. Repeat the check with the mount fixed.");
                    }
                }
                liveStatus = "VerificationComplete";
            } catch {
                session = null; calibrationSamples = 0; latestError = null; guidance = null;
                verificationResult = null; EndFieldSession("verification_failed_or_cancelled"); throw;
            } finally {
                verificationInProgress = false;
                reverseRaRotation = previousReverse; NotifyLive();
            }
        });
    }
    private void NotifyVerification() {
        foreach (var name in new[] { nameof(CanConfigureVerification), nameof(ShowVerification), nameof(HasVerificationResult),
            nameof(VerificationReverse), nameof(AutoVerificationStability), nameof(VerificationSummary) }) RaisePropertyChanged(name);
        (VerifyAlignmentCommand as IRelayCommand)?.NotifyCanExecuteChanged();
    }
}
