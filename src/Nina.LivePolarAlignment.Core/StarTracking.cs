namespace Nina.LivePolarAlignment.Core;

/// <summary>Bounded star detection on a peak-reduced image, with centroids measured on original RAW pixels.</summary>
public static class StarCentroids {
    public static PixelPoint[] Detect(ushort[] reduced, int width, int height, ushort[] raw, int rawWidth, int rawHeight, CancellationToken token) {
        bool eightBit = reduced.All(v => v <= 255);
        int shift = eightBit ? 0 : 4;
        var histogram = new int[eightBit ? 256 : 4096];
        foreach (var value in reduced) histogram[value >> shift]++;
        int Quantile(double q) {
            int count = 0;
            for (int i = 0; i < histogram.Length; i++) { count += histogram[i]; if (count >= reduced.Length * q) return i << shift; }
            return 65535;
        }
        int median = Quantile(.5), noise = Math.Max(eightBit ? 1 : 8, median - Quantile(.16));
        int threshold = median + Math.Max(eightBit ? 4 : 40, noise * 6);
        var candidates = new PriorityQueue<(int X, int Y), int>();
        for (int y = 2; y < height - 2; y++) {
            token.ThrowIfCancellationRequested();
            for (int x = 2; x < width - 2; x++) {
                int value = reduced[y * width + x];
                if (value <= threshold || value >= 65520) continue;
                bool maximum = true;
                for (int dy = -1; dy <= 1 && maximum; dy++) for (int dx = -1; dx <= 1; dx++)
                    if ((dx != 0 || dy != 0) && (reduced[(y + dy) * width + x + dx] > value
                        || reduced[(y + dy) * width + x + dx] == value && (dy<0 || dy==0 && dx<0))) { maximum = false; break; }
                if (!maximum) continue;
                candidates.Enqueue((x, y), value);
                if (candidates.Count > 140) candidates.Dequeue();
            }
        }
        var ordered = new List<(int X, int Y)>();
        while (candidates.TryDequeue(out var p, out _)) ordered.Add(p);
        ordered.Reverse();
        var stars = new List<PixelPoint>();
        int rx = Math.Clamp((int)Math.Ceiling((double)rawWidth / width) + 2, 4, 20);
        int ry = Math.Clamp((int)Math.Ceiling((double)rawHeight / height) + 2, 4, 20);
        foreach (var p in ordered) {
            token.ThrowIfCancellationRequested();
            int x = (int)((p.X + .5) * rawWidth / width), y = (int)((p.Y + .5) * rawHeight / height);
            if (x < rx || y < ry || x + rx >= rawWidth || y + ry >= rawHeight) continue;
            double background = (raw[(y - ry) * rawWidth + x - rx] + raw[(y - ry) * rawWidth + x + rx]
                + raw[(y + ry) * rawWidth + x - rx] + raw[(y + ry) * rawWidth + x + rx]) / 4d;
            double sum = 0, sx = 0, sy = 0; int significant = 0;
            for (int dy = -ry; dy <= ry; dy++) for (int dx = -rx; dx <= rx; dx++) {
                double weight = Math.Max(0, raw[(y + dy) * rawWidth + x + dx] - background - noise);
                if (weight > noise * 4) significant++;
                sum += weight; sx += weight * (x + dx + .5); sy += weight * (y + dy + .5);
            }
            if (sum <= 0 || significant < 3) continue; // Reject isolated hot pixels/cosmic rays.
            var centroid = new PixelPoint(sx / sum, sy / sum);
            if (stars.Any(s => Math.Pow(s.X - centroid.X, 2) + Math.Pow(s.Y - centroid.Y, 2) < Math.Pow(Math.Max(rx, ry) * 2, 2))) continue;
            stars.Add(centroid);
            if (stars.Count == FrameRegistration.MaximumStars) break;
        }
        return stars.ToArray();
    }
}

public sealed record TrackedStar(int Id, PixelPoint Position);
public sealed record TrackedOrientation(SkyOrientation Orientation, int Stars, double RmsPixels, TrackedStar[]? Markers = null);

/// <summary>Fits a fresh spherical camera pose to fixed ICRS stars. Never chains incremental transforms.</summary>
public sealed class AstrometricTracker {
    private readonly SkyOrientation orientation;
    private readonly PixelPoint[] stars;
    private readonly int width, height;
    private readonly double focal;
    private readonly bool flipped;
    public AstrometricTracker(SkyOrientation orientation, PixelPoint[] stars, int width, int height, double pixelScale, bool flipped) {
        if (stars.Length < 8 || width <= 0 || height <= 0 || !double.IsFinite(pixelScale) || pixelScale <= 0)
            throw new ArgumentException("At least eight reference stars and a valid plate solution are required.");
        this.orientation = orientation; this.stars = stars; this.width = width; this.height = height;
        focal = 206264.80624709636 / pixelScale; this.flipped = flipped;
    }
    public TrackedOrientation Track(PixelPoint[] current, int currentWidth, int currentHeight) {
        if (current.Length < 8 || currentWidth <= 0 || currentHeight <= 0 || Math.Abs((double)currentWidth / currentHeight / ((double)width / height) - 1) > .002)
            throw new InvalidOperationException("Too few stars or changed field geometry. Recover with a plate solve.");
        double scale = (double)width / currentWidth;
        var scaled = current.Select(p => new PixelPoint(p.X * scale, p.Y * scale)).ToArray();
        double tolerance = Math.Max(2, scale * 1.5);
        var registration = FrameRegistration.Estimate(stars, scaled, tolerance);
        var pairs = new List<(PixelPoint A, PixelPoint B, int Id)>(); var used = new HashSet<int>();
        for(int index=0;index<stars.Length;index++) {
            var star=stars[index];
            var predicted = registration.Transform.Apply(star);
            int best = -1; double distance = tolerance * tolerance;
            for (int j = 0; j < scaled.Length; j++) {
                double d = Math.Pow(scaled[j].X - predicted.X, 2) + Math.Pow(scaled[j].Y - predicted.Y, 2);
                if (!used.Contains(j) && d < distance) { distance = d; best = j; }
            }
            if (best >= 0) { used.Add(best); pairs.Add((star, scaled[best], index+1)); }
        }
        if (pairs.Count < Math.Max(8, Math.Min(stars.Length, current.Length) / 2)
            || pairs.Max(p => p.A.X) - pairs.Min(p => p.A.X) < width * .15
            || pairs.Max(p => p.A.Y) - pairs.Min(p => p.A.Y) < height * .15)
            throw new InvalidOperationException("Insufficient well-distributed tracking stars. Recover with a plate solve.");
        double parity = flipped ? -1 : 1;
        var h = new double[3, 3];
        foreach (var pair in pairs) {
            var sky = (orientation.Center + orientation.Right * ((pair.A.X - width / 2d) / focal)
                - orientation.Up * ((pair.A.Y - height / 2d) / focal)).Normalized();
            var camera = new Vector3d((pair.B.X - width / 2d) / focal * parity, -(pair.B.Y - height / 2d) / focal, 1).Normalized();
            double[] a = [camera.X, camera.Y, camera.Z], b = [sky.X, sky.Y, sky.Z];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) h[i, j] += a[i] * b[j];
        }
        double xx = h[0,0], xy = h[0,1], xz = h[0,2], yx = h[1,0], yy = h[1,1], yz = h[1,2], zx = h[2,0], zy = h[2,1], zz = h[2,2];
        var n = new double[,] { {xx+yy+zz,yz-zy,zx-xz,xy-yx}, {yz-zy,xx-yy-zz,xy+yx,zx+xz},
            {zx-xz,xy+yx,-xx+yy-zz,yz+zy}, {xy-yx,zx+xz,yz+zy,-xx-yy+zz} };
        var eigen = new double[,] { {1,0,0,0},{0,1,0,0},{0,0,1,0},{0,0,0,1} };
        for (int iteration = 0; iteration < 80; iteration++) {
            int p = 0, q = 1;
            for (int i = 0; i < 4; i++) for (int j = i + 1; j < 4; j++) if (Math.Abs(n[i,j]) > Math.Abs(n[p,q])) { p = i; q = j; }
            if (Math.Abs(n[p,q]) < 1e-14) break;
            double angle = .5 * Math.Atan2(2*n[p,q], n[q,q]-n[p,p]), c = Math.Cos(angle), s = Math.Sin(angle);
            double pp = n[p,p], qq = n[q,q], pq = n[p,q];
            for (int k = 0; k < 4; k++) if (k != p && k != q) { double kp = n[k,p], kq = n[k,q]; n[k,p] = n[p,k] = c*kp-s*kq; n[k,q] = n[q,k] = s*kp+c*kq; }
            n[p,p] = c*c*pp-2*s*c*pq+s*s*qq; n[q,q] = s*s*pp+2*s*c*pq+c*c*qq; n[p,q] = n[q,p] = 0;
            for (int k = 0; k < 4; k++) { double kp = eigen[k,p], kq = eigen[k,q]; eigen[k,p] = c*kp-s*kq; eigen[k,q] = s*kp+c*kq; }
        }
        int largest = Enumerable.Range(0,4).MaxBy(i => n[i,i]);
        var v = new Vector3d(eigen[1,largest], eigen[2,largest], eigen[3,largest]); double w = eigen[0,largest];
        Vector3d Turn(Vector3d p) => p + v.Cross(v.Cross(p) + p*w)*2;
        var pose = new SkyOrientation(Turn(new(0,0,1)), Turn(new(1,0,0))*parity, Turn(new(0,1,0)));
        double error = 0;
        foreach (var pair in pairs) {
            var sky = (orientation.Center + orientation.Right*((pair.A.X-width/2d)/focal) - orientation.Up*((pair.A.Y-height/2d)/focal)).Normalized();
            var projected = PolarAlignmentGeometry.ProjectDirection(sky, pose, focal, width/2d, height/2d)
                ?? throw new InvalidOperationException("Tracked star left the front hemisphere.");
            error += Math.Pow(projected.X-pair.B.X,2) + Math.Pow(projected.Y-pair.B.Y,2);
        }
        double rms = Math.Sqrt(error/pairs.Count)/scale;
        if (!double.IsFinite(rms) || rms > .8) throw new InvalidOperationException("Tracking residual too large. Recover with a plate solve.");
        return new(pose, pairs.Count, rms, pairs.Select(p=>new TrackedStar(p.Id,new(p.B.X/scale,p.B.Y/scale))).ToArray());
    }
}
