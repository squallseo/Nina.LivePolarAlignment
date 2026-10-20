using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Nina.LivePolarAlignment.UI;

/// <summary>Derives quiet text from the host text color without using its accent color.</summary>
public sealed class SupportingTextColorConverter : IMultiValueConverter {
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) {
        if (values.Length < 2 || values[0] is not Color foreground || values[1] is not Color background)
            return Binding.DoNothing;
        // Preserve deliberately dim/night-vision host palettes. For normal palettes,
        // reduce emphasis only as far as readable small-text contrast permits.
        if (Contrast(foreground, background) < 4.5) return foreground;
        for (double amount = .72; amount < 1; amount += .04) {
            var muted = Color.FromRgb(Mix(foreground.R, background.R, amount),
                Mix(foreground.G, background.G, amount), Mix(foreground.B, background.B, amount));
            if (Contrast(muted, background) >= 4.5) return muted;
        }
        return foreground;
    }

    private static byte Mix(byte foreground, byte background, double amount) =>
        (byte)Math.Round(foreground * amount + background * (1 - amount));
    private static double Luminance(Color color) {
        static double Channel(byte value) {
            double normalized = value / 255d;
            return normalized <= .04045 ? normalized / 12.92 : Math.Pow((normalized + .055) / 1.055, 2.4);
        }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }
    private static double Contrast(Color first, Color second) {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
