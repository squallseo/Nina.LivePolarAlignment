using System.Reflection;
using System.Runtime.CompilerServices;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using Nina.LivePolarAlignment.Capture;
using Xunit;

namespace Nina.LivePolarAlignment.Integration.Tests;

public class InterfaceStub : DispatchProxy {
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class {
        var value = DispatchProxy.Create<T, InterfaceStub>();
        ((InterfaceStub)(object)value).Handler = handler; return value;
    }
}

[Collection("Isolated NINA environment")]
public sealed class StreamingCaptureTests {
    [Theory]
    [InlineData("ZWOptical_test", true)]
    [InlineData("ToupTek_test", true)]
    [InlineData("ASCOM.Camera", false)]
    [InlineData("Nikon", false)]
    public void OffersOnlyNativeRawPaths(string id, bool expected) {
        var info = new CameraInfo { Connected = true, DeviceId = id };
        var camera = InterfaceStub.Create<ICameraMediator>((m, _) => m.Name == "GetInfo" ? info : null);
        var service = new NinaPolarAlignmentCaptureService(null!, camera, null!, null!);
        Assert.Equal(expected, service.SupportsStreaming);
        info.Connected = false; Assert.False(service.SupportsStreaming);
    }

    [Fact]
    public async Task UnsupportedOrBusyCameraDoesNotAcquireReservationAndCanRetry() {
        var info = new CameraInfo { Connected = true, DeviceId = "ASCOM.Camera" };
        var camera = InterfaceStub.Create<ICameraMediator>((m, _) => m.Name switch {
            "GetInfo" => info, "IsFreeToCapture" => false,
            "RegisterCaptureBlock" => throw new Exception("Must not reserve"), _ => null
        });
        var service = new NinaPolarAlignmentCaptureService(null!, camera, null!, null!);
        await using (var reader = service.StreamAndSolveAsync(1, CancellationToken.None).GetAsyncEnumerator())
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await reader.MoveNextAsync());
        info.DeviceId = "ZWOptical_test";
        await using var retry = service.StreamAndSolveAsync(1, CancellationToken.None).GetAsyncEnumerator();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await retry.MoveNextAsync());
        Assert.Contains("busy", error.Message); // Gate was released by the unsupported attempt.
    }

    [Fact]
    public async Task EmptyNativeFrameStopsSourceBeforeRestoringAndReleasingCamera() {
        bool stopped = false, reserved = false, released = false, restored = false;
        CaptureSequence? requested = null;
        async IAsyncEnumerable<IExposureData> Source([EnumeratorCancellation] CancellationToken token) {
            try { await Task.Yield(); yield return null!; }
            finally { stopped = true; }
        }
        var info = new CameraInfo { Connected = true, DeviceId = "ZWOptical_test", BinX = 1, BinY = 1, ExposureMin = .01, ExposureMax = 60 };
        var camera = InterfaceStub.Create<ICameraMediator>((m, args) => {
            switch (m.Name) {
                case "GetInfo": return info;
                case "IsFreeToCapture": return true;
                case "RegisterCaptureBlock": reserved = true; return null;
                case "ReleaseCaptureBlock": Assert.True(stopped); released = true; return null;
                case "SetReadoutMode": Assert.True(stopped); restored = true; return null;
                case "LiveView": requested = (CaptureSequence)args![0]!; return Source((CancellationToken)args[1]!);
                default: return null;
            }
        });
        var telescopeSettings = InterfaceStub.Create<ITelescopeSettings>((m, _) => m.Name == "get_FocalLength" ? 400d : null);
        var cameraSettings = InterfaceStub.Create<ICameraSettings>((m, _) => m.Name == "get_PixelSize" ? 3.76d : null);
        var solveSettings = new NINA.Profile.PlateSolveSettings();
        var profile = InterfaceStub.Create<IProfile>((m, _) => m.Name switch {
            "get_PlateSolveSettings" => solveSettings, "get_TelescopeSettings" => telescopeSettings,
            "get_CameraSettings" => cameraSettings, _ => null
        });
        var profiles = InterfaceStub.Create<IProfileService>((m, _) => m.Name == "get_ActiveProfile" ? profile : null);
        var telescope = InterfaceStub.Create<ITelescopeMediator>((_, _) => null);
        var service = new NinaPolarAlignmentCaptureService(profiles, camera, null!, telescope);
        await using var reader = service.StreamAndSolveAsync(1, CancellationToken.None).GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await reader.MoveNextAsync());
        Assert.True(reserved); Assert.True(released); Assert.True(restored);
        Assert.NotNull(requested); Assert.False(requested.EnableSubSample);
        Assert.Equal(1, requested.Binning.X);
    }
}
