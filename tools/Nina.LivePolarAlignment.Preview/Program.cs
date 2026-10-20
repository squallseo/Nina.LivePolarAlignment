using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Model;
using NINA.Profile.Interfaces;
using Nina.LivePolarAlignment.Capture;
using Nina.LivePolarAlignment.UI;

namespace Nina.LivePolarAlignment.Preview;

public class SampleProfile : DispatchProxy {
    public IPluginSettings PluginSettings { get; } = new NINA.Profile.PluginSettings();
    public object? Active { get; set; }
    public object? Site { get; set; }
    public object? Theme { get; set; }
    public object? Settings { get; set; }
    public static string Language { get; set; } = "en-US";
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch {
        "get_PluginSettings" => PluginSettings, "get_ApplicationSettings" => Settings, "get_Culture" => Language, "get_ColorSchemaSettings" => Theme, "get_ActiveProfile" => Active, "get_AstrometrySettings" => Site,
        "get_Latitude" => 37.5, "get_Longitude" => 127.0, _ => null
    };
}

public class SampleStatus : DispatchProxy {
    public static readonly List<ApplicationStatus> Updates = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args) {
        if (method?.Name == "StatusUpdate") Updates.Add((ApplicationStatus)args![0]!);
        return null;
    }
}

internal sealed class SampleCapture : IPolarAlignmentCaptureService {
    public static bool CameraOffline, MountOffline;
    public bool CameraConnected => !CameraOffline;
    public bool MountConnected => !MountOffline;
    public Func<int> Step { get; set; } = () => 0;
    public event Action<BitmapSource>? PreviewAvailable;
    public static AutoRaAvailability? AutoUnavailable;
    public static bool Parked, FailUnpark;
    public static int UnparkCalls, CaptureCalls;
    public bool CanPrepareAutoRaRotation => SupportsAutoRaRotation || (Parked && MountConnected && !UnsupportedAuto);
    public async Task PrepareAutoRaAsync(CancellationToken token) {
        if (!Parked) return;
        UnparkCalls++; await Task.Delay(200, token);
        if (FailUnpark) throw new InvalidOperationException("Synthetic unpark failure");
        Parked = false;
    }
    public AutoRaAvailability AutoRaAvailability => AutoUnavailable ?? (Parked ? AutoRaAvailability.Parked : SupportsAutoRaRotation ? AutoRaAvailability.Ready : AutoRaAvailability.DriverError);
    public static bool UnsupportedStream, StreamCleanupFailure;
    public bool SupportsStreaming => !UnsupportedStream;
    public bool IsSynthetic => true;
    public static bool UnsupportedAuto, FailRotation;
    public bool SupportsAutoRaRotation => MountConnected && !UnsupportedAuto && AutoUnavailable == null && !Parked;
    public static int Rotations;
    public static double LastRotationAngle;
    public static bool RotationStopped;
    private double orientation;
    private bool rotatedSinceReference;
    public static bool DriftTestMode, DriftSecondField, DriftFault;
    private bool driftWasSecond;
    private int driftIndex;
    private readonly DateTimeOffset driftStart = DateTimeOffset.UtcNow;
    public object? DriftMountIdentity => this;
    public void ValidateDriftTracking() { if (!DriftTestMode) throw new InvalidOperationException("Synthetic drift mode is off"); }
    public Nina.LivePolarAlignment.Core.DriftSample GetDriftSample(SolvedPolarFrame frame) =>
        new(frame.CapturedAtUtc, frame.DeclinationDegrees, frame.RightAscensionDegrees - 180, 40, frame.PixelScaleArcseconds);
    public async Task RotateRaAsync(bool reverse, IProgress<double>? progress, CancellationToken token, double targetDegrees = 90) {
        Rotations++; LastRotationAngle = targetDegrees; RotationStopped = false;
        try {
            if (FailRotation) throw new InvalidOperationException("Synthetic rotation failure");
            for (int i = 1; i <= targetDegrees; i++) { await Task.Delay(20, token); progress?.Report(i); }
            orientation += reverse ? -targetDegrees : targetDegrees; rotatedSinceReference = true;
        }
        finally { RotationStopped = true; }
    }
    public static int StreamFrames { get; private set; }
    public static bool FailStream { get; set; }
    public async IAsyncEnumerable<SolvedPolarFrame> StreamAndSolveAsync(double exposureSeconds,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token) {
        StreamFrames = 0;
        while (true) {
            await Task.Delay(100, token);
            if (StreamCleanupFailure) throw new StreamingCleanupException(new InvalidOperationException("Synthetic restoration failure"));
            if (FailStream) throw new InvalidOperationException("Synthetic streaming failure");
            StreamFrames++;
            yield return new(0, 89.7, orientation, 6, 800, 600, false, DateTimeOffset.UtcNow, Image);
        }
    }
    public BitmapSource Image { get; } = MakeStars();
    public async Task<SolvedPolarFrame> CaptureAndSolveAsync(double exposureSeconds, CancellationToken token, IProgress<ApplicationStatus>? progress = null) {
        CaptureCalls++;
        if (DriftTestMode) {
            await Task.Delay(10, token);
            if (driftWasSecond != DriftSecondField) { driftIndex = 0; driftWasSecond = DriftSecondField; }
            double seconds = driftIndex++ * 10, h0 = (DriftSecondField ? -50 : 0) * Math.PI / 180;
            double h = h0 + Nina.LivePolarAlignment.Core.DriftValidation.SiderealRadiansPerSecond * seconds;
            double declination = (300 * (Math.Cos(h0) - Math.Cos(h)) + 400 * (Math.Sin(h) - Math.Sin(h0))) / 3600;
            var time = driftStart.AddSeconds((DriftSecondField ? 600 : 0) + (DriftFault ? 0 : seconds));
            return new(180 + h * 180 / Math.PI, declination, 0, 2, 800, 600, false, time, Image);
        }
        await Task.Delay(150, token);
        PreviewAvailable?.Invoke(Image);
        await Task.Delay(500, token);
        int step = Step();
        if (step == 0) rotatedSinceReference = false;
        if (step == 1 && !rotatedSinceReference) { orientation += 72; rotatedSinceReference = true; }
        return new(0, step == 2 ? 89.7 : 89.6, orientation, 6, 800, 600, false, DateTimeOffset.UtcNow, Image);
    }
    private static BitmapSource MakeStars() {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(8, 15, 27)), null, new Rect(0, 0, 800, 600));
            var random = new Random(21);
            for (int i = 0; i < 140; i++) {
                var point = new Point(random.NextDouble() * 800, random.NextDouble() * 600);
                double radius = random.NextDouble() * 1.4 + .5;
                drawing.DrawEllipse(Brushes.White, null, point, radius, radius);
            }
        }
        var bitmap = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
}

internal static class Program {
    private static PolarAlignmentDockableVM? current;
    private static Task? currentLoop;
    private static PolarAlignmentView View = null!;
    private static NINA.Profile.ColorSchemaSettings Theme = null!;
    private static readonly StackPanel SceneButtons = new() { Orientation = Orientation.Horizontal };

    [STAThread]
    private static void Main(string[] args) {
        // The preview owns a temporary NINA context; it never opens the user's profiles/database.
        var sampleData = Path.Combine(Path.GetTempPath(), "NinaPolarAlignmentSample", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sampleData);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", sampleData);
        var application = new Application();
        NINA.Core.Locale.Loc.Instance.ReloadLocale("en-US");
        application.Resources["PuzzlePieceSVG"] = new GeometryGroup();
        var theme = new NINA.Profile.ColorSchemaSettings();
        Theme = theme;
        theme.ColorSchema = theme.ColorSchemas.Items.First(x => x.Name == "Persian");
        var tp = DispatchProxy.Create<IProfile, SampleProfile>(); ((SampleProfile)(object)tp).Theme = theme;
        var ts = DispatchProxy.Create<IProfileService, SampleProfile>(); ((SampleProfile)(object)ts).Active = tp;
        application.Resources["ProfileService"] = ts;
        foreach (var r in new[] { "StaticResources/SVGDictionary.xaml", "StaticResources/Brushes.xaml", "Styles/Button.xaml", "Styles/TextBlock.xaml", "Styles/TextBox.xaml", "Styles/CheckBox.xaml", "Styles/ComboBox.xaml", "Styles/StepperControl.xaml", "Styles/Expander.xaml" })
            application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/NINA.WPF.Base;component/Resources/" + r) });
        View = new PolarAlignmentView();
        var layout = new DockPanel();
        var header = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(34, 44, 60)) };
        header.Children.Add(new TextBlock {
            Text = "합성 샘플 미리보기 · 카메라/적도의 연결 없음",
            Foreground = new SolidColorBrush(Color.FromRgb(240, 186, 114)), Margin = new Thickness(14, 11, 14, 7)
        });
        foreach (var item in new[] { ("1. 첫 화면", 0), ("2. RA 회전", 1), ("3. 조정 화면", 2) }) {
            var button = new Button { Content = item.Item1, Foreground = Brushes.White, Margin = new Thickness(4, 0, 4, 9), Padding = new Thickness(10, 5, 10, 5) };
            button.Click += async (_, _) => await SelectScene(item.Item2);
            SceneButtons.Children.Add(button);
        }
        header.Children.Add(SceneButtons);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header); layout.Children.Add(View);
        var window = new Window {
            Title = "극축 정렬 · 넓은 화면 미리보기", Content = layout, Width = 1440,
            Height = Math.Min(960, SystemParameters.WorkArea.Height - 45), MinWidth = 380, MinHeight = 650,
            WindowState = WindowState.Maximized,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = View.Background
        };
        window.Closed += (_, _) => current?.Dispose();
        application.Dispatcher.InvokeAsync(async () => {
            try {
                if (args.Length == 2 && args[0] == "--render-to") {
                    await VerifyAndRender(Path.GetFullPath(args[1])); application.Shutdown();
                } else await SelectScene(2);
            } catch (Exception error) {
                if (args.Length == 2 && args[0] == "--render-to") {
                    File.WriteAllText(Path.Combine(Path.GetFullPath(args[1]), "preview-error.txt"), error.ToString());
                    Environment.ExitCode = 1; application.Shutdown();
                } else MessageBox.Show(window, error.ToString(), "샘플을 열지 못했습니다");
            }
        });
        if (args.Length == 2 && args[0] == "--render-to") application.Run();
        else application.Run(window);
    }

    private static async Task SelectScene(int scene) {
        SceneButtons.IsEnabled = false;
        try {
            current?.Dispose();
            if (currentLoop != null) await currentLoop;
            currentLoop = null;
            var site = DispatchProxy.Create<IAstrometrySettings, SampleProfile>();
            var profile = DispatchProxy.Create<IProfile, SampleProfile>(); ((SampleProfile)(object)profile).Site = site;
            ((SampleProfile)(object)profile).Settings = DispatchProxy.Create<IApplicationSettings, SampleProfile>();
            var profiles = DispatchProxy.Create<IProfileService, SampleProfile>(); ((SampleProfile)(object)profiles).Active = profile;
            var capture = new SampleCapture();
            current = new PolarAlignmentDockableVM(profiles, capture, DispatchProxy.Create<NINA.WPF.Base.Interfaces.Mediator.IApplicationStatusMediator, SampleStatus>());
            if (!current.AutoRaRotation || !current.UseStreaming || current.RotationAngleDegrees != 90 || !current.ReverseRaRotation || !current.VerificationReverse) throw new InvalidOperationException("Acquisition defaults must be on with westward RA rotation");
            current.AutoRaRotation = false; current.UseStreaming = false;
            capture.Step = () => current.WorkflowStep;
            View.DataContext = current;
            for (int i = 0; i < scene; i++) await ((IAsyncRelayCommand)current.CaptureReferenceCommand).ExecuteAsync(null);
            if (scene == 2) {
                currentLoop = ((IAsyncRelayCommand)current.StartLiveCommand).ExecuteAsync(null);
                while (!current.HasLiveMeasurement && !currentLoop.IsCompleted) await Task.Delay(25);
                if (!current.HasLiveMeasurement) throw new InvalidOperationException(current.OperationError);
            }
        } finally { SceneButtons.IsEnabled = true; }
    }

    private static async Task VerifyAndRender(string directory) {
        Directory.CreateDirectory(directory);
        var icon = (GeometryGroup)new PolarAlignmentTemplates()["Nina.LivePolarAlignment.PolarAxisIcon"];
        var iconVisual = new DrawingVisual();
        using (var dc = iconVisual.RenderOpen()) {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(36, 49, 55)), null, new Rect(0, 0, 400, 160));
            dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 80, 400, 80));
            foreach (var row in new[] { 0, 1 }) {
                int x = 20;
                foreach (var size in new[] { 16, 24, 32, 48, 64 }) {
                    dc.PushTransform(new TranslateTransform(x, row * 80 + (80 - size) / 2d));
                    dc.PushTransform(new ScaleTransform(size / 32d, size / 32d));
                    dc.DrawGeometry(row == 0 ? Brushes.WhiteSmoke : Brushes.DarkSlateGray, null, icon);
                    dc.Pop(); dc.Pop(); x += size + 25;
                }
            }
        }
        var iconBitmap = new RenderTargetBitmap(400, 160, 96, 96, PixelFormats.Pbgra32); iconBitmap.Render(iconVisual);
        var iconEncoder = new PngBitmapEncoder(); iconEncoder.Frames.Add(BitmapFrame.Create(iconBitmap));
        using (var iconFile = File.Create(Path.Combine(directory, "panel-icon.png"))) iconEncoder.Save(iconFile);
        var brandVisual = new DrawingVisual();
        using (var dc = brandVisual.RenderOpen()) {
            var background = new LinearGradientBrush(Color.FromRgb(29, 48, 61), Color.FromRgb(9, 18, 30), 60);
            dc.DrawRoundedRectangle(background, null, new Rect(0, 0, 512, 512), 88, 88);
            dc.PushTransform(new TranslateTransform(70, 88));
            dc.PushTransform(new ScaleTransform(12, 12));
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(49, 210, 193)), null, icon.Children[0]);
            dc.DrawGeometry(Brushes.WhiteSmoke, null, icon.Children[1]);
            dc.DrawGeometry(Brushes.WhiteSmoke, null, icon.Children[2]);
            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(255, 203, 112)), null, icon.Children[3]);
            dc.Pop(); dc.Pop();
        }
        var brandBitmap = new RenderTargetBitmap(512, 512, 96, 96, PixelFormats.Pbgra32); brandBitmap.Render(brandVisual);
        var brandEncoder = new PngBitmapEncoder(); brandEncoder.Frames.Add(BitmapFrame.Create(brandBitmap));
        using (var brandFile = File.Create(Path.Combine(directory, "plugin-icon.png"))) brandEncoder.Save(brandFile);
        var optionProfile = DispatchProxy.Create<IProfile, SampleProfile>();
        ((SampleProfile)(object)optionProfile).Settings = DispatchProxy.Create<IApplicationSettings, SampleProfile>();
        var optionProfiles = DispatchProxy.Create<IProfileService, SampleProfile>();
        ((SampleProfile)(object)optionProfiles).Active = optionProfile;
        var plugin = new Plugin(optionProfiles);
        if (plugin.EnableDebugRecording) throw new InvalidOperationException("Debug must default off");
        var optionResources = new PolarAlignmentTemplates();
        var optionView = (StackPanel)((DataTemplate)optionResources["Live Polar Alignment_Options"]).LoadContent();
        optionView.DataContext = plugin;
        optionView.Measure(new Size(500, 300)); optionView.Arrange(new Rect(0, 0, 500, 300)); optionView.UpdateLayout();
        var checkbox = optionView.Children.OfType<CheckBox>().Single();
        checkbox.IsChecked = true;
        if (!new Plugin(optionProfiles).EnableDebugRecording) throw new InvalidOperationException("Options binding did not persist debug recording");
        checkbox.IsChecked = false;
        if (new Plugin(optionProfiles).EnableDebugRecording) throw new InvalidOperationException("Options binding did not disable debug recording");
        plugin.EnableDebugRecording = true;
        ((SampleProfile)(object)optionProfiles).Active = DispatchProxy.Create<IProfile, SampleProfile>();
        if (plugin.EnableDebugRecording) throw new InvalidOperationException("Debug setting leaked between profiles");
        for (int scene = 0; scene < 3; scene++) {
            await SelectScene(scene);
            if (System.Text.RegularExpressions.Regex.IsMatch(current!.WorkflowTitle + current.WorkflowDescription + current.LiveStatus + current.FrameFreshness, @"[\uac00-\ud7af]")) throw new InvalidOperationException("Korean text in English workflow");
            if (current!.WorkflowStep != scene) throw new InvalidOperationException("Wrong workflow step");
            foreach (var size in new[] { (360, 1000), (1280, 800), (1920, 1080) }) Save(Path.Combine(directory, $"ui-step-{scene + 1}-{size.Item1}.png"), size.Item1, size.Item2);
        }
        SampleProfile.Language = "ko-KR"; NINA.Core.Locale.Loc.Instance.ReloadLocale(SampleProfile.Language); UiText.Refresh();
        if (!System.Text.RegularExpressions.Regex.IsMatch(current!.WorkflowTitle, @"[\uac00-\ud7af]")) throw new InvalidOperationException("Korean locale missing");
        Save(Path.Combine(directory, "ui-korean-1280.png"), 1280, 800);
        SampleProfile.Language = "fr-FR"; NINA.Core.Locale.Loc.Instance.ReloadLocale(SampleProfile.Language); UiText.Refresh();
        if (!current.PrimaryActionLabel.StartsWith("Capturing / solving")) throw new InvalidOperationException("English fallback missing");
        SampleProfile.Language = "en-US"; NINA.Core.Locale.Loc.Instance.ReloadLocale(SampleProfile.Language); UiText.Refresh();
        current!.StopLiveCommand.Execute(null);
        await currentLoop!;
        if (!current.IsMeasurementStale || current.HasCurrentFieldGuidance || current.HasAlignmentDetail) throw new InvalidOperationException("Old guidance survived stop");
        Save(Path.Combine(directory, "ui-paused-1280.png"), 1280, 800);
        current.UseStreaming = true;
        currentLoop = ((IAsyncRelayCommand)current.StartLiveCommand).ExecuteAsync(null);
        while (SampleCapture.StreamFrames < 2 && !currentLoop.IsCompleted) await Task.Delay(25);
        if (!current.HasLiveMeasurement || !current.HasCurrentFieldGuidance || SampleCapture.StreamFrames < 2) throw new InvalidOperationException("Streaming workflow failed");
        current.FieldNote = "Synthetic stationary window; not physical accuracy.";
        current.CheckStabilityCommand.Execute(null);
        while (!current.HasStabilityResult && !currentLoop.IsCompleted) await Task.Delay(25);
        if (!current.HasStabilityResult || current.HasFieldFailure) throw new InvalidOperationException("Field stability collection failed: " + current.FieldRecordingStatus);
        await ((IAsyncRelayCommand)current.MarkFieldSampleCommand).ExecuteAsync(null);
        if (current.HasFieldFailure || current.FieldSessionDirectory == null) throw new InvalidOperationException("Field sample was not saved");
        var review = FieldSessionReview.Review(current.FieldSessionDirectory, Path.Combine(directory, "field-review"));
        if (!review.Synthetic || review.ReferenceFrames != 2 || review.MeasurementFrames < 20 || review.MarkedSamples != 1
            || review.MaximumReplayDifferenceArcseconds > .000001 || review.Warnings.Any(w => w.Contains("replay failed")))
            throw new InvalidOperationException("Field journal did not replay its recorded measurements");
        File.WriteAllText(Path.Combine(directory, "field-session-path.txt"), current.FieldSessionDirectory);
        ((Expander)View.FindName("FieldValidationTools")).IsExpanded = true;
        Save(Path.Combine(directory, "ui-field-validation-1280.png"), 1280, 1000);
        ((Expander)View.FindName("FieldValidationTools")).IsExpanded = false;
        Save(Path.Combine(directory, "ui-streaming-1280.png"), 1280, 800);
        current.StopLiveCommand.Execute(null); await currentLoop;
        if (current.HasCurrentFieldGuidance || !current.IsMeasurementStale) throw new InvalidOperationException("Stopped streaming retained guidance");
        SampleCapture.FailStream = true;
        var fallbackLoop = ((IAsyncRelayCommand)current.StartLiveCommand).ExecuteAsync(null);
        for (int i = 0; i < 200 && current.UseStreaming && !fallbackLoop.IsCompleted; i++) await Task.Delay(10);
        if (current.UseStreaming || current.HasOperationError || fallbackLoop.IsCompleted) throw new InvalidOperationException("Streaming must fall back to ordinary exposures");
        current.StopLiveCommand.Execute(null); await fallbackLoop;
        SampleCapture.FailStream = false;
        current.UseStreaming = true; SampleCapture.UnsupportedStream = true;
        var unsupportedLoop = ((IAsyncRelayCommand)current.StartLiveCommand).ExecuteAsync(null);
        await Task.Delay(350);
        if (current.UseStreaming || current.HasOperationError || unsupportedLoop.IsCompleted) throw new InvalidOperationException("Unsupported streaming must use still captures");
        current.StopLiveCommand.Execute(null); await unsupportedLoop; SampleCapture.UnsupportedStream = false;
        current.UseStreaming = true; SampleCapture.StreamCleanupFailure = true;
        await ((IAsyncRelayCommand)current.StartLiveCommand).ExecuteAsync(null);
        SampleCapture.StreamCleanupFailure = false;
        if (!current.HasOperationError || current.IsWorking) throw new InvalidOperationException("Camera restoration failure must remain visible");
        current.Dispose();
        SampleCapture.Rotations = 0;
        SampleCapture.UnsupportedAuto = true;
        await SelectScene(0); current!.AutoRaRotation = true;
        if (current.CanUseAutoRotation || !current.CanUseStreaming) throw new InvalidOperationException("Unsupported mounts must disable only automatic RA rotation");
        await ((IAsyncRelayCommand)current.CaptureReferenceCommand).ExecuteAsync(null);
        if (current.AutoRaRotation || current.HasOperationError || current.WorkflowStep != 1 || SampleCapture.Rotations != 0)
            throw new InvalidOperationException("Unsupported RA must quietly fall back to manual reference capture");
        SampleCapture.UnsupportedAuto = false;
        await SelectScene(0);
        var settingsPanel = (StackPanel)View.FindName("RotationSettingsPanel");
        Save(Path.Combine(directory, "ui-manual-settings-1280.png"), 1280, 800);
        var manualHeight = settingsPanel.ActualHeight;
        if (settingsPanel.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Manual mode must collapse rotation settings");
        current!.AutoRaRotation = true;
        Save(Path.Combine(directory, "ui-auto-settings-1280.png"), 1280, 800);
        if (!settingsPanel.IsEnabled || settingsPanel.Visibility != Visibility.Visible || settingsPanel.ActualHeight < 30) throw new InvalidOperationException("Auto mode must reveal rotation settings");
        current.ReverseRaRotation = false;
        Save(Path.Combine(directory, "ui-east-settings-1280.png"), 1280, 800);
        current.ReverseRaRotation = true;
        Save(Path.Combine(directory, "ui-reversed-settings-1280.png"), 1280, 800);
        current.DecreaseRotationAngleCommand.Execute(null);
        if (current.RotationAngleDegrees != 80) throw new InvalidOperationException("Decrement must change angle by ten degrees");
        current.IncreaseRotationAngleCommand.Execute(null);
        if (current.RotationAngleDegrees != 90) throw new InvalidOperationException("Increment must restore ninety degrees");
        var automatic = ((IAsyncRelayCommand)current.CaptureReferenceCommand).ExecuteAsync(null);
        while (!current.IsRotating && !automatic.IsCompleted) await Task.Delay(10);
        await Task.Delay(300);
        Save(Path.Combine(directory, "ui-auto-ra-1280.png"), 1280, 1000);
        await automatic;
        if (current.WorkflowStep != 2 || current.HasOperationError || SampleCapture.Rotations != 1 || SampleCapture.LastRotationAngle != 90 || !SampleCapture.RotationStopped)
            throw new InvalidOperationException("Automatic RA must capture exactly two references around one stopped rotation");
        current.ResetLiveCommand.Execute(null);
        var interrupted = ((IAsyncRelayCommand)current.CaptureReferenceCommand).ExecuteAsync(null);
        while (!current.IsRotating && !interrupted.IsCompleted) await Task.Delay(10);
        current.StopLiveCommand.Execute(null); await interrupted;
        if (current.WorkflowStep != 0 || current.IsWorking || !SampleCapture.RotationStopped)
            throw new InvalidOperationException("Cancelled rotation must stop and discard the reference");
        current.Dispose();
        await SelectScene(0); current!.AutoRaRotation = true;
        SampleCapture.FailRotation = true;
        await ((IAsyncRelayCommand)current.CaptureReferenceCommand).ExecuteAsync(null);
        SampleCapture.FailRotation = false;
        if (!current.HasOperationError || current.AutoRaRotation || current.WorkflowStep != 0)
            throw new InvalidOperationException("A rotation fault must stop, disable auto and invalidate calibration");
        await SelectScene(0); current!.RecordFieldSession = false;
        await ((IAsyncRelayCommand)current.CaptureReferenceCommand).ExecuteAsync(null);
        await ((IAsyncRelayCommand)current.CaptureReferenceCommand).ExecuteAsync(null);
        if (!current.VerifyAlignmentCommand.CanExecute(null)) throw new InvalidOperationException("Verification must be ready without an extra confirmation switch");

        await ((IAsyncRelayCommand)current.VerifyAlignmentCommand).ExecuteAsync(null);
        if (current.HasOperationError || !current.HasVerificationResult || !current.HasStabilityResult || current.WorkflowStep != 2
            || current.FieldSessionDirectory != null)
            throw new InvalidOperationException("Verification and automatic stability must work without debug recording: " + current.OperationError);
        ((Expander)View.FindName("VerificationTools")).IsExpanded = true;
        Save(Path.Combine(directory, "ui-verification-1280.png"), 1280, 1000);
        current.AutoVerificationStability = false; current.VerificationReverse = true;
        await ((IAsyncRelayCommand)current.VerifyAlignmentCommand).ExecuteAsync(null);
        if (current.HasOperationError || !current.HasVerificationResult || current.HasStabilityResult)
            throw new InvalidOperationException("Optional stability must be skipped when off: " + current.OperationError);

        var cancelledVerify = ((IAsyncRelayCommand)current.VerifyAlignmentCommand).ExecuteAsync(null);
        while (!current.IsRotating && !cancelledVerify.IsCompleted) await Task.Delay(10);
        current.StopLiveCommand.Execute(null); await cancelledVerify;
        if (current.HasVerificationResult || current.WorkflowStep != 0 || !SampleCapture.RotationStopped)
            throw new InvalidOperationException("Cancelled verification must discard its reference and result");
        current.RecordFieldSession = true;
        await ((IAsyncRelayCommand)current.CaptureReferenceCommand).ExecuteAsync(null);
        await ((IAsyncRelayCommand)current.CaptureReferenceCommand).ExecuteAsync(null);
        var originalFieldDirectory = current.FieldSessionDirectory;

        await ((IAsyncRelayCommand)current.VerifyAlignmentCommand).ExecuteAsync(null);
        var verificationReview = FieldSessionReview.Review(current.FieldSessionDirectory!, Path.Combine(directory, "verification-review"));
        if (current.HasOperationError || !current.HasVerificationResult || current.FieldSessionDirectory == originalFieldDirectory
            || verificationReview.ReferenceFrames != 2 || verificationReview.MeasurementFrames != 1
            || verificationReview.MaximumReplayDifferenceArcseconds > 1e-6)
            throw new InvalidOperationException("Verification must keep an independent replayable journal: " + current.OperationError);
        current.Dispose();
        SampleCapture.DriftTestMode = true; SampleCapture.DriftSecondField = false;
        await SelectScene(0); current!.IndependentDriftEnabled = true;
        if (current.IndependentDriftCommand.CanExecute(null)) throw new InvalidOperationException("Unguided/stationary confirmation required");
        current.DriftConfirmed = true;
        int movesBeforeDrift = SampleCapture.Rotations;
        await ((IAsyncRelayCommand)current.IndependentDriftCommand).ExecuteAsync(null);
        if (current.HasOperationError || current.HasIndependentResult) throw new InvalidOperationException("First drift field alone cannot pass: " + current.OperationError);
        SampleCapture.DriftSecondField = true; current.DriftConfirmed = true;
        await ((IAsyncRelayCommand)current.IndependentDriftCommand).ExecuteAsync(null);
        if (current.HasOperationError || !current.HasIndependentResult || !current.HasValidationWarning || SampleCapture.Rotations != movesBeforeDrift)
            throw new InvalidOperationException("Large independent drift must recommend realignment without moving hardware: " + current.OperationError);
        ((Expander)View.FindName("IndependentDriftTools")).IsExpanded = true;
        Save(Path.Combine(directory, "ui-independent-drift-1280.png"), 1280, 1100);
        var driftReview = FieldSessionReview.Review(current.FieldSessionDirectory!, Path.Combine(directory, "independent-drift-review"));
        if (driftReview.IndependentDrift == null || driftReview.IndependentDrift.TotalArcseconds < 490
            || driftReview.Warnings.Any(w => w.Contains("replay failed"))) throw new InvalidOperationException("Independent drift journal did not replay");
        current.ResetIndependentDriftCommand.Execute(null); SampleCapture.DriftSecondField = false; SampleCapture.DriftFault = true;
        current.DriftConfirmed = true;
        await ((IAsyncRelayCommand)current.IndependentDriftCommand).ExecuteAsync(null);
        if (!current.HasOperationError || current.HasIndependentResult || !current.HasValidationWarning)
            throw new InvalidOperationException("Duplicate drift exposures must be inconclusive");
        SampleCapture.DriftFault = false; SampleCapture.DriftTestMode = false; current.Dispose();
        await SelectScene(2);
        current!.StopLiveCommand.Execute(null);
        if (currentLoop != null) await currentLoop;
        current.ValidationTargetArcseconds = 60;
        current.ScienceExposureSeconds = 300;
        var vmType = typeof(PolarAlignmentDockableVM);
        void SetQualityField(string name, object value) => vmType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(current, value);
        foreach (var quality in new[] { (30d, "Within", "#43C98F"), (60d, "Within", "#43C98F"),
            (90d, "Close", "#E9B85C"), (120d, "Close", "#E9B85C"), (150d, "Adjust", "#F07474") }) {
            SetQualityField("latestError", new Nina.LivePolarAlignment.Core.PolarError(quality.Item1 / 60, 0, quality.Item1 / 60));
            SetQualityField("measuredAt", DateTimeOffset.UtcNow);
            SetQualityField("measurementStale", false);
            SetQualityField("pixelScale", .7755557);
            vmType.GetMethod("NotifyLive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, null);
            Save(Path.Combine(directory, $"ui-quality-{quality.Item1:0}-1280.png"), 1280, 1000);
            var qualityIndicator = (TextBlock)View.FindName("AlignmentQualityIndicator");
            if (current.AlignmentQualityLevel != quality.Item2 || qualityIndicator.Text != current.AlignmentQualityText
                || ((SolidColorBrush)qualityIndicator.Foreground).Color != (Color)ColorConverter.ConvertFromString(quality.Item3))
                throw new InvalidOperationException("Quality color, threshold boundary or visible status binding failed");
            if (!current.EstimatedExposureMotion.Contains("px") || current.EstimatedUnguidedDrift == "—")
                throw new InvalidOperationException("Fresh measurements must show drift and pixel bounds");
        }
        SetQualityField("measurementStale", true);
        vmType.GetMethod("NotifyLive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, null);
        Save(Path.Combine(directory, "ui-quality-stale-1280.png"), 1280, 1000);
        if (current.AlignmentQualityLevel != "Unavailable" || current.EstimatedExposureMotion != "—")
            throw new InvalidOperationException("Paused data must not retain a success status or drift estimate");
        current.Dispose();
        await SelectScene(0); SampleCapture.Parked = true; current!.AutoRaRotation = true;
        int capturesBeforeUnpark = SampleCapture.CaptureCalls;
        var parkedStart = ((IAsyncRelayCommand)current.ActionButtonCommand).ExecuteAsync(null);
        await Task.Delay(30);
        if (SampleCapture.CaptureCalls != capturesBeforeUnpark || !current.IsWorking
            || current.ActionButtonLabel.Length > 12 || !current.LiveStatus.Contains("unparking"))
            throw new InvalidOperationException("Unpark must be shown in status before any reference capture");
        if (SampleStatus.Updates.LastOrDefault()?.Status != current.LiveStatus) throw new InvalidOperationException("Unpark progress must reach the NINA application status mediator");
        Save(Path.Combine(directory, "ui-unparking.png"), 1280, 1000);
        for (int i = 0; i < 300 && !current.HasLiveMeasurement && !parkedStart.IsCompleted; i++) await Task.Delay(25);
        if (!current.HasLiveMeasurement || SampleCapture.Parked) throw new InvalidOperationException("Parked Start must continue to alignment after Unpark");
        current.StopLiveCommand.Execute(null); await parkedStart;
        await SelectScene(0); SampleCapture.Parked = true; SampleCapture.FailUnpark = true; current!.AutoRaRotation = true;
        capturesBeforeUnpark = SampleCapture.CaptureCalls;
        await ((IAsyncRelayCommand)current.ActionButtonCommand).ExecuteAsync(null);
        if (!current.HasOperationError || SampleCapture.CaptureCalls != capturesBeforeUnpark) throw new InvalidOperationException("Failed Unpark must not start capture");
        SampleCapture.Parked = false; SampleCapture.FailUnpark = false;
        await SelectScene(0);
        var earlyPreviewCapture = ((IAsyncRelayCommand)current!.PrimaryActionCommand).ExecuteAsync(null);
        for (int i = 0; i < 50 && !current.HasPreview && !earlyPreviewCapture.IsCompleted; i++) await Task.Delay(10);
        if (!current.HasPreview || earlyPreviewCapture.IsCompleted || current.WorkflowStep != 0 || !current.IsMeasurementStale || current.HasCurrentFieldGuidance)
            throw new InvalidOperationException("Captured pixels must appear before plate solve without stale axis guidance");
        Save(Path.Combine(directory, "ui-before-solve.png"), 1280, 1000);
        await earlyPreviewCapture;
        await SelectScene(0);
        foreach (var unavailable in Enum.GetValues<AutoRaAvailability>().Where(x => x != AutoRaAvailability.Ready)) {
            SampleCapture.AutoUnavailable = unavailable;
            vmType.GetMethod("NotifyLive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, null);
            if (current!.CanUseAutoRotation || current.RotationStatus == "AutoRa" + unavailable || string.IsNullOrWhiteSpace(current.RotationStatus))
                throw new InvalidOperationException("Unavailable automatic rotation must show a specific translated reason");
        }
        SampleCapture.AutoUnavailable = null;
        await SelectScene(0);
        foreach (var disconnected in new[] { (true, false), (false, true), (true, true) }) {
            SampleCapture.CameraOffline = disconnected.Item1; SampleCapture.MountOffline = disconnected.Item2;
            vmType.GetMethod("NotifyLive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, null);
            Save(Path.Combine(directory, $"ui-equipment-{disconnected.Item1}-{disconnected.Item2}.png"), 1280, 900);
            if (current!.PrimaryActionCommand.CanExecute(null) == disconnected.Item1 || current.EquipmentReady == disconnected.Item1
                || ((FrameworkElement)View.FindName("CameraConnectionHint")).Visibility != (disconnected.Item1 ? Visibility.Visible : Visibility.Collapsed)
                || ((FrameworkElement)View.FindName("MountConnectionHint")).Visibility != (disconnected.Item2 ? Visibility.Visible : Visibility.Collapsed)
                || (disconnected.Item2 && current.CanUseAutoRotation))
                throw new InvalidOperationException("Compact camera and mount indicators or independent gating failed");
            Save(Path.Combine(directory, $"ui-equipment-narrow-{disconnected.Item1}-{disconnected.Item2}.png"), 360, 1000);
        }
        SampleCapture.CameraOffline = false; SampleCapture.MountOffline = true;
        vmType.GetMethod("NotifyLive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, null);
        await ((IAsyncRelayCommand)current!.PrimaryActionCommand).ExecuteAsync(null);
        if (!current.WaitingForManualRotation || current.WorkflowStep != 1 || current.IsWorking)
            throw new InvalidOperationException("Manual Start must explicitly wait for RA rotation");
        Save(Path.Combine(directory, "ui-manual-wait.png"), 1280, 1000);
        var manualSequence = ((IAsyncRelayCommand)current.PrimaryActionCommand).ExecuteAsync(null);
        for (int i = 0; i < 200 && !current.HasLiveMeasurement && !manualSequence.IsCompleted; i++) await Task.Delay(25);
        if (!current.HasLiveMeasurement || !current.IsWorking) throw new InvalidOperationException("Second reference must enter live measurement automatically");
        current.StopLiveCommand.Execute(null); await manualSequence;
        SampleCapture.MountOffline = false;
        await SelectScene(0); current!.AutoRaRotation = true;
        var autoSequence = ((IAsyncRelayCommand)current.PrimaryActionCommand).ExecuteAsync(null);
        for (int i = 0; i < 300 && !current.HasLiveMeasurement && !autoSequence.IsCompleted; i++) await Task.Delay(25);
        if (!current.HasLiveMeasurement || current.WorkflowStep != 2 || !current.IsWorking)
            throw new InvalidOperationException("One Start must run references, RA rotation and live measurement");
        AssertActionButton(true);
        Save(Path.Combine(directory, "ui-primary-stop.png"), 1280, 1000);
        ((Button)View.FindName("PrimaryActionButton")).Command.Execute(null);
        Save(Path.Combine(directory, "ui-primary-stopping.png"), 1280, 1000);
        await autoSequence; AssertActionButton(false);
        var resumeSequence = ((IAsyncRelayCommand)current.PrimaryActionCommand).ExecuteAsync(null);
        for (int i = 0; i < 100 && !current.IsWorking && !resumeSequence.IsCompleted; i++) await Task.Delay(25);
        await Task.Delay(150);
        if (!current.IsWorking || resumeSequence.IsCompleted) throw new InvalidOperationException("Resume must restart live measurement after Stop");
        SampleCapture.MountOffline = true;
        await Task.Delay(300);
        if (resumeSequence.IsCompleted || current.HasOperationError) throw new InvalidOperationException("Live image measurement must work without a mount connection");
        SampleCapture.CameraOffline = true;
        for (int i = 0; i < 100 && !resumeSequence.IsCompleted; i++) await Task.Delay(25);
        if (!resumeSequence.IsCompleted || !current.HasOperationError || current.PrimaryActionCommand.CanExecute(null))
            throw new InvalidOperationException("Losing the camera must stop measurements and prevent restart");
        await resumeSequence; SampleCapture.MountOffline = false; SampleCapture.CameraOffline = false;
        await SelectScene(0); current!.AutoRaRotation = true;
        var cancelSequence = ((IAsyncRelayCommand)current.PrimaryActionCommand).ExecuteAsync(null);
        while (!current.IsRotating && !cancelSequence.IsCompleted) await Task.Delay(10);
        AssertActionButton(true);
        if (SampleStatus.Updates.LastOrDefault()?.Source != "Live Polar Alignment" || SampleStatus.Updates.Last().Status != current.LiveStatus)
            throw new InvalidOperationException("RA progress must reach NINA's status bar with a stable source");
        Save(Path.Combine(directory, "ui-primary-cancel.png"), 1280, 1000);
        ((Button)View.FindName("PrimaryActionButton")).Command.Execute(null);
        await cancelSequence; AssertActionButton(false);
        if (SampleStatus.Updates.Last().Status != string.Empty) throw new InvalidOperationException("The host status entry must clear after cancellation");
        if (current.HasLiveMeasurement || current.IsWorking) throw new InvalidOperationException("Stop must not restart workflow");
        foreach (var schema in Theme.ColorSchemas.Items) {
            Theme.ColorSchema = schema;
            await Task.Delay(50);
            Save(Path.Combine(directory, $"ui-theme-{schema.Name}.png"), 1280, 1000);
            AssertActionButton(false);
            var inputLabel = (TextBlock)View.FindName("ExposureLabel");
            var explanation = (TextBlock)View.FindName("ImpactExplanation");
            var resultHeading = (TextBlock)View.FindName("PolarErrorHeading");
            if (!(explanation.FontSize < inputLabel.FontSize && inputLabel.FontSize < resultHeading.FontSize)
                || !(explanation.FontWeight.ToOpenTypeWeight() < inputLabel.FontWeight.ToOpenTypeWeight()
                    && inputLabel.FontWeight.ToOpenTypeWeight() < resultHeading.FontWeight.ToOpenTypeWeight()))
                throw new InvalidOperationException("Headings, input labels and supporting explanations must have distinct typography");
            foreach (var name in new[] { "AutoRaOptionLabel", "ExposureLabel" })
                if (((SolidColorBrush)((TextBlock)View.FindName(name)).Foreground).Color != schema.PrimaryColor)
                    throw new InvalidOperationException($"{name} must follow the NINA text color in {schema.Name}");
        }
        current.Dispose();
        if (SampleStatus.Updates.Last().Status != string.Empty) throw new InvalidOperationException("The host status entry must clear on disposal");
        File.WriteAllText(Path.Combine(directory, "preview-verification.txt"), "Unified play/stop/cancel button, visible surfaces and theme foreground; equipment gating and labels; one-Start workflow and manual wait; Stop cancellation; all built-in themes; quality thresholds; options binding; three workflow stages; layout and localization; streaming and failure; field journal and replay; automatic RA controls, two-reference sequence and cancellation; independent RA remeasurement with optional stability, recording off/on, verification cancellation and journal replay; independent drift with no mount motion, realignment warning, duplicate rejection and offline drift replay passed. Synthetic inputs only.");
    }

    private static void AssertActionButton(bool working) {
        View.UpdateLayout();
        var button = (Button)View.FindName("PrimaryActionButton");
        var label = (TextBlock)View.FindName("PrimaryActionText");
        var icon = (System.Windows.Shapes.Path)View.FindName("PrimaryActionIcon");
        var surface = (Border)button.Template.FindName("ButtonSurface", button);
        if (current!.IsWorking != working || button.Command != current.ActionButtonCommand
            || label.Text != current.ActionButtonLabel || surface.BorderThickness.Left < 1
            || !ReferenceEquals(icon.Data, View.FindResource(working ? "StopSVG" : "PlaySVG"))
            || ((SolidColorBrush)label.Foreground).Color != ((SolidColorBrush)button.Foreground).Color
            || ((SolidColorBrush)icon.Fill).Color != ((SolidColorBrush)button.Foreground).Color)
            throw new InvalidOperationException("Unified play/stop command, icon, label, surface or theme foreground failed");
        if (working && !current.IsStopping && (!button.IsEnabled || button.Command != current.StopLiveCommand))
            throw new InvalidOperationException("The same action button must remain enabled to stop or cancel any running operation");
    }

    private static void Save(string path, int width, int height) {
        View.Width = width; View.Height = height;
        View.Measure(new Size(width, height)); View.Arrange(new Rect(0, 0, width, height)); View.UpdateLayout();
        void RejectPanelProgress(DependencyObject node) {
            if (node is TextBlock text) {
                var path = System.Windows.Data.BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)?.ParentBinding.Path?.Path;
                if (path is "LiveStatus" or "WorkflowTitle" or "WorkflowDescription")
                    throw new InvalidOperationException("Operation progress must not be repeated in the plugin control panel");
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) RejectPanelProgress(VisualTreeHelper.GetChild(node, i));
        }
        RejectPanelProgress(View);
        var field = (FrameworkElement)View.FindName("SkyField");
        var placeholder = (FrameworkElement)View.FindName("EmptyImagePlaceholder");
        if ((placeholder.Visibility == Visibility.Visible) == current!.HasPreview)
            throw new InvalidOperationException("Empty placeholder must only show without an image");
        var controls = (FrameworkElement)View.FindName("ControlScroll");
        var detailPanel = (FrameworkElement)View.FindName("AlignmentDetailPanel");
        var detail = (PolarFieldView)View.FindName("AlignmentDetailView");
        if (current!.HasAlignmentDetail) {
            if (detailPanel.Visibility != Visibility.Visible || detail.ActualHeight < 150 || !detail.DetailMode
                || !ReferenceEquals(detail.Preview, ((PolarFieldView)field).Preview)
                || !ReferenceEquals(detail.Guidance, ((PolarFieldView)field).Guidance))
                throw new InvalidOperationException("Alignment close-up must use the current full-image pixels and guidance");
        } else if (detailPanel.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Close-up must disappear without a current measured image");
        if (width >= 900) {
            if (field.ActualWidth < width * .6 || field.ActualHeight < height * .65
                || controls.TranslatePoint(new Point(), View).X < width * .6)
                throw new InvalidOperationException("The wide layout did not prioritize the left image and right controls.");
        } else if (controls.TranslatePoint(new Point(), View).Y < field.TranslatePoint(new Point(0, field.ActualHeight), View).Y)
            throw new InvalidOperationException("Narrow controls overlap the image.");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(View);
        if (current!.HasAlignmentDetail && detail.Guidance?.CelestialPole is { } pole) {
            var region = detail.VisibleImageRegion;
            if (Math.Abs(region.Left + region.Width / 2 - pole.X) > .001
                || Math.Abs(region.Top + region.Height / 2 - pole.Y) > .001
                || detail.ImageScale < 8 || detail.ImageScale + .001 < ((PolarFieldView)field).ImageScale * 2)
                throw new InvalidOperationException("Close-up must center the pole and magnify at least twice the main view");
        }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
