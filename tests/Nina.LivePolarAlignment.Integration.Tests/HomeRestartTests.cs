using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using Nina.LivePolarAlignment.Capture;
using Xunit;
namespace Nina.LivePolarAlignment.Integration.Tests;
[Collection("Isolated NINA environment")]
public sealed class HomeRestartTests {
    [Fact] public async Task FindHomeWaitsForDelayedAtHomeTelemetryRatherThanRejectingImmediately() {
        bool found=false;int reads=0;
        var device=InterfaceStub.Create<ITelescope>((m,_)=>m.Name switch {
            "get_Connected" or "get_CanFindHome"=>true,"get_CanSetTrackingEnabled" or "get_Slewing" or "get_AtPark"=>false,
            "get_AtHome"=>found && ++reads>=4,_=>throw new Exception("Unexpected mount command: "+m.Name)});
        Task<bool> Find(){found=true;return Task.FromResult(true);}
        var mediator=InterfaceStub.Create<ITelescopeMediator>((m,_)=>m.Name switch{"GetDevice"=>device,"FindHome"=>Find(),_=>throw new Exception(m.Name)});
        await new NinaPolarAlignmentCaptureService(null!,null!,null!,mediator).HomeForRestartAsync(default);
        Assert.True(reads>=5);
    }
    [Theory]
    [InlineData(true,false,true,true)]
    [InlineData(true,false,false,false)]
    [InlineData(true,true,true,false)]
    [InlineData(false,false,true,false)]
    public void HomeStatusIsReadWithoutCommandingTheMount(bool connected,bool slewing,bool home,bool expected) {
        var device=InterfaceStub.Create<ITelescope>((m,_)=>m.Name switch {
            "get_Connected"=>connected,"get_Slewing"=>slewing,"get_AtHome"=>home,
            _=>throw new Exception("Home status must not command the mount: "+m.Name)});
        var mediator=InterfaceStub.Create<ITelescopeMediator>((m,_)=>m.Name=="GetDevice"?device:throw new Exception(m.Name));
        var service=new NinaPolarAlignmentCaptureService(null!,null!,null!,mediator);
        Assert.Equal(expected,service.MountAtHome);
    }
    [Fact] public async Task PoleMasterPreparationNormalizesTrackingBeforeReferenceWithoutMovingAxes() {
        bool tracking=true;double dec=86.52472;
        var device=InterfaceStub.Create<ITelescope>((m,a)=>m.Name switch {
            "get_Id"=>"test","get_Connected" or "get_CanMovePrimaryAxis" or "get_CanSetTrackingEnabled"=>true,
            "get_AtPark" or "get_Slewing"=>false,"get_PrimaryMovingRate"=>6d,
            "get_AlignmentMode"=>NINA.Core.Enum.AlignmentMode.GermanPolar,
            "get_TrackingEnabled"=>tracking,"set_TrackingEnabled"=>Set((bool)a![0]!),
            "get_RightAscension"=>5d,"get_Declination"=>dec,"get_SideOfPier"=>NINA.Core.Enum.PierSide.pierEast,
            "GetAxisRates"=>new List<(double,double)>{(.1,6)},_=>throw new Exception("Unexpected axis command: "+m.Name)});
        object? Set(bool value){tracking=value;if(!value)dec=90;return null;}
        var mediator=InterfaceStub.Create<ITelescopeMediator>((m,_)=>m.Name=="GetDevice"?device:throw new Exception(m.Name));
        var service=new NinaPolarAlignmentCaptureService(null!,null!,null!,mediator){PoleMasterMode=true};
        await service.PrepareAutoRaAsync(default);Assert.False(tracking);Assert.Equal(90,dec);
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task ExplicitFindHomeConfirmsArrivalAndAbortsCancellationOrFailure(int failure) {
        bool home=false,tracking=true;int homes=0,aborts=0;using var cancel=new CancellationTokenSource();
        var device=InterfaceStub.Create<ITelescope>((m,a)=>m.Name switch {
            "get_Connected" or "get_CanFindHome" or "get_CanSetTrackingEnabled"=>true,
            "get_Slewing" or "get_AtPark"=>false,"get_AtHome"=>home,"get_TrackingEnabled"=>tracking,
            "set_TrackingEnabled"=>SetTracking((bool)a![0]!),"StopSlew"=>Abort(),_=>throw new Exception("Unexpected mount operation: "+m.Name)});
        object? SetTracking(bool value){tracking=value;return null;}object? Abort(){aborts++;return null;}
        var mediator=InterfaceStub.Create<ITelescopeMediator>((m,a)=>m.Name switch{"GetDevice"=>device,"FindHome"=>Find((CancellationToken)a![1]!),_=>throw new Exception("Unexpected mediator operation: "+m.Name)});
        async Task<bool> Find(CancellationToken token){homes++;await Task.Yield();if(failure==1){cancel.Cancel();return false;}if(failure==2)return true;home=true;return true;}
        var service=new NinaPolarAlignmentCaptureService(null!,null!,null!,mediator);
        Assert.True(service.CanHomeForRestart);
        if(failure==0)await service.HomeForRestartAsync(cancel.Token);
        else if(failure==1)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.HomeForRestartAsync(cancel.Token));
        else await Assert.ThrowsAsync<InvalidOperationException>(()=>service.HomeForRestartAsync(cancel.Token));
        Assert.Equal(1,homes);Assert.False(tracking);Assert.Equal(failure==0?0:1,aborts);
    }
}
