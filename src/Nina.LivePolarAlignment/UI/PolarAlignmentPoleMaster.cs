using System;
using System.Threading;
using System.Threading.Tasks;
using Nina.LivePolarAlignment.Capture;

namespace Nina.LivePolarAlignment.UI;

public sealed partial class PolarAlignmentDockableVM {
    private bool poleMasterMode;
    private bool poleMasterMonitoring;
    public bool UsePolarisTarget => IsAdjustmentStep && guidance?.PolarisTarget!=null && !poleMasterMonitoring;
    public bool IsPoleMasterMonitoring => IsAdjustmentStep && poleMasterMonitoring;
    public bool ShowPoleMasterMonitoringAction => UsePolarisTarget && HasLiveMeasurement;
    public bool ShowPolarErrorReadout => !UsePolarisTarget;
    private ManualPolarisOffset? manualOffset;
    private DateTimeOffset? manualOffsetAt;
    private bool manualOffsetSeen;
    private System.Windows.Rect manualDetailRegion;
    private bool HasFreshManualOffset => UsePolarisTarget && liveBusy && !IsStopping && CameraConnected
        && manualOffsetAt is {} time && (DateTimeOffset.UtcNow-time).TotalSeconds is >=0 and <=2
        && manualOffset is {DistanceArcseconds:>=0} value && double.IsFinite(value.DistanceArcseconds)
        && double.IsFinite(value.NormalizedPosition.X) && double.IsFinite(value.NormalizedPosition.Y)
        && manualDetailRegion.Contains(new System.Windows.Point(value.NormalizedPosition.X,value.NormalizedPosition.Y));
    public bool ShowManualOffset => UsePolarisTarget && manualOffsetSeen;
    public string ManualOffsetReadout => HasFreshManualOffset ? UiText.Format("ManualTargetDistance",ArcsecondsText(manualOffset!.DistanceArcseconds)) : UiText.Get("ManualTargetUnconfirmed");
    public void SetManualDetailRegion(System.Windows.Rect normalizedRegion) {
        if(manualDetailRegion==normalizedRegion)return;
        manualDetailRegion=normalizedRegion;
        if(HasFreshManualOffset)manualOffsetSeen=true;
        NotifyManualOffset();
    }
    private void NotifyManualOffset() {RaisePropertyChanged(nameof(ShowManualOffset));RaisePropertyChanged(nameof(ManualOffsetReadout));}
    public System.Windows.Input.ICommand StartPoleMasterMonitoringCommand {get;private set;}=null!;
    public System.Windows.Input.ICommand ReturnToPolarisTargetCommand {get;private set;}=null!;
    private int poleMasterStage;
    private double? poleMasterAxisSpread;
    private (bool AutoRa, bool Streaming, bool Reverse, bool VerifyReverse, int Angle, int Bin, double Exposure, double StreamExposure, double Target)? previousCameraOptions;
    public bool CanConfigurePoleMaster => CanConfigureLive && IsPolarView;
    public int AlignmentMethodIndex {get=>PoleMasterMode?0:1;set{if(value is 0 or 1)PoleMasterMode=value==0;}}
    public bool PoleMasterMode {
        get => poleMasterMode;
        set {
            if (!CanConfigurePoleMaster || value == poleMasterMode) return;
            ResetLive();
            if (value) {
                previousCameraOptions = (autoRaRotation, useStreaming, reverseRaRotation, verificationReverse, rotationAngleDegrees, streamingBinning,
                    ExposureSeconds, streamingExposureSeconds, validationTarget);
                autoRaRotation = true; useStreaming = true; reverseRaRotation = false; verificationReverse = false; rotationAngleDegrees = 80;
                streamingBinning = 1; ExposureSeconds = .1; streamingExposureSeconds = .1;
                if(!captureService.IsSynthetic && profiles.ActiveProfile.PluginSettings?.TryGetValue(Plugin.SettingsId,"PoleMaster.Exposure",out double savedExposure)==true
                    && double.IsFinite(savedExposure) && savedExposure is >=.02 and <=10)ExposureSeconds=streamingExposureSeconds=savedExposure;
                validationTarget = 30;
            } else if (previousCameraOptions is { } saved) {
                autoRaRotation = saved.AutoRa; useStreaming = saved.Streaming; reverseRaRotation = saved.Reverse; verificationReverse = saved.VerifyReverse; rotationAngleDegrees = saved.Angle;
                streamingBinning = saved.Bin; ExposureSeconds = saved.Exposure;
                streamingExposureSeconds = saved.StreamExposure; validationTarget = saved.Target;
                previousCameraOptions = null;
            }
            poleMasterMode = value;
            RaisePropertyChanged(nameof(AlignmentMethodIndex));
            PreviewStretchEnabled=!value;
            // The user starts with the mount physically at Home. A new session
            // begins with the Polaris click; explicit restart still homes first.
            poleMasterNeedsHome=false;
            awaitingInitialHomeStatus=false;
            RaisePropertyChanged(nameof(CanToggleAutomaticRotation));
            RaisePropertyChanged(nameof(ExposureSeconds)); RaisePropertyChanged(nameof(StreamingExposureSeconds));
            RaisePropertyChanged(nameof(CaptureExposureSeconds));
            RaisePropertyChanged(nameof(StreamingBinning)); RaisePropertyChanged(nameof(UseStreaming));
            NotifyDrift(); NotifyRotation(); NotifyLive();
            previewManuallyPaused=false;previewAttempted=false;UpdatePoleMasterPreview();
        }
    }
    public string PoleMasterInstructions => UiText.Get("PoleMasterInstructions");
    public string PoleMasterProgress => poleMasterAxisSpread is { } spread
        ? UiText.Format("PoleMasterConsistent", spread) : UiText.Format("PoleMasterStage", poleMasterStage);

    private async Task CapturePoleMasterReferencesAsync(CancellationToken token, Action<SolvedPolarFrame>? firstSolved = null) {
        poleMasterCaptureLocked=true;NotifyCaptureControls();
        // This workflow always restarts from three fresh locations. No partial pair can resume.
        session = null; calibrationSamples = 0; automaticRotationCompleted = false;
        captureService.ResetRaReturn(); poleMasterAxisSpread = null; poleMasterStage = 0;
        try {
            await captureService.PrepareAutoRaAsync(token);
            var first = await CaptureReferenceFrameAsync(token);
            firstSolved?.Invoke(first);
            poleMasterStage = 1; NotifyLive();
            await RotateInterval(false);
            var middle = await captureService.CaptureAndSolveAsync(ExposureSeconds, token);
            token.ThrowIfCancellationRequested(); ValidateSite();
            livePreview = middle.Preview; displayedStars=middle.Stars; guidance = session!.GetReferenceGuidance(middle);
            measuredAt = middle.CapturedAtUtc; pixelScale = middle.PixelScaleArcseconds;
            RecordFieldFrame(middle, "polemaster_middle", true);
            poleMasterStage = 2; NotifyLive();
            await SaveReferenceEvidenceAsync(middle);
            await RotateInterval(true);
            var last = await captureService.CaptureAndSolveAsync(ExposureSeconds, token);
            token.ThrowIfCancellationRequested(); ValidateSite();
            RecordFieldFrame(last, "polemaster_last", true);
            await SaveReferenceEvidenceAsync(last);
            var result = PoleMasterCalibration.Calculate(first, middle, last, calibrationLatitude, calibrationLongitude);
            last=result.LastFrame??last;if(result.PatternScale is {} scale)captureService.SetPatternScale(scale);
            session = result.Session; calibrationSamples = 2; automaticRotationCompleted = true;
            poleMasterAxisSpread = result.AxisSpreadArcseconds; poleMasterStage = 3;
            livePreview = last.Preview; displayedStars=last.Stars; guidance = session.GetGuidance(last);
            RecordFieldFrame(last, "reference", true);
            RecordFieldEvent("polemaster_calibration", new { locations = 3, intervalDegrees = 40, result.AxisSpreadArcseconds,
                consistencyLimitArcseconds = 60, absoluteAccuracyVerified = false });
            NotifyLive();
            await ReturnForAdjustmentAsync(token);
        } catch {
            session = null; calibrationSamples = 0; automaticRotationCompleted = false;
            guidance = null; latestError = null; measurementStale = true; poleMasterAxisSpread = null;
            captureService.ResetRaReturn(); EndFieldSession("polemaster_calibration_failed_or_cancelled");
            throw;
        } finally { rotationInProgress = false; NotifyLive(); }

        async Task RotateInterval(bool continuation) {
            rotationInProgress = true; rotationProgress = continuation ? 40 : 0;
            bool reporting = true;
            try {
                RecordFieldEvent("ra_rotation_start", new { interval = poleMasterStage, targetDegrees = 40, reverse = ReverseRaRotation });
                var progress = new Progress<double>(degrees => {
                    if (!reporting || token.IsCancellationRequested) return;
                    rotationProgress = (continuation ? 40 : 0) + degrees; NotifyLive();
                });
                NotifyLive();
                await captureService.RotateRaWithPreviewAsync(ReverseRaRotation, continuation, StreamingExposureSeconds, progress, token, 40);
                token.ThrowIfCancellationRequested(); ValidateSite();
                RecordFieldEvent("ra_rotation_complete", new { interval = poleMasterStage, targetDegrees = 40 });
            } finally { reporting = false; rotationInProgress = false; NotifyLive(); }
        }
    }
}
