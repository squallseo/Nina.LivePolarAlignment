using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nina.LivePolarAlignment.Core;

public sealed record RaMountState(bool Connected, bool Parked, bool Slewing, bool Equatorial,
    bool CanMove, bool CanSetTracking, bool Tracking, double RaHours, double DecDegrees, int PierSide,
    IReadOnlyList<(double Minimum, double Maximum)> Rates) {
    // Opt-in for the ASI driver's delayed Home coordinate refresh. Never a general
    // permission to ignore declination changes during primary-axis motion.
    public bool HomePoleCoordinatesMayBeStale { get; init; }
}

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
    public static double SelectRate(RaMountState state, double maximumRate = 1) {
        if (!double.IsFinite(maximumRate) || maximumRate < .1)
            throw new ArgumentOutOfRangeException(nameof(maximumRate));
        if (!state.Connected || state.Parked || state.Slewing || !state.Equatorial || !state.CanMove
            || !state.CanSetTracking || !double.IsFinite(state.RaHours) || state.RaHours is < 0 or >= 24
            || !double.IsFinite(state.DecDegrees) || Math.Abs(state.DecDegrees) > 90)
            throw new InvalidOperationException("Connect an idle, unparked equatorial mount with primary-axis motion and tracking control. Otherwise use manual rotation.");
        var rates = state.Rates.Where(r => double.IsFinite(r.Minimum) && double.IsFinite(r.Maximum)
            && r.Minimum >= 0 && r.Maximum >= Math.Max(.1, r.Minimum) && r.Minimum <= maximumRate)
            .Select(r => Math.Min(maximumRate, r.Maximum)).ToArray();
        if (rates.Length == 0) throw new InvalidOperationException($"No supported RA speed between 0.1 and {maximumRate} degree/s. Use manual rotation.");
        return rates.Max();
    }
    public Task RunAsync(IRaRotationMount mount, bool reverse, IProgress<double>? progress, CancellationToken token, double targetDegrees = 90, double maximumRate = 1, Action<RaMountState>? beforeMotion = null, Action<RaMountState>? homeCoordinatesRefreshed = null,bool coarseCalibration=false) {
        if (!double.IsFinite(targetDegrees) || targetDegrees < 30 || targetDegrees > 90 || targetDegrees % 10 != 0) throw new ArgumentOutOfRangeException(nameof(targetDegrees));
        return RunCoreAsync(mount, reverse, progress, token, targetDegrees, maximumRate, beforeMotion: beforeMotion, homeCoordinatesRefreshed: homeCoordinatesRefreshed,coarseCalibration:coarseCalibration);
    }
    // Return distances include feedback overshoot and Earth rotation, so are not rounded
    // to the calibration angle. The caller validates the saved start and current position.
    public Task ReturnAsync(IRaRotationMount mount, bool reverse, IProgress<double>? progress, CancellationToken token, double targetDegrees, double maximumRate, int? expectedRaDirection = null) {
        if (!double.IsFinite(targetDegrees) || targetDegrees <= 0 || targetDegrees > 100) throw new ArgumentOutOfRangeException(nameof(targetDegrees));
        if (expectedRaDirection.HasValue && expectedRaDirection != -1 && expectedRaDirection != 1) throw new ArgumentOutOfRangeException(nameof(expectedRaDirection));
        return RunCoreAsync(mount, reverse, progress, token, targetDegrees, maximumRate, expectedRaDirection);
    }
    private async Task RunCoreAsync(IRaRotationMount mount, bool reverse, IProgress<double>? progress, CancellationToken token, double targetDegrees, double maximumRate, int? expectedRaDirection = null, Action<RaMountState>? beforeMotion = null, Action<RaMountState>? homeCoordinatesRefreshed = null,bool coarseCalibration=false) {
        token.ThrowIfCancellationRequested();
        var initial = mount.Read(); var rate = SelectRate(initial, maximumRate);
        bool restoreTracking=initial.Tracking;
        double finishRate;
        try { finishRate = SelectRate(initial, Math.Min(coarseCalibration?2:.5, rate)); }
        catch (InvalidOperationException) { finishRate = rate; }
        double brakingDistance=Math.Min(targetDegrees,Math.Max(5,rate*1.5+1));
        double timeBudget = Math.Max(0, targetDegrees - brakingDistance) / rate + brakingDistance / finishRate + 30;
        bool decelerated = false;
        bool owned = false, stopped = false;
        try {
            // Mark ownership before a driver call: a driver can move and then throw.
            owned = true;
            mount.SetTracking(false);
            if (mount.Read().Tracking) throw new InvalidOperationException("Mount did not disable tracking.");
            // Drivers can refresh their coordinate model when tracking is disabled.
            // Establish the baseline while both axes are still stationary, never
            // absorb a Dec or pier-side change after the motion command.
            await delay(TimeSpan.FromMilliseconds(250),token);
            var settled=mount.Read();
            await delay(TimeSpan.FromMilliseconds(250),token);
            var fresh=mount.Read();
            SelectRate(fresh,maximumRate);
            if(fresh.Tracking || Math.Abs(fresh.DecDegrees-settled.DecDegrees)>.02 || fresh.PierSide!=settled.PierSide
                || Math.Abs(WrapDegrees((fresh.RaHours-settled.RaHours)*15))>.05)
                throw new InvalidOperationException("Mount coordinates are not stable with tracking off. No RA rotation started.");
            initial=fresh;beforeMotion?.Invoke(initial);
            token.ThrowIfCancellationRequested();
            mount.Move(reverse ? -rate : rate);
            var timer = Stopwatch.StartNew();
            double previous = initial.RaHours, total = 0, lastMotion = 0;
            int polls = 0, stagnant = 0; double? direction = expectedRaDirection;
            bool homeRefreshPending = initial.HomePoleCoordinatesMayBeStale && Math.Abs(initial.DecDegrees) >= 85;
            while (total < targetDegrees) {
                await delay(TimeSpan.FromMilliseconds(250), token); token.ThrowIfCancellationRequested();
                var current = mount.Read(); polls++;
                // Observed ASI Home reports retain an old Dec until the first
                // MoveAxis response, then report exactly the celestial pole.
                // Accept that single, early transition only; all other guards
                // (including RA feedback and pier side) still apply below.
                if (homeRefreshPending && polls <= 8 && timer.Elapsed.TotalSeconds <= 2
                    && current.Connected && !current.Parked && current.Equatorial && !current.Tracking
                    && current.PierSide == initial.PierSide
                    && Math.Sign(current.DecDegrees) == Math.Sign(initial.DecDegrees)
                    && Math.Abs(Math.Abs(current.DecDegrees) - 90) < .00001
                    && Math.Abs(current.DecDegrees - initial.DecDegrees) > .2) {
                    initial = initial with { DecDegrees = current.DecDegrees };
                    homeRefreshPending = false;
                    homeCoordinatesRefreshed?.Invoke(initial);
                }
                if (!current.Connected || current.Parked || !current.Equatorial || current.Tracking
                    || !double.IsFinite(current.RaHours) || current.RaHours is < 0 or >= 24
                    || !double.IsFinite(current.DecDegrees) || Math.Abs(current.DecDegrees - initial.DecDegrees) > .2
                    || (initial.PierSide >= 0 && current.PierSide != initial.PierSide))
                    throw new InvalidOperationException($"Mount state, Dec or pier side changed during RA rotation. "
                        + $"Connected={current.Connected}, Parked={current.Parked}, Equatorial={current.Equatorial}, Tracking={current.Tracking}; "
                        + $"Dec {initial.DecDegrees:F5} -> {current.DecDegrees:F5}; pier {initial.PierSide} -> {current.PierSide}; RA {initial.RaHours:F5} -> {current.RaHours:F5}. Reset calibration.");
                double delta = WrapDegrees((current.RaHours - previous) * 15); previous = current.RaHours;
                double feedbackInterval = Math.Max(timer.Elapsed.TotalSeconds - lastMotion, (stagnant + 1) * .25);
                double commandedRate = decelerated ? finishRate : rate;
                // ASCOM coordinates can arrive in bursts: two nearby reads may
                // straddle a one-second driver refresh even if the preceding read
                // contained a small sidereal/model update. Do not assume every
                // nonzero delta was a complete fresh axis position. Bound both
                // the individual burst and total travel against commanded speed.
                double burstLimit=Math.Max(5,rate*(feedbackInterval+1)*1.25+.25);
                double elapsed=Math.Max(timer.Elapsed.TotalSeconds,polls*.25);
                double travelLimit=rate*(elapsed+1)*1.25+.5;
                if (Math.Abs(delta)>burstLimit || total+Math.Abs(delta)>travelLimit)
                    throw new InvalidOperationException($"RA feedback jumped unexpectedly. Reset calibration. Delta={delta:F4} deg; interval={feedbackInterval:F3}s; burstLimit={burstLimit:F3} deg; total={total:F3} deg; elapsed={elapsed:F3}s; rate={commandedRate:G8} deg/s; RA={current.RaHours:G12}h.");
                if (Math.Abs(delta) > .005) {
                    if (direction.HasValue && Math.Sign(delta) != direction.Value)
                        throw new InvalidOperationException("RA motion reversed unexpectedly. Reset calibration.");
                    direction = Math.Sign(delta); total += Math.Abs(delta); stagnant = 0; lastMotion = timer.Elapsed.TotalSeconds;
                } else stagnant++;
                progress?.Report(total);
                if (!decelerated && total < targetDegrees && targetDegrees - total <= brakingDistance && finishRate < rate) {
                    mount.Move(reverse ? -finishRate : finishRate);
                    decelerated = true;
                }
                if (stagnant >= 24 || timer.Elapsed.TotalSeconds - lastMotion > 6)
                    throw new TimeoutException($"No fresh RA movement feedback for 6 seconds. Reset calibration. Requested rate={rate:G8} deg/s; RA={current.RaHours:G12} h; measured rotation={total:F3} deg; elapsed={timer.Elapsed.TotalSeconds:F1} s.");
                if (polls * .25 > timeBudget || timer.Elapsed.TotalSeconds > timeBudget)
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
                    if (restoreTracking) mount.SetTracking(true);
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
