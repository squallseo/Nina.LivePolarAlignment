using System.Runtime.CompilerServices;
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
public sealed class LiveControlTests {
    [Theory][InlineData("POLEMASTER-test",null)][InlineData("POLEMASTER-test",9)][InlineData("QHY600M-test",12)]
    public void EquipmentGainWinsOverRetiredPluginDefaultAndSavedOverrides(string id,int? saved) {
        var stored=new NINA.Profile.PluginSettings();
        var key=new Guid("a1968bf0-8fc3-4605-bf07-b29799485257");
        if(saved is {} value)stored.SetValue(key,"CameraGain:"+id,value);
        var settings=InterfaceStub.Create<ICameraSettings>((m,_)=>m.Name switch{"get_Id"=>id,"get_Gain"=>(int?)42,_=>null});
        var profile=InterfaceStub.Create<IProfile>((m,_)=>m.Name switch{"get_CameraSettings"=>settings,"get_PluginSettings"=>stored,_=>null});
        var profiles=InterfaceStub.Create<IProfileService>((m,_)=>m.Name=="get_ActiveProfile"?profile:null);
        var info=new CameraInfo{DeviceId=id,Gain=42};
        var camera=InterfaceStub.Create<ICameraMediator>((m,_)=>m.Name=="GetInfo"?info:null);
        var service=new NinaPolarAlignmentCaptureService(profiles,camera,null!,null!){PoleMasterMode=true};
        Assert.Equal(42,service.CameraGain);
        info.Gain=73;Assert.Equal(73,service.CameraGain);
        if(saved is {} previous){Assert.True(stored.TryGetValue(key,"CameraGain:"+id,out int retained));Assert.Equal(previous,retained);}
    }
    [Fact] public async Task ExposureUpdatesKeepVideoRunningWithoutWritingGain() {
        int starts=0,releases=0;double appliedExposure=.25;
        var settings=InterfaceStub.Create<ICameraSettings>((m,_)=>m.Name switch{"get_Id"=>"QHY-test","get_Gain"=>(int?)42,_=>null});
        var profile=InterfaceStub.Create<IProfile>((m,_)=>m.Name switch{"get_Id"=>Guid.Empty,"get_CameraSettings"=>settings,_=>null});
        var profiles=InterfaceStub.Create<IProfileService>((m,_)=>m.Name=="get_ActiveProfile"?profile:null);
        var info=new CameraInfo{Connected=true,CanSetGain=true,Gain=42,DeviceId="QHY-test",XSize=160,YSize=120,BinX=1,BinY=1,ExposureMin=.01,ExposureMax=10};
        var qhy=(QHYCamera)RuntimeHelpers.GetUninitializedObject(typeof(QHYCamera));qhy.Connected=true;
        qhy.Sdk=InterfaceStub.Create<QHYCCD.IQhySdk>((m,a)=>m.Name switch{
            "SetControlValue"=>SetExposure((QHYCCD.QhySdk.CONTROL_ID)a![0]!, (double)a[1]!),
            "GetControlValue"=>appliedExposure*1e6,"IsControl"=>false,_=>null});
        bool SetExposure(QHYCCD.QhySdk.CONTROL_ID control,double microseconds){Assert.Equal(QHYCCD.QhySdk.CONTROL_ID.CONTROL_EXPOSURE,control);appliedExposure=microseconds/1e6;return true;}
        var image=new BaseImageData(Enumerable.Repeat((ushort)1000,160*120).ToArray(),160,120,16,false,new ImageMetaData(),null!,InterfaceStub.Create<NINA.Image.ImageAnalysis.IStarDetection>((_,_)=>null),null!);
        async IAsyncEnumerable<IExposureData> Frames([EnumeratorCancellation] CancellationToken token) {
            starts++;while(true){await Task.Delay(25,token);yield return InterfaceStub.Create<IExposureData>((m,_)=>m.Name=="ToImageData"?Task.FromResult<IImageData>(image):null);}
        }
        var camera=InterfaceStub.Create<ICameraMediator>((m,a)=>m.Name switch {
            "GetInfo"=>info,"GetDevice"=>qhy,"IsFreeToCapture"=>true,"LiveView"=>Frames((CancellationToken)a![1]!),"ReleaseCaptureBlock"=>Release(),_=>null});
        object? Release(){releases++;return null;}
        var service=new NinaPolarAlignmentCaptureService(profiles,camera,null!,null!);
        using var stop=new CancellationTokenSource();int seen=0;service.StreamingPreviewAvailable+=_=>Interlocked.Increment(ref seen);
        var run=service.RunPreviewAsync(.25,stop.Token);
        for(int i=0;i<100 && seen<2;i++)await Task.Delay(10);
        if(run.IsCompleted)await run;
        Assert.True(seen>=2);Assert.True(service.TryQueueLiveExposure(.05));
        for(int i=0;i<100 && appliedExposure!=.05;i++)await Task.Delay(10);
        Assert.Equal(.05,appliedExposure);Assert.True(service.TryQueueLiveExposure(.1));
        for(int i=0;i<100 && appliedExposure!=.1;i++)await Task.Delay(10);
        Assert.Equal(.1,appliedExposure);Assert.Equal(1,starts);Assert.Equal(0,releases);Assert.Equal(42,info.Gain);
        stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>run);Assert.Equal(1,releases);
    }
}
