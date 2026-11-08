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
        private readonly ushort[] reduced;
        private readonly int bitDepth;
        internal Prepared(BitmapSource image, ushort[] source, byte[] lookup, int width, int height, ushort[] reduced,int bitDepth) {
            Image = image; this.source = source; this.lookup = lookup; this.width = width; this.height = height; this.reduced = reduced;
            this.bitDepth=bitDepth;
        }
        public BitmapSource Render(double strength, CancellationToken token,bool stretchEnabled=true) => CreateBitmap(reduced, Image.PixelWidth, Image.PixelHeight, stretchEnabled?Core.DisplayStretch.CreateLookup(reduced,strength):Core.DisplayStretch.CreateLinearLookup(bitDepth),token);
        public Core.PixelPoint[] FindStars(CancellationToken token) => Core.StarCentroids.Detect(reduced, Image.PixelWidth, Image.PixelHeight, source, width, height, token);
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
    public static Prepared Prepare(IImageData image, CancellationToken token, double strength = 1,bool stretchEnabled=true) {
        int sourceWidth = image.Properties.Width, sourceHeight = image.Properties.Height;
        var source = image.Data.FlatArray;
        if (sourceWidth <= 0 || sourceHeight <= 0 || source.LongLength != (long)sourceWidth * sourceHeight)
            throw new InvalidOperationException("Invalid image dimensions for preview.");
        double scale = Math.Min(1, Math.Min(1600d / sourceWidth, 1200d / sourceHeight));
        int width = Math.Max(1, (int)(sourceWidth * scale)), height = Math.Max(1, (int)(sourceHeight * scale));
        var samples = new ushort[width * height];
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
            }
        }
        int displayBitDepth=image.Properties.BitDepth;
        // Some native RAW paths report ADC depth while delivering a wider
        // ushort container. Do not clip that payload to the declared ADC range.
        // Require a population of out-of-range samples, not a lone hot pixel.
        int declaredMaximum=(1<<Math.Clamp(displayBitDepth,1,16))-1;
        if(displayBitDepth<16 && System.Linq.Enumerable.Count(samples,v=>v>declaredMaximum)>samples.Length/100)displayBitDepth=16;
        var lookup = stretchEnabled?Core.DisplayStretch.CreateLookup(samples,strength):Core.DisplayStretch.CreateLinearLookup(displayBitDepth);
        var bitmap = CreateBitmap(samples,width,height,lookup,token);
        return new Prepared(bitmap, source, lookup, sourceWidth, sourceHeight, samples,displayBitDepth);
    }
    private static BitmapSource CreateBitmap(ushort[] samples,int width,int height,byte[] lookup,CancellationToken token) {
        var pixels=new byte[samples.Length];
        for(int i=0;i<pixels.Length;i++) { if((i&65535)==0)token.ThrowIfCancellationRequested();pixels[i]=lookup[samples[i]]; }
        var bitmap=BitmapSource.Create(width,height,96,96,PixelFormats.Gray8,null,pixels,width);bitmap.Freeze();return bitmap;
    }
}
