using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics.Icc;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The last colour points of the JPEG 2000 decoder (23.11.2026): signed channels of the enumerated spaces from their value of no light
/// (T.801 M.11.7.2; docs/FEHLERGEDAECHTNIS.md, No. 1066), signed fixed and floating point channels as numbers of −1 … 1 (No. 1067), the offsets
/// of the CIELab EP fields by Equation M-18 (No. 1068), signed palette columns as signed components (No. 1070), YPbPr(1125/60) and
/// YPbPr(1250/50) read, scRGB and PhotoYCC passed over (No. 1071), sYCC as the extended encoding it is and YCCK from its full codes (No. 1072),
/// reserved colour specification methods of a JPX file reported (No. 1073). The expected values were worked out apart from the decoder from
/// the formulas of T.801, ITU-T H.273, IEC 61966-2-1 and PIMA 7667 (and the matrix of ITU-R BT.2087 for the wide working space).
/// </summary>
public sealed partial class JpxColourTests
{
    // ---- helpers

    /// <summary>The matrix from the linear light of the BT.709 primaries to that of the BT.2020 primaries (ITU-R BT.2087, same white D65).</summary>
    private static readonly double[,] Bt709To2020 = { { 0.6274, 0.3293, 0.0433 }, { 0.0691, 0.9195, 0.0114 }, { 0.0164, 0.0880, 0.8956 } };

    /// <summary>The sRGB curve inverted (IEC 61966-2-1), continued odd around 0 and as the power above 1 (the extended encodings).</summary>
    private static double SrgbLinear(double v)
    {
        var a = Math.Abs(v);
        var l = a <= 0.04045 ? a / 12.92 : Math.Pow((a + 0.055) / 1.055, 2.4);
        return v < 0 ? -l : l;
    }

    /// <summary>Linear light 0 … 1 → 8 bits with the sRGB curve, rounded; beyond clipped.</summary>
    private static byte SrgbByte(double l)
    {
        var c = Math.Clamp(l, 0, 1);
        var e = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (byte)Math.Round(e * 255);
    }

    /// <summary>
    /// sYCC of 8-bit codes Y, Cb, Cr (interleaved) as the decoder has to show it since sYCC is read as the extended encoding it is (IEC 61966-2-1,
    /// Annex G; docs/FEHLERGEDAECHTNIS.md, No. 1072): R′G′B′ by the inverse matrix of BT.601; where the light of a pixel (the sRGB curve
    /// continued) lies more than half an 8-bit step beyond 0 … 1, the whole image in the wide working space — BT.2087 to the BT.2020
    /// primaries, written with the sRGB curve —, otherwise R′G′B′ rounded as they are (<see cref="YccToRgb"/>). Inside: per pixel whether its
    /// light lies within sRGB.
    /// </summary>
    private static (bool Wide, byte[] Samples, bool[] Inside) Sycc(byte[] s)
    {
        double low = -0.5 / 255 / 12.92, high = Math.Pow((255.5 / 255 + 0.055) / 1.055, 2.4);
        var count = s.Length / 3;
        var light = new double[count][];
        var inside = new bool[count];
        for (var i = 0; i < count; i++)
        {
            double y = s[3 * i] / 255.0, cb = Math.Clamp((s[3 * i + 1] - 128) / 255.0, -0.5, 0.5), cr = Math.Clamp((s[3 * i + 2] - 128) / 255.0, -0.5, 0.5);
            double r = y + 1.402 * cr, b = y + 1.772 * cb, g = (y - 0.299 * r - 0.114 * b) / 0.587;
            light[i] = [SrgbLinear(r), SrgbLinear(g), SrgbLinear(b)];
            inside[i] = light[i].All(v => v >= low && v <= high);
        }
        if (inside.All(v => v)) return (false, YccToRgb(s), inside);
        var samples = new byte[s.Length];
        for (var i = 0; i < count; i++)
        {
            var wide = ToBt2020(light[i]);
            for (var k = 0; k < 3; k++) samples[3 * i + k] = SrgbByte(wide[k]);
        }
        return (true, samples, inside);
    }

    /// <summary>
    /// Linear light of the BT.709 primaries in the BT.2020 primaries (BT.2087); a colour beyond those as well at the point where the line from
    /// the colour clipped to 0 … 1 in its own primaries to the colour itself leaves 0 … 1 (docs/FEHLERGEDAECHTNIS.md, No. 1092).
    /// </summary>
    private static double[] ToBt2020(double[] light)
    {
        double[] Multiply(double[] l) => [.. Enumerable.Range(0, 3).Select(k => Bt709To2020[k, 0] * l[0] + Bt709To2020[k, 1] * l[1] + Bt709To2020[k, 2] * l[2])];
        var wide = Multiply(light);
        if (wide.All(v => v is >= 0 and <= 1)) return wide;
        var start = Multiply([.. light.Select(v => Math.Clamp(v, 0, 1))]);
        var t = Enumerable.Range(0, 3).Select(k => wide[k] > 1 ? (1 - start[k]) / (wide[k] - start[k]) : wide[k] < 0 ? -start[k] / (wide[k] - start[k]) : 1).Min();
        return [.. Enumerable.Range(0, 3).Select(k => start[k] + t * (wide[k] - start[k]))];
    }

    /// <summary>The image is the sYCC of <paramref name="codes"/> (<see cref="Sycc"/>): colour, profile and samples.</summary>
    private static void NearSycc(byte[] codes, JpxImage image, int tolerance = 1)
    {
        var (wide, expected, _) = Sycc(codes);
        Assert.Equal(wide ? JpxColor.Icc : JpxColor.Rgb, image.Color);
        Assert.Equal(wide ? JpxParameterized.WideProfile() : null, image.IccProfile);
        Near(expected, image.Samples, tolerance);
    }

    /// <summary>
    /// The sYCC of <paramref name="codes"/> drawn on a page (through the profile of the wide working space where it is in it): the pixels inside
    /// sRGB as their R′G′B′ to CIE76 1.5 (the 8-bit wide encoding and the drawing each round; near black a few 8-bit steps are below that), the
    /// others not clipped as before.
    /// </summary>
    private static void NearSyccDrawn(byte[] codes, byte[] drawn)
    {
        var (_, _, inside) = Sycc(codes);
        var expected = YccToRgb(codes);
        for (var i = 0; i < inside.Length; i++)
        {
            if (!inside[i]) continue;
            var (a, b) = (SrgbLab(expected, 3 * i), SrgbLab(drawn, 3 * i));
            var delta = Math.Sqrt(Enumerable.Range(0, 3).Sum(k => (a[k] - b[k]) * (a[k] - b[k])));
            Assert.True(delta <= 1.5, $"Pixel {i}: ΔE {delta:F2}");
        }
        Assert.NotEqual(codes, drawn);
    }

    /// <summary>CIELab (D65 white) of the 8-bit sRGB pixel at <paramref name="at"/> (IEC 61966-2-1 matrix; CIE 15).</summary>
    private static double[] SrgbLab(byte[] s, int at)
    {
        double r = SrgbLinear(s[at] / 255.0), g = SrgbLinear(s[at + 1] / 255.0), b = SrgbLinear(s[at + 2] / 255.0);
        double x = 0.4124 * r + 0.3576 * g + 0.1805 * b, y = 0.2126 * r + 0.7152 * g + 0.0722 * b, z = 0.0193 * r + 0.1192 * g + 0.9505 * b;
        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : t * 841 / 108 + 4.0 / 29;
        double fx = F(x / 0.95047), fy = F(y), fz = F(z / 1.08883);
        return [116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)];
    }

    /// <summary>A codestream of <paramref name="width"/> × 1 pixels with components of one depth, signed, holding <paramref name="samples"/>.</summary>
    private static byte[] SignedRow(int width, int depth, params int[][] samples) =>
        JpxTests.AnnexG.Lossless(width, 1, [.. samples.Select(_ => (depth, true))], samples);

    /// <summary>
    /// A Palette box: NE entries, the columns with their Bi, each entry as the two's complement of its Bi + 1 bits, padded with zeros to whole
    /// bytes (T.800 I.5.3.4).
    /// </summary>
    private static byte[] Pclr(int[] bi, params int[][] entries) =>
        Box("pclr", U16(entries.Length), [(byte)bi.Length], [.. bi.Select(b => (byte)b)],
            [.. entries.SelectMany(e => e.SelectMany((v, c) =>
            {
                var depth = (bi[c] & 0x7F) + 1;
                var bytes = (depth + 7) / 8;
                var pattern = v & ((1L << depth) - 1);
                return Enumerable.Range(0, bytes).Select(j => (byte)(pattern >> (8 * (bytes - 1 - j))));
            }))]);

    /// <summary>A Component Mapping box: every channel through the palette column of its number, from component 0.</summary>
    private static byte[] CmapColumns(int columns) => Box("cmap", [.. Enumerable.Range(0, columns).SelectMany(k => new byte[] { 0, 0, 1, (byte)k })]);

    // ---- No. 1066: signed channels of the enumerated spaces from their value of no light

    /// <summary>
    /// T.801 M.11.7.2 (d = Dzero + (Dmax − Dzero)·x / MAX, MAX = 2^(n−1) − 1 by Table M.24bis), Dmax the top code 2^n − 1 of the depth (as the
    /// unsigned channels are read), Dzero the code of no light: sRGB 12 bits — 255·x/2047, negative values black; sRGB-grey 16 bits — 255·x/32767;
    /// sYCC 10 bits — Y from 0 (1023·x/511), Cb and Cr from 512 (512 + x); YCbCr(1) 8 bits — Y from 16 (16 + 239·x/127), Cb, Cr 128 + x;
    /// e-sRGB 12 bits — from R′ = 0, the code 1 536 (1 536 + 2 559·x/2 047). The colours inside sRGB, written as 8 bits once (sYCC and the
    /// others: R′G′B′ rounded as they are). Before the luma, the three channels of sRGB and those of e-sRGB were read shifted by half their
    /// range, 0 as mid-grey.
    /// </summary>
    [Theory]
    [InlineData("srgb12")]
    [InlineData("grey16")]
    [InlineData("sycc10")]
    [InlineData("ycbcr1")]
    [InlineData("esrgb12")]
    public void SignedChannels_EnumeratedSpaces_FromTheirValueOfNoLight(string key)
    {
        var (space, cs, expected) = key switch
        {
            "srgb12" => (16u, SignedRow(4, 12, [-2048, 0, 2047, 1000], [-1, 1, 2046, 512], [5, 2047, -700, 1500]),
                new byte[] { 0, 0, 1, 0, 0, 255, 255, 255, 0, 125, 64, 187 }),
            "grey16" => (17u, SignedRow(6, 16, [-32768, 0, 32767, 16384, 100, -5]), new byte[] { 0, 0, 255, 128, 1, 0 }),
            "sycc10" => (18u, SignedRow(8, 10, [224, 248, 255, 511, 0, 286, 413, 111], [-137, 240, 0, 0, 0, -93, -409, 221], [264, -281, 0, 0, 0, -227, 67, 61]),
                new byte[] { 204, 77, 51, 26, 153, 230, 127, 127, 127, 255, 255, 255, 0, 0, 0, 63, 191, 102, 230, 229, 25, 77, 26, 153 }),
            "ycbcr1" => (1u, SignedRow(8, 8, [51, 56, 58, 116, 0, 65, 94, 25], [-30, 52, 0, 0, 0, -20, -90, 48], [58, -61, 0, 0, 0, -50, 15, 13]),
                new byte[] { 204, 76, 51, 25, 152, 228, 127, 127, 127, 254, 254, 254, 0, 0, 0, 63, 191, 102, 230, 229, 24, 76, 25, 152 }),
            _ => (20u, SignedRow(4, 12, [326, 82, 1632, 734], [816, 1224, 0, 734], [1469, 539, 979, 734]),
                new byte[] { 51, 128, 230, 13, 191, 84, 255, 0, 153, 115, 115, 115 }),
        };
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(space)]), long.MaxValue);
        Assert.Equal(space == 17 ? JpxColor.Gray : JpxColor.Rgb, image.Color);
        Assert.Equal(expected, image.Samples);
    }

    /// <summary>
    /// T.801 M.11.7.2: where the colour of no light has no unique values the channels are not scaled — CMY (Dzero would be Dmax itself) and CMYK
    /// keep the signed samples shifted by half their range, as without a colour specification: CMY 255 − (x + 128), CMYK x + 128.
    /// </summary>
    [Fact]
    public void SignedChannels_InkSpaces_StillShiftedByHalf()
    {
        int[] x = [-128, -1, 0, 127];
        var cmy = JpxDecoder.Decode(Jpx(SignedRow(4, 8, x, x, x), [Enumerated(11)]), long.MaxValue);
        Assert.Equal(x.SelectMany(v => Enumerable.Repeat((byte)(127 - v), 3)).ToArray(), cmy.Samples);
        var cmyk = JpxDecoder.Decode(Jpx(SignedRow(4, 8, x, x, x, x), [Enumerated(12)]), long.MaxValue);
        Assert.Equal(x.SelectMany(v => Enumerable.Repeat((byte)(v + 128), 4)).ToArray(), cmyk.Samples);
    }

    // ---- No. 1067: signed fixed and floating point channels as numbers

    /// <summary>
    /// T.801 M.2.8, Table M.24bis (signed fixed point: MAX 1.0): a palette column of signed 8-bit fixed point numbers with 6 fractional bits
    /// (Bi 0x87, Pixel Format 0x3006) as sRGB-grey — −1, −1/64, 0, 0.5, 1, 1.5625: the level clamp(N, 0, 1)·255, 0 … 1 from no light. Before the
    /// entries were read as the unsigned bits of the column (the format applied to them, but the sign lost to the stretching).
    /// </summary>
    [Fact]
    public void SignedFixedPoint_PaletteColumn_TheirNumbers()
    {
        int[] index = [0, 1, 2, 3, 4, 5];
        var cs = JpxTests.AnnexG.Lossless8(6, 1, index);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(17), Pclr([0x87], [-64], [-1], [0], [32], [64], [100]), CmapColumns(1), M117Colour.Pxfm(1, 0x3006)]),
            long.MaxValue);
        Assert.Equal(JpxColor.Gray, image.Color);
        Assert.Equal(new byte[] { 0, 0, 0, 128, 255, 255 }, image.Samples);
    }

    /// <summary>
    /// T.801 M.11.7.2 with sYCC: a signed fixed point colour difference keeps its negative half — Y unsigned 8-bit integers, Cb and Cr signed
    /// 16-bit fixed point numbers with 15 fractional bits (N = x/32768): Cb = N/2 of the range (128 + 127·N in 8-bit codes), so N = −0.5
    /// gives a bluish yellow, not the clamped 0 of an unsigned number that made Cb −0.5.
    /// </summary>
    [Fact]
    public void SignedFixedPoint_ColourDifferences_KeepTheirSign()
    {
        int[] y = [128, 128, 200, 60];
        int[] n = [-16384, 16384, 0, -32768];   // N = −0.5, 0.5, 0, −1
        var cs = JpxTests.AnnexG.Lossless(4, 1, [(8, false), (16, true), (16, true)], [y, n, [0, 0, -8192, 8192]]);
        var formats = JpxDecoder.Decode(Jpx(cs, [Enumerated(3), M117Colour.Pxfm(0x0000, 0x300F, 0x300F)]), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, formats.Color);
        // the expected R′G′B′ from Y and the colour differences N/2 (Cr: 0, 0, −0.125, 0.125)
        double[][] diffs = [[-0.25, 0], [0.25, 0], [0, -0.125], [-0.5, 0.125]];
        var expected = new byte[12];
        for (var i = 0; i < 4; i++)
        {
            double l = y[i] / 255.0, cb = diffs[i][0], cr = diffs[i][1];
            double r = l + 1.402 * cr, b = l + 1.772 * cb, g = (l - 0.299 * r - 0.114 * b) / 0.587;
            expected[3 * i] = (byte)Math.Clamp(Math.Floor(r * 255 + 0.5), 0, 255);
            expected[3 * i + 1] = (byte)Math.Clamp(Math.Floor(g * 255 + 0.5), 0, 255);
            expected[3 * i + 2] = (byte)Math.Clamp(Math.Floor(b * 255 + 0.5), 0, 255);
        }
        Near(expected, formats.Samples);
    }

    // ---- No. 1068: the offsets of the CIELab EP fields by Equation M-18

    /// <summary>
    /// T.801 M.11.7.4.1, Equation M-18 on signed 12-bit channels (MAX 2 047, 2^⌈log2 MAX⌉ = 2 048, 2^n = 4 096): an offset counts as O/2 —
    /// EP RL 100, OL 0, RA 170, OA 1 000, RB 200, OB 0: L* = 100·x/2047, a* = 170·(x − 500)/2047, b* = 200·x/2047. Before OA counted in full
    /// (a* = 170·(x − 1 000)/2047).
    /// </summary>
    [Fact]
    public void CieLab_SignedTwelveBits_OffsetHalved()
    {
        int[][] x = [[-2048, 0, 2047, 1000, 300, -5], [-2048, 0, 2047, 500, 1500, -700], [-2048, 0, 2047, -1000, 700, 2000]];
        byte[] ep = [.. U32(100), .. U32(0), .. U32(170), .. U32(1000), .. U32(200), .. U32(0), .. U32(0x00443530)];
        var image = JpxDecoder.Decode(Jpx(SignedRow(6, 12, x), [Enumerated(14, ep: ep)]), long.MaxValue);
        Assert.Equal(JpxColor.Lab, image.Color);
        Near([0, 0, 0, 0, 86, 128, 255, 255, 255, 125, 128, 30, 37, 211, 196, 0, 28, 255], image.Samples);
    }

    /// <summary>
    /// T.801 M.11.7.4.1, Equation M-18 on signed fixed point channels (MAX 1.0, so 2^⌈log2 MAX⌉ = 1; n = 8): an offset counts as O/2^n of 1.0 —
    /// format 0x3006 (N = x/64, within −1 … 1), EP RL 100, OL 64, RA 170, OA 0, RB 200, OB 0: L* = 100·(N − 0.25), a* = 170·N, b* = 200·N.
    /// Before the offset counted as an integer of the stored level.
    /// </summary>
    [Fact]
    public void CieLab_SignedFixedPoint_OffsetAFractionOfOne()
    {
        int[][] x = [[-64, 0, 16, 32, 64, 100], [0, 10, -20, 30, 64, -64], [5, -5, 40, -40, 0, 127]];
        byte[] ep = [.. U32(100), .. U32(64), .. U32(170), .. U32(0), .. U32(200), .. U32(0), .. U32(0x00443530)];
        var image = JpxDecoder.Decode(Jpx(SignedRow(6, 8, x), [Enumerated(14, ep: ep), M117Colour.Pxfm(3, 0x3006)]), long.MaxValue);
        Assert.Equal(JpxColor.Lab, image.Color);
        Near([0, 128, 144, 0, 155, 112, 0, 75, 253, 64, 208, 3, 191, 255, 128, 191, 0, 255], image.Samples);
    }

    // ---- No. 1070: signed palette columns as signed components

    /// <summary>
    /// A signed palette column (Bi 0x87: 8 bits with sign; T.800 Table I.13) read as a signed component is (decided on 23.11.2026): without a
    /// colour the entries −128, −1, 0, 1, 64, 127 shifted by half their range — 0, 127, 128, 129, 192, 255; as sRGB-grey from no light —
    /// 255·x/127. Before their two's complement bits were read as unsigned values: 128, 255, 0, 1, 64, 127.
    /// </summary>
    [Fact]
    public void Palette_SignedColumn_AsASignedComponent()
    {
        var cs = JpxTests.AnnexG.Lossless8(6, 1, [0, 1, 2, 3, 4, 5]);
        var pclr = Pclr([0x87], [-128], [-1], [0], [1], [64], [127]);
        var unknown = JpxDecoder.Decode(Jpx(cs, [Enumerated(99), pclr, CmapColumns(1)]), long.MaxValue);
        Assert.Equal(JpxColor.Unknown, unknown.Color);
        Assert.Equal(new byte[] { 0, 127, 128, 129, 192, 255 }, unknown.Samples);
        var grey = JpxDecoder.Decode(Jpx(cs, [Enumerated(17), pclr, CmapColumns(1)]), long.MaxValue);
        Assert.Equal(new byte[] { 0, 0, 0, 2, 129, 255 }, grey.Samples);
    }

    /// <summary>
    /// Three signed 12-bit palette columns (Bi 0x8B, two bytes per entry) as sRGB: 255·x/2047 from no light, the negative values black; three
    /// signed 8-bit columns as sYCC: Y from 0 (255·x/127), Cb and Cr 128 + x, from the full codes to one 8-bit step.
    /// </summary>
    [Fact]
    public void Palette_SignedColumns_SrgbAndSycc()
    {
        var cs = JpxTests.AnnexG.Lossless8(4, 1, [0, 1, 2, 3]);
        var srgb = JpxDecoder.Decode(Jpx(cs, [Enumerated(16), Pclr([0x8B, 0x8B, 0x8B], [-2048, 0, 2047], [-1, 1, 1024], [700, 1500, -3], [2047, 2047, 2047]),
            CmapColumns(3)]), long.MaxValue);
        Assert.Equal(new byte[] { 0, 0, 255, 0, 0, 128, 87, 187, 0, 255, 255, 255 }, srgb.Samples);
        var six = JpxTests.AnnexG.Lossless8(6, 1, [0, 1, 2, 3, 4, 5]);
        var sycc = JpxDecoder.Decode(Jpx(six, [Enumerated(18), Pclr([0x87, 0x87, 0x87], [56, -34, 66], [62, 60, -70], [63, 0, 0], [127, 0, 0], [0, 0, 0],
            [71, -23, -56]), CmapColumns(3)]), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, sycc.Color);
        Assert.Equal(new byte[] { 205, 77, 52, 26, 154, 231, 126, 126, 126, 255, 255, 255, 0, 0, 0, 64, 190, 102 }, sycc.Samples);
    }

    // ---- No. 1071: YPbPr(1125/60), YPbPr(1250/50); scRGB and PhotoYCC passed over

    /// <summary>
    /// T.801 Table M.25, values 22 and 23 (the HDTV systems of ITU-R BT.709-3): limited range — Y 16 … 235, Pb and Pr 128 ± 112 at 8 bits,
    /// times 4 at 10 bits —, the components written forward from linear light (BT.709 opto-electronic function, H.273 Table 3 value 1) with the
    /// luma coefficients 0.2126/0.0722 (1125/60) and 0.299/0.114 (1250/50), read back to light and written with the sRGB curve: both as plain
    /// RGB — BT.709-3 Part I gives both systems the BT.709 primaries, those of sRGB (items 1.3, 1.4; until No. 1078 1250/50 had an ICC profile
    /// of the primaries of H.273 value 5). The samples stay what they were: the codes were written forward with those primaries, and only the
    /// profile changed. Before both were passed over and Y, Pb, Pr shown as R, G, B.
    /// </summary>
    [Theory]
    [InlineData(22u, 8)]
    [InlineData(22u, 10)]
    [InlineData(23u, 8)]
    [InlineData(23u, 10)]
    public void YPbPr_HdtvSystems_ReadBackToLight(uint space, int bits)
    {
        var (y, pb, pr, expected) = (space, bits) switch
        {
            (22, 8) => (new[] { 125, 137, 106, 235, 16, 180, 203, 62 }, new[] { 103, 169, 128, 128, 128, 97, 47, 188 }, new[] { 168, 76, 128, 128, 128, 98, 141, 144 },
                new byte[] { 205, 124, 89, 63, 169, 230, 118, 118, 118, 255, 255, 255, 0, 0, 0, 148, 218, 137, 243, 231, 62, 97, 48, 188 }),
            (22, _) => (new[] { 498, 549, 422, 940, 64, 720, 813, 249 }, new[] { 413, 677, 512, 512, 512, 387, 189, 751 }, new[] { 671, 303, 512, 512, 512, 393, 565, 576 },
                new byte[] { 204, 123, 89, 63, 170, 231, 118, 118, 118, 255, 255, 255, 0, 0, 0, 149, 218, 137, 243, 231, 63, 97, 48, 188 }),
            (_, 8) => (new[] { 130, 131, 106, 235, 16, 171, 198, 71 }, new[] { 99, 175, 128, 128, 128, 100, 47, 185 }, new[] { 169, 74, 128, 128, 128, 101, 147, 139 },
                new byte[] { 204, 124, 89, 63, 169, 232, 118, 118, 118, 255, 255, 255, 0, 0, 0, 149, 218, 136, 244, 231, 64, 96, 49, 187 }),
            _ => (new[] { 519, 525, 422, 940, 64, 685, 791, 285 }, new[] { 397, 698, 512, 512, 512, 401, 186, 741 }, new[] { 675, 294, 512, 512, 512, 404, 588, 558 },
                new byte[] { 203, 124, 89, 63, 170, 231, 118, 118, 118, 255, 255, 255, 0, 0, 0, 149, 218, 137, 243, 231, 63, 97, 48, 187 }),
        };
        var cs = JpxTests.AnnexG.Lossless(4, 2, [(bits, false), (bits, false), (bits, false)], [y, pb, pr]);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(space)]), long.MaxValue);
        Assert.False(image.Truncated);
        Assert.Equal(JpxColor.Rgb, image.Color);
        Assert.Null(image.IccProfile);
        Near(expected, image.Samples);
    }

    /// <summary>
    /// T.801 Table M.25: PhotoYCC (9; its encoding is in Kodak's Photo CD documents only), scRGB grey (26; neither IEC 61966-2-2 nor the ICC
    /// Colour Encoding Registry at hand fixes its codes) and scRGB (25) with 8-bit components (the registry fixes 16 bits only, No. 1079) are
    /// not read — the specification behind them counts; alone the colour is not known and the image is the codestream's.
    /// </summary>
    [Theory]
    [InlineData(9u)]
    [InlineData(25u)]
    [InlineData(26u)]
    public void PhotoYccAndScRgb_PassedOver(uint space)
    {
        var grey = space == 26;
        var cs = grey ? JpxTests.AnnexG.Lossless8(3, 2, [10, 60, 110, 160, 210, 250]) : Jp2HeaderBoxes.Three();
        var plain = Plain(cs);
        var next = JpxDecoder.Decode(Jpx(cs, [Enumerated(space, 1, 1), Enumerated(grey ? 17u : 16u, 0, 1)]), long.MaxValue);
        Assert.Equal(grey ? JpxColor.Gray : JpxColor.Rgb, next.Color);
        Assert.Equal(plain, next.Samples);
        var alone = JpxDecoder.Decode(Jpx(cs, [Enumerated(space, 0, 1)]), long.MaxValue);
        Assert.Equal(JpxColor.Unknown, alone.Color);
        Assert.Equal(plain, alone.Samples);
    }

    // ---- No. 1072: sYCC as an extended encoding, YCCK from its full codes

    /// <summary>
    /// sYCC (IEC 61966-2-1, Annex G) is an extended encoding: 12-bit codes whose R′G′B′ leave 0 … 1 (Y 4 095 with Cr 4 095: R′ 1.70; Cb 0:
    /// B′ −0.39) come into the wide working space — the light by the sRGB curve continued, the BT.2020 primaries, the sRGB curve again —, the
    /// colours beyond sRGB kept; the same codes as YCbCr(2) (BT.601, not extended) stay plain RGB, clipped at the edge of sRGB. Before sYCC was
    /// clipped as well (decided on 23.11.2026: the larger working space for extended spaces).
    /// </summary>
    [Fact]
    public void Sycc_ColoursBeyondSrgb_InTheWideSpace()
    {
        int[] y = [4095, 2048, 1200, 3000, 4095, 0, 2600, 1800], cb = [2048, 0, 2048, 1500, 2048, 2048, 3100, 2300], cr = [4095, 2048, 2048, 2900, 2048, 2048, 1700, 1500];
        var cs = JpxTests.AnnexG.Lossless(4, 2, [(12, false), (12, false), (12, false)], [y, cb, cr]);
        var sycc = JpxDecoder.Decode(Jpx(cs, [Enumerated(18)]), long.MaxValue);
        Assert.Equal(JpxColor.Icc, sycc.Color);
        Assert.Equal(JpxParameterized.WideProfile(), sycc.IccProfile);
        // pixels 0, 1 and 6 lie beyond BT.2020 too: brought to the edge of BT.2020 on the line to its colour clipped in its own primaries (docs/FEHLERGEDAECHTNIS.md, No. 1092)
        Near([255, 178, 249, 141, 168, 0, 75, 75, 75, 230, 170, 134, 255, 255, 255, 0, 0, 0, 148, 155, 255, 97, 128, 138], sycc.Samples);
        var ycbcr = JpxDecoder.Decode(Jpx(cs, [Enumerated(3)]), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, ycbcr.Color);
        Assert.Null(ycbcr.IccProfile);
        Assert.Equal(new byte[] { 255, 164, 255, 128, 171, 0, 75, 75, 75, 255, 161, 126, 255, 255, 255, 0, 0, 0, 132, 155, 255, 64, 131, 140 }, ycbcr.Samples);
    }

    /// <summary>
    /// sYCC of colours inside sRGB (12-bit codes written forward from eight sRGB colours) stays plain RGB, bit for bit as before: the same samples
    /// as YCbCr(2) of the codes, R′G′B′ rounded once.
    /// </summary>
    [Fact]
    public void Sycc_ColoursInsideSrgb_PlainRgbAsBefore()
    {
        int[] y = [1794, 1985, 2047, 4095, 0, 2296, 3312, 888], cb = [1498, 3008, 2048, 2048, 2048, 1677, 410, 2934], cr = [3105, 924, 2048, 2048, 2048, 1141, 2314, 2291];
        var cs = JpxTests.AnnexG.Lossless(4, 2, [(12, false), (12, false), (12, false)], [y, cb, cr]);
        var sycc = JpxDecoder.Decode(Jpx(cs, [Enumerated(18)]), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, sycc.Color);
        Assert.Null(sycc.IccProfile);
        Assert.Equal(new byte[] { 204, 76, 51, 25, 153, 230, 127, 127, 127, 255, 255, 255, 0, 0, 0, 64, 191, 102, 229, 230, 25, 77, 26, 153 }, sycc.Samples);
        Assert.Equal(JpxDecoder.Decode(Jpx(cs, [Enumerated(3)]), long.MaxValue).Samples, sycc.Samples);
    }

    /// <summary>
    /// YCCK (T.801 Table M.25, value 13) of 12 and 16 bits: Y, Cb, Cr as YCbCr(2) from their full codes back to R′G′B′, C, M, Y = 255 less the
    /// rounded R′, G′, B′, K = its code rounded to 8 bits. Before Y, Cb, Cr were cut to their top 8 bits and converted in fixed point — 12 and 9
    /// of the 32 values were different.
    /// </summary>
    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    public void Ycck_FromTheFullCodes(int bits)
    {
        var (y, cb, cr, k, expected) = bits == 12
            ? (new[] { 4095, 0, 2047, 1233, 3273, 2049, 7, 4086 }, new[] { 2048, 2048, 1045, 2826, 2033, 2063, 2048, 2047 },
                new[] { 2048, 2048, 2387, 1817, 3548, 2033, 2048, 2050 }, new[] { 0, 4095, 1367, 1287, 2047, 2063, 4094, 9 },
                new byte[] { 0, 0, 0, 0, 255, 255, 255, 255, 98, 121, 238, 85, 198, 185, 92, 80, 0, 118, 53, 127, 129, 127, 126, 128, 255, 255, 255, 255, 0, 1, 1, 1 })
            : (new[] { 65535, 0, 32767, 19665, 52425, 32769, 7, 65526 }, new[] { 32768, 32768, 16765, 45201, 32753, 32783, 32768, 32767 },
                new[] { 32768, 32768, 38102, 29207, 56776, 32753, 32768, 32770 }, new[] { 0, 65535, 21847, 20487, 32767, 32783, 65534, 9 },
                new byte[] { 0, 0, 0, 0, 255, 255, 255, 255, 98, 121, 238, 85, 198, 185, 93, 80, 0, 118, 51, 127, 128, 127, 127, 128, 255, 255, 255, 255, 0, 0, 0, 0 });
        var cs = JpxTests.AnnexG.Lossless(4, 2, [(bits, false), (bits, false), (bits, false), (bits, false)], [y, cb, cr, k]);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(13)]), long.MaxValue);
        Assert.Equal(JpxColor.Cmyk, image.Color);
        Assert.Equal(expected, image.Samples);
    }

    // ---- No. 1073: reserved colour specification methods in a JPX file

    /// <summary>
    /// T.801 Table M.22: METH 0 and 6 to 255 are reserved — a box with such a method ahead of sRGB, even of higher precedence and with an ICC
    /// profile as its data, is passed over (the image of sRGB), and since 23.11.2026 the image counts as damaged (decided: read and report);
    /// the methods 1 to 5 of the table are no damage.
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(6, true)]
    [InlineData(9, true)]
    [InlineData(255, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    public void ColourMethod_ReservedInAJpxFile_PassedOverAndReported(int method, bool reported)
    {
        var cs = Jp2HeaderBoxes.Three();
        var first = method switch
        {
            1 => Enumerated(17, 1, 1),                                          // sRGB grey: not fitting three channels, passed over
            5 => Box("colr", [5, 1, 1], U16(1), U16(13), U16(0), [0x80]),      // BT.709 primaries, sRGB curve, G, B, R: taken
            _ => Box("colr", [(byte)method, 1, 1], CalibratedProfile.RommRgb()),
        };
        var image = JpxDecoder.Decode(Jpx(cs, [first, Enumerated(16, 0, 1)]), long.MaxValue);
        Assert.Equal(reported, image.Truncated);
        if (method is not (2 or 3 or 5)) Assert.Equal(AnnexI.Interleaved(AnnexI.Ycc), image.Samples);
    }
}
