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
    [InlineData("ASCOM.QHY.Camera", false)]
    [InlineData("QHY600M-fake-name", false)]
    public void OffersOnlyNativeRawPaths(string id, bool expected) {
        var info = new CameraInfo { Connected = true, DeviceId = id };
        var camera = InterfaceStub.Create<ICameraMediator>((m, _) => m.Name == "GetInfo" ? info : null);
        var service = new NinaPolarAlignmentCaptureService(null!, camera, null!, null!);
        Assert.Equal(expected, service.SupportsStreaming);
        info.Connected = false; Assert.False(service.SupportsStreaming);
    }

    [Theory]
    [InlineData("QHY600M-test")]
    [InlineData("QHY268M-test")]
    [InlineData("QHY268C-test")]
    [InlineData("QHY533-test")]
    [InlineData("future-device-without-a-model-prefix")]
    public void EveryNativeQhyDriverUsesTheSameStreamingPath(string id) {
        // A type-only instance: no constructor, connection, SDK or device operations.
        var native = (QHYCamera)RuntimeHelpers.GetUninitializedObject(typeof(QHYCamera));
        var info = new CameraInfo { Connected = true, DeviceId = id, CanShowLiveView = false,
            ReadoutMode = 1, ReadoutModes = new List<string> { "Photographic", "Bin3x3" } };
        var camera = InterfaceStub.Create<ICameraMediator>((m, _) => m.Name switch {
            "GetInfo" => info, "GetDevice" => native, _ => null
        });
        var service = new NinaPolarAlignmentCaptureService(null!, camera, null!, null!);
        // Native snapshot mode defaults to 0; the currently selected mode is not used.
        Assert.True(service.SupportsStreaming);
        info.Connected = false; Assert.False(service.SupportsStreaming);
    }

    [Theory]
    [InlineData("2CMS-0", true)]
    [InlineData("Photographic DSO", true)]
    [InlineData("Bin3x3", false)]
    [InlineData("3×3 Bin Mode", false)]
    [InlineData("Bin 3*3", false)]
    public void QhySnapshotReadoutModeExceptionDoesNotDependOnModel(string mode, bool expected) {
        var native = (QHYCamera)RuntimeHelpers.GetUninitializedObject(typeof(QHYCamera));
        var info = new CameraInfo { Connected = true, DeviceId = "AnyNativeQhy", ReadoutModes = new List<string> { mode } };
        var camera = InterfaceStub.Create<ICameraMediator>((m, _) => m.Name switch {
            "GetInfo" => info, "GetDevice" => native, _ => null
        });
        var service = new NinaPolarAlignmentCaptureService(null!, camera, null!, null!);
        Assert.Equal(expected, service.SupportsStreaming);
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

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EmptyNativeFrameStopsSourceBeforeRestoringAndReleasingCamera(bool qhy) {
        bool stopped = false, reserved = false, released = false, restored = false;
        CaptureSequence? requested = null;
        async IAsyncEnumerable<IExposureData> Source([EnumeratorCancellation] CancellationToken token) {
            try { await Task.Yield(); yield return null!; }
            finally { stopped = true; }
        }
        var native = qhy ? (QHYCamera)RuntimeHelpers.GetUninitializedObject(typeof(QHYCamera)) : null;
        int nativeReads = 0;
        var info = new CameraInfo { Connected = true, DeviceId = qhy ? "AnyQhyNative-test" : "ZWOptical_test", BinX = 1, BinY = 1, ExposureMin = .01, ExposureMax = 60 };
        var camera = InterfaceStub.Create<ICameraMediator>((m, args) => {
            switch (m.Name) {
                case "GetInfo": return info;
                case "GetDevice": return !stopped && nativeReads++ == 0 ? native : null;
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
            "get_Id" => Guid.Empty,
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
