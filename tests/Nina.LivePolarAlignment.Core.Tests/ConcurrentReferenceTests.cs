using Nina.LivePolarAlignment.Core;
using Xunit;

namespace Nina.LivePolarAlignment.Core.Tests;

public sealed class ConcurrentReferenceTests {
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RotationStartsBeforeAnalysisFinishesAndBothMustFinish(bool motionFinishesFirst) {
        var solve = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var motion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started = false;
        var operation = ConcurrentReference.RunAsync(_ => solve.Task, _ => { started = true; return motion.Task; }, default);
        Assert.True(started); Assert.False(operation.IsCompleted);
        if (motionFinishesFirst) motion.SetResult(); else solve.SetResult(42);
        Assert.False(operation.IsCompleted);
        if (motionFinishesFirst) solve.SetResult(42); else motion.SetResult();
        Assert.Equal(42, await operation.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task FailureCancelsSiblingAndWaitsForItsCleanup(bool solveFails) {
        var failure = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> Sibling(CancellationToken ct) {
            try { await Task.Delay(Timeout.Infinite, ct); return 0; }
            finally { cancelled.SetResult(); await cleanup.Task; }
        }
        var operation = ConcurrentReference.RunAsync<int>(solveFails ? _ => failure.Task : Sibling,
            solveFails ? async ct => { await Sibling(ct); } : async _ => { await failure.Task; }, default);
        failure.SetException(new InvalidOperationException("original failure"));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.IsCompleted);
        cleanup.SetResult();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.Equal("original failure", error.Message);
    }

    [Fact]
    public async Task UserCancellationJoinsBothWorkers() {
        using var cts = new CancellationTokenSource();
        int cleaned = 0;
        async Task<int> Worker(CancellationToken ct) {
            try { await Task.Delay(Timeout.Infinite, ct); return 1; }
            finally { Interlocked.Increment(ref cleaned); }
        }
        var operation = ConcurrentReference.RunAsync(Worker, async ct => { await Worker(ct); }, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(2, cleaned);
    }

    [Fact]
    public async Task SynchronousAnalysisFailureNeverStartsMotion() {
        bool moved = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConcurrentReference.RunAsync<int>(
            _ => throw new InvalidOperationException("bad frame"), _ => { moved = true; return Task.CompletedTask; }, default));
        Assert.False(moved);
    }
}
