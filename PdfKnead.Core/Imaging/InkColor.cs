namespace PdfKnead.Core.Imaging;

/// <summary>
/// Ink colour per density, modelled on scanned ballpoint pen writing: the chosen colour is the darkest tone (never darker, no black
/// mixed in); applied more thinly it becomes lighter but stays coloured (Beer–Lambert law per channel) instead of fading
/// to grey. It is drawn with "Multiply" (/BM /Multiply) at full opacity, so it glazes like real ink: the sheet,
/// its colour and lines underneath show through. See docs/FEHLERGEDAECHTNIS.md, No. 38.
/// </summary>
public static class InkColor
{
    /// <summary>Density from which the full chosen colour is used (in the reference ink sample 60 % of the dense downstrokes lie below it).</summary>
    public const double DarkestAt = 0.476;

    /// <summary>Typical density of the texture (median of the reference ink sample): how the signature looks on average.</summary>
    public const double Median = 0.333;

    /// <summary>Smallest and largest ink strength (slider in the signature window).</summary>
    public const double MinStrength = 0.5, MaxStrength = 2.5;

    /// <summary>Ink strength that new signatures start with (slightly denser than the reference ink sample).</summary>
    public const double DefaultStrength = 1.6;

    /// <summary>
    /// Builds a palette (R, G, B per level) for <paramref name="levels"/> levels: level 0 white, from <see cref="DarkestAt"/> on the colour
    /// <paramref name="rgb"/>. A <paramref name="strength"/> above 1 reaches the colour earlier (denser, more saturated), below 1 later.
    /// </summary>
    /// <param name="rgb">The chosen colour as 0xRRGGBB.</param>
    /// <param name="levels">Number of density levels (at least 2).</param>
    /// <param name="strength">Ink strength, clamped to <see cref="MinStrength"/> … <see cref="MaxStrength"/>.</param>
    /// <returns><paramref name="levels"/> × 3 bytes.</returns>
    public static byte[] Palette(int rgb, int levels, double strength = 1)
    {
        strength = Math.Clamp(strength, MinStrength, MaxStrength);
        var palette = new byte[levels * 3];
        for (var level = 0; level < levels; level++)
        {
            var t = Math.Min(1, level / (double)(levels - 1) / DarkestAt * strength);
            for (var c = 0; c < 3; c++)
                palette[level * 3 + c] = (byte)Math.Round(255 * Math.Pow(Math.Max((rgb >> (16 - 8 * c)) & 0xFF, 3) / 255.0, t));
        }
        return palette;
    }

    // ------------------------------------------------------------ Fine grain

    /// <summary>
    /// Extremely fine grain at pixel level on top of the texture (at 600 dpi ≈ 0.04 mm): the visible colour share varies by
    /// ± this fraction, so even saturated areas get tiny lighter dots and the stroke does not look smudged.
    /// </summary>
    public const double FineGrain = 0.35;

    /// <summary>Levels of the fine grain per pixel: image value = density level · 4 + grain level (64 · 4 = 256 colours).</summary>
    public const int GrainClasses = 4;

    // Grain value per pixel: noise per pixel (weight 1.4) plus noise per 2 × 2 block (0.6), divided into four equally populated
    // levels; the limits between the levels and the mean value per level (−1 … 1)
    private static readonly (double[] Limits, double[] Means) Grain = MeasureGrain();

    private static (double[] Limits, double[] Means) MeasureGrain()
    {
        // Distribution of 1.4 · a + 0.6 · b for uniformly distributed a, b in [−0.5, 0.5]
        const int Steps = 400;
        var values = new double[Steps * Steps];
        for (var i = 0; i < Steps; i++)
            for (var j = 0; j < Steps; j++) values[i * Steps + j] = 1.4 * ((i + 0.5) / Steps - 0.5) + 0.6 * ((j + 0.5) / Steps - 0.5);
        Array.Sort(values);
        var per = values.Length / GrainClasses;
        var limits = Enumerable.Range(1, GrainClasses - 1).Select(g => values[g * per]).ToArray();
        var means = Enumerable.Range(0, GrainClasses).Select(g => values.Skip(g * per).Take(per).Average()).ToArray();
        return (limits, means);
    }

    private static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352d;
        x ^= x >> 15;
        x *= 0x846ca68b;
        x ^= x >> 16;
        return x;
    }

    /// <summary>Returns the grain level (0 … <see cref="GrainClasses"/> − 1) of a pixel: fixed per position, independent of colour and strength.</summary>
    /// <param name="x">X of the pixel.</param>
    /// <param name="y">Y of the pixel.</param>
    /// <returns>The grain level.</returns>
    public static int GrainClass(int x, int y)
    {
        var a = Hash((uint)x * 0x9E3779B1u ^ Hash((uint)y + 77)) / (double)uint.MaxValue - 0.5;
        var b = Hash((uint)(x / 2) * 0x85EBCA6Bu ^ Hash((uint)(y / 2) + 78)) / (double)uint.MaxValue - 0.5;
        var v = 1.4 * a + 0.6 * b;
        var g = 0;
        while (g < Grain.Limits.Length && v >= Grain.Limits[g]) g++;
        return g;
    }

    /// <summary>
    /// Builds a palette with fine grain: entry <c>level · 4 + grain level</c>, colour as in <see cref="Palette"/>, with the colour share
    /// shifted by the grain (never darker than the chosen colour; paper stays white).
    /// </summary>
    /// <param name="rgb">The chosen colour as 0xRRGGBB.</param>
    /// <param name="levels">Number of density levels (at least 2).</param>
    /// <param name="strength">Ink strength, clamped to <see cref="MinStrength"/> … <see cref="MaxStrength"/>.</param>
    /// <returns><paramref name="levels"/> × <see cref="GrainClasses"/> × 3 bytes.</returns>
    public static byte[] GrainPalette(int rgb, int levels, double strength = 1)
    {
        strength = Math.Clamp(strength, MinStrength, MaxStrength);
        var palette = new byte[levels * GrainClasses * 3];
        for (var level = 0; level < levels; level++)
        {
            var t = Math.Min(1, level / (double)(levels - 1) / DarkestAt * strength);
            for (var g = 0; g < GrainClasses; g++)
            {
                var shifted = Math.Clamp(t * (1 + FineGrain * Grain.Means[g]), 0, 1);
                var at = (level * GrainClasses + g) * 3;
                for (var c = 0; c < 3; c++)
                    palette[at + c] = (byte)Math.Round(255 * Math.Pow(Math.Max((rgb >> (16 - 8 * c)) & 0xFF, 3) / 255.0, shifted));
            }
        }
        return palette;
    }

    /// <summary>
    /// Computes the density (0 to <paramref name="levels"/> − 1) of an ink image on a white or transparent background (the images of colour fonts): the Beer–Lambert law
    /// inverted, relative to the darkest ten percent as full ink; alpha is taken into account.
    /// </summary>
    /// <param name="image">The ink image.</param>
    /// <param name="levels">Number of density levels.</param>
    /// <returns>One density level per pixel; all zero if the image has no opaque pixels.</returns>
    public static byte[] Density(RgbaImage image, int levels)
    {
        var px = image.Pixels;
        var n = image.Width * image.Height;
        // Full ink: mean of the darkest 10 % of the opaque pixels (histogram over the lightness)
        var histogram = new int[256];
        var opaque = 0;
        for (var i = 0; i < n; i++)
        {
            if (px[i * 4 + 3] <= 240) continue;
            histogram[Luma(px, i)]++;
            opaque++;
        }
        var result = new byte[n];
        if (opaque == 0) return result;
        var limit = 0;
        for (int seen = 0; limit < 256 && seen < Math.Max(1, opaque / 10); limit++) seen += histogram[limit];
        double r = 0, g = 0, b = 0;
        var count = 0;
        for (var i = 0; i < n; i++)
        {
            if (px[i * 4 + 3] <= 240 || Luma(px, i) >= limit) continue;
            r += px[i * 4];
            g += px[i * 4 + 1];
            b += px[i * 4 + 2];
            count++;
        }
        Span<double> absorb = stackalloc double[3];
        absorb[0] = -Math.Log(Math.Max(r / Math.Max(1, count), 3) / 255.0);
        absorb[1] = -Math.Log(Math.Max(g / Math.Max(1, count), 3) / 255.0);
        absorb[2] = -Math.Log(Math.Max(b / Math.Max(1, count), 3) / 255.0);
        var norm = absorb[0] * absorb[0] + absorb[1] * absorb[1] + absorb[2] * absorb[2];
        if (norm <= 1e-9) return result;
        // Per channel −ln(value) ≈ density · absorption: least squares over the three channels
        var table = new double[256];
        for (var v = 0; v < 256; v++) table[v] = -Math.Log(Math.Max(v, 1) / 255.0);
        for (var i = 0; i < n; i++)
        {
            var alpha = px[i * 4 + 3];
            if (alpha == 0) continue;
            var d = (table[px[i * 4]] * absorb[0] + table[px[i * 4 + 1]] * absorb[1] + table[px[i * 4 + 2]] * absorb[2]) / norm;
            result[i] = (byte)Math.Clamp(Math.Round(Math.Clamp(d, 0, 1) * alpha / 255.0 * (levels - 1)), 0, levels - 1);
        }
        return result;
    }

    private static int Luma(byte[] px, int i) => (px[i * 4] * 77 + px[i * 4 + 1] * 150 + px[i * 4 + 2] * 29) >> 8;
}
