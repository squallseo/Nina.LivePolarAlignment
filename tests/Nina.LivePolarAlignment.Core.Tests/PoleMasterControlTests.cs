using Nina.LivePolarAlignment.Core;
using Xunit;
namespace Nina.LivePolarAlignment.Core.Tests;
public sealed class PoleMasterControlTests {
    [Theory]
    [InlineData(.05,1,.1)][InlineData(.1,1,.2)][InlineData(.2,1,.3)][InlineData(.3,1,.3)]
    [InlineData(.3,-1,.2)][InlineData(.2,-1,.1)][InlineData(.1,-1,.05)][InlineData(.05,-1,.05)]
    [InlineData(.5,-1,.3)][InlineData(.5,1,.5)]
    public void ExposureUsesManualPresetsWithoutReversingButtonDirection(double current,int direction,double expected)
        =>Assert.Equal(expected,PoleMasterControls.NextExposure(current,direction));
    [Theory]
    [InlineData(5,0,100,1,6)][InlineData(50,0,100,-1,49)]
    [InlineData(1,0,100,-1,0)][InlineData(0,0,100,-1,0)]
    [InlineData(100,0,100,1,100)][InlineData(5,0,20,1,6)]
    public void NativeGainUsesSingleUnitsIncludingBelow50(int current,int min,int max,int direction,int expected)
        =>Assert.Equal(expected,PoleMasterControls.NextGain(current,min,max,direction));
}
