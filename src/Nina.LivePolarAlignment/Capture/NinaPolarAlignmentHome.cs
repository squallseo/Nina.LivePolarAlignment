using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Equipment.Interfaces;
namespace Nina.LivePolarAlignment.Capture;
public sealed partial class NinaPolarAlignmentCaptureService {
    public bool CanHomeForRestart => telescope.GetDevice() is ITelescope {Connected:true,CanFindHome:true};
    public bool MountAtHome {
        get { try { return telescope.GetDevice() is ITelescope { Connected:true, Slewing:false, AtHome:true }; }
            catch { return false; } }
    }
    public async Task HomeForRestartAsync(CancellationToken token) {
        if(!await captureGate.WaitAsync(0,token))throw new InvalidOperationException("Stop camera capture before homing.");
        ITelescope? device=null;bool requested=false;
        try {
            token.ThrowIfCancellationRequested();
            device=telescope.GetDevice() as ITelescope ?? throw new InvalidOperationException("Connect the mount before homing.");
            if(!device.Connected || !device.CanFindHome)throw new InvalidOperationException("This mount does not support FindHome.");
            if(device.Slewing)throw new InvalidOperationException("Stop the mount before homing.");
            if(device.AtPark && (!device.CanUnpark || !await telescope.UnparkTelescope(SilentProgress.Instance,token)))
                throw new InvalidOperationException("Unpark the mount before homing.");
            token.ThrowIfCancellationRequested();requested=true;
            if(device.CanSetTrackingEnabled)device.TrackingEnabled=false;
            NINA.Core.Utility.Logger.Info("Polar restart: explicit mount FindHome requested; RA and Dec may move.");
            var found=await telescope.FindHome(SilentProgress.Instance,token);
            token.ThrowIfCancellationRequested();
            if(!found)throw new InvalidOperationException("FindHome did not complete.");
            var deadline=DateTime.UtcNow.AddSeconds(10);int homeSamples=0;
            while(homeSamples<2) {
                token.ThrowIfCancellationRequested();
                if(!ReferenceEquals(device,telescope.GetDevice()) || !device.Connected)
                    throw new InvalidOperationException("Mount disconnected or changed during Home confirmation.");
                if(device.AtHome && !device.Slewing)homeSamples++;else homeSamples=0;
                if(homeSamples>=2)break;
                if(DateTime.UtcNow>=deadline)throw new InvalidOperationException($"The mount did not confirm a stopped Home position. AtHome={device.AtHome}; Slewing={device.Slewing}.");
                await Task.Delay(250,token);
            }
            ResetSolveHint();ResetRaReturn();
            NINA.Core.Utility.Logger.Info("Polar restart: mount Home confirmed; awaiting a new Polaris click.");
        } catch {
            if(requested && device!=null)device.StopSlew();
            throw;
        } finally {captureGate.Release();}
    }
}
