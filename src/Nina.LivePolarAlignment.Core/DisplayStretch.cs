using System;

namespace Nina.LivePolarAlignment.Core;

/// <summary>Same background/noise and midtone mapping as Nina.LiveFocus FocusDisplayStretch.
/// Display only: never modifies scientific samples or star centroids.</summary>
public static class DisplayStretch {
    public static byte[] CreateLinearLookup(int bitDepth) {
        if(bitDepth is <1 or >16)throw new ArgumentOutOfRangeException(nameof(bitDepth));
        double maximum=(1<<bitDepth)-1;
        var lookup=new byte[65536];
        for(int i=0;i<lookup.Length;i++)lookup[i]=(byte)Math.Round(255*Math.Min(1,i/maximum));
        return lookup;
    }
    public static byte[] CreateLookup(ushort[] pixels, double strength = 1) {
        if (pixels == null || pixels.Length == 0) throw new ArgumentException("Empty preview.");
        var sample = new double[Math.Min(pixels.Length,65536)];
        for(int i=0;i<sample.Length;i++) sample[i]=pixels[(int)((long)i*pixels.Length/sample.Length)];
        Array.Sort(sample);
        double median=sample[sample.Length/2];
        double noise=Math.Max(1,(sample[sample.Length*3/4]-sample[sample.Length/4])/1.349);
        double black=Math.Max(0,median-2.8*noise);
        double white=Math.Max(sample[(int)((sample.Length-1)*.9999)],median+20*noise);
        double range=Math.Max(1,white-black), background=Math.Clamp((median-black)/range,0,1);
        double target=.12*(double.IsFinite(strength)?Math.Clamp(strength,.25,2.5):1);
        double midtone=background==0?.5:Math.Clamp(background*(1-target)/(background+target-2*background*target),.0001,.9999);
        var lookup=new byte[65536];
        for(int i=0;i<lookup.Length;i++) {
            double x=Math.Clamp((i-black)/range,0,1);
            double y=(midtone-1)*x/((2*midtone-1)*x-midtone);
            lookup[i]=(byte)Math.Round(255*Math.Clamp(y,0,1));
        }
        return lookup;
    }
}
