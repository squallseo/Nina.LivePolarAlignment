using NINA.Core.Enum;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using Nina.LivePolarAlignment.Capture;
using Xunit;

namespace Nina.LivePolarAlignment.Integration.Tests;

[Collection("Isolated NINA environment")]
public sealed class RotationCaptureTests {
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
