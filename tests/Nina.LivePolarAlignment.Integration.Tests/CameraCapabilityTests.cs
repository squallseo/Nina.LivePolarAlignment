using System.Runtime.CompilerServices;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using Nina.LivePolarAlignment.Capture;
using Xunit;

namespace Nina.LivePolarAlignment.Integration.Tests;
[Collection("Isolated NINA environment")]
public sealed class CameraCapabilityTests {
    [Theory][InlineData(false)][InlineData(true)]
    public void CameraGeometryAndNativeDriverDetermineCapabilities(bool poleMaster) {
        var telescope=InterfaceStub.Create<ITelescopeSettings>((m,_)=>m.Name=="get_FocalLength"?1000d:null);
        var settings=InterfaceStub.Create<ICameraSettings>((m,_)=>m.Name=="get_PixelSize"?6d:null);
        var profile=InterfaceStub.Create<IProfile>((m,_)=>m.Name switch{"get_TelescopeSettings"=>telescope,"get_CameraSettings"=>settings,_=>null});
        var profiles=InterfaceStub.Create<IProfileService>((m,_)=>m.Name=="get_ActiveProfile"?profile:null);
        var info=new CameraInfo{Connected=true,DeviceId=poleMaster?"POLEMASTER-test":"QHY600-test",XSize=poleMaster?1280:6000,YSize=poleMaster?960:4000};
        var native=(QHYCamera)RuntimeHelpers.GetUninitializedObject(typeof(QHYCamera));
        var mediator=InterfaceStub.Create<ICameraMediator>((m,_)=>m.Name switch{"GetInfo"=>info,"GetDevice"=>native,_=>null});
        var camera=new NinaPolarAlignmentCaptureService(profiles,mediator,null!,null!);
        var c=camera.AlignmentCamera!;
        Assert.Equal(poleMaster,c.IsPoleMaster);Assert.Equal(poleMaster,c.PatternSupported);
        Assert.True(c.SupportsVideo);Assert.True(c.HasOptics);
        if(poleMaster){Assert.InRange(c.FieldWidthDegrees!.Value,10.9,11);Assert.InRange(c.FieldHeightDegrees!.Value,8.2,8.3);}
        else {Assert.InRange(c.FieldWidthDegrees!.Value,2.06,2.07);Assert.InRange(c.FieldHeightDegrees!.Value,1.37,1.38);}
        info.Connected=false;Assert.Null(camera.AlignmentCamera);
    }
}
