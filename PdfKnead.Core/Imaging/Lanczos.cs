namespace PdfKnead.Core.Imaging;

/// <summary>
/// Scales images with Lanczos 3: sharp without staircase artefacts, in linear light (light and
/// dark edges mix correctly, fine dark lines do not close up when scaling down), with premultiplied alpha (no
/// colour fringes at transparent edges). When scaling down strongly, the image is first area-averaged down to twice the target size,
/// then Lanczos is applied; the result is practically the same but costs a fraction, so it is fast
/// even on 2–4 cores. Two passes (horizontal first, then vertical), rows in parallel.
/// </summary>
public static class Lanczos
{
    private const int Lobes = 3;

    // sRGB ↔ linear: 256 levels forward, 4096 back (enough for 8 bits)
    private static readonly float[] ToLinear = BuildToLinear();
    private static readonly byte[] ToSrgb = BuildToSrgb();

    /// <summary>
    /// Scales to <paramref name="width"/> × <paramref name="height"/> (up or down).
    /// </summary>
    /// <param name="source">The source image.</param>
    /// <param name="width">Target width in px.</param>
    /// <param name="height">Target height in px.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>A new image, or the source itself if the size is unchanged.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is less than 1.</exception>
    /// <remarks>Uses all processor cores; memory about 16 bytes per pixel of the intermediate images.</remarks>
    public static RgbaImage Resize(RgbaImage source, int width, int height, CancellationToken cancel = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (width == source.Width && height == source.Height) return source;
        // Pre-pass: area average by an integer factor as long as at least twice the target size remains
        int fx = Math.Max(1, source.Width / (2 * width)), fy = Math.Max(1, source.Height / (2 * height));
        var planes = Planes(source, fx, fy, cancel, out var w, out var h);
        var options = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };

        // Horizontal: w → width, per row
        var horizontal = Weights(w, width, (double)source.Width / fx);
        var across = new float[(long)width * h * 4];
        Parallel.For(0, h, options, y =>
        {
            var row = y * w * 4;
            var outRow = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var (start, weights) = horizontal[x];
                float r = 0, g = 0, b = 0, a = 0;
                for (var k = 0; k < weights.Length; k++)
                {
                    var s = row + (start + k) * 4;
                    var wk = weights[k];
                    r += planes[s] * wk;
                    g += planes[s + 1] * wk;
                    b += planes[s + 2] * wk;
                    a += planes[s + 3] * wk;
                }
                var d = outRow + x * 4;
                across[d] = r;
                across[d + 1] = g;
                across[d + 2] = b;
                across[d + 3] = a;
            }
        });

        // Vertical: h → height, back to sRGB and un-premultiplied
        var vertical = Weights(h, height, (double)source.Height / fy);
        var result = RgbaImage.Blank(width, height);
        var pixels = result.Pixels;
        Parallel.For(0, height, options, y =>
        {
            var (start, weights) = vertical[y];
            for (var x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (var k = 0; k < weights.Length; k++)
                {
                    var s = ((start + k) * width + x) * 4;
                    var wk = weights[k];
                    r += across[s] * wk;
                    g += across[s + 1] * wk;
                    b += across[s + 2] * wk;
                    a += across[s + 3] * wk;
                }
                var d = (y * width + x) * 4;
                a = Math.Clamp(a, 0, 1);
                if (a <= 1f / 512)
                {
                    pixels[d + 3] = 0;   // fully transparent: colour irrelevant (black)
                    continue;
                }
                pixels[d] = Encode(r / a);
                pixels[d + 1] = Encode(g / a);
                pixels[d + 2] = Encode(b / a);
                pixels[d + 3] = (byte)(a * 255 + 0.5f);
            }
        });
        return result;
    }

    /// <summary>
    /// Premultiplied linear values (0 … 1), averaging <paramref name="fx"/> × <paramref name="fy"/> pixels each; the last column and row
    /// average what is left at the right and bottom edge (before, those pixels were dropped and the image slightly stretched;
    /// docs/FEHLERGEDAECHTNIS.md, No. 270).
    /// </summary>
    private static float[] Planes(RgbaImage source, int fx, int fy, CancellationToken cancel, out int w, out int h)
    {
        w = Math.Max(1, (source.Width + fx - 1) / fx);
        h = Math.Max(1, (source.Height + fy - 1) / fy);
        var (pw, ph) = (w, h);
        var planes = new float[(long)w * h * 4];
        var src = source.Pixels;
        var sw = source.Width;
        var sh = source.Height;
        var options = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };
        Parallel.For(0, ph, options, y =>
        {
            for (var x = 0; x < pw; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                var columns = Math.Min(fx, sw - x * fx);
                var rows = Math.Min(fy, sh - y * fy);
                var count = 1f / (columns * rows);
                for (var sy = y * fy; sy < y * fy + rows; sy++)
                {
                    var s = (sy * sw + x * fx) * 4;
                    for (var sx = 0; sx < columns; sx++, s += 4)
                    {
                        var alpha = src[s + 3] / 255f;
                        r += ToLinear[src[s]] * alpha;
                        g += ToLinear[src[s + 1]] * alpha;
                        b += ToLinear[src[s + 2]] * alpha;
                        a += alpha;
                    }
                }
                var d = (y * pw + x) * 4;
                planes[d] = r * count;
                planes[d + 1] = g * count;
                planes[d + 2] = b * count;
                planes[d + 3] = a * count;
            }
        });
        return planes;
    }

    /// <summary>Weights per target pixel: first source pixel and the (normalised) weights; at the border nothing is read beyond the image.</summary>
    /// <param name="from">Source pixels.</param>
    /// <param name="to">Target pixels.</param>
    /// <param name="extent">The width the source pixels cover, in source pixels (less than <paramref name="from"/> if the last one is a part).</param>
    private static (int Start, float[] Weights)[] Weights(int from, int to, double extent)
    {
        var scale = extent / to;
        var support = scale > 1 ? Lobes * scale : Lobes;   // when scaling down, widened by the reduction factor
        var stretch = scale > 1 ? 1 / scale : 1;
        var result = new (int, float[])[to];
        for (var i = 0; i < to; i++)
        {
            var center = (i + 0.5) * scale;
            var start = Math.Max(0, (int)Math.Floor(center - support));
            var end = Math.Min(from, (int)Math.Ceiling(center + support));
            var weights = new float[Math.Max(1, end - start)];
            double sum = 0;
            for (var j = start; j < end; j++)
            {
                var v = Kernel((j + 0.5 - center) * stretch);
                weights[j - start] = (float)v;
                sum += v;
            }
            if (Math.Abs(sum) < 1e-9)
            {
                weights[Math.Clamp((int)center - start, 0, weights.Length - 1)] = 1;
                sum = 1;
            }
            for (var k = 0; k < weights.Length; k++) weights[k] = (float)(weights[k] / sum);
            result[i] = (start, weights);
        }
        return result;
    }

    private static double Kernel(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-9) return 1;
        if (x >= Lobes) return 0;
        var px = Math.PI * x;
        return Lobes * Math.Sin(px) * Math.Sin(px / Lobes) / (px * px);
    }

    private static byte Encode(float linear)
    {
        var i = (int)(Math.Clamp(linear, 0, 1) * 4095 + 0.5f);
        return ToSrgb[i];
    }

    private static float[] BuildToLinear()
    {
        var table = new float[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255.0;
            table[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }
        return table;
    }

    private static byte[] BuildToSrgb()
    {
        var table = new byte[4096];
        for (var i = 0; i < 4096; i++)
        {
            var l = i / 4095.0;
            var c = l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
            table[i] = (byte)Math.Clamp(Math.Round(c * 255), 0, 255);
        }
        return table;
    }
}
