using System.Buffers.Binary;
using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Reserved and invalid values in the headers of a codestream (ITU-T T.800, Annex A): the decoder shows the image as well as it can and
/// reports it as damaged (<see cref="JpxImage.Truncated"/>; decided on 23.11.2026, docs/FEHLERGEDAECHTNIS.md, No. 1048). Each case is set
/// against the same codestream with a defined value in its place, which must give the same image and no report.
/// </summary>
public sealed partial class JpxMarkerTests
{
    private static JpxImage Reported(byte[] cs) => JpxDecoder.Decode(cs, long.MaxValue);

    /// <summary>The position of the first marker segment <paramref name="marker"/> of the main header.</summary>
    private static int MainSegment(byte[] cs, int marker)
    {
        var p = 2;
        while (BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p)) != marker) p += 2 + BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(p + 2));
        return p;
    }

    /// <summary>The codestream with <paramref name="bytes"/> at the start of the first tile-part header, Psot grown by their length.</summary>
    private static byte[] InFirstTilePart(byte[] cs, byte[] bytes)
    {
        var sot = FirstSot(cs);
        var copy = Insert(cs, sot + 12, bytes);
        var psot = BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(sot + 6));
        if (psot != 0) BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(sot + 6), psot + (uint)bytes.Length);
        return copy;
    }

    /// <summary>The codestream with byte <paramref name="offset"/> of the main header segment <paramref name="marker"/> (counted from the marker) set.</summary>
    private static byte[] WithByte(byte[] cs, int marker, int offset, byte value)
    {
        var copy = (byte[])cs.Clone();
        copy[MainSegment(copy, marker) + offset] = value;
        return copy;
    }

    /// <summary>Both decode to the same samples; the first is reported damaged, the second not.</summary>
    private static void SameImageReported(byte[] reserved, byte[] defined)
    {
        var expected = Reported(defined);
        Assert.False(expected.Truncated);
        var image = Reported(reserved);
        Assert.Equal(expected.Samples, image.Samples);
        Assert.True(image.Truncated);
    }

    /// <summary>
    /// T.800 A.1: a marker is 0xFF followed by 0x01 to 0xFE. A code that is none — 0x1234, 0xFF00, 0xFFFF — in the main header or in a tile-part
    /// header is read past as a segment over its length, as before, and the image is reported damaged (before without a word); a code of the
    /// unassigned range with 0xFF in front (0xFF6A) is an unknown segment and no damage.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(0x1234, false, true)]
    [InlineData(0xFF00, false, true)]
    [InlineData(0xFFFF, false, true)]
    [InlineData(0x1234, true, true)]
    [InlineData(0xFFFF, true, true)]
    [InlineData(0xFF6A, false, false)]
    [InlineData(0xFF6A, true, false)]
    public void NoMarker_ReadPastAndReported(int code, bool inTilePart, bool damaged)
    {
        var cs = Rgb.Value;
        var segment = Segment(code, 0, 0);
        var file = inTilePart ? InFirstTilePart(cs, segment) : Insert(cs, AfterSiz(cs), segment);
        var image = Reported(file);
        Assert.Equal(Plain(cs), image.Samples);
        Assert.Equal(damaged, image.Truncated);
    }

    /// <summary>
    /// T.800 Table A.10: values outside the table — 0x0008, 0x0A00, 0x1000, 0x3000, a broadcast profile with main level 12 (Table A.49 ends at
    /// 11), an IMF profile with sublevel 10 (Table A.53 ends at 9) — decode as a codestream of Part 1 and are reported; the values of the
    /// table are not.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(0x0008, true)]
    [InlineData(0x0A00, true)]
    [InlineData(0x1000, true)]
    [InlineData(0x3000, true)]
    [InlineData(0x010C, true)]
    [InlineData(0x04A1, true)]
    [InlineData(0x0000, false)]
    [InlineData(0x0007, false)]
    [InlineData(0x010B, false)]
    [InlineData(0x0307, false)]
    [InlineData(0x0499, false)]
    [InlineData(0x0FFF, false)]
    public void Rsiz_ReservedValues_Reported(int rsiz, bool reserved)
    {
        var image = Reported(WithRsiz(Rgb.Value, rsiz));
        Assert.Equal(Plain(Rgb.Value), image.Samples);
        Assert.Equal(reserved, image.Truncated);
    }

    /// <summary>
    /// T.800 Table A.11: a depth of 39 or 128 bits (Ssiz 0x26, 0x7F) is reserved. The decoder reads it as 38 bits throughout; with the 9-7
    /// filter the step size follows the depth (E-3), so the image is the one the depth given would give, scaled to its range — the same as
    /// with 8 bits: coefficient q with step 2 at 8 bits is reconstructed as (q + ½) · 2 = 2q + 1 plus the level 128 (G.1.2), so 50 gives 229 and
    /// −50 gives 27. Before the step took 39 bits and the range 38: the samples ran over to 255 and 0, without a report.
    /// </summary>
    [Theory]
    [InlineData(0x26, 50, 229)]
    [InlineData(0x26, -50, 27)]
    [InlineData(0x7F, 50, 229)]
    [InlineData(0x7F, -50, 27)]
    public void ComponentDepth_Reserved_ReadAs38BitsAndReported(int ssiz, int q, int expected)
    {
        byte[] Coded(int depthByte)
        {
            var cs = JpxTests.AnnexG.Codestream(new JpxTests.AnnexG.Image
            {
                Reversible = false, Guard = 2, Exponent = 7, Value = (_, _) => new JpxTests.AnnexG.Coded(q, JpxTests.AnnexG.BitLength(Math.Abs(q)) - 1),
            });
            cs[42] = (byte)depthByte;   // SOC, SIZ marker, Lsiz, Rsiz, eight sizes and offsets, Csiz: Ssiz of component 0
            return cs;
        }
        var eight = Reported(Coded(0x07));
        Assert.Equal([(byte)expected], eight.Samples);
        Assert.False(eight.Truncated);
        var image = Reported(Coded(ssiz));
        Assert.Equal([(byte)expected], image.Samples);
        Assert.True(image.Truncated);
        var reserved = Coded(ssiz);
        Assert.Equal(38, JpxCodestream.Parse(reserved, 0, reserved.Length).Components[0].Precision);
    }

    /// <summary>
    /// T.800 A.6.1 to A.6.5 (Equations A-2, A-3, A-5) and A.6.3: segments too short to hold their fields, or naming a component the codestream
    /// does not have, or an RGN with a style other than 0 (Table A.25) are passed over, as before — in the main header and in the tile-part
    /// header — and the image is reported damaged (before without a word): COD with Lcod 11, COC with Lcoc 8, QCC with Lqcc 3, RGN with Lrgn 4,
    /// COC and QCC for component 5 of three, RGN with Srgn 1.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(0xFF52, new byte[] { 0, 0, 0, 1, 1, 5, 4, 4, 2 }, false)]
    [InlineData(0xFF52, new byte[] { 0, 0, 0, 1, 1, 5, 4, 4, 2 }, true)]
    [InlineData(0xFF53, new byte[] { 0, 0, 5, 4, 4, 2 }, false)]
    [InlineData(0xFF53, new byte[] { 0, 0, 5, 4, 4, 2 }, true)]
    [InlineData(0xFF5D, new byte[] { 0 }, false)]
    [InlineData(0xFF5D, new byte[] { 0 }, true)]
    [InlineData(0xFF5E, new byte[] { 0, 0 }, false)]
    [InlineData(0xFF53, new byte[] { 5, 0, 5, 4, 4, 2, 1 }, false)]
    [InlineData(0xFF5D, new byte[] { 5, 0x40, 0x48 }, true)]
    [InlineData(0xFF5E, new byte[] { 0, 1, 3 }, false)]
    public void Segment_TooShortOrOutOfRange_PassedOverAndReported(int marker, byte[] body, bool inTilePart)
    {
        var cs = Rgb.Value;
        var segment = Segment(marker, body);
        var file = inTilePart ? InFirstTilePart(cs, segment) : Insert(cs, AfterSiz(cs), segment);
        var image = Reported(file);
        Assert.Equal(Plain(cs), image.Samples);
        Assert.True(image.Truncated);
    }

    /// <summary>
    /// T.800 Table A.14: the number of layers is 1 to 65 535; 0 is read as 1, as before, and reported. The codestream has one layer, so the image
    /// is the same.
    /// </summary>
    [TestPdfsFact]
    public void Layers_Zero_ReadAsOneAndReported()
    {
        var cs = Rgb.Value;
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(MainSegment(cs, 0xFF52) + 6)));
        var zero = (byte[])cs.Clone();
        zero[MainSegment(cs, 0xFF52) + 7] = 0;
        SameImageReported(zero, cs);
    }

    /// <summary>
    /// T.800 Table A.16: progression orders 5 to 255 are reserved; COD and POC read them like CPRL (4), as before, and the image is reported —
    /// the reference readers show no image at all (5: no image or a white area). Four tiles of one sample each, one packet per tile, so every
    /// order reads the same packets.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(255)]
    public void ProgressionOrder_Reserved_ReadAsCprlAndReported(int order)
    {
        var cs = JpxTests.AnnexG.Lossless8(2, 2, [10, 20, 30, 40]);
        Assert.Equal([10, 20, 30, 40], Plain(cs));
        SameImageReported(WithByte(cs, 0xFF52, 5, (byte)order), WithByte(cs, 0xFF52, 5, 4));
        byte[] Poc(int o) => Segment(0xFF5F, 0, 0, 0, 1, 33, 255, (byte)o);
        SameImageReported(Insert(cs, AfterSiz(cs), Poc(order)), Insert(cs, AfterSiz(cs), Poc(4)));
    }

    /// <summary>
    /// T.800 Table A.17: the component transformation field is 0 or 1 in a codestream of Part 1; 2 to 255 are read like 1, as before, and
    /// reported. Table A.20: the wavelet is 0 or 1; 2 to 255 are read like 0 (9-7), as before, and reported. Table A.28: quantization styles 3
    /// to 31 are read like 2, as before, and reported.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(8, 2, 1)]
    [InlineData(8, 255, 1)]
    [InlineData(13, 2, 0)]
    [InlineData(13, 255, 0)]
    public void CodValues_Reserved_ReadAsBeforeAndReported(int offset, int reserved, int defined)
    {
        var cs = Rgb.Value;
        SameImageReported(WithByte(cs, 0xFF52, offset, (byte)reserved), WithByte(cs, 0xFF52, offset, (byte)defined));
    }

    /// <inheritdoc cref="CodValues_Reserved_ReadAsBeforeAndReported"/>
    [Theory]
    [InlineData(3)]
    [InlineData(31)]
    public void QuantizationStyle_Reserved_ReadAs2AndReported(int style)
    {
        // the 9-7 filter with scalar expounded quantization (style 2): coefficient 50 with step 2 at 8 bits gives 2 · 50 + 1 + 128
        var cs = JpxTests.AnnexG.Codestream(new JpxTests.AnnexG.Image { Reversible = false, Exponent = 7, Value = (_, _) => new JpxTests.AnnexG.Coded(50, 5) });
        Assert.Equal(2, cs[MainSegment(cs, 0xFF5C) + 4] & 0x1F);
        Assert.Equal([229], Plain(cs));
        SameImageReported(WithByte(cs, 0xFF5C, 4, (byte)(cs[MainSegment(cs, 0xFF5C) + 4] & 0xE0 | style)), cs);
    }

    /// <summary>
    /// T.800 Table A.32: with 16-bit component indices (Csiz 257 and more) CEpoc runs from CSpoc + 1 to 16 384; 0 lies outside. It is read as
    /// all components — what 0 means with 8-bit indices — and reported; before the entry had no packets and every component stayed at its DC
    /// level. 257 components of one sample each, all decoded to their values.
    /// </summary>
    [Fact]
    public void Poc_CeZeroWithWideIndices_AllComponentsAndReported()
    {
        const int n = 257;
        var samples = Enumerable.Range(0, n).Select(c => new[] { (c * 7 + 3) % 256 }).ToArray();
        var plain = JpxTests.AnnexG.Lossless(1, 1, [.. Enumerable.Repeat((8, false), n)], samples);
        byte[] Entry(int ce) => [0, 0, 0, 0, 1, 33, (byte)(ce >> 8), (byte)ce, 0];   // RSpoc, CSpoc (16 bits), LYEpoc, REpoc, CEpoc (16 bits), Ppoc
        var at = AfterSiz(plain);
        var full = JpxDecoder.DecodePlanes(Insert(plain, at, Segment(0xFF5F, Entry(n))), long.MaxValue);
        var zero = JpxDecoder.DecodePlanes(Insert(plain, at, Segment(0xFF5F, Entry(0))), long.MaxValue);
        Assert.Equal(samples.Select(s => s[0]), full.Planes.Select(p => (int)p[0]));
        Assert.False(full.Truncated);
        Assert.Equal(samples.Select(s => s[0]), zero.Planes.Select(p => (int)p[0]));
        Assert.True(zero.Truncated);
    }
}
