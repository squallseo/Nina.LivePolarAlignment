using System;
using System.ComponentModel.Composition;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.ViewModel;

namespace Nina.LivePolarAlignment.UI;

/// <summary>Manual RA calibration, camera-based live polar guidance, and a separate synthetic demo.</summary>
[Export(typeof(IDockableVM))]
public sealed partial class PolarAlignmentDockableVM : DockableVM, IDisposable {
    private int samples;
    private bool running;
    private double altitude = 6, azimuth = -8;

    [ImportingConstructor]
    public PolarAlignmentDockableVM(IProfileService profiles, Capture.IPolarAlignmentCaptureService captureService, IApplicationStatusMediator? applicationStatusMediator = null) : base(profiles) {
        freshnessTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, uiDispatcher) {
            Interval = TimeSpan.FromSeconds(1)
        };
        Title = "Live Polar Alignment";
        this.applicationStatusMediator = applicationStatusMediator;
        var iconResources = new PolarAlignmentTemplates();
        ImageGeometry = (System.Windows.Media.GeometryGroup)iconResources["Nina.LivePolarAlignment.PolarAxisIcon"];
        ImageGeometry.Freeze();
        StartCommand = new RelayCommand(Start, () => !running);
        CaptureCommand = new RelayCommand(Capture, () => running && samples < 2);
        StopCommand = new RelayCommand(Stop, () => running);
        InitializeLive(profiles, captureService);
    }

    public override bool IsTool => true;
    public ICommand StartCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand StopCommand { get; }
    public bool IsAdjusting => running && samples == 2;
    public bool CanStart => !running;
    public string Stage => !running ? "Ready to explore" : samples < 2 ? "Calibrate the rotation axis" : "Adjust the mount";
    public string Progress => $"{samples} / 2 reference samples";
    public string NextAction => !running ? "Start the demo to explore the workflow. No equipment is controlled." : samples switch {
        0 => "1. Capture the first reference field near the celestial pole.",
        1 => "2. Rotate only the RA axis by 60–90°, then capture the rotated field.",
        _ => "Use the demo altitude and azimuth sliders to bring the orange marker into the target."
    };
    public string CaptureLabel => $"Use demo sample {Math.Min(samples + 1, 2)}";
    public double AltitudeError {
        get => altitude;
        set { altitude = Math.Clamp(value, -12, 12); Refresh(); }
    }
    public double AzimuthError {
        get => azimuth;
        set { azimuth = Math.Clamp(value, -12, 12); Refresh(); }
    }
    public double MarkerLeft => 154 + azimuth * 8;
    public double MarkerTop => 104 - altitude * 8;
    public string TotalError => IsAdjusting ? $"{Math.Sqrt(altitude * altitude + azimuth * azimuth):0.0}′" : "—";
    public string AltitudeText => IsAdjusting ? $"Altitude: {Math.Abs(altitude):0.0}′ {(altitude > 0 ? "above" : "below")} target" : "Altitude: awaiting calibration";
    public string AzimuthText => IsAdjusting ? $"Azimuth: {Math.Abs(azimuth):0.0}′ {(azimuth > 0 ? "right of" : "left of")} target" : "Azimuth: awaiting calibration";
    public string Quality => !IsAdjusting ? "Demo data only · no sky measurement" : Math.Sqrt(altitude * altitude + azimuth * azimuth) <= 1 ? "Demo target reached · within 1 arcminute" : "Move the orange marker toward the center";

    private void Start() { running = true; samples = 0; altitude = 6; azimuth = -8; Refresh(); }
    private void Capture() { if (running && samples < 2) samples++; Refresh(); }
    private void Stop() { running = false; samples = 0; Refresh(); }
    private void Refresh() {
        foreach (var property in new[] { nameof(IsAdjusting), nameof(CanStart), nameof(Stage), nameof(Progress), nameof(NextAction), nameof(CaptureLabel), nameof(AltitudeError), nameof(AzimuthError), nameof(MarkerLeft), nameof(MarkerTop), nameof(TotalError), nameof(AltitudeText), nameof(AzimuthText), nameof(Quality) }) RaisePropertyChanged(property);
        ((RelayCommand)StartCommand).NotifyCanExecuteChanged();
        ((RelayCommand)CaptureCommand).NotifyCanExecuteChanged();
        ((RelayCommand)StopCommand).NotifyCanExecuteChanged();
    }
}
