using NINA.Core.Enum;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using Nina.LivePolarAlignment.Capture;
using Xunit;

namespace Nina.LivePolarAlignment.Integration.Tests;

[Collection("Isolated NINA environment")]
public sealed class RotationCaptureTests {
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    [InlineData(false,false,true)] [InlineData(true,false,true)]
    [InlineData(true,false,true,true)]
    public async Task ReturnUsesOppositeCommandAndCannotRepeatAfterCancellation(bool reverse, bool cancelReturn, bool continued = false, bool delayedHomeRefresh = false, bool overshoot = false) {
        using var cancellation = new CancellationTokenSource();
        double ra = 23;
        double movingRate = 0;
        bool tracking = true, returning = false;
        bool coordinatesRefreshed = false;
        bool overshootInjected=false;
        var commands = new List<double>();
        var device = InterfaceStub.Create<ITelescope>((method, args) => {
            switch (method.Name) {
                case "get_Id": return "ASCOM.ASIMount.Telescope";
                case "get_PrimaryMovingRate": return 8d;
                case "get_SiderealTime": return 2d;
                case "get_Connected": case "get_CanMovePrimaryAxis": case "get_CanSetTrackingEnabled": return true;
                case "get_AtPark": case "get_Slewing": return false;
                case "get_AlignmentMode": return AlignmentMode.GermanPolar;
                case "get_TrackingEnabled": return tracking;
                case "set_TrackingEnabled": tracking = (bool)args![0]!; return null;
                case "get_RightAscension":
                    ra = ((ra + movingRate * .25 / 15) % 24 + 24) % 24;
                    return ra;
                case "get_Declination":
                    if (movingRate != 0) coordinatesRefreshed = true;
                    return delayedHomeRefresh ? (coordinatesRefreshed ? 90d : 86.52472) : 80d;
                case "get_AtHome": return delayedHomeRefresh && !coordinatesRefreshed;
                case "get_SideOfPier": return PierSide.pierEast;
                case "GetAxisRates": return new List<(double, double)> { (overshoot?0:8, 8) };
                default: throw new InvalidOperationException("Unexpected call: " + method.Name);
            }
        });
        var mediator = InterfaceStub.Create<ITelescopeMediator>((method, args) => {
            if (method.Name == "GetDevice") return device;
            if (method.Name == "MoveAxis") {
                Assert.Equal(TelescopeAxes.Primary, args![0]);
                movingRate = (double)args[1]!; commands.Add(movingRate);
                if(overshoot && returning && movingRate==0 && !overshootInjected){ra=(ra-2d/15+24)%24;overshootInjected=true;}
                if (returning && cancelReturn && movingRate != 0) cancellation.Cancel();
                return null;
            }
            throw new InvalidOperationException("Unexpected mediator call: " + method.Name);
        });
        var service = new NinaPolarAlignmentCaptureService(null!, null!, null!, mediator);
        // Missing origin must not issue a command.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReturnRaToStartAsync(null, default));
        Assert.Empty(commands);
        await service.RotateRaAsync(reverse, null, default, continued ? 40 : 60);
        if(continued) await service.ContinueRaAsync(reverse,null,default,40);
        returning = true;
        if (cancelReturn) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReturnRaToStartAsync(null, cancellation.Token));
        else {
            await service.ReturnRaToStartAsync(null, default);
            Assert.InRange(Math.Abs(Core.RaRotation.WrapDegrees((ra - 23) * 15)), 0, .01);
        }
        var outbound = new[] { reverse ? -8d : 8d, 0d };
        if(overshoot)Assert.Contains(.25,commands);
        else Assert.Equal((continued ? outbound.Concat(outbound) : outbound).Concat(new[] { reverse ? 8d : -8d, 0d }), commands);
        Assert.True(tracking); Assert.Equal(0, movingRate);
        int count = commands.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReturnRaToStartAsync(null, default));
        Assert.Equal(count, commands.Count);
    }
    [Fact] public async Task ReturnUsesBoundedSlowCorrectionForStopOvershoot()=>await ReturnUsesOppositeCommandAndCannotRepeatAfterCancellation(false,false,false,false,true);

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task PreparationVerifiesUnparkBeforeAllowingCapture(int failure) {
        using var cancellation = new CancellationTokenSource();
        bool parked = true;
        int unparkCalls = 0;
        var device = InterfaceStub.Create<ITelescope>((method, _) => method.Name switch {
            "get_Connected" or "get_CanMovePrimaryAxis" or "get_CanSetTrackingEnabled" => true,
            "get_Id" => "ScopeSim.Telescope",
            "get_PrimaryMovingRate" => 4d,
            "get_SiderealTime" => 2d,
            "get_CanUnpark" => failure != 3,
            "get_AtPark" => parked,
            "get_Slewing" or "get_TrackingEnabled" => false,
            "get_AlignmentMode" => AlignmentMode.GermanPolar,
            "get_RightAscension" => 1d,
            "get_Declination" => 80d,
            "get_SideOfPier" => PierSide.pierEast,
            "GetAxisRates" => new List<(double, double)> { (0, 8) },
            _ => throw new InvalidOperationException("Unexpected preparation device call: " + method.Name)
        });
        var mediator = InterfaceStub.Create<ITelescopeMediator>((method, _) => {
            if (method.Name == "GetDevice") return device;
            if (method.Name == "UnparkTelescope") {
                unparkCalls++;
                if (failure == 2) { cancellation.Cancel(); return Task.FromCanceled<bool>(cancellation.Token); }
                if (failure == 1) return Task.FromResult(false);
                if (failure == 4) { cancellation.Cancel(); return Task.FromResult(false); }
                parked = false; return Task.FromResult(true);
            }
            throw new InvalidOperationException("Preparation must not move or slew: " + method.Name);
        });
        var service = new NinaPolarAlignmentCaptureService(null!, null!, null!, mediator);
        if (failure == 0) { await service.PrepareAutoRaAsync(cancellation.Token); Assert.False(parked); }
        else if (failure is 2 or 4) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAutoRaAsync(cancellation.Token));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAutoRaAsync(cancellation.Token));
        Assert.Equal(failure == 3 ? 0 : 1, unparkCalls);
    }

    [Theory]
    [InlineData(AutoRaAvailability.Ready)]
    [InlineData(AutoRaAvailability.Disconnected)]
    [InlineData(AutoRaAvailability.Parked)]
    [InlineData(AutoRaAvailability.Busy)]
    [InlineData(AutoRaAvailability.NotEquatorial)]
    [InlineData(AutoRaAvailability.AxisUnsupported)]
    [InlineData(AutoRaAvailability.TrackingUnsupported)]
    [InlineData(AutoRaAvailability.RateUnsupported)]
    [InlineData(AutoRaAvailability.InvalidCoordinates)]
    public void ReportsSpecificAutoRotationPrerequisite(AutoRaAvailability expected) {
        var info = new NINA.Equipment.Equipment.MyTelescope.TelescopeInfo {
            Connected = expected != AutoRaAvailability.Disconnected,
            AtPark = expected == AutoRaAvailability.Parked,
            Slewing = expected == AutoRaAvailability.Busy,
            AlignmentMode = expected == AutoRaAvailability.NotEquatorial ? AlignmentMode.AltAz : AlignmentMode.GermanPolar,
            CanMovePrimaryAxis = expected != AutoRaAvailability.AxisUnsupported,
            CanSetTrackingEnabled = expected != AutoRaAvailability.TrackingUnsupported,
            RightAscension = expected == AutoRaAvailability.InvalidCoordinates ? double.NaN : 1,
            Declination = 80,
            PrimaryAxisRates = new List<(double, double)> { expected == AutoRaAvailability.RateUnsupported ? (2d, 4d) : (0d, 8d) }
        };
        var device = InterfaceStub.Create<ITelescope>((method, _) => method.Name == "get_PrimaryMovingRate" ? 1d : null);
        var mediator = InterfaceStub.Create<ITelescopeMediator>((method, _) => method.Name switch {
            "GetInfo" => info, "GetDevice" => device, _ => null
        });
        var service = new NinaPolarAlignmentCaptureService(null!, null!, null!, mediator);
        Assert.Equal(expected, service.AutoRaAvailability);
        Assert.Equal(expected == AutoRaAvailability.Ready, service.SupportsAutoRaRotation);
    }

    [Theory]
    [InlineData(false, "ASCOM.ASIMount.Telescope", 8)] [InlineData(true, "ASCOM.ASIMount.Telescope", 8)]
    [InlineData(false, "Real.Mount", 0.5)] [InlineData(true, "Real.Mount", 2)]
    [InlineData(false, "ScopeSim.Telescope", 4)] [InlineData(true, "ASCOM.OmniSim.Telescope", 4)]
    public async Task AdapterUsesOnlyPrimaryAxisAndCleansUpCancelledMotion(bool reverse, string driverId, double expectedRate) {
        using var cancellation = new CancellationTokenSource();
        bool tracking = true;
        var commands = new List<(TelescopeAxes Axis, double Rate)>();
        var device = InterfaceStub.Create<ITelescope>((method, args) => {
            switch (method.Name) {
                case "get_Id": return driverId;
                case "get_PrimaryMovingRate": return expectedRate;
                case "get_SiderealTime": return 2d;
                case "get_Connected": case "get_CanMovePrimaryAxis": case "get_CanSetTrackingEnabled": return true;
                case "get_AtPark": case "get_Slewing": return false;
                case "get_AlignmentMode": return AlignmentMode.GermanPolar;
                case "get_TrackingEnabled": return tracking;
                case "set_TrackingEnabled": tracking = (bool)args![0]!; return null;
                case "get_RightAscension": return 23d;
                case "get_Declination": return 80d;
                case "get_SideOfPier": return PierSide.pierEast;
                case "GetAxisRates": return new List<(double, double)> { (.1, 8) };
                default: throw new InvalidOperationException("Unexpected device call: " + method.Name);
            }
        });
        var mediator = InterfaceStub.Create<ITelescopeMediator>((method, args) => {
            if (method.Name == "GetDevice") return device;
            if (method.Name == "MoveAxis") {
                var axis = (TelescopeAxes)args![0]!; var rate = (double)args[1]!;
                commands.Add((axis, rate));
                if (rate != 0) cancellation.Cancel();
                return null;
            }
            throw new InvalidOperationException("Unexpected mediator call: " + method.Name);
        });
        var capture = new NinaPolarAlignmentCaptureService(null!, null!, null!, mediator);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.RotateRaAsync(reverse, null, cancellation.Token));
        Assert.Equal(new[] { (TelescopeAxes.Primary, reverse ? -expectedRate : expectedRate), (TelescopeAxes.Primary, 0d) }, commands);
        Assert.True(tracking);
        // Reservation is released even when cancelled; a pre-cancelled retry reaches cancellation, not busy.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.RotateRaAsync(reverse, null, cancellation.Token));
    }
}
