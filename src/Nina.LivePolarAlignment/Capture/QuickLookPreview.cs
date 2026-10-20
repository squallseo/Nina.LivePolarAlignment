using System;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NINA.Image.Interfaces;

namespace Nina.LivePolarAlignment.Capture;

/// <summary>Small grayscale display only; solving and evidence always use unchanged full-resolution pixels.</summary>
public static class QuickLookPreview {
    public sealed class Prepared {
        public BitmapSource Image { get; }
        private readonly ushort[] source;
        private readonly byte[] lookup;
        private readonly int width, height;
        internal Prepared(BitmapSource image, ushort[] source, byte[] lookup, int width, int height) {
            Image = image; this.source = source; this.lookup = lookup; this.width = width; this.height = height;
        }
        public BitmapSource CreateFullResolution(CancellationToken token) {
            if (Image.PixelWidth == width && Image.PixelHeight == height) return Image;
            var pixels = new byte[source.Length];
            for (int y = 0; y < height; y++) {
                token.ThrowIfCancellationRequested();
                int end = (y + 1) * width;
                for (int i = y * width; i < end; i++) pixels[i] = lookup[source[i]];
            }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
            bitmap.Freeze();
            return bitmap;
        }
    }
    public static Prepared Prepare(IImageData image, CancellationToken token) {
        int sourceWidth = image.Properties.Width, sourceHeight = image.Properties.Height;
        var source = image.Data.FlatArray;
        if (sourceWidth <= 0 || sourceHeight <= 0 || source.LongLength != (long)sourceWidth * sourceHeight)
            throw new InvalidOperationException("Invalid image dimensions for preview.");
        double scale = Math.Min(1, Math.Min(1600d / sourceWidth, 1200d / sourceHeight));
        int width = Math.Max(1, (int)(sourceWidth * scale)), height = Math.Max(1, (int)(sourceHeight * scale));
        var samples = new ushort[width * height];
        var histogram = new int[4096];
        for (int y = 0; y < height; y++) {
            token.ThrowIfCancellationRequested();
            int firstY = y * sourceHeight / height, endY = (y + 1) * sourceHeight / height;
            for (int x = 0; x < width; x++) {
                int firstX = x * sourceWidth / width, endX = (x + 1) * sourceWidth / width;
                ushort peak = 0;
                // Retain small stars when reducing a full-frame sensor to display size.
                for (int sy = firstY; sy < endY; sy++) {
                    int offset = sy * sourceWidth;
                    for (int sx = firstX; sx < endX; sx++) if (source[offset + sx] > peak) peak = source[offset + sx];
                }
                samples[y * width + x] = peak;
                histogram[peak >> 4]++;
            }
        }
        int Quantile(double fraction) {
            int total = 0, target = Math.Max(1, (int)(samples.Length * fraction));
            for (int i = 0; i < histogram.Length; i++) { total += histogram[i]; if (total >= target) return i * 16; }
            return ushort.MaxValue;
        }
        int low = Quantile(.005), median = Quantile(.5);
        double high = Math.Max(Quantile(.9995), median + Math.Max(64, (median - low) * 16));
        var lookup = new byte[65536];
        double range = Math.Max(1, high - low), normalization = Math.Asinh(10);
        for (int i = 0; i < lookup.Length; i++) {
            double value = Math.Clamp((i - low) / range, 0, 1);
            lookup[i] = (byte)Math.Round(255 * Math.Asinh(value * 10) / normalization);
        }
        var pixels = new byte[samples.Length];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = lookup[samples[i]];
        token.ThrowIfCancellationRequested();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, pixels, width);
        bitmap.Freeze();
        return new Prepared(bitmap, source, lookup, sourceWidth, sourceHeight);
    }
}
