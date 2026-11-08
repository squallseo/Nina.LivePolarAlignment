using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nina.LivePolarAlignment.Capture;

public sealed partial class NinaPolarAlignmentCaptureService {
    public Task RotateRaWithPreviewAsync(bool reverse,bool continuation,double exposureSeconds,
        IProgress<double>? progress,CancellationToken token,double targetDegrees=40) =>
        WithRotationPreviewAsync(exposureSeconds,ct=>RotateRaCoreAsync(reverse,progress,ct,targetDegrees,continuation),token);
    public Task ReturnRaWithPreviewAsync(double exposureSeconds,IProgress<double>? progress,CancellationToken token) =>
        WithRotationPreviewAsync(exposureSeconds,ct=>ReturnRaToStartCoreAsync(progress,ct,true),token);

    private async Task WithRotationPreviewAsync(double exposureSeconds,Func<CancellationToken,Task> motion,CancellationToken token) {
        if(!await captureGate.WaitAsync(0,token))throw new InvalidOperationException("Another alignment operation is running.");
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(token);
        var firstFrame=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? preview=null;
        try {
            preview=ReceivePreview();
            var started=await Task.WhenAny(firstFrame.Task,preview).WaitAsync(token);
            if(started==preview) {await preview;throw new InvalidOperationException("Rotation preview stopped before motion.");}
            token.ThrowIfCancellationRequested();
            await motion(stop.Token);
        } finally {
            stop.Cancel();
            try {
                if(preview!=null) {
                    try {await preview;}
                    catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
                }
            } finally {captureGate.Release();}
        }
        async Task ReceivePreview() {
            try {
                await foreach(var _ in StreamCoreAsync(exposureSeconds,true,stop.Token,true,true,()=>firstFrame.TrySetResult())) { }
            } catch {stop.Cancel();throw;}
        }
    }
}
