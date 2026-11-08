using System.Globalization;

namespace Nina.LivePolarAlignment.Core;

public sealed record PatternStar(int Id, Vector3d Direction, double Magnitude);
public sealed record PolarisPatternResult(SkyOrientation Orientation, double PixelScale, bool Flipped, double RmsPixels, TrackedStar[] Stars);

/// <summary>Local polar template matching: clicked Polaris anchors the template;
/// relative angular separations supply rotation, parity and stock-lens scale.
/// No image files, external solver, mount pointing or all-sky search.</summary>
public static class PolarisPattern {
    public static PatternStar[] Catalog(double year) {
        using var stream=typeof(PolarisPattern).Assembly.GetManifestResourceStream("Nina.LivePolarAlignment.Core.Data.north-pole-hyg41.csv")!;
        using var reader=new StreamReader(stream);reader.ReadLine();var stars=new List<PatternStar>();
        while(reader.ReadLine() is {} line) {
            var a=line.Replace("\"","").Split(',');double D(int i)=>double.Parse(a[i],CultureInfo.InvariantCulture);
            double dec=D(2),ra=D(1)*15;
            ra+=D(4)*(year-2000)/3600000/Math.Cos(dec*Math.PI/180);
            dec+=D(5)*(year-2000)/3600000;
            stars.Add(new(int.Parse(a[0]),PolarAlignmentGeometry.FromEquatorial(ra,dec,0).Center,D(3)));
        }
        return stars.ToArray();
    }

    public static PolarisPatternResult Match(IReadOnlyList<PixelPoint> detections,int width,int height,PixelPoint? clickedPolaris,
        double year,CancellationToken token, IReadOnlyList<PatternStar>? template=null, int minimumStars=3, bool allowSparse=true,double? calibratedScale=null) {
        if(width<=0 || height<=0 || detections.Count<(allowSparse?2:minimumStars))throw new InvalidOperationException("Show Polaris and at least one of the fixed surrounding template stars.");
        var observed=detections.Take(60).ToArray();
        if(observed.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)||p.X<0||p.Y<0||p.X>=width||p.Y>=height))throw new ArgumentException("Invalid star centroids.");
        var catalog=(template??Catalog(year)).OrderBy(s=>s.Magnitude).ToArray();
        var polaris=catalog.Single(s=>s.Id==11767);
        // Fixed northern PoleMaster overlay, identified against the published manual:
        // Polaris + HIP 37391, 115746, 19454, 84535. Never select arbitrary field stars.
        var neighbors=catalog.Where(s=>s.Id!=11767).ToArray();
        var seeds=clickedPolaris is {} click
            ? observed.Where(p=>Distance(p,click)<20).OrderBy(p=>Distance(p,click)).Take(1).ToArray()
            : observed.Take(8).ToArray();
        if(seeds.Length==0)throw new InvalidOperationException("No star near the Polaris click. Click the star's centre.");
        var candidates=new List<(SkyOrientation Pose,double Focal,bool Flip,int Count,double Error)>();
        double minimumFocal=206264.80624709636/40,maximumFocal=206264.80624709636/24;
        foreach(var seed in seeds)foreach(var point in observed.Take(35)) {
            token.ThrowIfCancellationRequested();
            if(Distance(seed,point)<60)continue;
            Vector3d Ray(PixelPoint p,double f,bool flip)=>new Vector3d((p.X-width/2d)*(flip?-1:1),height/2d-p.Y,f).Normalized();
            double Separation(double f)=>Math.Acos(Math.Clamp(Ray(seed,f,false).Dot(Ray(point,f,false)),-1,1));
            double loAngle=Separation(maximumFocal),hiAngle=Separation(minimumFocal);
            foreach(var star in neighbors.Take(300)) {
                double angle=Math.Acos(Math.Clamp(polaris.Direction.Dot(star.Direction),-1,1));
                if(angle<loAngle || angle>hiAngle)continue;
                double lo=minimumFocal,hi=maximumFocal;
                for(int i=0;i<24;i++){double mid=(lo+hi)/2;if(Separation(mid)>angle)lo=mid;else hi=mid;}
                double focal=(lo+hi)/2;
                foreach(bool flip in new[]{false,true}) {
                    var a=Ray(seed,focal,flip);var b=Ray(point,focal,flip);
                    var u=(b-a*a.Dot(b)).Normalized();var v=a.Cross(u);
                    var x=polaris.Direction;var y=(star.Direction-x*x.Dot(star.Direction)).Normalized();var z=x.Cross(y);
                    Vector3d Map(Vector3d p)=>x*p.Dot(a)+y*p.Dot(u)+z*p.Dot(v);
                    var pose=new SkyOrientation(Map(new(0,0,1)),Map(new(1,0,0))*(flip?-1:1),Map(new(0,1,0)));
                    var pairs=Pairs(pose,focal,catalog,observed,width,height,4);
                    if(pairs.Count<minimumStars || !pairs.Any(p=>p.Star.Id==11767))continue;
                    double error=pairs.Sum(p=>Squared(p.Projected,p.Measured));
                    candidates.Add((pose,focal,flip,pairs.Count,error));
                }
            }
        }
        var fits=new List<PolarisPatternResult>();
        foreach(var candidate in candidates.OrderByDescending(c=>c.Count).ThenBy(c=>c.Error).Take(24)) {
            token.ThrowIfCancellationRequested();var pose=candidate.Pose;double focal=candidate.Focal;
            for(int iteration=0;iteration<8;iteration++) {
                var pairs=Pairs(pose,focal,catalog,observed,width,height,iteration<3?4:2.5);
                if(pairs.Count<minimumStars)break;
                var normal=new double[4,4];var rhs=new double[4];
                foreach(var pair in pairs) {
                    var basePoint=pair.Projected;var jac=new double[2,4];
                    for(int j=0;j<4;j++) {
                        const double step=1e-6;
                        var shifted=Project(pair.Star.Direction,j==3?pose:Turn(pose,j,step),j==3?focal*Math.Exp(step):focal,width,height)!.Value;
                        jac[0,j]=(shifted.X-basePoint.X)/step;jac[1,j]=(shifted.Y-basePoint.Y)/step;
                    }
                    double[] residual=[pair.Measured.X-basePoint.X,pair.Measured.Y-basePoint.Y];
                    for(int j=0;j<4;j++)for(int k=0;k<2;k++) {
                        rhs[j]+=jac[k,j]*residual[k];
                        for(int l=0;l<4;l++)normal[j,l]+=jac[k,j]*jac[k,l];
                    }
                }
                var update=Solve(normal,rhs);if(update==null)break;
                for(int j=0;j<3;j++)pose=Turn(pose,j,Math.Clamp(update[j],-.005,.005));
                focal*=Math.Exp(Math.Clamp(update[3],-.02,.02));
            }
            var final=Pairs(pose,focal,catalog,observed,width,height,2.5);
            if(final.Count<minimumStars || !final.Any(p=>p.Star.Id==11767) || focal<minimumFocal || focal>maximumFocal
                || final.Max(p=>p.Measured.X)-final.Min(p=>p.Measured.X)<width*.15
                || final.Max(p=>p.Measured.Y)-final.Min(p=>p.Measured.Y)<height*.15)continue;
            double rms=Math.Sqrt(final.Sum(p=>Squared(p.Projected,p.Measured))/final.Count);
            if(rms>1.5)continue;
            if(clickedPolaris is {} clicked && Distance(final.Single(p=>p.Star.Id==11767).Measured,clicked)>20)continue;
            fits.Add(new(pose,206264.80624709636/focal,candidate.Flip,rms,final.Select(p=>new TrackedStar(p.Star.Id,p.Measured)).ToArray()));
        }
        var best=fits.OrderByDescending(f=>f.Stars.Length).ThenBy(f=>f.RmsPixels).FirstOrDefault();
        if(best==null && allowSparse)return Sparse(observed,clickedPolaris==null?seeds.Take(1).ToArray():seeds,catalog,width,height,token,calibratedScale);
        if(best==null)throw new InvalidOperationException("The fixed Polaris template did not match. Check the Polaris click and surrounding stars.");
        if(fits.Any(f=>f.Stars.Length>=best.Stars.Length-1 && f.RmsPixels<=best.RmsPixels+.3
            && (f.Flipped!=best.Flipped || Math.Acos(Math.Clamp(f.Orientation.Center.Dot(best.Orientation.Center),-1,1))>.001)))
            throw new InvalidOperationException("Polaris pattern is ambiguous. Include more surrounding stars before starting.");
        return best;
    }
    private static PolarisPatternResult Sparse(PixelPoint[] observed,PixelPoint[] seeds,PatternStar[] catalog,int width,int height,CancellationToken token,double? calibratedScale) {
        // With only Polaris + one known neighbor there is one rotation degree of
        // freedom. Do not fit scale or parity from two stars: use the stock QHY
        // optical scale and raw-sensor parity. Ambiguous radial identities fail.
        double scale=calibratedScale??206264.80624709636*3.75/25000;
        if(!double.IsFinite(scale)||scale is <24 or >40)throw new ArgumentException("Invalid calibrated template scale.");
        double focal=206264.80624709636/scale;
        var polaris=catalog.Single(s=>s.Id==11767);var fits=new List<PolarisPatternResult>();
        Vector3d Ray(PixelPoint p)=>new Vector3d(-(p.X-width/2d),height/2d-p.Y,focal).Normalized();
        foreach(var seed in seeds)foreach(var point in observed) {
            token.ThrowIfCancellationRequested();if(Distance(seed,point)<60)continue;
            var a=Ray(seed);var b=Ray(point);double measured=Math.Acos(Math.Clamp(a.Dot(b),-1,1));
            foreach(var star in catalog.Where(s=>s.Id!=11767)) {
                double expected=Math.Acos(Math.Clamp(polaris.Direction.Dot(star.Direction),-1,1));
                if(Math.Abs(expected-measured)*focal>Math.Max(2,expected*focal*.005))continue;
                var u=(b-a*a.Dot(b)).Normalized();var v=a.Cross(u);
                var x=polaris.Direction;var y=(star.Direction-x*x.Dot(star.Direction)).Normalized();var z=x.Cross(y);
                Vector3d Map(Vector3d p)=>x*p.Dot(a)+y*p.Dot(u)+z*p.Dot(v);
                var pose=new SkyOrientation(Map(new(0,0,1)),Map(new(1,0,0))*(-1),Map(new(0,1,0)));
                var pairs=Pairs(pose,focal,catalog,observed,width,height,3);
                if(pairs.Count<2 || !pairs.Any(p=>p.Star.Id==11767))continue;
                fits.Add(new(pose,scale,true,Math.Sqrt(pairs.Sum(p=>Squared(p.Projected,p.Measured))/pairs.Count),pairs.Select(p=>new TrackedStar(p.Star.Id,p.Measured)).ToArray()));
            }
        }
        var best=fits.OrderByDescending(f=>f.Stars.Length).ThenBy(f=>f.RmsPixels).FirstOrDefault()
            ?? throw new InvalidOperationException("No fixed template star matches the Polaris distance. Adjust exposure or click Polaris again.");
        if(fits.Any(f=>f.Stars.Length==best.Stars.Length && f.RmsPixels<=best.RmsPixels+.7
            && Math.Abs(f.Orientation.Up.Dot(best.Orientation.Up)-1)>.00001))
            throw new InvalidOperationException("Two fixed-template rotations fit. Show another template star to resolve the ambiguity.");
        return best;
    }
    // At fixed RA, adjustment bolts cannot reverse the camera roll. Compare
    // camera basis vectors, never RA/PA numbers which wrap near the pole.
    public static void ValidateAdjustmentOrientation(SkyOrientation reference,SkyOrientation candidate) {
        double limit=Math.Cos(5*Math.PI/180);
        if(reference.Center.Dot(candidate.Center)<limit || reference.Up.Dot(candidate.Up)<limit || reference.Right.Dot(candidate.Right)<limit)
            throw new InvalidOperationException("Polaris template changed camera orientation during adjustment. Holding the last verified measurement.");
    }
    private sealed record Pair(PatternStar Star,PixelPoint Measured,PixelPoint Projected);
    public static PolarisPatternResult MatchReference(IReadOnlyList<PixelPoint> detections, int width, int height,
        SkyOrientation reference, double pixelScale, bool flipped, double year, CancellationToken token) {
        if (width <= 0 || height <= 0 || !double.IsFinite(pixelScale) || pixelScale is <24 or >40)
            throw new ArgumentException("Invalid template reference geometry.");
        var focal=206264.80624709636/pixelScale;
        var catalog=Catalog(year).ToArray();
        // Star identities, parity and lens scale were established during calibration.
        // Only associate nearby detections; never infer a new sparse pattern here.
        var pairs=Pairs(reference,focal,catalog,detections.ToArray(),width,height,20);
        if(pairs.Count<2 || !pairs.Any(p=>p.Star.Id==11767)
            || pairs.Max(p=>Distance(p.Measured,pairs.Single(s=>s.Star.Id==11767).Measured))<60)
            throw new InvalidOperationException("The calibrated template stars left the tracking window.");
        foreach(var pair in pairs) {
            if(detections.Count(p=>Distance(p,pair.Projected)<20 && Distance(p,pair.Projected)<=Distance(pair.Measured,pair.Projected)+2)>1)
                throw new InvalidOperationException("A template tracking window contains ambiguous detections.");
        }
        return RefitAtScale(new(reference,pixelScale,flipped,0,pairs.Select(p=>new TrackedStar(p.Star.Id,p.Measured)).ToArray()),width,height,year,pixelScale,token);
    }
    public static PolarisPatternResult RefitAtScale(PolarisPatternResult initial,int width,int height,double year,double pixelScale,CancellationToken token) {
        if(!double.IsFinite(pixelScale)||pixelScale is <24 or >40 || initial.Stars.Length<2)throw new ArgumentException("Invalid fixed-template optical scale.");
        double focal=206264.80624709636/pixelScale;var pose=initial.Orientation;
        var catalog=Catalog(year).ToDictionary(s=>s.Id);
        for(int iteration=0;iteration<10;iteration++) {
            token.ThrowIfCancellationRequested();var normal=new double[3,3];var rhs=new double[3];
            foreach(var star in initial.Stars) {
                var direction=catalog[star.Id].Direction;var p=Project(direction,pose,focal,width,height)!.Value;var jac=new double[2,3];
                for(int j=0;j<3;j++){var q=Project(direction,Turn(pose,j,1e-6),focal,width,height)!.Value;jac[0,j]=(q.X-p.X)/1e-6;jac[1,j]=(q.Y-p.Y)/1e-6;}
                double[] residual=[star.Position.X-p.X,star.Position.Y-p.Y];
                for(int j=0;j<3;j++)for(int k=0;k<2;k++){rhs[j]+=jac[k,j]*residual[k];for(int l=0;l<3;l++)normal[j,l]+=jac[k,j]*jac[k,l];}
            }
            var update=Solve(normal,rhs)??throw new InvalidOperationException("Template stars do not constrain camera rotation.");
            for(int j=0;j<3;j++)pose=Turn(pose,j,Math.Clamp(update[j],-.005,.005));
        }
        double error=initial.Stars.Sum(s=>Squared(Project(catalog[s.Id].Direction,pose,focal,width,height)!.Value,s.Position));
        double rms=Math.Sqrt(error/initial.Stars.Length);
        if(rms>1.5)throw new InvalidOperationException("Template residual is too large with the calibrated lens scale.");
        return initial with{Orientation=pose,PixelScale=pixelScale,RmsPixels=rms};
    }
    private static List<Pair> Pairs(SkyOrientation pose,double focal,PatternStar[] catalog,PixelPoint[] observed,int w,int h,double tolerance) {
        var possible=new List<(PatternStar Star,int Index,PixelPoint Projected,double Distance)>();
        foreach(var star in catalog) {
            var p=Project(star.Direction,pose,focal,w,h);if(p is not {} q || q.X<0||q.Y<0||q.X>=w||q.Y>=h)continue;
            for(int i=0;i<observed.Length;i++){double d=Squared(q,observed[i]);if(d<tolerance*tolerance)possible.Add((star,i,q,d));}
        }
        var used=new HashSet<int>();var ids=new HashSet<int>();var result=new List<Pair>();
        foreach(var p in possible.OrderBy(p=>p.Distance))if(!used.Contains(p.Index)&&!ids.Contains(p.Star.Id)){used.Add(p.Index);ids.Add(p.Star.Id);result.Add(new(p.Star,observed[p.Index],p.Projected));}
        return result;
    }
    private static PixelPoint? Project(Vector3d p,SkyOrientation pose,double focal,int w,int h)=>PolarAlignmentGeometry.ProjectDirection(p,pose,focal,w/2d,h/2d);
    private static double Squared(PixelPoint a,PixelPoint b)=>Math.Pow(a.X-b.X,2)+Math.Pow(a.Y-b.Y,2);
    private static double Distance(PixelPoint a,PixelPoint b)=>Math.Sqrt(Squared(a,b));
    private static SkyOrientation Turn(SkyOrientation pose,int axis,double angle) {
        Vector3d n=axis==0?new(1,0,0):axis==1?new(0,1,0):new(0,0,1);
        Vector3d R(Vector3d p)=>p*Math.Cos(angle)+n.Cross(p)*Math.Sin(angle)+n*(n.Dot(p)*(1-Math.Cos(angle)));
        return new(R(pose.Center),R(pose.Right),R(pose.Up));
    }
    private static double[]? Solve(double[,] a,double[] b) {
        int n=b.Length;
        for(int i=0;i<n;i++) {
            int pivot=Enumerable.Range(i,n-i).MaxBy(j=>Math.Abs(a[j,i]));if(Math.Abs(a[pivot,i])<1e-10)return null;
            for(int k=0;k<n;k++)(a[i,k],a[pivot,k])=(a[pivot,k],a[i,k]);(b[i],b[pivot])=(b[pivot],b[i]);
            double d=a[i,i];for(int k=i;k<n;k++)a[i,k]/=d;b[i]/=d;
            for(int j=0;j<n;j++)if(j!=i){double f=a[j,i];for(int k=i;k<n;k++)a[j,k]-=f*a[i,k];b[j]-=f*b[i];}
        }
        return b;
    }
}
