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
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch {
        "get_ActiveProfile" => Active, "get_AstrometrySettings" => Site,
        "get_Latitude" => 37.5, "get_Longitude" => 127.0, _ => null
    };
}

internal sealed class SampleCapture : IPolarAlignmentCaptureService {
    public Func<int> Step { get; set; } = () => 0;
    public BitmapSource Image { get; } = MakeStars();
    public async Task<SolvedPolarFrame> CaptureAndSolveAsync(double exposureSeconds, CancellationToken token, IProgress<ApplicationStatus>? progress = null) {
        await Task.Delay(650, token);
        int step = Step();
        return new(0, step == 2 ? 89.7 : 89.6, step == 0 ? 0 : 72, 30, 800, 600, false, DateTimeOffset.UtcNow, Image);
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
    private static readonly PolarAlignmentView View = new();
    private static readonly StackPanel SceneButtons = new() { Orientation = Orientation.Horizontal };

    [STAThread]
    private static void Main(string[] args) {
        // The preview owns a temporary NINA context; it never opens the user's profiles/database.
        var sampleData = Path.Combine(Path.GetTempPath(), "NinaPolarAlignmentSample", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sampleData);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", sampleData);
        var application = new Application();
        application.Resources["PuzzlePieceSVG"] = new GeometryGroup();
        var layout = new DockPanel();
        var header = new StackPanel { Background = new SolidColorBrush(Color.FromRgb(34, 44, 60)) };
        header.Children.Add(new TextBlock {
            Text = "합성 샘플 미리보기 · 카메라/적도의 연결 없음",
            Foreground = new SolidColorBrush(Color.FromRgb(240, 186, 114)), Margin = new Thickness(14, 11, 14, 7)
        });
        foreach (var item in new[] { ("1. 첫 화면", 0), ("2. RA 회전", 1), ("3. 조정 화면", 2) }) {
            var button = new Button { Content = item.Item1, Style = (Style)View.Resources[typeof(Button)], Margin = new Thickness(4, 0, 4, 9), Padding = new Thickness(10, 5, 10, 5) };
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
            if (current!.WorkflowStep != scene) throw new InvalidOperationException("Wrong workflow step");
            foreach (var size in new[] { (360, 1000), (1280, 800), (1920, 1080) }) Save(Path.Combine(directory, $"ui-step-{scene + 1}-{size.Item1}.png"), size.Item1, size.Item2);
        }
        current!.StopLiveCommand.Execute(null);
        await currentLoop!;
        if (!current.IsMeasurementStale || current.HasCurrentFieldGuidance) throw new InvalidOperationException("Old guidance survived stop");
        Save(Path.Combine(directory, "ui-paused-1280.png"), 1280, 800);
        current.Dispose();
        File.WriteAllText(Path.Combine(directory, "preview-verification.txt"), "Three workflow stages at narrow, HD and Full HD sizes, live publication, and stop/stale checks passed. Synthetic inputs only.");
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
