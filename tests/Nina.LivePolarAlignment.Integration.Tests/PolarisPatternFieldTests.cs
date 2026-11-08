using System.Buffers.Binary;
using System.IO;
using Nina.LivePolarAlignment.Core;
using Xunit;
namespace Nina.LivePolarAlignment.Integration.Tests;
[Collection("Isolated NINA environment")]
public sealed class PolarisPatternFieldTests {
    [Fact] public async Task RecordedThreeStarFirstFrameNoLongerUsesFactoryScaleAgainstMeasuredLaterFrames() {
        var path=Environment.GetEnvironmentVariable("POLAR_REPLAY_FIRST");if(string.IsNullOrEmpty(path))return;
        var bytes=await File.ReadAllBytesAsync(path);int end=0;
        for(int i=0;i<bytes.Length;i+=80)if(System.Text.Encoding.ASCII.GetString(bytes,i,8)=="END     "){end=(i+80+2879)/2880*2880;break;}
        var raw=new ushort[1280*960];for(int i=0;i<raw.Length;i++)raw[i]=(ushort)(BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(end+i*2,2))+32768);
        var sky=SkyRegionSelection.Mask(raw,1280,960,new(0,0,1280,960,"sky"),true,default);
        var stars=StarCentroids.Detect(sky,1280,960,sky,1280,960,default);
        var match=PolarisPattern.Match(stars,1280,960,new(781,361),2026.85,default);
        var c=match.Orientation.Center;var up=match.Orientation.Up;double ra=(Math.Atan2(c.Y,c.X)*180/Math.PI+360)%360,dec=Math.Asin(c.Z)*180/Math.PI;
        var basis=PolarAlignmentGeometry.FromEquatorial(ra,dec,0);double pa=Math.Atan2(up.Dot(basis.Right),up.Dot(basis.Up))*180/Math.PI;
        var first=new Nina.LivePolarAlignment.Capture.SolvedPolarFrame(ra,dec,pa,match.PixelScale,1280,960,true,DateTimeOffset.Parse("2026-11-07T14:41:08.5067660Z"),null,Stars:new(match.Stars,1280,960,true,default,match.Stars));
        var middle=first with{RightAscensionDegrees=77.396015834960679,DeclinationDegrees=87.646001113751026,PositionAngleDegrees=86.790884898244002,PixelScaleArcseconds=30.870981142757309,CapturedAtUtc=DateTimeOffset.Parse("2026-11-07T14:41:28.4913588Z")};
        var last=first with{RightAscensionDegrees=78.57709174849191,DeclinationDegrees=87.34007718257169,PositionAngleDegrees=129.42353482743317,PixelScaleArcseconds=30.863135678473657,CapturedAtUtc=DateTimeOffset.Parse("2026-11-07T14:41:47.9169118Z")};
        Nina.LivePolarAlignment.Capture.SolvedPolarFrame ReconstructMatchedPositions(Nina.LivePolarAlignment.Capture.SolvedPolarFrame f) {
            var pose=PolarAlignmentGeometry.FromEquatorial(f.RightAscensionDegrees,f.DeclinationDegrees,f.PositionAngleDegrees,f.Flipped);
            var markers=PolarisPattern.Catalog(2026.85).Where(s=>s.Id!=37391).Select(s=>new TrackedStar(s.Id,PolarAlignmentGeometry.ProjectDirection(s.Direction,pose,206264.80624709636/f.PixelScaleArcseconds,640,480)!.Value)).ToArray();
            return f with{Stars=new(markers,1280,960,true,default,markers)};
        }
        middle=ReconstructMatchedPositions(middle);last=ReconstructMatchedPositions(last);
        double Coordinate(string key)=>double.Parse(Environment.GetEnvironmentVariable(key)
            ??throw new InvalidOperationException("Set "+key+" for the local recorded-frame replay."),System.Globalization.CultureInfo.InvariantCulture);
        var result=Nina.LivePolarAlignment.Capture.PoleMasterCalibration.Calculate(first,middle,last,
            Coordinate("POLAR_REPLAY_LATITUDE"),Coordinate("POLAR_REPLAY_LONGITUDE"));
        Console.WriteLine($"Recorded replay: {match.Stars.Length} stars; scale={match.PixelScale}; axis spread={result.AxisSpreadArcseconds}");
        Assert.True(match.Stars.Length>=3);Assert.InRange(match.PixelScale,30.8,30.9);Assert.InRange(result.ReprojectionRmsPixels!.Value,0,1.5);
    }
    [Fact] public async Task ObstructedFieldMatchesClickedPolarisWithoutExternalSolver() {
        var path=Environment.GetEnvironmentVariable("POLAR_FIELD_FITS");if(string.IsNullOrEmpty(path))return;
        var bytes=await File.ReadAllBytesAsync(path);int end=0;
        for(int i=0;i<bytes.Length;i+=80)if(System.Text.Encoding.ASCII.GetString(bytes,i,8)=="END     "){end=(i+80+2879)/2880*2880;break;}
        var raw=new ushort[1280*960];for(int i=0;i<raw.Length;i++)raw[i]=(ushort)(BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(end+i*2,2))+32768);
        var sky=SkyRegionSelection.Mask(raw,1280,960,new(0,0,1280,960,"sky"),true,default);
        var stars=StarCentroids.Detect(sky,1280,960,sky,1280,960,default);
        Console.WriteLine("Detected "+stars.Length+" stars");
        var partial=PolarisPattern.Match(stars,1280,960,new(744,341),2026.85,default,minimumStars:4);
        Console.WriteLine($"Visible fixed template stars: {string.Join(",",partial.Stars.Select(s=>s.Id))}; scale={partial.PixelScale}; rms={partial.RmsPixels}; flipped={partial.Flipped}");
        foreach(var star in PolarisPattern.Catalog(2026.85))Console.WriteLine($"Expected HIP {star.Id}: "+PolarAlignmentGeometry.ProjectDirection(star.Direction,partial.Orientation,206264.80624709636/partial.PixelScale,640,480));
        var match=PolarisPattern.Match(stars,1280,960,new(744,341),2026.85,default);
        Console.WriteLine($"Pattern matched {match.Stars.Length}; scale={match.PixelScale}; RMS={match.RmsPixels}");
        Assert.InRange(match.PixelScale,30.5,31.5);Assert.InRange(Math.Asin(match.Orientation.Center.Z)*180/Math.PI,87.7,87.9);
        var measuredPolaris=match.Stars.Single(s=>s.Id==11767).Position;
        foreach(var neighbor in match.Stars.Where(s=>s.Id!=11767)) {
            var sparse=PolarisPattern.Match(new[]{measuredPolaris,neighbor.Position},1280,960,measuredPolaris,2026.85,default);
            Assert.Equal(2,sparse.Stars.Length);Assert.True(sparse.Flipped);
            Assert.InRange(Math.Asin(sparse.Orientation.Center.Z)*180/Math.PI,87.7,87.9);
        }
        var reacquired=PolarisPattern.Match(stars,1280,960,null,2026.85,default);
        Assert.InRange(reacquired.PixelScale,30.5,31.5);Assert.Equal(4,reacquired.Stars.Length);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task PoleMasterPatternRequiresNoConfiguredOrBundledSolver(bool clickPolaris) {
        var path=Environment.GetEnvironmentVariable("POLAR_FIELD_FITS");if(string.IsNullOrEmpty(path))return;
        var bytes=await File.ReadAllBytesAsync(path);int end=0;
        for(int i=0;i<bytes.Length;i+=80)if(System.Text.Encoding.ASCII.GetString(bytes,i,8)=="END     "){end=(i+80+2879)/2880*2880;break;}
        var raw=new ushort[1280*960];for(int i=0;i<raw.Length;i++)raw[i]=(ushort)(BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(end+i*2,2))+32768);
        var original=(ushort[])raw.Clone();
        // Exercise the actual integration boundary with no ImageSolver instance and
        // no plate-solver settings at all. Any fallback would throw here.
        var site=InterfaceStub.Create<NINA.Profile.Interfaces.IAstrometrySettings>((m,_)=>m.Name=="get_Latitude"?37d:throw new Exception(m.Name));
        var profile=InterfaceStub.Create<NINA.Profile.Interfaces.IProfile>((m,_)=>m.Name=="get_AstrometrySettings"?site:throw new Exception("Unexpected profile/solver access: "+m.Name));
        var profiles=InterfaceStub.Create<NINA.Profile.Interfaces.IProfileService>((m,_)=>m.Name=="get_ActiveProfile"?profile:throw new Exception(m.Name));
        var service=new Nina.LivePolarAlignment.Capture.NinaPolarAlignmentCaptureService(profiles,null!,null!,null!){PoleMasterMode=true,PolarisCandidate=clickPolaris?new(744d/1280,341d/960):null};
        var image=new NINA.Image.ImageData.BaseImageData(raw,1280,960,16,false,new NINA.Image.ImageData.ImageMetaData(),null!,InterfaceStub.Create<NINA.Image.ImageAnalysis.IStarDetection>((_,_)=>null),null!);
        var method=service.GetType().GetMethod("SolveAlignmentImageAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        var task=(Task<NINA.PlateSolving.PlateSolveResult>)method.Invoke(service,new object?[]{image,null,null,CancellationToken.None,false})!;
        var integrated=await task;Assert.True(integrated.Success);Assert.InRange(integrated.Pixscale,30.5,31.5);
        Assert.Equal(original,raw);
    }
}
