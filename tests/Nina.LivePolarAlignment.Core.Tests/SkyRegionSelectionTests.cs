using Nina.LivePolarAlignment.Core;
using Xunit;
namespace Nina.LivePolarAlignment.Core.Tests;
public sealed class SkyRegionSelectionTests {
    [Fact] public void LocalKnownStarDetectionSurvivesBrighterObstructionPeaks() {
        const int w=400,h=300;var raw=Enumerable.Repeat((ushort)100,w*h).ToArray();
        for(int cy=15;cy<290;cy+=14)for(int cx=15;cx<200;cx+=14)
            for(int y=-3;y<=3;y++)for(int x=-3;x<=3;x++)raw[(cy+y)*w+cx+x]=(ushort)(100+5000*Math.Exp(-(x*x+y*y)/4d));
        for(int y=-6;y<=6;y++)for(int x=-6;x<=6;x++)raw[(150+y)*w+300+x]=(ushort)(100+200*Math.Exp(-(x*x+y*y)/5d));
        var global=StarCentroids.Detect(raw,w,h,raw,w,h,default);
        Assert.DoesNotContain(global,p=>Math.Abs(p.X-300.5)<2 && Math.Abs(p.Y-150.5)<2);
        var local=SkyRegionSelection.ConfirmStarNear(raw,w,h,300,150);
        Assert.NotNull(local);Assert.InRange(local.Value.X,300.4,300.6);Assert.InRange(local.Value.Y,150.4,150.6);
    }
    [Fact] public void CatalogueCandidateNeedsCompactRawSignal() {
        const int w=100;var raw=Enumerable.Repeat((ushort)69,w*w).ToArray();
        raw[50*w+50]=255;Assert.Null(SkyRegionSelection.ConfirmStarNear(raw,w,w,50,50));
        for(int y=-6;y<=6;y++)for(int x=-6;x<=6;x++)raw[(50+y)*w+50+x]=(ushort)Math.Min(255,69+300*Math.Exp(-(x*x+y*y)/8d));
        Assert.NotNull(SkyRegionSelection.ConfirmStarNear(raw,w,w,50,50));
        Array.Fill(raw,(ushort)255);Assert.Null(SkyRegionSelection.ConfirmStarNear(raw,w,w,50,50));
    }
    [Fact] public void MaskPreservesSensorCoordinatesAndSource() {
        var raw=Enumerable.Repeat((ushort)30,100*80).ToArray();raw[45*100+55]=200;
        var before=(ushort[])raw.Clone();var result=SkyRegionSelection.Mask(raw,100,80,new(40,30,30,30,"test"),false,CancellationToken.None);
        Assert.Equal(raw.Length,result.Length);Assert.Equal(200,result[45*100+55]);Assert.Equal(before,raw);
    }
    [Fact] public void EightBitStarsAndSaturatedPlateauRemainDetectable() {
        const int w=180,h=120;var raw=Enumerable.Repeat((ushort)32,w*h).ToArray();
        foreach(var (cx,cy) in new[]{(35,35),(90,35),(145,35),(35,85),(90,85),(145,85)})
            for(int y=-4;y<=4;y++)for(int x=-4;x<=4;x++)raw[(cy+y)*w+cx+x]=(ushort)Math.Min(255,32+300*Math.Exp(-(x*x+y*y)/4d));
        var stars=StarCentroids.Detect(raw,w,h,raw,w,h,CancellationToken.None);
        Assert.Equal(6,stars.Length);Assert.All(stars,p=>Assert.True(double.IsFinite(p.X)&&double.IsFinite(p.Y)));
    }
    [Fact] public void CancelledMaskStopsBeforeFurtherProcessing() {
        Assert.Throws<OperationCanceledException>(()=>SkyRegionSelection.Mask(new ushort[10000],100,100,new(0,0,100,100,"sky"),true,new CancellationToken(true)));
    }
}
