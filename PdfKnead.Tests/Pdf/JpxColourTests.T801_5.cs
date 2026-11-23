using System.Text;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics.Icc;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// ITU-T T.801 M.5, M.7 and M.9 as the JPX baseline reader of ISO 32000-2, 7.4.9 has them — the first compositing layer, what it passes over
/// (several codestreams in one layer, further layers, composition and animation, reserved and user values, JPEG XR) and what it refuses —, and
/// PIMA 7667 (e-sRGB, e-sYCC) checked against values worked out here from its equations.
/// </summary>
public sealed partial class JpxColourTests
{
    /// <summary>A JPX file from the given boxes after signature and file type (brand „jpx “, list „jpx jpxb“).</summary>
    private static byte[] AnnexMFile(params byte[][] boxes) =>
    [
        .. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]),
        .. Box("ftyp", Encoding.ASCII.GetBytes("jpx "), U32(0), Encoding.ASCII.GetBytes("jpx jpxb")),
        .. boxes.SelectMany(b => b),
    ];

    /// <summary>Channel Definition box: N, then per channel Cn, Typ (0 colour), Asoc (colour number).</summary>
    private static byte[] AnnexMChannels(params int[] association) =>
        Box("cdef", [0, (byte)association.Length, .. association.SelectMany((a, i) => new byte[] { 0, (byte)i, 0, 0, 0, (byte)a })]);

    /// <summary>
    /// T.801 M.5.1 (sentences 9, 10): the channels of a layer take their meaning from its header — a Channel Definition box in the first
    /// Compositing Layer Header box takes the place of the one in the JP2 Header box: there the order red, green, blue, in the layer blue, green,
    /// red (and the other way round).
    /// </summary>
    [TestPdfsFact]
    public void CompositingLayer_ChannelDefinition_TakesThePlaceOfTheHeaders()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var reversed = Enumerable.Range(0, plain.Length / 3).SelectMany(i => new[] { plain[i * 3 + 2], plain[i * 3 + 1], plain[i * 3] }).ToArray();
        var layer = JpxDecoder.Decode(Jpx(cs, [Enumerated(16), AnnexMChannels(1, 2, 3)], Box("jplh", AnnexMChannels(3, 2, 1))), long.MaxValue);
        Assert.Equal(reversed, layer.Samples);
        var back = JpxDecoder.Decode(Jpx(cs, [Enumerated(16), AnnexMChannels(3, 2, 1)], Box("jplh", AnnexMChannels(1, 2, 3))), long.MaxValue);
        Assert.Equal(plain, back.Samples);
    }

    /// <summary>
    /// T.801 M.5.1 (sentences 3, 6) and M.5.1.2 (sentences 1, 4): a Codestream Registration box in the first layer joins two codestreams into
    /// one layer — the JPX baseline layer has one codestream (M.9.2.2), so the image is the first codestream's: its three channels, its samples.
    /// </summary>
    [TestPdfsFact]
    public void CompositingLayer_RegistrationOfTwoCodestreams_TheFirstCodestreamShown()
    {
        var cs = Rgb.Value;
        // XS, YS, then per codestream CDN, XR, YR, XO, YO
        var creg = Box("creg", [0, 1, 0, 1, 0, 0, 1, 1, 0, 0, 0, 1, 1, 1, 0, 0]);
        var image = JpxDecoder.Decode(AnnexMFile(Box("jp2h", Enumerated(16, 0, 1)), Box("jplh", Box("cgrp", Enumerated(16, 0, 1)), creg),
            Box("jp2c", cs), Box("jp2c", Gray.Value)), long.MaxValue);
        Assert.Equal((3, 40, 27), (image.Channels, image.Width, image.Height));
        Assert.Equal(Plain(cs), image.Samples);
    }

    /// <summary>
    /// T.801 M.5.2.4 (sentences 1, 4 to 8, 13): a Compositing Layer Extensions box with a layer header of its own (sYCC) after the first layer
    /// (sRGB) adds layers a baseline reader does not show — the image stays that of the first layer, not converted from sYCC.
    /// </summary>
    [TestPdfsFact]
    public void CompositingLayerExtensions_FurtherLayers_ImageOfTheFirstLayer()
    {
        var cs = Rgb.Value;
        var extensions = Box("jclx", Box("jlhd", U32(1), U32(1), [0, 0]), Box("jplh", Box("cgrp", Enumerated(18, 0, 1))));
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(18)], Box("jplh", Box("cgrp", Enumerated(16, 0, 1))), extensions), long.MaxValue);
        Assert.Equal(Plain(cs), image.Samples);
    }

    /// <summary>A Composition box: options (height, width, global repetition) and one instruction set (Ityp, REPT, TICK, the instruction).</summary>
    private static byte[] AnnexMComposition(uint height, uint width, byte repeat, int type, int rept, uint tick, params uint[] instruction) =>
        Box("comp", Box("copt", U32(height), U32(width), [repeat]),
            Box("inst", U16(type), U16(rept), U32(tick), [.. instruction.SelectMany(U32)]));

    /// <summary>
    /// T.801 M.5.3 (sentences 1, 6 to 9, 11 to 13), M.5.3.1 (sentences 1, 2), M.5.3.2 (sentence 2), M.5.3.2.1 (sentences 4 to 7), M.5.3.2.2
    /// (sentences 5, 7, 9), M.5.3.2.3 (sentences 2 to 4): a baseline reader shows the first layer and passes over the Composition box — a render
    /// area twice the size with an offset of 10/10, an instruction with LIFE, one with LIFE 0 and PERSIST, one with NEXT-USE 1 and a repetition,
    /// a looping animation (GREPT 3 and 255): size, channels and samples stay those of the codestream.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("area")]
    [InlineData("life")]
    [InlineData("persist")]
    [InlineData("next-use")]
    [InlineData("loop")]
    [InlineData("loop forever")]
    public void Composition_PassedOver_ImageOfTheFirstLayer(string variant)
    {
        var cs = Rgb.Value;
        var comp = variant switch
        {
            "area" => AnnexMComposition(54, 80, 0, 0x0003, 0, 0, 10, 10, 40, 27),
            "life" => AnnexMComposition(27, 40, 0, 0x0008, 0, 40, 5, 0),
            "persist" => AnnexMComposition(27, 40, 0, 0x0008, 0, 40, 0x80000000, 0),
            "next-use" => AnnexMComposition(27, 40, 0, 0x000B, 3, 40, 0, 0, 40, 27, 2, 1),
            "loop" => AnnexMComposition(27, 40, 3, 0x0008, 0, 40, 1, 0),
            _ => AnnexMComposition(27, 40, 255, 0x0008, 0, 40, 1, 0),
        };
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(16, 0, 1)], Box("jplh", Box("cgrp", Enumerated(16, 0, 1))), comp), long.MaxValue);
        Assert.Equal((3, 40, 27), (image.Channels, image.Width, image.Height));
        Assert.Equal(Plain(cs), image.Samples);
    }

    /// <summary>
    /// T.801 M.7.2.1 (sentences 7, 8), Table M.11, first row: an enumerated colour space not (yet) assigned (99), even of the highest precedence,
    /// is passed over — the next specification counts, here sYCC (converted to RGB).
    /// </summary>
    [TestPdfsFact]
    public void ReservedItems_UnknownEnumeratedColourSpace_TheNextCounts()
    {
        var cs = Rgb.Value;
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(99, 5, 1), Enumerated(18, 0, 1)]), long.MaxValue);
        NearSycc(Plain(cs), image);
    }

    /// <summary>
    /// T.801 M.7.2.1 (sentences 7, 8), Table M.11, second row: a Desired Reproductions box with a box of a kind not (yet) defined, in the JP2
    /// Header box and at the top level, leaves the image as it is.
    /// </summary>
    [TestPdfsFact]
    public void ReservedItems_DesiredReproductionWithAnUnknownBox_ImageUnchanged()
    {
        var cs = Rgb.Value;
        var reproduction = Box("drep", Box("zzzz", [1, 2, 3, 4, 5, 6, 7, 8]));
        var plain = JpxDecoder.Decode(Jpx(cs, [Enumerated(18)]), long.MaxValue).Samples;
        Assert.Equal(plain, JpxDecoder.Decode(Jpx(cs, [Enumerated(18), reproduction]), long.MaxValue).Samples);
        Assert.Equal(plain, JpxDecoder.Decode(Jpx(cs, [Enumerated(18)], reproduction), long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 M.7.2.1 (sentences 7, 8), Table M.11, third row: a compatibility list with only an entry not (yet) defined („abcd“) reads as one with
    /// the baseline entry „jpxb“.
    /// </summary>
    [TestPdfsFact]
    public void ReservedItems_UnknownCompatibilityEntry_ReadAsWithBaseline()
    {
        var cs = Rgb.Value;
        var unknown = JpxDecoder.Decode(AnnexMFallbackFile(cs, "abcd"), long.MaxValue);
        Assert.Equal(JpxDecoder.Decode(AnnexMFallbackFile(cs, "jpxb"), long.MaxValue).Samples, unknown.Samples);
        Assert.Equal(Plain(cs), unknown.Samples);
    }

    /// <summary>
    /// T.801 M.7.3 (sentences 4 to 6), Table M.12, third row: a Binary Filter box naming a filter of its own (a made-up UUID) leaves the image as
    /// it is — the reader passes it over.
    /// </summary>
    [TestPdfsFact]
    public void UserItems_BinaryFilterOfItsOwn_ImageUnchanged()
    {
        var cs = Rgb.Value;
        byte[] filter = [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0x4D, 0xEF, 0x81, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF];
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(16, 0, 1)], Box("bfil", filter, [9, 8, 7, 6, 5, 4, 3, 2, 1])), long.MaxValue);
        Assert.Equal(Plain(cs), image.Samples);
    }

    /// <summary>
    /// T.801 M.9.2 (sentences 4, 5), M.9.2.10 (sentence 2), M.9.2.11 (sentences 1, 4), M.9.2.17 (sentence 2), M.9.2.18 (sentence 2), M.9.2.19
    /// (sentence 3): the profiles for JPEG XR codestreams (image header compression type 11) are not part of what ISO 32000-2, 7.4.9 asks — a
    /// codestream without the SOC marker (here a made-up JPEG XR start) gives no image.
    /// </summary>
    [Fact]
    public void JpegXrCodestream_NoImage()
    {
        byte[] xr = [.. "WMPHOTO\0"u8, 0x11, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F];
        var header = Box("ihdr", U32(27), U32(40), U16(3), [7, 11, 0, 0]);
        Assert.Contains("kein Datenstrom", Assert.Throws<JpxException>(() => JpxDecoder.Decode(Jpx(xr, [header, Enumerated(16, 0, 1)]), long.MaxValue)).Message);
    }

    /// <summary>
    /// T.801 M.9.2.3 (sentence 3): beyond the irreversible decorrelation a baseline reader needs no part of the multiple component
    /// transformation — a reversible decorrelation (Tmcc bit 16) and a wavelet-based collection (Xmcc 3, with Omcc) are refused, not drawn
    /// wrong; the irreversible decorrelation of the same component goes through.
    /// </summary>
    [Fact]
    public void Part2_ReversibleDecorrelationAndWaveletCollection_Refused()
    {
        // Zmcc, Imcc, Ymcc, Qmcc 1; then Xmcc, Nmcc 1, input 0, Mmcc 1, output 0, Tmcc (and Omcc for Xmcc 3)
        static float[][] Run(byte kind, byte[] tmcc, byte[] omcc)
        {
            var header = new JpxPart2Header();
            byte[] mcc = [0, 0, 0, 0, 0, 0, 1, kind, 0, 1, 0, 0, 1, 0, .. tmcc, .. omcc];
            JpxPart2.ReadMcc(header, mcc, 0, mcc.Length);
            header.Order = [0];
            return JpxPart2.Transform(header, new JpxPart2Header(), [[1, 2, 3, 4]], 1);
        }
        Assert.Equal([1f, 2f, 3f, 4f], Run(0x01, [0, 0, 0], [])[0]);
        Assert.Contains("nicht unterstützt", Assert.Throws<JpxException>(() => Run(0x01, [1, 0, 0], [])).Message);
        Assert.Contains("nicht unterstützt", Assert.Throws<JpxException>(() => Run(0x03, [0, 0, 0], [0, 0, 0, 0])).Message);
    }

    /// <summary>
    /// T.801 M.9.2.4 (sentence 4): CIEJab (EnumCS 19) is in the baseline list, but ISO 32000-2, 7.4.9 leaves it out — a specification of it of
    /// higher precedence is passed over and the next counts, here sYCC (converted to RGB).
    /// </summary>
    [TestPdfsFact]
    public void CieJab_PassedOver_TheNextCounts()
    {
        var cs = Rgb.Value;
        byte[] parameters = [0, 0, 0, 100, 0, 0, 0, 0, 0, 0, 0, 100, 0, 0, 0, 0, 0, 0, 0, 100, 0, 0, 0, 0];
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(19, 5, 1, parameters), Enumerated(18, 0, 1)]), long.MaxValue);
        NearSycc(Plain(cs), image);
    }

    // ---- PIMA 7667: e-sRGB and e-sYCC

    /// <summary>The Bradford cone response matrix.</summary>
    private static readonly double[,] PimaCone = { { 0.8951, 0.2664, -0.1614 }, { -0.7502, 1.7135, 0.0367 }, { 0.0389, -0.0685, 1.0296 } };

    /// <summary>The D50 the sRGB colorants of the colour management were adapted to (the sum of the three colorants).</summary>
    private static readonly double[] PimaD50 = [0.96422, 1, 0.82521];

    /// <summary>D65 from its chromaticity x 0.3127, y 0.3290, Y 1.</summary>
    private static readonly double[] PimaD65 = [0.3127 / 0.3290, 1, (1 - 0.3127 - 0.3290) / 0.3290];

    private static double[] PimaTimes(double[,] m, double[] v) =>
        [.. Enumerable.Range(0, 3).Select(i => m[i, 0] * v[0] + m[i, 1] * v[1] + m[i, 2] * v[2])];

    /// <summary>XYZ under D50 back to D65 with Bradford (cone responses scaled by the ratio of the two whites, then back).</summary>
    private static double[] PimaToD65(double[] xyz)
    {
        var (s, d, c) = (PimaTimes(PimaCone, PimaD50), PimaTimes(PimaCone, PimaD65), PimaTimes(PimaCone, xyz));
        double[] scaled = [c[0] * d[0] / s[0], c[1] * d[1] / s[1], c[2] * d[2] / s[2]];
        // the inverse of the cone matrix by its adjugate
        var m = PimaCone;
        var det = m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
            + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        var inverse = new double[3, 3];
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                int r0 = (j + 1) % 3, r1 = (j + 2) % 3, c0 = (i + 1) % 3, c1 = (i + 2) % 3;
                inverse[i, j] = (m[r0, c0] * m[r1, c1] - m[r0, c1] * m[r1, c0]) / det;
            }
        return PimaTimes(inverse, scaled);
    }

    /// <summary>The XYZ (D50) the colour management gives an sRGB value, adapted back to D65.</summary>
    private static double[] PimaSrgbAtD65(float r, float g, float b)
    {
        Span<float> xyz = stackalloc float[3];
        IccColor.SrgbToXyz(r, g, b, xyz);
        return PimaToD65([xyz[0], xyz[1], xyz[2]]);
    }

    /// <summary>
    /// PIMA 7667, 4.3.1 (sentence 3): the white of the reference display is D65 at 80 cd/m² — the white of the sRGB colour management, adapted
    /// from D50 back to D65 and taken times 80, is 80 · x / y, 80, 80 · (1 − x − y) / y with x 0.3127, y 0.3290 (76.036, 80, 87.125) within
    /// 0.03 cd/m².
    /// </summary>
    [Fact]
    public void ESrgb_ReferenceWhite_D65()
    {
        var white = PimaSrgbAtD65(1, 1, 1);
        for (var i = 0; i < 3; i++) Assert.Equal(80 * PimaD65[i], 80 * white[i], 0.03);
        Assert.Equal(76.036, 80 * PimaD65[0], 0.001);
        Assert.Equal(87.125, 80 * PimaD65[2], 0.001);
    }

    /// <summary>
    /// PIMA 7667, 4.3.3 (sentences 3 to 13): the primaries of ITU-R BT.709 — the colorants of the sRGB colour management, adapted back to D65,
    /// have these chromaticities x, y within 0.0005.
    /// </summary>
    [Theory]
    [InlineData(0, 0.64, 0.33)]
    [InlineData(1, 0.30, 0.60)]
    [InlineData(2, 0.15, 0.06)]
    public void ESrgb_Primaries_Bt709(int channel, double x, double y)
    {
        var xyz = PimaSrgbAtD65(channel == 0 ? 1 : 0, channel == 1 ? 1 : 0, channel == 2 ? 1 : 0);
        var sum = xyz[0] + xyz[1] + xyz[2];
        Assert.Equal(x, xyz[0] / sum, 0.0005);
        Assert.Equal(y, xyz[1] / sum, 0.0005);
    }

    /// <summary>
    /// PIMA 7667, 4.5.3 (sentences 3 to 13), Equation 7: the matrix from linear e-sRGB to XYZ under D65, per column (red, green, blue) as
    /// worked out from the primaries of BT.709 and D65 (x 0.3127, y 0.3290); the colour management, adapted back to D65, gives each column within
    /// 0.0003, the rows add up to the white.
    /// </summary>
    [Theory]
    [InlineData(0, 0.41239, 0.21264, 0.01933)]
    [InlineData(1, 0.35758, 0.71517, 0.11919)]
    [InlineData(2, 0.18048, 0.07219, 0.95053)]
    public void ESrgb_MatrixToXyz_PerColumn(int channel, double x, double y, double z)
    {
        var column = PimaSrgbAtD65(channel == 0 ? 1 : 0, channel == 1 ? 1 : 0, channel == 2 ? 1 : 0);
        Assert.Equal(x, column[0], 0.0003);
        Assert.Equal(y, column[1], 0.0003);
        Assert.Equal(z, column[2], 0.0003);
        var white = PimaSrgbAtD65(1, 1, 1);
        Assert.Equal([0.95046, 1.0, 1.08906], white.Select(v => Math.Round(v, 5)));
    }

    /// <summary>
    /// PIMA 7667, 4.4.3 (sentences 10 to 13, 16 to 23) and 4.5.2 (sentences 3, 4), Equations 3 to 5 for e-sRGB10: scale 255 · 2^(10 − 9) = 510,
    /// offset 2^8 + 2^7 = 384 — the code 384 is 0, 384 + 255 is 128 (255 / 510 · 255 = 127.5, rounded up), 894 is 255.
    /// </summary>
    [Theory]
    [InlineData(384, 0)]
    [InlineData(639, 128)]
    [InlineData(640, 128)]
    [InlineData(894, 255)]
    public void ESrgb_TenBits_ScaleAndOffset(int code, int level) => Assert.Equal(level, ESrgbLevel(code, 10));

    /// <summary>
    /// PIMA 7667, 4.4.3 (sentences 28 to 31): the neutral patches Y = k / 99 — the codes of e-sRGB16, e-sRGB12 and e-sRGB10 worked out here with
    /// Equations 2c (2b for 0), 3 and 4, rounded; read back each gives the 8-bit sRGB level of Y, round(255 · (1.055 · Y^(1/2.4) − 0.055)) (10
    /// bits: within one step, the halves rounded up as the 8-bit output rounds since 23.11.2026 — before to the even level, 481 gave 48;
    /// docs/FEHLERGEDAECHTNIS.md, No. 1057).
    /// </summary>
    [Theory]
    [InlineData(0, 24576, 1536, 384, 0, 0)]
    [InlineData(1, 27856, 1741, 435, 26, 26)]
    [InlineData(3, 30803, 1925, 481, 49, 49)]
    [InlineData(7, 34199, 2137, 534, 75, 75)]
    [InlineData(14, 38023, 2376, 594, 105, 105)]
    [InlineData(29, 43426, 2714, 679, 147, 148)]
    [InlineData(59, 50536, 3158, 790, 203, 203)]
    [InlineData(79, 54126, 3383, 846, 231, 231)]
    [InlineData(99, 57216, 3576, 894, 255, 255)]
    public void ESrgb_NeutralPatches(int k, int code16, int code12, int code10, int level, int level10)
    {
        var y = k / 99.0;
        var encoded = y > 0.0031308 ? 1.055 * Math.Pow(y, 1 / 2.4) - 0.055 : 12.92 * y;
        int Code(int n) => (int)Math.Round(encoded * 255 * Math.ScaleB(1.0, n - 9) + Math.ScaleB(1.0, n - 2) + Math.ScaleB(1.0, n - 3));
        Assert.Equal((code16, code12, code10), (Code(16), Code(12), Code(10)));
        Assert.Equal(level, (int)Math.Round(255 * encoded));
        Assert.Equal(level, ESrgbLevel(code16, 16));
        Assert.Equal(level, ESrgbLevel(code12, 12));
        Assert.Equal(level10, ESrgbLevel(code10, 10));
        Assert.InRange(level10 - level, -1, 1);
    }

    /// <summary>
    /// PIMA 7667, 4.5.2 (sentences 20 to 24), Equation 6b: from −0.04045 to 0.04045 the inverse curve is linear, R′ / 12.92 — the sRGB
    /// linearisation of the colour management there: 0 gives 0, 0.02 gives 0.0015480, 0.04045 (still linear) 0.0031308.
    /// </summary>
    [Theory]
    [InlineData(0f, 0.0)]
    [InlineData(0.02f, 0.0015479876)]
    [InlineData(0.04045f, 0.0031308050)]
    public void SrgbDecode_LinearSegment(float encoded, double linear) => Assert.Equal(linear, IccColor.Decode(encoded), 1e-7);

    /// <summary>
    /// PIMA 7667, 4.5.2 (sentences 27 to 41), Equation 6c: above 0.04045 the inverse curve is ((R′ + 0.055) / 1.055)^2.4 — 0.05 gives 0.0039359,
    /// 0.5 gives 0.2140411, 1 gives 1 (within 10^−5).
    /// </summary>
    [Theory]
    [InlineData(0.05f, 0.0039359395)]
    [InlineData(0.5f, 0.2140411405)]
    [InlineData(1f, 1.0)]
    public void SrgbDecode_PowerSegment(float encoded, double linear) => Assert.Equal(linear, IccColor.Decode(encoded), 1e-5);

    /// <summary>
    /// PIMA 7667, B.2 (sentences 13 to 25), Equation B9 with Equations B6 to B8 for 8 bits, one coefficient at a time: a palette entry
    /// (Y″, Cb″, Cr″) in e-sYCC (EnumCS 24) becomes 255 · (Y′ + 2.804 Cr′, Y′ − 0.6882 Cb′ − 1.4282 Cr′, Y′ + 3.544 Cb′), rounded — Cr′ = 26 / 255
    /// gives red 172.904 and green 62.867; Cb′ = 20 / 255 green 86.236 and blue 170.880; Cb′ = −20 / 255 green 113.764 and blue 29.120;
    /// Cr′ = −26 / 255 red 27.096 and green 137.133 (the term 0.0003 Cr′ of blue stays below a hundredth of a step).
    /// </summary>
    [TestPdfsTheory]
    [InlineData(100, 128, 154, 173, 63, 100)]
    [InlineData(100, 148, 128, 100, 86, 171)]
    [InlineData(100, 108, 128, 100, 114, 29)]
    [InlineData(100, 128, 102, 27, 137, 100)]
    public void ESycc_EachCoefficientOfB9(byte y, byte cb, byte cr, byte r, byte g, byte b)
    {
        // NE = 1, NPC = 3, 8 bits each, the one entry; every pixel of the grey image (all 0) takes it
        byte[] palette = [0, 1, 3, 7, 7, 7, y, cb, cr];
        byte[] mapping = [0, 0, 1, 0, 0, 0, 1, 1, 0, 0, 1, 2];
        var image = JpxDecoder.Decode(Jpx(Gray.Value, [Enumerated(24), Box("pclr", palette), Box("cmap", mapping)]), long.MaxValue);
        Assert.Equal((JpxColor.Rgb, 3), (image.Color, image.Channels));
        for (var i = 0; i < image.Samples.Length; i += 3) Assert.Equal(new[] { r, g, b }, image.Samples[i..(i + 3)]);
    }
}
