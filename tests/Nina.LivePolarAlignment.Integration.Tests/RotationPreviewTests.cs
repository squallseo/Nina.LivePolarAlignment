using System.Runtime.CompilerServices;
using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using Nina.LivePolarAlignment.Capture;
using Xunit;

namespace Nina.LivePolarAlignment.Integration.Tests;

[Collection("Isolated NINA environment")]
public sealed class RotationPreviewTests {
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task PreviewOwnsCameraDuringBothIntervalsAndReturnAndFailureStopsMotion(int failure) {
        bool drained=true,tracking=true;int frames=0,reservations=0,releases=0;
        double ra=23,rate=0;var commands=new List<double>();
        var data=new BaseImageData(Enumerable.Repeat((ushort)1000,1280*960).ToArray(),1280,960,16,false,new ImageMetaData(),null!,
            InterfaceStub.Create<NINA.Image.ImageAnalysis.IStarDetection>((_,_)=>null),null!);
        async IAsyncEnumerable<IExposureData> Frames([EnumeratorCancellation] CancellationToken token) {
            drained=false;
            try {
                while(true) {
                    await Task.Delay(40,token);
                    if(failure==1 || failure==2 && frames>=30)throw new InvalidOperationException("Synthetic video loss");
                    frames++;
                    yield return InterfaceStub.Create<IExposureData>((m,_)=>m.Name=="ToImageData"?Task.FromResult<IImageData>(data):null);
                }
            } finally {drained=true;}
        }
        var native=(QHYCamera)RuntimeHelpers.GetUninitializedObject(typeof(QHYCamera));
        var info=new CameraInfo{Connected=true,DeviceId="POLEMASTER-test",XSize=1280,YSize=960,BinX=1,BinY=1,ExposureMin=.01,ExposureMax=10};
        var camera=InterfaceStub.Create<ICameraMediator>((m,args)=>m.Name switch {
            "GetInfo"=>info,"GetDevice"=>native,"IsFreeToCapture"=>true,
            "LiveView"=>Frames((CancellationToken)args![1]!),"RegisterCaptureBlock"=>Reserve(),"ReleaseCaptureBlock"=>Release(),_=>null
        });
        object? Reserve(){reservations++;Assert.Equal(releases+1,reservations);return null;}
        object? Release(){Assert.True(drained);releases++;return null;}
        var mount=InterfaceStub.Create<ITelescope>((m,args)=>m.Name switch {
            "get_Id"=>"ASCOM.ASIMount.Telescope","get_PrimaryMovingRate"=>8d,"get_SiderealTime"=>2d,
            "get_Connected" or "get_CanMovePrimaryAxis" or "get_CanSetTrackingEnabled"=>true,
            "get_AtPark" or "get_Slewing"=>false,"get_AlignmentMode"=>AlignmentMode.GermanPolar,
            "get_TrackingEnabled"=>tracking,"set_TrackingEnabled"=>SetTracking((bool)args![0]!),
            "get_RightAscension"=>ReadRa(),"get_Declination"=>80d,"get_SideOfPier"=>PierSide.pierEast,
            "GetAxisRates"=>new List<(double,double)>{(8,8)},_=>null
        });
        object? SetTracking(bool value){tracking=value;return null;}
        // Match the 250 ms rotation poll; advancing 500 ms per read simulated twice the commanded speed.
        double ReadRa(){ra=((ra+rate*.25/15)%24+24)%24;return ra;}
        var telescope=InterfaceStub.Create<ITelescopeMediator>((m,args)=>m.Name switch {
            "GetDevice"=>mount,"MoveAxis"=>Move((double)args![1]!),_=>throw new Exception("Unexpected mount call "+m.Name)
        });
        object? Move(double value){if(value!=0){Assert.False(drained);Assert.True(frames>0);}rate=value;commands.Add(value);return null;}
        var profile=InterfaceStub.Create<IProfile>((m,_)=>m.Name=="get_Id"?Guid.Empty:throw new Exception("Rotation preview read "+m.Name));
        var profiles=InterfaceStub.Create<IProfileService>((m,_)=>m.Name=="get_ActiveProfile"?profile:null);
        var service=new NinaPolarAlignmentCaptureService(profiles,camera,null!,telescope){PoleMasterMode=true};
        int published=0;service.StreamingPreviewAvailable+=frame=>{Assert.True(frame.IsRotationPreview);Assert.False(frame.IsAimingPreview);Assert.NotNull(frame.Stars);published++;};
        if(failure!=0) {
            await Assert.ThrowsAsync<InvalidOperationException>(()=>service.RotateRaWithPreviewAsync(true,false,.25,null,default));
            Assert.Equal(failure==2,commands.Any(c=>c!=0));
        } else {
            await service.RotateRaWithPreviewAsync(true,false,.25,null,default);
            await service.RotateRaWithPreviewAsync(true,true,.25,null,default);
            await service.ReturnRaWithPreviewAsync(.25,null,default);
            Assert.True(published>10);
            Assert.Equal(new[]{-8d,0d,-8d,0d,8d,0d},commands);
            Assert.InRange(Math.Abs(Core.RaRotation.WrapDegrees((ra-23)*15)),0,.01);
        }
        Assert.Equal(reservations,releases);Assert.Equal(0,rate);Assert.True(tracking);Assert.True(drained);
        service.PoleMasterMode=false; // The shared gate was released after every exit path.
    }
}
