using Nina.LivePolarAlignment.Capture;
using Xunit;

namespace Nina.LivePolarAlignment.Integration.Tests;

public sealed class PoleMasterSetupTests {
    [Theory]
    [InlineData(37.5,89.9)] [InlineData(-33.9,-89.9)]
    public void UsesVisiblePoleInsteadOfMainTelescopePointing(double latitude,double expected) {
        Assert.Equal(expected,PoleMasterSetup.PoleHint(latitude).Dec);
        Assert.Equal(25,PoleMasterSetup.FocalLengthMillimeters);
        Assert.Equal(3.75,PoleMasterSetup.PixelSizeMicrons);
    }
    [Theory]
    [InlineData(false,1280,960)] [InlineData(true,9576,6388)] [InlineData(true,640,480)]
    public void WrongCameraCannotStartStockMode(bool native,int width,int height) =>
        Assert.Throws<InvalidOperationException>(()=>PoleMasterSetup.ValidateCamera(native,width,height));

    [Theory]
    [InlineData(30.94,89.5,true)] [InlineData(30.94,-89.5,false)]
    [InlineData(4,89.5,false)] [InlineData(30.94,75,false)] [InlineData(double.NaN,89.5,false)]
    public void RejectsWrongOpticsOrPoleBeforeRaMotion(double scale,double dec,bool accepted) {
        var frame=new SolvedPolarFrame(0,dec,0,scale,1280,960,false,DateTimeOffset.UtcNow,null);
        if(accepted) PoleMasterSetup.ValidateSolution(frame,37.5);
        else Assert.Throws<InvalidOperationException>(()=>PoleMasterSetup.ValidateSolution(frame,37.5));
    }
}
