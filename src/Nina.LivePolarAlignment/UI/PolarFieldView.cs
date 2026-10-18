using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using Nina.LivePolarAlignment.Capture;
using Nina.LivePolarAlignment.Core;

namespace Nina.LivePolarAlignment.UI;

/// <summary>Scale-preserving overlay of measured directions; never invents detected stars.</summary>
public sealed class PolarFieldView : FrameworkElement {
    public static readonly DependencyProperty PreviewProperty = DependencyProperty.Register(nameof(Preview), typeof(BitmapSource), typeof(PolarFieldView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GuidanceProperty = DependencyProperty.Register(nameof(Guidance), typeof(PolarGuidance), typeof(PolarFieldView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsCurrentProperty = DependencyProperty.Register(nameof(IsCurrent), typeof(bool), typeof(PolarFieldView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PixelScaleProperty = DependencyProperty.Register(nameof(PixelScale), typeof(double), typeof(PolarFieldView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public BitmapSource? Preview { get => (BitmapSource?)GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    public PolarGuidance? Guidance { get => (PolarGuidance?)GetValue(GuidanceProperty); set => SetValue(GuidanceProperty, value); }
    public bool IsCurrent { get => (bool)GetValue(IsCurrentProperty); set => SetValue(IsCurrentProperty, value); }
    public double PixelScale { get => (double)GetValue(PixelScaleProperty); set => SetValue(PixelScaleProperty, value); }
    public PolarFieldView() {
        Loaded += (_, _) => UiText.Instance.PropertyChanged += LanguageChanged;
        Unloaded += (_, _) => UiText.Instance.PropertyChanged -= LanguageChanged;
    }
    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();
    private static Brush Color(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    public static readonly DependencyProperty FocusAlignmentProperty = DependencyProperty.Register(nameof(FocusAlignment), typeof(bool), typeof(PolarFieldView), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool FocusAlignment { get => (bool)GetValue(FocusAlignmentProperty); set => SetValue(FocusAlignmentProperty, value); }
    public static readonly DependencyProperty DetailModeProperty = DependencyProperty.Register(nameof(DetailMode), typeof(bool), typeof(PolarFieldView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool DetailMode { get => (bool)GetValue(DetailModeProperty); set => SetValue(DetailModeProperty, value); }
    private static readonly DependencyPropertyKey ImageScalePropertyKey = DependencyProperty.RegisterReadOnly(nameof(ImageScale), typeof(double), typeof(PolarFieldView), new PropertyMetadata(1.0));
    public static readonly DependencyProperty ImageScaleProperty = ImageScalePropertyKey.DependencyProperty;
    public double ImageScale => (double)GetValue(ImageScaleProperty);
    public static readonly DependencyProperty ReferenceScaleProperty = DependencyProperty.Register(nameof(ReferenceScale), typeof(double), typeof(PolarFieldView), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double ReferenceScale { get => (double)GetValue(ReferenceScaleProperty); set => SetValue(ReferenceScaleProperty, value); }
    public Rect VisibleImageRegion { get; private set; }
    private Brush Mint => TryFindResource("ButtonBackgroundSelectedBrush") as Brush ?? Brushes.LightGray;
    private Brush Amber => TryFindResource("CrosshairBrush") as Brush ?? Brushes.Gray;
    private Brush Muted => TryFindResource("PrimaryBrush") as Brush ?? Brushes.LightGray;

    protected override Size MeasureOverride(Size availableSize) {
        double width = double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? Math.Clamp(width * .66, 200, 410) : availableSize.Height;
        return new(width, height);
    }

    protected override void OnRender(DrawingContext dc) {
        base.OnRender(dc);
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        dc.PushClip(new RectangleGeometry(bounds, 10, 10));
        if (Preview == null) {
            dc.DrawRectangle(TryFindResource("BackgroundBrush") as Brush ?? Color("#202020"), null, bounds);
            dc.Pop(); return;
        }
        dc.DrawRectangle(Color("#080F1B"), null, bounds);
        double width = Guidance?.Width ?? Preview?.PixelWidth ?? 800;
        double height = Guidance?.Height ?? Preview?.PixelHeight ?? 600;
        double scale = Math.Min(ActualWidth / width, ActualHeight / height);
        double cx = width / 2, cy = height / 2;
        if ((FocusAlignment || DetailMode) && IsCurrent && Guidance?.CelestialPole is { } target) {
            if (DetailMode) {
                // The target remains fixed at the center. Never fit both markers: an
                // off-screen RA axis is shown by the existing edge-direction indicator.
                cx = target.X; cy = target.Y;
                scale = Math.Max(8, ReferenceScale * 2);
            } else {
                var axis = Guidance.MountAxis ?? target;
                cx = (target.X + axis.X) / 2; cy = (target.Y + axis.Y) / 2;
                scale = Math.Max(scale, Math.Min(ActualWidth / Math.Max(width * .32, Math.Abs(target.X - axis.X) * 2.5), ActualHeight / Math.Max(height * .32, Math.Abs(target.Y - axis.Y) * 2.5)));
            }
        }
        SetValue(ImageScalePropertyKey, scale);
        VisibleImageRegion = new Rect(cx - ActualWidth / (2 * scale), cy - ActualHeight / (2 * scale), ActualWidth / scale, ActualHeight / scale);
        var imageRect = new Rect(ActualWidth / 2 - cx * scale, ActualHeight / 2 - cy * scale, width * scale, height * scale);
        if (Preview != null) dc.DrawImage(Preview, imageRect);
        Point Map(PixelPoint p) => new(imageRect.Left + p.X * scale, imageRect.Top + p.Y * scale);
        if (IsCurrent && Guidance != null) {
            Point? pole = Guidance.CelestialPole is { } p ? Map(p) : null;
            Point? axis = Guidance.MountAxis is { } a ? Map(a) : null;
            if (!DetailMode && pole is { } center && double.IsFinite(PixelScale) && PixelScale > 0) {
                // Angular-distance guide rings, not a claimed observed stellar trajectory.
                int count = 0;
                foreach (double degrees in new[] { 1.0 / 60, 5.0 / 60, .25, .5, 1, 2, 5 }) {
                    double radius = Math.Tan(degrees * Math.PI / 180) * 206264.80624709636 / PixelScale * scale;
                    if (radius < 30 || radius > Math.Max(ActualWidth, ActualHeight) * .8) continue;
                    var ringPen = new Pen(Amber, .8) { DashStyle = DashStyles.Dash };
                    var path = AngularRing(Guidance.CelestialPole!.Value, width, height, degrees, Map);
                    dc.DrawGeometry(null, ringPen, path);
                    var labelPosition = new Point(center.X + radius + 3, center.Y - 8);
                    if (bounds.Contains(labelPosition)) Label(dc, degrees >= 1 ? $"{degrees:0}°" : $"{degrees * 60:0}′", labelPosition, Muted, 10, false);
                    if (++count == 3) break;
                }
            }
            if (axis is { } axisPoint && pole is { } polePoint) {
                var connector = new Pen(Amber, 1.3) { DashStyle = DashStyles.Dash };
                dc.DrawLine(connector, axisPoint, polePoint);
            }
            if (axis is { } mount) {
                if (bounds.Contains(mount)) {
                    var pen = new Pen(Amber, 1.8);
                    dc.DrawEllipse(null, pen, mount, 8, 8);
                    dc.DrawLine(pen, new(mount.X - 13, mount.Y), new(mount.X + 13, mount.Y));
                    dc.DrawLine(pen, new(mount.X, mount.Y - 13), new(mount.X, mount.Y + 13));
                    Label(dc, "RA Axis", DetailMode ? new Point(mount.X - 44, mount.Y - 28) : new Point(mount.X + 17, mount.Y - 7), Amber, DetailMode ? 10 : 11);
                } else EdgeMarker(dc, mount, DetailMode ? "RA Axis" : UiText.Get("Field1"), Amber, bounds);
            }
            if (pole is { } celestial) {
                if (bounds.Contains(celestial)) {
                    var pen = new Pen(Mint, 2);
                    dc.DrawEllipse(null, pen, celestial, 6, 6);
                    dc.DrawEllipse(Mint, null, celestial, 1.8, 1.8);
                    Label(dc, "Celestial Pole", new Point(celestial.X + 12, celestial.Y + 9), Mint, 11);
                } else EdgeMarker(dc, celestial, UiText.Get("Field2"), Mint, bounds);
            }
        }
        if (Preview != null && !IsCurrent) {
            dc.DrawRectangle(Color("#66080F1B"), null, bounds);
            Label(dc, UiText.Get("Field3"), new Point(12, 14), Muted, 11);
        }
        dc.Pop();
    }

    private StreamGeometry AngularRing(PixelPoint polePixel, double width, double height, double degrees, Func<PixelPoint, Point> map) {
        double focal = 206264.80624709636 / PixelScale;
        var pole = new Vector3d((polePixel.X - width / 2) / focal, (height / 2 - polePixel.Y) / focal, 1).Normalized();
        var helper = Math.Abs(pole.Z) < .9 ? new Vector3d(0, 0, 1) : new Vector3d(1, 0, 0);
        var u = pole.Cross(helper).Normalized();
        var v = pole.Cross(u).Normalized();
        double angle = degrees * Math.PI / 180;
        var geometry = new StreamGeometry();
        using (var path = geometry.Open()) {
            bool started = false;
            for (int i = 0; i <= 96; i++) {
                double t = i * 2 * Math.PI / 96;
                var direction = pole * Math.Cos(angle) + (u * Math.Cos(t) + v * Math.Sin(t)) * Math.Sin(angle);
                if (direction.Z <= 1e-6) { started = false; continue; }
                var point = map(new(width / 2 + focal * direction.X / direction.Z, height / 2 - focal * direction.Y / direction.Z));
                if (!started) { path.BeginFigure(point, false, false); started = true; }
                else path.LineTo(point, true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private void Label(DrawingContext dc, string text, Point position, Brush foreground, double size, bool background = true) {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this), TextElement.GetFontStyle(this), TextElement.GetFontWeight(this), TextElement.GetFontStretch(this)), size, foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double x = Math.Clamp(position.X, 6, Math.Max(6, ActualWidth - formatted.Width - 12));
        double y = Math.Clamp(position.Y, 6, Math.Max(6, ActualHeight - formatted.Height - 12));
        if (background) dc.DrawRoundedRectangle(Color("#D90B1522"), null, new Rect(x - 4, y - 3, formatted.Width + 8, formatted.Height + 6), 4, 4);
        dc.DrawText(formatted, new Point(x, y));
    }

    private void EdgeMarker(DrawingContext dc, Point outside, string label, Brush brush, Rect bounds) {
        var center = new Point(bounds.Width / 2, bounds.Height / 2);
        var direction = outside - center;
        if (!double.IsFinite(direction.X) || !double.IsFinite(direction.Y) || direction.Length < 1) return;
        double factor = Math.Min((bounds.Width / 2 - 20) / Math.Max(Math.Abs(direction.X), .001),
            (bounds.Height / 2 - 20) / Math.Max(Math.Abs(direction.Y), .001));
        var point = center + direction * factor;
        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        dc.DrawLine(new Pen(brush, 2), point - direction * 10 + normal * 5, point);
        dc.DrawLine(new Pen(brush, 2), point - direction * 10 - normal * 5, point);
        Label(dc, label, DetailMode ? point - direction * 70 + normal * 18 : point - direction * 32, brush, 11);
    }
}
