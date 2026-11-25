namespace PdfKnead.Core.Pdf.Graphics.Icc;

/// <summary>Colorimetry: Lab and XYZ (D50), XYZ D50 → sRGB through the Bradford adaptation, chromatic adaptation of any white point.</summary>
internal static class IccColor
{
    // XYZ (D50) → linear sRGB and back, Bradford-adapted (Lindbloom)
    private static readonly float[] ToLinearSrgb =
    [
        3.1338561f, -1.6168667f, -0.4906146f,
        -0.9787684f, 1.9161415f, 0.0334540f,
        0.0719453f, -0.2289914f, 1.4052427f,
    ];

    private static readonly float[] FromLinearSrgb =
    [
        0.4360747f, 0.3850649f, 0.1430804f,
        0.2225045f, 0.7168786f, 0.0606169f,
        0.0139322f, 0.0971045f, 0.7141733f,
    ];

    /// <summary>Lab (D50) → XYZ (D50), result in <paramref name="xyz"/>[0…2].</summary>
    public static void LabToXyz(float l, float a, float b, Span<float> xyz)
    {
        var fy = (l + 16) / 116;
        var fx = fy + a / 500;
        var fz = fy - b / 200;
        xyz[0] = IccProfile.WhiteX * Inverse(fx);
        xyz[1] = IccProfile.WhiteY * Inverse(fy);
        xyz[2] = IccProfile.WhiteZ * Inverse(fz);

        static float Inverse(float t) => t > 6f / 29 ? t * t * t : 3 * (6f / 29) * (6f / 29) * (t - 4f / 29);
    }

    /// <summary>XYZ (D50) → Lab (D50).</summary>
    public static (float L, float A, float B) XyzToLab(float x, float y, float z)
    {
        float fx = F(x / IccProfile.WhiteX), fy = F(y / IccProfile.WhiteY), fz = F(z / IccProfile.WhiteZ);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));

        static float F(float t) => t > 216f / 24389 ? MathF.Cbrt(t) : (24389f / 27 * t + 16) / 116;
    }

    /// <summary>XYZ (D50) → sRGB 0…1, clamped.</summary>
    public static void XyzToSrgb(float x, float y, float z, Span<float> rgb)
    {
        var m = ToLinearSrgb;
        rgb[0] = Encode(m[0] * x + m[1] * y + m[2] * z);
        rgb[1] = Encode(m[3] * x + m[4] * y + m[5] * z);
        rgb[2] = Encode(m[6] * x + m[7] * y + m[8] * z);
    }

    /// <summary>XYZ (D50) → linear sRGB (not clamped, for combined matrices).</summary>
    public static void XyzToSrgbLinear(float x, float y, float z, Span<float> rgb)
    {
        var m = ToLinearSrgb;
        rgb[0] = m[0] * x + m[1] * y + m[2] * z;
        rgb[1] = m[3] * x + m[4] * y + m[5] * z;
        rgb[2] = m[6] * x + m[7] * y + m[8] * z;
    }

    /// <summary>sRGB 0…1 → XYZ (D50).</summary>
    public static void SrgbToXyz(float r, float g, float b, Span<float> xyz)
    {
        float lr = Decode(r), lg = Decode(g), lb = Decode(b);
        var m = FromLinearSrgb;
        xyz[0] = m[0] * lr + m[1] * lg + m[2] * lb;
        xyz[1] = m[3] * lr + m[4] * lg + m[5] * lb;
        xyz[2] = m[6] * lr + m[7] * lg + m[8] * lb;
    }

    /// <summary>Linear → sRGB-encoded value (sRGB transfer curve), clamped to 0–1; NaN gives 0.</summary>
    public static float Encode(float v)
    {
        if (!(v > 0)) return 0;
        if (v >= 1) return 1;
        return v <= 0.0031308f ? 12.92f * v : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;
    }

    /// <summary>sRGB-encoded → linear value (inverse sRGB transfer curve).</summary>
    public static float Decode(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    /// <summary>XYZ under the white point <paramref name="white"/> → XYZ under D50 (Bradford).</summary>
    public static void AdaptToD50(ReadOnlySpan<float> white, Span<float> xyz)
    {
        // Bradford cone space
        static (float, float, float) Cone(float x, float y, float z) =>
            (0.8951f * x + 0.2664f * y - 0.1614f * z, -0.7502f * x + 1.7135f * y + 0.0367f * z, 0.0389f * x - 0.0685f * y + 1.0296f * z);
        var (rs, gs, bs) = Cone(white[0], white[1], white[2]);
        var (rd, gd, bd) = Cone(IccProfile.WhiteX, IccProfile.WhiteY, IccProfile.WhiteZ);
        var (r, g, b) = Cone(xyz[0], xyz[1], xyz[2]);
        r *= rd / rs;
        g *= gd / gs;
        b *= bd / bs;
        // back (inverse Bradford matrix)
        xyz[0] = 0.9869929f * r - 0.1470543f * g + 0.1599627f * b;
        xyz[1] = 0.4323053f * r + 0.5183603f * g + 0.0492912f * b;
        xyz[2] = -0.0085287f * r + 0.0400428f * g + 0.9684867f * b;
    }
}

/// <summary>
/// Profile → sRGB for a rendering intent: the tables of the intent, black point compensation after ISO 18619 (otherwise the black of a print
/// profile would become dark grey), and for absolute colorimetric the media white of the profile (ICC.1 6.3.2.2, equations 4 to 6) instead.
/// Both are one affine map in the PCS (a factor and an offset per component), applied after the tables. Single colours exactly, with a cache;
/// 8-bit images through a grid (RGB 33³, CMYK 17⁴), tetrahedrally interpolated — built once per profile. Thread-safe.
/// </summary>
internal sealed class IccTransform
{
    private readonly IccProfile _profile;
    private readonly float[] _scale = [1, 1, 1], _offset = [0, 0, 0], _bp = [0, 0, 0];
    private readonly bool _absolute, _compensate;
    private readonly ColorCache _cache = new();
    private readonly Lazy<float[]?> _grid;

    /// <summary>
    /// The perceptual reference medium's black (ICC.1 6.3.3.2): 0.30911 % reflectance on a medium of 89 %, so Y = 0.30911 / 89 relative to the
    /// medium's white, neutral (the chromaticity of D50).
    /// </summary>
    internal const float PerceptualBlackY = 0.30911f / 89;

    /// <param name="profile">The profile (as read, or a variant of <see cref="IccProfile.ForIntent"/>).</param>
    /// <param name="compensateBlack">Black point compensation (never for absolute colorimetric: ISO 32000-2, 8.6.5.9).</param>
    /// <param name="intent">The rendering intent, 0 perceptual … 3 absolute colorimetric (ISO 32000-2, Table 69).</param>
    public IccTransform(IccProfile profile, bool compensateBlack = true, int intent = 1)
    {
        intent = intent is >= 0 and <= 3 ? intent : 1;
        _profile = profile.ForIntent(intent);
        _grid = new Lazy<float[]?>(BuildGrid, LazyThreadSafetyMode.ExecutionAndPublication);
        if (intent == 3)
        {
            // ICC-absolute (ICC.1 6.2.3, 6.3.2.2 equations 4–6): the media-relative values times media white / PCS white; the display as
            // destination has the PCS white as its media white (6.2.3 sentences 8–9), so nothing is divided out again. Without a usable wtpt
            // as relative colorimetric (docs/FEHLERGEDAECHTNIS.md, No. 1129)
            if (profile.AbsoluteScale is { } scale)
            {
                scale.CopyTo(_scale, 0);
                _absolute = true;
            }
            return;
        }
        Span<float> bp = stackalloc float[3];
        if (compensateBlack && DetectBlackPoint(profile, intent, bp))
        {
            // ISO 18619: a linear scaling in XYZ that keeps the PCS white and moves the source black onto the destination's — the display's
            // black is 0 — per component: X′ = (X − Xbp) · Xw / (Xw − Xbp)
            ReadOnlySpan<float> white = [IccProfile.WhiteX, IccProfile.WhiteY, IccProfile.WhiteZ];
            for (var i = 0; i < 3; i++)
            {
                _bp[i] = bp[i];
                _scale[i] = white[i] / (white[i] - bp[i]);
                _offset[i] = -bp[i] * _scale[i];
            }
            _compensate = true;
        }
    }

    /// <summary>
    /// The black point of a source profile for black point compensation, as ISO 18619 detects it (the procedure published for it; the PDF
    /// Association's AN001 only states the aim — the darkest colour of the source onto the darkest of the destination — and names ISO 18619;
    /// docs/FEHLERGEDAECHTNIS.md, No. 1129):
    /// <list type="bullet">
    /// <item>DeviceLink, abstract and named colour profiles and absolute colorimetric: none;</item>
    /// <item>version 4 or later, perceptual or saturation: the black of the perceptual reference medium (its tables are made for it), for a
    /// profile with matrix and curves the darkest colorant through the relative tables;</item>
    /// <item>output profiles of CMYK, relative colorimetric: the perceptual round trip (Lab 0/0/0 through BToA0, back through the relative
    /// tables), which discounts an ink limit the device black would exceed — without a readable BToA0 the darkest colorant;</item>
    /// <item>otherwise the darkest colorant (device black: 0 for grey and RGB, all channels 1 for CMY and CMYK) through the tables of the
    /// intent.</item>
    /// </list>
    /// The black found is made neutral (a* = b* = 0, its L* kept); a darkest colorant lighter than L* 50 (or below 0) counts as none, a round
    /// trip lighter than L* 50 as L* 50.
    /// </summary>
    /// <param name="profile">The profile (as read, or a variant).</param>
    /// <param name="intent">The rendering intent, 0 to 3.</param>
    /// <param name="xyz">The black point (XYZ, D50).</param>
    /// <returns>False where there is no black point to compensate (it is 0).</returns>
    internal static bool DetectBlackPoint(IccProfile profile, int intent, Span<float> xyz)
    {
        xyz.Clear();
        var root = profile.Root;
        if (root.DeviceClass is IccProfile.LinkClass or IccProfile.AbstractClass or IccProfile.NamedColorClass || intent is not (0 or 1 or 2)) return false;
        if (root.Version >= 4 && intent is 0 or 2)
        {
            if (root.HasShaperTags) return DarkestColorant(root.ForIntent(1), xyz);
            xyz[0] = PerceptualBlackY * IccProfile.WhiteX;
            xyz[1] = PerceptualBlackY * IccProfile.WhiteY;
            xyz[2] = PerceptualBlackY * IccProfile.WhiteZ;
            return true;
        }
        if (intent == 1 && root.DeviceClass == IccProfile.OutputClass && root.Space == IccProfile.CmykSpace && root.PerceptualRoundTripBlack(xyz))
        {
            var (l, _, _) = IccColor.XyzToLab(xyz[0], xyz[1], xyz[2]);
            l = float.IsNaN(l) ? 0 : Math.Clamp(l, 0, 50);
            IccColor.LabToXyz(l, 0, 0, xyz);
            return l > 0.01f;
        }
        return DarkestColorant(root.ForIntent(intent), xyz);
    }

    /// <summary>The darkest colorant through the tables of <paramref name="profile"/>, neutral; none for other colour spaces or above L* 50.</summary>
    private static bool DarkestColorant(IccProfile profile, Span<float> xyz)
    {
        xyz.Clear();
        if (profile.Space is not (IccProfile.RgbSpace or IccProfile.GraySpace or IccProfile.CmykSpace or IccProfile.CmySpace)) return false;
        Span<float> black = stackalloc float[profile.Channels];
        black.Fill(profile.Space is IccProfile.RgbSpace or IccProfile.GraySpace ? 0 : 1);
        profile.ToXyz(black, xyz);
        var (l, _, _) = IccColor.XyzToLab(xyz[0], xyz[1], xyz[2]);
        if (l is not (> 0.01f and <= 50))
        {
            xyz.Clear();
            return false;
        }
        IccColor.LabToXyz(l, 0, 0, xyz);
        return true;
    }

    /// <summary>The source profile (the variant of the intent).</summary>
    public IccProfile Profile => _profile;

    /// <summary>Number of device channels of the profile.</summary>
    public int Channels => _profile.Channels;

    /// <summary>The factors of the affine map in the PCS after the tables (black point compensation or ICC-absolute; otherwise 1).</summary>
    internal ReadOnlySpan<float> PcsScale => _scale;

    /// <summary>The offsets of the affine map in the PCS after the tables (black point compensation; otherwise 0).</summary>
    internal ReadOnlySpan<float> PcsOffset => _offset;

    /// <summary>Device values 0…1 → sRGB 0…1.</summary>
    public void ToSrgb(ReadOnlySpan<float> device, Span<float> rgb)
    {
        Span<float> xyz = stackalloc float[3];
        _profile.ToXyz(device, xyz);
        float x = xyz[0], y = xyz[1], z = xyz[2];
        if (_compensate)
        {
            x = (x - _bp[0]) * IccProfile.WhiteX / (IccProfile.WhiteX - _bp[0]);
            y = (y - _bp[1]) * IccProfile.WhiteY / (IccProfile.WhiteY - _bp[1]);
            z = (z - _bp[2]) * IccProfile.WhiteZ / (IccProfile.WhiteZ - _bp[2]);
        }
        else if (_absolute)
        {
            x *= _scale[0];
            y *= _scale[1];
            z *= _scale[2];
        }
        IccColor.XyzToSrgb(x, y, z, rgb);
    }

    /// <summary>Single colour (fills, text, shadings), cached for up to 6 channels.</summary>
    public Rgb ToRgb(ReadOnlySpan<double> components)
    {
        var n = _profile.Channels;
        var cacheable = n <= 6;
        long key = 0;
        for (var i = 0; i < Math.Min(n, 6); i++) key = key * 1031 + (long)Math.Round(Math.Clamp(i < components.Length ? components[i] : 0, 0, 1) * 1000);
        if (cacheable && _cache.TryGet(key, out var cached)) return cached;
        Span<float> device = stackalloc float[n];
        // the same values as in the key — clamped (docs/FEHLERGEDAECHTNIS.md, No. 272) and, when cached, rounded to its thousandths: an entry
        // stands for exactly the colour computed from it, so a colour does not depend on which value of its step came first (No. 400)
        for (var i = 0; i < n; i++)
        {
            var v = i < components.Length ? Math.Clamp(components[i], 0, 1) : 0;
            device[i] = (float)(cacheable ? Math.Round(v * 1000) / 1000 : v);
        }
        Span<float> rgb = stackalloc float[3];
        ToSrgb(device, rgb);
        var result = Rgb.FromUnit(rgb[0], rgb[1], rgb[2]);
        if (cacheable) _cache.Add(key, result);
        return result;
    }

    /// <summary>
    /// A single colour without the cache and its rounding to thousandths: for images drawn at their full depth — through the wide working space
    /// of JPEG 2000 that rounding grew to 2 steps near black (docs/FEHLERGEDAECHTNIS.md, No. 1092).
    /// </summary>
    public Rgb ToRgbExact(ReadOnlySpan<double> components)
    {
        var n = _profile.Channels;
        Span<float> device = stackalloc float[n];
        for (var i = 0; i < n; i++) device[i] = (float)(i < components.Length && !double.IsNaN(components[i]) ? Math.Clamp(components[i], 0, 1) : 0);
        Span<float> rgb = stackalloc float[3];
        ToSrgb(device, rgb);
        return Rgb.FromUnit(rgb[0], rgb[1], rgb[2]);
    }

    // ------------------------------------------------------------ 8-bit images

    /// <summary>Grid points per axis: 33 for three channels, 17 for four (as in common colour management modules).</summary>
    private int Points => Channels == 3 ? 33 : 17;

    /// <summary>Can convert 8-bit rows through the grid (three or four channels).</summary>
    public bool HasRow8 => Channels is 3 or 4;

    private float[]? BuildGrid()
    {
        if (!HasRow8) return null;
        var g = Points;
        var n = Channels;
        var count = n == 3 ? g * g * g : g * g * g * g;
        var grid = new float[count * 3];
        Parallel.For(0, count, i =>
        {
            Span<float> device = stackalloc float[4];
            var rest = i;
            // the last channel varies fastest; with CMYK, K is the slowest (outermost) so that each K level holds one C·M·Y cube
            if (n == 4)
            {
                device[3] = rest / (g * g * g) / (float)(g - 1);
                rest %= g * g * g;
            }
            device[0] = rest / (g * g) / (float)(g - 1);
            device[1] = rest / g % g / (float)(g - 1);
            device[2] = rest % g / (float)(g - 1);
            Span<float> rgb = stackalloc float[3];
            ToSrgb(device[..n], rgb);
            grid[i * 3] = rgb[0] * 255;
            grid[i * 3 + 1] = rgb[1] * 255;
            grid[i * 3 + 2] = rgb[2] * 255;
        });
        return grid;
    }

    /// <summary>One row with 8 bits per channel → RGBA (opaque); only if <see cref="HasRow8"/>. Missing input pixels become black.</summary>
    public void Row8(ReadOnlySpan<byte> row, int width, Span<byte> rgba)
    {
        var grid = _grid.Value!;
        var g = Points;
        var n = Channels;
        var scale = (g - 1) / 255f;
        for (int x = 0, s = 0, d = 0; x < width; x++, s += n, d += 4)
        {
            float r, gr, b;
            if (s + n > row.Length)
            {
                (r, gr, b) = (0, 0, 0);
            }
            else if (n == 3)
            {
                (r, gr, b) = Tetrahedral(grid, 0, g, row[s] * scale, row[s + 1] * scale, row[s + 2] * scale);
            }
            else
            {
                // CMYK: linear between the cubes of the two neighbouring K levels
                var k = row[s + 3] * scale;
                var k0 = Math.Min((int)k, g - 2);
                var fk = k - k0;
                var cube = g * g * g * 3;
                var (r0, g0, b0) = Tetrahedral(grid, k0 * cube, g, row[s] * scale, row[s + 1] * scale, row[s + 2] * scale);
                var (r1, g1, b1) = Tetrahedral(grid, (k0 + 1) * cube, g, row[s] * scale, row[s + 1] * scale, row[s + 2] * scale);
                (r, gr, b) = (r0 + (r1 - r0) * fk, g0 + (g1 - g0) * fk, b0 + (b1 - b0) * fk);
            }
            rgba[d] = (byte)Math.Clamp((int)(r + 0.5f), 0, 255);
            rgba[d + 1] = (byte)Math.Clamp((int)(gr + 0.5f), 0, 255);
            rgba[d + 2] = (byte)Math.Clamp((int)(b + 0.5f), 0, 255);
            rgba[d + 3] = 255;
        }
    }

    /// <summary>Tetrahedral interpolation in a cube with <paramref name="g"/> points per axis (values in grid steps).</summary>
    private static (float, float, float) Tetrahedral(float[] t, int origin, int g, float x, float y, float z)
    {
        int x0 = Math.Min((int)x, g - 2), y0 = Math.Min((int)y, g - 2), z0 = Math.Min((int)z, g - 2);
        float rx = x - x0, ry = y - y0, rz = z - z0;
        int sx = g * g * 3, sy = g * 3, sz = 3;
        var c000 = origin + x0 * sx + y0 * sy + z0 * sz;
        int c100 = c000 + sx, c010 = c000 + sy, c001 = c000 + sz;
        int c110 = c100 + sy, c101 = c100 + sz, c011 = c010 + sz, c111 = c110 + sz;
        int a, b;
        float w1, w2, w3;
        // tetrahedron chosen by the order of the fractions: c000 → a → b → c111
        if (rx >= ry)
        {
            if (ry >= rz) { a = c100; b = c110; w1 = rx; w2 = ry; w3 = rz; }
            else if (rx >= rz) { a = c100; b = c101; w1 = rx; w2 = rz; w3 = ry; }
            else { a = c001; b = c101; w1 = rz; w2 = rx; w3 = ry; }
        }
        else
        {
            if (rz >= ry) { a = c001; b = c011; w1 = rz; w2 = ry; w3 = rx; }
            else if (rz >= rx) { a = c010; b = c011; w1 = ry; w2 = rz; w3 = rx; }
            else { a = c010; b = c110; w1 = ry; w2 = rx; w3 = rz; }
        }
        float V(int corner, int k) => t[corner + k];
        float Mix(int k) => V(c000, k) + w1 * (V(a, k) - V(c000, k)) + w2 * (V(b, k) - V(a, k)) + w3 * (V(c111, k) - V(b, k));
        return (Mix(0), Mix(1), Mix(2));
    }
}
