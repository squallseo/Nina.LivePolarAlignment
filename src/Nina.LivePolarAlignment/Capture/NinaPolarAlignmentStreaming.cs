using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.PlateSolving;
using Nina.LivePolarAlignment.Core;

namespace Nina.LivePolarAlignment.Capture;

public sealed partial class NinaPolarAlignmentCaptureService {
    // The first streaming option is limited to native RAW video paths, not DSLR JPEG LiveView.
    public bool SupportsStreaming {
        get {
            var info = camera.GetInfo();
            return info?.Connected == true && (info.DeviceId?.StartsWith("ZWOptical_", StringComparison.OrdinalIgnoreCase) == true
                || info.DeviceId?.StartsWith("ToupTek_", StringComparison.OrdinalIgnoreCase) == true);
        }
    }

    public async IAsyncEnumerable<SolvedPolarFrame> StreamAndSolveAsync(double exposureSeconds,
        [EnumeratorCancellation] CancellationToken token) {
        if (!double.IsFinite(exposureSeconds) || exposureSeconds <= 0 || exposureSeconds > 60)
            throw new ArgumentOutOfRangeException(nameof(exposureSeconds));
        bool reserved = false;
        var info = camera.GetInfo();
        string id = info?.DeviceId ?? string.Empty;
        short binX = info?.BinX ?? 1, binY = info?.BinY ?? 1, readout = info?.ReadoutMode ?? 0;
        int gain = info?.Gain ?? -1, offset = info?.Offset ?? -1;
        bool wasSubSampleEnabled = info?.IsSubSampleEnabled == true;
        var rectangle = info?.IsSubSampleEnabled == true
            ? new ObservableRectangle(info.SubSampleX, info.SubSampleY, info.SubSampleWidth, info.SubSampleHeight)
            : new ObservableRectangle(0, 0, info?.XSize ?? 0, info?.YSize ?? 0);
        if (!await captureGate.WaitAsync(0, token)) throw new InvalidOperationException("Another alignment capture is running.");
        try {
            if (!SupportsStreaming) throw new InvalidOperationException("Streaming requires a connected native ZWO ASI or ToupTek camera. Use single frames for other drivers.");
            if (info!.IsExposing || info.LiveViewEnabled || !camera.IsFreeToCapture(this))
                throw new InvalidOperationException("The camera is busy. Stop other capture or live view first.");
            if (exposureSeconds < info.ExposureMin || info.ExposureMax > 0 && exposureSeconds > info.ExposureMax)
                throw new InvalidOperationException("Streaming exposure is outside the camera range.");
            var settings = profiles.ActiveProfile.PlateSolveSettings;
            var focal = profiles.ActiveProfile.TelescopeSettings.FocalLength;
            var pixels = profiles.ActiveProfile.CameraSettings.PixelSize;
            if (!double.IsFinite(focal) || focal <= 0 || !double.IsFinite(pixels) || pixels <= 0)
                throw new InvalidOperationException("Set focal length and camera pixel size before streaming.");
            var bin = (short)Math.Max(1, (int)settings.Binning);
            var sequence = new CaptureSequence(exposureSeconds, CaptureSequence.ImageTypes.SNAPSHOT, null!, new BinningMode(bin, bin), 1) {
                Gain = settings.Gain, Offset = offset, EnableSubSample = false
            };
            var solver = new ImageSolver(PlateSolverFactory.GetPlateSolver(settings), PlateSolverFactory.GetBlindSolver(settings));
            var parameter = new PlateSolveParameter {
                Binning = bin, FocalLength = focal, PixelSize = pixels,
                DownSampleFactor = settings.DownSampleFactor, MaxObjects = settings.MaxObjects,
                Regions = settings.Regions, SearchRadius = settings.SearchRadius,
                BlindFailoverEnabled = settings.BlindFailoverEnabled, DisableNotifications = true,
                Coordinates = telescope.GetInfo()?.Connected == true ? telescope.GetCurrentPosition() : null!
            };
            camera.RegisterCaptureBlock(this); reserved = true;
            await using var buffer = new LatestFrameBuffer<StreamFrame>(Receive,
                TimeSpan.FromSeconds(Math.Max(15, exposureSeconds * 3 + 5)), token);
            while (true) {
                var frame = await buffer.ReadAsync(token);
                token.ThrowIfCancellationRequested();
                var image = await imaging.PrepareImage(frame.Image, new PrepareImageParameters(autoStretch: true, detectStars: false), token);
                var solution = await solver.Solve(frame.Image, parameter, SilentProgress.Instance, token);
                token.ThrowIfCancellationRequested();
                if (!solution.Success || solution.Coordinates == null || !double.IsFinite(solution.Pixscale) || solution.Pixscale <= 0)
                    throw new InvalidOperationException("Streaming plate solve failed. Increase exposure or use single frames. No estimated guidance is shown.");
                var preview = image?.Image;
                if (preview != null && !preview.IsFrozen) {
                    preview = preview.Clone();
                    if (preview.CanFreeze) preview.Freeze(); else preview = null;
                }
                yield return new SolvedPolarFrame(solution.Coordinates.RADegrees, solution.Coordinates.Dec,
                    solution.PositionAngle, solution.Pixscale, frame.Image.Properties.Width, frame.Image.Properties.Height,
                    solution.Flipped, frame.CapturedAt, preview, frame.Image);
            }

            async IAsyncEnumerable<StreamFrame> Receive([EnumeratorCancellation] CancellationToken ct) {
                // This task alone advances and disposes the NINA enumerator, including StopLiveView.
                await foreach (var exposure in camera.LiveView(sequence, ct).WithCancellation(ct).ConfigureAwait(false)) {
                    ct.ThrowIfCancellationRequested();
                    if (exposure == null) throw new InvalidOperationException("Camera returned an empty streaming frame.");
                    var currentCamera = camera.GetInfo();
                    if (currentCamera?.Connected != true || currentCamera.DeviceId != id)
                        throw new InvalidOperationException("Camera disconnected or changed during streaming.");
                    var receivedAt = DateTimeOffset.UtcNow;
                    var raw = await exposure.ToImageData(SilentProgress.Instance, ct).ConfigureAwait(false);
                    if (raw?.Data?.FlatArray == null || raw.Properties.Width <= 0 || raw.Properties.Height <= 0
                        || (long)raw.Properties.Width * raw.Properties.Height != raw.Data.FlatArray.Length)
                        throw new InvalidOperationException("Invalid RAW streaming frame.");
                    // Native video drivers timestamp download, not the sensor shutter. Use a
                    // receive-time midpoint estimate; this option cannot certify hardware timing.
                    yield return new(raw, receivedAt.AddSeconds(-exposureSeconds / 2));
                }
            }
        } finally {
            try {
                if (reserved) {
                    // Some native ToupTek versions restore still mode asynchronously after StopLiveView.
                    if (id.StartsWith("ToupTek_", StringComparison.OrdinalIgnoreCase) && camera.GetDevice() is ICamera device && device.Id == id) {
                        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(15, exposureSeconds * 3 + 5));
                        while (device.Connected && device.LiveViewEnabled) {
                            if (DateTime.UtcNow > deadline) throw new TimeoutException("Video mode did not stop. Reconnect the camera before capturing again.");
                            await Task.Delay(25);
                        }
                    }
                    var current = camera.GetInfo();
                    if (current?.Connected == true && current.DeviceId == id) {
                        camera.SetReadoutMode(readout); camera.SetBinning(binX, binY);
                        if (camera.GetDevice() is ICamera restored) {
                            if (restored.CanSetGain) restored.Gain = gain;
                            if (restored.CanSetOffset) restored.Offset = offset;
                        }
                        if (current.CanSubSample) {
                            camera.SetSubSambleRectangle(rectangle);
                            if (camera.GetDevice() is ICamera restoredRoi) restoredRoi.EnableSubSample = wasSubSampleEnabled;
                        }
                    }
                }
            } catch (Exception error) {
                throw new StreamingCleanupException(error);
            } finally {
                try { if (reserved) camera.ReleaseCaptureBlock(this); }
                finally { captureGate.Release(); }
            }
        }
    }
    private sealed class SilentProgress : IProgress<ApplicationStatus> {
        public static SilentProgress Instance { get; } = new();
        public void Report(ApplicationStatus value) { }
    }
    private sealed record StreamFrame(IImageData Image, DateTimeOffset CapturedAt);
}
