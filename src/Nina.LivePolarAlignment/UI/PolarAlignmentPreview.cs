using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace Nina.LivePolarAlignment.UI;

public sealed partial class PolarAlignmentDockableVM {
    private CancellationTokenSource? poleMasterPreviewCancellation;
    private Task poleMasterPreviewTask=Task.CompletedTask;
    private bool previewAttempted, previewCameraWasConnected, previewSuspended;
    private bool poleMasterPreviewing;
    private bool previewManuallyPaused;
    private AsyncRelayCommand? togglePreviewLoopCommand;
    public AsyncRelayCommand TogglePreviewLoopCommand => togglePreviewLoopCommand ??= new(TogglePreviewLoopAsync,
        () => ShowPreviewLoop && CameraConnected && !liveBusy && !fieldSaving && !fieldDisposed);
    public bool ShowPreviewLoop => PoleMasterMode && calibrationSamples == 0;
    public bool IsPreviewLoopActive => poleMasterPreviewCancellation is { IsCancellationRequested: false };
    public string PreviewLoopLabel => UiText.Get(IsPreviewLoopActive ? "PreviewLoopStop" : "PreviewLoopStart");
    private async Task TogglePreviewLoopAsync() {
        if (IsPreviewLoopActive) {
            previewManuallyPaused = true;
            try { await StopPoleMasterPreviewAsync(); }
            catch (Exception error) { poleMasterPreviewFailure = error; }
        } else {
            previewManuallyPaused = false;
            UpdatePoleMasterPreview(true);
        }
        NotifyPreview();
    }
    private void NotifyPreviewLoop() {
        RaisePropertyChanged(nameof(ShowPreviewLoop)); RaisePropertyChanged(nameof(IsPreviewLoopActive));
        RaisePropertyChanged(nameof(PreviewLoopLabel)); togglePreviewLoopCommand?.NotifyCanExecuteChanged();
    }
    private Exception? poleMasterPreviewFailure;
    private string poleMasterPreviewStatusKey="PolePreviewOff";
    public bool IsPoleMasterPreviewing => poleMasterPreviewing;
    public string PoleMasterPreviewStatus => poleMasterPreviewFailure is { } error
        ? UiText.Format("PolePreviewFailed",error.Message) : UiText.Get(poleMasterPreviewStatusKey);

    private void UpdatePoleMasterPreview(bool restart=false) {
        RefreshCameraMethod();
        RefreshInitialHomeStatus();
        bool connected=CameraConnected;
        if(connected!=previewCameraWasConnected) { previewCameraWasConnected=connected;previewAttempted=false;if(connected){poleMasterPreviewFailure=null;} }
        if(!PoleMasterMode || !CanUseSelectedMethod || !connected || fieldDisposed || previewSuspended || previewManuallyPaused || IndependentDriftEnabled || liveBusy) {
            poleMasterPreviewCancellation?.Cancel();
            if(PoleMasterMode && !connected) {poleMasterPreviewStatusKey="PolePreviewWaiting";NotifyPreview();}
            return;
        }
        if(calibrationSamples!=0 || (!restart && previewAttempted))return;
        previewAttempted=true;
        poleMasterPreviewCancellation?.Cancel();
        var source=new CancellationTokenSource();
        var previous=poleMasterPreviewTask;
        poleMasterPreviewCancellation=source;
        poleMasterPreviewTask=RunPoleMasterPreviewAsync(previous,source);
        NotifyPreviewLoop();
    }
    private async Task RunPoleMasterPreviewAsync(Task previous,CancellationTokenSource source) {
        try {
            // Exactly one camera owner: drain the preceding stream before any replacement.
            await previous;
            source.Token.ThrowIfCancellationRequested();
            if(!PoleMasterMode || liveBusy || fieldDisposed)return;
            captureService.PoleMasterMode=true;
            poleMasterPreviewFailure=null;poleMasterPreviewing=true;
            poleMasterPreviewStatusKey="PolePreviewStarting";NotifyPreview();
            await captureService.RunPreviewAsync(StreamingExposureSeconds,source.Token);
            if(!source.IsCancellationRequested)throw new InvalidOperationException(UiText.Get("PolePreviewEnded"));
        } catch(OperationCanceledException) when(source.IsCancellationRequested) { }
          catch(Exception error) {
            if(ReferenceEquals(poleMasterPreviewCancellation,source))poleMasterPreviewFailure=error;
        } finally {
            if(ReferenceEquals(poleMasterPreviewCancellation,source)) {
                poleMasterPreviewCancellation=null;poleMasterPreviewing=false;
                poleMasterPreviewStatusKey="PolePreviewOff";NotifyPreview();
            }
            source.Dispose();
        }
    }
    private async Task StopPoleMasterPreviewAsync() {
        poleMasterPreviewCancellation?.Cancel();
        await poleMasterPreviewTask;
        if(poleMasterPreviewFailure is Capture.StreamingCleanupException error)throw error;
    }
    private void NotifyPreview() {
        NotifyPreviewLoop();
        RaisePropertyChanged(nameof(IsPoleMasterPreviewing));RaisePropertyChanged(nameof(PoleMasterPreviewStatus));
        NotifyWorkflow();
    }

    private double previewStretchStrength=1;
    private bool previewStretchEnabled=true;
    public bool PreviewStretchEnabled {
        get=>previewStretchEnabled;
        set {if(value==previewStretchEnabled)return;previewStretchEnabled=value;captureService.PreviewStretchEnabled=value;
            stretchVersion++;RaisePropertyChanged();RaisePropertyChanged(nameof(PreviewStretchText));
            if(!liveBusy && !poleMasterPreviewing)_=RefreshDisplayStretchAsync();}
    }
    private int stretchVersion;
    private bool restretchRunning;
    public double PreviewStretchStrength {
        get=>previewStretchStrength;
        set {
            if(!double.IsFinite(value))return;
            value=Math.Clamp(value,.25,2.5);
            if(Math.Abs(value-previewStretchStrength)<.001)return;
            previewStretchStrength=value;captureService.PreviewStretchStrength=value;stretchVersion++;
            RaisePropertyChanged();RaisePropertyChanged(nameof(PreviewStretchText));
            if(!liveBusy && !poleMasterPreviewing)_=RefreshDisplayStretchAsync();
        }
    }
    public string PreviewStretchText=>PreviewStretchEnabled?$"{PreviewStretchStrength:0.00}×":UiText.Get("StretchOff");
    private RelayCommand? resetPreviewStretchCommand;
    public RelayCommand ResetPreviewStretchCommand=>resetPreviewStretchCommand??=new(()=>PreviewStretchStrength=1);
    private async Task RefreshDisplayStretchAsync() {
        if(holdingStreamMeasurement)return;
        if(restretchRunning)return;
        restretchRunning=true;
        try {
            int version;
            do {
                version=stretchVersion;var original=livePreview;
                var rendered=await captureService.RefreshPreviewAsync(CancellationToken.None);
                if(!fieldDisposed && version==stretchVersion && ReferenceEquals(original,livePreview) && rendered!=null) {
                    livePreview=rendered;NotifyLive();
                }
            } while(!fieldDisposed && !liveBusy && !poleMasterPreviewing && version!=stretchVersion);
        } catch(Exception error) { NINA.Core.Utility.Logger.Warning("Polar display stretch: "+error.Message); }
        finally {restretchRunning=false;}
    }
}
