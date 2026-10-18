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
    public bool SupportsAutoRaRotation {
        get {
            try {
                var info = telescope.GetInfo();
                RaRotation.SelectRate(new(info.Connected, info.AtPark, info.Slewing,
                    info.AlignmentMode is AlignmentMode.Polar or AlignmentMode.GermanPolar,
                    info.CanMovePrimaryAxis, info.CanSetTrackingEnabled, info.TrackingEnabled,
                    info.RightAscension, info.Declination, (int)info.SideOfPier,
                    info.PrimaryAxisRates.ToArray()));
                return true;
            }
            catch { return false; }
        }
    }
    public async Task RotateRaAsync(bool reverse, IProgress<double>? progress, CancellationToken token) {
        if (!await captureGate.WaitAsync(0, token)) throw new InvalidOperationException("Another alignment operation is running.");
        try {
            var mount = new RotationMount(telescope);
            await Task.Run(() => new RaRotation().RunAsync(mount, reverse, progress, token), CancellationToken.None);
        } finally { captureGate.Release(); }
    }
    private sealed class RotationMount : IRaRotationMount {
        private readonly ITelescopeMediator mediator;
        private readonly ITelescope device;
        private readonly (double, double)[] rates;
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
