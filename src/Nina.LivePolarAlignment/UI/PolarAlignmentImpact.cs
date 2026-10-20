using System;
using Nina.LivePolarAlignment.Core;

namespace Nina.LivePolarAlignment.UI;

public sealed partial class PolarAlignmentDockableVM {
    private double scienceExposureSeconds = 300;

    public bool CanConfigureAlignmentTarget => !fieldSaving && !driftInProgress;

    public double ScienceExposureSeconds {
        get => scienceExposureSeconds;
        set {
            if (!double.IsFinite(value) || value is < 1 or > 3600) return;
            scienceExposureSeconds = value;
            NotifyImpact();
        }
    }

    public string AlignmentQualityLevel => latestError == null || IsMeasurementStale ? "Unavailable"
        : IsWithinTarget ? "Within" : latestError.Value.TotalArcminutes * 60 <= validationTarget * 2 ? "Close" : "Adjust";

    public string AlignmentQualityText => AlignmentQualityLevel switch {
        "Within" => UiText.Format("QualityWithin", validationTarget),
        "Close" => UiText.Format("QualityClose", validationTarget),
        "Adjust" => UiText.Format("QualityAdjust", validationTarget),
        _ => UiText.Get("QualityUnavailable")
    };

    public string EstimatedUnguidedDrift {
        get {
            if (latestError == null || IsMeasurementStale) return "—";
            var bound = Core.PolarAlignmentImpact.Estimate(latestError.Value.TotalArcminutes * 60, scienceExposureSeconds);
            return UiText.Format("ImpactRate", bound.ArcsecondsPerMinute);
        }
    }

    public string EstimatedExposureMotion {
        get {
            if (latestError == null || IsMeasurementStale) return "—";
            var bound = Core.PolarAlignmentImpact.Estimate(latestError.Value.TotalArcminutes * 60, scienceExposureSeconds);
            return pixelScale > 0 ? UiText.Format("ImpactMotionPixels", bound.ExposureArcseconds,
                bound.ExposureArcseconds / pixelScale) : UiText.Format("ImpactMotion", bound.ExposureArcseconds);
        }
    }

    private void NotifyImpact() {
        foreach (var name in new[] { nameof(CanConfigureAlignmentTarget), nameof(ScienceExposureSeconds), nameof(AlignmentQualityLevel),
            nameof(AlignmentQualityText), nameof(EstimatedUnguidedDrift), nameof(EstimatedExposureMotion) })
            RaisePropertyChanged(name);
    }
}
