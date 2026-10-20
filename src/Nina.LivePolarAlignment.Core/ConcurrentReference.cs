using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nina.LivePolarAlignment.Core;

/// <summary>Runs image analysis and mount motion together, joining cleanup before returning.</summary>
public static class ConcurrentReference {
    public static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> solve,
        Func<CancellationToken, Task> rotate, CancellationToken token) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        token.ThrowIfCancellationRequested();
        async Task<T> Solve() {
            try { return await solve(linked.Token); }
            catch { linked.Cancel(); throw; }
        }
        async Task Rotate() {
            try { linked.Token.ThrowIfCancellationRequested(); await rotate(linked.Token); }
            catch { linked.Cancel(); throw; }
        }
        var analysis = Solve();
        var motion = Rotate();
        try { await Task.WhenAll(analysis, motion); }
        catch {
            // Keep a real failure visible when its sibling was cancelled as a consequence.
            var error = new Task[] { motion, analysis }.Where(t => t.Exception != null)
                .SelectMany(t => t.Exception!.Flatten().InnerExceptions)
                .FirstOrDefault(e => e is not OperationCanceledException);
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
        token.ThrowIfCancellationRequested();
        return await analysis;
    }
}
