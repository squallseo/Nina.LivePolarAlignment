using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nina.LivePolarAlignment.Core;

public sealed record RaMountState(bool Connected, bool Parked, bool Slewing, bool Equatorial,
    bool CanMove, bool CanSetTracking, bool Tracking, double RaHours, double DecDegrees, int PierSide,
    IReadOnlyList<(double Minimum, double Maximum)> Rates);

public interface IRaRotationMount {
    RaMountState Read();
    void SetTracking(bool enabled);
    void Move(double degreesPerSecond);
    void Abort();
}

/// <summary>Primary-axis motion only. Never GoTo, sync, unpark or move Dec.</summary>
public sealed class RaRotation {
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    public RaRotation(Func<TimeSpan, CancellationToken, Task>? delay = null) => this.delay = delay ?? Task.Delay;
    public static double SelectRate(RaMountState state) {
        if (!state.Connected || state.Parked || state.Slewing || !state.Equatorial || !state.CanMove
            || !state.CanSetTracking || !double.IsFinite(state.RaHours) || state.RaHours is < 0 or >= 24
            || !double.IsFinite(state.DecDegrees) || Math.Abs(state.DecDegrees) > 90)
            throw new InvalidOperationException("Connect an idle, unparked equatorial mount with primary-axis motion and tracking control. Otherwise use manual rotation.");
        var rates = state.Rates.Where(r => double.IsFinite(r.Minimum) && double.IsFinite(r.Maximum)
            && r.Minimum >= 0 && r.Maximum >= Math.Max(.1, r.Minimum) && r.Minimum <= 1)
            .Select(r => Math.Min(1, r.Maximum)).ToArray();
        if (rates.Length == 0) throw new InvalidOperationException("No supported RA speed between 0.1 and 1 degree/s. Use manual rotation.");
        return rates.Max();
    }
    public async Task RunAsync(IRaRotationMount mount, bool reverse, IProgress<double>? progress, CancellationToken token, double targetDegrees = 60) {
        if (targetDegrees is not (60 or 90)) throw new ArgumentOutOfRangeException(nameof(targetDegrees));
        token.ThrowIfCancellationRequested();
        var initial = mount.Read(); var rate = SelectRate(initial);
        bool owned = false, stopped = false;
        try {
            // Mark ownership before a driver call: a driver can move and then throw.
            owned = true;
            mount.SetTracking(false);
            if (mount.Read().Tracking) throw new InvalidOperationException("Mount did not disable tracking.");
            token.ThrowIfCancellationRequested();
            mount.Move(reverse ? -rate : rate);
            var timer = Stopwatch.StartNew();
            double previous = initial.RaHours, total = 0, lastMotion = 0;
            int polls = 0, stagnant = 0; double? direction = null;
            while (total < targetDegrees) {
                await delay(TimeSpan.FromMilliseconds(250), token); token.ThrowIfCancellationRequested();
                var current = mount.Read(); polls++;
                if (!current.Connected || current.Parked || !current.Equatorial || current.Tracking
                    || !double.IsFinite(current.RaHours) || current.RaHours is < 0 or >= 24
                    || !double.IsFinite(current.DecDegrees) || Math.Abs(current.DecDegrees - initial.DecDegrees) > .2
                    || (initial.PierSide >= 0 && current.PierSide != initial.PierSide))
                    throw new InvalidOperationException("Mount state, Dec or pier side changed during RA rotation. Reset calibration.");
                double delta = WrapDegrees((current.RaHours - previous) * 15); previous = current.RaHours;
                if (Math.Abs(delta) > 5) throw new InvalidOperationException("RA feedback jumped unexpectedly. Reset calibration.");
                if (Math.Abs(delta) > .005) {
                    if (direction.HasValue && Math.Sign(delta) != direction.Value)
                        throw new InvalidOperationException("RA motion reversed unexpectedly. Reset calibration.");
                    direction = Math.Sign(delta); total += Math.Abs(delta); stagnant = 0; lastMotion = timer.Elapsed.TotalSeconds;
                } else stagnant++;
                progress?.Report(total);
                if (stagnant >= 24 || timer.Elapsed.TotalSeconds - lastMotion > 6)
                    throw new TimeoutException("No fresh RA movement feedback for 6 seconds. Reset calibration.");
                if (polls * .25 > targetDegrees / rate + 30 || timer.Elapsed.TotalSeconds > targetDegrees / rate + 30)
                    throw new TimeoutException("RA rotation timed out. Reset calibration.");
            }
        } finally {
            if (owned) {
                try {
                    try { mount.Move(0); }
                    catch { mount.Abort(); }
                    // Verify a stopped axis with tracking still off before restoring tracking.
                    var previous = mount.Read(); int stable = 0;
                    for (int i = 0; i < 20; i++) {
                        await delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
                        var current = mount.Read();
                        if (!current.Connected) break;
                        if (!current.Slewing && !current.Tracking && double.IsFinite(current.RaHours)
                            && Math.Abs(WrapDegrees((current.RaHours - previous.RaHours) * 15)) < .02) stable++;
                        else stable = 0;
                        previous = current;
                        if (stable >= 4) { stopped = true; break; }
                    }
                    if (!stopped) { mount.Abort(); throw new InvalidOperationException("Could not verify RA stop. Check the mount and stop it from its controller."); }
                    if (initial.Tracking) mount.SetTracking(true);
                } catch (Exception cleanupError) {
                    try { mount.Abort(); } catch (Exception abortError) { throw new AggregateException("RA cleanup and emergency abort failed. Stop the mount from its controller.", cleanupError, abortError); }
                    throw new InvalidOperationException("RA cleanup failed. Check the mount before continuing.", cleanupError);
                }
            }
        }
        await delay(TimeSpan.FromSeconds(2), token);
    }
    public static double WrapDegrees(double angle) => (angle % 360 + 540) % 360 - 180;
}
