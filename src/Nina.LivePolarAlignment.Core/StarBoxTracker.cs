namespace Nina.LivePolarAlignment.Core;

public sealed record StarBoxTracking(TrackedStar[] Markers,bool Verified);

/// <summary>Frame-to-frame identities for visual boxes during RA motion. Positions always
/// come from current measured centroids. Never used as an astrometric alignment solution.</summary>
public sealed class StarBoxTracker {
    private TrackedStar[] previous=[];
    private int width,height,nextId=1;
    public StarBoxTracking Update(PixelPoint[] current,int imageWidth,int imageHeight) {
        if(previous.Length>=8 && current.Length>=8 && width==imageWidth && height==imageHeight) {
            try {
                const double tolerance=3;
                var registration=FrameRegistration.Estimate(previous.Select(s=>s.Position).ToArray(),current,tolerance);
                var candidates=new List<(int I,int J,double Distance)>();
                for(int i=0;i<previous.Length;i++) {
                    var predicted=registration.Transform.Apply(previous[i].Position);
                    for(int j=0;j<current.Length;j++) {
                        double d=Math.Pow(predicted.X-current[j].X,2)+Math.Pow(predicted.Y-current[j].Y,2);
                        if(d<tolerance*tolerance)candidates.Add((i,j,d));
                    }
                }
                var usedPrevious=new HashSet<int>();var usedCurrent=new HashSet<int>();var matched=new List<TrackedStar>();
                foreach(var pair in candidates.OrderBy(p=>p.Distance))
                    if(!usedPrevious.Contains(pair.I) && !usedCurrent.Contains(pair.J)) {
                        usedPrevious.Add(pair.I);usedCurrent.Add(pair.J);
                        matched.Add(new(previous[pair.I].Id,current[pair.J]));
                    }
                if(matched.Count>=Math.Max(8,Math.Min(previous.Length,current.Length)/2)
                    && matched.Max(s=>s.Position.X)-matched.Min(s=>s.Position.X)>imageWidth*.15
                    && matched.Max(s=>s.Position.Y)-matched.Min(s=>s.Position.Y)>imageHeight*.15) {
                    previous=matched.OrderBy(s=>s.Id).ToArray();
                    return new(previous,true);
                }
            } catch(InvalidOperationException) { } catch(ArgumentException) { }
        }
        width=imageWidth;height=imageHeight;
        previous=current.Take(FrameRegistration.MaximumStars).Select(p=>new TrackedStar(nextId++,p)).ToArray();
        return new(previous,false);
    }
}
