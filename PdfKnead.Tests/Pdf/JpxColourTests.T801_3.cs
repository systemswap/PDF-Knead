using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// ITU-T T.801 Annex K, the non-linearity point transformation, step by step: its place after the multiple component transformation, the Rsiz
/// gate, the depths of CBD and of the NLT, the three forms with signed values on either side, and the parsing of the segment. Expected values are
/// worked out here from the equations (K-2, K-3, K-6 to K-9, K.2.3), apart from the decoder.
/// </summary>
public sealed partial class JpxColourTests
{
    /// <summary>A Part 2 transformation of three components: CBD (one depth for all), decorrelation array, offsets, one collection, one stage.</summary>
    private static byte[][] AnnexKTransform(byte depth, float[] matrix, float[] offsets) =>
    [
        Marker(0xFF78, U16(0x8003), [depth]),                                         // CBD: three components, all of this depth
        Marker(0xFF74, U16(0), U16(0x0901), U16(0), F32(matrix)),                     // MCT 1: decorrelation, float32
        Marker(0xFF74, U16(0), U16(0x0A01), U16(0), F32(offsets)),                    // MCT 1: offsets, float32
        Marker(0xFF75, U16(0), [0], U16(0), U16(1),                                   // MCC 0, one collection over components 0 to 2
            [0x01], U16(3), [0, 1, 2], U16(3), [0, 1, 2], [0, 0x01, 0x01]),
        Marker(0xFF77, [1, 0]),                                                       // MCO: one stage, MCC 0
    ];

    /// <summary>A parameter as the segment stores it: 8-bit integer and a 16-bit fraction of 65 535 steps.</summary>
    private static double AnnexKStored(double v) => Math.Floor(v) + Math.Round((v - Math.Floor(v)) * 65535) / 65535;

    /// <summary>Equation K-2 on a normalised value.</summary>
    private static double AnnexKGamma(double y, double e, double s, double t, double a, double b) =>
        Math.Abs(y) <= t ? y / s : Math.Sign(y) * Math.Pow((Math.Abs(y) + b) / a, 1 / e);

    /// <summary>Equations K-6 and K-8 on a normalised value: linear between the table values spread evenly over Dmin to Dmax.</summary>
    private static double AnnexKTable(double y, double[] table, double dmin, double dmax)
    {
        var delta = (dmax - dmin) / (table.Length - 1);
        y = Math.Clamp(y, dmin, dmax);
        var k = Math.Min(table.Length - 2, (int)Math.Floor((y - dmin) / delta));
        return table[k] + (y - (dmin + k * delta)) * (table[k + 1] - table[k]) / delta;
    }

    /// <summary>The curve of ITU-R BT.709 as an NLT for all components (BDnlt 8 bits): E 0.45, S 4.5, T 0.081, A 1.099, B 0.099.</summary>
    private static byte[] AnnexKBt709 => Marker(0xFF76, U16(65535), [0x07], [1], Fixed(0.45), Fixed(4.5), Fixed(0.081), Fixed(1.099), Fixed(0.099));

    private static double AnnexKBt709Of(int v) =>
        Math.Round(255 * AnnexKGamma(v / 255.0, AnnexKStored(0.45), AnnexKStored(4.5), AnnexKStored(0.081), AnnexKStored(1.099), AnnexKStored(0.099)));

    /// <summary>
    /// T.801 K (sentences 3, 4, 6), K.1.1 (sentences 5, 8), K.1.3 (sentence 1): the non-linearity together with the multiple component
    /// transformation (Rsiz 0x8300) acts on the reconstructed components, after the transformation. The transformation here gives red and green
    /// averaged in component 0, so the order shows: the curve of the average is not the average of the curves.
    /// </summary>
    [TestPdfsFact]
    public void NonLinearity_WithTheComponentTransformation_CurveOnTheTransformedValues()
    {
        var cs = Rgb.Value;
        // the inverse of the reversible component transformation, linear, with component 0 the average of red and green
        var transform = AnnexKTransform(0x07, [1, -0.25f, 0.25f, 1, -0.25f, -0.25f, 1, 0.75f, -0.25f], [128, 128, 128]);
        var reconstructed = JpxDecoder.DecodePlanes(Part2(cs, 0x8100, 2, transform), long.MaxValue).Planes;
        var both = JpxDecoder.DecodePlanes(Part2(cs, 0x8300, 2, [.. transform, AnnexKBt709]), long.MaxValue).Planes;
        for (var c = 0; c < 3; c++)
            for (var i = 0; i < both[c].Length; i++)
                Assert.Equal(AnnexKBt709Of(reconstructed[c][i]), both[c][i]);
        // the other order (curve first, then the average) gives another image
        var plain = Plain(cs);
        Assert.Contains(Enumerable.Range(0, both[0].Length), i =>
            Math.Abs(Math.Round((AnnexKBt709Of(plain[3 * i]) + AnnexKBt709Of(plain[3 * i + 1])) / 2) - both[0][i]) > 2);
    }

    /// <summary>
    /// T.801 K.1 (sentence 1): Rsiz announces the non-linearity; a codestream of ISO/IEC 15444-1 (Rsiz 0) with an NLT segment keeps its image
    /// and its 8 bits, the segment means nothing there.
    /// </summary>
    [TestPdfsFact]
    public void NonLinearity_Part1Codestream_SegmentMeansNothing()
    {
        var cs = Rgb.Value;
        Assert.Equal(Plain(cs), JpxDecoder.Decode(Part2(cs, 0, null, AnnexKBt709), long.MaxValue).Samples);
        var deep = Marker(0xFF76, U16(65535), [0x0F], [2], U16(1), [0, 0, 0, 0], [0xFF, 0xFF, 0xFF, 0xFF], [16], U16(0), U16(65535));
        var planes = JpxDecoder.DecodePlanes(Part2(cs, 0, null, deep), long.MaxValue);
        Assert.All(planes.Codestream.Output, o => Assert.Equal(8, o.Precision));
    }

    /// <summary>
    /// T.801 K.1.2 (sentences 1, 2, 9): with the multiple component transformation CBD names the reconstructed components — their depth and
    /// sign, and so their range — while SIZ keeps naming the codestream components (here three of 8 bits). The transformation is the linear
    /// inverse of the reversible component transformation (within one step of it) plus an offset: 12 bits, 300 added: the values up to 555 stay;
    /// 8 bits: the same values end at 255; 8 bits signed, offsets 0: the values around 0, shown shifted by half the range.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(0x0B, 300, 12, false)]
    [InlineData(0x07, 300, 8, false)]
    [InlineData(0x87, -128, 8, true)]
    public void ComponentTransformation_DepthAndSignFromCbd(byte depth, int added, int bits, bool signed)
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var decoded = JpxDecoder.DecodePlanes(Part2(cs, 0x8100, 2,
            AnnexKTransform(depth, [1, -0.25f, 0.75f, 1, -0.25f, -0.25f, 1, 0.75f, -0.25f], [128 + added, 128 + added, 128 + added])), long.MaxValue);
        Assert.All(decoded.Codestream.Components, c => Assert.Equal((8, false), (c.Precision, c.Signed)));
        Assert.All(decoded.Codestream.Reconstructed, c => Assert.Equal((bits, signed), (c.Precision, c.Signed)));
        Assert.All(decoded.Codestream.Output, c => Assert.Equal((bits, signed), (c.Precision, c.Signed)));
        var max = (1 << bits) - 1;
        var display = signed ? 1 << (bits - 1) : 0;
        for (var c = 0; c < 3; c++)
            for (var i = 0; i < decoded.Planes[c].Length; i++)
                Assert.InRange(decoded.Planes[c][i] - Math.Clamp(plain[3 * i + c] + added + display, 0, max), -1, 1);
        Assert.Equal(bits == 12, decoded.Planes[0].Any(v => v > 255));
    }

    /// <summary>
    /// T.801 K.1.3 (sentence 2): the NLT of the main header names the depth of the decoded component — BDnlt 16 bits over components of 8 bits
    /// (SIZ): a table from 0 to 1 gives the 16-bit output y · 65 535 / 255 = y · 257, stretched back to the same 8 bits for display.
    /// </summary>
    [TestPdfsFact]
    public void NonLinearity_DepthOfTheMainHeader_SixteenBitsOutput()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var lut = Marker(0xFF76, U16(65535), [0x0F], [2], U16(1), [0, 0, 0, 0], [0xFF, 0xFF, 0xFF, 0xFF], [16], U16(0), U16(65535));
        var decoded = JpxDecoder.DecodePlanes(Part2(cs, 0x8200, null, lut), long.MaxValue);
        Assert.All(decoded.Codestream.Output, c => Assert.Equal((16, false), (c.Precision, c.Signed)));
        for (var c = 0; c < 3; c++)
            for (var i = 0; i < decoded.Planes[c].Length; i++)
                Assert.Equal(plain[3 * i + c] * 257, decoded.Planes[c][i]);
        Assert.Equal(plain, JpxDecoder.Decode(Part2(cs, 0x8200, null, lut), long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 K.2 (sentences 1 to 4, 6): three forms (types 1 to 3); type 0 means no non-linearity, a reserved type (4, 255) is not taken — the
    /// image stays as it is.
    /// </summary>
    [TestPdfsFact]
    public void NonLinearity_TypeZeroAndReservedTypes_NoTransformation()
    {
        var header = new JpxPart2Header();
        JpxPart2.ReadNlt(header, [0, 0, 0x07, 4, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15], 0, 19);
        JpxPart2.ReadNlt(header, [0, 1, 0x07, 255], 0, 4);
        Assert.Empty(header.Nlt);
        JpxPart2.ReadNlt(header, [0, 2, 0x07, 0], 0, 4);
        Assert.Equal(0, header.Nlt[2].Type);
        Assert.Null(JpxPart2.NonLinearityOf(new JpxPart2Header(), header, 2));
        var cs = Rgb.Value;
        Assert.Equal(Plain(cs), JpxDecoder.Decode(Part2(cs, 0x8200, null, Marker(0xFF76, U16(65535), [0x07], [0])), long.MaxValue).Samples);
        Assert.Equal(Plain(cs), JpxDecoder.Decode(Part2(cs, 0x8200, null, Marker(0xFF76, U16(65535), [0x07], [4], new byte[15])), long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 K.2.1.2 (sentences 32 to 46) and K.2.2 (sentences 9 to 54): with the multiple component transformation the depth b_y of the values
    /// going into the non-linearity is that of CBD, not of SIZ — here 12 bits (the identity times 16 plus 2 048, so the values span the 12 bits)
    /// under components of 8 bits: normalised by 4 095 for the gamma form (E 0.5, so the square) and for a table of three values; the output is
    /// 8 bits (BDnlt).
    /// </summary>
    [TestPdfsFact]
    public void NonLinearity_InputDepthFromCbd_GammaAndTable()
    {
        var cs = Rgb.Value;
        var transform = AnnexKTransform(0x0B, [16, 0, 0, 0, 16, 0, 0, 0, 16], [2048, 2048, 2048]);
        var reconstructed = JpxDecoder.DecodePlanes(Part2(cs, 0x8100, 2, transform), long.MaxValue);
        Assert.All(reconstructed.Codestream.Output, c => Assert.Equal(12, c.Precision));
        Assert.Contains(reconstructed.Planes[0], v => v > 2048);

        var gamma = Marker(0xFF76, U16(65535), [0x07], [1], Fixed(0.5), Fixed(1), Fixed(0), Fixed(1), Fixed(0));
        var withGamma = JpxDecoder.DecodePlanes(Part2(cs, 0x8300, 2, [.. transform, gamma]), long.MaxValue);
        var e = AnnexKStored(0.5);
        for (var c = 0; c < 3; c++)
            for (var i = 0; i < withGamma.Planes[c].Length; i++)
                Assert.Equal(Math.Round(255 * AnnexKGamma(reconstructed.Planes[c][i] / 4095.0, e, 1, 0, 1, 0)), withGamma.Planes[c][i]);

        var lut = Marker(0xFF76, U16(65535), [0x07], [2], U16(2), [0, 0, 0, 0], [0xFF, 0xFF, 0xFF, 0xFF], [16], U16(0), U16(16384), U16(65535));
        var withTable = JpxDecoder.DecodePlanes(Part2(cs, 0x8300, 2, [.. transform, lut]), long.MaxValue);
        double[] table = [0, 16384 / 65535.0, 1];
        for (var c = 0; c < 3; c++)
            for (var i = 0; i < withTable.Planes[c].Length; i++)
                Assert.Equal(Math.Round(255 * AnnexKTable(reconstructed.Planes[c][i] / 4095.0, table, 0, 1)), withTable.Planes[c][i]);
    }

    /// <summary>
    /// T.801 K.2.1.2 (sentences 47, 48, 113, 114): the depth b_z of the decoded values is BDnlt, here 16 bits over an input of 8 bits — the
    /// identity curve (E 1, S 1, T 0, A 1, B 0) gives y / 255 · 65 535.
    /// </summary>
    [Theory]
    [InlineData(255, 65535)]
    [InlineData(51, 13107)]
    [InlineData(0, 0)]
    public void Gamma_OutputDepthFromBdnlt(int y, double z) =>
        Assert.Equal(z, JpxPart2.Apply(new JpxNonLinearity(16, false, 1, [1, 1, 0, 1, 0], null, 0, 1), y, 8, false), 1e-9);

    /// <summary>
    /// T.801 K.2.1.2 (sentences 49 to 51), Equation K-2 with E 0.5, S 2, T 0.2, A 1, B 0: linear (y / S) for |y| up to T including T, the power
    /// above it, odd for negative values. Signed 8 bits: −64 / 127 lies below −T, −20 / 127 between −T and 0; unsigned 8 bits: 51 / 255 is T
    /// exactly (still linear: 0.1 · 255), 52 / 255 lies above it.
    /// </summary>
    [Theory]
    [InlineData(-64, true, -32.251968503937)]
    [InlineData(-20, true, -10.0)]
    [InlineData(64, true, 32.251968503937)]
    [InlineData(51, false, 25.5)]
    [InlineData(52, false, 10.603921568627)]
    public void Gamma_BothBranchesBothSigns(int y, bool signed, double z) =>
        Assert.Equal(z, JpxPart2.Apply(new JpxNonLinearity(8, signed, 1, [0.5, 2, 0.2, 1, 0], null, 0, 1), y, 8, signed), 1e-9);

    /// <summary>
    /// T.801 K.2.1.2 (sentences 97 to 106), Equation K-3 for signed values: f = 2^(b − 1) − 1 on both sides — 8 bits in and out: ±127 stay;
    /// 8 bits in, 16 bits out: 127 gives 32 767, −64 gives −64 / 127 · 32 767.
    /// </summary>
    [Theory]
    [InlineData(127, 8, 127)]
    [InlineData(-127, 8, -127)]
    [InlineData(127, 16, 32767)]
    [InlineData(-64, 16, -16512.503937007874)]
    public void Gamma_SignedNormalisation(int y, int outputBits, double z) =>
        Assert.Equal(z, JpxPart2.Apply(new JpxNonLinearity(outputBits, true, 1, [1, 1, 0, 1, 0], null, 0, 1), y, 8, true), 1e-9);

    /// <summary>
    /// T.801 K.2.2 (sentences 70 to 78, 85 to 115), Equation K-7 for a signed input: (y + 2^(b − 1)) / (2^b − 1) — the table 0, 1 over the whole
    /// range, output unsigned 8 bits: −128 gives 0, −1 gives 127, 0 gives 128, 127 gives 255.
    /// </summary>
    [Theory]
    [InlineData(-128, 0)]
    [InlineData(-1, 127)]
    [InlineData(0, 128)]
    [InlineData(127, 255)]
    public void Table_SignedInput(int y, double z) =>
        Assert.Equal(z, JpxPart2.Apply(new JpxNonLinearity(8, false, 2, [], [0, 1], 0, 1), y, 8, true), 1e-9);

    /// <summary>
    /// T.801 K.2.2 (sentences 85 to 102, 154 to 171, 175), Equation K-9 for a signed output: z_norm · (2^b − 1) − 2^(b − 1) — the table 0, 1,
    /// unsigned 8-bit input, output signed 8 bits: 0 gives −128, 128 gives 0, 255 gives 127.
    /// </summary>
    [Theory]
    [InlineData(0, -128)]
    [InlineData(128, 0)]
    [InlineData(255, 127)]
    public void Table_SignedOutput(int y, double z) =>
        Assert.Equal(z, JpxPart2.Apply(new JpxNonLinearity(8, true, 2, [], [0, 1], 0, 1), y, 8, false), 1e-9);

    /// <summary>
    /// T.801 K.2.3 (sentence 12): the binary complement to sign-magnitude with the depth b of its input, here 16 bits signed: a negative y
    /// becomes −2^(b − 1) − y − 1 — −1 gives −32 768, −32 768 gives −1, −2 gives −32 767; positive values stay.
    /// </summary>
    [Theory]
    [InlineData(-1, -32768)]
    [InlineData(-32768, -1)]
    [InlineData(-2, -32767)]
    [InlineData(5, 5)]
    public void Complement_SixteenBits(int y, double z) =>
        Assert.Equal(z, JpxPart2.Apply(new JpxNonLinearity(16, true, 3, [], null, 0, 1), y, 16, true));

    /// <summary>
    /// T.801 K.2.3 (sentence 12) through the decoder: a codestream with signed components in SIZ (8 bits) and an NLT of type 3 with BDnlt 8 bits
    /// signed. A negative value y becomes −129 − y; shown shifted by 128 a value q below 128 becomes 127 − q, the others stay.
    /// </summary>
    [TestPdfsFact]
    public void Complement_SignedCodestream_ThroughTheDecoder()
    {
        byte[] Signed(byte[] d)
        {
            for (var c = 0; c < 3; c++) d[42 + 3 * c] |= 0x80;
            return d;
        }
        var cs = Rgb.Value;
        var without = JpxDecoder.DecodePlanes(Signed(Part2(cs, 0x8200, null)), long.MaxValue);
        var with = JpxDecoder.DecodePlanes(Signed(Part2(cs, 0x8200, null, Marker(0xFF76, U16(65535), [0x87], [3]))), long.MaxValue);
        Assert.All(with.Codestream.Output, c => Assert.Equal((8, true), (c.Precision, c.Signed)));
        Assert.Contains(without.Planes[0], q => q < 128);
        for (var c = 0; c < 3; c++)
            for (var i = 0; i < with.Planes[c].Length; i++)
            {
                var q = without.Planes[c][i];
                Assert.Equal(q < 128 ? 127 - q : q, with.Planes[c][i]);
            }
    }

    /// <summary>
    /// T.801 K.2.2 (sentences 8, 176, 177): the table form from the segment — Dmin and Dmax as 32-bit fractions of 2^32 − 1 (0x40000000 and
    /// 0xC0000000 give 0.25 and 0.75 within 2·10^−10), the values T_k of PTval bits in 1, 2 or 4 bytes, divided by 2^PTval − 1.
    /// </summary>
    [Theory]
    [InlineData(8, new byte[] { 0, 128, 255 }, new[] { 0, 128 / 255.0, 1 })]
    [InlineData(12, new byte[] { 0, 0, 0x08, 0x00, 0x0F, 0xFF }, new[] { 0, 2048 / 4095.0, 1 })]
    [InlineData(32, new byte[] { 0, 0, 0, 0, 0x80, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF }, new[] { 0, 2147483648 / 4294967295.0, 1 })]
    public void TableSegment_RangeAndValueSizes(byte bits, byte[] values, double[] expected)
    {
        var header = new JpxPart2Header();
        byte[] segment = [0, 0, 0x07, 2, 0, 2, 0x40, 0, 0, 0, 0xC0, 0, 0, 0, bits, .. values];
        JpxPart2.ReadNlt(header, segment, 0, segment.Length);
        var nlt = header.Nlt[0];
        Assert.Equal(0.25000000005820766, nlt.Dmin, 1e-15);
        Assert.Equal(0.750000000174623, nlt.Dmax, 1e-15);
        Assert.Equal(expected.Length, nlt.Table!.Length);
        for (var i = 0; i < expected.Length; i++) Assert.Equal(expected[i], nlt.Table[i], 1e-12);
    }

    /// <summary>
    /// T.801 K.2.2 (sentences 8, 79): at least two values — Npoints field 0 (one value) is not taken; a segment shorter than Npoints values is
    /// not taken either. Field 1 with its two values is.
    /// </summary>
    [Fact]
    public void TableSegment_TooFewValues_NotTaken()
    {
        var header = new JpxPart2Header();
        byte[] head = [0x07, 2];
        byte[] range = [0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 16];
        byte[] one = [0, 1, .. head, 0, 0, .. range, 0x12, 0x34];
        byte[] short3 = [0, 2, .. head, 0, 2, .. range, 0, 0, 0xFF, 0xFF];
        byte[] two = [0, 3, .. head, 0, 1, .. range, 0, 0, 0xFF, 0xFF];
        JpxPart2.ReadNlt(header, one, 0, one.Length);
        JpxPart2.ReadNlt(header, short3, 0, short3.Length);
        Assert.Empty(header.Nlt);
        JpxPart2.ReadNlt(header, two, 0, two.Length);
        Assert.Equal([3], header.Nlt.Keys);
        Assert.Equal([0.0, 1.0], header.Nlt[3].Table!);
    }
}
