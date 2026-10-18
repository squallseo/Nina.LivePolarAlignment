using Nina.LivePolarAlignment.Core;
using Xunit;

namespace Nina.LivePolarAlignment.Core.Tests;

public sealed class RaRotationTests {
    private sealed class Mount : IRaRotationMount {
        public RaMountState State = new(true, false, false, true, true, true, true, 23, 80, 0, new[] { (.1, 2d) });
        public List<double> Commands = new();
        public double Rate;
        public bool Freeze, FailMove, FailStop, IgnoreStop, DeclinationDrift, Disconnect, AbortCalled;
        public CancellationTokenSource? Cancel;
        public RaMountState Read() {
            if (Rate != 0 && !Freeze) {
                State = State with { RaHours = ((State.RaHours + Rate * .25 / 15) % 24 + 24) % 24,
                    DecDegrees = State.DecDegrees + (DeclinationDrift ? .3 : 0), Connected = !Disconnect };
                if (Cancel != null) Cancel.Cancel();
            }
            return State;
        }
        public void SetTracking(bool enabled) => State = State with { Tracking = enabled };
        public void Move(double rate) {
            Commands.Add(rate);
            if (rate == 0 && FailStop) throw new InvalidOperationException("stop failed");
            if (!(rate == 0 && IgnoreStop)) Rate = rate;
            if (rate != 0 && FailMove) throw new InvalidOperationException("moved then threw");
        }
        public void Abort() { AbortCalled = true; Rate = 0; }
    }
    private static RaRotation Controller() => new((_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });

    [Theory]
    [InlineData(45)] [InlineData(double.NaN)]
    public async Task InvalidAngleDoesNotChangeMount(double angle) {
        var mount = new Mount();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Controller().RunAsync(mount, false, null, default, angle));
        Assert.Empty(mount.Commands); Assert.True(mount.State.Tracking);
    }

    [Theory]
    [InlineData(false, 60)] [InlineData(true, 60)] [InlineData(false, 90)] [InlineData(true, 90)]
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
