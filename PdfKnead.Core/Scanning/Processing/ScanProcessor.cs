using System.Buffers;

namespace PdfKnead.Core.Scanning.Processing;

/// <summary>
/// Applies the processing of a page (<see cref="ScanAdjustments"/>) to its raw scan, in 32-bit floating point (KONZEPT §5.25, „Verarbeitung
/// je Seite“). Order: relative to the paper (automatically improve, prevent show-through) → luminance → brightness and contrast → vibrance and
/// saturation → sharpen → greyscale or black and white. The raw scan is not changed; nothing to do returns it as it is.
/// </summary>
/// <remarks>
/// The whole page is looked at only for the paper and the black point; the rest is computed in bands of about 16 MB of floating point (with
/// an overlap of the reach of the sharpening, so the result does not depend on the bands) — a colour A4 page at 1200 dpi would otherwise
/// need several gigabytes.
/// </remarks>
public static class ScanProcessor
{
    // floating-point samples per band (16 MB)
    private const long BandSamples = 4L << 20;

    /// <summary>Processes a page.</summary>
    /// <param name="raw">The raw scan (grey or colour, 8 bit).</param>
    /// <param name="adjustments">The processing.</param>
    /// <param name="cancel">Stops the work (a slider moved on).</param>
    /// <returns>The processed page: same size and channels; with black and white one channel of 0 and 255.</returns>
    public static ScannedImage Apply(ScannedImage raw, ScanAdjustments adjustments, CancellationToken cancel = default) =>
        Apply(raw, adjustments, null, cancel, BandSamples);

    /// <summary>Processes a page whose back is known (both sides scanned): show-through is taken out with the back of the sheet.</summary>
    /// <param name="raw">The raw scan (straightened as processed).</param>
    /// <param name="adjustments">The processing.</param>
    /// <param name="back">The back of the same sheet (straightened); null: one side only.</param>
    /// <param name="cancel">Stops the work.</param>
    /// <returns>The processed page.</returns>
    public static ScannedImage Apply(ScannedImage raw, ScanAdjustments adjustments, ScannedImage? back, CancellationToken cancel = default) =>
        Apply(raw, adjustments, back, cancel, BandSamples);

    /// <summary>Processes a page in bands of the given size (tests: the result does not depend on the bands).</summary>
    internal static ScannedImage Apply(ScannedImage raw, ScanAdjustments adjustments, CancellationToken cancel, long bandSamples) =>
        Apply(raw, adjustments, null, cancel, bandSamples);

    private static ScannedImage Apply(ScannedImage raw, ScanAdjustments adjustments, ScannedImage? back, CancellationToken cancel, long bandSamples)
    {
        var a = adjustments.Normalized();
        if (a.LightIsNone || raw.BlackWhite && !a.BlackWhite && a.Sharpen == 0) return raw;
        var (w, h, c) = (raw.Width, raw.Height, raw.Channels);
        var paper = a.AutoEnhance || a.ShowThrough > 0 ? PaperBackground.Estimate(raw) : null;
        cancel.ThrowIfCancellationRequested();
        // with the back of the sheet the show-through is divided out where the back is printed (full at 60 %), the rest of the strength
        // goes to the lifting of pale tones on one side
        var sides = back is not null && a.ShowThrough > 0 ? ShowThroughBack.Prepare(raw, back, cancel) : null;
        var through = new Through(sides, (float)Math.Min(1, a.ShowThrough / 60), (float)(a.ShowThrough / 100 * (sides is null ? 1 : 0.5)));
        var low = a.AutoEnhance && paper is not null ? BlackPoint(raw, paper, through) : 0f;
        var curve = a.Brightness != 0 || a.Contrast != 0 ? BuildCurve(a.Brightness / 100, a.Contrast / 100) : null;
        // sharpening: radius about 0.1 mm (the width of a thin stroke), strength up to twice the edge; the bands overlap by its reach
        var sigma = Math.Max(0.6, raw.Dpi / 25.4 * 0.1);
        var margin = a.Sharpen > 0 ? 3 * BoxRadius(sigma) + 1 : 0;
        var single = a.BlackWhite || a.Grayscale;   // one channel out
        var colour = c == 3 && !single && (a.Saturation != 0 || a.Vibrance != 0);
        var output = new byte[(long)w * h * (single ? 1 : c)];
        var band = (int)Math.Clamp(bandSamples / Math.Max(1L, (long)w * c), 1, Math.Max(1, h));
        var options = new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4) };
        Parallel.For(0, (h + band - 1) / band, options, b =>
        {
            var (y0, y1) = (b * band, Math.Min(h, (b + 1) * band));
            var (top, bottom) = (Math.Max(0, y0 - margin), Math.Min(h, y1 + margin));
            var rows = bottom - top;
            var count = (long)rows * w * c;
            // the bands share their buffers through the pool: a large page would otherwise allocate gigabytes over its bands
            var px = ArrayPool<float>.Shared.Rent((int)count);   // 0 … 1
            try
            {
                var source = raw.Pixels;
                var from = (long)top * w * c;
                for (long i = 0; i < count; i++) px[i] = source[from + i] / 255f;
                if (paper is not null) Relative(px, top, rows, w, c, paper, a.AutoEnhance, through, low);
                if (a.Luminance != 0) Luminance(px, count, c, a.Luminance / 100);
                if (curve is not null) ToneCurve(px, count, curve);
                if (colour) Chroma(px, count, (float)(a.Saturation / 100), (float)(a.Vibrance / 100));
                if (a.Sharpen > 0) UnsharpMask(px, w, rows, c, (float)(a.Sharpen / 100 * 2), sigma, 2f / 255);
                options.CancellationToken.ThrowIfCancellationRequested();
                var skip = (long)(y0 - top) * w * c;
                if (a.BlackWhite)
                {
                    var threshold = (float)(a.Threshold / 100);
                    for (long p = (long)y0 * w, q = skip; p < (long)y1 * w; p++, q += c) output[p] = Lum(px, q, c) < threshold ? (byte)0 : (byte)255;
                }
                else if (a.Grayscale)
                {
                    for (long p = (long)y0 * w, q = skip; p < (long)y1 * w; p++, q += c) output[p] = (byte)(Math.Clamp(Lum(px, q, c), 0, 1) * 255 + 0.5f);
                }
                else
                {
                    for (long i = (long)y0 * w * c, q = skip; i < (long)y1 * w * c; i++, q++) output[i] = (byte)(Math.Clamp(px[q], 0, 1) * 255 + 0.5f);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(px);
            }
        });
        return new ScannedImage(w, h, single ? 1 : c, raw.Dpi, output, BlackWhite: a.BlackWhite);
    }

    /// <summary>
    /// The black point of the automatic improvement: the darkest half per cent of the page relative to its paper (after show-through), from
    /// every third sample of every third row; never above half the paper — a page without print stays as it is.
    /// </summary>
    private static float BlackPoint(ScannedImage raw, PaperBackground paper, Through through)
    {
        var (w, h, c) = (raw.Width, raw.Height, raw.Channels);
        var histogram = new long[1024];
        Span<float> rel = stackalloc float[3];
        for (var y = 0; y < h; y += 3)
            for (var x = 0; x < w; x += 3)
            {
                var at = ((long)y * w + x) * c;
                for (var ch = 0; ch < c; ch++) rel[ch] = raw.Pixels[at + ch] / 255f;
                ToPaper(rel, c, x, y, paper, through);
                histogram[Math.Clamp((int)(Lum(rel, c) * 800), 0, 1023)]++;
            }
        var total = histogram.Sum();
        var black = 0;
        for (long seen = 0; black < 1023; black++)
        {
            seen += histogram[black];
            if (seen >= total / 200) break;
        }
        return Math.Min(0.5f, black / 800f);
    }

    /// <summary>
    /// One sample relative to the paper at its place (shadows, spine and uneven light disappear from the relative values), with show-through
    /// removed: pale, colourless tones close to the paper are lifted to it, softly — real strokes are darker, highlighter and stamps are
    /// coloured and stay.
    /// </summary>
    private static void ToPaper(Span<float> rel, int c, int x, int y, PaperBackground paper, Through through)
    {
        var transmission = through.Back?.Transmission(x, y, through.BackAmount) ?? 1;
        for (var ch = 0; ch < c; ch++) rel[ch] = Math.Min(1.25f, rel[ch] * 255 / paper.At(x, y, ch) / transmission);
        var strength = through.Lift;
        if (strength <= 0) return;
        var lift = 1 - 0.4f * strength;   // tones above this share of the paper count as show-through at full strength
        var (lum, chroma) = c == 1 ? (rel[0], 0f) : (Lum(rel, c),
            Math.Max(rel[0], Math.Max(rel[1], rel[2])) - Math.Min(rel[0], Math.Min(rel[1], rel[2])));
        var weight = SmoothStep(lift, lift + (1 - lift) * 0.7f, lum) * (1 - SmoothStep(0.06f, 0.15f, chroma));
        for (var ch = 0; ch < c; ch++) rel[ch] += (1 - rel[ch]) * weight;
    }

    /// <summary>
    /// Works relative to the paper (see <see cref="ToPaper"/>). Automatically improve: the paper becomes white, the black point black, tones
    /// close to the paper run into white (paper grain and faint noise disappear); otherwise the result returns to the colour of the paper.
    /// </summary>
    private static void Relative(float[] px, int top, int rows, int w, int c, PaperBackground paper, bool autoEnhance, Through through, float low)
    {
        Parallel.For(0, rows, r =>
        {
            Span<float> rel = stackalloc float[3];
            var y = top + r;
            for (var x = 0; x < w; x++)
            {
                var at = ((long)r * w + x) * c;
                for (var ch = 0; ch < c; ch++) rel[ch] = px[at + ch];
                ToPaper(rel, c, x, y, paper, through);
                for (var ch = 0; ch < c; ch++)
                {
                    if (autoEnhance)
                    {
                        var v = Math.Clamp((rel[ch] - low) / (1 - low), 0, 1);
                        px[at + ch] = v + (1 - v) * SmoothStep(0.85f, 0.97f, v);
                    }
                    else px[at + ch] = Math.Min(1, rel[ch] * paper.At(x, y, ch) / 255);
                }
            }
        });
    }

    /// <summary>How show-through is taken out: with the back of the sheet (how much of it), and the lifting of pale tones on one side.</summary>
    private readonly record struct Through(ShowThroughBack? Back, float BackAmount, float Lift);

    /// <summary>
    /// Luminance as more or less light: in linear light, all channels in the same ratio (the colours keep their strength); the perceived
    /// lightness (the cube root of the linear luminance) is scaled and runs out softly above 0.8, so nothing clips hard — scaled so that
    /// white stays white (the paper of a scan must not turn grey when lightening).
    /// </summary>
    private static void Luminance(float[] px, long count, int c, double amount)
    {
        var factor = (float)Math.Max(0.05, 1 + amount);
        var white = factor > 1 ? Shoulder(factor) : 1;
        var pixels = count / c;
        Parallel.For(0, (pixels + 16383) / 16384, block =>
        {
            Span<float> lin = stackalloc float[3];
            for (long p = block * 16384L, end = Math.Min(pixels, p + 16384); p < end; p++)
            {
                var at = p * c;
                for (var ch = 0; ch < c; ch++) lin[ch] = ToLinear(px[at + ch]);
                var y = c == 1 ? lin[0] : 0.2126f * lin[0] + 0.7152f * lin[1] + 0.0722f * lin[2];
                if (y <= 1e-6f) continue;
                var l = MathF.Cbrt(y);
                var target = factor > 1 ? Shoulder(l * factor) / white : l * factor;
                var scale = MathF.Pow(target / l, 3);
                for (var ch = 0; ch < c; ch++) px[at + ch] = ToSrgb(Math.Min(1, lin[ch] * scale));
            }
        });
    }

    // a chroma at which a colour counts as strong (Oklab)
    private const float StrongChroma = 0.2f;

    /// <summary>Vibrance and saturation in Oklab: only the chroma changes, hue and lightness stay.</summary>
    private static void Chroma(float[] px, long count, float saturation, float vibrance)
    {
        var pixels = count / 3;
        Parallel.For(0, (pixels + 16383) / 16384, block =>
        {
            for (long p = block * 16384L, end = Math.Min(pixels, p + 16384); p < end; p++)
            {
                var at = p * 3;
                var (l, oa, ob) = Oklab.FromSrgb(px[at], px[at + 1], px[at + 2]);
                var factor = 1f;
                if (vibrance != 0) factor *= VibranceFactor(l, oa, ob, vibrance);
                if (saturation != 0) factor *= 1 + saturation;
                (px[at], px[at + 1], px[at + 2]) = Oklab.ToSrgb(l, oa * factor, ob * factor);
            }
        });
    }

    /// <summary>
    /// Vibrance: pale colours are boosted strongly, strong colours hardly, skin tones are spared; towards the left pale colours turn grey
    /// first, strong ones last longer.
    /// </summary>
    /// <returns>The factor for the chroma (1 = unchanged).</returns>
    internal static float VibranceFactor(float l, float a, float b, float vibrance)
    {
        var chroma = MathF.Sqrt(a * a + b * b);
        var sat = Math.Clamp(chroma / StrongChroma, 0, 1);
        if (vibrance < 0) return Math.Max(0, 1 + vibrance * (1 - 0.6f * sat));
        var room = MathF.Pow(1 - sat, 1.5f);   // pale: much headroom
        return 1 + 1.5f * vibrance * room * (1 - 0.75f * SkinTone(l, a, b, chroma));
    }

    /// <summary>How much a colour looks like skin (0 … 1): hue around 55° (orange-reddish), not too dark, moderately coloured.</summary>
    private static float SkinTone(float l, float a, float b, float chroma)
    {
        if (chroma < 1e-3f) return 0;
        var onHue = Math.Clamp(1 - MathF.Abs(Oklab.Hue(a, b) - 55) / 30, 0, 1);
        var onChroma = 1 - SmoothStep(0.12f, 0.18f, chroma);   // very strong is not skin
        var onLight = SmoothStep(0.25f, 0.4f, l);
        return onHue * onChroma * onLight;
    }

    private const int CurveSize = 4096;

    /// <summary>Brightness as a gamma on the midtones (black and white stay), contrast as an S-curve around the middle — as a table.</summary>
    internal static float[] BuildCurve(double brightness, double contrast)
    {
        var gamma = Math.Pow(2, -0.8 * Math.Clamp(brightness, -1, 1));
        var p = Math.Pow(2, 1.2 * Math.Clamp(contrast, -1, 1));
        var curve = new float[CurveSize];
        for (var i = 0; i < CurveSize; i++)
        {
            var x = Math.Pow((double)i / (CurveSize - 1), gamma);
            x = x < 0.5 ? 0.5 * Math.Pow(2 * x, p) : 1 - 0.5 * Math.Pow(2 - 2 * x, p);
            curve[i] = (float)x;
        }
        return curve;
    }

    private static void ToneCurve(float[] px, long count, float[] curve) =>
        Parallel.For(0, (count + 65535) / 65536, block =>
        {
            for (long i = block * 65536L, end = Math.Min(count, i + 65536); i < end; i++)
            {
                var f = Math.Clamp(px[i], 0, 1) * (CurveSize - 1);
                var k = Math.Min((int)f, CurveSize - 2);
                px[i] = curve[k] + (curve[k + 1] - curve[k]) * (f - k);
            }
        });

    /// <summary>Unsharp mask: original plus amount × (original − blurred), only where the difference exceeds the threshold.</summary>
    private static void UnsharpMask(float[] px, int w, int h, int c, float amount, double sigma, float threshold)
    {
        var count = (long)w * h * c;
        var blur = ArrayPool<float>.Shared.Rent((int)count);
        try
        {
            Array.Copy(px, blur, count);
            GaussApprox(blur, w, h, c, sigma);
            Parallel.For(0, h, y =>
            {
                for (long i = (long)y * w * c, end = i + (long)w * c; i < end; i++)
                {
                    var diff = px[i] - blur[i];
                    if (MathF.Abs(diff) >= threshold) px[i] = Math.Clamp(px[i] + amount * diff, 0, 1);
                }
            });
        }
        finally
        {
            ArrayPool<float>.Shared.Return(blur);
        }
    }

    /// <summary>A Gaussian blur approximated by three box filters per direction (box width from the sigma).</summary>
    private static void GaussApprox(float[] px, int w, int h, int c, double sigma)
    {
        var radius = BoxRadius(sigma);
        for (var pass = 0; pass < 3; pass++)
        {
            BoxRows(px, w, h, c, radius);
            BoxColumns(px, w, h, c, radius);
        }
    }

    /// <summary>The radius of each of the three box filters that together approximate a Gaussian of this sigma.</summary>
    private static int BoxRadius(double sigma) => Math.Max(1, (int)Math.Round(Math.Sqrt(12 * sigma * sigma / 3 + 1) / 2));

    private static void BoxRows(float[] px, int w, int h, int c, int r) =>
        Parallel.For(0, h, () => new float[w], (y, _, line) =>
        {
            for (var ch = 0; ch < c; ch++)
            {
                var row = (long)y * w * c + ch;
                var sum = 0f;
                for (var x = -r; x <= r; x++) sum += px[row + Math.Clamp(x, 0, w - 1) * c];
                for (var x = 0; x < w; x++)
                {
                    line[x] = sum / (2 * r + 1);
                    sum += px[row + Math.Min(w - 1, x + r + 1) * c] - px[row + Math.Max(0, x - r) * c];
                }
                for (var x = 0; x < w; x++) px[row + x * c] = line[x];
            }
            return line;
        }, _ => { });

    private static void BoxColumns(float[] px, int w, int h, int c, int r) =>
        Parallel.For(0, w, () => new float[h], (x, _, line) =>
        {
            for (var ch = 0; ch < c; ch++)
            {
                long At(int y) => ((long)Math.Clamp(y, 0, h - 1) * w + x) * c + ch;
                var sum = 0f;
                for (var y = -r; y <= r; y++) sum += px[At(y)];
                for (var y = 0; y < h; y++)
                {
                    line[y] = sum / (2 * r + 1);
                    sum += px[At(y + r + 1)] - px[At(y - r)];
                }
                for (var y = 0; y < h; y++) px[At(y)] = line[y];
            }
            return line;
        }, _ => { });

    private static float Lum(float[] px, long at, int c) => c == 1 ? px[at] : 0.299f * px[at] + 0.587f * px[at + 1] + 0.114f * px[at + 2];

    private static float Lum(ReadOnlySpan<float> px, int c) => c == 1 ? px[0] : 0.299f * px[0] + 0.587f * px[1] + 0.114f * px[2];

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static float Shoulder(float l)
    {
        const float knee = 0.8f;
        return l <= knee ? Math.Max(0, l) : knee + (1 - knee) * (1 - MathF.Exp(-(l - knee) / (1 - knee)));
    }

    // sRGB transfer function (IEC 61966-2-1)
    private static float ToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    private static float ToSrgb(float v) => v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;
}
