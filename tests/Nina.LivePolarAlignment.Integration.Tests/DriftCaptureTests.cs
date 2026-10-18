using NINA.Core.Enum;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using Nina.LivePolarAlignment.Capture;
using Xunit;

namespace Nina.LivePolarAlignment.Integration.Tests;

[Collection("Isolated NINA environment")]
public sealed class DriftCaptureTests {
    [Theory]
    [InlineData("Stopped", false, true)] [InlineData("Looping", false, true)]
    [InlineData("Guiding", false, false)] [InlineData("Paused", false, false)]
    [InlineData("Calibrating", false, false)] [InlineData("LostLock", false, false)]
    [InlineData("Stopped", true, false)]
    public void RequiresUnguidedSiderealTrackingWithoutIssuingCommands(string guiderState, bool pulseGuiding, bool expected) {
        var mount = InterfaceStub.Create<ITelescope>((m, _) => m.Name switch {
            "get_Connected" or "get_TrackingEnabled" => true,
            "get_AtPark" or "get_Slewing" => false, "get_IsPulseGuiding" => pulseGuiding,
            "get_TrackingMode" => TrackingMode.Sidereal, "get_AlignmentMode" => AlignmentMode.GermanPolar,
            _ => throw new InvalidOperationException("Unexpected hardware command: " + m.Name)
        });
        var guider = InterfaceStub.Create<IGuider>((m, _) => m.Name switch {
            "get_Connected" => true, "get_State" => guiderState, "get_ShiftEnabled" => false,
            _ => throw new InvalidOperationException("Unexpected guider command: " + m.Name)
        });
        var telescope = InterfaceStub.Create<ITelescopeMediator>((m, _) => m.Name == "GetDevice" ? mount : throw new Exception(m.Name));
        var guiderMediator = InterfaceStub.Create<IGuiderMediator>((m, _) => m.Name == "GetDevice" ? guider : throw new Exception(m.Name));
        var capture = new NinaPolarAlignmentCaptureService(null!, null!, null!, telescope) { DriftGuider = guiderMediator };
        if (expected) capture.ValidateDriftTracking();
        else Assert.Throws<InvalidOperationException>(capture.ValidateDriftTracking);
    }
}
