using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Nina.LivePolarAlignment.Core;

namespace Nina.LivePolarAlignment.UI;

public sealed partial class PolarAlignmentDockableVM {
    private bool independentDriftEnabled, driftConfirmed, driftInProgress;
    private DriftFit? firstDrift;
    private DriftResult? independentResult;
    private object? driftMount, driftProfile;
    private double driftLatitude, driftLongitude, validationTarget = 60, driftElapsed, driftDuration = 180;
    public double DriftDurationSeconds {
        get => driftDuration;
        set { if (CanConfigureDrift && double.IsFinite(value) && value is >= 120 and <= 540) { driftDuration = value; NotifyDrift(); } }
    }
    private string? driftFailure;
    private int driftRecordedFrames;
    public bool IsPolarView => !independentDriftEnabled;
    public bool ShowAlignmentAssessment => independentDriftEnabled || independentResult != null || verificationResult != null || stabilityResult != null;
    public ICommand IndependentDriftCommand { get; private set; } = null!;
    public ICommand ResetIndependentDriftCommand { get; private set; } = null!;
    public bool CanConfigureDrift => !liveBusy && !fieldSaving;
    public bool IndependentDriftEnabled {
        get => independentDriftEnabled;
        set {
            if (!CanConfigureDrift || value == independentDriftEnabled) return;
            independentDriftEnabled = value; livePreview = null; guidance = null; measurementStale = true;
            ResetDriftEvidence(); NotifyLive();
        }
    }
    public bool DriftConfirmed { get => driftConfirmed; set { if (CanConfigureDrift) { driftConfirmed = value; NotifyDrift(); } } }
    public double ValidationTargetArcseconds {
        get => validationTarget;
        set { if (CanConfigureDrift && double.IsFinite(value) && value is >= 15 and <= 600) { validationTarget = value; NotifyDrift(); NotifyWorkflow(); } }
    }
    public bool HasIndependentResult => independentResult != null;
    public string DriftActionLabel => UiText.Get(independentResult != null ? "DriftFinished" : firstDrift == null ? "DriftFirst" : "DriftSecond");
    public string DriftInstruction => UiText.Get(independentResult != null ? "DriftCompletedHint" : firstDrift == null ? "DriftFirstHint" : "DriftSecondHint");
    public string IndependentDriftStatus => driftInProgress ? UiText.Format("DriftProgress", driftElapsed, driftDuration)
        : driftFailure != null ? UiText.Format("DriftInconclusive", driftFailure)
        : independentResult is { } result ? UiText.Format("DriftResult", result.TotalArcseconds, result.LowerArcseconds, result.UpperArcseconds)
        : firstDrift is { } first ? UiText.Format("DriftFirstDone", first.SlopeArcsecondsPerSecond * 60) : UiText.Get("DriftReady");
    private ValidationVerdict CurrentAssessment => AlignmentAssessment.Evaluate(validationTarget,
        verificationResult?.Remeasured.TotalArcminutes * 60,
        stabilityResult is { } scatter ? Math.Sqrt(scatter.SigmaAltitudeArcseconds * scatter.SigmaAltitudeArcseconds
            + scatter.SigmaAzimuthArcseconds * scatter.SigmaAzimuthArcseconds) : null, independentResult);
    public string AlignmentAssessmentText => UiText.Format("Assessment" + (driftFailure != null ? ValidationVerdict.Inconclusive : CurrentAssessment), validationTarget);
    public bool HasValidationWarning => driftFailure != null || CurrentAssessment is ValidationVerdict.Realign or ValidationVerdict.Inconsistent or ValidationVerdict.Inconclusive;
    private void InitializeDrift() {
        IndependentDriftCommand = new AsyncRelayCommand(RunIndependentDriftAsync,
            () => CanConfigureDrift && independentDriftEnabled && driftConfirmed && independentResult == null);
        ResetIndependentDriftCommand = new RelayCommand(ResetDriftEvidence, () => CanConfigureDrift);
    }
    private void ResetDriftEvidence() {
        if (driftMount != null) EndFieldSession("drift_evidence_reset");
        firstDrift = null; independentResult = null; driftFailure = null; driftConfirmed = false;
        driftMount = null; driftProfile = null; driftElapsed = 0; NotifyDrift();
    }
    private void ValidateDriftContext() {
        if (!ReferenceEquals(driftProfile, profiles.ActiveProfile) || !ReferenceEquals(driftMount, captureService.DriftMountIdentity)
            || profiles.ActiveProfile.AstrometrySettings.Latitude != driftLatitude
            || profiles.ActiveProfile.AstrometrySettings.Longitude != driftLongitude)
            throw new InvalidOperationException("Mount, profile or observing site changed. Start both drift fields again.");
        captureService.ValidateDriftTracking();
    }
    private async Task RunIndependentDriftAsync() {
        await WithLiveOperation(async token => {
            if (!independentDriftEnabled || !driftConfirmed || independentResult != null || fieldSaving)
                throw new InvalidOperationException(UiText.Get("DriftConfirm"));
            bool meridian = firstDrift == null;
            driftInProgress = true; driftFailure = null; driftElapsed = 0;
            try {
                captureService.ValidateDriftTracking();
                if (meridian) {
                    driftMount = captureService.DriftMountIdentity ?? throw new InvalidOperationException("Connect a telescope first.");
                    driftProfile = profiles.ActiveProfile;
                    driftLatitude = profiles.ActiveProfile.AstrometrySettings.Latitude;
                    driftLongitude = profiles.ActiveProfile.AstrometrySettings.Longitude;
                    if (!double.IsFinite(driftLatitude) || Math.Abs(driftLatitude) is < 1 or >= 89.5)
                        throw new InvalidOperationException("Set a supported observing latitude first.");
                    EndFieldSession("independent_drift_started");
                    // Equatorial test pointings invalidate the polar-field capture basis.
                    session = null; calibrationSamples = 0; latestError = null; guidance = null; measuredAt = null;
                    automaticRotationCompleted = false; EnsureFieldSession();
                    driftRecordedFrames = 0;
                }
                ValidateDriftContext(); NotifyLive();
                RecordFieldEvent("drift_start", new { meridian, requestedSeconds = driftDuration, targetArcseconds = validationTarget });
                var samples = new List<DriftSample>();
                Capture.SolvedPolarFrame? firstFrame = null;
                Capture.SolvedPolarFrame? last = null;
                while (samples.Count < 20 || driftElapsed < driftDuration) {
                    token.ThrowIfCancellationRequested(); ValidateDriftContext();
                    var frame = await captureService.CaptureAndSolveAsync(ExposureSeconds, token);
                    token.ThrowIfCancellationRequested(); ValidateDriftContext();
                    var point = captureService.GetDriftSample(frame);
                    firstFrame ??= frame;
                    if (frame.Width != firstFrame.Width || frame.Height != firstFrame.Height || frame.Flipped != firstFrame.Flipped)
                        throw new InvalidOperationException("Camera configuration changed during drift recording.");
                    if (!captureService.IsSynthetic && Math.Abs((DateTimeOffset.UtcNow - point.Time).TotalSeconds) > 60)
                        throw new InvalidOperationException("Drift exposure timestamp is stale or the PC clock changed.");
                    DriftValidation.ValidateField(point, meridian);
                    if (samples.Count > 0) {
                        double gap = (point.Time - samples[^1].Time).TotalSeconds;
                        if (gap < 1 || gap > 30) throw new InvalidOperationException("Missing, duplicate or slow drift exposure. Repeat this check.");
                    }
                    samples.Add(point); last = frame;
                    driftElapsed = (point.Time - samples[0].Time).TotalSeconds;
                    if (driftElapsed > 600) throw new TimeoutException("Drift recording exceeded ten minutes.");
                    livePreview = frame.Preview;
                    RecordFieldEvent("drift_sample", new { meridian, point, solution = new FieldSolution(frame.RightAscensionDegrees,
                        frame.DeclinationDegrees, frame.PositionAngleDegrees, frame.PixelScaleArcseconds, frame.Width, frame.Height,
                        frame.Flipped, frame.CapturedAtUtc, frame.Dut1Seconds) });
                    if (fieldJournal != null && fieldFailure == null) driftRecordedFrames++;
                    if (samples.Count == 1) await SaveDriftEvidenceAsync(frame, meridian ? "drift_first_start" : "drift_second_start");
                    NotifyLive();
                    if (!captureService.IsSynthetic) await Task.Delay(TimeSpan.FromSeconds(3), token);
                }
                var fit = DriftValidation.Fit(samples, meridian);
                if (last != null) await SaveDriftEvidenceAsync(last, meridian ? "drift_first_end" : "drift_second_end");
                RecordFieldEvent("drift_fit", new { meridian, fit });
                if (meridian) firstDrift = fit;
                else {
                    independentResult = DriftValidation.Combine(firstDrift!, fit, driftLatitude);
                    RecordFieldEvent("drift_result", new { result = independentResult, verdict = CurrentAssessment.ToString(), targetArcseconds = validationTarget });
                }
                liveStatus = "DriftPhaseComplete";
            } catch (Exception error) {
                firstDrift = null; independentResult = null; driftFailure = error is OperationCanceledException ? UiText.Get("DriftCancelled") : error.Message;
                RecordFieldEvent("drift_inconclusive", new { reason = driftFailure });
                EndFieldSession("drift_failed_or_cancelled"); throw;
            } finally {
                driftInProgress = false; driftConfirmed = false; NotifyLive();
            }
        });
    }
    private async Task SaveDriftEvidenceAsync(Capture.SolvedPolarFrame frame, string kind) {
        if (fieldJournal == null || fieldFailure != null) return;
        try { await SaveFieldImagesAsync(fieldJournal, frame, 0, kind, FieldNote); }
        catch (Exception error) { fieldFailure = error.Message; NotifyField(); }
    }
    private void NotifyDrift() {
        foreach (var name in new[] { nameof(IsPolarView), nameof(ShowAlignmentAssessment), nameof(CanConfigureDrift), nameof(IndependentDriftEnabled), nameof(DriftConfirmed), nameof(ValidationTargetArcseconds),
            nameof(HasIndependentResult), nameof(DriftActionLabel), nameof(DriftInstruction), nameof(DriftDurationSeconds), nameof(IndependentDriftStatus), nameof(AlignmentAssessmentText), nameof(HasValidationWarning) }) RaisePropertyChanged(name);
        (IndependentDriftCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (ResetIndependentDriftCommand as IRelayCommand)?.NotifyCanExecuteChanged();
    }
}
