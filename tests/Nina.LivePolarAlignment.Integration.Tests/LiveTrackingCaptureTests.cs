using System.Reflection;
using System.Runtime.CompilerServices;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using Nina.LivePolarAlignment.Capture;
using Xunit;

namespace Nina.LivePolarAlignment.Integration.Tests;

[Collection("Isolated NINA environment")]
public sealed class LiveTrackingCaptureTests {
    private static IImageData Image(int bin, double shift, bool blank = false) {
        int width=640/bin,height=480/bin; var pixels=Enumerable.Repeat((ushort)1000,width*height).ToArray();
        for(int row=0;row<5 && !blank;row++) for(int col=0;col<6;col++) {
            double x=(50+col*103+row*2.3+shift)/bin, y=(45+row*89+col*1.7)/bin;
            for(int dy=-5;dy<=5;dy++) for(int dx=-5;dx<=5;dx++) {
                int px=(int)x+dx,py=(int)y+dy;
                if(px<0||py<0||px>=width||py>=height)continue;
                pixels[py*width+px]+=(ushort)(10000*Math.Exp(-((px+.5-x)*(px+.5-x)+(py+.5-y)*(py+.5-y))/2));
            }
        }
        return new BaseImageData(pixels,width,height,16,false,new ImageMetaData(),null!,
            InterfaceStub.Create<NINA.Image.ImageAnalysis.IStarDetection>((_,_)=>null),null!);
    }
    [Fact]
    public void LargeBinnedSensorTrackingProcessingBenchmark() {
        const int width=4788,height=3194;
        var pixels=Enumerable.Repeat((ushort)1000,width*height).ToArray();
        for(int row=0;row<5;row++) for(int col=0;col<6;col++) {
            double x=350+col*780+row*2.3,y=300+row*620+col*1.7;
            for(int dy=-6;dy<=6;dy++) for(int dx=-6;dx<=6;dx++) {
                int px=(int)x+dx,py=(int)y+dy;
                pixels[py*width+px]+=(ushort)(15000*Math.Exp(-((px+.5-x)*(px+.5-x)+(py+.5-y)*(py+.5-y))/4));
            }
        }
        var image=new BaseImageData(pixels,width,height,16,false,new ImageMetaData(),null!,
            InterfaceStub.Create<NINA.Image.ImageAnalysis.IStarDetection>((_,_)=>null),null!);
        var initial=QuickLookPreview.Prepare(image,CancellationToken.None);
        var stars=initial.FindStars(CancellationToken.None);
        var tracker=new Core.AstrometricTracker(Core.PolarAlignmentGeometry.FromEquatorial(150,88,0),stars,width,height,3,false);
        var clock=System.Diagnostics.Stopwatch.StartNew();
        for(int i=0;i<5;i++) {
            var prepared=QuickLookPreview.Prepare(image,CancellationToken.None);
            var fit=tracker.Track(prepared.FindStars(CancellationToken.None),width,height);
            Assert.InRange(fit.Stars,8,35);
        }
        clock.Stop();
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(),"polar-streaming-v43-benchmark.txt"),
            $"4788x3194 RAW preview + centroid detection + spherical fit: {clock.Elapsed.TotalMilliseconds/5:F1} ms/frame (5 synthetic frames). Excludes camera transfer and UI rendering.");
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LiveFramesTrackWithoutCallingAnExternalSolverAndStopBeforeCameraRelease(bool loseStars) {
        using var cancellation=new CancellationTokenSource();
        bool stopped=false,released=false,blank=false; CaptureSequence? request=null;
        async IAsyncEnumerable<IExposureData> Frames([EnumeratorCancellation] CancellationToken token) {
            try {
                while(true) {
                    await Task.Delay(20,token);
                    var data=Image(2,3,blank);
                    yield return InterfaceStub.Create<IExposureData>((m,_)=>m.Name=="ToImageData"?Task.FromResult(data):null);
                }
            } finally { stopped=true; }
        }
        var info=new CameraInfo {Connected=true,DeviceId="ZWOptical_test",BinX=1,BinY=1,ExposureMin=.01,ExposureMax=10};
        var native=InterfaceStub.Create<ICamera>((m,_)=>m.Name switch {
            "get_Connected"=>true,
            "get_BinningModes"=>new NINA.Core.Utility.AsyncObservableCollection<BinningMode>(new BinningMode[]{new(1,1),new(2,2)}),
            "get_CanSetGain" or "get_CanSetOffset"=>false,_=>null
        });
        var camera=InterfaceStub.Create<ICameraMediator>((m,args)=>m.Name switch {
            "GetInfo"=>info,"GetDevice"=>native,"IsFreeToCapture"=>true,
            "LiveView"=>Receive((CaptureSequence)args![0]!, (CancellationToken)args[1]!),
            "ReleaseCaptureBlock"=>Release(),_=>null
        });
        object? Release(){Assert.True(stopped);released=true;return null;}
        IAsyncEnumerable<IExposureData> Receive(CaptureSequence sequence,CancellationToken token){request=sequence;return Frames(token);}
        var optics=InterfaceStub.Create<ITelescopeSettings>((m,_)=>m.Name=="get_FocalLength"?400d:null);
        var sensor=InterfaceStub.Create<ICameraSettings>((m,_)=>m.Name=="get_PixelSize"?3.76d:null);
        var profile=InterfaceStub.Create<IProfile>((m,_)=>m.Name switch {
            "get_Id"=>Guid.Empty,"get_PlateSolveSettings"=>new NINA.Profile.PlateSolveSettings(),
            "get_TelescopeSettings"=>optics,"get_CameraSettings"=>sensor,_=>null
        });
        var profiles=InterfaceStub.Create<IProfileService>((m,_)=>m.Name=="get_ActiveProfile"?profile:null);
        var telescope=InterfaceStub.Create<ITelescopeMediator>((_,_)=>null);
        var service=new NinaPolarAlignmentCaptureService(profiles,camera,null!,telescope);
        var anchor=new SolvedPolarFrame(150,88,0,3,640,480,false,DateTimeOffset.UtcNow,null,Image(1,0));
        typeof(NinaPolarAlignmentCaptureService).GetField("streamingAnchor",BindingFlags.Instance|BindingFlags.NonPublic)!
            .SetValue(service,((SolveHintContext Context,SolvedPolarFrame Frame)?)(new(Guid.Empty,info.DeviceId,400,3.76),anchor));
        await using(var reader=service.StreamAndSolveAsync(.25,cancellation.Token).GetAsyncEnumerator()) {
            for(int i=0;i<5;i++) {
                Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4)));
                var frame=reader.Current;
                Assert.True(frame.IsTracked);Assert.InRange(frame.TrackedStars,8,35);
                Assert.Equal(320,frame.Width);Assert.Equal(6,frame.PixelScaleArcseconds);
                Assert.InRange(frame.TrackingRmsPixels,0,.8);
            }
            if(loseStars) {
                int previews=0;
                var visible=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                service.StreamingPreviewAvailable+=_=>{if(Interlocked.Increment(ref previews)>=2)visible.TrySetResult();};
                blank=true;
                async Task Consume() { while(await reader.MoveNextAsync()) { } }
                var pending=Consume();
                await visible.Task.WaitAsync(TimeSpan.FromSeconds(4));
                Assert.False(pending.IsCompleted); // Preview continues while no guidance is yielded.
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
            }
            cancellation.Cancel();
        }
        Assert.True(released);Assert.NotNull(request);Assert.Equal(2,request.Binning.X);
    }
}
