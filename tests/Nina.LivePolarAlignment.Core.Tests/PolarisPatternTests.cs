using Nina.LivePolarAlignment.Core;
using Xunit;
namespace Nina.LivePolarAlignment.Core.Tests;
public sealed class PolarisPatternTests {
    [Fact] public void AdjustmentRejectsRecordedFalsePoseInsteadOfJumpingFrom14To4459Arcseconds() {
        var returned=PolarAlignmentGeometry.FromEquatorial(305.843,89.4439,243.701,true);
        var good=PolarAlignmentGeometry.FromEquatorial(288.246,89.518,225.896,true);
        var bad=PolarAlignmentGeometry.FromEquatorial(45.323,88.2666,176.943,true);
        PolarisPattern.ValidateAdjustmentOrientation(returned,good);
        Assert.Throws<InvalidOperationException>(()=>PolarisPattern.ValidateAdjustmentOrientation(returned,bad));
        Assert.Throws<InvalidOperationException>(()=>PolarisPattern.ValidateAdjustmentOrientation(good,bad));
        PolarisPattern.ValidateAdjustmentOrientation(good,PolarAlignmentGeometry.FromEquatorial(288.515,89.5169,225.971,true));
    }
    [Fact] public void SparseReacquisitionUsesMeasuredLensScaleAndExplicitPolarisSeed() {
        const double year=2026.85,scale=30.5;
        var catalog=PolarisPattern.Catalog(year);var pose=PolarAlignmentGeometry.FromEquatorial(40,88.5,147,true);
        PixelPoint P(int hip)=>PolarAlignmentGeometry.ProjectDirection(catalog.Single(s=>s.Id==hip).Direction,pose,206264.80624709636/scale,640,480)!.Value;
        var seed=P(11767);
        var result=PolarisPattern.Match(new[]{new PixelPoint(100,100),seed,P(84535)},1280,960,seed,year,default,calibratedScale:scale);
        Assert.Equal(scale,result.PixelScale);Assert.Equal(2,result.Stars.Length);
        Assert.True(result.Orientation.Center.Dot(pose.Center)>1-1e-10);
    }
    [Fact] public void CalibratedSparseTrackingFollowsSmallAdjustmentWithoutRefittingStockOptics() {
        var year=2026.85;var catalog=PolarisPattern.Catalog(year);
        var reference=PolarAlignmentGeometry.FromEquatorial(40,88.5,147,true);
        var actual=PolarAlignmentGeometry.FromEquatorial(40.03,88.52,147.02,true);
        const double scale=30.8557;
        PixelPoint P(int hip)=>PolarAlignmentGeometry.ProjectDirection(catalog.Single(s=>s.Id==hip).Direction,actual,206264.80624709636/scale,640,480)!.Value;
        var result=PolarisPattern.MatchReference(new[]{P(11767),P(84535),new PixelPoint(100,100)},1280,960,reference,scale,true,year,default);
        Assert.Equal(2,result.Stars.Length);Assert.Equal(scale,result.PixelScale);
        Assert.InRange(result.RmsPixels,0,.001);Assert.True(result.Orientation.Center.Dot(actual.Center)>1-1e-10);
        Assert.Throws<InvalidOperationException>(()=>PolarisPattern.MatchReference(new[]{P(11767)},1280,960,reference,scale,true,year,default));
        Assert.Throws<InvalidOperationException>(()=>PolarisPattern.MatchReference(new[]{P(11767),new PixelPoint(P(84535).X+80,P(84535).Y)},1280,960,reference,scale,true,year,default));
    }
    [Fact] public void IdentifyManualFixedPattern() {
        PixelPoint[] stars=[new(640,480),new(325.33,254.22),new(817.78,368),new(312.89,631.11),new(768,748.44)];
        var result=PolarisPattern.Match(stars,1280,960,stars[0],2017,default,minimumStars:5);
        Console.WriteLine($"Manual: {result.PixelScale} RMS {result.RmsPixels}; "+string.Join(",",result.Stars.Select(s=>$"HIP{s.Id} @ {s.Position}")));
    }
    [Theory][InlineData(false,12)][InlineData(true,147)][InlineData(false,275)]
    public void RotatedMirroredAndObstructedTemplatesRecoverActualCameraPose(bool flip,double angle) {
        var catalog=PolarisPattern.Catalog(2026.85);var pose=PolarAlignmentGeometry.FromEquatorial(40,88.5,angle,flip);
        const double scale=30.86;double focal=206264.80624709636/scale;
        PixelPoint? P(PatternStar s)=>PolarAlignmentGeometry.ProjectDirection(s.Direction,pose,focal,640,480);
        var detected=catalog.Select(s=>(Star:s,Point:P(s))).Where(s=>s.Point is {X:>10 and <1270,Y:>10 and <950})
            .Take(30).Select(s=>s.Point!.Value).ToList();
        var polaris=P(catalog.Single(s=>s.Id==11767))!.Value;
        Assert.True(detected.Count>=5);
        detected.AddRange(new[]{new PixelPoint(100,100),new PixelPoint(300,200),new PixelPoint(1000,850)});
        var result=PolarisPattern.Match(detected,1280,960,polaris,2026.85,default);
        Assert.Equal(flip,result.Flipped);Assert.InRange(result.PixelScale,scale-.01,scale+.01);
        Assert.Equal(5,result.Stars.Length);Assert.InRange(result.RmsPixels,0,.01);
        Assert.True(result.Orientation.Center.Dot(pose.Center)>1-1e-10);
    }
    [Fact] public void WrongClickCannotBeAcceptedAsPolaris() {
        var stars=Enumerable.Range(0,20).Select(i=>new PixelPoint(100+i*45,100+(i%4)*150)).ToArray();
        Assert.Throws<InvalidOperationException>(()=>PolarisPattern.Match(stars,1280,960,new PixelPoint(600,900),2026.85,default));
    }
    [Theory][InlineData(37391)][InlineData(115746)][InlineData(19454)][InlineData(84535)]
    public void PolarisAndOneFixedNeighborDetermineRotationWithKnownStockOptics(int id) {
        var catalog=PolarisPattern.Catalog(2026.85);var pose=PolarAlignmentGeometry.FromEquatorial(40,88.5,147,true);
        PixelPoint Project(int hip)=>PolarAlignmentGeometry.ProjectDirection(catalog.Single(s=>s.Id==hip).Direction,pose,25000d/3.75,640,480)!.Value;
        var seed=Project(11767);var result=PolarisPattern.Match(new[]{seed,Project(id)},1280,960,seed,2026.85,default);
        Assert.Equal(2,result.Stars.Length);Assert.True(result.Flipped);Assert.True(result.Orientation.Center.Dot(pose.Center)>1-1e-10);
        var reacquired=PolarisPattern.Match(new[]{seed,Project(id)},1280,960,null,2026.85,default);
        Assert.Equal(2,reacquired.Stars.Length);Assert.True(reacquired.Orientation.Center.Dot(pose.Center)>1-1e-10);
    }
    [Fact] public void PolarisAloneCannotDetermineTemplateRotation() {
        Assert.Throws<InvalidOperationException>(()=>PolarisPattern.Match(new[]{new PixelPoint(640,480)},1280,960,new(640,480),2026.85,default));
    }
    [Fact] public void TwoEquallyPlausibleSparseRotationsAreRejected() {
        var catalog=PolarisPattern.Catalog(2026.85);var pose=PolarAlignmentGeometry.FromEquatorial(40,89.264,147,true);
        PixelPoint Project(int hip)=>PolarAlignmentGeometry.ProjectDirection(catalog.Single(s=>s.Id==hip).Direction,pose,25000d/3.75,640,480)!.Value;
        var p=Project(11767);var q=Project(84535);
        var reflected=new PixelPoint(2*p.X-q.X,2*p.Y-q.Y);
        Assert.Throws<InvalidOperationException>(()=>PolarisPattern.Match(new[]{p,q,reflected},1280,960,p,2026.85,default));
    }
}
