namespace Nina.LivePolarAlignment.Core;

public readonly record struct RigidTransform(double RotationRadians, double TranslationX, double TranslationY) {
    public PixelPoint Apply(PixelPoint p) => new(Math.Cos(RotationRadians)*p.X-Math.Sin(RotationRadians)*p.Y+TranslationX,Math.Sin(RotationRadians)*p.X+Math.Cos(RotationRadians)*p.Y+TranslationY);
}
public readonly record struct RegistrationResult(RigidTransform Transform, int MatchedStars, double RmsPixels);

public static class FrameRegistration {
    public const int MaximumStars = 35;
    public const int MaximumHypotheses = 4096;
    // Brightest stars should come first. Bounded deterministic pair hypotheses avoid a random
    // RANSAC seed and support unordered detections, missing stars, and spurious detections.
    public static RegistrationResult Estimate(IReadOnlyList<PixelPoint> reference, IReadOnlyList<PixelPoint> current, double maxResidualPixels=2) {
        if(reference.Count<3 || current.Count<3) throw new ArgumentException("At least three stars per frame are required.");
        if(!double.IsFinite(maxResidualPixels) || maxResidualPixels<=0) throw new ArgumentOutOfRangeException(nameof(maxResidualPixels));
        if(reference.Concat(current).Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y))) throw new ArgumentException("Star coordinates must be finite.");
        var r=reference.Take(MaximumStars).ToArray(); var c=current.Take(MaximumStars).ToArray();
        List<(PixelPoint A,PixelPoint B)> best=[]; double bestError=double.PositiveInfinity; RigidTransform transform=default;
        int evaluated=0;
        // Long baselines are less sensitive to centroid noise. A fixed hypothesis budget
        // bounds full matching at 4096 * 35 * 35 distance checks plus three refinements.
        var pairs=(from i in Enumerable.Range(0,r.Length) from j in Enumerable.Range(i+1,r.Length-i-1)
                   let distance=Math.Pow(r[j].X-r[i].X,2)+Math.Pow(r[j].Y-r[i].Y,2)
                   orderby distance descending select (i,j)).ToArray();
        foreach(var (i,j) in pairs) {
            double dx=r[j].X-r[i].X,dy=r[j].Y-r[i].Y,len=Math.Sqrt(dx*dx+dy*dy);
            if(len<maxResidualPixels*5) continue;
            for(int k=0;k<c.Length;k++) for(int l=0;l<c.Length;l++) {
                if(k==l) continue;
                double ex=c[l].X-c[k].X,ey=c[l].Y-c[k].Y;
                if(Math.Abs(Math.Sqrt(ex*ex+ey*ey)-len)>maxResidualPixels*2) continue;
                if(evaluated++ >= MaximumHypotheses) goto Refine;
                double angle=Math.Atan2(ey,ex)-Math.Atan2(dy,dx);
                var candidate=new RigidTransform(angle,c[k].X-Math.Cos(angle)*r[i].X+Math.Sin(angle)*r[i].Y,c[k].Y-Math.Sin(angle)*r[i].X-Math.Cos(angle)*r[i].Y);
                var matches=Match(r,c,candidate,maxResidualPixels); double error=Error(matches,candidate);
                if(matches.Count>best.Count || matches.Count==best.Count && error<bestError) { best=matches;bestError=error;transform=candidate; }
                if(best.Count>=Math.Ceiling(Math.Min(r.Length,c.Length)*.8) && bestError/best.Count<maxResidualPixels*maxResidualPixels*.25) goto Refine;
            }
        }
        Refine:
        int minimumMatches=Math.Max(3,(int)Math.Ceiling(Math.Min(r.Length,c.Length)*.4));
        if(best.Count<minimumMatches) throw new InvalidOperationException("No reliable star registration; plate solve again.");
        for(int iter=0;iter<3;iter++) { transform=Fit(best); best=Match(r,c,transform,maxResidualPixels); }
        if(best.Count<minimumMatches) throw new InvalidOperationException("Insufficient consistent stars after refinement.");
        return new(transform,best.Count,Math.Sqrt(Error(best,transform)/best.Count));
    }
    private static List<(PixelPoint A,PixelPoint B)> Match(PixelPoint[] a,PixelPoint[] b,RigidTransform t,double tolerance) {
        var candidates=new List<(int I,int J,double D)>();
        double cos=Math.Cos(t.RotationRadians),sin=Math.Sin(t.RotationRadians),limit=tolerance*tolerance;
        for(int i=0;i<a.Length;i++) {
            double px=cos*a[i].X-sin*a[i].Y+t.TranslationX,py=sin*a[i].X+cos*a[i].Y+t.TranslationY;
            for(int j=0;j<b.Length;j++) { double dx=px-b[j].X,dy=py-b[j].Y,d=dx*dx+dy*dy; if(d<limit) candidates.Add((i,j,d)); }
        }
        var usedA=new HashSet<int>(); var usedB=new HashSet<int>(); var matches=new List<(PixelPoint,PixelPoint)>();
        foreach(var p in candidates.OrderBy(p=>p.D)) if(!usedA.Contains(p.I)&&!usedB.Contains(p.J)) { usedA.Add(p.I);usedB.Add(p.J);matches.Add((a[p.I],b[p.J])); }
        return matches;
    }
    private static double Error(List<(PixelPoint A,PixelPoint B)> pairs,RigidTransform t) => pairs.Sum(p=> { var q=t.Apply(p.A);return Math.Pow(q.X-p.B.X,2)+Math.Pow(q.Y-p.B.Y,2); });
    private static RigidTransform Fit(List<(PixelPoint A,PixelPoint B)> pairs) {
        double ax=pairs.Average(p=>p.A.X),ay=pairs.Average(p=>p.A.Y),bx=pairs.Average(p=>p.B.X),by=pairs.Average(p=>p.B.Y);
        double dot=0,cross=0;
        foreach(var p in pairs) { double x=p.A.X-ax,y=p.A.Y-ay,u=p.B.X-bx,v=p.B.Y-by;dot+=x*u+y*v;cross+=x*v-y*u; }
        double angle=Math.Atan2(cross,dot);
        return new(angle,bx-Math.Cos(angle)*ax+Math.Sin(angle)*ay,by-Math.Sin(angle)*ax-Math.Cos(angle)*ay);
    }
}
