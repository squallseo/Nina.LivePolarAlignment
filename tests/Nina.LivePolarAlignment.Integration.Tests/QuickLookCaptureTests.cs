using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using Nina.LivePolarAlignment.Capture;
using Xunit;

namespace Nina.LivePolarAlignment.Integration.Tests;

[Collection("Isolated NINA environment")]
public sealed class QuickLookCaptureTests {
    private static IImageData Image(ushort[] pixels, int width, int height) => new BaseImageData(
        pixels, width, height, 16, false, new ImageMetaData(), null!,
        InterfaceStub.Create<NINA.Image.ImageAnalysis.IStarDetection>((_, _) => null), null!);

    [Fact]
    public void ReducedPreviewAndFullDisplayKeepRawPixelsUnchanged() {
        var pixels = Enumerable.Repeat((ushort)300, 2000 * 1400).ToArray();
        pixels[700 * 2000 + 1000] = 60000;
        var original = (ushort[])pixels.Clone();
        var prepared = QuickLookPreview.Prepare(Image(pixels, 2000, 1400), CancellationToken.None);
        Assert.True(prepared.Image.IsFrozen);
        Assert.InRange(prepared.Image.PixelWidth, 1, 1600);
        Assert.InRange(prepared.Image.PixelHeight, 1, 1200);
        var full = prepared.CreateFullResolution(CancellationToken.None);
        Assert.Equal(2000, full.PixelWidth); Assert.Equal(1400, full.PixelHeight);
        var display = new byte[pixels.Length];
        full.CopyPixels(display, 2000, 0);
        Assert.True(display[700 * 2000 + 1000] > display[0]);
        Assert.True(original.AsSpan().SequenceEqual(pixels));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CapturePublishesPixelsWithoutWaitingForHostPreparationOrSolving(bool automaticRotation) {
        using var cancellation = new CancellationTokenSource();
        var data = Image(Enumerable.Repeat((ushort)300, 128 * 96).ToArray(), 128, 96);
        var exposure = InterfaceStub.Create<IExposureData>((method, _) => method.Name == "ToImageData" ? Task.FromResult(data) : null);
        bool released = false, published = false;
        var camera = InterfaceStub.Create<ICameraMediator>((method, _) => method.Name switch {
            "GetInfo" => new NINA.Equipment.Equipment.MyCamera.CameraInfo { Connected = true },
            "IsFreeToCapture" => true,
            "ReleaseCaptureBlock" => Release(),
            _ => null
        });
        object? Release() { released = true; return null; }
        var imaging = InterfaceStub.Create<IImagingMediator>((method, _) => method.Name == "CaptureImage"
            ? Task.FromResult(exposure) : throw new InvalidOperationException("Full host preparation must not delay preview: " + method.Name));
        var optics = InterfaceStub.Create<ITelescopeSettings>((method, _) => method.Name == "get_FocalLength" ? 1000d : null);
        var sensor = InterfaceStub.Create<ICameraSettings>((method, _) => method.Name == "get_PixelSize" ? 3.76d : null);
        var profile = InterfaceStub.Create<IProfile>((method, _) => method.Name switch {
            "get_Id" => Guid.Empty,
            "get_TelescopeSettings" => optics, "get_CameraSettings" => sensor,
            "get_PlateSolveSettings" => new NINA.Profile.PlateSolveSettings(), _ => null
        });
        var profiles = InterfaceStub.Create<IProfileService>((method, _) => method.Name == "get_ActiveProfile" ? profile : null);
        var telescope = InterfaceStub.Create<ITelescopeMediator>((_, _) => null);
        var service = new NinaPolarAlignmentCaptureService(profiles, camera, imaging, telescope);
        service.PreviewAvailable += preview => {
            published = true;
            Assert.True(preview.IsFrozen);
            cancellation.Cancel(); // Cancellation at first display prevents the solver from starting.
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => automaticRotation
            ? service.CaptureAndSolveWithRotationAsync(1, false, null, cancellation.Token)
            : service.CaptureAndSolveAsync(1, cancellation.Token));
        Assert.True(published); Assert.True(released);
    }
}
