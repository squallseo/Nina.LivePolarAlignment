using System;

namespace Nina.LivePolarAlignment.UI;

public sealed partial class PolarAlignmentDockableVM {
    public bool CanShowAdjustmentAdvice => liveBusy && !IsStopping && !HasOperationError && !IsRotating && !verificationInProgress
        && !driftInProgress && session?.IsCalibrated == true && latestError != null && !IsMeasurementStale && CameraConnected
        && HasUsableMarkerGeometry && !NeedsCalibrationReview;
    private bool HasUsableMarkerGeometry => guidance is { Width: > 0, Height: > 0, MountAxis: { } a, CelestialPole: { } p }
        && double.IsFinite(a.X) && double.IsFinite(a.Y) && double.IsFinite(p.X) && double.IsFinite(p.Y);
    public bool NeedsCalibrationReview => latestError is { } error && (guidance != null && !HasUsableMarkerGeometry
        || !double.IsFinite(error.TotalArcminutes) || !double.IsFinite(error.AltitudeArcminutes)
        || !double.IsFinite(error.AzimuthArcminutes) || error.TotalArcminutes < 0 || error.TotalArcminutes > 300);
    public bool HasOffFrameMarkers => HasUsableMarkerGeometry && guidance is { MountAxis: { } a, CelestialPole: { } p }
        && (a.X < 0 || a.Y < 0 || a.X >= guidance.Width || a.Y >= guidance.Height
            || p.X < 0 || p.Y < 0 || p.X >= guidance.Width || p.Y >= guidance.Height);

    public string OperatorState => IsStopping ? "Stopping" : homingForRestart ? "HomingRestart" : PoleMasterMode && polarisClickRequired && polarisCandidate==null && !liveBusy ? "SelectPolaris" : HasOperationError ? "Failed"
        : returningToStart ? "Returning" : IsRotating ? "Rotating"
        : verificationInProgress ? "Verifying" : IndependentDriftEnabled ? "Drift"
        : !CameraConnected ? "ConnectCamera"
        : !CanUseSelectedMethod ? "MethodUnavailable"
        : PoleMasterMode && poleMasterNeedsHome && !liveBusy ? "NeedHomeRestart"
        : PoleMasterMode && poleMasterPreviewFailure != null && !liveBusy ? "PreviewFailed"
        : PoleMasterMode && !PolarisSelectionReady && !liveBusy ? "SelectPolaris"
        : PoleMasterMode && poleMasterPreviewing && !liveBusy ? "PolePreview"
        : AutoRaRotation && WorkflowStep < 2 && !MountConnected ? "ConnectMount"
        : !liveBusy && HasLiveMeasurement ? "Paused"
        : liveBusy && UsePolarisTarget && guidance?.PolarisTarget!=null ? "PolarisMove"
        : liveBusy && trackingRecovery ? "Recovering"
        : liveBusy && latestError != null && IsMeasurementStale ? "Stale"
        : liveBusy && NeedsCalibrationReview ? "Recalibrate"
        : CanShowAdjustmentAdvice && UsePolarisTarget ? "PolarisMove"
        : CanShowAdjustmentAdvice && HasOffFrameMarkers ? "Outside"
        : CanShowAdjustmentAdvice && latestError!.Value.TotalArcminutes > 30 ? "Far"
        : CanShowAdjustmentAdvice ? AlignmentQualityLevel
        : liveBusy ? "Calibrating" : WaitingForManualRotation ? "Manual" : "Ready";
    public string OperatorStateTitle => UiText.Get(OperatorState=="PolarisMove" && guidance?.TargetIsPolaris==false ? "OperatorReferenceStarMove" : "Operator" + OperatorState);
    public string OperatorNextAction {
        get {
            if(!CanUseSelectedMethod)return MethodCompatibilityWarning;
            if(PoleMasterMode && !liveBusy && (poleMasterNeedsHome || !PolarisSelectionReady))return RestartPlan;
            if(homingForRestart)return RestartPlan;
            if(OperatorState=="PreviewFailed")return poleMasterPreviewFailure!.Message;
            if(PoleMasterMode && OperatorState=="Calibrating")return UiText.Format("PoleMasterPatternStep",Math.Min(3,poleMasterStage+1));
            if(OperatorState=="PolarisMove")return UiText.Get(guidance?.TargetIsPolaris==false?"OperatorNextReferenceStarMove":"OperatorNextPolarisMove");
            if(IsPoleMasterMonitoring && CanShowAdjustmentAdvice && !IsWithinTarget)return UiText.Get("MonitorLegend");
            if (HasOperationError && (OperationError.Contains("Plate solve", StringComparison.OrdinalIgnoreCase)
                || OperationError.Contains("plate solution", StringComparison.OrdinalIgnoreCase))) return UiText.Get("OperatorNextSolveFailed");
            if (!CanShowAdjustmentAdvice || IsWithinTarget) return UiText.Get("OperatorNext" + OperatorState);
            var error = latestError!.Value;
            // Compare tangent errors, since a degree of azimuth is not a degree of sky displacement.
            bool altitudeFirst = Math.Abs(error.AltitudeArcminutes) >= Math.Abs(error.AzimuthArcminutes * Math.Cos(calibrationLatitude * Math.PI / 180));
            var action = altitudeFirst ? error.AltitudeArcminutes > 0 ? "OperatorFirstLower" : "OperatorFirstRaise"
                : error.AzimuthArcminutes > 0 ? "OperatorFirstLeft" : "OperatorFirstRight";
            return UiText.Format(OperatorState == "Outside" ? "OperatorOutsideNext" : OperatorState == "Far" ? "OperatorFarNext" : "OperatorAdjustNext", UiText.Get(action));
        }
    }
    public string AdjustmentErrorReadout => latestError is { } error && double.IsFinite(error.TotalArcminutes) ? ArcsecondsText(error.TotalArcminutes * 60) : "—";
    public string AdjustmentGoalReadout => UiText.Format("OperatorGoal", ArcsecondsText(validationTarget));
    public string AltitudeAdjustmentAction => !CanShowAdjustmentAdvice ? UiText.Get("OperatorHold")
        : IsWithinTarget || Math.Abs(latestError!.Value.AltitudeArcminutes * 60) <= validationTarget / 2 ? UiText.Get("OperatorKeep")
        : UiText.Get(latestError.Value.AltitudeArcminutes > 0 ? "OperatorLower" : "OperatorRaise");
    public string AzimuthAdjustmentAction => !CanShowAdjustmentAdvice ? UiText.Get("OperatorHold")
        : IsWithinTarget || Math.Abs(latestError!.Value.AzimuthArcminutes * Math.Cos(calibrationLatitude * Math.PI / 180) * 60) <= validationTarget / 2 ? UiText.Get("OperatorKeep")
        : UiText.Get(latestError.Value.AzimuthArcminutes > 0 ? "OperatorLeft" : "OperatorRight");
    public string AltitudeRemaining => latestError is { } error ? UiText.Format(CanShowAdjustmentAdvice ? "OperatorRemaining" : "OperatorPrevious", ArcsecondsText(Math.Abs(error.AltitudeArcminutes * 60))) : "—";
    public string AzimuthRemaining => latestError is { } error ? UiText.Format(CanShowAdjustmentAdvice ? "OperatorRemaining" : "OperatorPrevious", ArcsecondsText(Math.Abs(error.AzimuthArcminutes * 60))) : "—";
    private static string ArcsecondsText(double value) {
        if (!double.IsFinite(value) || value < 0 || value > int.MaxValue) return "—";
        int seconds = (int)Math.Round(value);
        if (seconds >= 3600) return $"{seconds / 3600}° {seconds % 3600 / 60:00}′ {seconds % 60:00}″";
        return seconds < 60 ? $"{seconds}″" : $"{seconds / 60}′ {seconds % 60:00}″";
    }
    private void NotifyAdvice() {
        foreach (var name in new[] { nameof(CanShowAdjustmentAdvice), nameof(OperatorState), nameof(OperatorStateTitle),
            nameof(OperatorNextAction), nameof(NeedsCalibrationReview), nameof(HasOffFrameMarkers), nameof(AdjustmentErrorReadout), nameof(AdjustmentGoalReadout),
            nameof(AltitudeAdjustmentAction), nameof(AzimuthAdjustmentAction), nameof(AltitudeRemaining), nameof(AzimuthRemaining) }) RaisePropertyChanged(name);
    }
}
