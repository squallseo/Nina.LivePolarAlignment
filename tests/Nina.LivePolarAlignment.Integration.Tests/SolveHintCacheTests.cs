using NINA.Astrometry;
using Nina.LivePolarAlignment.Capture;
using Xunit;

namespace Nina.LivePolarAlignment.Integration.Tests;

public sealed class SolveHintCacheTests {
    private static readonly SolveHintContext Context = new(Guid.NewGuid(), "camera", 1000, 3.76);
    private static Coordinates Position() => new(25, 89.5, Epoch.J2000, Coordinates.RAType.Degrees);

    [Fact]
    public void CacheReturnsCopiesAndResetRemovesTheHint() {
        var cache = new SolveHintCache();
        Assert.Null(cache.Get(Context));
        var original = Position(); cache.Store(Context, original);
        original.Dec = 0;
        var hint = cache.Get(Context)!;
        Assert.Equal(89.5, hint.Dec); Assert.Equal(Epoch.J2000, hint.Epoch);
        hint.Dec = 1;
        Assert.Equal(89.5, cache.Get(Context)!.Dec);
        cache.Clear(); Assert.Null(cache.Get(Context));
    }

    [Theory]
    [InlineData("profile")] [InlineData("camera")] [InlineData("focal")] [InlineData("pixel")]
    public void HintDoesNotCrossEquipmentOrProfileChanges(string change) {
        var cache = new SolveHintCache(); cache.Store(Context, Position());
        var other = change switch {
            "profile" => Context with { ProfileId = Guid.NewGuid() },
            "camera" => Context with { CameraId = "other" },
            "focal" => Context with { FocalLength = 500 },
            _ => Context with { PixelSize = 5 }
        };
        Assert.Null(cache.Get(other));
    }

    [Theory]
    [InlineData(601)] [InlineData(-1)]
    public void ExpiredOrClockReversedHintsAreNotUsed(int seconds) {
        var now = DateTimeOffset.UtcNow;
        var cache = new SolveHintCache(() => now); cache.Store(Context, Position());
        now = now.AddSeconds(seconds);
        Assert.Null(cache.Get(Context));
    }
}
