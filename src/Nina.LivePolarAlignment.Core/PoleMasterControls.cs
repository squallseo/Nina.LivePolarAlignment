namespace Nina.LivePolarAlignment.Core;

/// <summary>PoleMaster exposure presets and native gain adjustments.</summary>
public static class PoleMasterControls {
    private static readonly double[] Exposures={.05,.1,.2,.3};
    public static double NextExposure(double current,int direction) => direction>0
        ? Exposures.FirstOrDefault(v=>v>current+1e-6,current)
        : Exposures.LastOrDefault(v=>v<current-1e-6,current);
    public static int NextGain(int current,int minimum,int maximum,int direction) {
        if(minimum>maximum)throw new ArgumentOutOfRangeException(nameof(minimum));
        // The PoleMaster application's displayed gain is not a verified SDK
        // conversion. Keep native gain in single units, including below 50.
        return (int)Math.Clamp((long)current+Math.Sign(direction),minimum,maximum);
    }
}
