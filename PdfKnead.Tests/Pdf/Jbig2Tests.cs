using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Filters.Jbig2;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// JBIG2 with hand-built segments. The full breadth (arithmetic coding, refinement,
/// halftone, …) is checked by the public test files in TestPDFs (not in Git).
/// </summary>
public sealed class Jbig2Tests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    /// <summary>Builds a segment with header (ITU-T T.88, 7.2): number, type, referred-to segments (1 byte each), page 1, length.</summary>
    private static byte[] Segment(int number, int type, byte[] data, params byte[] referred)
    {
        var header = new List<byte> { 0, 0, 0, (byte)number, (byte)type, (byte)(referred.Length << 5) };
        header.AddRange(referred);
        header.Add(1);
        header.AddRange([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
        return [.. header, .. data];
    }

    private static byte[] PageInfo(int width, int height) =>
        Segment(0, 48, [0, 0, 0, (byte)width, 0, 0, 0, (byte)height, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

    private static byte[] RegionInfo(int width, int height) =>
        [0, 0, 0, (byte)width, 0, 0, 0, (byte)height, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public void ArithmeticDecoder_MatchesTheTestSequenceOfTheStandard()
    {
        // ITU-T T.88, Annex H.2: 256 bits, all coded in the same context
        var encoded = Hex("84C73BFC E1A14304 02200000 410DBB86 F4317FFF 88FF3747 1ADB6ADF FFAC");
        var expected = Hex("00020051 000000C0 0352872A AAAAAAAA 82C02000 FCD79EF6 BF7FED90 4F46A3BF");
        var decoder = new MqDecoder(encoded, 0, encoded.Length);
        var cx = new byte[1];
        var decoded = new byte[expected.Length];
        for (var i = 0; i < decoded.Length * 8; i++)
            decoded[i >> 3] |= (byte)(decoder.Decode(cx, 0) << (7 - (i & 7)));
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void GenericRegionWithMmr_IsPlacedOnThePage()
    {
        // Group 4, 8 pixels wide: twice "2 white, 3 black, 3 white" (see CcittTests)
        var region = Segment(1, 38, [.. RegionInfo(8, 2), 0x01, 0x2F, 0x78]);
        var truncated = false;
        var image = Jbig2Decoder.Decode([.. PageInfo(8, 2), .. region], null, 8, 2, ref truncated);
        Assert.Equal([0b1100_0111, 0b1100_0111], image);   // PDF: 0 = black
        Assert.False(truncated);
    }

    [Fact]
    public void GenericRegionWithUnknownLength_EndsAtTheMarker()
    {
        // Length 0xFFFFFFFF: the end is 00 00 + row count (ITU-T T.88, 7.2.7)
        byte[] data = [.. RegionInfo(8, 2), 0x01, 0x2F, 0x78, 0, 0, 0, 0, 0, 2];
        byte[] header = [0, 0, 0, 1, 38, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF];
        var truncated = false;
        var image = Jbig2Decoder.Decode([.. PageInfo(8, 3), .. header, .. data], null, 8, 3, ref truncated);
        Assert.Equal([0b1100_0111, 0b1100_0111, 0xFF], image);
        Assert.False(truncated);
    }

    [Fact]
    public void SymbolDictionaryAndTextRegion_WithHuffmanCodes()
    {
        // Dictionary: one symbol 3 × 2 as an uncompressed collective bitmap. Huffman bits:
        // height +2 (B.4 "10"), width +3 (B.2 "1110 000"), OOB (B.2 "111111"), BMSIZE 0 (B.1 "0 0000")
        // → B8 7E 00; bitmap E0 A0; export: 0 not exported, 1 exported (B.1) → 00 40
        byte[] dictionary = [0x00, 0x01, 0, 0, 0, 1, 0, 0, 0, 1, 0xB8, 0x7E, 0x00, 0xE0, 0xA0, 0x00, 0x40];

        // Text region 8 × 4, Huffman, reference corner top left, two instances. Symbol ID table:
        // run-length code 1 has length 1, all others 0 → "0" = symbol 0 gets length 1 (18 bytes).
        // Then: DT 1, DT 1 (B.11 "0", "0"), FS 0 (B.6 "00 0000000"), ID "0", DS 2 (B.8 "11010"), ID "0",
        // DS OOB (B.8 "01") → 00 0D 10
        byte[] text =
        [
            .. RegionInfo(8, 4), 0x00, 0x11, 0x00, 0x00, 0, 0, 0, 2,
            0x01, .. new byte[17], 0x00, 0x0D, 0x10,
        ];
        var truncated = false;
        var image = Jbig2Decoder.Decode([.. PageInfo(8, 4), .. Segment(1, 0, dictionary), .. Segment(2, 6, text, 1)], null, 8, 4, ref truncated);
        Assert.False(truncated);
        // Symbol 111/101 at x = 0 and x = 4
        Assert.Equal([0b0001_0001, 0b0101_0101, 0xFF, 0xFF], image);
    }

    /// <summary>
    /// ITU-T T.88 Amd. 2: a generic region with the extended template (flag bit 4, twelve AT pixels) is not implemented. Read as the ordinary
    /// template it would take the AT pixels and the coded data from the wrong bytes and show a wrong image without notice; it is refused, the
    /// region stays white and the stream counts as damaged (docs/FEHLERGEDAECHTNIS.md, No. 730).
    /// </summary>
    [Fact]
    public void GenericRegionWithExtendedTemplate_IsRefused()
    {
        sbyte[] at = [-2, 0, 0, -2, -2, -1, -1, -2, 1, -2, 2, -1, -3, 0, -4, 0, 2, -2, 3, -1, -2, -2, -3, -1];
        var coded = Enumerable.Repeat((byte)0x5A, 64).Concat(new byte[] { 0xFF, 0xAC }).ToArray();
        var region = Segment(1, 38, [.. RegionInfo(16, 8), 0x10, .. at.Select(v => (byte)v), .. coded]);
        var truncated = false;
        var image = Jbig2Decoder.Decode([.. PageInfo(16, 8), .. region], null, 16, 8, ref truncated);
        Assert.True(truncated);
        Assert.All(image, b => Assert.Equal(0xFF, b));
    }

    /// <summary>
    /// ITU-T T.88 Amd. 3 and ISO 32000-2, 7.4.7: the colour extension flag (bit 3 of the region segment flags) must be 0 in PDF; a region with it
    /// carries colour data the decoder does not read — refused instead of drawn (docs/FEHLERGEDAECHTNIS.md, No. 730).
    /// </summary>
    [Fact]
    public void RegionWithColourExtension_IsRefused()
    {
        byte[] info = [.. RegionInfo(8, 2)[..16], 0x08];
        var region = Segment(1, 38, [.. info, 0x01, 0x2F, 0x78]);
        var truncated = false;
        var image = Jbig2Decoder.Decode([.. PageInfo(8, 2), .. region], null, 8, 2, ref truncated);
        Assert.True(truncated);
        Assert.Equal([0xFF, 0xFF], image);
    }

    [Fact]
    public void BrokenData_KeepsWhatWasDecoded()
    {
        var region = Segment(1, 38, [.. RegionInfo(8, 2), 0x01, 0x2F, 0x78]);
        byte[] garbage = [0, 0, 0, 2, 0, 0, 1, 0xFF, 0xFF, 0x00, 0x10];   // symbol dictionary with nonsense
        var truncated = false;
        var image = Jbig2Decoder.Decode([.. PageInfo(8, 2), .. region, .. garbage], null, 8, 2, ref truncated);
        Assert.True(truncated);
        Assert.Equal([0b1100_0111, 0b1100_0111], image);
    }

    [Fact]
    public void HugePage_IsRefused()
    {
        // 16 million × 16 million pixels: a limit instead of running out of memory. The limit follows the image size in the PDF (8 × 2)
        // and applies before allocating; the image counts as damaged (white) instead of aborting the whole page
        byte[] page = [0, 0, 0, 0, 48, 0, 1, 0, 0, 0, 19, 0, 0xFF, 0xFF, 0xFF, 0, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var truncated = false;
        var data = Jbig2Decoder.Decode(page, null, 8, 2, ref truncated);
        Assert.True(truncated);
        Assert.Equal(2, data.Length);
    }

    [Theory]
    [InlineData(Jbig2Bitmap.Or)]
    [InlineData(Jbig2Bitmap.And)]
    [InlineData(Jbig2Bitmap.Xor)]
    [InlineData(Jbig2Bitmap.Xnor)]
    [InlineData(Jbig2Bitmap.Replace)]
    public void Compose_MatchesPixelByPixel(int op)
    {
        var random = new Random(op);
        for (var round = 0; round < 40; round++)
        {
            var target = RandomBitmap(random, random.Next(1, 40), random.Next(1, 12));
            var source = RandomBitmap(random, random.Next(1, 40), random.Next(1, 12));
            int x = random.Next(-20, 40), y = random.Next(-8, 12);
            var expected = new int[target.Width, target.Height];
            for (var ty = 0; ty < target.Height; ty++)
            for (var tx = 0; tx < target.Width; tx++)
            {
                int d = target.Get(tx, ty), sx = tx - x, sy = ty - y;
                var inside = sx >= 0 && sx < source.Width && sy >= 0 && sy < source.Height;
                var s = source.Get(sx, sy);
                expected[tx, ty] = !inside ? d : op switch
                {
                    Jbig2Bitmap.Or => d | s,
                    Jbig2Bitmap.And => d & s,
                    Jbig2Bitmap.Xor => d ^ s,
                    Jbig2Bitmap.Xnor => 1 - (d ^ s),
                    _ => s,
                };
            }
            target.Compose(source, x, y, op);
            for (var ty = 0; ty < target.Height; ty++)
            for (var tx = 0; tx < target.Width; tx++)
                Assert.Equal(expected[tx, ty], target.Get(tx, ty));
        }
    }

    [Fact]
    public void BitReader_EndBeyondTheData_IsAJbig2Error()
    {
        // Found by fuzzing: a segment may declare more length than the data has; this must end in an orderly abort, not IndexOutOfRange
        var reader = new Jbig2BitReader([0xFF, 0x00], 0, 10);
        Assert.Equal(0xFF00, reader.ReadBits(16));
        Assert.Throws<Jbig2Exception>(() => reader.ReadBit());
        Assert.Throws<Jbig2Exception>(() => new Jbig2BitReader([1, 2], -3, 2).ReadBit());
    }

    [Theory]
    [InlineData(2147483703L, 112L)]
    [InlineData(5L, 4294967296L)]
    [InlineData(-4294967296L, 0L)]
    public void Compose_PositionBeyondInt_IsOutside(long x, long y)
    {
        // Found by fuzzing: a region at x = 2 147 483 703 must not turn negative when narrowed to int
        var target = new Jbig2Bitmap(399, 400);
        var source = new Jbig2Bitmap(23, 23, 1);
        target.Compose(source, x, y, Jbig2Bitmap.Or);
        Assert.All(target.Data, b => Assert.Equal(0, b));
    }

    private static Jbig2Bitmap RandomBitmap(Random random, int width, int height)
    {
        var bitmap = new Jbig2Bitmap(width, height);
        random.NextBytes(bitmap.Data);
        bitmap.ClearPadding();
        return bitmap;
    }

    [Fact]
    public void StandardHuffmanTables_DecodeRangesAndOob()
    {
        // B.3: "11111110" + 8 bits → −256 + value; lower range line "11111111" + 32 bits → −257 − value; OOB "111110"
        byte[] bits = [0b1111_1110, 0b0000_0101, 0b1111_1111, 0, 0, 0, 3, 0b1111_1000];
        var reader = new Jbig2BitReader(bits, 0, bits.Length);
        var table = Jbig2Huffman.Standard(3);
        Assert.Equal(-251, table.Decode(reader));
        Assert.Equal(-260, table.Decode(reader));
        Assert.Null(table.Decode(reader));
    }

    [Fact]
    public void JbigImage_InAPdf_IsDecodedLikeAnUncompressedImage()
    {
        byte[] data = [.. PageInfo(8, 2), .. Segment(1, 38, [.. RegionInfo(8, 2), 0x01, 0x2F, 0x78])];
        var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(8)), ("Height", PdfInteger.Of(2)),
            ("ImageMask", PdfBoolean.True), ("Filter", TestPdf.N("JBIG2Decode")));
        var (doc, list) = InterpreterTests.Page("q 8 0 0 2 0 0 cm /Im Do Q", pdf =>
            TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(pdf.Add(new PdfStream(dict, data))))))));
        using (doc)
        {
            var image = PdfImageDecoder.Decode(list.Images[0], doc);
            Assert.NotNull(image);
            for (var x = 0; x < 8; x++) Assert.Equal(x is >= 2 and < 5 ? 255 : 0, image.Rgba[x * 4 + 3]);
        }
    }

    /// <summary>
    /// The work budget grows with the encoded data: a dictionary of 262 144 tiny symbols for a 399 × 400 image (348 160 bytes of data) must
    /// decode — sized by the image alone, the budget ran out and the image came back blank (docs/FEHLERGEDAECHTNIS.md, No. 437) —, while
    /// 300 bytes of crafted data still cannot buy that much work.
    /// </summary>
    [Fact]
    public void Budget_GrowsWithTheData()
    {
        var real = new Core.Pdf.Filters.Jbig2.Jbig2Budget(399 * 400, 348_160);
        for (var i = 0; i < 262_144; i++) real.Spend(64);
        var crafted = new Core.Pdf.Filters.Jbig2.Jbig2Budget(399 * 400, 300);
        Assert.Throws<Core.Pdf.Filters.Jbig2.Jbig2Exception>(() =>
        {
            for (var i = 0; i < 262_144; i++) crafted.Spend(64);
        });
    }
}
