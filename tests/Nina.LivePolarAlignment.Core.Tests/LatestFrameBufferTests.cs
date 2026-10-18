using System.Runtime.CompilerServices;
using Nina.LivePolarAlignment.Core;
using Xunit;

namespace Nina.LivePolarAlignment.Core.Tests;

public sealed class LatestFrameBufferTests {
    [Fact]
    public async Task SlowConsumerGetsNewestFrameInsteadOfBacklog() {
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken token) {
            yield return 1;
            await firstRead.Task.WaitAsync(token);
            yield return 2; yield return 3; yield return 4;
            published.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
        await using var buffer = new LatestFrameBuffer<int>(Source, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(1, await buffer.ReadAsync(CancellationToken.None));
        firstRead.SetResult(); await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, await buffer.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StopAwaitsInFlightDownloadBeforeDisposingEnumerator() {
        var downloading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool disposed = false;
        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken token) {
            try {
                downloading.SetResult();
                await release.Task; // Simulates a native download that ignores cancellation.
                token.ThrowIfCancellationRequested(); yield return 1;
            } finally { disposed = true; }
        }
        var buffer = new LatestFrameBuffer<int>(Source, TimeSpan.FromSeconds(5), CancellationToken.None);
        await downloading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = buffer.DisposeAsync().AsTask();
        Assert.False(stopping.IsCompleted); Assert.False(disposed);
        release.SetResult(); await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(disposed);
    }

    [Fact]
    public async Task ProducerFailureReachesConsumerAndCleanup() {
        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken token) {
            await Task.Yield();
            if (!token.IsCancellationRequested) throw new IOException("camera disconnected");
            yield break;
        }
        var buffer = new LatestFrameBuffer<int>(Source, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(async () => await buffer.ReadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(async () => await buffer.DisposeAsync());
    }

    [Fact]
    public async Task MissingFrameTimesOutAndDisposesSource() {
        bool disposed = false;
        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken token) {
            try { await Task.Delay(Timeout.Infinite, token); yield return 1; }
            finally { disposed = true; }
        }
        var buffer = new LatestFrameBuffer<int>(Source, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        await Assert.ThrowsAsync<TimeoutException>(async () => await buffer.ReadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<TimeoutException>(async () => await buffer.DisposeAsync());
        Assert.True(disposed);
    }

    [Fact]
    public async Task UserCancellationIsNotReportedAsCameraTimeout() {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken token) {
            entered.SetResult(); await Task.Delay(Timeout.Infinite, token); yield return 1;
        }
        var buffer = new LatestFrameBuffer<int>(Source, TimeSpan.FromSeconds(5), cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await buffer.ReadAsync(cancel.Token));
        await buffer.DisposeAsync();
    }
}
