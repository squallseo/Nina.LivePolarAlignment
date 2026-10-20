using NINA.Core.Enum;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using Nina.LivePolarAlignment.Capture;
using Xunit;

namespace Nina.LivePolarAlignment.Integration.Tests;

[Collection("Isolated NINA environment")]
public sealed class RotationCaptureTests {
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task PreparationVerifiesUnparkBeforeAllowingCapture(int failure) {
        using var cancellation = new CancellationTokenSource();
        bool parked = true;
        int unparkCalls = 0;
        var device = InterfaceStub.Create<ITelescope>((method, _) => method.Name switch {
            "get_Connected" or "get_CanMovePrimaryAxis" or "get_CanSetTrackingEnabled" => true,
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
        var mediator = InterfaceStub.Create<ITelescopeMediator>((method, _) => method.Name == "GetInfo" ? info : null);
        var service = new NinaPolarAlignmentCaptureService(null!, null!, null!, mediator);
        Assert.Equal(expected, service.AutoRaAvailability);
        Assert.Equal(expected == AutoRaAvailability.Ready, service.SupportsAutoRaRotation);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AdapterUsesOnlyPrimaryAxisAndCleansUpCancelledMotion(bool reverse) {
        using var cancellation = new CancellationTokenSource();
        bool tracking = true;
        var commands = new List<(TelescopeAxes Axis, double Rate)>();
        var device = InterfaceStub.Create<ITelescope>((method, args) => {
            switch (method.Name) {
                case "get_Connected": case "get_CanMovePrimaryAxis": case "get_CanSetTrackingEnabled": return true;
                case "get_AtPark": case "get_Slewing": return false;
                case "get_AlignmentMode": return AlignmentMode.GermanPolar;
                case "get_TrackingEnabled": return tracking;
                case "set_TrackingEnabled": tracking = (bool)args![0]!; return null;
                case "get_RightAscension": return 23d;
                case "get_Declination": return 80d;
                case "get_SideOfPier": return PierSide.pierEast;
                case "GetAxisRates": return new List<(double, double)> { (.1, 2) };
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
        Assert.Equal(new[] { (TelescopeAxes.Primary, reverse ? -1d : 1d), (TelescopeAxes.Primary, 0d) }, commands);
        Assert.True(tracking);
        // Reservation is released even when cancelled; a pre-cancelled retry reaches cancellation, not busy.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.RotateRaAsync(reverse, null, cancellation.Token));
    }
}
