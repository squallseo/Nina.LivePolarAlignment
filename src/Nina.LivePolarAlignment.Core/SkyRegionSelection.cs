namespace Nina.LivePolarAlignment.Core;

public readonly record struct SkyRegion(int X, int Y, int Width, int Height, string Name);

/// <summary>Solver-only exclusion masks. Sensor coordinates and retained RAW samples never move.</summary>
public static class SkyRegionSelection {
    public static SkyRegion[] Regions(int width, int height, PixelPoint? candidate = null) {
        SkyRegion Box(double x,double y,double w,double h,string name) => new(
            Math.Clamp((int)(x*width),0,width-1),Math.Clamp((int)(y*height),0,height-1),
            Math.Max(1,(int)(w*width)),Math.Max(1,(int)(h*height)),name);
        var list=new List<SkyRegion>();
        if(candidate is { } p)list.Add(Box(Math.Clamp(p.X/width-.3,0,.4),Math.Clamp(p.Y/height-.35,0,.3),.6,.7,"candidate"));
        list.Add(new(0,0,width,height,"automatic sky mask"));
        list.Add(Box(.1875,.3125,.5859375,2d/3,"lower sky"));
        list.Add(Box(.2,0,.6,2d/3,"upper sky"));
        list.Add(Box(.2,.16,.6,2d/3,"central sky"));
        return list.ToArray();
    }
    public static ushort[] Mask(ushort[] raw,int width,int height,SkyRegion region,bool classifyTiles,CancellationToken token) {
        if(raw.Length!=(long)width*height || width<=0 || height<=0)throw new ArgumentException("Invalid image dimensions.");
        var sample=raw.Where((_,i)=>i%Math.Max(1,raw.Length/4096)==0).Order().ToArray();
        ushort globalMedian=sample[sample.Length/2];
        double low=sample[sample.Length/4],spread=Math.Max(1,globalMedian-low);
        var local=new List<ushort>();
        for(int y=Math.Max(0,region.Y);y<Math.Min(height,region.Y+region.Height);y+=4)
            for(int x=Math.Max(0,region.X);x<Math.Min(width,region.X+region.Width);x+=4)local.Add(raw[y*width+x]);
        if(local.Count==0)throw new ArgumentException("Sky region is outside the image.");
        local.Sort();ushort background=classifyTiles?sample[sample.Length/4]:local[local.Count/2];
        var result=Enumerable.Repeat(background,raw.Length).ToArray();
        for(int ty=0;ty<height;ty+=32)for(int tx=0;tx<width;tx+=32) {
            token.ThrowIfCancellationRequested();
            bool clear=true;
            if(classifyTiles) {
                var tile=new List<ushort>();
                for(int y=ty;y<Math.Min(height,ty+32);y+=4)for(int x=tx;x<Math.Min(width,tx+32);x+=4)tile.Add(raw[y*width+x]);
                tile.Sort();double median=tile[tile.Count/2],range=tile[tile.Count*9/10]-tile[tile.Count/10];
                clear=median<low+Math.Max(12,spread*8) && range<Math.Max(16,spread*6);
            }
            if(!clear)continue;
            for(int y=Math.Max(ty,region.Y);y<Math.Min(Math.Min(height,ty+32),region.Y+region.Height);y++)
                for(int x=Math.Max(tx,region.X);x<Math.Min(Math.Min(width,tx+32),region.X+region.Width);x++)result[y*width+x]=raw[y*width+x];
        }
        return result;
    }
    public static PixelPoint? BrightestCandidate(ushort[] raw,int width,int height,CancellationToken token) {
        var sky=Mask(raw,width,height,new(0,0,width,height,"sky"),true,token);
        var stars=StarCentroids.Detect(sky,width,height,sky,width,height,token);
        return stars.Length==0?null:stars.OrderByDescending(p=>raw[Math.Clamp((int)p.Y,0,height-1)*width+Math.Clamp((int)p.X,0,width-1)]).First();
    }
    public static PixelPoint? ConfirmStarNear(ushort[] raw,int width,int height,double x,double y,int searchRadius=8) {
        if(searchRadius is <1 or >24)throw new ArgumentOutOfRangeException(nameof(searchRadius));
        int cx=(int)Math.Round(x),cy=(int)Math.Round(y);
        int margin=Math.Max(16,searchRadius+5);
        if(cx<margin || cy<margin || cx>=width-margin || cy>=height-margin)return null;
        var border=new List<ushort>();
        for(int i=-14;i<=14;i++){border.Add(raw[(cy-14)*width+cx+i]);border.Add(raw[(cy+14)*width+cx+i]);}
        border.Sort();double background=border[border.Count/2],noise=Math.Max(1,background-border[border.Count/6]);
        int mx=cx,my=cy;
        for(int yy=cy-searchRadius;yy<=cy+searchRadius;yy++)for(int xx=cx-searchRadius;xx<=cx+searchRadius;xx++)if(raw[yy*width+xx]>raw[my*width+mx]){mx=xx;my=yy;}
        double sum=0,sx=0,sy=0;int bright=0;
        for(int yy=my-4;yy<=my+4;yy++)for(int xx=mx-4;xx<=mx+4;xx++) {
            double weight=Math.Max(0,raw[yy*width+xx]-background-noise*3);
            if(weight>noise*3)bright++;sum+=weight;sx+=weight*(xx+.5);sy+=weight*(yy+.5);
        }
        return sum>0 && bright is >=3 and <=80 ? new(sx/sum,sy/sum) : null;
    }
}
