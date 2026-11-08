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
    private sealed record RaReturnPosition(ITelescope Device, double StartHourAngle, double EndHourAngle,
        double Declination, int PierSide, bool Reverse);
    private RaReturnPosition? raReturn;
    public void ResetRaReturn() => raReturn = null;
    private static double HourAngle(ITelescope device) {
        var sidereal = device.SiderealTime;
        var ra = device.RightAscension;
        if (!double.IsFinite(sidereal) || sidereal is < 0 or >= 24 || !double.IsFinite(ra) || ra is < 0 or >= 24)
            throw new InvalidOperationException("Valid sidereal time and RA are required to return to the starting position.");
        return RaRotation.WrapDegrees((sidereal - ra) * 15);
    }
    public Task ReturnRaToStartAsync(IProgress<double>? progress, CancellationToken token) => ReturnRaToStartCoreAsync(progress,token);
    private async Task ReturnRaToStartCoreAsync(IProgress<double>? progress, CancellationToken token,bool gateAlreadyHeld=false) {
        token.ThrowIfCancellationRequested();
        if (!gateAlreadyHeld && !await captureGate.WaitAsync(0, token)) throw new InvalidOperationException("Another alignment operation is running.");
        try {
            var saved = raReturn ?? throw new InvalidOperationException("No completed RA rotation to return from. Reset calibration.");
            // Consume before attempting motion: cancellation/failure must never repeat a full return.
            raReturn = null;
            var mount = new RotationMount(telescope);
            var current = mount.Read();
            RaRotation.SelectRate(current, mount.SelectedRate);
            if (!ReferenceEquals(saved.Device, telescope.GetDevice()) || Math.Abs(current.DecDegrees - saved.Declination) > .2
                || (saved.PierSide >= 0 && current.PierSide != saved.PierSide))
                throw new InvalidOperationException("Mount, Dec or pier side changed before RA return. Reset calibration.");
            var currentHourAngle = HourAngle(saved.Device);
            var outbound = RaRotation.WrapDegrees(saved.EndHourAngle - saved.StartHourAngle);
            var remaining = RaRotation.WrapDegrees(currentHourAngle - saved.StartHourAngle);
            if (Math.Sign(remaining) != Math.Sign(outbound) || Math.Abs(RaRotation.WrapDegrees(currentHourAngle - saved.EndHourAngle)) > 1)
                throw new InvalidOperationException("Mount moved since calibration. Automatic RA return refused. Reset calibration.");
            NINA.Core.Utility.Logger.Info($"Polar RA return: target={Math.Abs(remaining):F4} deg, selectedRate={mount.SelectedRate:G8} deg/s");
            await Task.Run(() => new RaRotation().ReturnAsync(mount, !saved.Reverse, progress, token,
                Math.Abs(remaining), mount.SelectedRate, Math.Sign(outbound)), CancellationToken.None);
            token.ThrowIfCancellationRequested();
            var residual = RaRotation.WrapDegrees(HourAngle(saved.Device) - saved.StartHourAngle);
            for(int attempt=0;attempt<2 && Math.Abs(residual)>.5 && Math.Abs(residual)<=5;attempt++) {
                var fineState=mount.Read();
                if(Math.Abs(fineState.DecDegrees-saved.Declination)>.2 || (saved.PierSide>=0 && fineState.PierSide!=saved.PierSide))
                    throw new InvalidOperationException("Dec or pier side changed before fine RA return correction.");
                double fineRate=RaRotation.SelectRate(fineState,Math.Min(.25,mount.SelectedRate));
                bool fineReverse=Math.Sign(residual)==Math.Sign(outbound)?!saved.Reverse:saved.Reverse;
                NINA.Core.Utility.Logger.Info($"Polar RA return fine correction: residual={residual:F4} deg; rate={fineRate:G8} deg/s; reverse={fineReverse}");
                await Task.Run(()=>new RaRotation().ReturnAsync(mount,fineReverse,null,token,Math.Abs(residual),fineRate,Math.Sign(residual)),CancellationToken.None);
                token.ThrowIfCancellationRequested();residual=RaRotation.WrapDegrees(HourAngle(saved.Device)-saved.StartHourAngle);
            }
            var error = Math.Abs(residual);
            if (error > .5) throw new InvalidOperationException($"RA return missed the starting position by {error:F3} degrees. Reset calibration.");
            NINA.Core.Utility.Logger.Info($"Polar RA return complete: position error={error:F4} deg");
        } catch (Exception error) {
            NINA.Core.Utility.Logger.Error(error); throw;
        } finally { if(!gateAlreadyHeld)captureGate.Release(); }
    }
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
                        info.RightAscension, info.Declination, (int)info.SideOfPier, info.PrimaryAxisRates.ToArray()), (telescope.GetDevice() as ITelescope)?.PrimaryMovingRate ?? double.NaN);
                } catch (InvalidOperationException) { return AutoRaAvailability.RateUnsupported; }
                  catch (ArgumentOutOfRangeException) { return AutoRaAvailability.RateUnsupported; }
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
                RaRotation.SelectRate(state with { Parked = false }, device.PrimaryMovingRate);
                return true;
            } catch { return false; }
        }
    }
    public async Task PrepareAutoRaAsync(CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (!await captureGate.WaitAsync(0, token)) throw new InvalidOperationException("Another alignment operation is running.");
        try {
            manualPolarisAdjustment=false;fixedPolarisSeed=null;
            var device = telescope.GetDevice() as ITelescope ?? throw new InvalidOperationException("Connect a mount before automatic alignment.");
            var mount = new RotationMount(telescope);
            var before = mount.Read();
            RaRotation.SelectRate(before with { Parked = false }, mount.SelectedRate);
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
            if(PoleMasterMode && device.TrackingEnabled) {
                device.TrackingEnabled=false;
                await Task.Delay(500,token);
                if(device.TrackingEnabled)throw new InvalidOperationException("The mount did not disable tracking before polar calibration.");
                NINA.Core.Utility.Logger.Info("Polar preparation: tracking disabled before the first reference; coordinate model remains consistent through calibration and return.");
            }
            RaRotation.SelectRate(mount.Read(), mount.SelectedRate);
        } finally { captureGate.Release(); }
    }
    public async Task RotateRaAsync(bool reverse, IProgress<double>? progress, CancellationToken token, double targetDegrees = 90) {
        if (!await captureGate.WaitAsync(0, token)) throw new InvalidOperationException("Another alignment operation is running.");
        try {
            await RotateRaCoreAsync(reverse, progress, token, targetDegrees);
        } finally { captureGate.Release(); }
    }
    public async Task ContinueRaAsync(bool reverse, IProgress<double>? progress, CancellationToken token, double targetDegrees = 40) {
        if (!await captureGate.WaitAsync(0, token)) throw new InvalidOperationException("Another alignment operation is running.");
        try { await RotateRaCoreAsync(reverse, progress, token, targetDegrees, true); }
        finally { captureGate.Release(); }
    }
    private async Task RotateRaCoreAsync(bool reverse, IProgress<double>? progress, CancellationToken token, double targetDegrees, bool continuePath = false) {
        token.ThrowIfCancellationRequested();
        var mount = new RotationMount(telescope);
        var saved = raReturn;
        raReturn = null;
        progress?.Report(0);
        var rate = mount.SelectedRate;
        var initial = mount.Read();
        var device = telescope.GetDevice() as ITelescope ?? throw new InvalidOperationException("No telescope device connected.");
        var startHourAngle = HourAngle(device);
        if (continuePath) {
            if (saved == null || saved.Reverse != reverse || !ReferenceEquals(saved.Device, device)
                || Math.Abs(initial.DecDegrees - saved.Declination) > .2
                || (saved.PierSide >= 0 && initial.PierSide != saved.PierSide)
                || Math.Abs(RaRotation.WrapDegrees(startHourAngle - saved.EndHourAngle)) > 1
                || Math.Abs(RaRotation.WrapDegrees(startHourAngle - saved.StartHourAngle)) + targetDegrees > 95)
                throw new InvalidOperationException("RA position changed between calibration intervals. Reset calibration.");
        }
        NINA.Core.Utility.Logger.Info($"Polar RA rotation: driver={mount.DriverId}, selectedRate={rate:G8} deg/s, reverse={reverse}, target={targetDegrees}, initialRA={initial.RaHours:G12} h");
        try {
            await Task.Run(() => new RaRotation().RunAsync(mount, reverse, progress, token, targetDegrees, rate, baseline => {
                NINA.Core.Utility.Logger.Info($"Polar stationary baseline after tracking off: Dec {initial.DecDegrees:F5} -> {baseline.DecDegrees:F5}; pier {initial.PierSide} -> {baseline.PierSide}; RA={baseline.RaHours:F7}");
                initial=baseline;startHourAngle=HourAngle(device);
            }, homeCoordinatesRefreshed: refreshed => {
                NINA.Core.Utility.Logger.Warning($"Polar ASI Home coordinate refresh after initial RA motion: Dec {initial.DecDegrees:F5} -> {refreshed.DecDegrees:F5}; original RA return position retained.");
                initial = initial with { DecDegrees = refreshed.DecDegrees };
            },coarseCalibration:PoleMasterMode), CancellationToken.None);
            token.ThrowIfCancellationRequested();
            raReturn = new(device, continuePath ? saved!.StartHourAngle : startHourAngle, HourAngle(device), initial.DecDegrees, initial.PierSide, reverse);
        } catch (Exception error) {
            NINA.Core.Utility.Logger.Error(error);
            throw;
        }
    }
    public Task<SolvedPolarFrame> CaptureAndSolveWithRotationAsync(double exposureSeconds, bool reverse,
        IProgress<double>? progress, CancellationToken token, double targetDegrees = 90,
        Func<SolvedPolarFrame, Task>? referenceSolved = null) =>
        CaptureAndSolveCoreAsync(exposureSeconds, token, null,
            ct => RotateRaCoreAsync(reverse, progress, ct, targetDegrees), referenceSolved);

    private sealed class RotationMount : IRaRotationMount {
        private readonly ITelescopeMediator mediator;
        private readonly ITelescope device;
        private readonly (double, double)[] rates;
        public double SelectedRate { get; }
        public string DriverId => device.Id;
        public RotationMount(ITelescopeMediator mediator) {
            this.mediator = mediator;
            device = mediator.GetDevice() as ITelescope ?? throw new InvalidOperationException("No telescope device connected.");
            rates = device.GetAxisRates(TelescopeAxes.Primary).ToArray();
            SelectedRate = device.PrimaryMovingRate;
        }
        private void CheckDevice() {
            if (!ReferenceEquals(mediator.GetDevice(), device)) throw new InvalidOperationException("Telescope device changed during rotation.");
        }
        public RaMountState Read() {
            CheckDevice();
            return new(device.Connected, device.AtPark, device.Slewing, device.AlignmentMode is AlignmentMode.Polar or AlignmentMode.GermanPolar,
                device.CanMovePrimaryAxis, device.CanSetTrackingEnabled, device.TrackingEnabled,
                device.RightAscension, device.Declination, (int)device.SideOfPier,
                rates) {
                HomePoleCoordinatesMayBeStale = Math.Abs(device.Declination) >= 85
                    && string.Equals(device.Id, "ASCOM.ASIMount.Telescope", StringComparison.OrdinalIgnoreCase)
                    && device.AtHome
            };
        }
        public void SetTracking(bool enabled) { CheckDevice(); device.TrackingEnabled = enabled; }
        // Use NINA's manual-control reversal setting, as its direction buttons do.
        public void Move(double rate) { CheckDevice(); mediator.MoveAxis(TelescopeAxes.Primary, rate); }
        // The original device is retained so emergency abort never targets a replacement mount.
        public void Abort() => device.StopSlew();
    }
}
