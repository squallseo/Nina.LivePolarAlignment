using Nina.LivePolarAlignment.Core;
using Xunit;

namespace Nina.LivePolarAlignment.Core.Tests;

public sealed class RaRotationTests {
    private sealed class Mount : IRaRotationMount {
        public RaMountState State = new(true, false, false, true, true, true, true, 23, 80, 0, new[] { (.1, 2d) });
        public List<double> Commands = new();
        public double Rate;
        public int FeedbackEvery = 1;
        private int feedbackReads;
        public int NormalizeOnMotionRead;
        public double NormalizedDec = 90;
        public bool ChangePierOnNormalization;
        public bool BurstyAscomFeedback,ImpossibleJump;
        private int motionReads;
        public bool Freeze, FailMove, FailStop, IgnoreStop, DeclinationDrift, Disconnect, AbortCalled,NormalizeOnTrackingOff;
        public CancellationTokenSource? Cancel;
        public RaMountState Read() {
            if(Rate!=0 && BurstyAscomFeedback) {
                motionReads++;
                // Tiny model/sidereal updates between one-second axis refreshes.
                double delta=motionReads%4==0?Rate*.25*4:.01;
                if(ImpossibleJump && motionReads==4)delta=60;
                State=State with{RaHours=((State.RaHours+delta/15)%24+24)%24};return State;
            }
            if (Rate != 0 && !Freeze && ++feedbackReads % FeedbackEvery == 0) {
                State = State with { RaHours = ((State.RaHours + Rate * .25 * FeedbackEvery / 15) % 24 + 24) % 24,
                    DecDegrees = State.DecDegrees + (DeclinationDrift ? .3 : 0), Connected = !Disconnect };
                if (feedbackReads == NormalizeOnMotionRead)
                    State = State with { DecDegrees = NormalizedDec, PierSide = ChangePierOnNormalization ? 1 : State.PierSide };
                if (Cancel != null) Cancel.Cancel();
            }
            return State;
        }
        public void SetTracking(bool enabled) => State = State with { Tracking = enabled,DecDegrees=!enabled && NormalizeOnTrackingOff?90:State.DecDegrees };
        public void Move(double rate) {
            Commands.Add(rate);
            if (rate == 0 && FailStop) throw new InvalidOperationException("stop failed");
            if (!(rate == 0 && IgnoreStop)) Rate = rate;
            if (rate != 0 && FailMove) throw new InvalidOperationException("moved then threw");
        }
        public void Abort() { AbortCalled = true; Rate = 0; }
    }
    private static RaRotation Controller() => new((_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
    [Theory][InlineData(false)][InlineData(true)]
    public async Task PoleMasterCalibrationHasShorterTailButReturnKeepsFineBraking(bool reverse) {
        var mount=new Mount{FeedbackEvery=4};mount.State=mount.State with{Rates=new[]{(.1,6d)}};
        await Controller().RunAsync(mount,reverse,null,default,40,6,coarseCalibration:true);
        await Controller().RunAsync(mount,reverse,null,default,40,6,coarseCalibration:true);
        Assert.Contains(reverse?-2:2,mount.Commands);
        Assert.DoesNotContain(reverse?-.5:.5,mount.Commands);
        double distance=Math.Abs(RaRotation.WrapDegrees((mount.State.RaHours-23)*15));
        Assert.InRange(distance,80-1e-9,90);
        await Controller().ReturnAsync(mount,!reverse,null,default,distance,6);
        Assert.Contains(reverse?.5:-.5,mount.Commands);
        Assert.InRange(Math.Abs(RaRotation.WrapDegrees((mount.State.RaHours-23)*15)),0,.51);
        Assert.Equal(0,mount.Rate);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SmallModelUpdatesDoNotMakeOneSecondAxisBurstsLookImpossible(bool impossibleJump) {
        var mount=new Mount{BurstyAscomFeedback=true,ImpossibleJump=impossibleJump};
        mount.State=mount.State with{Rates=new[]{(6d,6d)}};
        if(impossibleJump)await Assert.ThrowsAsync<InvalidOperationException>(()=>Controller().RunAsync(mount,false,null,default,80,6));
        else await Controller().RunAsync(mount,false,null,default,80,6);
        Assert.Equal(0,mount.Rate);
    }
    [Theory]
    [InlineData(1)] [InlineData(-1)]
    public async Task AsiHomeDelayedPoleRefreshCompletesBothIntervalsAndReturn(int hemisphere) {
        var mount = new Mount { NormalizeOnMotionRead = 2, NormalizedDec = hemisphere * 90 };
        mount.State = mount.State with { DecDegrees = hemisphere * 86.52472, HomePoleCoordinatesMayBeStale = true };
        int refreshed = 0;
        await Controller().RunAsync(mount, true, null, default, 40, 1,
            homeCoordinatesRefreshed: state => { refreshed++; Assert.Equal(hemisphere * 90, state.DecDegrees); });
        await Controller().RunAsync(mount, true, null, default, 40, 1);
        var distance = Math.Abs(RaRotation.WrapDegrees((mount.State.RaHours - 23) * 15));
        await Controller().ReturnAsync(mount, false, null, default, distance, 1);
        Assert.Equal(1, refreshed); Assert.Equal(0, mount.Rate);
        Assert.InRange(Math.Abs(RaRotation.WrapDegrees((mount.State.RaHours - 23) * 15)), 0, .13);
    }
    [Theory]
    [InlineData(false, 2, 90, false)] // Other drivers / no confirmed Home.
    [InlineData(true, 9, 90, false)] // A later change during rotation.
    [InlineData(true, 2, 89, false)] // A change that is not the exact pole refresh.
    [InlineData(true, 2, -90, false)] // Opposite hemisphere.
    [InlineData(true, 2, 90, true)] // Pier side must never be absorbed.
    public async Task DelayedHomeRefreshDoesNotHideOtherDeclinationOrPierChanges(bool allowed, int atRead, double dec, bool pier) {
        var mount = new Mount { NormalizeOnMotionRead = atRead, NormalizedDec = dec, ChangePierOnNormalization = pier };
        mount.State = mount.State with { DecDegrees = 86.52472, HomePoleCoordinatesMayBeStale = allowed };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Controller().RunAsync(mount, true, null, default, 40));
        Assert.Equal(0, mount.Rate);
    }
    [Fact]
    public async Task RealDeclinationDriftStillStopsAfterAcceptedHomeRefresh() {
        var mount = new Mount { NormalizeOnMotionRead = 1 };
        mount.State = mount.State with { DecDegrees = 86.52472, HomePoleCoordinatesMayBeStale = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Controller().RunAsync(mount, true, null, default, 40, 1,
            homeCoordinatesRefreshed: _ => mount.DeclinationDrift = true));
        Assert.Equal(0, mount.Rate);
    }
    [Fact] public async Task CoordinateRefreshBeforeMotionUsesFreshStationaryBaseline() {
        var mount=new Mount{NormalizeOnTrackingOff=true};mount.State=mount.State with{DecDegrees=86.52472};
        RaMountState? baseline=null;
        await Controller().RunAsync(mount,false,null,default,40,1,s=>baseline=s);
        Assert.NotNull(baseline);Assert.Equal(90,baseline.DecDegrees);Assert.Equal(0,mount.Rate);Assert.True(mount.State.Tracking);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FractionalReturnReversesTheCompletedPathAndRestoresTracking(bool reverse) {
        var mount = new Mount();
        await Controller().RunAsync(mount, reverse, null, default, 90);
        var distance = Math.Abs(RaRotation.WrapDegrees((mount.State.RaHours - 23) * 15));
        int returnCommand = mount.Commands.Count;
        await Controller().ReturnAsync(mount, !reverse, null, default, distance, 1);
        Assert.Equal(reverse ? 1 : -1, mount.Commands[returnCommand]);
        Assert.InRange(Math.Abs(RaRotation.WrapDegrees((mount.State.RaHours - 23) * 15)), 0, .13);
        Assert.Equal(0, mount.Rate); Assert.True(mount.State.Tracking);
    }

    [Fact]
    public async Task CancelledReturnStopsWithoutContinuingThePath() {
        using var cancel = new CancellationTokenSource();
        var mount = new Mount { Cancel = cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Controller().ReturnAsync(mount, true, null, cancel.Token, 90.125, 1));
        Assert.Equal(0, mount.Rate); Assert.True(mount.State.Tracking);
    }

    [Fact]
    public async Task ReturnInWrongFeedbackDirectionStopsImmediately() {
        var mount = new Mount();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Controller().ReturnAsync(mount, false, null, default, 90.125, 1, -1));
        Assert.Equal(new[] { 1d, 0d }, mount.Commands);
        Assert.Equal(0, mount.Rate); Assert.True(mount.State.Tracking);
        Assert.InRange(Math.Abs(RaRotation.WrapDegrees((mount.State.RaHours - 23) * 15)), 0, .26);
    }

    [Theory]
    [InlineData(false, 0.5)] [InlineData(true, 0.5)]
    [InlineData(false, 4)] [InlineData(true, 4)]
    [InlineData(false, 8)] [InlineData(true, 8)]
    public async Task ConfiguredRateRotationStopsAndRestoresTracking(bool reverse, double configuredRate) {
        var mount = new Mount();
        mount.State = mount.State with { Rates = new[] { (0d, 8d) } };
        Assert.Equal(1, RaRotation.SelectRate(mount.State));
        await Controller().RunAsync(mount, reverse, null, default, 90, configuredRate);
        Assert.Equal(reverse ? -configuredRate : configuredRate, mount.Commands[0]);
        Assert.Contains(reverse ? -.5 : .5, mount.Commands);
        Assert.InRange(Math.Abs(RaRotation.WrapDegrees((mount.State.RaHours - 23) * 15)), 89.99, 90.13);
        Assert.Equal(0, mount.Rate); Assert.True(mount.State.Tracking);
    }

    [Fact]
    public async Task FastSimulatorCancellationStillStops() {
        using var cancel = new CancellationTokenSource();
        var mount = new Mount { Cancel = cancel };
        mount.State = mount.State with { Rates = new[] { (0d, 8d) } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Controller().RunAsync(mount, false, null, cancel.Token, 90, 4));
        Assert.Equal(4, mount.Commands[0]); Assert.Equal(0, mount.Rate); Assert.True(mount.State.Tracking);
    }

    [Theory]
    [InlineData(45)] [InlineData(65)] [InlineData(100)] [InlineData(double.NaN)]
    public async Task InvalidAngleDoesNotChangeMount(double angle) {
        var mount = new Mount();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Controller().RunAsync(mount, false, null, default, angle));
        Assert.Empty(mount.Commands); Assert.True(mount.State.Tracking);
    }

    [Theory]
    [InlineData(false, 30)] [InlineData(true, 40)] [InlineData(false, 60)] [InlineData(true, 60)] [InlineData(false, 70)] [InlineData(true, 70)] [InlineData(false, 80)] [InlineData(true, 80)] [InlineData(false, 90)] [InlineData(true, 90)]
    public async Task RotatesAcrossRaWrapAndStopsBeforeRestoringTracking(bool reverse, double targetDegrees) {
        var mount = new Mount();
        await Controller().RunAsync(mount, reverse, null, default, targetDegrees);
        Assert.InRange(Math.Abs(RaRotation.WrapDegrees((mount.State.RaHours - 23) * 15)), targetDegrees - .01, targetDegrees + .26);
        Assert.Equal(reverse ? -1 : 1, mount.Commands[0]); Assert.Equal(0, mount.Commands[^1]);
        Assert.Equal(0, mount.Rate); Assert.True(mount.State.Tracking);
        Assert.Equal(80, mount.State.DecDegrees);
    }
    [Fact]
    public async Task CancellationStopsAndRestoresTracking() {
        using var cancel = new CancellationTokenSource(); var mount = new Mount { Cancel = cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Controller().RunAsync(mount, false, null, cancel.Token));
        Assert.Equal(0, mount.Rate); Assert.True(mount.State.Tracking);
    }
    [Fact]
    public async Task CancelBeforeStartingNeverCommandsMount() {
        var mount = new Mount(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Controller().RunAsync(mount, false, null, cancel.Token));
        Assert.Empty(mount.Commands);
    }
    [Fact]
    public async Task OneSecondTelemetryAtEightDegreesPerSecondIsNotAFalseJump() {
        var mount=new Mount { FeedbackEvery=4 };mount.State=mount.State with {Rates=new[]{(8d,8d)}};
        await Controller().RunAsync(mount,false,null,default,80,8);
        Assert.Equal(0,mount.Rate);Assert.True(mount.State.Tracking);
    }
    [Fact]
    public async Task SlowFinalRateGetsItsOwnTimeBudget() {
        var mount=new Mount();mount.State=mount.State with {Rates=new[]{(.1,.1),(6d,6d)}};
        await Controller().RunAsync(mount,false,null,default,80,6);
        Assert.Contains(.1,mount.Commands);Assert.Equal(0,mount.Rate);
    }
    [Fact]
    public async Task MissingMotionFeedbackTimesOutAndStops() {
        var mount = new Mount { Freeze = true };
        await Assert.ThrowsAsync<TimeoutException>(() => Controller().RunAsync(mount, false, null, default));
        Assert.Equal(0, mount.Rate); Assert.True(mount.State.Tracking);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task UnsafeStateOrDriverFailureStopsTheAxis(bool decDrift) {
        var mount = new Mount { DeclinationDrift = decDrift, FailMove = !decDrift };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Controller().RunAsync(mount, false, null, default));
        Assert.Equal(0, mount.Rate);
    }
    [Fact]
    public async Task ZeroRateFailureUsesAbort() {
        var mount = new Mount { FailStop = true };
        await Controller().RunAsync(mount, false, null, default);
        Assert.True(mount.AbortCalled); Assert.Equal(0, mount.Rate);
    }
    [Fact]
    public async Task UnconfirmedStopAbortsWithoutRestoringTracking() {
        var mount = new Mount { IgnoreStop = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Controller().RunAsync(mount, false, null, default));
        Assert.True(mount.AbortCalled); Assert.False(mount.State.Tracking);
    }
    [Fact]
    public async Task InitiallyDisabledTrackingRemainsDisabled() {
        var mount = new Mount(); mount.State = mount.State with { Tracking = false };
        await Controller().RunAsync(mount, false, null, default);
        Assert.False(mount.State.Tracking);
    }
    [Fact]
    public async Task DisconnectionDoesNotRestoreTracking() {
        var mount = new Mount { Disconnect = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Controller().RunAsync(mount, false, null, default));
        Assert.Equal(0, mount.Rate); Assert.False(mount.State.Tracking);
    }
    [Theory]
    [InlineData("parked")] [InlineData("altaz")] [InlineData("busy")] [InlineData("disconnected")]
    [InlineData("unsupported")] [InlineData("rate")] [InlineData("tracking")]
    public async Task PreflightRejectsUnsupportedMountWithoutCommands(string issue) {
        var mount = new Mount();
        mount.State = issue switch {
            "parked" => mount.State with { Parked = true }, "altaz" => mount.State with { Equatorial = false },
            "busy" => mount.State with { Slewing = true }, "disconnected" => mount.State with { Connected = false },
            "unsupported" => mount.State with { CanMove = false }, "tracking" => mount.State with { CanSetTracking = false },
            _ => mount.State with { Rates = new[] { (2d, 4d) } }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Controller().RunAsync(mount, false, null, default));
        Assert.Empty(mount.Commands);
    }
}
