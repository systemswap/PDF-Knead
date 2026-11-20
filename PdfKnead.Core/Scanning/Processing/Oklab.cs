namespace PdfKnead.Core.Scanning.Processing;

/// <summary>
/// The Oklab colour space (L lightness, a/b colour direction; chroma √(a² + b²)): more or less colour means changing only the chroma — hue
/// and perceived lightness stay (in RGB, blue would turn violet and yellow lighter).
/// </summary>
/// <remarks>
/// Matrices from B. Ottosson, „A perceptual color space for image processing“, 2020, https://bottosson.github.io/posts/oklab/ — public
/// domain or MIT (THIRD-PARTY-NOTICES.md, licenses/Oklab-MIT.txt). sRGB transfer function per IEC 61966-2-1.
/// </remarks>
internal static class Oklab
{
    private const int TableSize = 4096;
    private static readonly float[] ToLinearTable = Table(v => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4));
    private static readonly float[] ToSrgbTable = Table(v => v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055);

    private static float[] Table(Func<double, double> f)
    {
        var t = new float[TableSize + 1];
        for (var i = 0; i <= TableSize; i++) t[i] = (float)f((double)i / TableSize);
        return t;
    }

    private static float Lookup(float[] table, float v)
    {
        var f = Math.Clamp(v, 0, 1) * TableSize;
        var i = Math.Min((int)f, TableSize - 1);
        return table[i] + (table[i + 1] - table[i]) * (f - i);
    }

    /// <summary>sRGB-encoded (0 … 1) → Oklab.</summary>
    public static (float L, float A, float B) FromSrgb(float r, float g, float b)
    {
        var (lr, lg, lb) = (Lookup(ToLinearTable, r), Lookup(ToLinearTable, g), Lookup(ToLinearTable, b));
        var l = MathF.Cbrt(0.4122214708f * lr + 0.5363325363f * lg + 0.0514459929f * lb);
        var m = MathF.Cbrt(0.2119034982f * lr + 0.6806995451f * lg + 0.1073969566f * lb);
        var s = MathF.Cbrt(0.0883024619f * lr + 0.2817188376f * lg + 0.6299787005f * lb);
        return (0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s,
                1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s,
                0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s);
    }

    /// <summary>
    /// Oklab → sRGB-encoded (0 … 1). A colour outside sRGB loses chroma until it fits (bisection, 10 steps) — hue and lightness stay.
    /// </summary>
    public static (float R, float G, float B) ToSrgb(float L, float a, float b)
    {
        L = Math.Clamp(L, 0, 1);
        var lin = ToLinear(L, a, b);
        if (!InGamut(lin))
        {
            float lo = 0, hi = 1;
            for (var k = 0; k < 10; k++)
            {
                var mid = (lo + hi) / 2;
                if (InGamut(ToLinear(L, a * mid, b * mid))) lo = mid;
                else hi = mid;
            }
            lin = ToLinear(L, a * lo, b * lo);
        }
        return (Lookup(ToSrgbTable, lin.R), Lookup(ToSrgbTable, lin.G), Lookup(ToSrgbTable, lin.B));
    }

    /// <summary>The hue in degrees 0 … 360.</summary>
    public static float Hue(float a, float b)
    {
        var h = MathF.Atan2(b, a) * 180 / MathF.PI;
        return h < 0 ? h + 360 : h;
    }

    private static (float R, float G, float B) ToLinear(float L, float a, float b)
    {
        var l = L + 0.3963377774f * a + 0.2158037573f * b;
        var m = L - 0.1055613458f * a - 0.0638541728f * b;
        var s = L - 0.0894841775f * a - 1.2914855480f * b;
        (l, m, s) = (l * l * l, m * m * m, s * s * s);
        return (4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s,
               -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s,
               -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s);
    }

    private static bool InGamut((float R, float G, float B) c) =>
        c.R is >= -1e-4f and <= 1.0001f && c.G is >= -1e-4f and <= 1.0001f && c.B is >= -1e-4f and <= 1.0001f;
}
