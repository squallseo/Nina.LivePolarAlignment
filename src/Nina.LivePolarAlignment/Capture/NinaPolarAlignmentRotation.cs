using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Enum;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using Nina.LivePolarAlignment.Core;

namespace Nina.LivePolarAlignment.Capture;

public sealed partial class NinaPolarAlignmentCaptureService {
    public bool SupportsAutoRaRotation => AutoRaAvailability == AutoRaAvailability.Ready;
    public AutoRaAvailability AutoRaAvailability {
        get {
            try {
                var info = telescope.GetInfo();
                if (info?.Connected != true) return AutoRaAvailability.Disconnected;
                if (info.AtPark) return AutoRaAvailability.Parked;
                if (info.Slewing) return AutoRaAvailability.Busy;
                if (info.AlignmentMode is not (AlignmentMode.Polar or AlignmentMode.GermanPolar)) return AutoRaAvailability.NotEquatorial;
                if (!info.CanMovePrimaryAxis) return AutoRaAvailability.AxisUnsupported;
                if (!info.CanSetTrackingEnabled) return AutoRaAvailability.TrackingUnsupported;
                if (!double.IsFinite(info.RightAscension) || info.RightAscension is < 0 or >= 24
                    || !double.IsFinite(info.Declination) || Math.Abs(info.Declination) > 90) return AutoRaAvailability.InvalidCoordinates;
                try {
                    RaRotation.SelectRate(new(true, false, false, true, true, true, info.TrackingEnabled,
                        info.RightAscension, info.Declination, (int)info.SideOfPier, info.PrimaryAxisRates.ToArray()), RaRotation.MaximumRateForDriver((telescope.GetDevice() as ITelescope)?.Id));
                } catch (InvalidOperationException) { return AutoRaAvailability.RateUnsupported; }
                return AutoRaAvailability.Ready;
            } catch { return AutoRaAvailability.DriverError; }
        }
    }
    public bool CanPrepareAutoRaRotation {
        get {
            if (SupportsAutoRaRotation) return true;
            if (AutoRaAvailability != AutoRaAvailability.Parked) return false;
            try {
                var device = telescope.GetDevice() as ITelescope;
                if (device?.CanUnpark != true) return false;
                var state = new RotationMount(telescope).Read();
                RaRotation.SelectRate(state with { Parked = false }, RaRotation.MaximumRateForDriver(device.Id));
                return true;
            } catch { return false; }
        }
    }
    public async Task PrepareAutoRaAsync(CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (!await captureGate.WaitAsync(0, token)) throw new InvalidOperationException("Another alignment operation is running.");
        try {
            var device = telescope.GetDevice() as ITelescope ?? throw new InvalidOperationException("Connect a mount before automatic alignment.");
            var mount = new RotationMount(telescope);
            var before = mount.Read();
            RaRotation.SelectRate(before with { Parked = false }, mount.MaximumRate);
            if (before.Parked) {
                if (!device.CanUnpark) throw new InvalidOperationException("The mount does not support Unpark. Unpark it manually before starting.");
                token.ThrowIfCancellationRequested();
                var unparked = await telescope.UnparkTelescope(SilentProgress.Instance, token);
                token.ThrowIfCancellationRequested();
                if (!unparked) throw new InvalidOperationException("Could not unpark the mount. Alignment did not start.");
            }
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (mount.Read().Parked) {
                token.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("The mount is still parked. Alignment did not start.");
                await Task.Delay(100, token);
            }
            token.ThrowIfCancellationRequested();
            RaRotation.SelectRate(mount.Read(), mount.MaximumRate);
        } finally { captureGate.Release(); }
    }
    public async Task RotateRaAsync(bool reverse, IProgress<double>? progress, CancellationToken token, double targetDegrees = 90) {
        if (!await captureGate.WaitAsync(0, token)) throw new InvalidOperationException("Another alignment operation is running.");
        try {
            await RotateRaCoreAsync(reverse, progress, token, targetDegrees);
        } finally { captureGate.Release(); }
    }
    private Task RotateRaCoreAsync(bool reverse, IProgress<double>? progress, CancellationToken token, double targetDegrees) {
        token.ThrowIfCancellationRequested();
        var mount = new RotationMount(telescope);
        progress?.Report(0);
        return Task.Run(() => new RaRotation().RunAsync(mount, reverse, progress, token, targetDegrees, mount.MaximumRate), CancellationToken.None);
    }
    public Task<SolvedPolarFrame> CaptureAndSolveWithRotationAsync(double exposureSeconds, bool reverse,
        IProgress<double>? progress, CancellationToken token, double targetDegrees = 90) =>
        CaptureAndSolveCoreAsync(exposureSeconds, token, null,
            ct => RotateRaCoreAsync(reverse, progress, ct, targetDegrees));

    private sealed class RotationMount : IRaRotationMount {
        private readonly ITelescopeMediator mediator;
        private readonly ITelescope device;
        private readonly (double, double)[] rates;
        public double MaximumRate => RaRotation.MaximumRateForDriver(device.Id);
        public RotationMount(ITelescopeMediator mediator) {
            this.mediator = mediator;
            device = mediator.GetDevice() as ITelescope ?? throw new InvalidOperationException("No telescope device connected.");
            rates = device.GetAxisRates(TelescopeAxes.Primary).ToArray();
        }
        private void CheckDevice() {
            if (!ReferenceEquals(mediator.GetDevice(), device)) throw new InvalidOperationException("Telescope device changed during rotation.");
        }
        public RaMountState Read() {
            CheckDevice();
            return new(device.Connected, device.AtPark, device.Slewing, device.AlignmentMode is AlignmentMode.Polar or AlignmentMode.GermanPolar,
                device.CanMovePrimaryAxis, device.CanSetTrackingEnabled, device.TrackingEnabled,
                device.RightAscension, device.Declination, (int)device.SideOfPier,
                rates);
        }
        public void SetTracking(bool enabled) { CheckDevice(); device.TrackingEnabled = enabled; }
        // Use NINA's manual-control reversal setting, as its direction buttons do.
        public void Move(double rate) { CheckDevice(); mediator.MoveAxis(TelescopeAxes.Primary, rate); }
        // The original device is retained so emergency abort never targets a replacement mount.
        public void Abort() => device.StopSlew();
    }
}
