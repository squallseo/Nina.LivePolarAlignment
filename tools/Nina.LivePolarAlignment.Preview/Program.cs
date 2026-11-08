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

public class SampleDockHost : DispatchProxy {
    public List<NINA.Equipment.Interfaces.ViewModel.IDockableVM> Tools=new(),All=new();
    protected override object? Invoke(MethodInfo? method,object?[]? args)=>method?.Name switch {
        "get_AnchorableTools"=>Tools,"get_Anchorables"=>All,_=>null
    };
}

internal sealed class SampleCapture : IPolarAlignmentCaptureService {
    public static bool SupportHome=true;
    public static bool AlreadyHome;
    public bool MountAtHome=>AlreadyHome && !MountOffline;
    public static int HomeCalls;
    public bool CanHomeForRestart=>SupportHome && !MountOffline;
    public async Task HomeForRestartAsync(CancellationToken token){if(PreviewOwners!=0)throw new InvalidOperationException("Home started before preview stopped");await Task.Delay(40,token);HomeCalls++;orientation=0;}
    public int CameraGain {get;private set;}=5;
    public bool TryQueueLiveExposure(double seconds)=>PreviewOwners==1;
    public static bool CameraOffline, MountOffline;
    public bool PoleMasterMode { get; set; }
    public double PreviewStretchStrength { get; set; }=1;
    public Task<BitmapSource?> RefreshPreviewAsync(CancellationToken token)=>Task.FromResult<BitmapSource?>(Image);
    public event Action<StreamingPreview>? StreamingPreviewAvailable;
    public static int PreviewStarts,PreviewFrames,PreviewStops,PreviewOwners,MaximumPreviewOwners;
    public static bool FailPreviewCleanup;
    public async Task RunPreviewAsync(double exposureSeconds,CancellationToken token) {
        PreviewStarts++;PreviewOwners++;MaximumPreviewOwners=Math.Max(MaximumPreviewOwners,PreviewOwners);
        try {
            while(true) {await Task.Delay(40,token);PreviewFrames++;StreamingPreviewAvailable?.Invoke(new(Image,DateTimeOffset.UtcNow,string.Empty,IsAimingPreview:true,Stars:Overlay(false)));}
        } finally {
            await Task.Delay(80);StreamingPreviewAvailable?.Invoke(new(Image,DateTimeOffset.UtcNow,string.Empty,IsAimingPreview:true,Stars:Overlay(false)));PreviewOwners--;PreviewStops++;
            if(FailPreviewCleanup)throw new StreamingCleanupException(new InvalidOperationException("Synthetic preview restoration failure"));
        }
    }
    public bool CameraConnected => !CameraOffline;
    public bool MountConnected => !MountOffline;
    public Func<int> Step { get; set; } = () => 0;
    public event Action<BitmapSource>? PreviewAvailable;
    public static AutoRaAvailability? AutoUnavailable;
    public static bool Parked, FailUnpark;
    public static bool FailInitialPattern;
    public static AlignmentCameraCapabilities? MethodCamera;
    public AlignmentCameraCapabilities? AlignmentCamera=>MethodCamera;
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
    public static bool UnsupportedAuto, FailRotation, FailSecondSolveOnce;
    public static Action? AfterPreview;
    public bool SupportsAutoRaRotation => MountConnected && !UnsupportedAuto && AutoUnavailable == null && !Parked;
    public static int Rotations;
    public static double LastRotationAngle;
    public static bool RotationStopped;
    private double orientation;
    private bool rotationVideo;
    public static bool DemoSlow;

    private double returnOrientation;
    public static int Returns;
    public static bool FailReturn;
    public async Task ReturnRaToStartAsync(IProgress<double>? progress, CancellationToken token) {
        Returns++; RotationStopped = false;
        try {
            if (FailReturn) throw new InvalidOperationException("Synthetic RA return failure");
            double from=orientation,target=PoleMasterMode?0:returnOrientation;
            int distance=(int)Math.Ceiling(Math.Abs(from-target));
            for (int i = 1; i <= Math.Max(1,distance); i++) {
                await Task.Delay(DemoSlow?65:10, token);progress?.Report(i);
                orientation=from+(target-from)*i/Math.Max(1,distance);
                if(rotationVideo)PublishRotationFrame();
            }
            orientation = target;
        } finally { RotationStopped = true; }
    }
    private bool rotatedSinceReference;
    public static bool DriftTestMode, DriftSecondField, DriftFault;
    private bool driftWasSecond;
    private int driftIndex;
    private readonly DateTimeOffset driftStart = DateTimeOffset.UtcNow;
    public object? DriftMountIdentity => this;
    public void ValidateDriftTracking() { if (!DriftTestMode) throw new InvalidOperationException("Synthetic drift mode is off"); }
    public Nina.LivePolarAlignment.Core.DriftSample GetDriftSample(SolvedPolarFrame frame) =>
        new(frame.CapturedAtUtc, frame.DeclinationDegrees, frame.RightAscensionDegrees - 180, 40, frame.PixelScaleArcseconds);
    public async Task ContinueRaAsync(bool reverse,IProgress<double>? progress,CancellationToken token,double targetDegrees=40) {
        double saved=returnOrientation;
        try { await RotateRaAsync(reverse,progress,token,targetDegrees); }
        finally { returnOrientation=saved; }
    }
    public async Task RotateRaAsync(bool reverse, IProgress<double>? progress, CancellationToken token, double targetDegrees = 90) {
        if(PreviewOwners!=0)throw new InvalidOperationException("RA moved before preview drained");
        Rotations++; LastRotationAngle = targetDegrees; RotationStopped = false;
        returnOrientation = orientation;
        try {
            if (FailRotation) throw new InvalidOperationException("Synthetic rotation failure");
            double initial=orientation;
            for (int i = 1; i <= targetDegrees; i++) {
                await Task.Delay(DemoSlow?90:20, token);progress?.Report(i);
                orientation=initial+(reverse?-i:i);if(rotationVideo)PublishRotationFrame();
            }
            rotatedSinceReference = true;
        }
        finally { RotationStopped = true; }
    }
    public async Task RotateRaWithPreviewAsync(bool reverse,bool continuation,double exposureSeconds,IProgress<double>? progress,CancellationToken token,double targetDegrees=40) {
        rotationVideo=true;
        try {if(continuation)await ContinueRaAsync(reverse,progress,token,targetDegrees);else await RotateRaAsync(reverse,progress,token,targetDegrees);}
        finally {rotationVideo=false;}
    }
    public async Task ReturnRaWithPreviewAsync(double exposureSeconds,IProgress<double>? progress,CancellationToken token) {
        rotationVideo=true;try{await ReturnRaToStartAsync(progress,token);}finally{rotationVideo=false;}
    }
    private void PublishRotationFrame()=>StreamingPreviewAvailable?.Invoke(new(Image,DateTimeOffset.UtcNow,string.Empty,IsRotationPreview:true,Stars:Overlay(true)));
    private StarOverlay Overlay(bool verified) {
        var random=new Random(21);var points=new List<Core.TrackedStar>();
        double angle=orientation*Math.PI/180,c=Math.Cos(angle),s=Math.Sin(angle);
        for(int i=0;i<140;i++) {
            double x=random.NextDouble()*800,y=random.NextDouble()*600;random.NextDouble();
            double xx=400+c*(x-400)-s*(y-300),yy=300+s*(x-400)+c*(y-300);
            if(i<25 && xx>=0 && yy>=0 && xx<800 && yy<600)points.Add(new(i+1,new(xx,yy)));
        }
        return new(points.ToArray(),800,600,verified,DateTimeOffset.UtcNow);
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
            yield return new(0, 89.7, orientation, 6, 800, 600, false, DateTimeOffset.UtcNow, Image,Stars:Overlay(true));
        }
    }
    public BitmapSource Image => MakeStars(orientation,PreviewStretchStrength);
    public async Task<SolvedPolarFrame> CaptureAndSolveAsync(double exposureSeconds, CancellationToken token, IProgress<ApplicationStatus>? progress = null) {
        if(PreviewOwners!=0)throw new InvalidOperationException("Snapshot started before preview drained");
        CaptureCalls++;
        if(FailInitialPattern)throw new PolarisIdentificationException(new InvalidOperationException("Synthetic pattern not identified."));
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
        AfterPreview?.Invoke();
        await Task.Delay(500, token);
        if (Step() == 1 && FailSecondSolveOnce) {
            FailSecondSolveOnce = false;
            throw new InvalidOperationException("Synthetic second-reference solve failure");
        }
        int step = Step();
        if (step == 0) rotatedSinceReference = false;
        if (!PoleMasterMode && step == 1 && !rotatedSinceReference) { orientation += 72; rotatedSinceReference = true; }
        return new(0, step == 2 ? 89.7 : 89.6, orientation, 6, 800, 600, false, DateTimeOffset.UtcNow, Image,Stars:Overlay(false));
    }
    private static BitmapSource MakeStars(double degrees=0,double strength=1) {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)Math.Clamp(8*strength,0,255),(byte)Math.Clamp(15*strength,0,255),(byte)Math.Clamp(27*strength,0,255))), null, new Rect(0, 0, 800, 600));
            var random = new Random(21);
            for (int i = 0; i < 140; i++) {
                double x=random.NextDouble()*800-400,y=random.NextDouble()*600-300,angle=degrees*Math.PI/180;
                var point = new Point(400+Math.Cos(angle)*x-Math.Sin(angle)*y,300+Math.Sin(angle)*x+Math.Cos(angle)*y);
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
    private static int SessionGain=>((IPolarAlignmentCaptureService)typeof(PolarAlignmentDockableVM).GetField("captureService",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(current)!).CameraGain;
    private static Task? currentLoop;
    private static PolarAlignmentView View = null!;
    private static PolarAdvancedSettingsView? advancedPreview;
    private static PolarAdvancedSettingsView AdvancedPreview {
        get {
            if(advancedPreview==null || !ReferenceEquals(advancedPreview.DataContext,current))advancedPreview=new(){DataContext=current};
            advancedPreview.Measure(new Size(620,1600));advancedPreview.Arrange(new Rect(0,0,620,1600));advancedPreview.UpdateLayout();
            return advancedPreview;
        }
    }
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
        if (args.Length == 2 && args[0] == "--render-markers-to") {
            RenderMarkers(Path.GetFullPath(args[1]));
            return;
        }
        View = new PolarAlignmentView();
        var layout = new DockPanel();
        var header = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(34, 44, 60)) };
        header.Children.Add(new TextBlock {
            Text = args.Length==1 && args[0]=="--polemaster-demo" ? "가상 데모 · 실제 장비 제어 없음 · 오른쪽 시작 버튼을 눌러 보세요" : "합성 샘플 미리보기 · 카메라/적도의 연결 없음",
            Foreground = new SolidColorBrush(Color.FromRgb(240, 186, 114)), Margin = new Thickness(14, 11, 14, 7)
        });
        foreach (var item in new[] { ("1. 첫 화면", 0), ("2. RA 회전", 1), ("3. 조정 화면", 2) }) {
            var button = new Button { Content = item.Item1, Foreground = Brushes.White, Margin = new Thickness(4, 0, 4, 9), Padding = new Thickness(10, 5, 10, 5) };
            button.Click += async (_, _) => await SelectScene(item.Item2);
            SceneButtons.Children.Add(button);
        }
        if(args.Length==1 && args[0]=="--polemaster-demo") {
            SceneButtons.Children.Clear();
            var restart=new Button {Content="데모 다시 시작",Margin=new Thickness(14,0,14,8),Padding=new Thickness(10,5,10,5)};
            restart.Click+=async(_,_)=> {
                current?.StopLiveCommand.Execute(null);
                if(current?.PrimaryActionCommand is IAsyncRelayCommand action && action.ExecutionTask is { } task)await task;
                await SelectScene(0);current!.PoleMasterMode=true;
            };
            SceneButtons.Children.Add(restart);
        }
        header.Children.Add(SceneButtons);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header); layout.Children.Add(View);
        var window = new Window {
            Title = args.Length==1 && args[0]=="--polemaster-demo" ? "폴마스터 자동 정렬 데모 · 가상 장비" : "극축 정렬 · 넓은 화면 미리보기", Content = layout, Width = 1440,
            Height = Math.Min(960, SystemParameters.WorkArea.Height - 45), MinWidth = 380, MinHeight = 650,
            WindowState = WindowState.Maximized,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = View.Background
        };
        window.Closed += (_, _) => current?.Dispose();
        application.Dispatcher.InvokeAsync(async () => {
            try {
                if (args.Length == 2 && args[0] == "--render-polemaster-to") {
                    await VerifyPoleMaster(Path.GetFullPath(args[1])); application.Shutdown();
                } else if (args.Length == 2 && args[0] == "--render-adjustment-to") {
                    await RenderAdjustmentSamples(Path.GetFullPath(args[1])); application.Shutdown();
                } else if (args.Length == 2 && args[0] == "--render-to") {
                    await VerifyAndRender(Path.GetFullPath(args[1])); application.Shutdown();
                } else if(args.Length==1 && args[0]=="--polemaster-demo") {
                    SampleProfile.Language="ko-KR";NINA.Core.Locale.Loc.Instance.ReloadLocale("ko-KR");SampleCapture.DemoSlow=true;
                    await SelectScene(0);current!.PoleMasterMode=true;
                } else await SelectScene(2);
            } catch (Exception error) {
                if (args.Length == 2 && (args[0] == "--render-to" || args[0] == "--render-adjustment-to" || args[0] == "--render-polemaster-to")) {
                    File.WriteAllText(Path.Combine(Path.GetFullPath(args[1]), "preview-error.txt"), error.ToString());
                    Environment.ExitCode = 1; application.Shutdown();
                } else MessageBox.Show(window, error.ToString(), "샘플을 열지 못했습니다");
            }
        });
        if (args.Length == 2 && (args[0] == "--render-to" || args[0] == "--render-adjustment-to" || args[0] == "--render-polemaster-to")) application.Run();
        else application.Run(window);
    }

    private static async Task VerifyPoleMaster(string directory) {
        Directory.CreateDirectory(directory);
        SampleProfile.Language="en-US";NINA.Core.Locale.Loc.Instance.ReloadLocale("en-US");
        await SelectScene(0);
        if(current!.CaptureExposureValue!=current.CaptureExposureSeconds*1000 || current.CaptureExposureLabel!=UiText.Get("CaptureExposureMs"))throw new InvalidOperationException("General camera exposure must also use milliseconds.");
        current!.AutoRaRotation=true;current.UseStreaming=true;
        SampleCapture.SupportHome=true;
        current.PoleMasterMode=true;
        if(current.RotationAngleDegrees!=80 || current.StreamingBinning!=1 || current.ValidationTargetArcseconds!=30 || current.ReverseRaRotation || current.VerificationReverse)
            throw new InvalidOperationException("PoleMaster preset defaults are wrong");
        int initialRotations=SampleCapture.Rotations,initialCaptures=SampleCapture.CaptureCalls;
        for(int i=0;i<200 && SampleCapture.PreviewFrames<3;i++)await Task.Delay(10);
        if(!current.PrimaryActionCommand.CanExecute(null))throw new InvalidOperationException("Start must allow automatic Polaris identification without a click.");
        var methodSelector = (ComboBox)View.FindName("AlignmentMethodSelector");
        foreach(var language in new[]{"ko-KR","en-US","ko-KR","en-US"}) {
            SampleProfile.Language=language;UiText.Refresh();await Task.Delay(20);
            if(methodSelector.SelectedIndex!=0 || methodSelector.Items.Count!=2 || ((ComboBoxItem)methodSelector.SelectedItem).Content?.ToString()!=UiText.Get("MethodPoleMaster") || !current.PrimaryActionCommand.CanExecute(null))
                throw new InvalidOperationException("Fixed Polaris workflow or its localized label changed during localization.");
        }
        await current.HomeRestartCommand.ExecuteAsync(null);
        int homesBeforePatternFailure=SampleCapture.HomeCalls;
        SampleCapture.FailInitialPattern=true;
        await ((IAsyncRelayCommand)current.PrimaryActionCommand).ExecuteAsync(null);
        SampleCapture.FailInitialPattern=false;
        if(!current.HasOperationError || current.OperatorState!="SelectPolaris" || current.PrimaryActionCommand.CanExecute(null)
            || SampleCapture.Rotations!=initialRotations || SampleCapture.HomeCalls!=homesBeforePatternFailure || current.CaptureSettingsLocked)
            throw new InvalidOperationException("Automatic identification failure must request a click without rotating, homing or retaining the exposure lock.");
        initialCaptures=SampleCapture.CaptureCalls;
        for(int i=0;i<100 && !current.HasPreview;i++)await Task.Delay(10);
        current.SelectPolarisCandidate(.6,.4);
        for(int i=0;i<100 && !current.IsPoleMasterPreviewing;i++)await Task.Delay(10);
        if(!current.IsPoleMasterPreviewing || !current.HasPreview || current.IsWorking || !current.PrimaryActionCommand.CanExecute(null)
            || current.OperatorState!="PolePreview" || SampleCapture.Rotations!=initialRotations || SampleCapture.CaptureCalls!=initialCaptures)
            throw new InvalidOperationException("PoleMaster ON did not start motion-free aiming preview");
        current.PreviewStretchStrength=2;current.ResetPreviewStretchCommand.Execute(null);
        if(current.PreviewStretchStrength!=1)throw new InvalidOperationException("Stretch reset failed");
        Save(Path.Combine(directory,"ui-polemaster-aiming-1280.png"),1280,800);
        var methodBottom=methodSelector.TranslatePoint(new Point(0,methodSelector.ActualHeight),View).Y;
        var startTop=((Button)View.FindName("PrimaryActionButton")).TranslatePoint(new Point(),View).Y;
        var stagesTop=((ItemsControl)View.FindName("WorkflowStages")).TranslatePoint(new Point(),View).Y;
        if(startTop<methodBottom || startTop>=stagesTop)throw new InvalidOperationException("Start must immediately follow the method selector, before workflow guidance.");
        var optionsProfiles=(IProfileService)typeof(PolarAlignmentDockableVM).GetField("profiles",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(current)!;
        int optionsSessionLoads=0,optionsCaptureCalls=SampleCapture.CaptureCalls,optionsPreviewStarts=SampleCapture.PreviewStarts;
        var pluginOptions=new Plugin(optionsProfiles){AlignmentSession=new Lazy<PolarAlignmentDockableVM>(()=>{optionsSessionLoads++;return current;})};
        if(optionsSessionLoads!=0)throw new InvalidOperationException("Manifest creation eagerly loaded an alignment session.");
        var optionsTemplate=(DataTemplate)new PolarAlignmentTemplates()["Live Polar Alignment_Options"];
        var advanced=(PolarAdvancedSettingsView)optionsTemplate.LoadContent();
        var optionsHost=new Border{DataContext=pluginOptions,Child=advanced,Background=System.Windows.Application.Current.FindResource("BackgroundBrush") as Brush};
        await Task.Delay(100);
        optionsHost.Measure(new Size(620,1000));optionsHost.Arrange(new Rect(0,0,620,1000));optionsHost.UpdateLayout();
        if(!ReferenceEquals(advanced.DataContext,current) || optionsSessionLoads!=1 || advanced.FindName("FieldValidationTools")==null
            || View.FindName("FieldValidationTools")!=null || View.FindName("AcquisitionSettings")!=null
            || SampleCapture.CaptureCalls!=optionsCaptureCalls || SampleCapture.PreviewStarts!=optionsPreviewStarts)
            throw new InvalidOperationException("Plugin options must share the existing session without duplicating tools or starting captures.");
        var optionsBitmap=new RenderTargetBitmap(620,1000,96,96,PixelFormats.Pbgra32);optionsBitmap.Render(optionsHost);
        var optionsEncoder=new PngBitmapEncoder();optionsEncoder.Frames.Add(BitmapFrame.Create(optionsBitmap));
        using(var optionsFile=File.Create(Path.Combine(directory,"ui-plugin-options.png")))optionsEncoder.Save(optionsFile);
        double initialExposure=current.StreamingExposureSeconds;
        current.DecreaseExposureCommand.Execute(null);
        if(current.StreamingExposureSeconds>=initialExposure || current.ExposureSeconds!=current.StreamingExposureSeconds)throw new InvalidOperationException("Exposure step buttons did not update both live and reference exposures.");
        current.IncreaseExposureCommand.Execute(null);
        if(View.FindName("CameraGainInput")!=null)throw new InvalidOperationException("Gain belongs in NINA equipment, not the plugin.");
        current.SelectPolarisCandidate(.6,.4);
        if(current.PolarisCandidate==null)throw new InvalidOperationException("Polaris candidate selection failed.");
        await current.TogglePreviewLoopCommand.ExecuteAsync(null);
        await Task.Delay(1100);
        if(current.IsPreviewLoopActive || current.IsPoleMasterPreviewing || SampleCapture.PreviewOwners!=0 || current.PreviewLoopLabel!="Start looping")
            throw new InvalidOperationException("Loop stop did not drain the camera or stay paused.");
        current.StreamingExposureSeconds=.3;
        if(current.IsPreviewLoopActive)throw new InvalidOperationException("Exposure change restarted a manually stopped loop.");
        await current.TogglePreviewLoopCommand.ExecuteAsync(null);
        for(int i=0;i<200 && !current.IsPoleMasterPreviewing;i++)await Task.Delay(10);
        if(!current.IsPreviewLoopActive || !current.IsPoleMasterPreviewing || current.PreviewLoopLabel!="Stop looping"
            || SampleCapture.Rotations!=initialRotations || SampleCapture.CaptureCalls!=initialCaptures)
            throw new InvalidOperationException("Loop restart failed or moved the mount.");
        for(int i=0;i<3;i++){current.PoleMasterMode=false;current.PoleMasterMode=true;}
        await Task.Delay(250);
        if(methodSelector.SelectedIndex!=0 || methodSelector.Items.Count!=2 || ((ComboBoxItem)methodSelector.SelectedItem).Content?.ToString()!=UiText.Get("MethodPoleMaster") || !current.PrimaryActionCommand.CanExecute(null))throw new InvalidOperationException("PoleMaster must return to the fixed pattern and a new Polaris click.");
        if(SampleCapture.MaximumPreviewOwners!=1)throw new InvalidOperationException("Overlapping preview owners");
        int poleRotations=SampleCapture.Rotations,poleReturns=SampleCapture.Returns;
        await current.HomeRestartCommand.ExecuteAsync(null);
        current.SelectPolarisCandidate(.6,.4);
        var seenStages=new List<int>();
        System.ComponentModel.PropertyChangedEventHandler stageObserver=(_,e)=>{
            if(e.PropertyName!=nameof(current.PoleMasterSteps))return;
            var steps=current.PoleMasterSteps;
            if(steps.Count!=6 || steps.Count(s=>s.IsCurrent)!=1 || steps.Where((s,i)=>s.IsCompleted!=(i<current.CurrentPoleMasterStage)).Any())
                throw new InvalidOperationException("Progress rail must have exactly one current stage and ordered completed stages.");
            if(seenStages.Count==0 || seenStages[^1]!=current.CurrentPoleMasterStage)seenStages.Add(current.CurrentPoleMasterStage);
        };
        current.PropertyChanged+=stageObserver;
        var poleWorkflow=((IAsyncRelayCommand)current.PrimaryActionCommand).ExecuteAsync(null);
        for(int i=0;i<200 && !current.IsRotating && !poleWorkflow.IsCompleted;i++)await Task.Delay(10);
        await Task.Delay(90);
        var beforeBoxes=current.DisplayStars;var beforeImage=current.LivePreview;
        await Task.Delay(90);
        var afterBoxes=current.DisplayStars;
        if(!current.IsRotating || beforeBoxes?.Verified!=true || afterBoxes?.Verified!=true || !current.HasFreshStars
            || ReferenceEquals(beforeImage,current.LivePreview) || !beforeBoxes.Markers.Any(a=>afterBoxes.Markers.Any(b=>a.Id==b.Id && a.Position!=b.Position)))
            throw new InvalidOperationException("Rotation video and measured star boxes did not advance together");
        Save(Path.Combine(directory,"ui-polemaster-rotation-starboxes-1280.png"),1280,800);
        for(int i=0;i<600 && !current.HasLiveMeasurement && !poleWorkflow.IsCompleted;i++) await Task.Delay(25);
        if(!current.HasLiveMeasurement || current.HasOperationError || SampleCapture.Rotations-poleRotations!=2 || SampleCapture.Returns-poleReturns!=1)
            throw new InvalidOperationException("PoleMaster three-location workflow failed: "+current.OperationError);
        for(int i=0;i<200 && (SampleCapture.StreamFrames<2 || current.DisplayStars?.Verified!=true) && !poleWorkflow.IsCompleted;i++)await Task.Delay(10);
        if(current.DisplayStars?.Verified!=true || SampleCapture.StreamFrames<2)throw new InvalidOperationException("Live tracked boxes did not start after return");
        if(!current.PoleMasterProgress.Contains("Three-location")) throw new InvalidOperationException("PoleMaster consistency is missing");
        View.UpdateLayout(); Save(Path.Combine(directory,"ui-polemaster-adjustment-1280.png"),1280,800);
        current.PropertyChanged-=stageObserver;
        if(!seenStages.SequenceEqual(new[]{0,1,2,3,4}))throw new InvalidOperationException("Automatic progress rail skipped or reordered a stage: "+string.Join(",",seenStages));
        var lockedExposure=current.CaptureExposureSeconds;var lockedGain=SessionGain;
        if(!current.CaptureSettingsLocked || current.CanChangeCaptureControls
            || current.IncreaseExposureCommand.CanExecute(null))
            throw new InvalidOperationException("PoleMaster acquisition settings must lock throughout adjustment.");
        current.CaptureExposureValue=300;current.StreamingExposureSeconds=.3;
        if(current.CaptureExposureSeconds!=lockedExposure || SessionGain!=lockedGain)
            throw new InvalidOperationException("Locked controls changed camera parameters.");
        if(current.HasSettings || View.FindName("PanelSettings")!=null || View.FindName("CaptureExposureInput")!=null || AdvancedPreview.FindName("CaptureExposureInput")==null)
            throw new InvalidOperationException("Settings must exist only in the plugin Options view.");
        current.StopLiveCommand.Execute(null);await poleWorkflow;
        if(!current.CaptureSettingsLocked || current.CanChangeCaptureControls)throw new InvalidOperationException("Pausing must retain the capture settings lock.");
        poleRotations=SampleCapture.Rotations;poleReturns=SampleCapture.Returns;
        current.AutoVerificationStability=false;
        await ((IAsyncRelayCommand)current.VerifyAlignmentCommand).ExecuteAsync(null);
        if(!current.HasVerificationResult || current.HasOperationError || SampleCapture.Rotations-poleRotations!=2 || SampleCapture.Returns-poleReturns!=1)
            throw new InvalidOperationException("PoleMaster verification did not repeat all three locations: "+current.OperationError);
        current.ResetLiveCommand.Execute(null);
        current.SelectPolarisCandidate(.6,.4);
        poleReturns=SampleCapture.Returns;
        var interrupted=((IAsyncRelayCommand)current.PrimaryActionCommand).ExecuteAsync(null);
        for(int i=0;i<200 && !current.IsRotating && !interrupted.IsCompleted;i++)await Task.Delay(10);
        if(!current.IsRotating)throw new InvalidOperationException("PoleMaster cancellation did not reach an interval");
        current.StopLiveCommand.Execute(null);await interrupted;
        if(current.WorkflowStep!=0 || current.HasLiveMeasurement || SampleCapture.Returns!=poleReturns)
            throw new InvalidOperationException("An interrupted PoleMaster interval retained guidance or requested return");
        current.PoleMasterMode=false;
        if(current.RotationAngleDegrees!=90 || current.StreamingBinning!=2 || current.ValidationTargetArcseconds!=60 || current.WorkflowStep!=0)
            throw new InvalidOperationException("Main-camera settings were not restored after PoleMaster mode");
        if(typeof(PolarAlignmentDockableVM).GetField("poleMasterPreviewTask",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(current) is Task stoppedPreview)await stoppedPreview;
        if(SampleCapture.PreviewOwners!=0)throw new InvalidOperationException("PoleMaster OFF did not stop preview");
        current.PoleMasterMode=true;await Task.Delay(150);
        SampleCapture.FailPreviewCleanup=true;
        int safeCaptures=SampleCapture.CaptureCalls,safeRotations=SampleCapture.Rotations;
        await ((IAsyncRelayCommand)current.PrimaryActionCommand).ExecuteAsync(null);
        if(!current.HasOperationError || SampleCapture.CaptureCalls!=safeCaptures || SampleCapture.Rotations!=safeRotations)
            throw new InvalidOperationException("Preview restoration failure did not block alignment");
        SampleCapture.FailPreviewCleanup=false;current.PoleMasterMode=false;
        SampleCapture.SupportHome=true;
        await SelectScene(0);current!.PoleMasterMode=true;await Task.Delay(150);
        if(current.OperatorState!="PolePreview" || !current.ActionButtonCommand.CanExecute(null)
            || current.ActionButtonCommand!=current.PrimaryActionCommand || !current.HomeRestartCommand.CanExecute(null)
            || !current.ShowOptionalHome || current.ActionButtonLabel!=UiText.Get("RestartBeginButton")
            || ((Button)View.FindName("HomeResetButton")).Visibility!=Visibility.Visible)
            throw new InvalidOperationException("Initial startup must accept Polaris immediately without a Home command.");
        Save(Path.Combine(directory,"ui-home-before-restart.png"),1280,800);
        int beforeHomeCaptures=SampleCapture.CaptureCalls,beforeHomeRotations=SampleCapture.Rotations,beforeHome=SampleCapture.HomeCalls;
        current.CaptureExposureSeconds=.75;
        string gainBeforeHome=SessionGain.ToString();
        await current.HomeRestartCommand.ExecuteAsync(null);
        if(SessionGain.ToString()!=gainBeforeHome || current.ExposureSeconds!=.75 || current.StreamingExposureSeconds!=.75)
            throw new InvalidOperationException("Home restart reset user gain or exposure.");
        if(SampleCapture.HomeCalls!=beforeHome+1 || current.PolarisCandidate!=null || !current.PrimaryActionCommand.CanExecute(null)
            || SampleCapture.CaptureCalls!=beforeHomeCaptures || SampleCapture.Rotations!=beforeHomeRotations || current.HasOperationError)
            throw new InvalidOperationException("Home restart did not wait for a new Polaris click.");
        if(current.ActionButtonLabel!=UiText.Get("RestartBeginButton") || current.OperatorState!="PolePreview")
            throw new InvalidOperationException("Start must retain its name while waiting for the Polaris click.");
        SampleProfile.Language="ko-KR";NINA.Core.Locale.Loc.Instance.ReloadLocale("ko-KR");
        UiText.Refresh();for(int i=0;i<100 && !current.HasPreview;i++)await Task.Delay(10);await Task.Delay(100);
        Save(Path.Combine(directory,"ui-home-complete-select-polaris-ko.png"),1280,800);
        current.SelectPolarisCandidate(.6,.4);
        if(!current.PrimaryActionCommand.CanExecute(null))throw new InvalidOperationException("New Polaris selection did not enable a fresh calibration.");
        current.PoleMasterMode=false;SampleCapture.SupportHome=false;
        SampleCapture.AlreadyHome=true;
        int homesBeforeAlreadyHome=SampleCapture.HomeCalls;
        current.PoleMasterMode=true;await Task.Delay(100);
        if(current.OperatorState!="PolePreview" || !current.PrimaryActionCommand.CanExecute(null))
            throw new InvalidOperationException("A mount already at Home must wait only for Polaris, without requiring reset.");
        current.SelectPolarisCandidate(.6,.4);
        if(!current.PrimaryActionCommand.CanExecute(null) || SampleCapture.HomeCalls!=homesBeforeAlreadyHome)
            throw new InvalidOperationException("Already-Home startup must allow alignment without issuing FindHome.");
        Save(Path.Combine(directory,"ui-already-home-ready-ko.png"),1280,800);
        current.PoleMasterMode=false;SampleCapture.AlreadyHome=false;
        SampleCapture.SupportHome=true;
        int startupHomes=SampleCapture.HomeCalls;
        current.PoleMasterMode=true;await Task.Delay(150);
        current.SelectPolarisCandidate(.6,.4);
        if(!current.PrimaryActionCommand.CanExecute(null) || SampleCapture.HomeCalls!=startupHomes)
            throw new InvalidOperationException("Initial Polaris click must work even when the driver does not report AtHome");
        var monitorWorkflow=((IAsyncRelayCommand)current.PrimaryActionCommand).ExecuteAsync(null);
        for(int i=0;i<1000 && !current.HasLiveMeasurement && !monitorWorkflow.IsCompleted;i++)await Task.Delay(10);
        if(!current.HasLiveMeasurement || monitorWorkflow.IsCompleted)throw new InvalidOperationException("Restart test did not reach live adjustment: "+current.OperationError);
        current.StartPoleMasterMonitoringCommand.Execute(null);
        if(current.CurrentPoleMasterStage!=5 || current.PoleMasterSteps.Count(s=>s.IsCompleted)!=5)throw new InvalidOperationException("Fine monitoring must activate the last stage.");
        if(!current.IsPoleMasterMonitoring || !current.HomeRestartCommand.CanExecute(null))throw new InvalidOperationException("Restart must be enabled during fine monitoring");
        var sharedProfiles=(IProfileService)typeof(PolarAlignmentDockableVM).GetField("profiles",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(current)!;
        var imagePanel=new PolarAlignmentImageDockableVM(sharedProfiles,current);
        if(!ReferenceEquals(imagePanel.Session,current))throw new InvalidOperationException("The image dock must use the existing capture session");
        current.IsVisible=false;await Task.Delay(150);
        if(monitorWorkflow.IsCompleted)throw new InvalidOperationException("Hiding controls must not stop capture while the image dock remains visible");
        current.IsVisible=true;imagePanel.IsClosed=true;await Task.Delay(150);
        if(monitorWorkflow.IsCompleted)throw new InvalidOperationException("Closing the image dock must not stop capture while controls remain visible");

        var restartExposure=current.CaptureExposureSeconds;var restartGain=SessionGain.ToString();
        var restartRotations=SampleCapture.Rotations;
        await current.HomeRestartCommand.ExecuteAsync(null);await monitorWorkflow;
        if(SampleCapture.HomeCalls!=startupHomes+1 || current.WorkflowStep!=0 || current.PolarisCandidate!=null || current.HasOperationError
            || current.CaptureExposureSeconds!=restartExposure || SessionGain.ToString()!=restartGain || SampleCapture.Rotations!=restartRotations)
            throw new InvalidOperationException("Monitoring restart must drain streaming, Home once, clear calibration and retain capture settings");
        Save(Path.Combine(directory,"ui-monitor-restarted-click-polaris.png"),1280,800);
        if(current.CurrentPoleMasterStage!=0 || current.PoleMasterSteps.Any(s=>s.IsCompleted))throw new InvalidOperationException("Home restart must clear completed stages and wait for a new Polaris click.");
        if(current.CaptureSettingsLocked)throw new InvalidOperationException("Home restart must unlock initial capture and method controls without resetting their values.");
        current.PoleMasterMode=false;
        current.IsVisible=true;imagePanel.IsClosed=false;imagePanel.IsVisible=true;
        current.Hide(null!);
        if(current.IsVisible || imagePanel.IsVisible)throw new InvalidOperationException("One toolbar action must hide both panels");
        current.Hide(null!);
        if(!current.IsVisible || !imagePanel.IsVisible || imagePanel.IsClosed)throw new InvalidOperationException("One toolbar action must reopen both panels");
        imagePanel.IsClosed=true;current.Hide(null!);
        if(!current.IsVisible || imagePanel.IsClosed || !imagePanel.IsVisible)throw new InvalidOperationException("A missing image panel must reopen from the single toolbar icon");
        var otherTool=new NINA.WPF.Base.ViewModel.DockableVM(sharedProfiles);
        var dockHost=DispatchProxy.Create<NINA.WPF.Base.Interfaces.ViewModel.IDockManagerVM,SampleDockHost>();
        var dockData=(SampleDockHost)(object)dockHost;
        dockData.Tools=new(){current,imagePanel,otherTool};dockData.All=new(dockData.Tools);
        var toolbarView=System.Windows.Data.CollectionViewSource.GetDefaultView(dockData.Tools);
        toolbarView.Filter=item=>!ReferenceEquals(item,otherTool);
        var configure=typeof(PolarAlignmentDockableVM).Assembly.GetType("Nina.LivePolarAlignment.UI.PolarToolbar")!.GetMethod("Configure",BindingFlags.Static|BindingFlags.NonPublic)!;
        configure.Invoke(null,new object[]{dockHost});
        if(toolbarView.Cast<object>().Count()!=1 || !ReferenceEquals(toolbarView.Cast<object>().Single(),current)
            || dockData.All.Count!=3 || !dockData.All.Contains(imagePanel))throw new InvalidOperationException("Toolbar filtering must preserve other plugin filters and the full docking collection");
        var appliedFilter=toolbarView.Filter;configure.Invoke(null,new object[]{dockHost});
        if(!ReferenceEquals(toolbarView.Filter,appliedFilter))throw new InvalidOperationException("Repeated layout updates must not accumulate toolbar filters");

        current.PoleMasterMode=false;
        void GeneralState(string name,object? value)=>typeof(PolarAlignmentDockableVM).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(current,value);
        var referenceError=new Nina.LivePolarAlignment.Core.PolarError(2,-3,3.6);
        GeneralState("livePreview",new SampleCapture().Image);GeneralState("pixelScale",6d);
        GeneralState("calibrationSamples",2);GeneralState("liveBusy",true);GeneralState("latestError",referenceError);
        GeneralState("guidance",new PolarGuidance(referenceError,new(390,310),new(400,300),800,600,new(350,300),new(410,300),false));
        GeneralState("measuredAt",DateTimeOffset.UtcNow);GeneralState("measurementStale",false);
        typeof(PolarAlignmentDockableVM).GetMethod("NotifyLive",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(current,null);
        if(!current.UsePolarisTarget || current.OperatorStateTitle!=UiText.Get("OperatorReferenceStarMove"))throw new InvalidOperationException("General-camera adjustment must identify its fixed reference-star target.");
        await Task.Delay(50);
        Save(Path.Combine(directory,"ui-general-fixed-reference.png"),1440,1000);
        if(current.WorkflowStages.Count!=(current.AutoRaRotation?6:5) || current.WorkflowStages.Count(x=>x.IsCurrent)!=1
            || ((ItemsControl)View.FindName("WorkflowStages")).Visibility!=Visibility.Visible)
            throw new InvalidOperationException("General mode progress rail: count="+current.WorkflowStages.Count+", current="+current.CurrentWorkflowStage+", visibility="+((ItemsControl)View.FindName("WorkflowStages")).Visibility);
        GeneralState("liveBusy",false);

        void RefreshMethod()=>typeof(PolarAlignmentDockableVM).GetMethod("RefreshCameraMethod",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(current,null);
        SampleCapture.MethodCamera=new("POLEMASTER-test",true,true,true,true,10.97,8.24);RefreshMethod();
        if(!current.PoleMasterMode || !current.CanUseSelectedMethod)throw new InvalidOperationException("PoleMaster connection must default to the supported pattern method.");
        current.AlignmentMethodIndex=1;RefreshMethod();
        if(current.PoleMasterMode)throw new InvalidOperationException("Automatic selection must respect a manual choice for the same connected camera.");
        SampleCapture.MethodCamera=new("QHY600-test",false,false,false,true,2.06,1.38);RefreshMethod();
        if(current.PoleMasterMode || !current.CanUseSelectedMethod || !current.HasMethodWarning || !current.CameraFieldOfView.Contains("2.06"))throw new InvalidOperationException("A non-video main camera must default to NINA solving and report the still-exposure fallback.");
        current.AlignmentMethodIndex=0;
        if(current.CanUseSelectedMethod || !current.HasMethodWarning || current.PrimaryActionCommand.CanExecute(null))throw new InvalidOperationException("An incompatible PoleMaster pattern choice must be blocked before capture.");
        SampleCapture.MethodCamera=new("no-optics",false,false,true,false,null,null);RefreshMethod();
        if(current.CanUseSelectedMethod || current.PrimaryActionCommand.CanExecute(null))throw new InvalidOperationException("Missing solve optics must block Start with an explanation.");
        SampleCapture.MethodCamera=null;RefreshMethod();current.PoleMasterMode=false;
        if(typeof(PolarAlignmentDockableVM).GetField("poleMasterPreviewTask",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(current) is Task finalPreview)await finalPreview;

        File.WriteAllText(Path.Combine(directory,"polemaster-verification.txt"),"ON starts aiming preview without capture/solve/motion; OFF drains; rapid toggles have one owner; Start awaits preview cleanup; restoration failure blocks motion; stretch reset; three-location sequence, return, verification and cancellation passed. Synthetic inputs only; no physical mount.");
    }

    private static async Task RenderAdjustmentSamples(string directory) {
        Directory.CreateDirectory(directory);
        File.Delete(Path.Combine(directory, "preview-error.txt"));
        SampleProfile.Language = "ko-KR";
        NINA.Core.Locale.Loc.Instance.ReloadLocale("ko-KR");
        await SelectScene(2);
        var original = current!.FieldGuidance!;
        current.StopLiveCommand.Execute(null);
        if (currentLoop != null) await currentLoop;
        var vm = typeof(PolarAlignmentDockableVM);
        void Set(string name, object? value) => vm.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(current, value);
        Set("guidance",original);Set("latestError",original.Error);Set("liveBusy",true);
        Set("measuredAt",DateTimeOffset.UtcNow);Set("measurementStale",false);
        var retainedImage=current.LivePreview;var retainedStars=current.DisplayStars;
        var failedImage=BitmapSource.Create(2,2,96,96,PixelFormats.Gray8,null,new byte[]{0,255,255,0},2);failedImage.Freeze();
        var failureMethod=vm.GetMethod("StreamingPreviewAvailable",BindingFlags.Instance|BindingFlags.NonPublic)!;
        failureMethod.Invoke(current,new object[]{new StreamingPreview(failedImage,DateTimeOffset.UtcNow.AddMilliseconds(10),"synthetic lost match")});
        if(!ReferenceEquals(current.LivePreview,retainedImage) || !ReferenceEquals(current.FieldGuidance,original)
            || !ReferenceEquals(current.DisplayStars,retainedStars) || current.IsMeasurementStale)
            throw new InvalidOperationException("A transient failed video frame must not erase or replace the measured display");
        Set("measuredAt",DateTimeOffset.UtcNow.AddSeconds(-5));
        failureMethod.Invoke(current,new object[]{new StreamingPreview(failedImage,DateTimeOffset.UtcNow,"synthetic lost match")});
        if(!current.IsMeasurementStale || !current.HasDisplayGuidance || !ReferenceEquals(current.LivePreview,retainedImage))
            throw new InvalidOperationException("Lost tracking must retain the last measured image and markers, marked stale");
        Save(Path.Combine(directory,"00-retained-lost-match.png"),1440,1000);
        Set("holdingStreamMeasurement",false);Set("trackingRecovery",false);Set("liveBusy",false);
        void RefreshAndAssert(string expectedState, bool advice) {
            vm.GetMethod("NotifyLive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, null);
            if (current!.OperatorState != expectedState || current.CanShowAdjustmentAdvice != advice
                || current.OperatorStateTitle.StartsWith("Operator") || current.OperatorNextAction.StartsWith("Operator"))
                throw new InvalidOperationException("Operator guidance state failed: " + expectedState);
            if (expectedState == "Within" && (current.AltitudeAdjustmentAction != UiText.Get("OperatorKeep") || current.AzimuthAdjustmentAction != UiText.Get("OperatorKeep")))
                throw new InvalidOperationException("Within target must stop both adjustment instructions");
        }
        foreach (var sample in new[] { ("01-adjust", 600d), ("02-close", 90d), ("03-within-target", 30d) }) {
            double ratio = sample.Item2 / (original.Error.TotalArcminutes * 60);
            var error = new Core.PolarError(original.Error.AltitudeArcminutes * ratio,
                original.Error.AzimuthArcminutes * ratio, sample.Item2 / 60);
            var pole = original.CelestialPole!.Value;
            var axis = original.MountAxis!.Value;
            var guidance = original with { Error = error, MountAxis = new Core.PixelPoint(
                pole.X + (axis.X - pole.X) * ratio, pole.Y + (axis.Y - pole.Y) * ratio) };
            Set("guidance", guidance); Set("latestError", error);
            Set("measurementStale", false); Set("measuredAt", DateTimeOffset.UtcNow);
            Set("lastCycleMilliseconds", 2500d); Set("liveBusy", true); Set("autoRaRotation", true);
            RefreshAndAssert(sample.Item2 <= 60 ? "Within" : sample.Item2 <= 120 ? "Close" : "Adjust", true);
            Save(Path.Combine(directory, sample.Item1 + ".png"), 1440, 1000);
        }
        var withinGuidance = current!.FieldGuidance!;
        var withinError = withinGuidance.Error;
        var farError = new Core.PolarError(40, 0, 40);
        Set("latestError", farError);
        Set("guidance", original with { Error = farError, MountAxis = new Core.PixelPoint(300, 300), CelestialPole = new Core.PixelPoint(700, 300) });
        RefreshAndAssert("Far", true);
        Save(Path.Combine(directory, "07-large-error.png"), 1440, 1000);
        var outsideError = new Core.PolarError(120, 0, 120);
        Set("latestError", outsideError);
        Set("guidance", original with { Error = outsideError, MountAxis = new Core.PixelPoint(1600, 300), CelestialPole = new Core.PixelPoint(400, 300) });
        RefreshAndAssert("Outside", true);
        Save(Path.Combine(directory, "08-outside-image.png"), 1440, 1000);
        Set("latestError", new Core.PolarError(301, 0, 301));
        RefreshAndAssert("Recalibrate", false);
        Save(Path.Combine(directory, "09-recalibration-needed.png"), 1440, 1000);
        Set("latestError", withinError); Set("guidance", withinGuidance with { MountAxis = null });
        RefreshAndAssert("Recalibrate", false);
        Set("guidance", withinGuidance with { MountAxis = new Core.PixelPoint(double.NaN, 0) });
        RefreshAndAssert("Recalibrate", false);
        Save(Path.Combine(directory, "10-invalid-geometry.png"), 1440, 1000);
        Set("guidance", withinGuidance);
        Set("operationError", "Plate solve failed.");
        RefreshAndAssert("Failed", false);
        if (current.OperatorNextAction != UiText.Get("OperatorNextSolveFailed"))
            throw new InvalidOperationException("Solve failure must give specific recovery instructions");
        Set("operationError", null);
        SampleCapture.CameraOffline = true;
        RefreshAndAssert("ConnectCamera", false);
        if (current!.AlignmentQualityLevel != "Unavailable" || current.HasCurrentFieldGuidance)
            throw new InvalidOperationException("Disconnected camera must invalidate guidance and success colour");
        SampleCapture.CameraOffline = false;
        Set("measuredAt", DateTimeOffset.UtcNow.AddMinutes(-5));
        RefreshAndAssert("Stale", false);
        Save(Path.Combine(directory, "04-wait-for-measurement.png"), 1440, 1000);
        if (((PolarFieldView)View.FindName("SkyField")).IsGuidanceFresh)
            throw new InvalidOperationException("Stale measurement must suppress live visual advice");
        Set("liveBusy", false); RefreshAndAssert("Paused", false);
        Save(Path.Combine(directory, "05-paused.png"), 1440, 1000);
        Set("liveBusy", true); Set("rotationInProgress", true); Set("returningToStart", true);
        Set("guidance", null); Set("latestError", null);
        RefreshAndAssert("Returning", false);
        Save(Path.Combine(directory, "06-returning.png"), 1440, 1000);
        Set("returningToStart", false); RefreshAndAssert("Rotating", false);
        Set("rotationInProgress", false); RefreshAndAssert("Calibrating", false);
        Set("poleMasterMode",true);Set("poleMasterMonitoring",false);Set("latestError",original.Error);
        Set("guidance",original with{Polaris=new Core.PixelPoint(original.Width*.6,original.Height*.55),PolarisTarget=new Core.PixelPoint(original.Width*.45,original.Height*.4)});
        Set("measuredAt",DateTimeOffset.UtcNow);Set("measurementStale",false);
        current.PreviewStretchEnabled=false;
        RefreshAndAssert("PolarisMove",true);
        if(!current.UsePolarisTarget || !current.ShowPoleMasterMonitoringAction || !current.StartPoleMasterMonitoringCommand.CanExecute(null))
            throw new InvalidOperationException("PoleMaster must start with a Polaris destination and a separate monitoring action");
        Save(Path.Combine(directory,"11-polaris-into-fixed-target.png"),1440,1000);
        failureMethod.Invoke(current,new object[]{new StreamingPreview(failedImage,DateTimeOffset.UtcNow.AddMilliseconds(10),"synthetic lost match")});
        if(current.FieldGuidance?.PolarisTarget==null || current.IsMeasurementStale)
            throw new InvalidOperationException("Polaris destination must survive an unmeasured video frame");
        var fixedDestination=current.FieldGuidance!.PolarisTarget;
        for(int i=0;i<5;i++)failureMethod.Invoke(current,new object[]{new StreamingPreview(retainedImage!,DateTimeOffset.UtcNow.AddSeconds(i),string.Empty,IsManualPolarisAdjustment:true)});
        if(current.FieldGuidance!.Polaris!=null || current.FieldGuidance.PolarisTarget!=fixedDestination
            || current.OperatorState!="PolarisMove" || current.ShowPolarErrorReadout || !current.StartPoleMasterMonitoringCommand.CanExecute(null))
            throw new InvalidOperationException("Manual alignment must show live pixels and a fixed destination without inferred star boxes or stale error guidance");
        Save(Path.Combine(directory,"13-manual-video-fixed-circle.png"),1440,1000);
        var targetPoint=fixedDestination!.Value;
        var normalizedGoal=new Core.PixelPoint(targetPoint.X/current.FieldGuidance.Width,targetPoint.Y/current.FieldGuidance.Height);
        void ManualOffsetFrame(Core.PixelPoint? position,double ageSeconds=0) => failureMethod.Invoke(current,new object[]{new StreamingPreview(retainedImage!,DateTimeOffset.UtcNow.AddSeconds(-ageSeconds),string.Empty,
            IsManualPolarisAdjustment:true,ManualOffset:position is {} p?new ManualPolarisOffset(p,42):null)});
        ManualOffsetFrame(new(.01,.01));
        if(current.ShowManualOffset)throw new InvalidOperationException("A star outside the actual magnifier must not enable the distance readout");
        ManualOffsetFrame(normalizedGoal);
        if(!current.ShowManualOffset || current.ManualOffsetReadout!=UiText.Format("ManualTargetDistance","42″"))
            throw new InvalidOperationException("Confirmed Polaris inside the actual crop must display its fresh distance");
        Save(Path.Combine(directory,"15-manual-closeup-distance.png"),1440,1000);
        ManualOffsetFrame(null);
        if(!current.ShowManualOffset || current.ManualOffsetReadout!=UiText.Get("ManualTargetUnconfirmed"))
            throw new InvalidOperationException("Lost Polaris must clear the number while retaining a stable readout area");
        ManualOffsetFrame(normalizedGoal,3);
        if(current.ManualOffsetReadout!=UiText.Get("ManualTargetUnconfirmed"))
            throw new InvalidOperationException("Old distance evidence must not be shown as current");
        ManualOffsetFrame(new(.01,.01));
        if(current.ManualOffsetReadout!=UiText.Get("ManualTargetUnconfirmed") || current.FieldGuidance.PolarisTarget!=fixedDestination)
            throw new InvalidOperationException("Leaving the crop must clear the number without moving the fixed goal");
        current.StartPoleMasterMonitoringCommand.Execute(null);
        if(current.ShowManualOffset)throw new InvalidOperationException("Fine monitoring must hide the separate rough-alignment distance");
        if(current.UsePolarisTarget || !current.IsPoleMasterMonitoring || current.ShowPoleMasterMonitoringAction)
            throw new InvalidOperationException("Start monitoring must advance to fine red/green axis guidance without recalibration");
        Save(Path.Combine(directory,"12-polemaster-fine-monitoring.png"),1440,1000);
        Set("guidance",withinGuidance with{PolarisTarget=fixedDestination});Set("latestError",withinError);
        Set("measuredAt",DateTimeOffset.UtcNow);Set("measurementStale",false);Set("trackingRecovery",false);Set("holdingStreamMeasurement",false);
        RefreshAndAssert("Within",true);
        Save(Path.Combine(directory,"14-polemaster-fine-fresh.png"),1440,1000);
        RenderDetachedPanels(directory);

        current.ReturnToPolarisTargetCommand.Execute(null);
        if(!current.UsePolarisTarget)throw new InvalidOperationException("Fine monitoring must return to the fixed target without recalibration");
        SampleCapture.CameraOffline=true;
        vm.GetMethod("NotifyLive",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(current,null);
        Save(Path.Combine(directory,"16-disconnected-gain-placeholder.png"),1440,1000);
        if(View.FindName("CameraGainInput")!=null)throw new InvalidOperationException("The plugin must not expose duplicate gain controls.");
        SampleCapture.CameraOffline=false;
        Set("liveBusy", false); current.Dispose();
    }

    private static void RenderDetachedPanels(string directory) {
        var image=new PolarAlignmentImageView{DataContext=current};
        var controls=new PolarAlignmentControlsView{DataContext=current};
        void Render(FrameworkElement view,string name,int width,int height) {
            view.Measure(new Size(width,height));view.Arrange(new Rect(0,0,width,height));view.UpdateLayout();
            var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(directory,name));encoder.Save(file);
        }
        Render(image,"17-independent-image-1200.png",1200,800);
        Render(controls,"18-independent-controls-320.png",320,900);
        var field=(PolarFieldView)image.FindName("SkyField");
        var detail=(PolarFieldView)controls.FindName("AlignmentDetailView");
        var toolbar=(FrameworkElement)image.FindName("ImageToolbar");
        if(field.ActualWidth<1180 || field.ActualHeight<750 || toolbar.ActualHeight>34
            || ((Slider)image.FindName("CompactStretchSlider")).ActualWidth!=140
            || !ReferenceEquals(field.Preview,detail.Preview) || !ReferenceEquals(field.Guidance,detail.Guidance))
            throw new InvalidOperationException("Independent image/control panels must share one measured frame and reserve almost the entire image panel for video");
    }

    private static void RenderMarkers(string directory) {
        Directory.CreateDirectory(directory);
        var image = new SampleCapture().Image;
        var stable = new PolarFieldView { Preview=image, IsCurrent=true,
            Guidance=new(default,new Core.PixelPoint(270,260),new Core.PixelPoint(500,340),800,600) };
        stable.Measure(new Size(960,600));stable.Arrange(new Rect(0,0,960,600));stable.UpdateLayout();
        void RenderStable() { new RenderTargetBitmap(960,600,96,96,PixelFormats.Pbgra32).Render(stable); }
        RenderStable();var fullRegion=stable.VisibleImageRegion;
        if(stable.FocusAlignment)throw new InvalidOperationException("The main image must default to a fixed full field");
        stable.FocusAlignment=true;RenderStable();var focusedRegion=stable.VisibleImageRegion;
        stable.Guidance=null;stable.IsCurrent=false;RenderStable();
        if(stable.VisibleImageRegion!=focusedRegion)throw new InvalidOperationException("A missing match must not change the focused viewport");
        stable.Guidance=new(default,new Core.PixelPoint(600,480),new Core.PixelPoint(700,500),800,600);stable.IsCurrent=true;RenderStable();
        if(stable.VisibleImageRegion!=focusedRegion)throw new InvalidOperationException("New measurements must not pan or zoom the focused viewport");
        stable.FocusAlignment=false;RenderStable();
        if(stable.VisibleImageRegion!=fullRegion)throw new InvalidOperationException("Disabling focus must restore the full field");
        foreach (var culture in new[] { "ko-KR", "en-US" }) {
            UiText.Configure(() => culture);
            foreach (var scene in new[] {
                ("inside", new Core.PixelPoint(270, 260), new Core.PixelPoint(500, 340), false, 960, 600),
                ("axis-outside", new Core.PixelPoint(-300, 270), new Core.PixelPoint(470, 340), false, 960, 600),
                ("pole-outside", new Core.PixelPoint(280, 260), new Core.PixelPoint(1200, 700), false, 960, 600),
                ("both-outside", new Core.PixelPoint(1300, -400), new Core.PixelPoint(1600, -600), false, 960, 600),
                ("detail", new Core.PixelPoint(270, 260), new Core.PixelPoint(500, 340), true, 420, 300),
                ("narrow", new Core.PixelPoint(-300, 270), new Core.PixelPoint(470, 340), false, 320, 260)
            }) {
                var field = new PolarFieldView { Preview = image, IsCurrent = true, FocusAlignment = false,
                    DetailMode = scene.Item4, PixelScale = 6,
                    Guidance = new(default, scene.Item2, scene.Item3, 800, 600) };
                field.Measure(new Size(scene.Item5, scene.Item6));
                field.Arrange(new Rect(0, 0, scene.Item5, scene.Item6)); field.UpdateLayout();
                var bitmap = new RenderTargetBitmap(scene.Item5, scene.Item6, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(field);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"{culture}-{scene.Item1}.png")); encoder.Save(file);
            }
        }
    }

    private static async Task SelectScene(int scene) {
        SceneButtons.IsEnabled = false;
        try {
            current?.Dispose();
            if(current!=null && typeof(PolarAlignmentDockableVM).GetField("poleMasterPreviewTask",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(current) is Task oldPreview)await oldPreview;
            if (currentLoop != null) await currentLoop;
            currentLoop = null;
            var site = DispatchProxy.Create<IAstrometrySettings, SampleProfile>();
            var profile = DispatchProxy.Create<IProfile, SampleProfile>(); ((SampleProfile)(object)profile).Site = site;
            ((SampleProfile)(object)profile).Settings = DispatchProxy.Create<IApplicationSettings, SampleProfile>();
            var profiles = DispatchProxy.Create<IProfileService, SampleProfile>(); ((SampleProfile)(object)profiles).Active = profile;
            var capture = new SampleCapture();
            // Match the host's possible background MEF composition; its loader Dispatcher is not pumped.
            current = await Task.Run(() => new PolarAlignmentDockableVM(profiles, capture,
                DispatchProxy.Create<NINA.WPF.Base.Interfaces.Mediator.IApplicationStatusMediator, SampleStatus>()));
            if(!current.PoleMasterMode || current.CaptureExposureValue!=100)throw new InvalidOperationException("Startup must default to PoleMaster ON at 100 ms");
            current.IsVisible=true;
            current.PoleMasterMode=false;
            if (!current.AutoRaRotation || !current.UseStreaming || current.RotationAngleDegrees != 90 || current.ReverseRaRotation || current.VerificationReverse) throw new InvalidOperationException("Acquisition defaults must use the corrected positive RA direction");
            if (scene == 0) {
                SampleCapture.MountOffline = true;
                typeof(PolarAlignmentDockableVM).GetMethod("NotifyLive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, null);
                if (!current.AutoRaRotation || current.PrimaryActionCommand.CanExecute(null))
                    throw new InvalidOperationException("Disconnected startup must preserve default Auto ON and block Start");
                SampleCapture.MountOffline = false;
                typeof(PolarAlignmentDockableVM).GetMethod("NotifyLive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, null);
                if (!current.AutoRaRotation) throw new InvalidOperationException("Reconnection must preserve Auto ON");
            }
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
        // Debug persistence is still profile-owned; UI binding to the shared
        // session is exercised in VerifyPoleMaster with the real options template.
        plugin.EnableDebugRecording=true;
        if(!new Plugin(optionProfiles).EnableDebugRecording)throw new InvalidOperationException("Debug recording did not persist.");
        plugin.EnableDebugRecording=false;
        if(new Plugin(optionProfiles).EnableDebugRecording)throw new InvalidOperationException("Debug recording did not disable.");
        plugin.EnableDebugRecording = true;
        ((SampleProfile)(object)optionProfiles).Active = DispatchProxy.Create<IProfile, SampleProfile>();
        if (plugin.EnableDebugRecording) throw new InvalidOperationException("Debug setting leaked between profiles");
        for (int scene = 0; scene < 3; scene++) {
            await SelectScene(scene);
            if (System.Text.RegularExpressions.Regex.IsMatch(current!.WorkflowTitle + current.WorkflowDescription + current.LiveStatus + current.FrameFreshness, @"[\uac00-\ud7af]")) throw new InvalidOperationException("Korean text in English workflow");
            if (current!.WorkflowStep != scene) throw new InvalidOperationException("Wrong workflow step");
            foreach (var size in new[] { (360, 1000), (1280, 800), (1920, 1080) }) Save(Path.Combine(directory, $"ui-step-{scene + 1}-{size.Item1}.png"), size.Item1, size.Item2);
        }
        void AssertMeasurementSurvivesNextPreview(string name) {
            var previousImage = current!.LivePreview;
            var previousGuidance = current.FieldGuidance;
            var previousError = current.LiveTotalError;
            var next = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Gray8, null, new byte[] { 0, 255, 255, 0 }, 2);
            next.Freeze();
            typeof(PolarAlignmentDockableVM).GetMethod("CapturePreviewAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, new object[] { next });
            if (!ReferenceEquals(previousImage, current.LivePreview) || !ReferenceEquals(previousGuidance, current.FieldGuidance)
                || current.LiveTotalError != previousError || !current.HasCurrentFieldGuidance || !current.HasAlignmentDetail)
                throw new InvalidOperationException("Next-frame solving must retain the solved image, overlay and error in " + name);
            Save(Path.Combine(directory, "ui-retained-" + name + ".png"), 1280, 800);
            var type = typeof(PolarAlignmentDockableVM);
            var timeField = type.GetField("measuredAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var cycleField = type.GetField("lastCycleMilliseconds", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var previousTime = timeField.GetValue(current);
            var previousCycle = cycleField.GetValue(current);
            try {
                cycleField.SetValue(current, 12000d); // Actual observed solver cycles are about 12 seconds.
                timeField.SetValue(current, DateTimeOffset.UtcNow.AddSeconds(-20));
                type.GetMethod("NotifyLive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, null);
                if (current.IsMeasurementStale || !current.HasAlignmentDetail || !current.HasDisplayGuidance)
                    throw new InvalidOperationException("Expected slow-solver cycle must not expire the display at 15 seconds: " + name);
                timeField.SetValue(current, DateTimeOffset.UtcNow.AddSeconds(-50));
                type.GetMethod("NotifyLive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, null);
                if (!current.IsMeasurementStale || current.HasCurrentFieldGuidance || !current.HasDisplayGuidance || !current.HasAlignmentDetail
                    || !ReferenceEquals(previousImage, current.LivePreview) || !ReferenceEquals(previousGuidance, current.FieldGuidance))
                    throw new InvalidOperationException("Age-only staleness must retain the solved image and overlay together: " + name);
                Save(Path.Combine(directory, "ui-retained-stale-" + name + ".png"), 1280, 800);
                var field = (PolarFieldView)View.FindName("SkyField");
                var detail = (PolarFieldView)View.FindName("AlignmentDetailView");
                if (!field.IsCurrent || !detail.IsCurrent || detail.Visibility != Visibility.Visible)
                    throw new InvalidOperationException("Stale-age transition must not hide or dim either image canvas: " + name);
            } finally {
                timeField.SetValue(current, previousTime); cycleField.SetValue(current, previousCycle);
                type.GetMethod("NotifyLive", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(current, null);
            }
        }
        AssertMeasurementSurvivesNextPreview("single");
        SampleProfile.Language = "ko-KR"; NINA.Core.Locale.Loc.Instance.ReloadLocale(SampleProfile.Language); UiText.Refresh();
        if (!System.Text.RegularExpressions.Regex.IsMatch(current!.WorkflowTitle, @"[\uac00-\ud7af]")) throw new InvalidOperationException("Korean locale missing");
        Save(Path.Combine(directory, "ui-korean-1280.png"), 1280, 800);
        SampleProfile.Language = "fr-FR"; NINA.Core.Locale.Loc.Instance.ReloadLocale(SampleProfile.Language); UiText.Refresh();
        if (!current.PrimaryActionLabel.StartsWith("Capturing / solving")) throw new InvalidOperationException("English fallback missing");
        SampleProfile.Language = "en-US"; NINA.Core.Locale.Loc.Instance.ReloadLocale(SampleProfile.Language); UiText.Refresh();
        current!.StopLiveCommand.Execute(null);
        await currentLoop!;
        if (!current.IsMeasurementStale || current.HasCurrentFieldGuidance || !current.HasAlignmentDetail) throw new InvalidOperationException("Stop must disable current guidance and retain the detail panel space");
        Save(Path.Combine(directory, "ui-paused-1280.png"), 1280, 800);
        current.UseStreaming = true;
        currentLoop = ((IAsyncRelayCommand)current.StartLiveCommand).ExecuteAsync(null);
        while (SampleCapture.StreamFrames < 2 && !currentLoop.IsCompleted) await Task.Delay(25);
        if (!current.HasLiveMeasurement || !current.HasCurrentFieldGuidance || SampleCapture.StreamFrames < 2) throw new InvalidOperationException("Streaming workflow failed");
        AssertMeasurementSurvivesNextPreview("streaming");
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
        ((Expander)AdvancedPreview.FindName("FieldValidationTools")).IsExpanded = true;
        Save(Path.Combine(directory, "ui-field-validation-1280.png"), 1280, 1000);
        ((Expander)AdvancedPreview.FindName("FieldValidationTools")).IsExpanded = false;
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
        foreach (var angle in new[] { 60, 70, 80, 90 }) {
            foreach (var reverse in new[] { false, true }) {
                await SelectScene(0);
                current!.AutoRaRotation = true; current.RotationAngleDegrees = angle; current.ReverseRaRotation = reverse;
                int rotationsBefore = SampleCapture.Rotations;
                int returnsBefore = SampleCapture.Returns;
                bool sawSecondPreview = false;
                SampleCapture.AfterPreview = () => {
                    if (current.WorkflowStep != 1) return;
                    sawSecondPreview = true;
                    if (current.FieldGuidance?.CelestialPole == null || !current.HasCurrentFieldGuidance)
                        throw new InvalidOperationException("Reference rings disappeared during second solve at " + angle);
                };
                var sequence = ((IAsyncRelayCommand)current.PrimaryActionCommand).ExecuteAsync(null);
                try {
                    for (int i = 0; i < 500 && !current.HasLiveMeasurement && !sequence.IsCompleted; i++) {
                        if (current.ActionButtonLabel == UiText.Get("ActionContinue"))
                            throw new InvalidOperationException("Automatic workflow exposed Continue at " + angle);
                        await Task.Delay(15);
                    }
                    if (!current.HasLiveMeasurement || current.HasOperationError || !sawSecondPreview
                        || current.WorkflowStep != 2 || SampleCapture.Rotations != rotationsBefore + 1 || SampleCapture.LastRotationAngle != angle
                        || SampleCapture.Returns != returnsBefore + 1)
                        throw new InvalidOperationException("One-Start angle/direction matrix failed at " + angle + "/" + reverse + ": " + current.OperationError);
                    Save(Path.Combine(directory, $"ui-angle-{angle}-{reverse}.png"), 1280, 800);
                } finally { SampleCapture.AfterPreview = null; current.StopLiveCommand.Execute(null); await sequence; }
            }
        }
        await SelectScene(0); current!.AutoRaRotation = true; current.RotationAngleDegrees = 60;
        SampleCapture.FailReturn = true;
        await ((IAsyncRelayCommand)current.PrimaryActionCommand).ExecuteAsync(null);
        SampleCapture.FailReturn = false;
        if (!current.HasOperationError || current.WorkflowStep != 0 || current.HasLiveMeasurement || !SampleCapture.RotationStopped)
            throw new InvalidOperationException("Failed return must discard calibration and block adjustment");
        await SelectScene(0); current!.AutoRaRotation = true; current.RotationAngleDegrees = 60;
        SampleCapture.FailSecondSolveOnce = true;
        int rotationsBeforeRetry = SampleCapture.Rotations;
        await ((IAsyncRelayCommand)current.PrimaryActionCommand).ExecuteAsync(null);
        if (!current.HasOperationError || current.ActionButtonLabel != UiText.Get("ActionRetry") || current.WorkflowStep != 1)
            throw new InvalidOperationException("Automatic second-solve failure must show Retry, not Continue");
        var retry = ((IAsyncRelayCommand)current.PrimaryActionCommand).ExecuteAsync(null);
        for (int i = 0; i < 300 && !current.HasLiveMeasurement && !retry.IsCompleted; i++) await Task.Delay(15);
        if (!current.HasLiveMeasurement || SampleCapture.Rotations != rotationsBeforeRetry + 1)
            throw new InvalidOperationException("Second-solve Retry must reach measurement without repeating RA rotation");
        current.StopLiveCommand.Execute(null); await retry;
        SampleCapture.Rotations = 0;
        SampleCapture.UnsupportedAuto = true;
        await SelectScene(0); current!.AutoRaRotation = true;
        if (current.CanUseAutoRotation || !current.CanUseStreaming) throw new InvalidOperationException("Unsupported mounts must disable only automatic RA rotation");
        if (!current.AutoRaRotation || current.PrimaryActionCommand.CanExecute(null))
            throw new InvalidOperationException("Unavailable mount must retain Auto ON and prevent automatic Start");
        current.AutoRaRotation = false;
        await ((IAsyncRelayCommand)current.CaptureReferenceCommand).ExecuteAsync(null);
        if (current.AutoRaRotation || current.HasOperationError || current.WorkflowStep != 1 || SampleCapture.Rotations != 0)
            throw new InvalidOperationException("Explicit manual mode must allow camera-only reference capture");
        SampleCapture.UnsupportedAuto = false;
        await SelectScene(0);
        var settingsPanel = (StackPanel)AdvancedPreview.FindName("RotationSettingsPanel");
        Save(Path.Combine(directory, "ui-manual-settings-1280.png"), 1280, 800);
        var manualHeight = settingsPanel.ActualHeight;
        if (settingsPanel.Visibility != Visibility.Collapsed) throw new InvalidOperationException("Manual mode must collapse rotation settings");
        current!.AutoRaRotation = true;
        var acquisitionSettings=(Expander)AdvancedPreview.FindName("AcquisitionSettings");
        if(acquisitionSettings.IsExpanded)throw new InvalidOperationException("Acquisition settings must start compact");
        acquisitionSettings.IsExpanded=true;
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
        if (!current.IsRotating || current.FieldGuidance?.CelestialPole == null
            || current.FieldGuidance.MountAxis != null || !current.HasCurrentFieldGuidance || current.WorkflowStep != 0)
            throw new InvalidOperationException("First solved pole and rings must be visible during rotation, before calibration completes");
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
        if (!current.HasOperationError || !current.AutoRaRotation || current.WorkflowStep != 0)
            throw new InvalidOperationException("A rotation fault must stop and invalidate calibration without changing the selected mode");
        await SelectScene(0); current!.RecordFieldSession = false;
        await ((IAsyncRelayCommand)current.CaptureReferenceCommand).ExecuteAsync(null);
        await ((IAsyncRelayCommand)current.CaptureReferenceCommand).ExecuteAsync(null);
        if (!current.VerifyAlignmentCommand.CanExecute(null)) throw new InvalidOperationException("Verification must be ready without an extra confirmation switch");

        await ((IAsyncRelayCommand)current.VerifyAlignmentCommand).ExecuteAsync(null);
        if (current.HasOperationError || !current.HasVerificationResult || !current.HasStabilityResult || current.WorkflowStep != 2
            || current.FieldSessionDirectory != null)
            throw new InvalidOperationException("Verification and automatic stability must work without debug recording: " + current.OperationError);
        ((Expander)AdvancedPreview.FindName("VerificationTools")).IsExpanded = true;
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
            var inputLabel = (TextBlock)AdvancedPreview.FindName("ExposureLabel");
            var resultHeading = (TextBlock)View.FindName("PolarErrorHeading");
            if(inputLabel.FontSize>=resultHeading.FontSize || inputLabel.FontWeight.ToOpenTypeWeight()>=resultHeading.FontWeight.ToOpenTypeWeight())
                throw new InvalidOperationException("Result headings must be visually stronger than input labels.");
            foreach (var name in new[] { "AutoRaOptionLabel", "ExposureLabel" })
                if (((SolidColorBrush)((TextBlock)(View.FindName(name)??AdvancedPreview.FindName(name))).Foreground).Color != schema.PrimaryColor)
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
        _=AdvancedPreview;
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
        if (current!.HasAlignmentDetail && !current.NeedsCalibrationReview && (detail.UsePolarisTarget ? detail.Guidance?.PolarisTarget : detail.Guidance?.CelestialPole) is { } pole) {
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
