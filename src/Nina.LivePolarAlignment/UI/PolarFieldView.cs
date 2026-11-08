using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using Nina.LivePolarAlignment.Capture;
using Nina.LivePolarAlignment.Core;

namespace Nina.LivePolarAlignment.UI;

/// <summary>Scale-preserving overlay of measured directions; never invents detected stars.</summary>
public sealed class PolarFieldView : FrameworkElement {
    public static readonly DependencyProperty UsePolarisTargetProperty=DependencyProperty.Register(nameof(UsePolarisTarget),typeof(bool),typeof(PolarFieldView),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.AffectsRender));
    public bool UsePolarisTarget {get=>(bool)GetValue(UsePolarisTargetProperty);set=>SetValue(UsePolarisTargetProperty,value);}
    public static readonly DependencyProperty IsPoleMasterMonitoringProperty=DependencyProperty.Register(nameof(IsPoleMasterMonitoring),typeof(bool),typeof(PolarFieldView),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsPoleMasterMonitoring {get=>(bool)GetValue(IsPoleMasterMonitoringProperty);set=>SetValue(IsPoleMasterMonitoringProperty,value);}
    public static readonly DependencyProperty PolarisCandidateProperty=DependencyProperty.Register(nameof(PolarisCandidate),typeof(PixelPoint?),typeof(PolarFieldView),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public PixelPoint? PolarisCandidate {get=>(PixelPoint?)GetValue(PolarisCandidateProperty);set=>SetValue(PolarisCandidateProperty,value);}
    public static readonly DependencyProperty AreStarsFreshProperty=DependencyProperty.Register(nameof(AreStarsFresh),typeof(bool),typeof(PolarFieldView),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.AffectsRender));
    public bool AreStarsFresh {get=>(bool)GetValue(AreStarsFreshProperty);set=>SetValue(AreStarsFreshProperty,value);}
    public static readonly DependencyProperty StarsProperty = DependencyProperty.Register(nameof(Stars),typeof(StarOverlay),typeof(PolarFieldView),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public StarOverlay? Stars {get=>(StarOverlay?)GetValue(StarsProperty);set=>SetValue(StarsProperty,value);}
    public static readonly DependencyProperty PreviewProperty = DependencyProperty.Register(nameof(Preview), typeof(BitmapSource), typeof(PolarFieldView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsDriftViewProperty = DependencyProperty.Register(nameof(IsDriftView), typeof(bool), typeof(PolarFieldView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsDriftView { get => (bool)GetValue(IsDriftViewProperty); set => SetValue(IsDriftViewProperty, value); }
    public static readonly DependencyProperty GuidanceProperty = DependencyProperty.Register(nameof(Guidance), typeof(PolarGuidance), typeof(PolarFieldView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsCurrentProperty = DependencyProperty.Register(nameof(IsCurrent), typeof(bool), typeof(PolarFieldView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PixelScaleProperty = DependencyProperty.Register(nameof(PixelScale), typeof(double), typeof(PolarFieldView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public BitmapSource? Preview { get => (BitmapSource?)GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    public PolarGuidance? Guidance { get => (PolarGuidance?)GetValue(GuidanceProperty); set => SetValue(GuidanceProperty, value); }
    public bool IsCurrent { get => (bool)GetValue(IsCurrentProperty); set => SetValue(IsCurrentProperty, value); }
    public static readonly DependencyProperty IsGuidanceFreshProperty = DependencyProperty.Register(nameof(IsGuidanceFresh), typeof(bool), typeof(PolarFieldView), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool IsGuidanceFresh { get => (bool)GetValue(IsGuidanceFreshProperty); set => SetValue(IsGuidanceFreshProperty, value); }
    public double PixelScale { get => (double)GetValue(PixelScaleProperty); set => SetValue(PixelScaleProperty, value); }
    public PolarFieldView() {
        Loaded += (_, _) => UiText.Instance.PropertyChanged += LanguageChanged;
        Unloaded += (_, _) => UiText.Instance.PropertyChanged -= LanguageChanged;
    }
    private void LanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();
    private static Brush Color(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    public static readonly DependencyProperty FocusAlignmentProperty = DependencyProperty.Register(nameof(FocusAlignment), typeof(bool), typeof(PolarFieldView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender,
        (d, _) => ((PolarFieldView)d).focusedRegion = null));
    private Rect? focusedRegion;
    private Size focusedImageSize;
    public bool FocusAlignment { get => (bool)GetValue(FocusAlignmentProperty); set => SetValue(FocusAlignmentProperty, value); }
    public static readonly DependencyProperty DetailModeProperty = DependencyProperty.Register(nameof(DetailMode), typeof(bool), typeof(PolarFieldView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool DetailMode { get => (bool)GetValue(DetailModeProperty); set => SetValue(DetailModeProperty, value); }
    private static readonly DependencyPropertyKey ImageScalePropertyKey = DependencyProperty.RegisterReadOnly(nameof(ImageScale), typeof(double), typeof(PolarFieldView), new PropertyMetadata(1.0));
    public static readonly DependencyProperty ImageScaleProperty = ImageScalePropertyKey.DependencyProperty;
    public double ImageScale => (double)GetValue(ImageScaleProperty);
    public static readonly DependencyProperty ReferenceScaleProperty = DependencyProperty.Register(nameof(ReferenceScale), typeof(double), typeof(PolarFieldView), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double ReferenceScale { get => (double)GetValue(ReferenceScaleProperty); set => SetValue(ReferenceScaleProperty, value); }
    public Rect VisibleImageRegion { get; private set; }
    public event Action? VisibleImageRegionChanged;
    private Brush Mint => Color("#43C98F");
    private Brush Amber => Color("#E9B85C");
    private Brush Muted => Color("#E5E7EB");

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
            focusedRegion = null;
            dc.DrawRectangle(TryFindResource("BackgroundBrush") as Brush ?? Color("#202020"), null, bounds);
            dc.Pop(); return;
        }
        dc.DrawRectangle(Color("#080F1B"), null, bounds);
        static bool Finite(PixelPoint? p) => p == null || double.IsFinite(p.Value.X) && double.IsFinite(p.Value.Y);
        bool validGeometry = Guidance is { Width: > 0, Height: > 0 } && Finite(Guidance.MountAxis) && Finite(Guidance.CelestialPole);
        bool polarisView=UsePolarisTarget && Guidance?.PolarisTarget is {};
        double width = validGeometry ? Guidance!.Width : Preview.PixelWidth;
        double height = validGeometry ? Guidance!.Height : Preview.PixelHeight;
        double scale = Math.Min(ActualWidth / width, ActualHeight / height);
        double cx = width / 2, cy = height / 2;
        if ((FocusAlignment || DetailMode) && IsCurrent && validGeometry && (polarisView ? Guidance?.PolarisTarget : Guidance?.CelestialPole) is { } target) {
            if (DetailMode) {
                // The target remains fixed at the center. Never fit both markers: an
                // off-screen RA axis is shown by the existing edge-direction indicator.
                cx = target.X; cy = target.Y;
                scale = Math.Max(8, ReferenceScale * 2);
            } else {
                var axis = (polarisView ? Guidance!.Polaris : Guidance!.MountAxis) ?? target;
                cx = (target.X + axis.X) / 2; cy = (target.Y + axis.Y) / 2;
                scale = Math.Max(scale, Math.Min(ActualWidth / Math.Max(width * .32, Math.Abs(target.X - axis.X) * 2.5), ActualHeight / Math.Max(height * .32, Math.Abs(target.Y - axis.Y) * 2.5)));
            }
        }
        // Focus is a user-selected camera viewport, not a per-measurement fit.
        // Keep it through lost matches and moving adjustment markers.
        if (FocusAlignment && !DetailMode) {
            if (focusedImageSize != new Size(width, height)) focusedRegion = null;
            if (focusedRegion == null && IsCurrent && validGeometry) {
                focusedRegion = new Rect(cx - ActualWidth / (2 * scale), cy - ActualHeight / (2 * scale), ActualWidth / scale, ActualHeight / scale);
                focusedImageSize = new Size(width, height);
            }
            if (focusedRegion is { } region) {
                cx = region.Left + region.Width / 2; cy = region.Top + region.Height / 2;
                scale = Math.Min(ActualWidth / region.Width, ActualHeight / region.Height);
            }
        }
        SetValue(ImageScalePropertyKey, scale);
        var visibleRegion=new Rect(cx - ActualWidth / (2 * scale), cy - ActualHeight / (2 * scale), ActualWidth / scale, ActualHeight / scale);
        if(VisibleImageRegion!=visibleRegion) {VisibleImageRegion=visibleRegion;VisibleImageRegionChanged?.Invoke();}
        var imageRect = new Rect(ActualWidth / 2 - cx * scale, ActualHeight / 2 - cy * scale, width * scale, height * scale);
        if (Preview != null) dc.DrawImage(Preview, imageRect);
        Point Map(PixelPoint p) => new(imageRect.Left + p.X * scale, imageRect.Top + p.Y * scale);
        if(IsPoleMasterMonitoring && IsCurrent && validGeometry && Guidance is {MountAxis: {} measuredAxis,CelestialPole: {} measuredPole}) {
            DrawMonitoringMarkers(dc,Map(measuredAxis),Map(measuredPole),bounds);
            if(!DetailMode)Label(dc,UiText.Get("MonitorLegend"),new(12,bounds.Height-28),Muted,12);
            if(!IsGuidanceFresh)Label(dc,UiText.Get("FieldGuidancePaused"),new(12,12),Muted,11);
            dc.Pop();return;
        }
        // Enlarge the same preview and map its actual target through the same
        // transform. Keep just the destination ring, without tracking overlays.
        if(DetailMode) {
            if(IsCurrent && validGeometry && (polarisView ? Guidance?.PolarisTarget : Guidance?.CelestialPole) is {} detailTarget) {
                var point=Map(detailTarget);
                if(double.IsFinite(point.X) && double.IsFinite(point.Y) && bounds.Contains(point))
                    dc.DrawEllipse(null,new Pen(Mint,2),point,15,15);
            }
            dc.Pop();return;
        }
        if(!DetailMode && !polarisView && PolarisCandidate is {} candidate) {
            var p=Map(new(candidate.X*width,candidate.Y*height));dc.DrawRectangle(null,new Pen(Amber,2),new Rect(p.X-12,p.Y-12,24,24));
            Label(dc,UiText.Get("PolarisCandidateLabel"),new Point(p.X+15,p.Y+10),Amber,11);
        }
        if(!DetailMode && !polarisView && Stars is { Width:>0,Height:>0 } stars) {
            bool tracked=stars.Verified && AreStarsFresh;
            var brush=tracked?Color("#62E7A8"):Color("#9EB4C4");
            var pen=new Pen(brush,1.2);if(!tracked)pen.DashStyle=DashStyles.Dash;
            if(stars.TemplateMarkers is {} template)foreach(var marker in template) {
                var point=Map(new(marker.Position.X*width/stars.Width,marker.Position.Y*height/stars.Height));
                if(!double.IsFinite(point.X)||!double.IsFinite(point.Y)||!bounds.Contains(point))continue;
                bool matched=tracked && stars.Markers.Any(s=>s.Id==marker.Id);
                var templatePen=new Pen(matched?Color("#62E7A8"):Amber,1.5);if(!matched)templatePen.DashStyle=DashStyles.Dash;
                dc.DrawEllipse(null,templatePen,point,13,13);
            }
            foreach(var star in stars.Markers) {
                var point=Map(new(star.Position.X*width/stars.Width,star.Position.Y*height/stars.Height));
                if(!double.IsFinite(point.X)||!double.IsFinite(point.Y)||!bounds.Contains(point))continue;
                dc.DrawRectangle(null,pen,new Rect(point.X-9,point.Y-9,18,18));
                dc.DrawEllipse(brush,null,point,1.5,1.5);
                if(star.Id<=3)Label(dc,star.Id.ToString(CultureInfo.InvariantCulture),new(point.X+11,point.Y-13),brush,10,false);
            }
            if(stars.Markers.Length>0)Label(dc,stars.TemplateMarkers!=null?UiText.Format("PatternMatchedStars",stars.Markers.Count(s=>s.Id!=11767)):UiText.Format(tracked?"TrackedStarBoxes":stars.Verified?"PausedStarBoxes":"DetectedStarBoxes",stars.Markers.Length),new(10,10),brush,11);
        }
        if(IsCurrent && polarisView && Guidance?.PolarisTarget is {} destination) {
            if(!Guidance.TargetIsPolaris && Guidance.Polaris is {} referenceStar) {
                var at=Map(referenceStar);
                if(bounds.Contains(at)) {
                    dc.DrawRectangle(null,new Pen(Amber,2),new Rect(at.X-10,at.Y-10,20,20));
                    if(!DetailMode)Label(dc,UiText.Get("ReferenceStar"),new(at.X+14,at.Y-14),Amber,11);
                }
            }
            var to=Map(destination);
            if(bounds.Contains(to)) {
                dc.DrawEllipse(null,new Pen(Mint,2.5),to,15,15);
                dc.DrawLine(new Pen(Mint,1),new(to.X-6,to.Y),new(to.X+6,to.Y));
                dc.DrawLine(new Pen(Mint,1),new(to.X,to.Y-6),new(to.X,to.Y+6));
                Label(dc,UiText.Get(Guidance.TargetIsPolaris?"PolarisDestination":"ReferenceDestination"),new(to.X+20,to.Y-26),Mint,12);
            } else EdgeMarker(dc,to,UiText.Get(Guidance.TargetIsPolaris?"PolarisDestination":"ReferenceDestination"),Mint,bounds,true,0);
            if(!DetailMode)Label(dc,UiText.Get(Guidance.TargetIsPolaris?"PolarisMoveLegend":"ReferenceMoveLegend"),new(12,bounds.Height-28),Muted,12);
        }
        if (IsCurrent && !polarisView && validGeometry && Guidance != null) {
            var axisBrush=IsPoleMasterMonitoring ? Color("#F36F70") : Amber;
            Point? pole = Guidance.CelestialPole is { } p ? Map(p) : null;
            Point? axis = Guidance.MountAxis is { } a ? Map(a) : null;
            if (!DetailMode && pole is { } center && double.IsFinite(PixelScale) && PixelScale > 0) {
                // Angular-distance guide rings, not a claimed observed stellar trajectory.
                int count = 0;
                foreach (double degrees in new[] { 1.0 / 60, 5.0 / 60, .25, .5, 1, 2, 5 }) {
                    double radius = Math.Tan(degrees * Math.PI / 180) * 206264.80624709636 / PixelScale * scale;
                    if (radius < 30 || radius > Math.Max(ActualWidth, ActualHeight) * .8) continue;
                    var ringPen = new Pen(Color("#7543C98F"), .8) { DashStyle = DashStyles.Dash };
                    var path = AngularRing(Guidance.CelestialPole!.Value, width, height, degrees, Map);
                    dc.DrawGeometry(null, ringPen, path);
                    var labelPosition = new Point(center.X + radius + 3, center.Y - 8);
                    if (bounds.Contains(labelPosition)) Label(dc, degrees >= 1 ? $"{degrees:0}°" : $"{degrees * 60:0}′", labelPosition, Muted, 10, false);
                    if (++count == 3) break;
                }
            }
            if (axis is { } axisPoint && pole is { } polePoint) {
                var connector = new Pen(axisBrush, 1.3) { DashStyle = DashStyles.Dash };
                dc.DrawLine(connector, axisPoint, polePoint);
                if (bounds.Contains(axisPoint) && bounds.Contains(polePoint)) DrawArrow(dc, axisPoint, polePoint, IsGuidanceFresh ? axisBrush : Muted);
            }
            if (axis is { } mount) {
                if (bounds.Contains(mount)) {
                    var pen = new Pen(axisBrush, 1.8);
                    dc.DrawEllipse(null, pen, mount, 8, 8);
                    dc.DrawLine(pen, new(mount.X - 13, mount.Y), new(mount.X + 13, mount.Y));
                    dc.DrawLine(pen, new(mount.X, mount.Y - 13), new(mount.X, mount.Y + 13));
                    Label(dc, UiText.Get("FieldAxis"), new Point(mount.X - 30, mount.Y - 34), axisBrush, DetailMode ? 10 : 11);
                } else EdgeMarker(dc, mount, UiText.Get("FieldAxisOutside"), Amber, bounds, false,
                    pole is { } otherPole && !bounds.Contains(otherPole) ? -12 : 0);
            }
            if (pole is { } celestial) {
                if (bounds.Contains(celestial)) {
                    var pen = new Pen(Mint, 2);
                    dc.DrawEllipse(null, pen, celestial, 6, 6);
                    dc.DrawEllipse(Mint, null, celestial, 1.8, 1.8);
                    Label(dc, UiText.Get("FieldPole"), new Point(celestial.X + 12, celestial.Y + 9), Mint, 11);
                } else EdgeMarker(dc, celestial, UiText.Get("FieldPoleOutside"), Mint, bounds, true,
                    axis is { } otherAxis && !bounds.Contains(otherAxis) ? 12 : 0);
            }
            if (axis is { } axisLocation && pole is { } poleLocation) {
                if (!DetailMode && (!bounds.Contains(axisLocation) || !bounds.Contains(poleLocation)) && bounds.Width >= 400 && bounds.Height >= 260)
                    Overview(dc, axisLocation, poleLocation, bounds);
                if (IsGuidanceFresh && !DetailMode && bounds.Width >= 280)
                    Label(dc, UiText.Get(IsPoleMasterMonitoring?"MonitorLegend":"FieldMarkerLegend"), new Point(12, bounds.Height - 28), Muted, 11);
            }
        }
        if (Preview != null && IsDriftView) Label(dc, UiText.Get("DriftImage"), new Point(12, 14), Muted, 11);
        if (Preview != null && !IsCurrent && !IsDriftView) {
            dc.DrawRectangle(Color("#66080F1B"), null, bounds);
        }
        if (Guidance != null && !IsGuidanceFresh && !IsDriftView && !polarisView) {
            dc.DrawRectangle(Color("#20080F1B"), null, bounds);
            Label(dc, UiText.Get("FieldGuidancePaused"), new Point(12, bounds.Height - 30), Muted, DetailMode ? 10 : 12);
        }
        dc.Pop();
    }

    private void DrawMonitoringMarkers(DrawingContext dc,Point current,Point target,Rect bounds) {
        var targetBrush=IsGuidanceFresh?Mint:Muted;
        var currentBrush=IsGuidanceFresh?Color("#F36F70"):Muted;
        if(bounds.Contains(target)) {
            dc.DrawRectangle(null,new Pen(targetBrush,2),new Rect(target.X-14,target.Y-14,28,28));
            if(!DetailMode)Label(dc,UiText.Get("MonitorTarget"),new(target.X+20,target.Y-30),targetBrush,11);
        } else EdgeMarker(dc,target,UiText.Get("MonitorTarget"),targetBrush,bounds,true,0);
        if(bounds.Contains(current)) {
            var pen=new Pen(currentBrush,2);
            dc.DrawLine(pen,new(current.X-9,current.Y),new(current.X+9,current.Y));
            dc.DrawLine(pen,new(current.X,current.Y-9),new(current.X,current.Y+9));
            if(!DetailMode)Label(dc,UiText.Get("MonitorCurrent"),new(current.X-30,current.Y+20),currentBrush,11);
        } else EdgeMarker(dc,current,UiText.Get("MonitorCurrent"),currentBrush,bounds,false,0);
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

    private static void DrawArrow(DrawingContext dc, Point from, Point to, Brush brush) {
        var direction = to - from;
        if (!double.IsFinite(direction.Length) || direction.Length < 36) return;
        direction.Normalize();
        var start = from + direction * 16;
        var tip = to - direction * 12;
        var normal = new Vector(-direction.Y, direction.X);
        dc.DrawLine(new Pen(brush, 2), start, tip);
        var head = new StreamGeometry();
        using (var path = head.Open()) {
            path.BeginFigure(tip, true, true);
            path.LineTo(tip - direction * 9 + normal * 5, true, false);
            path.LineTo(tip - direction * 9 - normal * 5, true, false);
        }
        dc.DrawGeometry(brush, null, head);
    }

    private void Overview(DrawingContext dc, Point axis, Point pole, Rect bounds) {
        // This is a uniformly scaled location map, not a second camera image.
        double panelWidth = Math.Min(204, bounds.Width * .4);
        Point ClampToView(Point p) => new(Math.Clamp(p.X, 24, bounds.Width - 24), Math.Clamp(p.Y, 42, bounds.Height - 42));
        double Cost(Rect candidate) {
            double Distance(Point p) {
                var marker = ClampToView(p);
                double dx = Math.Max(candidate.Left - marker.X, Math.Max(0, marker.X - candidate.Right));
                double dy = Math.Max(candidate.Top - marker.Y, Math.Max(0, marker.Y - candidate.Bottom));
                return dx * dx + dy * dy;
            }
            return 1 / (1 + Distance(axis)) + 1 / (1 + Distance(pole));
        }
        var panel = new Rect(12, 12, panelWidth, 148);
        foreach (var candidate in new[] {
            new Rect(bounds.Width - panelWidth - 12, 12, panelWidth, 148),
            new Rect(12, bounds.Height - 188, panelWidth, 148),
            new Rect(bounds.Width - panelWidth - 12, bounds.Height - 188, panelWidth, 148)
        }) if (Cost(candidate) < Cost(panel)) panel = candidate;
        dc.DrawRoundedRectangle(Color("#ED0B1522"), new Pen(Color("#64748B"), 1), panel, 7, 7);
        Label(dc, UiText.Get("FieldOverview"), new Point(panel.Left + 10, panel.Top + 9), Muted, 11, false);
        var map = new Rect(panel.Left + 12, panel.Top + 34, panel.Width - 24, 70);
        double left = Math.Min(0, Math.Min(axis.X, pole.X)), right = Math.Max(bounds.Width, Math.Max(axis.X, pole.X));
        double top = Math.Min(0, Math.Min(axis.Y, pole.Y)), bottom = Math.Max(bounds.Height, Math.Max(axis.Y, pole.Y));
        double scale = Math.Min(map.Width / (right - left), map.Height / (bottom - top));
        Point Map(Point p) => new(map.Left + map.Width / 2 + (p.X - (left + right) / 2) * scale,
            map.Top + map.Height / 2 + (p.Y - (top + bottom) / 2) * scale);
        var visible = new Rect(Map(bounds.TopLeft), Map(bounds.BottomRight));
        dc.DrawRectangle(Color("#253F5267"), new Pen(Muted, 1) { DashStyle = DashStyles.Dash }, visible);
        var a = Map(axis); var p = Map(pole);
        dc.DrawLine(new Pen(Amber, 1), a, p);
        dc.DrawEllipse(Color("#0B1522"), new Pen(Amber, 1.7), a, 4, 4);
        dc.DrawLine(new Pen(Amber, 1.7), a - new Vector(7, 0), a + new Vector(7, 0));
        dc.DrawLine(new Pen(Amber, 1.7), a - new Vector(0, 7), a + new Vector(0, 7));
        dc.DrawEllipse(Color("#0B1522"), new Pen(Mint, 1.7), p, 4, 4);
        dc.DrawEllipse(Mint, null, p, 1.3, 1.3);
        Label(dc, UiText.Get("FieldViewport"), new Point(panel.Left + 10, panel.Top + 110), Muted, 10, false);
        Label(dc, UiText.Get("FieldAxis"), new Point(panel.Left + 10, panel.Top + 128), Amber, 10, false);
        Label(dc, UiText.Get("FieldTargetShort"), new Point(panel.Left + panel.Width * .65, panel.Top + 128), Mint, 10, false);
    }

    private void EdgeMarker(DrawingContext dc, Point outside, string label, Brush brush, Rect bounds, bool isPole, double laneOffset) {
        var center = new Point(bounds.Width / 2, bounds.Height / 2);
        var direction = outside - center;
        if (!double.IsFinite(direction.X) || !double.IsFinite(direction.Y) || direction.Length < 1) return;
        double factor = Math.Min(Math.Max(1, bounds.Width / 2 - 24) / Math.Max(Math.Abs(direction.X), .001),
            Math.Max(1, bounds.Height / 2 - 42) / Math.Max(Math.Abs(direction.Y), .001));
        var point = center + direction * factor;
        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        point += normal * laneOffset;
        var pen = new Pen(brush, 2);
        dc.DrawEllipse(Color("#ED0B1522"), pen, point, 9, 9);
        if (isPole) dc.DrawEllipse(brush, null, point, 2, 2);
        else {
            dc.DrawLine(pen, point - new Vector(13, 0), point + new Vector(13, 0));
            dc.DrawLine(pen, point - new Vector(0, 13), point + new Vector(0, 13));
        }
        var tip = point + direction * 19;
        dc.DrawLine(pen, tip - direction * 6 + normal * 4, tip);
        dc.DrawLine(pen, tip - direction * 6 - normal * 4, tip);
        Label(dc, label, point - direction * 65 + normal * (isPole ? 18 : -20), brush, 11);
    }
}
