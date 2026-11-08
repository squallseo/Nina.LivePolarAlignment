using System.Runtime.CompilerServices;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using Nina.LivePolarAlignment.Capture;
using Xunit;

namespace Nina.LivePolarAlignment.Integration.Tests;

[Collection("Isolated NINA environment")]
public sealed class PoleMasterPreviewTests {
    [Fact]
    public async Task BlankDaytimePreviewNeedsNoSolverOpticsSiteOrMountAndDrainsBeforeRelease() {
        using var stop=new CancellationTokenSource();
        bool drained=false,released=false;
        var native=(QHYCamera)RuntimeHelpers.GetUninitializedObject(typeof(QHYCamera));
        var info=new CameraInfo{Connected=true,DeviceId="POLEMASTER-test",XSize=1280,YSize=960,BinX=1,BinY=1,ExposureMin=.01,ExposureMax=10};
        var pixels=Enumerable.Repeat((ushort)1000,1280*960).ToArray();
        var image=new BaseImageData(pixels,1280,960,16,false,new ImageMetaData(),null!,
            InterfaceStub.Create<NINA.Image.ImageAnalysis.IStarDetection>((_,_)=>null),null!);
        async IAsyncEnumerable<IExposureData> Frames([EnumeratorCancellation] CancellationToken token) {
            try {while(true){await Task.Delay(20,token);yield return InterfaceStub.Create<IExposureData>((m,_)=>m.Name=="ToImageData"?Task.FromResult<IImageData>(image):null);}}
            finally{drained=true;}
        }
        var camera=InterfaceStub.Create<ICameraMediator>((m,args)=>m.Name switch {
            "GetInfo"=>info,"GetDevice"=>native,"IsFreeToCapture"=>true,
            "LiveView"=>Frames((CancellationToken)args![1]!),"ReleaseCaptureBlock"=>Release(),_=>null
        });
        object? Release(){Assert.True(drained);released=true;return null;}
        var profile=InterfaceStub.Create<IProfile>((m,_)=>m.Name=="get_Id"?Guid.Empty:throw new Exception("Preview must not read "+m.Name));
        var profiles=InterfaceStub.Create<IProfileService>((m,_)=>m.Name=="get_ActiveProfile"?profile:null);
        var mount=InterfaceStub.Create<ITelescopeMediator>((m,_)=>throw new Exception("Preview touched mount: "+m.Name));
        var service=new NinaPolarAlignmentCaptureService(profiles,camera,null!,mount){PoleMasterMode=true};
        int count=0;service.StreamingPreviewAvailable+=frame=>{Assert.True(frame.IsAimingPreview);Assert.Equal(1280,frame.Image.PixelWidth);if(++count==3)stop.Cancel();};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.RunPreviewAsync(.25,stop.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3,count);Assert.True(released);
        service.PreviewStretchStrength=2;
        Assert.NotNull(await service.RefreshPreviewAsync(default));
        Assert.All(pixels,p=>Assert.Equal(1000,p));
    }
}
