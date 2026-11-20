namespace PdfKnead.Core.Imaging;

/// <summary>Palette image: one index per pixel (1 byte), the colour list (one entry per colour, 1 or 3 bytes each) and the quality (PSNR in dB).</summary>
/// <param name="Indices">Palette index per pixel, row by row.</param>
/// <param name="Palette">The colours, 1 byte (grey) or 3 bytes (RGB) per entry.</param>
/// <param name="Count">Number of palette entries.</param>
/// <param name="Psnr">Quality against the original in dB; infinity for a lossless palette.</param>
public sealed record PaletteImage(byte[] Indices, byte[] Palette, int Count, double Psnr)
{
    /// <summary>Bits per index that suffice for <see cref="Count"/> colours (1, 2, 4 or 8).</summary>
    public int Bits => Count <= 2 ? 1 : Count <= 4 ? 2 : Count <= 16 ? 4 : 8;

    /// <summary>Packs the indices row by row into <see cref="Bits"/> bits each (every row padded to whole bytes), as PDF expects them.</summary>
    /// <param name="width">Image width in px.</param>
    /// <param name="height">Image height in px.</param>
    /// <returns>The packed samples; <see cref="Indices"/> itself for 8 bits.</returns>
    public byte[] Packed(int width, int height)
    {
        var bits = Bits;
        if (bits == 8) return Indices;
        var stride = (width * bits + 7) / 8;
        var result = new byte[stride * height];
        var perByte = 8 / bits;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                result[y * stride + x / perByte] |= (byte)(Indices[y * width + x] << (8 - bits * (x % perByte + 1)));
        return result;
    }
}

/// <summary>
/// Reduces the colours of an image to a palette (an implementation of its own, not pngquant, because pngquant is GPL-licensed):
/// <list type="bullet">
/// <item><see cref="Exact"/>: at most as many different colours as allowed, lossless, only rewritten.</item>
/// <item><see cref="Quantize"/>: more colours. Median cut (the box with the largest weighted spread first, split at the weighted
/// median of the axis with the largest spread), then a few rounds of k-means over the colour frequencies, then every pixel to the
/// nearest colour, optionally with Floyd–Steinberg error diffusion (strength 0 … 1).</item>
/// </list>
/// The quality is the PSNR over all channels against the original (measured without dithering).
/// </summary>
public static class PaletteQuantizer
{
    /// <summary>At most this many different colours are counted exactly; beyond that they are reduced to 6 bits per channel (speed, memory).</summary>
    private const int MaxExactColors = 1 << 18;

    /// <summary>Builds a lossless palette if the image has at most <paramref name="maxColors"/> colours.</summary>
    /// <param name="samples">The samples, <paramref name="components"/> bytes per pixel.</param>
    /// <param name="pixels">Number of pixels.</param>
    /// <param name="components">1 (grey) or 3 (RGB).</param>
    /// <param name="maxColors">Maximum number of colours (at most 256).</param>
    /// <returns>The palette image, or null if the image has more colours.</returns>
    public static PaletteImage? Exact(ReadOnlySpan<byte> samples, int pixels, int components, int maxColors = 256)
    {
        // Count first (a photo gives up after about 257 pixels), then assign the indices; allocating the index array up front would
        // cost 200 MB for 200 megapixels even for images that are rejected
        var map = new Dictionary<int, int>();
        var palette = new List<int>();
        for (var i = 0; i < pixels; i++)
        {
            var key = Key(samples, i, components);
            if (map.ContainsKey(key)) continue;
            if (palette.Count >= maxColors) return null;
            map[key] = palette.Count;
            palette.Add(key);
        }
        var indices = new byte[pixels];
        for (var i = 0; i < pixels; i++) indices[i] = (byte)map[Key(samples, i, components)];
        return new PaletteImage(indices, Bytes(palette, components), palette.Count, double.PositiveInfinity);
    }

    /// <summary>
    /// Reduces an RGB image (3 bytes per pixel) to at most <paramref name="maxColors"/> colours.
    /// </summary>
    /// <param name="rgb">The samples, 3 bytes per pixel.</param>
    /// <param name="width">Width in px.</param>
    /// <param name="height">Height in px.</param>
    /// <param name="maxColors">Maximum number of colours, 2 … 256 (larger values are limited to 256).</param>
    /// <param name="dither">0 = no dithering, 1 = full Floyd–Steinberg diffusion.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <param name="minimumPsnr">Minimum quality in dB; 0 = no minimum.</param>
    /// <returns>The palette image, or null if the image is empty, <paramref name="maxColors"/> is below 2, or the estimated quality clearly
    /// misses <paramref name="minimumPsnr"/>.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was triggered.</exception>
    public static PaletteImage? Quantize(ReadOnlySpan<byte> rgb, int width, int height, int maxColors, double dither, CancellationToken cancel = default,
        double minimumPsnr = 0)
    {
        var pixels = width * height;
        if (pixels == 0 || maxColors < 2) return null;
        maxColors = Math.Min(maxColors, 256);

        // Frequencies (reduced in precision for very many colours)
        var histogram = new Dictionary<int, int>();
        var coarse = false;
        for (var i = 0; i < pixels; i++)
        {
            var key = Key(rgb, i, 3);
            if (coarse) key &= 0xFCFCFC;
            histogram[key] = histogram.GetValueOrDefault(key) + 1;
            if (!coarse && histogram.Count > MaxExactColors)
            {
                coarse = true;
                var merged = new Dictionary<int, int>();
                foreach (var (k, n) in histogram) merged[k & 0xFCFCFC] = merged.GetValueOrDefault(k & 0xFCFCFC) + n;
                histogram = merged;
            }
        }
        cancel.ThrowIfCancellationRequested();
        var colors = histogram.Select(e => new Entry(e.Key >> 16 & 255, e.Key >> 8 & 255, e.Key & 255, e.Value)).ToArray();
        var palette = MedianCut(colors, maxColors);
        cancel.ThrowIfCancellationRequested();
        // First estimate before refining, from a sample of the colours: k-means usually raises the quality by 1–2 dB, hence a 3 dB margin
        if (minimumPsnr > 0 && colors.Length > 20_000)
        {
            var cut = new int[palette.Count * 3];
            for (var p = 0; p < palette.Count; p++) (cut[p * 3], cut[p * 3 + 1], cut[p * 3 + 2]) = palette[p];
            double sample = 0, weight = 0;
            for (var e = 0; e < colors.Length; e += colors.Length / 20_000)
            {
                var c = colors[e];
                var index = Nearest(cut, palette.Count, c.R, c.G, c.B);
                int dr = c.R - cut[index * 3], dg = c.G - cut[index * 3 + 1], db = c.B - cut[index * 3 + 2];
                sample += (double)c.Count * (dr * dr + dg * dg + db * db);
                weight += c.Count;
            }
            if (Psnr(sample / (3.0 * weight)) < minimumPsnr - 3) return null;
        }
        Refine(colors, palette, rounds: 4, cancel);

        // Nearest colour per entry of the frequency table, and from it the quality estimate: if it clearly misses the minimum quality, stop
        // here. Assigning every pixel first and discarding the result afterwards took 4.9 s for a non-JPEG photo with 1.8 million colours.
        var count = palette.Count;
        var pal = new int[count * 3];
        for (var p = 0; p < count; p++) (pal[p * 3], pal[p * 3 + 1], pal[p * 3 + 2]) = palette[p];
        var nearest = new Dictionary<int, byte>(histogram.Count);
        double estimate = 0;
        var done = 0;
        foreach (var (key, n) in histogram)
        {
            var index = Nearest(pal, count, key >> 16 & 255, key >> 8 & 255, key & 255);
            nearest[key] = index;
            int dr = (key >> 16 & 255) - pal[index * 3], dg = (key >> 8 & 255) - pal[index * 3 + 1], db = (key & 255) - pal[index * 3 + 2];
            estimate += (double)n * (dr * dr + dg * dg + db * db);
            if ((++done & 4095) == 0) cancel.ThrowIfCancellationRequested();
        }
        // The reduced table underestimates the error by at most the rounding (up to 3 levels per channel): 1 dB margin
        if (minimumPsnr > 0 && Psnr(estimate / (3.0 * pixels)) < minimumPsnr - 1) return null;

        // Assign the pixels: without dithering via the frequency table (each colour searched once; the reduced one for very many colours),
        // with dithering via a 5-bit lookup table (the diffused errors produce almost arbitrary colours)
        double sse = 0;
        var indices = new byte[pixels];
        for (var i = 0; i < pixels; i++)
        {
            var key = Key(rgb, i, 3);
            if (coarse) key &= 0xFCFCFC;
            var index = nearest[key];
            indices[i] = index;
            if ((i & 0xFFFF) == 0) cancel.ThrowIfCancellationRequested();
            for (var c = 0; c < 3; c++)
            {
                double d = rgb[i * 3 + c] - pal[index * 3 + c];
                sse += d * d;
            }
        }
        var psnr = Psnr(sse / (3.0 * pixels));
        if (dither > 0) Dither(rgb, width, height, pal, count, Math.Min(1, dither), indices, cancel);
        return new PaletteImage(indices, Bytes(palette.Select(c => c.R << 16 | c.G << 8 | c.B).ToList(), 3), count, psnr);
    }

    private static double Psnr(double mse) => mse <= 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255 / mse);

    private readonly record struct Entry(int R, int G, int B, int Count);

    private static int Key(ReadOnlySpan<byte> samples, int pixel, int components) => components == 1
        ? samples[pixel]
        : samples[pixel * 3] << 16 | samples[pixel * 3 + 1] << 8 | samples[pixel * 3 + 2];

    private static byte[] Bytes(List<int> palette, int components)
    {
        var result = new byte[palette.Count * components];
        for (var i = 0; i < palette.Count; i++)
        {
            if (components == 1)
            {
                result[i] = (byte)palette[i];
                continue;
            }
            (result[i * 3], result[i * 3 + 1], result[i * 3 + 2]) = ((byte)(palette[i] >> 16), (byte)(palette[i] >> 8), (byte)palette[i]);
        }
        return result;
    }

    /// <summary>Median cut: always splits the box with the largest weighted spread along its widest axis at the weighted median.</summary>
    private static List<(int R, int G, int B)> MedianCut(Entry[] colors, int maxColors)
    {
        var boxes = new List<(int Start, int End)> { (0, colors.Length) };
        while (boxes.Count < maxColors)
        {
            var bestBox = -1;
            double bestScore = 0;
            var bestAxis = 0;
            for (var b = 0; b < boxes.Count; b++)
            {
                var (start, end) = boxes[b];
                if (end - start < 2) continue;
                var (axis, variance) = Spread(colors, start, end);
                if (variance > bestScore)
                {
                    (bestScore, bestBox, bestAxis) = (variance, b, axis);
                }
            }
            if (bestBox < 0) break;
            var (s, e) = boxes[bestBox];
            Array.Sort(colors, s, e - s, Comparer<Entry>.Create((x, y) => Channel(x, bestAxis).CompareTo(Channel(y, bestAxis))));
            long total = 0;
            for (var i = s; i < e; i++) total += colors[i].Count;
            long running = 0;
            var cut = s + 1;
            for (var i = s; i < e - 1; i++)
            {
                running += colors[i].Count;
                cut = i + 1;
                if (running * 2 >= total) break;
            }
            boxes[bestBox] = (s, cut);
            boxes.Add((cut, e));
        }
        return [.. boxes.Select(b => Mean(colors, b.Start, b.End))];
    }

    private static int Channel(Entry e, int axis) => axis switch { 0 => e.R, 1 => e.G, _ => e.B };

    /// <summary>Axis with the largest spread and the weighted total spread (sum of squared deviations) of the box.</summary>
    private static (int Axis, double Variance) Spread(Entry[] colors, int start, int end)
    {
        double n = 0, sr = 0, sg = 0, sb = 0, qr = 0, qg = 0, qb = 0;
        for (var i = start; i < end; i++)
        {
            var e = colors[i];
            n += e.Count;
            sr += (double)e.R * e.Count; sg += (double)e.G * e.Count; sb += (double)e.B * e.Count;
            qr += (double)e.R * e.R * e.Count; qg += (double)e.G * e.G * e.Count; qb += (double)e.B * e.B * e.Count;
        }
        double vr = qr - sr * sr / n, vg = qg - sg * sg / n, vb = qb - sb * sb / n;
        // Green weighs more for the eye (as in luminance)
        (vr, vg, vb) = (vr * 0.8, vg * 1.2, vb * 0.6);
        var axis = vr >= vg && vr >= vb ? 0 : vg >= vb ? 1 : 2;
        return (axis, vr + vg + vb);
    }

    private static (int R, int G, int B) Mean(Entry[] colors, int start, int end)
    {
        double n = 0, r = 0, g = 0, b = 0;
        for (var i = start; i < end; i++)
        {
            var e = colors[i];
            n += e.Count;
            r += (double)e.R * e.Count; g += (double)e.G * e.Count; b += (double)e.B * e.Count;
        }
        return ((int)Math.Round(r / n), (int)Math.Round(g / n), (int)Math.Round(b / n));
    }

    /// <summary>k-means over the frequencies: every colour to its nearest palette colour, then the palette to the means.</summary>
    private static void Refine(Entry[] colors, List<(int R, int G, int B)> palette, int rounds, CancellationToken cancel)
    {
        var count = palette.Count;
        var pal = new int[count * 3];
        for (var round = 0; round < rounds; round++)
        {
            cancel.ThrowIfCancellationRequested();
            for (var p = 0; p < count; p++) (pal[p * 3], pal[p * 3 + 1], pal[p * 3 + 2]) = palette[p];
            var sums = new double[count * 4];
            foreach (var e in colors)
            {
                var p = Nearest(pal, count, e.R, e.G, e.B);
                sums[p * 4] += (double)e.R * e.Count;
                sums[p * 4 + 1] += (double)e.G * e.Count;
                sums[p * 4 + 2] += (double)e.B * e.Count;
                sums[p * 4 + 3] += e.Count;
            }
            for (var p = 0; p < count; p++)
                if (sums[p * 4 + 3] > 0)
                    palette[p] = ((int)Math.Round(sums[p * 4] / sums[p * 4 + 3]), (int)Math.Round(sums[p * 4 + 1] / sums[p * 4 + 3]),
                        (int)Math.Round(sums[p * 4 + 2] / sums[p * 4 + 3]));
        }
    }

    private static byte Nearest(int[] pal, int count, int r, int g, int b)
    {
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var p = 0; p < count; p++)
        {
            int dr = r - pal[p * 3], dg = g - pal[p * 3 + 1], db = b - pal[p * 3 + 2];
            var d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = p;
                if (d == 0) break;
            }
        }
        return (byte)best;
    }

    /// <summary>Floyd–Steinberg with strength <paramref name="strength"/>; the errors are limited per channel (no outliers at edges).</summary>
    private static void Dither(ReadOnlySpan<byte> rgb, int width, int height, int[] pal, int count, double strength, byte[] indices, CancellationToken cancel)
    {
        var cache = new short[1 << 15];
        Array.Fill(cache, (short)-1);
        var current = new float[(width + 2) * 3];
        var next = new float[(width + 2) * 3];
        for (var y = 0; y < height; y++)
        {
            if ((y & 63) == 0) cancel.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var o = (x + 1) * 3;
                int r = Math.Clamp((int)Math.Round(rgb[i * 3] + current[o]), 0, 255);
                int g = Math.Clamp((int)Math.Round(rgb[i * 3 + 1] + current[o + 1]), 0, 255);
                int b = Math.Clamp((int)Math.Round(rgb[i * 3 + 2] + current[o + 2]), 0, 255);
                var key = (r >> 3) << 10 | (g >> 3) << 5 | b >> 3;
                int index = cache[key];
                if (index < 0) cache[key] = (short)(index = Nearest(pal, count, r | 4, g | 4, b | 4));
                // Colours present exactly in the palette stay as they are (flat areas in graphics must not become noisy)
                var exact = rgb[i * 3] == pal[indices[i] * 3] && rgb[i * 3 + 1] == pal[indices[i] * 3 + 1] && rgb[i * 3 + 2] == pal[indices[i] * 3 + 2];
                if (exact) index = indices[i];
                indices[i] = (byte)index;
                for (var c = 0; c < 3; c++)
                {
                    var value = c == 0 ? r : c == 1 ? g : b;
                    var error = exact ? 0 : Math.Clamp((value - pal[index * 3 + c]) * (float)strength, -48f, 48f);
                    current[o + 3 + c] += error * 7 / 16;
                    next[o - 3 + c] += error * 3 / 16;
                    next[o + c] += error * 5 / 16;
                    next[o + 3 + c] += error * 1 / 16;
                }
            }
            (current, next) = (next, current);
            Array.Clear(next);
        }
    }
}
