using Nina.LivePolarAlignment.Core;
using Xunit;

namespace Nina.LivePolarAlignment.Core.Tests;

public sealed class StarTrackingTests {
    [Theory]
    [InlineData(false, 1)] [InlineData(true, 1)] [InlineData(false, 2)] [InlineData(true, 2)] [InlineData(false, 4)]
    [InlineData(false, 1, 6666.6666667)] [InlineData(true, 1, 6666.6666667)]
    [InlineData(false,1,6666.6666667,40)] [InlineData(true,1,6666.6666667,40)]
    public void RecoversSphericalPoseFromFixedStarsWithoutIncrementalDrift(bool flipped, int bin, double focal = 10000, double rotationDegrees = 0) {
        var initial = PolarAlignmentGeometry.FromEquatorial(150, 88, 30, flipped);
        var turn = rotationDegrees==0 ? new Vector3d(.2,.7,.4).Normalized() : initial.Center; double angle = rotationDegrees==0 ? .0015 : rotationDegrees*Math.PI/180;
        Vector3d Rotate(Vector3d v) => v*Math.Cos(angle)+turn.Cross(v)*Math.Sin(angle)+turn*turn.Dot(v)*(1-Math.Cos(angle));
        var actual = new SkyOrientation(Rotate(initial.Center), Rotate(initial.Right), Rotate(initial.Up));
        var random = new Random(38);
        var reference = Enumerable.Range(0,30).Select(_ => new PixelPoint(100+random.NextDouble()*1400, 100+random.NextDouble()*800)).ToArray();
        var current = reference.Select(p => {
            var sky = (initial.Center+initial.Right*((p.X-800)/focal)-initial.Up*((p.Y-500)/focal)).Normalized();
            var pixel = PolarAlignmentGeometry.ProjectDirection(sky,actual,focal,800,500)!.Value;
            return new PixelPoint(pixel.X/bin,pixel.Y/bin);
        }).Where(p=>p.X>=0 && p.Y>=0 && p.X<1600/bin && p.Y<1000/bin).Reverse().ToArray();
        var tracker = new AstrometricTracker(initial, reference, 1600,1000,206264.80624709636/focal,flipped);
        for (int i=0;i<5;i++) {
            var result = tracker.Track(current,1600/bin,1000/bin);
            Assert.True(result.Orientation.Center.Dot(actual.Center)>1-1e-11);
            Assert.True(result.Orientation.Up.Dot(actual.Up)>1-1e-11);
            Assert.InRange(result.RmsPixels,0,1e-4);
            Assert.Equal(current.Length,result.Stars);
            Assert.NotNull(result.Markers);Assert.Equal(result.Stars,result.Markers.Length);
            foreach(var marker in result.Markers) {
                var p=reference[marker.Id-1];
                var sky=(initial.Center+initial.Right*((p.X-800)/focal)-initial.Up*((p.Y-500)/focal)).Normalized();
                var expected=PolarAlignmentGeometry.ProjectDirection(sky,actual,focal,800,500)!.Value;
                Assert.InRange(Math.Abs(marker.Position.X-expected.X/bin),0,1e-6);
                Assert.InRange(Math.Abs(marker.Position.Y-expected.Y/bin),0,1e-6);
            }
        }
    }
    [Fact]
    public void StarCentroidsRejectHotPixelsAndMeasureRawCentres() {
        int width=512,height=384; var raw=Enumerable.Repeat((ushort)1000,width*height).ToArray();
        var expected = new List<PixelPoint>();
        for(int row=0;row<4;row++) for(int col=0;col<5;col++) {
            double x=40+col*90+.3,y=40+row*85+.7; expected.Add(new(x+.5,y+.5));
            for(int dy=-6;dy<=6;dy++) for(int dx=-6;dx<=6;dx++) {
                int px=(int)x+dx,py=(int)y+dy;
                raw[py*width+px]+=(ushort)(12000*Math.Exp(-((px-x)*(px-x)+(py-y)*(py-y))/3));
            }
        }
        raw[10*width+10]=50000;
        var found=StarCentroids.Detect(raw,width,height,raw,width,height,default);
        Assert.Equal(20,found.Length);
        foreach(var p in expected) Assert.Contains(found,s=>Math.Abs(s.X-p.X)<.05&&Math.Abs(s.Y-p.Y)<.05);
    }
    [Fact]
    public void UnrelatedFieldsCannotGenerateGuidance() {
        var random=new Random(41);
        PixelPoint[] Stars()=>Enumerable.Range(0,30).Select(_=>new PixelPoint(random.NextDouble()*1600,random.NextDouble()*1000)).ToArray();
        var tracker=new AstrometricTracker(PolarAlignmentGeometry.FromEquatorial(20,88,0),Stars(),1600,1000,2,false);
        Assert.Throws<InvalidOperationException>(()=>tracker.Track(Stars(),1600,1000));
    }
}
