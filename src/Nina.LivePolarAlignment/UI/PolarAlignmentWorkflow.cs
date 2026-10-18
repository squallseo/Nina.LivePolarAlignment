using System;
using System.Windows.Input;

namespace Nina.LivePolarAlignment.UI;

public sealed partial class PolarAlignmentDockableVM {
    private int calibrationSamples;
    private double pixelScale;
    private string? operationError;

    public int WorkflowStep => calibrationSamples;
    public bool IsRotationStep => WorkflowStep == 1;
    public bool IsAdjustmentStep => WorkflowStep == 2;
    public bool IsWorking => liveBusy;
    public bool HasOperationError => operationError != null;
    public string OperationError => operationError ?? string.Empty;
    public bool HasLiveMeasurement => latestError != null;
    public bool HasPreview => livePreview != null;
    public bool HasCurrentFieldGuidance => guidance != null && (calibrationSamples < 2 || !IsMeasurementStale);
    public Capture.PolarGuidance? FieldGuidance => guidance;
    public double PixelScaleArcseconds => pixelScale;
    public string PrimaryActionLabel => liveBusy ? "촬영 · 해석 중…" : WorkflowStep switch {
        0 => "첫 기준 촬영",
        1 => "회전 후 기준 촬영",
        _ => "실시간 조정 시작"
    };
    public ICommand PrimaryActionCommand => IsAdjustmentStep ? StartLiveCommand : CaptureReferenceCommand;
    public string WorkflowTitle => WorkflowStep switch {
        0 => "먼저, 천구극 주변을 촬영해 주세요",
        1 => "이제 RA축만 60–90° 돌려주세요",
        _ => liveBusy ? "두 표시가 겹치도록 볼트를 조정하세요" : "보정 완료. 실시간 측정을 시작하세요"
    };
    public string WorkflowDescription => WorkflowStep switch {
        0 => "카메라를 천구극 근처로 향하게 하고 첫 기준을 촬영합니다. 북극성 한 점이 아닌 주변 별 패턴으로 하늘 위치를 찾습니다.",
        1 => "고도·방위각과 DEC는 그대로 둡니다. RA만 회전하고 흔들림이 멈추면 두 번째 기준을 촬영하세요.",
        _ => "RA와 카메라를 고정하고 고도·방위각 볼트만 조금씩 조정하세요. 다음 측정에서 오차가 줄어드는지 확인합니다."
    };
    public string RotationReadout => session?.RotationDegrees is { } rotation
        ? $"마지막 촬영 회전량 {rotation:0.0}°" : "권장 60–90° · 최소 30°";
    public string MeasurementState => latestError == null ? "측정 대기" : IsMeasurementStale ? "이전 측정 · 일시정지"
        : latestError.Value.TotalArcminutes <= 1 ? "목표 범위 · 1′ 이내"
        : latestError.Value.TotalArcminutes <= 2 ? "좋음 · 미세 조정" : "조정 중";
    public bool IsWithinTarget => latestError is { TotalArcminutes: <= 1 } && !IsMeasurementStale;
    public string AltitudeValue => latestError is { } error ? $"{error.AltitudeArcminutes:+0.00;-0.00;0.00}′" : "—";
    public string AzimuthValue => latestError is { } error ? $"{error.AzimuthArcminutes:+0.00;-0.00;0.00}′" : "—";
    public string AltitudeAction => latestError == null ? "두 기준 촬영 후 표시" : IsMeasurementStale ? "측정을 다시 시작하세요"
        : Math.Abs(latestError.Value.AltitudeArcminutes) < .05 ? "현재 높이를 유지하세요"
        : latestError.Value.AltitudeArcminutes > 0 ? "경통 끝을 조금 낮추세요" : "경통 끝을 조금 올리세요";
    // Facing the visible pole, increasing north-through-east azimuth is to the right
    // in either hemisphere. These describe the telescope direction, never bolt rotation.
    public string AzimuthAction => latestError == null ? "두 기준 촬영 후 표시" : IsMeasurementStale ? "측정을 다시 시작하세요"
        : Math.Abs(latestError.Value.AzimuthArcminutes) < .05 ? "현재 방향을 유지하세요"
        : latestError.Value.AzimuthArcminutes > 0 ? "경통 끝을 조금 왼쪽으로" : "경통 끝을 조금 오른쪽으로";

    private void NotifyWorkflow() {
        foreach (var name in new[] { nameof(WorkflowStep), nameof(IsRotationStep), nameof(IsAdjustmentStep),
            nameof(IsWorking), nameof(HasOperationError), nameof(OperationError), nameof(HasLiveMeasurement),
            nameof(HasPreview), nameof(HasCurrentFieldGuidance), nameof(FieldGuidance), nameof(PixelScaleArcseconds),
            nameof(PrimaryActionLabel), nameof(PrimaryActionCommand), nameof(WorkflowTitle), nameof(WorkflowDescription),
            nameof(RotationReadout), nameof(MeasurementState), nameof(IsWithinTarget), nameof(AltitudeValue),
            nameof(AzimuthValue), nameof(AltitudeAction), nameof(AzimuthAction) }) RaisePropertyChanged(name);
    }
}
