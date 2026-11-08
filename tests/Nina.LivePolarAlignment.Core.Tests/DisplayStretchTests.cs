using Nina.LivePolarAlignment.Core;
using Xunit;

namespace Nina.LivePolarAlignment.Core.Tests;

public sealed class DisplayStretchTests {
    [Theory][InlineData(8)][InlineData(12)][InlineData(16)]
    public void LinearDisplayUsesSensorRangeWithoutBackgroundRescaling(int depth) {
        var lookup=DisplayStretch.CreateLinearLookup(depth);int maximum=(1<<depth)-1;
        Assert.Equal(0,lookup[0]);Assert.Equal(255,lookup[maximum]);Assert.InRange(lookup[maximum/2],127,128);
    }
    [Fact]
    public void BrighterStrengthRaisesDisplayMidtonesWithoutChangingRawStars() {
        var random=new Random(33);
        var raw=Enumerable.Range(0,20000).Select(_=>(ushort)(1000+random.Next(0,40))).ToArray();
        raw[100]=18000;var original=raw.ToArray();
        var before=StarCentroids.Detect(raw,200,100,raw,200,100,default);
        var dark=DisplayStretch.CreateLookup(raw,.25);var normal=DisplayStretch.CreateLookup(raw,1);var bright=DisplayStretch.CreateLookup(raw,2.5);
        Assert.True(dark[1020]<normal[1020]);Assert.True(normal[1020]<bright[1020]);
        Assert.Equal(original,raw);Assert.Equal(before,StarCentroids.Detect(raw,200,100,raw,200,100,default));
        for(int i=1;i<65536;i++)Assert.True(bright[i]>=bright[i-1]);
        Assert.Equal(255,bright[65535]);
    }
}
