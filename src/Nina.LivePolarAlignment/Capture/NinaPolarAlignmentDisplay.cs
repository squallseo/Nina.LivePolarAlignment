using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Nina.LivePolarAlignment.Capture;

public sealed partial class NinaPolarAlignmentCaptureService {
    private volatile QuickLookPreview.Prepared? lastPreparedPreview;
    private double previewStretchStrength=1;
    public bool PreviewStretchEnabled {get;set;}=true;
    public double PreviewStretchStrength {
        get => Volatile.Read(ref previewStretchStrength);
        set { if(double.IsFinite(value)) Volatile.Write(ref previewStretchStrength,Math.Clamp(value,.25,2.5)); }
    }
    public Task<BitmapSource?> RefreshPreviewAsync(CancellationToken token) {
        var prepared=lastPreparedPreview; double strength=PreviewStretchStrength;
        bool enabled=PreviewStretchEnabled;
        return Task.Run<BitmapSource?>(()=>prepared?.Render(strength,token,enabled),token);
    }
}
