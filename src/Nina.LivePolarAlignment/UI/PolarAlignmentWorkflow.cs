using System;
using System.Windows.Input;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace Nina.LivePolarAlignment.UI;

public sealed partial class PolarAlignmentDockableVM {
    private int calibrationSamples;
    private double pixelScale;
    private string? operationError;
    private AsyncRelayCommand? workflowCommand;
    public bool CameraConnected => captureService.CameraConnected;
    public bool MountConnected => captureService.MountConnected;
    public bool EquipmentReady => CameraConnected && (IndependentDriftEnabled || WorkflowStep >= 2 || !AutoRaRotation || captureService.CanPrepareAutoRaRotation);
    public bool CameraDisconnected => !CameraConnected;
    public bool MountDisconnected => !MountConnected;
    public string NotConnectedText => UiText.Get("NotConnected");
    public string CameraConnectionHint => UiText.Get("CameraRequired");
    public string PrimaryActionHint => !CameraConnected ? CameraConnectionHint : !EquipmentReady ? RotationStatus : WorkflowHelp;
    public bool WaitingForManualRotation => !IndependentDriftEnabled && WorkflowStep == 1 && !AutoRaRotation && !liveBusy;
    public string ManualRotationInstruction => UiText.Get("ManualRotationRequired");

    public int WorkflowStep => calibrationSamples;
    public bool IsRotationStep => WorkflowStep == 1;
    public bool IsAdjustmentStep => WorkflowStep == 2;
    public bool IsWorking => liveBusy;
    public bool HasOperationError => operationError != null;
    public string OperationError => operationError ?? string.Empty;
    public bool HasLiveMeasurement => latestError != null;
    public bool HasPreview => livePreview != null;
    public bool HasAlignmentDetail => HasLiveMeasurement && HasCurrentFieldGuidance && HasPreview;
    public bool HasCurrentFieldGuidance => guidance != null && (calibrationSamples < 2 || !IsMeasurementStale);
    public Capture.PolarGuidance? FieldGuidance => guidance;
    public double PixelScaleArcseconds => pixelScale;
    public string PrimaryActionLabel => IndependentDriftEnabled ? (driftInProgress ? UiText.Get("DriftMeasuring") : DriftActionLabel) : IsRotating ? UiText.Get("AutoRaWorking") : liveBusy ? UiText.Get("Workflow0") : WorkflowStep switch {
        0 => UiText.Get("ActionStart"),
        1 => UiText.Get(AutoRaRotation && !automaticRotationCompleted ? "AutoRaSecond" : "Workflow2"),
        _ => UiText.Get("Workflow3")
    };
    public bool IsStopping => liveBusy && liveCancellation?.IsCancellationRequested == true;
    public string ActionButtonLabel => !liveBusy && !IndependentDriftEnabled && AutoRaRotation && HasOperationError ? UiText.Get("ActionRetry")
        : !liveBusy ? UiText.Get(IndependentDriftEnabled ? (firstDrift == null ? "ActionStart" : "ActionContinue")
        : WorkflowStep == 0 ? "ActionStart" : WorkflowStep == 1 ? "ActionContinue" : "ActionResume") : IsStopping ? UiText.Get("ActionStopping")
        : UiText.Get(IsRotating || WorkflowStep < 2 || driftInProgress ? "ActionCancel" : "ActionStop");
    public string ActionButtonHint => liveBusy ? UiText.Get("ActionStopHint") : PrimaryActionHint;
    public ICommand ActionButtonCommand => liveBusy ? StopLiveCommand : PrimaryActionCommand;
    public ICommand PrimaryActionCommand => IndependentDriftEnabled ? IndependentDriftCommand : workflowCommand ??= new AsyncRelayCommand(RunWorkflowAsync, () => EquipmentReady && !liveBusy);
    private async Task RunWorkflowAsync() {
        if (!EquipmentReady) return;
        if (IsAdjustmentStep) { await RunLiveAsync(); return; }
        await CaptureReferenceAsync();
        if (IsAdjustmentStep && !HasOperationError && liveStatus != "Live7" && liveStatus != "Live2" && EquipmentReady) await RunLiveAsync();
    }
    public string WorkflowTitle => IndependentDriftEnabled ? UiText.Get("DriftTitle") : IsRotating ? UiText.Get("AutoRaWorking") : WorkflowStep switch {
        0 => UiText.Get(AutoRaRotation ? "AutoReady" : "ManualReady"),
        1 => UiText.Get("Workflow5"),
        _ => liveBusy ? UiText.Get("Workflow6") : UiText.Get("Workflow7")
    };
    public string WorkflowHelp => WaitingForManualRotation ? ManualRotationInstruction : AutoRaRotation ? RotationStatus : UiText.Get("Workflow8");
    public string WorkflowDescription => IndependentDriftEnabled ? DriftInstruction : WorkflowStep switch {
        0 => UiText.Get(AutoRaRotation ? "AutoBrief" : "ManualBrief"),
        1 => IsRotating ? RotationStatus : UiText.Get(AutoRaRotation && !automaticRotationCompleted ? "AutoBrief" : "ManualRotateBrief"),
        _ => UiText.Get("AdjustBrief")
    };
    public string RotationReadout => session?.RotationDegrees is { } rotation
        ? UiText.Format("RotationMeasured", rotation) : UiText.Get("Workflow11");
    public string MeasurementState => latestError == null ? UiText.Get("Workflow12") : IsMeasurementStale ? UiText.Get("Workflow13")
        : latestError.Value.TotalArcminutes <= validationTarget / 60 ? UiText.Get("Workflow14")
        : latestError.Value.TotalArcminutes <= validationTarget / 30 ? UiText.Get("Workflow15") : UiText.Get("Workflow16");
    public bool IsWithinTarget => latestError is { } targetError && targetError.TotalArcminutes <= validationTarget / 60 && !IsMeasurementStale;
    public string AltitudeValue => latestError is { } error ? $"{error.AltitudeArcminutes:+0.00;-0.00;0.00}′" : "—";
    public string AzimuthValue => latestError is { } error ? $"{error.AzimuthArcminutes:+0.00;-0.00;0.00}′" : "—";
    public string AltitudeAction => latestError == null ? UiText.Get("Workflow17") : IsMeasurementStale ? UiText.Get("Workflow18")
        : Math.Abs(latestError.Value.AltitudeArcminutes) < .05 ? UiText.Get("Workflow19")
        : latestError.Value.AltitudeArcminutes > 0 ? UiText.Get("Workflow20") : UiText.Get("Workflow21");
    // Facing the visible pole, increasing north-through-east azimuth is to the right
    // in either hemisphere. These describe the telescope direction, never bolt rotation.
    public string AzimuthAction => latestError == null ? UiText.Get("Workflow22") : IsMeasurementStale ? UiText.Get("Workflow23")
        : Math.Abs(latestError.Value.AzimuthArcminutes) < .05 ? UiText.Get("Workflow24")
        : latestError.Value.AzimuthArcminutes > 0 ? UiText.Get("Workflow25") : UiText.Get("Workflow26");

    private void NotifyWorkflow() {
        NotifyImpact();
        workflowCommand?.NotifyCanExecuteChanged();
        (IndependentDriftCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (CaptureReferenceCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        (StartLiveCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        foreach (var name in new[] { nameof(CameraConnected), nameof(MountConnected), nameof(EquipmentReady),
            nameof(CameraDisconnected), nameof(MountDisconnected), nameof(NotConnectedText), nameof(CameraConnectionHint), nameof(PrimaryActionHint), nameof(WaitingForManualRotation) }) RaisePropertyChanged(name);
        foreach (var name in new[] { nameof(WorkflowStep), nameof(IsRotationStep), nameof(IsAdjustmentStep),
            nameof(IsWorking), nameof(HasOperationError), nameof(OperationError), nameof(HasLiveMeasurement),
            nameof(HasPreview), nameof(HasAlignmentDetail), nameof(HasCurrentFieldGuidance), nameof(FieldGuidance), nameof(PixelScaleArcseconds),
            nameof(PrimaryActionLabel), nameof(PrimaryActionCommand), nameof(IsStopping), nameof(ActionButtonLabel), nameof(ActionButtonHint), nameof(ActionButtonCommand), nameof(WorkflowTitle), nameof(WorkflowDescription), nameof(WorkflowHelp),
            nameof(RotationReadout), nameof(MeasurementState), nameof(IsWithinTarget), nameof(AltitudeValue),
            nameof(AzimuthValue), nameof(AltitudeAction), nameof(AzimuthAction) }) RaisePropertyChanged(name);
    }
}
