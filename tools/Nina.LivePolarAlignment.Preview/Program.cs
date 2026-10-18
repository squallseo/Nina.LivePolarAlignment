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
    public object? Active { get; set; }
    public object? Site { get; set; }
    public object? Theme { get; set; }
    public object? Settings { get; set; }
    public static string Language { get; set; } = "en-US";
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch {
        "get_ApplicationSettings" => Settings, "get_Culture" => Language, "get_ColorSchemaSettings" => Theme, "get_ActiveProfile" => Active, "get_AstrometrySettings" => Site,
        "get_Latitude" => 37.5, "get_Longitude" => 127.0, _ => null
    };
}

internal sealed class SampleCapture : IPolarAlignmentCaptureService {
    public Func<int> Step { get; set; } = () => 0;
    public bool SupportsStreaming => true;
    public static int StreamFrames { get; private set; }
    public static bool FailStream { get; set; }
    public async IAsyncEnumerable<SolvedPolarFrame> StreamAndSolveAsync(double exposureSeconds,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token) {
        StreamFrames = 0;
        while (true) {
            await Task.Delay(100, token);
            if (FailStream) throw new InvalidOperationException("Synthetic streaming failure");
            StreamFrames++;
            yield return new(0, 89.7, 72, 6, 800, 600, false, DateTimeOffset.UtcNow, Image);
        }
    }
    public BitmapSource Image { get; } = MakeStars();
    public async Task<SolvedPolarFrame> CaptureAndSolveAsync(double exposureSeconds, CancellationToken token, IProgress<ApplicationStatus>? progress = null) {
        await Task.Delay(650, token);
        int step = Step();
        return new(0, step == 2 ? 89.7 : 89.6, step == 0 ? 0 : 72, 6, 800, 600, false, DateTimeOffset.UtcNow, Image);
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
        theme.ColorSchema = theme.ColorSchemas.Items.First(x => x.Name == "Persian");
        var tp = DispatchProxy.Create<IProfile, SampleProfile>(); ((SampleProfile)(object)tp).Theme = theme;
        var ts = DispatchProxy.Create<IProfileService, SampleProfile>(); ((SampleProfile)(object)ts).Active = tp;
        application.Resources["ProfileService"] = ts;
        foreach (var r in new[] { "StaticResources/Brushes.xaml", "Styles/Button.xaml", "Styles/TextBlock.xaml", "Styles/TextBox.xaml", "Styles/CheckBox.xaml", "Styles/Expander.xaml" })
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
            current = new PolarAlignmentDockableVM(profiles, capture);
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
        if (!current.IsMeasurementStale || current.HasCurrentFieldGuidance) throw new InvalidOperationException("Old guidance survived stop");
        Save(Path.Combine(directory, "ui-paused-1280.png"), 1280, 800);
        current.UseStreaming = true;
        currentLoop = ((IAsyncRelayCommand)current.StartLiveCommand).ExecuteAsync(null);
        while (SampleCapture.StreamFrames < 2 && !currentLoop.IsCompleted) await Task.Delay(25);
        if (!current.HasLiveMeasurement || !current.HasCurrentFieldGuidance || SampleCapture.StreamFrames < 2) throw new InvalidOperationException("Streaming workflow failed");
        Save(Path.Combine(directory, "ui-streaming-1280.png"), 1280, 800);
        current.StopLiveCommand.Execute(null); await currentLoop;
        if (current.HasCurrentFieldGuidance || !current.IsMeasurementStale) throw new InvalidOperationException("Stopped streaming retained guidance");
        SampleCapture.FailStream = true;
        await ((IAsyncRelayCommand)current.StartLiveCommand).ExecuteAsync(null);
        if (!current.HasOperationError || current.HasCurrentFieldGuidance || current.IsWorking) throw new InvalidOperationException("Streaming failure was not surfaced safely");
        SampleCapture.FailStream = false;
        current.Dispose();
        File.WriteAllText(Path.Combine(directory, "preview-verification.txt"), "Three workflow stages at narrow, HD and Full HD sizes, live publication, and stop/stale, streaming selection, repeated frames, cancellation, and failure checks passed. Synthetic inputs only.");
    }

    private static void Save(string path, int width, int height) {
        View.Width = width; View.Height = height;
        View.Measure(new Size(width, height)); View.Arrange(new Rect(0, 0, width, height)); View.UpdateLayout();
        var field = (FrameworkElement)View.FindName("SkyField");
        var controls = (FrameworkElement)View.FindName("ControlScroll");
        if (width >= 900) {
            if (field.ActualWidth < width * .6 || field.ActualHeight < height * .65
                || controls.TranslatePoint(new Point(), View).X < width * .6)
                throw new InvalidOperationException("The wide layout did not prioritize the left image and right controls.");
        } else if (controls.TranslatePoint(new Point(), View).Y < field.TranslatePoint(new Point(0, field.ActualHeight), View).Y)
            throw new InvalidOperationException("Narrow controls overlap the image.");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(View);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
