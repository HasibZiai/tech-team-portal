using System.Numerics;
using SkiaSharp;

// SkiaSharp supports the platforms this app runs on (Windows and Linux).
#pragma warning disable CA1416

namespace PcServ.Web.Services.Maps;

/// <summary>
/// How a point on one map lands on another. Works in 0-to-1 map coordinates:
/// X' = M00*X + M01*Y + M02 and Y' = M10*X + M11*Y + M12.
/// </summary>
public sealed record MapTransform(double M00, double M01, double M02, double M10, double M11, double M12)
{
    public static readonly MapTransform Identity = new(1, 0, 0, 0, 1, 0);

    public (double X, double Y) Apply(double x, double y) => (M00 * x + M01 * y + M02, M10 * x + M11 * y + M12);
}

public sealed record AlignmentResult(MapTransform Transform, double Score, double Scale, double RotationDegrees, bool IsSameDrawing);

/// <summary>A point a person clicked on each map, in 0-to-1 map coordinates.</summary>
public sealed record AlignmentPoint(double SourceX, double SourceY, double TargetX, double TargetY);

/// <summary>
/// Lines up two drawings of the same place by their black linework: tries many scales, and at each
/// one finds the best position using image correlation. Results are suggestions for a person to check.
/// </summary>
public static class MapAligner
{
    private const int CoarseCanvas = 1024, CoarseSide = 400, FineCanvas = 2048, FineSide = 800;
    private const double MinScale = .35, MaxScale = 2.6;

    /// <summary>Below this, a match this weak is worth trying other quarter turns for.</summary>
    private const double TryRotationsBelow = .30;

    private sealed record Mask(int Width, int Height, bool[] Dark);
    private sealed record Spectrum(float[] Re, float[] Im, double Sum);

    public static AlignmentResult Align(SKBitmap source, SKBitmap target)
    {
        var sourceFull = ToMask(source);
        var targetFull = ToMask(target);

        // Coarse: every scale, at low resolution.
        var targetCoarse = Shrink(targetFull, CoarseSide);
        var sourceCoarse = Shrink(sourceFull, CoarseSide);
        var spectrum = Transform(targetCoarse, CoarseCanvas);

        (double Score, double Scale, double Rotation, int Dx, int Dy) best = (0, 1, 0, 0, 0);
        foreach (var turn in new[] { 0.0, 90, 180, 270 })
        {
            if (turn != 0 && best.Score > TryRotationsBelow) break;
            for (var scale = MinScale; scale <= MaxScale; scale *= 1.02)
            {
                var (score, dx, dy) = Correlate(spectrum, sourceCoarse, CoarseCanvas, scale, turn);
                if (score > best.Score) best = (score, scale, turn, dx, dy);
            }
        }

        // Fine: narrow in on scale and a slight rotation, at higher resolution.
        var targetFine = Shrink(targetFull, FineSide);
        var sourceFine = Shrink(sourceFull, FineSide);
        var fineSpectrum = Transform(targetFine, FineCanvas);

        (double Score, double Scale, double Rotation, int Dx, int Dy) fine = (0, best.Scale, best.Rotation, 0, 0);
        void Try(double scale, double rotation)
        {
            var (score, dx, dy) = Correlate(fineSpectrum, sourceFine, FineCanvas, scale, rotation);
            if (score > fine.Score) fine = (score, scale, rotation, dx, dy);
        }

        for (var s = best.Scale * .975; s <= best.Scale * 1.025; s *= 1.0025) Try(s, best.Rotation);
        var scale1 = fine.Scale;
        for (var a = -3.0; a <= 3.001; a += .5) Try(scale1, best.Rotation + a);
        var (scale2, rotation2) = (fine.Scale, fine.Rotation);
        for (var s = scale2 * .996; s <= scale2 * 1.004; s *= 1.0008) Try(s, rotation2);

        var radians = fine.Rotation * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians), center = FineCanvas / 2.0;
        double sw = sourceFine.Width, sh = sourceFine.Height, tw = targetFine.Width, th = targetFine.Height;
        var transform = new MapTransform(
            fine.Scale * cos * sw / tw,
            -fine.Scale * sin * sh / tw,
            (center + fine.Dx + fine.Scale * (-cos * sw / 2 + sin * sh / 2)) / tw,
            fine.Scale * sin * sw / th,
            fine.Scale * cos * sh / th,
            (center + fine.Dy + fine.Scale * (-sin * sw / 2 - cos * sh / 2)) / th);

        // The same drawing at the same size lines up exactly, so there's nothing to check.
        var sameSize = source.Width == target.Width && source.Height == target.Height;
        var same = sameSize && Math.Abs(fine.Scale - 1) < .006 && Math.Abs(fine.Rotation) < .01
                   && Math.Abs(transform.M02) < .006 && Math.Abs(transform.M12) < .006;

        return same
            ? new AlignmentResult(MapTransform.Identity, fine.Score, 1, 0, true)
            : new AlignmentResult(transform, fine.Score, fine.Scale, fine.Rotation, false);
    }

    /// <summary>
    /// Works out an alignment from two or more matching points. It allows moving, turning and resizing,
    /// but never stretching. With more than two points it finds the best fit across all of them.
    /// </summary>
    public static MapTransform FromPoints(IReadOnlyList<AlignmentPoint> points, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        if (points.Count < 2) throw new ArgumentException("At least two matching points are needed.", nameof(points));

        // Measured in pixels, because 0-to-1 coordinates aren't square on a non-square map.
        var from = points.Select(p => new Complex(p.SourceX * sourceWidth, p.SourceY * sourceHeight)).ToList();
        var to = points.Select(p => new Complex(p.TargetX * targetWidth, p.TargetY * targetHeight)).ToList();
        var meanFrom = from.Aggregate(Complex.Zero, (a, b) => a + b) / from.Count;
        var meanTo = to.Aggregate(Complex.Zero, (a, b) => a + b) / to.Count;

        Complex top = Complex.Zero;
        double bottom = 0;
        for (var i = 0; i < from.Count; i++)
        {
            var f = from[i] - meanFrom;
            top += Complex.Conjugate(f) * (to[i] - meanTo);
            bottom += f.Real * f.Real + f.Imaginary * f.Imaginary;
        }
        if (bottom < 1) throw new ArgumentException("Those points are too close together. Choose points further apart.", nameof(points));

        var factor = top / bottom;
        var shift = meanTo - factor * meanFrom;
        return new MapTransform(
            factor.Real * sourceWidth / targetWidth,
            -factor.Imaginary * sourceHeight / targetWidth,
            shift.Real / targetWidth,
            factor.Imaginary * sourceWidth / targetHeight,
            factor.Real * sourceHeight / targetHeight,
            shift.Imaginary / targetHeight);
    }

    /// <summary>A picture for checking an alignment: the plan in grey with the other drawing's lines in red on top.</summary>
    public static byte[] RenderPreview(SKBitmap source, SKBitmap target, MapTransform t, int longSide = 2400)
    {
        var scale = (float)longSide / Math.Max(target.Width, target.Height);
        int width = (int)(target.Width * scale), height = (int)(target.Height * scale);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        // Lighten the plan so red lines stand out against it.
        using var greyPaint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(255, 255, 255, 120), SKBlendMode.SrcOver) };
        using var targetImage = SKImage.FromBitmap(target);
        canvas.DrawImage(targetImage, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), greyPaint);

        using var red = ToRedLines(source);
        using var redImage = SKImage.FromBitmap(red);
        var matrix = new SKMatrix(
            (float)(t.M00 * width / source.Width), (float)(t.M01 * width / source.Height), (float)(t.M02 * width),
            (float)(t.M10 * height / source.Width), (float)(t.M11 * height / source.Height), (float)(t.M12 * height),
            0, 0, 1);
        canvas.SetMatrix(matrix);
        canvas.DrawImage(redImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        canvas.ResetMatrix();

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static SKBitmap ToRedLines(SKBitmap source)
    {
        var result = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var pixels = source.Pixels;
        var output = new SKColor[pixels.Length];
        var line = new SKColor(220, 20, 20, 255);
        for (var i = 0; i < pixels.Length; i++)
        {
            output[i] = IsDark(pixels[i]) ? line : SKColors.Transparent;
        }
        result.Pixels = output;
        return result;
    }

    // Plans use lighter lines than the Wi-Fi maps, so anything clearly not white and not colored counts.
    private static bool IsDark(SKColor c)
    {
        int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue)), min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
        return max - min <= 30 && max < 200;
    }

    private static Mask ToMask(SKBitmap bitmap)
    {
        var pixels = bitmap.Pixels;
        var dark = new bool[pixels.Length];
        for (var i = 0; i < pixels.Length; i++) dark[i] = IsDark(pixels[i]);
        return new Mask(bitmap.Width, bitmap.Height, dark);
    }

    // Marks a cell if enough of it is dark, so thin lines survive shrinking.
    private static Mask Shrink(Mask m, int longSide)
    {
        var k = (double)longSide / Math.Max(m.Width, m.Height);
        int w = Math.Max(1, (int)(m.Width * k)), h = Math.Max(1, (int)(m.Height * k));
        var darkCount = new int[w * h];
        var total = new int[w * h];
        for (var y = 0; y < m.Height; y++)
        {
            var row = Math.Min(h - 1, (int)(y * k)) * w;
            for (var x = 0; x < m.Width; x++)
            {
                var cell = row + Math.Min(w - 1, (int)(x * k));
                total[cell]++;
                if (m.Dark[y * m.Width + x]) darkCount[cell]++;
            }
        }

        var dark = new bool[w * h];
        for (var i = 0; i < dark.Length; i++) dark[i] = total[i] > 0 && darkCount[i] >= Math.Max(1, total[i] / 5);
        return new Mask(w, h, dark);
    }

    private static Spectrum Transform(Mask target, int size)
    {
        var re = new float[size * size];
        var im = new float[size * size];
        double sum = 0;

        // Widening target lines by a pixel makes the match forgiving of small differences.
        for (var y = 0; y < target.Height; y++)
        {
            for (var x = 0; x < target.Width; x++)
            {
                if (!target.Dark[y * target.Width + x]) continue;
                for (var yy = Math.Max(0, y - 1); yy <= Math.Min(target.Height - 1, y + 1); yy++)
                {
                    for (var xx = Math.Max(0, x - 1); xx <= Math.Min(target.Width - 1, x + 1); xx++)
                    {
                        if (re[yy * size + xx] != 0) continue;
                        re[yy * size + xx] = 1;
                        sum++;
                    }
                }
            }
        }

        Fft2(re, im, size, inverse: false);
        return new Spectrum(re, im, sum);
    }

    private static (double Score, int Dx, int Dy) Correlate(Spectrum target, Mask source, int size, double scale, double rotationDegrees)
    {
        var re = new float[size * size];
        var im = new float[size * size];
        double sum = 0;
        var radians = rotationDegrees * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians), cx = source.Width / 2.0, cy = source.Height / 2.0, center = size / 2.0;

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                if (!source.Dark[y * source.Width + x]) continue;
                double px = x - cx, py = y - cy;
                int u = (int)(center + scale * (cos * px - sin * py)), v = (int)(center + scale * (sin * px + cos * py));
                if (u < 0 || v < 0 || u >= size || v >= size || re[v * size + u] != 0) continue;
                re[v * size + u] = 1;
                sum++;
            }
        }
        if (sum < 50 || target.Sum < 50) return (0, 0, 0);

        Fft2(re, im, size, inverse: false);
        var productRe = new float[size * size];
        var productIm = new float[size * size];
        for (var i = 0; i < productRe.Length; i++)
        {
            productRe[i] = target.Re[i] * re[i] + target.Im[i] * im[i];
            productIm[i] = target.Im[i] * re[i] - target.Re[i] * im[i];
        }
        Fft2(productRe, productIm, size, inverse: true);

        var bestIndex = 0;
        var bestValue = float.MinValue;
        for (var i = 0; i < productRe.Length; i++)
        {
            if (productRe[i] <= bestValue) continue;
            bestValue = productRe[i];
            bestIndex = i;
        }

        int dy = bestIndex / size, dx = bestIndex % size;
        if (dx > size / 2) dx -= size;
        if (dy > size / 2) dy -= size;
        return (bestValue / Math.Sqrt(target.Sum * sum), dx, dy);
    }

    private static void Fft2(float[] re, float[] im, int n, bool inverse)
    {
        var r = new float[n];
        var i = new float[n];
        for (var y = 0; y < n; y++)
        {
            Array.Copy(re, y * n, r, 0, n);
            Array.Copy(im, y * n, i, 0, n);
            Fft1(r, i, inverse);
            Array.Copy(r, 0, re, y * n, n);
            Array.Copy(i, 0, im, y * n, n);
        }
        for (var x = 0; x < n; x++)
        {
            for (var y = 0; y < n; y++)
            {
                r[y] = re[y * n + x];
                i[y] = im[y * n + x];
            }
            Fft1(r, i, inverse);
            for (var y = 0; y < n; y++)
            {
                re[y * n + x] = r[y];
                im[y * n + x] = i[y];
            }
        }

        if (!inverse) return;
        var factor = 1f / (n * n);
        for (var k = 0; k < re.Length; k++)
        {
            re[k] *= factor;
            im[k] *= factor;
        }
    }

    private static void Fft1(float[] re, float[] im, bool inverse)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = 2 * Math.PI / length * (inverse ? 1 : -1);
            float stepRe = (float)Math.Cos(angle), stepIm = (float)Math.Sin(angle);
            for (var i = 0; i < n; i += length)
            {
                float curRe = 1, curIm = 0;
                for (var j = 0; j < length / 2; j++)
                {
                    int a = i + j, b = i + j + length / 2;
                    float xr = re[b] * curRe - im[b] * curIm, xi = re[b] * curIm + im[b] * curRe;
                    re[b] = re[a] - xr;
                    im[b] = im[a] - xi;
                    re[a] += xr;
                    im[a] += xi;
                    var nextRe = curRe * stepRe - curIm * stepIm;
                    curIm = curRe * stepIm + curIm * stepRe;
                    curRe = nextRe;
                }
            }
        }
    }
}
