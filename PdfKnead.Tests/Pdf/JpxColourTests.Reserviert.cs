using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Reserved and invalid values in the boxes of a JP2 file (ITU-T T.800, Annex I) and in the marker segments of ISO/IEC 15444-2 (ITU-T T.801,
/// Annexes A, J, K): the decoder shows the image as well as it can and reports it as damaged (decided on 23.11.2026;
/// docs/FEHLERGEDAECHTNIS.md, No. 1049 and No. 1050). Each case is set against the same file with a defined value in its place.
/// </summary>
public sealed partial class JpxColourTests
{
    private static JpxImage Reported(byte[] file) => JpxDecoder.Decode(file, long.MaxValue);

    /// <summary>
    /// T.800 I.4: LBox 2 to 7 is reserved. Behind the codestream box the box reaches to the end, as before; the image is shown and reported
    /// damaged (before without a word). In front of it the box swallows the codestream box: no image, as before and as two of the three
    /// reference readers have it.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    public void BoxLength_Reserved_ReportedOrNoImage(int length)
    {
        var cs = Jp2HeaderBoxes.Three();
        byte[][] header = [Jp2HeaderBoxes.Ihdr(2, 3, 3, 7), Enumerated(16)];
        var plain = Jp2HeaderBoxes.File(cs, header);
        byte[] reserved = [0, 0, 0, (byte)length, (byte)'x', (byte)'x', (byte)'x', (byte)'x'];
        var expected = Reported(plain);
        Assert.False(expected.Truncated);
        var behind = Reported([.. plain, .. reserved]);
        Assert.Equal(expected.Samples, behind.Samples);
        Assert.True(behind.Truncated);
        var before = AnnexI.Jp2(Box("jp2h", header), reserved, Box("jp2c", cs));
        Assert.Throws<JpxException>(() => Reported(before));
    }

    /// <summary>
    /// T.800 Table I.13: a palette column of 39 to 128 bits is reserved. The column is read with its top 38 bits — the same colours as with 8
    /// bits where the entries carry the same top bits (stored in 5 and 16 bytes) — and the image is reported damaged; before the entry ran over
    /// the 64-bit register and the shifts by more than 63 bits wrapped round.
    /// </summary>
    [Theory]
    [InlineData(0x26, 5)]
    [InlineData(0x7F, 16)]
    public void PaletteColumn_ReservedDepth_TopBitsAndReported(int bi, int bytes)
    {
        var cs = JpxTests.AnnexG.Lossless8(4, 1, [0, 1, 2, 3]);
        byte[][] colours = [[10, 20, 30], [40, 50, 60], [200, 100, 0], [255, 128, 1]];
        var depth = (bi & 0x7F) + 1;
        byte[] Entry(byte v) => [.. Enumerable.Range(0, bytes).Select(b => (byte)((UInt128)v << (depth - 8) >> (8 * (bytes - 1 - b))))];
        byte[] File(byte[] pclr) => Jp2HeaderBoxes.File(cs, [Jp2HeaderBoxes.Ihdr(1, 4, 1, 7), Enumerated(16), pclr,
            Box("cmap", [0, 0, 1, 0], [0, 0, 1, 1], [0, 0, 1, 2])]);
        var eight = File(Box("pclr", [0, 4, 3, 7, 7, 7], [.. colours.SelectMany(c => c)]));
        var deep = File(Box("pclr", [0, 4, 3, (byte)bi, (byte)bi, (byte)bi], [.. colours.SelectMany(c => c.SelectMany(Entry))]));
        var expected = Reported(eight);
        Assert.Equal(colours.SelectMany(c => c), expected.Samples);
        Assert.False(expected.Truncated);
        var image = Reported(deep);
        Assert.Equal(expected.Samples, image.Samples);
        Assert.True(image.Truncated);
    }

    /// <summary>
    /// T.800 I.5.3.5: a channel of the component mapping names a component the codestream does not have (CMP 7 of three), or has the reserved
    /// type MTYP 2 (Table I.14). As before the last component is taken, and the reserved type reads the component directly like 0; the image is
    /// reported damaged.
    /// </summary>
    [Theory]
    [InlineData(7, 0)]
    [InlineData(2, 2)]
    [InlineData(2, 255)]
    public void ComponentMapping_Invalid_ReadAsBeforeAndReported(int component, int type)
    {
        var cs = Jp2HeaderBoxes.Three();
        byte[] File(int cmp, int mtyp) => Jp2HeaderBoxes.File(cs, [Jp2HeaderBoxes.Ihdr(2, 3, 3, 7), Enumerated(16),
            Box("pclr", [0, 2, 1, 7, 0, 255]), Box("cmap", [0, 0, 0, 0], [0, 1, 0, 0], [(byte)(cmp >> 8), (byte)cmp, (byte)mtyp, 0])]);
        var expected = Reported(File(2, 0));
        Assert.False(expected.Truncated);
        var image = Reported(File(component, type));
        Assert.Equal(expected.Samples, image.Samples);
        Assert.True(image.Truncated);
    }

    /// <summary>
    /// T.800 Tables I.16 and I.17: a colour channel (type 0) whose association 65 535 says it belongs to no colour. It stays a colour channel
    /// in its place, as before, and the image is reported damaged — in the JP2 Header box and in the first compositing layer.
    /// </summary>
    [Fact]
    public void ChannelDefinition_ColourWithoutAssociation_Reported()
    {
        var cs = Jp2HeaderBoxes.Three();
        byte[] Cdef(int third) => Box("cdef", U16(3), U16(0), U16(0), U16(1), U16(1), U16(0), U16(2), U16(2), U16(0), U16(third));
        byte[] File(int third) => Jp2HeaderBoxes.File(cs, [Jp2HeaderBoxes.Ihdr(2, 3, 3, 7), Enumerated(16), Cdef(third)]);
        var expected = Reported(File(3));
        Assert.False(expected.Truncated);
        var image = Reported(File(65535));
        Assert.Equal(expected.Samples, image.Samples);
        Assert.True(image.Truncated);
        var layer = Reported(Jpx(cs, [Jp2HeaderBoxes.Ihdr(2, 3, 3, 7), Enumerated(16)], Box("jplh", Cdef(65535))));
        Assert.Equal(expected.Samples, layer.Samples);
        Assert.True(layer.Truncated);
    }

    // ---- ISO/IEC 15444-2

    /// <summary>
    /// T.801 K.1: the non-linearity is announced in Rsiz (bit 9). An NLT in a codestream of Part 2 without that bit is applied, as before, and
    /// the image is reported damaged; with the bit the same image, not reported.
    /// </summary>
    [TestPdfsFact]
    public void Nlt_WithoutItsRsizBit_AppliedAndReported()
    {
        var announced = Reported(Part2(Rgb.Value, 0x8200, null, T801Gamma(65535)));
        Assert.False(announced.Truncated);
        Assert.NotEqual(Plain(Rgb.Value), announced.Samples);
        var unannounced = Reported(Part2(Rgb.Value, 0x8000, null, T801Gamma(65535)));
        Assert.Equal(announced.Samples, unannounced.Samples);
        Assert.True(unannounced.Truncated);
    }

    /// <summary>
    /// T.801 Table A.42: Cnlt 16 384 to 65 534 is reserved. The NLT applies to no component — the image as without it, as before — and is
    /// reported.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(16384)]
    [InlineData(20000)]
    [InlineData(65534)]
    public void Nlt_ReservedComponent_NoneAndReported(int component)
    {
        var image = Reported(Part2(Rgb.Value, 0x8200, null, T801Gamma(component)));
        Assert.Equal(Plain(Rgb.Value), image.Samples);
        Assert.True(image.Truncated);
    }

    /// <summary>
    /// T.801 A.3.10 and K.2.2: what ReadNlt counts as damaged or reserved — Npoints above 8 191 (Table A.46: the field 8 192), Dmax not above
    /// Dmin (K-6), a reserved type (4), BDnlt of 39 bits (Table A.43), PTval 0 and 33, fewer than two values, a segment too short — and what not
    /// (the field 8 191 with Dmin below Dmax, gamma, the types 0 and 3).
    /// </summary>
    [Theory]
    [InlineData("lut 8192", true)]
    [InlineData("lut 8191", false)]
    [InlineData("dmax=dmin", true)]
    [InlineData("dmax<dmin", true)]
    [InlineData("type 4", true)]
    [InlineData("depth 39", true)]
    [InlineData("ptval 0", true)]
    [InlineData("ptval 33", true)]
    [InlineData("one value", true)]
    [InlineData("short", true)]
    [InlineData("gamma", false)]
    [InlineData("type 3", false)]
    public void Nlt_DamagedOrReservedValues(string kind, bool damaged)
    {
        var segment = kind switch
        {
            "lut 8192" => T801Lut(65535, 0, 0xFFFFFFFF, 8, [.. Enumerable.Range(0, 8193).Select(i => (ulong)(i * 255 / 8192))]),
            "lut 8191" => T801Lut(65535, 0, 0xFFFFFFFF, 8, [.. Enumerable.Range(0, 8192).Select(i => (ulong)(i * 255 / 8191))]),
            "dmax=dmin" => T801Lut(65535, 0x80000000, 0x80000000, 8, 200, 100, 50),
            "dmax<dmin" => T801Lut(65535, 0x90000000, 0x80000000, 8, 200, 100, 50),
            "type 4" => T801Nlt(65535, 4),
            "depth 39" => Marker(0xFF76, U16(65535), [0x26], [3]),
            "ptval 0" => T801Lut(65535, 0, 0xFFFFFFFF, 0, 0, 255),
            "ptval 33" => T801Lut(65535, 0, 0xFFFFFFFF, 33, 0, 255),
            "one value" => T801Lut(65535, 0, 0xFFFFFFFF, 8, 255),
            "short" => Marker(0xFF76, U16(65535), [0x07]),
            "gamma" => T801Gamma(65535),
            _ => T801Nlt(65535, 3),
        };
        var header = new JpxPart2Header();
        JpxPart2.ReadNlt(header, segment, 4, segment.Length);
        Assert.Equal(damaged, header.Damaged);
    }

    /// <summary>
    /// T.801 K.2.2, Equation K-6: with Dmax equal to Dmin every value clips to Dmin, where the table gives T_0 — 200 at 8 bits for every
    /// sample — and the image is reported; before the result was 0 for every sample, without a report.
    /// </summary>
    [TestPdfsFact]
    public void NltTable_DmaxEqualToDmin_FirstValueAndReported()
    {
        var image = Reported(Part2(Rgb.Value, 0x8200, null, T801Lut(65535, 0x80000000, 0x80000000, 8, 200, 100, 50)));
        Assert.All(image.Samples, v => Assert.Equal(200, v));
        Assert.True(image.Truncated);
    }

    /// <summary>
    /// T.801 K.2.2: at most 2^b_y table values, b_y the depth of the reconstructed component (8 here). 256 values are not reported; 257 are
    /// computed as given (the same identity, within one level) and reported.
    /// </summary>
    [TestPdfsFact]
    public void NltTable_MoreValuesThanTheInputHas_Reported()
    {
        var plain = Plain(Rgb.Value);
        var fits = Reported(Part2(Rgb.Value, 0x8200, null, T801Lut(65535, 0, 0xFFFFFFFF, 8, [.. Enumerable.Range(0, 256).Select(i => (ulong)i)])));
        Assert.Equal(plain, fits.Samples);
        Assert.False(fits.Truncated);
        var more = Reported(Part2(Rgb.Value, 0x8200, null, T801Lut(65535, 0, 0xFFFFFFFF, 8, [.. Enumerable.Range(0, 257).Select(i => (ulong)Math.Round(i * 255 / 256.0))])));
        Assert.All(plain.Zip(more.Samples), p => Assert.InRange(p.Second - p.First, -1, 1));
        Assert.True(more.Truncated);
    }

    /// <summary>
    /// T.801 J.2.3.3: no output index may stand twice in the collections of one MCC. The later collection gives that component, as before
    /// (component 2 with offset 2000 in place of component 0), and the image is reported; the same MCC without the second collection is not.
    /// </summary>
    [TestPdfsFact]
    public void Mcc_OutputNamedTwice_LaterOneAndReported()
    {
        var raw = Part2Segments.Raw();
        const int l = Part2Segments.Level;
        byte[] File(params byte[][] collections) => Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Offsets(1, l, l, l),
            Part2Segments.Offsets(2, 2000), Part2Segments.Mcc(0, collections), Part2Segments.Mco(0));
        var first = Part2Segments.Collection([0, 1, 2], [0, 1, 2], 0, 1);
        var once = JpxDecoder.DecodePlanes(File(first), long.MaxValue);
        Assert.False(once.Truncated);
        var twice = File(first, Part2Segments.Collection([2], [0], 0, 2));
        Part2Segments.Equal([Part2Segments.Of(raw[2], 2000), Part2Segments.Of(raw[1], l), Part2Segments.Of(raw[2], l)], Part2Segments.Planes(twice));
        Assert.True(JpxDecoder.DecodePlanes(twice, long.MaxValue).Truncated);
    }

    /// <summary>
    /// T.801 Tables A.36 and A.37: Nmcc and Mmcc count 1 to 16 384 components. A collection without inputs (it fills its outputs from its offsets),
    /// one without outputs, and one of 16 385 inputs (component 0, then component 0 again; its matrix one row that takes the first, so output 0
    /// is component 0, and a second collection gives outputs 1 and 2) are read as given, as before, and reported — the work stays below the
    /// limit of the image, so only the count makes the report.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Mcc_CountOutsideItsRange_ReadAndReported(int kind)
    {
        var raw = Part2Segments.Raw();
        const int l = Part2Segments.Level;
        var identity = Part2Segments.Collection([0, 1, 2], [0, 1, 2], 0, 1);
        byte[] file;
        if (kind == 2)
        {
            const int n = 16385;
            var row = Enumerable.Range(0, n).SelectMany(col => U16(col == 0 ? 1 : 0)).ToArray();   // 16-bit integers (element type 0)
            file = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Marker(0xFF74, U16(0), U16(0x0101), U16(0), row),
                Part2Segments.Offsets(1, l), Part2Segments.Offsets(2, l, l),
                Part2Segments.Mcc(0, Part2Segments.Collection([.. Enumerable.Repeat(0, n)], [0], 1, 1), Part2Segments.Collection([1, 2], [1, 2], 0, 2)),
                Part2Segments.Mco(0));
        }
        else
        {
            var odd = kind == 0 ? Part2Segments.Collection([], [3], 0, 2) : Part2Segments.Collection([0], [], 0, 0);
            file = Part2Segments.File(Part2Segments.Cbd(0x8003, 0x0B), Part2Segments.Offsets(1, l, l, l), Part2Segments.Offsets(2, 5),
                Part2Segments.Mcc(0, identity, odd), Part2Segments.Mco(0));
        }
        Part2Segments.Equal([.. raw.Select(c => Part2Segments.Of(c, l))], Part2Segments.Planes(file));
        Assert.True(JpxDecoder.DecodePlanes(file, long.MaxValue).Truncated);
    }

    /// <summary>
    /// T.801 K.1.2: with a component transformation the codestream has a CBD. Without one the decoder takes the components of SIZ, as before,
    /// and reports the image; a CBD with a reserved depth (39 bits, Table A.31) is passed over and reported the same way.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComponentTransformation_WithoutCbd_SizComponentsAndReported(bool reservedCbd)
    {
        byte[][] rest = [Part2Segments.Offsets(1, 128, 128, 128), Part2Segments.Mcc(0, Part2Segments.Collection([0, 1, 2], [0, 1, 2], 0, 1)),
            Part2Segments.Mco(0)];
        var with = Reported(Part2Segments.File([Part2Segments.Cbd(0x8003, 0x07), .. rest]));
        Assert.False(with.Truncated);
        byte[][] cbd = reservedCbd ? [Part2Segments.Cbd(0x8003, 0x26)] : [];
        var without = Reported(Part2Segments.File([.. cbd, .. rest]));
        Assert.Equal(with.Samples, without.Samples);
        Assert.True(without.Truncated);
    }
}
