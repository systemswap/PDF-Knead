using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The colour definitions PDF-Knead builds itself — ROMM RGB (<see cref="CalibratedProfile.RommRgb"/>, ISO 22028-2), sRGB
/// (<see cref="CalibratedProfile.Srgb"/>, <see cref="CalibratedProfile.Primaries"/> for the BT.709 primaries under D65, and the sRGB curve of
/// the JPEG 2000 colour arithmetic, <see cref="JpxParameterized.Encode"/>) — against the profiles the ICC publishes for them under its free
/// licence (ICC v4: ISO22028-2_ROMM-RGB.icc and sRGB_v4_ICC_preference.icc, kept locally in Normen-ISO/farbe-icc, not in Git). The profiles
/// are read at run time, their tags directly and their transforms through PDF-Knead's own ICC path; without them the tests are skipped.
/// <para>
/// What the reference profiles add on purpose is taken out before comparing, and checked on its own: the ROMM profile maps code 0 to the
/// black of the perceptual reference medium of ICC v4 (an offset in its matrix, white not scaled); the colorimetric transform of the sRGB
/// profile (A2B1) carries a neutral flare of 1.25 % of white (black at 0.0125·D50, white kept: XYZ = (1 − e)·XYZ₀ + e·D50). The perceptual
/// tables of the sRGB profile are a preference rendering and are not compared.
/// </para>
/// </summary>
public sealed class IccReferenceProfileTests
{
    /// <summary>The PCS XYZ of a lutAtoBType: 1.0 of the curves is 0xFFFF, 1.0 of XYZ 0x8000 (u1Fixed15).</summary>
    private const double PcsXyz = 65535.0 / 32768;

    /// <summary>The PCS white, D50 (as PDF-Knead's profiles write it).</summary>
    private static readonly double[] D50 = [IccProfile.WhiteX, IccProfile.WhiteY, IccProfile.WhiteZ];

    /// <summary>The black point of the perceptual reference medium gamut of ICC v4 (ICC.1, the perceptual intent).</summary>
    private static readonly double[] PerceptualBlack = [0.00336, 0.0034731, 0.00287];

    // ---- reading the reference profiles (tags directly)

    private static byte[] Tag(byte[] profile, string signature)
    {
        var count = BinaryPrimitives.ReadInt32BigEndian(profile.AsSpan(128));
        for (var i = 0; i < count; i++)
        {
            var entry = profile.AsSpan(132 + 12 * i, 12);
            if (System.Text.Encoding.ASCII.GetString(entry[..4]) != signature) continue;
            var offset = BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
            var size = BinaryPrimitives.ReadInt32BigEndian(entry[8..]);
            return profile.AsSpan(offset, size).ToArray();
        }
        throw new InvalidDataException(signature);
    }

    private static double S15(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadInt32BigEndian(d[at..]) / 65536.0;

    /// <summary>An XYZType tag.</summary>
    private static double[] Xyz(byte[] tag) => [S15(tag, 8), S15(tag, 12), S15(tag, 16)];

    /// <summary>The matrix element of a lutAtoBType: the 3 × 3 matrix (rows X, Y, Z) and the offsets, in PCS XYZ.</summary>
    private static (double[,] Matrix, double[] Offset) LutMatrix(byte[] lut)
    {
        var at = BinaryPrimitives.ReadInt32BigEndian(lut.AsSpan(16));
        var m = new double[3, 3];
        for (var i = 0; i < 9; i++) m[i / 3, i % 3] = S15(lut, at + 4 * i) * PcsXyz;
        return (m, [S15(lut, at + 36) * PcsXyz, S15(lut, at + 40) * PcsXyz, S15(lut, at + 44) * PcsXyz]);
    }

    /// <summary>The first M curve of a lutAtoBType: function type and parameters of a parametricCurveType.</summary>
    private static (int Type, double[] P) FirstMCurve(byte[] lut)
    {
        var at = BinaryPrimitives.ReadInt32BigEndian(lut.AsSpan(20));
        Assert.Equal("para", System.Text.Encoding.ASCII.GetString(lut, at, 4));
        var type = BinaryPrimitives.ReadUInt16BigEndian(lut.AsSpan(at + 8));
        var count = new[] { 1, 3, 4, 5, 7 }[type];
        return (type, [.. Enumerable.Range(0, count).Select(k => S15(lut, at + 12 + 4 * k))]);
    }

    /// <summary>The colorants (columns, D50) and the first tone curve table of one of PDF-Knead's matrix profiles.</summary>
    private static (double[,] Colorants, ushort[] Curve) Own(byte[] profile)
    {
        var m = new double[3, 3];
        string[] tags = ["rXYZ", "gXYZ", "bXYZ"];
        for (var c = 0; c < 3; c++)
        {
            var xyz = Xyz(Tag(profile, tags[c]));
            for (var r = 0; r < 3; r++) m[r, c] = xyz[r];
        }
        var trc = Tag(profile, "rTRC");
        var n = BinaryPrimitives.ReadInt32BigEndian(trc.AsSpan(8));
        return (m, [.. Enumerable.Range(0, n).Select(i => BinaryPrimitives.ReadUInt16BigEndian(trc.AsSpan(12 + 2 * i)))]);
    }

    private static double DeltaE76(float[] a, float[] b)
    {
        var (l1, a1, b1) = IccColor.XyzToLab(a[0], a[1], a[2]);
        var (l2, a2, b2) = IccColor.XyzToLab(b[0], b[1], b[2]);
        return Math.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    /// <summary>The 17³ codes 0, 1/16 … 1 of an RGB profile.</summary>
    private static IEnumerable<float[]> Grid() =>
        from r in Enumerable.Range(0, 17) from g in Enumerable.Range(0, 17) from b in Enumerable.Range(0, 17) select new[] { r / 16f, g / 16f, b / 16f };

    // ---- ROMM RGB

    /// <summary>
    /// ROMM RGB: the colorants PDF-Knead derives from the chromaticities of ISO 22028-2 (R 0.7347/0.2653, G 0.1596/0.8404, B 0.0366/0.0001,
    /// white D50) against the matrix of the reference profile (its A2B0, PCS XYZ). Red, green and the X of blue agree to 0.0005 (the reference
    /// gives four decimals' worth; its s15Fixed16 steps are 3·10⁻⁵ in PCS XYZ). The Y and Z of blue do not: the reference has 0.00104 and
    /// 0.82410 where the chromaticities give 0.0000855 and 0.8249 (blue y 0.0012 instead of 0.0001, its white Y 1.0009, Z 0.8241) — a
    /// deviation of the reference profile, pinned here so that it is noticed if the file changes; PDF-Knead keeps to the chromaticities. The
    /// offsets of the matrix are the perceptual reference medium black of ICC v4; the media white tag of the reference is 0.89·D50 (the
    /// reference medium white of ROMM RGB) — relative rendering, which is all PDF-Knead does with an image profile, does not use it.
    /// </summary>
    [ReferenceIccProfileFact]
    public void RommRgb_ColorantsAgainstTheReferenceProfile()
    {
        var reference = ReferenceIccProfileFactAttribute.Read("ISO22028-2_ROMM-RGB.icc")!;
        var (matrix, offset) = LutMatrix(Tag(reference, "A2B0"));
        var (own, _) = Own(CalibratedProfile.RommRgb());
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
            {
                if (c == 2 && r > 0) continue;
                Assert.True(Math.Abs(matrix[r, c] - own[r, c]) <= 0.0005, $"[{r},{c}]: Referenz {matrix[r, c]:F6}, eigen {own[r, c]:F6}");
            }
        // blue: PDF-Knead from the chromaticities, the reference with its own Y and Z
        Assert.Equal(0.0000855, own[1, 2], 0.00002);
        Assert.Equal(0.8249, own[2, 2], 0.0001);
        Assert.InRange(matrix[1, 2], 0.0010, 0.0011);
        Assert.InRange(matrix[2, 2], 0.8240, 0.8242);
        for (var i = 0; i < 3; i++) Assert.Equal(PerceptualBlack[i], offset[i], 0.00003);
        var media = Xyz(Tag(reference, "wtpt"));
        for (var i = 0; i < 3; i++) Assert.Equal(0.89, media[i] / D50[i], 0.0005);
        var ownWhite = Xyz(Tag(CalibratedProfile.RommRgb(), "wtpt"));
        for (var i = 0; i < 3; i++) Assert.Equal(D50[i], ownWhite[i], 0.0001);
    }

    /// <summary>
    /// ROMM RGB: the tone curve (encoded → linear) of the reference profile is the parametric function 3 with γ 1.8, a 1, b 0, c 1/16 and the
    /// break at d 0.03125 — E′/16 below, E′^1.8 above, as PDF-Knead builds it; PDF-Knead's table of 4 096 points lies within one 16-bit step
    /// of that function at every point (its own rounding half a step, γ of the reference 1.800003 another 0.1).
    /// </summary>
    [ReferenceIccProfileFact]
    public void RommRgb_ToneCurveAgainstTheReferenceProfile()
    {
        var reference = ReferenceIccProfileFactAttribute.Read("ISO22028-2_ROMM-RGB.icc")!;
        var (type, p) = FirstMCurve(Tag(reference, "A2B0"));
        Assert.Equal(3, type);
        Assert.Equal(1.8, p[0], 0.00001);
        Assert.Equal(1.0, p[1], 0.00001);
        Assert.Equal(0.0, p[2], 0.00001);
        Assert.Equal(1 / 16.0, p[3], 0.00001);
        Assert.Equal(0.03125, p[4], 0.00001);
        var (_, curve) = Own(CalibratedProfile.RommRgb());
        Assert.Equal(4096, curve.Length);
        for (var i = 0; i < curve.Length; i++)
        {
            var x = i / 4095.0;
            var expected = x >= p[4] ? Math.Pow(p[1] * x + p[2], p[0]) : p[3] * x;
            Assert.True(Math.Abs(curve[i] / 65535.0 - expected) <= 1 / 65535.0, $"Stelle {i}: {curve[i] / 65535.0:F7} gegen {expected:F7}");
        }
    }

    /// <summary>
    /// ROMM RGB: 17³ codes drawn through both profiles on PDF-Knead's ICC path, the black offset of the reference taken out (its matrix adds it
    /// to every colour, so it is subtracted in PCS XYZ). Where blue does not exceed red or green, the colours agree to ΔE76 0.5 (measured
    /// 0.43; what is left comes from the blue column of the reference, see <see cref="RommRgb_ColorantsAgainstTheReferenceProfile"/>); over
    /// all codes to ΔE76 4.2 — the largest difference, 4.08, is pure blue, whose Y the reference makes 0.00095 larger (L* 0.08 against 0.94).
    /// </summary>
    [ReferenceIccProfileFact]
    public void RommRgb_DrawnColoursAgainstTheReferenceProfile()
    {
        var reference = IccProfile.Parse(ReferenceIccProfileFactAttribute.Read("ISO22028-2_ROMM-RGB.icc")!)!;
        var own = IccProfile.Parse(CalibratedProfile.RommRgb())!;
        var black = new float[3];
        reference.ToXyz([0f, 0f, 0f], black);
        for (var i = 0; i < 3; i++) Assert.Equal(PerceptualBlack[i], black[i], 0.00005);
        float[] a = new float[3], b = new float[3];
        double most = 0, mostMixed = 0;
        foreach (var code in Grid())
        {
            own.ToXyz(code, a);
            reference.ToXyz(code, b);
            for (var i = 0; i < 3; i++) b[i] -= black[i];
            var delta = DeltaE76(a, b);
            most = Math.Max(most, delta);
            if (code[2] <= Math.Max(code[0], code[1])) mostMixed = Math.Max(mostMixed, delta);
        }
        Assert.True(mostMixed <= 0.5, $"ΔE76 {mostMixed:F3} (Blau höchstens Rot oder Grün)");
        Assert.True(most <= 4.2, $"ΔE76 {most:F3}");
        own.ToXyz([0f, 0f, 1f], a);
        reference.ToXyz([0f, 0f, 1f], b);
        Assert.Equal(0.00095, b[1] - black[1] - a[1], 0.00005);
    }

    // ---- sRGB

    /// <summary>
    /// The flare of the colorimetric transform of the sRGB reference profile: black at e·D50 with e = 0.0125 (1/80 of white), the white D50
    /// itself — so XYZ = (1 − e)·XYZ₀ + e·D50, and XYZ₀ = (XYZ − e·D50) / (1 − e) is the sRGB colorimetry without it.
    /// </summary>
    private static (IccProfile Profile, double Flare) SrgbReference()
    {
        var profile = IccProfile.Parse(ReferenceIccProfileFactAttribute.Read("sRGB_v4_ICC_preference.icc")!)!.ForIntent(1);
        var black = new float[3];
        profile.ToXyz([0f, 0f, 0f], black);
        var flare = black[1];
        Assert.InRange(flare, 0.0124, 0.0126);
        for (var i = 0; i < 3; i++) Assert.Equal(flare, black[i] / D50[i], 0.0001);
        var white = new float[3];
        profile.ToXyz([1f, 1f, 1f], white);
        for (var i = 0; i < 3; i++) Assert.Equal(D50[i], white[i], 0.0002);
        return (profile, flare);
    }

    private static void WithoutFlare(float[] xyz, double flare)
    {
        for (var i = 0; i < 3; i++) xyz[i] = (float)((xyz[i] - flare * D50[i]) / (1 - flare));
    }

    /// <summary>
    /// sRGB: the colorants of <see cref="CalibratedProfile.Srgb"/> and of <see cref="CalibratedProfile.Primaries"/> for the BT.709 primaries
    /// under D65 (adapted to D50 with Bradford) against those of the reference profile (its A2B1 at pure red, green and blue, without the
    /// flare) — to 0.0003 in XYZ, a tenth of what moves an 8-bit step of a saturated primary.
    /// </summary>
    [ReferenceIccProfileFact]
    public void Srgb_ColorantsAgainstTheReferenceProfile()
    {
        var (reference, flare) = SrgbReference();
        var (srgb, _) = Own(CalibratedProfile.Srgb());
        var (bt709, _) = Own(CalibratedProfile.Primaries((0.640, 0.330), (0.300, 0.600), (0.150, 0.060), (0.3127, 0.3290), "BT.709"));
        var xyz = new float[3];
        for (var c = 0; c < 3; c++)
        {
            var code = new float[3];
            code[c] = 1;
            reference.ToXyz(code, xyz);
            WithoutFlare(xyz, flare);
            for (var r = 0; r < 3; r++)
            {
                Assert.True(Math.Abs(xyz[r] - srgb[r, c]) <= 0.0003, $"sRGB [{r},{c}]: Referenz {xyz[r]:F6}, eigen {srgb[r, c]:F6}");
                Assert.True(Math.Abs(xyz[r] - bt709[r, c]) <= 0.0003, $"BT.709 [{r},{c}]: Referenz {xyz[r]:F6}, eigen {bt709[r, c]:F6}");
            }
        }
    }

    /// <summary>
    /// sRGB: 17³ codes drawn through <see cref="CalibratedProfile.Srgb"/> and through the profile of the BT.709 primaries against the
    /// colorimetric transform of the reference profile without its flare, on PDF-Knead's ICC path: ΔE76 at most 0.1 (measured 0.03) —
    /// the same colours.
    /// </summary>
    [ReferenceIccProfileFact]
    public void Srgb_DrawnColoursAgainstTheReferenceProfile()
    {
        var (reference, flare) = SrgbReference();
        foreach (var own in new[]
                 {
                     CalibratedProfile.Srgb(),
                     CalibratedProfile.Primaries((0.640, 0.330), (0.300, 0.600), (0.150, 0.060), (0.3127, 0.3290), "BT.709"),
                 })
        {
            var profile = IccProfile.Parse(own)!;
            float[] a = new float[3], b = new float[3];
            double most = 0;
            foreach (var code in Grid())
            {
                profile.ToXyz(code, a);
                reference.ToXyz(code, b);
                WithoutFlare(b, flare);
                most = Math.Max(most, DeltaE76(a, b));
            }
            Assert.True(most <= 0.1, $"ΔE76 {most:F3}");
        }
    }

    /// <summary>
    /// The sRGB curve of the JPEG 2000 colour arithmetic and of the colour code: for every 8-bit value v the reference profile's luminance of the
    /// grey v/255 (without the flare) is written back as v by <see cref="JpxParameterized.Encode"/> — the two curves are closer than half an
    /// 8-bit step everywhere —, and <see cref="IccColor.Decode"/> gives that luminance to 2·10⁻⁴.
    /// </summary>
    [ReferenceIccProfileFact]
    public void Srgb_ToneCurveAgainstTheReferenceProfile()
    {
        var (reference, flare) = SrgbReference();
        var xyz = new float[3];
        for (var v = 0; v < 256; v++)
        {
            var e = v / 255f;
            reference.ToXyz([e, e, e], xyz);
            WithoutFlare(xyz, flare);
            Assert.Equal(v, JpxParameterized.Encode(xyz[1]));
            Assert.Equal(xyz[1], IccColor.Decode(e), 0.0002);
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that is skipped when the ICC reference profiles (Normen-ISO/farbe-icc/ISO22028-2_ROMM-RGB.icc and
/// sRGB_v4_ICC_preference.icc, kept locally only, not in Git) cannot be found from the source file.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ReferenceIccProfileFactAttribute : FactAttribute
{
    private static readonly string[] Names = ["ISO22028-2_ROMM-RGB.icc", "sRGB_v4_ICC_preference.icc"];

    /// <summary>Marks the test as skipped if a profile is missing.</summary>
    /// <param name="source">Path of the test's source file (filled in by the compiler).</param>
    public ReferenceIccProfileFactAttribute([CallerFilePath] string source = "")
    {
        if (Names.Any(n => Path(n, source) is null))
            Skip = "Normen-ISO/farbe-icc: the ICC reference profiles are kept locally only, not in the repository.";
    }

    /// <summary>The path of a reference profile; null if it cannot be found.</summary>
    /// <param name="name">The file name in Normen-ISO/farbe-icc.</param>
    /// <param name="source">Path of the calling source file (filled in by the compiler).</param>
    public static string? Path(string name, [CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(System.IO.Path.Combine(dir.FullName, "PdfKnead.sln"))) continue;
            var file = System.IO.Path.Combine(dir.FullName, "Normen-ISO", "farbe-icc", name);
            return File.Exists(file) ? file : null;
        }
        return null;
    }

    /// <summary>The bytes of a reference profile; null if it cannot be found.</summary>
    /// <param name="name">The file name in Normen-ISO/farbe-icc.</param>
    /// <param name="source">Path of the calling source file (filled in by the compiler).</param>
    public static byte[]? Read(string name, [CallerFilePath] string source = "") => Path(name, source) is { } file ? File.ReadAllBytes(file) : null;
}
