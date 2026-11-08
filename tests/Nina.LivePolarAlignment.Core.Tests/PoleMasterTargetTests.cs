using Nina.LivePolarAlignment.Core;
using Xunit;
namespace Nina.LivePolarAlignment.Core.Tests;
public sealed class PoleMasterTargetTests {
    [Fact]
    public void TargetDistanceUsesCalibratedCameraRays() {
        var center=new PixelPoint(640,480);
        double focal=206264.80624709636/30.5;
        var oneArcminute=new PixelPoint(640+focal*Math.Tan(Math.PI/10800),480);
        Assert.Equal(0,PoleMasterTarget.AngularDistance(center,center,1280,960,30.5));
        Assert.Equal(60,PoleMasterTarget.AngularDistance(oneArcminute,center,1280,960,30.5),7);
        Assert.Equal(60,PoleMasterTarget.AngularDistance(center,oneArcminute,1280,960,30.5),7);
        Assert.Throws<ArgumentOutOfRangeException>(()=>PoleMasterTarget.AngularDistance(center,center,1280,960,double.NaN));
    }
    [Theory][InlineData(37.5,2,-3,false)][InlineData(37.5,-2,3,true)][InlineData(60,1,2,false)]
    public void CorrectedCameraCarriesMechanicalAxisToNorthPole(double latitude,double altitudeError,double azimuthError,bool flipped) {
        double alt=(latitude+altitudeError)*Math.PI/180,az=azimuthError*Math.PI/180;
        var axis=new Vector3d(Math.Cos(alt)*Math.Sin(az),Math.Cos(alt)*Math.Cos(az),Math.Sin(alt));
        var camera=PolarAlignmentGeometry.FromEquatorial(40,88,24,flipped);
        var corrected=PoleMasterTarget.AlignedCamera(camera,axis,latitude);
        var actual=PolarAlignmentGeometry.TransformDirection(axis,camera,corrected);
        var pole=new Vector3d(0,Math.Cos(latitude*Math.PI/180),Math.Sin(latitude*Math.PI/180));
        Assert.True(actual.Dot(pole)>1-1e-12);
        Assert.InRange(Math.Abs(corrected.Center.Dot(corrected.Right)),0,1e-12);
    }
}
