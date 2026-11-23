using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics.Icc;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Colour of the JPEG 2000 decoder beyond SDR and beyond the primaries (23.11.2026): PQ and HLG brought onto the page with the reference white
/// of ITU-R BT.2408 and the highlight roll-off of its Annex 5, ICtCp with PQ, HLG and an SDR characteristic (docs/FEHLERGEDAECHTNIS.md,
/// No. 1055); the extended characteristics, e-sRGB and e-sYCC in a wide working space where an image has colours beyond the primaries
/// (No. 1056); sYCC, e-sYCC, YCbCr and e-sRGB from their full codes to one 8-bit step (No. 1057). The components were written forward from
/// chosen light by the equations of ITU-T H.273 and PIMA 7667, and the expected output computed apart from the decoder (the light read back
/// from the codes, the PQ EOTF, the HLG OOTF, the EETF steps 1 to 5, the matrix to the BT.2020 primaries, the sRGB curve).
/// </summary>
public sealed partial class JpxColourTests
{
    /// <summary>The 8-bit red of an e-sRGB pixel whose three channels have the code <paramref name="value"/> of <paramref name="bits"/> bits.</summary>
    private static byte ESrgbLevel(long value, int bits)
    {
        var pixel = new byte[3];
        JpxParameterized.Enumerated(20)!.Pixel([value, value, value], [bits, bits, bits], pixel);
        return pixel[0];
    }

    /// <summary>The cases of this file (4 × 2 pixels each), as <see cref="H273Cases"/>.</summary>
    private static readonly Dictionary<string, H273Case> ColourCases = new()
    {
        ["pq"] = new(9, 16, 9, false, [10, 10, 10],
            [[64, 397, 490, 573, 723, 940, 481, 668], [512, 512, 512, 512, 512, 512, 455, 425], [512, 512, 512, 512, 512, 512, 557, 596]],
            [0, 0, 0, 100, 100, 100, 169, 169, 169, 229, 229, 229, 255, 255, 255, 255, 255, 255, 214, 148, 88, 255, 249, 185]),
        ["hlg"] = new(9, 18, 9, false, [10, 10, 10],
            [[64, 279, 493, 721, 940, 828, 581, 811], [512, 512, 512, 512, 512, 512, 374, 365], [512, 512, 512, 512, 512, 512, 624, 590]],
            [0, 0, 0, 60, 60, 60, 134, 134, 134, 229, 229, 229, 255, 255, 255, 251, 251, 251, 229, 157, 90, 255, 247, 175]),
        ["ictcp_pq"] = new(9, 16, 14, false, [10, 10, 10],
            [[64, 397, 490, 573, 723, 940, 491, 703], [512, 512, 512, 512, 512, 512, 412, 375], [512, 512, 512, 512, 512, 512, 637, 741]],
            [0, 0, 0, 100, 100, 100, 169, 169, 169, 229, 229, 229, 255, 255, 255, 255, 255, 255, 213, 147, 88, 255, 249, 186]),
        ["ictcp_hlg"] = new(9, 18, 14, false, [10, 10, 10],
            [[64, 279, 493, 721, 940, 828, 618, 832], [512, 512, 512, 512, 512, 512, 368, 359], [512, 512, 512, 512, 512, 512, 682, 630]],
            [0, 0, 0, 60, 60, 60, 134, 134, 134, 229, 229, 229, 255, 255, 255, 251, 251, 251, 229, 157, 89, 255, 247, 175]),
        ["ictcp_sdr"] = new(1, 1, 14, false, [12, 12, 12],
            [[2061, 3010, 1188, 3760, 256, 2176, 1689, 1982], [1728, 0, 4095, 2048, 2048, 1261, 2048, 2919], [4095, 348, 290, 2048, 2048, 3157, 2048, 688]],
            [209, 105, 78, 77, 245, 160, 0, 76, 205, 255, 255, 255, 0, 0, 0, 188, 137, 89, 118, 118, 118, 63, 149, 203]),
        ["ingamut"] = new(1, 13, 5, true, [12, 12, 12],
            [[2437, 2754, 2442, 4095, 0, 3011, 3690, 1214], [1479, 1843, 2876, 2048, 2048, 2048, 1085, 1714], [2957, 1104, 1721, 2048, 2048, 2048, 2205, 1906]],
            [231, 124, 89, 89, 218, 149, 124, 149, 243, 255, 255, 255, 0, 0, 0, 187, 187, 187, 243, 243, 124, 63, 89, 39]),
        ["mc8lim"] = new(1, 1, 8, false, [8, 8, 8],
            [[71, 126, 71, 235, 16, 136, 126, 126], [73, 238, 73, 128, 128, 139, 128, 128], [238, 128, 18, 128, 128, 194, 84, 128]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 209, 163, 66, 92, 140, 187, 140, 140, 140]),
        ["mc16lim"] = new(1, 1, 16, false, [10, 10, 10],
            [[70, 125, 70, 235, 16, 136, 125, 126], [403, 731, 403, 512, 512, 534, 513, 512], [731, 512, 293, 512, 512, 643, 425, 512]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 209, 163, 67, 92, 140, 186, 140, 140, 140]),
        ["mc17lim"] = new(1, 1, 17, false, [9, 10, 10],
            [[70, 125, 70, 235, 16, 136, 125, 126], [403, 731, 403, 512, 512, 534, 513, 512], [731, 512, 293, 512, 512, 643, 425, 512]],
            [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255, 0, 0, 0, 209, 163, 67, 92, 140, 186, 140, 140, 140]),
        ["bits4lim"] = new(1, 1, 1, false, [4, 4, 4],
            [[4, 11, 2, 15, 1, 12, 10, 11], [6, 3, 15, 8, 8, 5, 9, 8], [15, 2, 7, 8, 8, 9, 7, 8]],
            [255, 9, 0, 29, 255, 33, 0, 9, 255, 255, 255, 255, 0, 0, 0, 236, 212, 117, 150, 181, 207, 193, 193, 193]),
    };

    /// <summary>A file of 4 × 2 pixels with the components of <paramref name="components"/> and <paramref name="header"/>.</summary>
    private static JpxImage Decode4x2(int[] bits, int[][] components, params byte[][] header) =>
        JpxDecoder.Decode(JpxFile(JpxTests.AnnexG.Lossless(4, 2, [.. bits.Select(b => (b, false))], components), header), long.MaxValue);

    /// <summary>
    /// The colour drawn through the image's profile (PCS XYZ, D50) as CIELab against the Lab of the light the codes carry (D65 adapted to D50
    /// with Bradford), for the <paramref name="pixels"/> inside the wide space: CIE76 within <paramref name="tolerance"/>.
    /// </summary>
    private static void DrawnLab(JpxImage image, double[][] labs, int[] pixels, double tolerance = 1.5)
    {
        var profile = IccProfile.Parse(image.IccProfile!)!;
        var xyz = new float[3];
        foreach (var i in pixels)
        {
            profile.ToXyz([image.Samples[3 * i] / 255f, image.Samples[3 * i + 1] / 255f, image.Samples[3 * i + 2] / 255f], xyz);
            var (l, a, b) = IccColor.XyzToLab(xyz[0], xyz[1], xyz[2]);
            var delta = Math.Sqrt(Math.Pow(l - labs[i][0], 2) + Math.Pow(a - labs[i][1], 2) + Math.Pow(b - labs[i][2], 2));
            Assert.True(delta <= tolerance, $"Pixel {i}: ΔE {delta:F2} (L* {l:F1}, a* {a:F1}, b* {b:F1})");
        }
    }

    // ---- No. 1055: PQ, HLG, ICtCp

    /// <summary>
    /// PQ and HLG (TransferCharacteristics 16, 18; BT.2020 primaries, MatrixCoefficients 9 and 14, limited range, 10 bits) on the page: black;
    /// grey of 26, 80, 203, 1 000 and 10 000 cd/m² (PQ) and the HLG scene light 0.02, 0.08, that of 75 % HLG, 1 and 0.5; two colours. The
    /// page white has the luminance of the HDR reference white (203 cd/m²): below the knee (88 cd/m²) light stays as it is — 80 cd/m² gives
    /// 80/203 of the page white, 169 in the sRGB curve —, the reference white, 58 % PQ and 75 % HLG alike, comes to 229, 1 000 cd/m² and above
    /// to 255. ICtCp of the same light gives the same page (to one step). The image carries the profile of the BT.2020 primaries. Before
    /// 23.11.2026 the specification was passed over.
    /// </summary>
    [Theory]
    [InlineData("pq")]
    [InlineData("hlg")]
    [InlineData("ictcp_pq")]
    [InlineData("ictcp_hlg")]
    public void Hdr_ReferenceWhiteAsPageWhite_HighlightsRolledOff(string key)
    {
        var c = ColourCases[key];
        var image = DecodeH273(c);
        Assert.False(image.Truncated);
        Assert.Equal(JpxColor.Icc, image.Color);
        Near(Bytes(c.Expected), image.Samples);
        Assert.Equal(229, image.Samples[9]);
    }

    /// <summary>
    /// The roll-off itself (ITU-R BT.2408, Annex 5; displayed light in cd/m² → page, 1 = page white): 1:1 below the knee KS = 1.5·maxLum − 0.5
    /// of the step from 1 000 to 203 cd/m² (87.8 cd/m²), so 50 and 87 cd/m² give 50/203 and 87/203 exactly; the reference white 0.7835; the
    /// common peak of 1 000 cd/m² and the PQ peak of 10 000 cd/m² page white; PQ above the knee of its first step (317 cd/m²) rolled off to
    /// 1 000 cd/m² first (1 000 cd/m² PQ: 0.9976). Values computed apart.
    /// </summary>
    [Theory]
    [InlineData(0.0, true, 0.0)]
    [InlineData(50.0, true, 0.2463054187192132)]
    [InlineData(87.0, true, 0.4285714285714248)]
    [InlineData(87.0, false, 0.4285714285714248)]
    [InlineData(150.0, false, 0.6661850892677519)]
    [InlineData(203.0, false, 0.7834574653176367)]
    [InlineData(203.0, true, 0.7834574653176367)]
    [InlineData(500.0, false, 0.9795862509784796)]
    [InlineData(1000.0, false, 1.0)]
    [InlineData(1000.0, true, 0.9975975765170749)]
    [InlineData(4000.0, true, 0.9999994672837464)]
    [InlineData(10000.0, true, 1.0)]
    [InlineData(double.NaN, true, 0.0)]
    [InlineData(-5.0, false, 0.0)]
    [InlineData(1e300, true, 1.0)]
    public void Hdr_RollOff_KneeReferenceWhiteAndPeak(double luminance, bool fromPq, double page) =>
        Assert.Equal(page, JpxParameterized.Tone(luminance, fromPq), 1e-9);

    /// <summary>
    /// H.273, Table 3, values 16 (PQ: V of Lo, Lo = 1 at 10 000 cd/m²; Lo = 0 gives c1^m, not 0) and 18 (HLG: √3·Lc^0.5 up to 1/12, where
    /// V = 0.5, then a·ln(12·Lc − b) + c; 0.2647 is about 75 % HLG): the function at chosen L against values computed apart, and back.
    /// </summary>
    [Theory]
    [InlineData(16, new[] { 0.0, 0.0001, 0.001, 0.0203, 0.1, 0.5, 1.0 },
        new[] { 7.309559025783966e-07, 0.14994573210018022, 0.29969909242098597, 0.5806888810416109, 0.751827096247041, 0.9265467040826304, 1.0 })]
    [InlineData(18, new[] { 0.0, 0.01, 1.0 / 12, 0.2, 0.2647, 0.5, 1.0 },
        new[] { 0.0, 0.17320508075688773, 0.5, 0.6938942691142709, 0.7498052568594701, 0.8716434713446153, 0.9999999955365686 })]
    public void Hdr_TransferCharacteristic_FunctionAndInverse(int transfer, double[] light, double[] values)
    {
        var parameterized = JpxParameterized.Read([0, 9, 0, (byte)transfer, 0, 9, 0x80])!;
        for (var i = 0; i < light.Length; i++)
        {
            Assert.Equal(values[i], parameterized.Forward(light[i]), 1e-9);
            Assert.Equal(light[i], parameterized.Inverse(values[i]), 1e-9);
        }
        Assert.Equal(0, parameterized.Inverse(double.NaN));
        Assert.Equal(parameterized.Inverse(1), parameterized.Inverse(7));
        Assert.Equal(0, parameterized.Inverse(-3));
    }

    /// <summary>
    /// ICtCp with an SDR characteristic (MatrixCoefficients 14, TransferCharacteristics 1, sRGB primaries, limited range, 12 bits): H.273 applies
    /// equations 79–81 to every characteristic but HLG. CT and CP have no stated range: red, green and blue reach codes up to 4 095 (CT, CP
    /// up to 0.57) and are read with them, not clipped to ±0.5. No tone mapping: plain RGB.
    /// </summary>
    [Fact]
    public void Ictcp_SdrCharacteristic_ColourDifferencesNotClipped()
    {
        var c = ColourCases["ictcp_sdr"];
        var image = DecodeH273(c);
        Assert.Equal(JpxColor.Rgb, image.Color);
        Near(Bytes(c.Expected), image.Samples);
    }

    // ---- No. 1056: the wide working space

    /// <summary>
    /// The extended characteristics 11, 12 and 13 with MatrixCoefficients 5 (the cases of
    /// <see cref="Parameterized_TransferCharacteristics_InvertedToLight"/>, two colours beyond sRGB, pixels 5 and 6): the image goes into the
    /// wide working space (BT.2020 primaries, sRGB curve, its own profile); the samples are the light converted to it, and drawn through the
    /// profile they give the colours the codes carry (ΔE76 ≤ 1.5; pixel 6 lies beyond BT.2020 too and is brought to the edge of BT.2020 on the line to its colour clipped in its own primaries,
    /// drawn as the reference images of T.803 clip — docs/FEHLERGEDAECHTNIS.md, No. 1092). Before 23.11.2026 cyan
    /// came out as 0, 187, 187 — its red, −0.13, clipped.
    /// </summary>
    [Theory]
    [InlineData("tc11", new[] { 207, 74, 34, 155, 246, 84, 184, 153, 99, 255, 255, 255, 0, 0, 0, 98, 180, 186, 255, 244, 77, 118, 118, 118 },
        "54.272 80.806 70.066|87.858 -79.285 80.957|66.106 16.288 39.796|100 0 0|0 0 0|66.964 -52.09 -14.476|96.302 -2.088 103.929|49.48 0 0")]
    [InlineData("tc12", new[] { 207, 74, 34, 155, 246, 84, 184, 153, 99, 255, 255, 255, 0, 0, 0, 99, 180, 186, 255, 244, 75, 118, 118, 118 },
        "54.272 80.806 70.066|87.858 -79.285 80.957|66.106 16.288 39.796|100 0 0|0 0 0|66.975 -51.918 -14.663|96.42 -2.673 106.62|49.48 0 0")]
    [InlineData("tc13sycc", new[] { 207, 74, 34, 155, 246, 84, 184, 153, 99, 255, 255, 255, 0, 0, 0, 100, 173, 185, 255, 235, 84, 118, 118, 118 },
        "54.275 80.798 69.883|87.833 -79.287 81.006|66.103 16.324 39.724|100 0 0|0 0 0|65.08 -45.35 -17.324|93.852 3.275 92.469|49.49 0 0")]
    public void Wide_ExtendedCharacteristics_ColoursBeyondSrgbKept(string key, int[] expected, string labs)
    {
        var image = DecodeH273(H273Cases[key]);
        Assert.Equal(JpxColor.Icc, image.Color);
        Assert.Equal(JpxParameterized.WideProfile(), image.IccProfile);
        Near(Bytes(expected), image.Samples);
        DrawnLab(image, Labs(labs), [0, 1, 2, 3, 4, 5, 7]);
    }

    private static double[][] Labs(string text) =>
        [.. text.Split('|').Select(p => p.Split(' ').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray())];

    /// <summary>
    /// An image with an extended characteristic whose colours all lie inside sRGB (13 with MatrixCoefficients 5, full range, 12 bits) stays as
    /// before: plain RGB, the light written with the sRGB curve.
    /// </summary>
    [Fact]
    public void Wide_ColoursInsideSrgb_StayPlainRgb()
    {
        var c = ColourCases["ingamut"];
        var image = DecodeH273(c);
        Assert.Equal(JpxColor.Rgb, image.Color);
        Assert.Null(image.IccProfile);
        Near(Bytes(c.Expected), image.Samples);
    }

    /// <summary>
    /// The limit: light beyond the primaries by less than half a step of the 8-bit output changes nothing when clipped and keeps the image in
    /// sRGB — white with Cr 4 codes above neutral (R′ 1.0014, red 1.0031) stays plain RGB, 8 codes (R′ 1.0027, red 1.0062) lead into the wide
    /// space.
    /// </summary>
    [Fact]
    public void Wide_OnlyBeyondHalfAStep()
    {
        var parameterized = JpxParameterized.Read([0, 1, 0, 13, 0, 5, 0x80])!;
        int[] bits = [12, 12, 12];
        Assert.False(parameterized.Outside([4095, 2048, 2048 + 4], bits));
        Assert.True(parameterized.Outside([4095, 2048, 2048 + 8], bits));
        Assert.True(parameterized.Outside([4095, 2048, 2048 - 2000], bits));
        int[][] Image(int cr) => [Enumerable.Repeat(4095, 8).ToArray(), Enumerable.Repeat(2048, 8).ToArray(), [2048, 2048, cr, 2048, 2048, 2048, 2048, 2048]];
        byte[] header = Parameterized(1, 13, 5, true);
        Assert.Equal(JpxColor.Rgb, Decode4x2(bits, Image(2052), header).Color);
        Assert.Equal(JpxColor.Icc, Decode4x2(bits, Image(2056), header).Color);
    }

    /// <summary>
    /// e-sRGB (EnumCS 20, 12 bits) beyond 0 … 1: R′ 1.2, 1.15, 1.1, 1.25 and −0.3, −0.1 — linear by PIMA 7667, equations 6a and 6c (the power
    /// continued above 100 %, odd around 0), into the wide space: drawn through its profile they give the colours of the codes (ΔE76 ≤ 1.5
    /// where they lie inside BT.2020). Before they were clipped in R′: red 1.2 came out as 255, 51, 51.
    /// </summary>
    [Fact]
    public void ESrgb_BeyondHundredPercent_InTheWideSpace()
    {
        int[][] components = [[3984, 924, 1740, 3576, 1536, 2556, 4086, 2148], [1944, 2760, 3882, 3576, 1536, 2556, 4086, 1332],
            [1944, 3372, 1638, 3576, 1536, 2556, 4086, 3780]];
        var image = Decode4x2([12, 12, 12], components, Enumerated(20));
        Assert.Equal(JpxColor.Icc, image.Color);
        Assert.Equal(JpxParameterized.WideProfile(), image.IccProfile);
        // pixels 2 and 7 lie beyond BT.2020 too: brought to the edge of BT.2020 on the line to its colour clipped in its own primaries (docs/FEHLERGEDAECHTNIS.md, No. 1092)
        Near([251, 103, 68, 86, 148, 222, 163, 255, 89, 255, 255, 255, 0, 0, 0, 128, 128, 128, 255, 255, 255, 86, 31, 255], image.Samples);
        DrawnLab(image, Labs("66.765 88.072 65.217|58.018 -21.409 -51.449|99.565 -87.318 89.45|100 0 0|0 0 0|53.389 0 0|121.513 0 0|34.89 79.482 -117.178"),
            [0, 1, 3, 4, 5]);
    }

    /// <summary>
    /// e-sYCC (EnumCS 24, 12 bits) with colours beyond sRGB (e-sRGB R′ from −0.2 to 1.2): the colour differences at half their size (PIMA 7667,
    /// B3), the way back B9, the curve extended (6a, 6c), into the wide space — drawn through its profile the colours of the codes.
    /// </summary>
    [Fact]
    public void ESycc_ColoursBeyondSrgb_InTheWideSpace()
    {
        int[][] components = [[1634, 1865, 1526, 4095, 0, 1790, 2668, 1142], [1703, 1984, 3004, 2048, 2048, 1774, 2393, 2419],
            [3072, 1091, 1796, 2048, 2048, 2286, 1169, 2955]];
        var image = Decode4x2([12, 12, 12], components, Enumerated(24));
        Assert.Equal(JpxColor.Icc, image.Color);
        // pixel 2 lies beyond BT.2020 too: brought to the edge of BT.2020 on the line to its colour clipped in its own primaries (docs/FEHLERGEDAECHTNIS.md, No. 1092)
        Near([229, 87, 49, 119, 196, 115, 86, 81, 255, 255, 255, 255, 0, 0, 0, 136, 106, 61, 149, 223, 239, 188, 62, 148], image.Samples);
        DrawnLab(image, Labs("60.283 85.211 68.769|71.796 -65.171 37.307|45.875 56.162 -112.819|100 0 0|0 0 0|48.037 17.414 36.889|82.887 -43.526 -19.935|50.369 82.111 -17.609"),
            [0, 1, 3, 4, 5, 6, 7]);
    }

    /// <summary>
    /// H.273, 8.3 (after equation 47): E′Y is clipped to 0 … 1 and E′PB, E′PR to ±0.5 unless TransferCharacteristics is 11 or 12 — also for 13
    /// with MatrixCoefficients 5, whose wider range covers E′R, E′G, E′B only. Limited range, 10 bits: Y 1 019 gives the same pixel as Y 940
    /// (white), Cr 1 023 the same as Cr 960 (E′PR 0.57 → 0.5), while R′ of that pixel, 1.70, still lies beyond 1. For TransferCharacteristics 11
    /// the luma is not clipped: Y 1 019 gives more than Y 940. Before 23.11.2026 13 was treated as 11 and 12.
    /// </summary>
    [Fact]
    public void Parameterized_Transfer13_LumaAndColourDifferencesClipped()
    {
        int[][] components = [[1019, 940, 940, 940, 64, 64, 502, 502], [512, 512, 512, 512, 512, 512, 512, 512], [512, 512, 1023, 960, 512, 512, 512, 512]];
        var image = Decode4x2([10, 10, 10], components, Parameterized(1, 13, 5, false));
        var s = image.Samples;
        Assert.Equal(JpxColor.Icc, image.Color);   // R′ 1.70: beyond sRGB
        Assert.Equal(s[0..3], s[3..6]);
        Assert.Equal(s[6..9], s[9..12]);
        // the light itself: 13 the same for Y 1 019 and 940, 11 more for 1 019
        double[] high = new double[3], nominal = new double[3];
        var sycc = JpxParameterized.Read([0, 1, 0, 13, 0, 5, 0])!;
        sycc.Linear([1019, 512, 512], [10, 10, 10], high);
        sycc.Linear([940, 512, 512], [10, 10, 10], nominal);
        Assert.Equal(nominal, high);
        var xvycc = JpxParameterized.Read([0, 1, 0, 11, 0, 1, 0])!;
        xvycc.Linear([1019, 512, 512], [10, 10, 10], high);
        xvycc.Linear([940, 512, 512], [10, 10, 10], nominal);
        Assert.True(high[0] > nominal[0] + 0.05);
    }

    // ---- Additions to the Parameterized specification (H.273, 8.3)

    /// <summary>
    /// Without an extended characteristic (MatrixCoefficients 1, TransferCharacteristics 1, limited range, 10 bits) E′Y is clipped to 1: Y 1 019,
    /// Cb 512, Cr 960 is the same pixel as Y 940, Cb 512, Cr 960 (8.3, after equation 47).
    /// </summary>
    [Fact]
    public void Parameterized_LimitedRange_ClippedWithoutExtendedCharacteristic()
    {
        int[][] components = [[1019, 940, 600, 600, 64, 64, 502, 502], [512, 512, 512, 512, 512, 512, 512, 512], [960, 960, 512, 512, 512, 512, 512, 512]];
        var s = Decode4x2([10, 10, 10], components, Parameterized(1, 1, 1, false)).Samples;
        Assert.Equal(s[0..3], s[3..6]);
        Assert.NotEqual(s[0..3], s[6..9]);
    }

    /// <summary>
    /// Equations 27–29 in the limited range for the integer transforms too (VideoFullRangeFlag 0): YCgCo (8, 8 bits), YCgCo-Re (16, Y of 10 bits)
    /// and YCgCo-Ro (17, Y of 9 bits) made from coded R, G, B in 16 … 235·2^(BitDepthRGB − 8) — read back to the colours they came from
    /// (16 is black, 235 white; TransferCharacteristics 1, so that the rounding of YCgCo, red 236, stays clipped in sRGB).
    /// </summary>
    [Theory]
    [InlineData("mc8lim")]
    [InlineData("mc16lim")]
    [InlineData("mc17lim")]
    public void Parameterized_IntegerTransforms_LimitedRange(string key)
    {
        var c = ColourCases[key];
        Near(Bytes(c.Expected), DecodeH273(c).Samples);
    }

    /// <summary>
    /// The limited range below 8 bits (4 bits, VideoFullRangeFlag 0; MatrixCoefficients 1, TransferCharacteristics 1): 2^(n−8) taken as the
    /// fraction 1/16 — black is Y 1, white Y 15 (beyond 235/16, clipped to 1), Cb and Cr about 8.
    /// </summary>
    [Fact]
    public void Parameterized_LimitedRange_BelowEightBits()
    {
        var c = ColourCases["bits4lim"];
        Near(Bytes(c.Expected), DecodeH273(c).Samples);
    }

    /// <summary>MatrixCoefficients that are not applicable make the specification not applicable with VideoFullRangeFlag 1 too.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(18)]
    [InlineData(255)]
    [InlineData(257)]
    [InlineData(65535)]
    public void Parameterized_MatrixNotApplicable_FullRangeToo(int matrix)
    {
        var c = H273Cases["mc1"];
        var image = JpxDecoder.Decode(JpxFile(H273Codestream(c), [Parameterized(1, 1, matrix, true, precedence: 1), Enumerated(16)]), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, image.Color);
        Assert.Equal(Mc1Plain, image.Samples);
    }

    // ---- No. 1057: from the full codes to one 8-bit step

    /// <summary>
    /// sYCC with 16 bits (EnumCS 18) and YCbCr(1) with 12 bits, limited range (EnumCS 1): R′ = Y′ + 1.402·C′r … from the full codes, rounded
    /// once to 8 bits. Before Y, Cb and Cr were cut to their upper eight bits first and converted from there — that gives other values here.
    /// </summary>
    [Theory]
    [InlineData(18u, 16, new[] { 25018, 25847, 24471, 39252, 238, 45875, 36421, 30083 }, new[] { 46387, 47362, 33752, 47230, 32856, 32768, 15913, 33174 },
        new[] { 38295, 20082, 57383, 20196, 32692, 32768, 18476, 39825 },
        new[] { 127, 64, 191, 31, 116, 201, 229, 26, 102, 84, 168, 252, 1, 1, 2, 179, 179, 179, 64, 204, 26, 156, 97, 120 })]
    [InlineData(1u, 12, new[] { 1594, 1638, 1564, 2355, 269, 2709, 2203, 1864 }, new[] { 2793, 2846, 2102, 2839, 2053, 2048, 1126, 2070 },
        new[] { 2350, 1354, 3394, 1360, 2044, 2048, 1266, 2434 },
        new[] { 127, 64, 191, 31, 116, 201, 229, 25, 102, 84, 168, 252, 1, 1, 2, 179, 179, 179, 64, 204, 25, 156, 97, 120 })]
    public void Ycc_FullCodes_OneEightBitStep(uint space, int bits, int[] y, int[] cb, int[] cr, int[] expected)
    {
        var image = Decode4x2([bits, bits, bits], [y, cb, cr], Enumerated(space));
        Assert.Equal(JpxColor.Rgb, image.Color);
        Assert.Equal(Bytes(expected), image.Samples);
        if (space == 18)
        {
            byte[] cut = [.. Enumerable.Range(0, 8).SelectMany(i => new[] { (byte)(y[i] >> 8), (byte)(cb[i] >> 8), (byte)(cr[i] >> 8) })];
            Assert.NotEqual(YccToRgb(cut), image.Samples);
        }
    }

    /// <summary>
    /// e-sRGB read to linear light (PIMA 7667, equations 4–6): the neutral patches of 4.4.3 give their Y back — 384, 1 536 and 24 576 are 0, 894,
    /// 3 576 and 57 216 are 1, 594 / 2 376 / 38 023 are 0.14141 (to the rounding of the codes, 0.002).
    /// </summary>
    [Theory]
    [InlineData(0.0, 384, 1536, 24576)]
    [InlineData(0.01010, 435, 1741, 27856)]
    [InlineData(0.07071, 534, 2137, 34199)]
    [InlineData(0.14141, 594, 2376, 38023)]
    [InlineData(0.59596, 790, 3158, 50536)]
    [InlineData(1.0, 894, 3576, 57216)]
    public void ESrgb_NeutralPatches_LinearLight(double y, int code10, int code12, int code16)
    {
        var esrgb = JpxParameterized.Enumerated(20)!;
        var rgb = new double[3];
        foreach (var (code, bits) in new[] { (code10, 10), (code12, 12), (code16, 16) })
        {
            esrgb.Linear([code, code, code], [bits, bits, bits], rgb);
            Assert.Equal(y, rgb[0], 0.002);
            Assert.Equal(rgb[0], rgb[2]);
        }
    }
}
