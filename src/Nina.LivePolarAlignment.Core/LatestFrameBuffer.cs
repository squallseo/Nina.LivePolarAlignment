using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Nina.LivePolarAlignment.Core;

/// <summary>A single reader owns acquisition and cleanup. Slow consumers retain only the newest frame.</summary>
public sealed class LatestFrameBuffer<T> : IAsyncDisposable {
    private readonly Channel<T> frames = Channel.CreateBounded<T>(new BoundedChannelOptions(1) {
        FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true
    });
    private readonly CancellationTokenSource stop;
    private readonly Task producer;
    private readonly object disposalGate = new();
    private Exception? failure;
    private volatile bool disposing;
    private Task? disposal;

    public LatestFrameBuffer(Func<CancellationToken, IAsyncEnumerable<T>> source, TimeSpan frameTimeout, CancellationToken token) {
        if (frameTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(frameTimeout));
        stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        producer = Task.Run(async () => {
            try {
                await using var reader = source(stop.Token).GetAsyncEnumerator(stop.Token);
                while (true) {
                    stop.Token.ThrowIfCancellationRequested();
                    stop.CancelAfter(frameTimeout);
                    bool available = await reader.MoveNextAsync().ConfigureAwait(false);
                    bool expired = stop.IsCancellationRequested;
                    stop.CancelAfter(Timeout.InfiniteTimeSpan);
                    if (expired) throw new OperationCanceledException(stop.Token);
                    if (!available) throw new InvalidOperationException("Camera stream ended unexpectedly.");
                    frames.Writer.TryWrite(reader.Current);
                }
            } catch (OperationCanceledException) when (stop.IsCancellationRequested) {
                if (!token.IsCancellationRequested && !disposing)
                    failure = new TimeoutException("No streaming frame arrived in time. Check the camera connection.");
            } catch (Exception error) { failure = error; }
            finally { frames.Writer.TryComplete(failure); }
        });
    }

    public async ValueTask<T> ReadAsync(CancellationToken token) {
        token.ThrowIfCancellationRequested();
        while (await frames.Reader.WaitToReadAsync(token).ConfigureAwait(false)) {
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            if (frames.Reader.TryRead(out var frame)) { token.ThrowIfCancellationRequested(); return frame; }
        }
        await frames.Reader.Completion.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        throw new InvalidOperationException("Camera stream stopped.");
    }

    public ValueTask DisposeAsync() {
        lock (disposalGate) return new ValueTask(disposal ??= StopAsync());
    }
    private async Task StopAsync() {
        disposing = true;
        stop.Cancel();
        // Never release camera ownership or restore settings during a native download.
        await producer.ConfigureAwait(false);
        stop.Dispose();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
