using System;
using System.Linq;
using Nina.LivePolarAlignment.Core;

namespace Nina.LivePolarAlignment.Capture;

public sealed record PoleMasterCalibrationResult(PolarAlignmentSession Session, double AxisSpreadArcseconds, SolvedPolarFrame? LastFrame=null, double? PatternScale=null, double? ReprojectionRmsPixels=null);

public static class PoleMasterCalibration {
    public static PoleMasterCalibrationResult Calculate(SolvedPolarFrame first, SolvedPolarFrame middle,
        SolvedPolarFrame last, double latitude, double longitude) {
        double? patternScale=null;
        var frames=new[]{first,middle,last};
        if(frames.All(f=>f.Stars?.TemplateMarkers!=null && f.Stars.Markers.Length>=2)) {
            var scales=frames.Where(f=>f.Stars!.Markers.Length>=3).Select(f=>f.PixelScaleArcseconds).Order().ToArray();
            patternScale=scales.Length==0?first.PixelScaleArcseconds:scales[scales.Length/2];
            SolvedPolarFrame Refit(SolvedPolarFrame f) {
                double year=2000+(f.CapturedAtUtc.UtcDateTime-new DateTime(2000,1,1)).TotalDays/365.25;
                var result=PolarisPattern.RefitAtScale(new(PolarAlignmentGeometry.FromEquatorial(f.RightAscensionDegrees,f.DeclinationDegrees,f.PositionAngleDegrees,f.Flipped),f.PixelScaleArcseconds,f.Flipped,0,f.Stars!.Markers),f.Width,f.Height,year,patternScale.Value,default);
                var c=result.Orientation.Center;var up=result.Orientation.Up;double ra=(Math.Atan2(c.Y,c.X)*180/Math.PI+360)%360,dec=Math.Asin(c.Z)*180/Math.PI;
                var basis=PolarAlignmentGeometry.FromEquatorial(ra,dec,0);double pa=Math.Atan2(up.Dot(basis.Right),up.Dot(basis.Up))*180/Math.PI;
                return f with{RightAscensionDegrees=ra,DeclinationDegrees=dec,PositionAngleDegrees=pa,PixelScaleArcseconds=patternScale.Value};
            }
            first=Refit(first);middle=Refit(middle);last=Refit(last);
            NINA.Core.Utility.Logger.Info($"Polar fixed-template calibration: shared lens scale={patternScale:G17} arcsec/pixel");
        }
        foreach(var frame in new[]{first,middle,last})
            NINA.Core.Utility.Logger.Info($"Polar calibration input: UTC={frame.CapturedAtUtc:O}; RA={frame.RightAscensionDegrees:G17}; Dec={frame.DeclinationDegrees:G17}; PA={frame.PositionAngleDegrees:G17}; scale={frame.PixelScaleArcseconds:G17}; flipped={frame.Flipped}; stars={frame.Stars?.Markers.Length ?? 0}");
        var early = Pair(first, middle);
        var late = Pair(middle, last);
        var full = Pair(first, last);
        // Two independent intervals must describe the same fixed mechanical axis.
        // This is consistency evidence, not a measurement of absolute accuracy.
        var a = early.CalibratedAxis!.Value; var b = late.CalibratedAxis!.Value; var c = full.CalibratedAxis!.Value;
        double Distance(Vector3d x, Vector3d y) => Math.Atan2(x.Cross(y).Length, x.Dot(y)) * 206264.80624709636;
        double spread = Math.Max(Distance(a,b), Math.Max(Distance(a,c),Distance(b,c)));
        double? reprojection=null;
        if(patternScale.HasValue) {
            var start=full.ToHorizon(first);var observed=full.ToHorizon(middle);var axis=c;
            Vector3d[] x=[start.Right,start.Up,start.Center],y=[observed.Right,observed.Up,observed.Center];
            double sine=x.Zip(y,(p,q)=>axis.Dot(p.Cross(q))).Sum();
            double cosine=x.Zip(y,(p,q)=>p.Dot(q)-p.Dot(axis)*q.Dot(axis)).Sum();
            double angle=Math.Atan2(sine,cosine);
            Vector3d Turn(Vector3d p)=>p*Math.Cos(angle)+axis.Cross(p)*Math.Sin(angle)+axis*(axis.Dot(p)*(1-Math.Cos(angle)));
            var predicted=new SkyOrientation(Turn(start.Center),Turn(start.Right),Turn(start.Up));
            double focal=206264.80624709636/middle.PixelScaleArcseconds;
            var errors=middle.Stars!.Markers.Select(s=> {
                var sky=(observed.Center+observed.Right*((s.Position.X-middle.Width/2d)/focal)-observed.Up*((s.Position.Y-middle.Height/2d)/focal)).Normalized();
                var p=PolarAlignmentGeometry.ProjectDirection(sky,predicted,focal,middle.Width/2d,middle.Height/2d)!.Value;
                return Math.Pow(p.X-s.Position.X,2)+Math.Pow(p.Y-s.Position.Y,2);
            }).ToArray();
            reprojection=Math.Sqrt(errors.Average());
            NINA.Core.Utility.Logger.Info($"Polar fixed-axis middle-frame reprojection RMS={reprojection:F4}px; axis spread={spread:F3} arcsec");
        }
        NINA.Core.Utility.Logger.Info($"Polar calibration axis consistency: early-late={Distance(a,b):F3}; early-full={Distance(a,c):F3}; late-full={Distance(b,c):F3} arcsec");
        // For the fixed template, validate the actual middle-image star positions
        // against the full-span axis. Independent short-arc axes amplify the same
        // centroid/lens errors and are not a direct image residual. Use the same
        // 1.5-pixel bound as the accepted template fit, preserving real flex rejection.
        if(patternScale.HasValue && (reprojection is not {} rms || !double.IsFinite(rms) || rms>1.5))
            throw new InvalidOperationException(UI.UiText.Format("PoleMasterPatternInconsistent",reprojection??double.NaN));
        if (!double.IsFinite(spread) || (!patternScale.HasValue && spread > 60))
            throw new InvalidOperationException(UI.UiText.Format("PoleMasterInconsistent", spread));
        return new(full, spread,last,patternScale,reprojection);

        PolarAlignmentSession Pair(SolvedPolarFrame start, SolvedPolarFrame end) {
            var session = new PolarAlignmentSession(latitude, longitude);
            session.AddCalibration(start); session.AddCalibration(end);
            return session;
        }
    }
}
