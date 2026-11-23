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
public sealed partial class Jbig2Tests
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

    /// <summary>
    /// ITU-T T.88, 7.2.7 and 7.4.6.4 sentence 90: with an unknown length the row count after the end marker may be smaller than the height of
    /// the region — here an MMR region of height 3 with two rows; the region has those two rows. Before an MMR region had to have exactly its
    /// height, the end was not found and the region counted as damaged (docs/FEHLERGEDAECHTNIS.md, No. 977). A row count above the height is
    /// no end.
    /// </summary>
    [Fact]
    public void GenericRegionWithUnknownLength_FewerRowsThanTheHeight()
    {
        byte[] header = [0, 0, 0, 1, 38, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF];
        byte[] data = [.. RegionInfo(8, 3), 0x01, 0x2F, 0x78, 0, 0, 0, 0, 0, 2];
        var truncated = false;
        var image = Jbig2Decoder.Decode([.. PageInfo(8, 3), .. header, .. data], null, 8, 3, ref truncated);
        Assert.Equal([0b1100_0111, 0b1100_0111, 0xFF], image);
        Assert.False(truncated);
        byte[] tooMany = [.. RegionInfo(8, 1), 0x01, 0x2F, 0x78, 0, 0, 0, 0, 0, 2];
        truncated = false;
        Jbig2Decoder.Decode([.. PageInfo(8, 3), .. header, .. tooMany], null, 8, 3, ref truncated);
        Assert.True(truncated);
    }

    /// <summary>
    /// ITU-T T.88, 8.2 step 5 c and 7.4.7.5 sentence 48: an immediate refinement region that refers to no other region replaces its part of the
    /// page, whatever the page's operator — before it was composed with the page operator (OR could not clear a pixel; docs/FEHLERGEDAECHTNIS.md,
    /// No. 976). Other direct regions: their own operator where the page allows overriding (bit 6), else the page's.
    /// </summary>
    [Fact]
    public void RefinementOfThePage_ReplacesItsPart()
    {
        const int or = 0, xor = 2, replace = 4;
        Assert.Equal(replace, Jbig2Decoder.OperatorFor(refinesThePage: true, overrideAllowed: false, regionOperator: or, pageOperator: or));
        Assert.Equal(replace, Jbig2Decoder.OperatorFor(refinesThePage: true, overrideAllowed: true, regionOperator: xor, pageOperator: or));
        Assert.Equal(or, Jbig2Decoder.OperatorFor(refinesThePage: false, overrideAllowed: false, regionOperator: xor, pageOperator: or));
        Assert.Equal(xor, Jbig2Decoder.OperatorFor(refinesThePage: false, overrideAllowed: true, regionOperator: xor, pageOperator: or));
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
    /// ITU-T T.88 Amd. 2 (6.2.5.3 Figure 3(b), 6.2.5.4, 7.4.6.3 Figure 50): a generic region with the extended template — four fixed pixels and
    /// twelve AT pixels from a 24-byte AT field (the text of Amd. 2 says 32 bytes, its Figure 50 and the conformance stream TT7 have 24) —
    /// decodes the image it was encoded from, with the AT pixels at their nominal places (Table 5) and moved. With an unknown length the end
    /// marker is searched behind the 24-byte field: the moved set holds the pair (−1, −84), the bytes
    /// FF AC, followed by a valid row count, where an 8-byte field would end (docs/FEHLERGEDAECHTNIS.md, No. 966; refused before, No. 730).
    /// The encoder here forms the context from the positions of Figure 3(b) on its own; that the decoder reads the norm's data alike is shown by
    /// the conformance stream TT7 of Annex K (Jbig2Conformance).
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void GenericRegionWithExtendedTemplate_DecodesTheImage(bool moved, bool unknownLength)
    {
        const int width = 40, height = 12;
        var black = new bool[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            black[y * width + x] = (x * 7 + y * 3) % 11 < 4 ^ (x > 20 && y > 5);
        sbyte[] at = moved
            ? [-2, 0, 0, -2, -2, -1, -1, -2, -1, -84, 0, -1, -3, 0, -4, 0, 2, -2, 3, -1, -5, -3, 4, -1]
            : [-2, 0, 0, -2, -2, -1, -1, -2, 1, -2, 2, -1, -3, 0, -4, 0, 2, -2, 3, -1, -2, -2, -3, -1];
        int Pixel(int x, int y) => x >= 0 && x < width && y >= 0 && y < height && black[y * width + x] ? 1 : 0;
        (int X, int Y)[] fixedPixels = [(-1, 0), (1, -1), (0, -1), (-1, -1)];
        var encoder = new MqEncoder();
        var cx = new byte[1 << 16];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var context = 0;
            for (var k = 0; k < 4; k++) context |= Pixel(x + fixedPixels[k].X, y + fixedPixels[k].Y) << k;
            for (var k = 0; k < 12; k++) context |= Pixel(x + at[2 * k], y + at[2 * k + 1]) << (k + 4);
            encoder.Encode(cx, context, Pixel(x, y));
        }
        byte[] data = [.. RegionInfo(width, height), 0x10, .. at.Select(v => (byte)v), .. encoder.Finish()];
        byte[] region = unknownLength
            ? [0, 0, 0, 1, 38, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF, .. data, 0, 0, 0, height]
            : Segment(1, 38, data);
        var truncated = false;
        var image = Jbig2Decoder.Decode([.. PageInfo(width, height), .. region], null, width, height, ref truncated);
        Assert.False(truncated);
        var expected = new byte[(width + 7) / 8 * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            if (!black[y * width + x]) expected[y * 5 + (x >> 3)] |= (byte)(0x80 >> (x & 7));   // PDF: 0 = black
        Assert.Equal(expected, image);
    }

    /// <summary>
    /// The extended template together with TPGDON (T.88 Amd. 2; 6.2.5.7): the context of SLTP is Figure 8 placed into Figure 3(b) — 0x5749 in
    /// the bit order of the decoder (decided on 20.11.2026, S-B B50; docs/FEHLERGEDAECHTNIS.md, No. 990: refused before). An image
    /// with rows equal to the one above (typical rows, coded only by SLTP) and others decodes to the image it was encoded from, with the AT
    /// pixels at their nominal places and moved. No conformance data covers this combination: the encoder here forms SLTP from the same reading
    /// of Figure 8, the test shows that the decoder follows it consistently.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericRegionWithExtendedTemplateAndTpgdon_DecodesTheImage(bool moved)
    {
        Assert.Equal(0x5749, Jbig2Regions.ExtendedTpgdonContext);
        const int width = 24, height = 14;
        var black = new bool[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            black[y * width + x] = y is 0 or 1 ? false : y is 6 or 7 or 8 ? (x * 5 + 6) % 7 < 3 : (x * 7 + y * 3) % 11 < 4;
        sbyte[] at = moved
            ? [-2, 0, 0, -2, -2, -1, -1, -2, -1, -3, 0, -1, -3, 0, -4, 0, 2, -2, 3, -1, -5, -3, 4, -1]
            : [-2, 0, 0, -2, -2, -1, -1, -2, 1, -2, 2, -1, -3, 0, -4, 0, 2, -2, 3, -1, -2, -2, -3, -1];
        int Pixel(int x, int y) => x >= 0 && x < width && y >= 0 && y < height && black[y * width + x] ? 1 : 0;
        bool Typical(int y) => Enumerable.Range(0, width).All(x => Pixel(x, y) == Pixel(x, y - 1));
        (int X, int Y)[] fixedPixels = [(-1, 0), (1, -1), (0, -1), (-1, -1)];
        var encoder = new MqEncoder();
        var cx = new byte[1 << 16];
        var ltp = false;
        var typicalRows = 0;
        for (var y = 0; y < height; y++)
        {
            var typical = Typical(y);
            encoder.Encode(cx, 0x5749, typical != ltp ? 1 : 0);   // SLTP = LTP of this row XOR that of the row before
            ltp = typical;
            if (typical)
            {
                typicalRows++;
                continue;
            }
            for (var x = 0; x < width; x++)
            {
                var context = 0;
                for (var k = 0; k < 4; k++) context |= Pixel(x + fixedPixels[k].X, y + fixedPixels[k].Y) << k;
                for (var k = 0; k < 12; k++) context |= Pixel(x + at[2 * k], y + at[2 * k + 1]) << (k + 4);
                encoder.Encode(cx, context, Pixel(x, y));
            }
        }
        Assert.True(typicalRows >= 4, $"{typicalRows} typische Zeilen");
        var region = Segment(1, 38, [.. RegionInfo(width, height), 0x18, .. at.Select(v => (byte)v), .. encoder.Finish()]);
        var truncated = false;
        var image = Jbig2Decoder.Decode([.. PageInfo(width, height), .. region], null, width, height, ref truncated);
        Assert.False(truncated);
        var expected = new byte[(width + 7) / 8 * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            if (!black[y * width + x]) expected[y * 3 + (x >> 3)] |= (byte)(0x80 >> (x & 7));   // PDF: 0 = black
        Assert.Equal(expected, image);
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

    /// <summary>
    /// T.88, 7.2.8 (the pattern of its two examples, with own values): a short form — segment 65, type 7 with deferred non-retain, three
    /// referred-to segments of one byte each (7, 40, 3), page 2 in one byte, 16 data bytes; and a long form — segment 769, type 0, ten referred-to
    /// segments (count 0xE0 00 00 0A, then ⌈11 / 8⌉ = 2 bytes of retention bits) of two bytes each, page 1026 in four bytes, 256 data bytes.
    /// </summary>
    [Fact]
    public void SegmentHeader_ShortAndLongForm_AsTheExamplesOf728()
    {
        var shortForm = Hex("00000041 87 63 07 28 03 02 00000010");
        var p = 0;
        var (number, type, referred, length) = Jbig2Decoder.SegmentHeader(shortForm, ref p);
        Assert.Equal((65u, 7, 16L, 14), (number, type, length, p));
        Assert.Equal([7u, 40u, 3u], referred);

        var longForm = Hex("00000301 40 E000000A 0500 012C 0002 001F 0006 0208 0209 020A 020B 020C 020D 00000402 00000100");
        p = 0;
        (number, type, referred, length) = Jbig2Decoder.SegmentHeader(longForm, ref p);
        Assert.Equal((769u, 0, 256L, longForm.Length), (number, type, length, p));
        Assert.Equal([300u, 2u, 31u, 6u, 520u, 521u, 522u, 523u, 524u, 525u], referred);
    }

    /// <summary>T.88, 7.2.5: a referred-to segment number has one byte up to segment number 256, two up to 65536, else four.</summary>
    [Theory]
    [InlineData(256u, 1)]
    [InlineData(257u, 2)]
    [InlineData(65536u, 2)]
    [InlineData(65537u, 4)]
    public void SegmentHeader_ReferredNumberWidth_ByTheSegmentNumber(uint segment, int width)
    {
        byte[] header = [(byte)(segment >> 24), (byte)(segment >> 16), (byte)(segment >> 8), (byte)segment, 0x00, 0x20, .. new byte[width - 1], 1, 0x01, 0, 0, 0, 0];
        var p = 0;
        var (number, _, referred, _) = Jbig2Decoder.SegmentHeader(header, ref p);
        Assert.Equal(segment, number);
        Assert.Equal([1u], referred);
        Assert.Equal(header.Length, p);
    }

    /// <summary>
    /// T.88, 7.4.3.1.7 with Table 29 and B.3 (the pattern of its Example 1, with own values): 35 run code lengths of four bits (RUNCODE4 two,
    /// RUNCODE2, 3, 32, 33, 34 three bits — by B.3 00, 010, 011, 100, 101, 110), then RUNCODE34 with 7 bits 1 (twelve lengths 0), RUNCODE2,
    /// RUNCODE3, RUNCODE32 with 2 bits 1 (the 3 four times more), RUNCODE33 with 3 bits 1 (four lengths 0), RUNCODE4 twice: 24 symbols with
    /// lengths 2 (symbol 12), 3 (13–17) and 4 (22, 23) — by B.3 the codes 00, 010 … 110, 1110, 1111; the reader stands on the next byte.
    /// </summary>
    [Fact]
    public void SymbolIdTable_RunCodesAsTable29_CodesAsB3()
    {
        var runLengths = new int[35];
        (runLengths[2], runLengths[3], runLengths[4], runLengths[32], runLengths[33], runLengths[34]) = (3, 3, 2, 3, 3, 3);
        var bits = string.Concat(runLengths.Select(l => Convert.ToString(l, 2).PadLeft(4, '0')))
            + "110" + "0000001" + "010" + "011" + "100" + "01" + "101" + "001" + "00" + "00";
        var data = BitString(bits);
        var reader = new Jbig2BitReader(data, 0, data.Length);
        var table = Jbig2Regions.SymbolIdTable(reader, 24);
        Assert.Equal(data.Length, reader.BytePosition);
        foreach (var (code, id) in new[] { ("00", 12), ("010", 13), ("011", 14), ("100", 15), ("101", 16), ("110", 17), ("1110", 22), ("1111", 23) })
        {
            var one = BitString(code);
            Assert.Equal(id, table.Value(new Jbig2BitReader(one, 0, one.Length)));
        }
    }

    /// <summary>
    /// T.88, B.5 (checked line by line on 23.11.2026): every line of the 15 standard Huffman tables — HTOOB, PREFLEN and RANGELEN in the order of
    /// the standard and HTLOW as data; the codes by B.3 (computed here); for every normal line its lowest and highest value, for the lower and
    /// upper range lines the offsets 0 and 1 (32 bits), and OOB decode as B.4 says. Before, only some lines of Tables B.1–B.3 had a test of
    /// their own (the test files show the others only through the image).
    /// </summary>
    [Theory]
    [InlineData(1, 0, "1 2 3 3", "4 8 16 32", 0, false, true)]
    [InlineData(2, 1, "1 2 3 4 5 6 6", "0 0 0 3 6 32", 0, false, true)]
    [InlineData(3, 1, "8 1 2 3 4 5 8 7 6", "8 0 0 0 3 6 32 32", -256, true, true)]
    [InlineData(4, 0, "1 2 3 4 5 5", "0 0 0 3 6 32", 1, false, true)]
    [InlineData(5, 0, "7 1 2 3 4 5 7 6", "8 0 0 0 3 6 32 32", -255, true, true)]
    [InlineData(6, 0, "5 4 4 4 5 5 4 2 3 3 4 4 6 6", "10 9 8 7 6 5 5 7 7 8 9 10 32 32", -2048, true, true)]
    [InlineData(7, 0, "4 3 4 5 5 4 4 5 5 4 3 3 3 5 5", "9 8 7 6 5 5 5 5 6 7 8 9 10 32 32", -1024, true, true)]
    [InlineData(8, 1, "8 9 8 9 7 4 2 5 6 3 6 4 4 5 5 6 7 6 9 9 2", "3 1 1 0 0 0 1 0 0 4 1 4 5 6 7 7 8 10 32 32", -15, true, true)]
    [InlineData(9, 1, "8 9 8 9 7 4 3 3 5 6 3 6 4 4 5 5 6 7 6 9 9 2", "4 2 2 1 1 1 1 1 1 1 5 2 5 6 7 8 8 9 11 32 32", -31, true, true)]
    [InlineData(10, 1, "7 8 7 5 2 5 6 7 8 2 5 6 6 6 6 6 6 7 8 8 2", "4 0 0 0 2 0 0 0 0 6 5 5 6 7 8 9 10 11 32 32", -21, true, true)]
    [InlineData(11, 0, "1 2 4 4 5 5 6 7 7 7 7 7 7", "0 1 0 1 1 2 2 2 3 4 5 6 32", 1, false, true)]
    [InlineData(12, 0, "1 2 3 5 5 6 7 7 7 7 7 8 8", "0 0 1 0 1 1 0 1 2 3 4 5 32", 1, false, true)]
    [InlineData(13, 0, "1 3 4 5 4 3 6 6 6 6 6 7 7", "0 0 0 0 1 3 1 2 3 4 5 6 32", 1, false, true)]
    [InlineData(14, 0, "3 3 1 3 3", "0 0 0 0 0", -2, false, false)]
    [InlineData(15, 0, "7 6 5 4 3 1 3 4 5 6 7 7 7", "4 2 1 0 0 0 0 0 1 2 4 32 32", -24, true, true)]
    public void StandardTables_EveryLineAsB5(int number, int htoob, string preflen, string rangelen, int htlow, bool lower, bool upper)
    {
        var pre = preflen.Split(' ').Select(int.Parse).ToArray();
        var rng = rangelen.Split(' ').Select(int.Parse).ToArray();
        Assert.Equal(pre.Length, rng.Length + htoob);
        var codes = new string[pre.Length];
        var lenCount = new int[33];
        foreach (var l in pre) lenCount[l]++;
        lenCount[0] = 0;
        var first = 0;
        for (var len = 1; len <= 32; len++)
        {
            first = (first + lenCount[len - 1]) << 1;
            var code = first;
            for (var i = 0; i < pre.Length; i++)
                if (pre[i] == len) codes[i] = Convert.ToString(code++, 2).PadLeft(len, '0');
        }
        var table = Jbig2Huffman.Standard(number);
        long? Decoded(string code, int bits, long offset)
        {
            var data = BitString(code + (bits == 0 ? "" : Convert.ToString(offset, 2).PadLeft(bits, '0')));
            return table.Decode(new Jbig2BitReader(data, 0, data.Length));
        }
        var normal = rng.Length - (lower ? 1 : 0) - (upper ? 1 : 0);
        long low = htlow;
        for (var i = 0; i < normal; i++)
        {
            Assert.Equal(low, Decoded(codes[i], rng[i], 0));
            Assert.Equal(low + (1L << rng[i]) - 1, Decoded(codes[i], rng[i], (1L << rng[i]) - 1));
            low += 1L << rng[i];
        }
        var next = normal;
        if (lower)
        {
            Assert.Equal(htlow - 1L, Decoded(codes[next], 32, 0));
            Assert.Equal(htlow - 2L, Decoded(codes[next++], 32, 1));
        }
        if (upper)
        {
            Assert.Equal(low, Decoded(codes[next], 32, 0));
            Assert.Equal(low + 1, Decoded(codes[next++], 32, 1));
        }
        if (htoob == 1) Assert.Null(Decoded(codes[next], 0, 0));
    }

    /// <summary>
    /// T.88, Table E.1 (T.800, Table C.2 alike): every one of the 47 states of the MQ coder — Qe, NMPS, NLPS and SWITCH as data from the table,
    /// compared with the tables of the decoder and the encoder (checked line by line on 23.11.2026; before, only the test sequence of H.2
    /// went through some of the states).
    /// </summary>
    [Fact]
    public void MqStates_AsTableE1()
    {
        ushort[] qe =
        [
            0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401,
            0x1C01, 0x1601, 0x5601, 0x5401, 0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201,
            0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101, 0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1,
            0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
        ];
        byte[] nmps =
        [
            1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24,
            25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46,
        ];
        byte[] nlps =
        [
            1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21,
            22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46,
        ];
        bool[] sw = [true, false, false, false, false, false, true, false, false, false, false, false, false, false, true, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false];
        Assert.Equal(qe, MqDecoder.Qe);
        Assert.Equal(nmps, MqDecoder.Nmps);
        Assert.Equal(nlps, MqDecoder.Nlps);
        Assert.Equal(sw, MqDecoder.Switch);
    }

    /// <summary>A bit string as bytes, filled with zeros to a whole byte.</summary>
    private static byte[] BitString(string bits)
    {
        bits = bits.PadRight((bits.Length + 7) / 8 * 8, '0');
        return [.. Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2))];
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

    // ---- rows of T.88 split on 20.11.2026 (the check for collective rows): each rule its own test

    /// <summary>An MMR region 8 × 2 („2 white, 3 black, 3 white“ twice) at row <paramref name="y"/>; in PDF bits each row is 1100 0111.</summary>
    private static byte[] MmrRegion(int number, int y = 0, int flags = 0)
    {
        var info = RegionInfo(8, 2);
        info[15] = (byte)y;
        info[16] = (byte)flags;
        return Segment(number, 38, [.. info, 0x01, 0x2F, 0x78]);
    }

    /// <summary>The page information with its flags (7.4.8.5) and striping field (7.4.8.6); height 0xFFFFFFFF for unknown.</summary>
    private static byte[] PageInfoWith(int width, uint height, byte flags, ushort striping = 0) =>
        Segment(0, 48, [0, 0, 0, (byte)width, (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, 0, 0, 0, 0, 0, 0, 0, 0,
            flags, (byte)(striping >> 8), (byte)striping]);

    private static byte[] Decoded(byte[] data, int height, out bool truncated, byte[]? globals = null)
    {
        truncated = false;
        return Jbig2Decoder.Decode(data, globals, 8, height, ref truncated);
    }

    /// <summary>
    /// T.88, 7.4.12 and 7.4.15: a profiles segment (type 52) and an extension segment (type 62, here an unknown, not necessary extension) give
    /// the decoder nothing it needs; they are passed over and the page is the same (Bericht M2, Zeilen 241, 444).
    /// </summary>
    [Fact]
    public void ProfilesAndExtensionSegments_PassedOver()
    {
        var plain = Decoded([.. PageInfo(8, 2), .. MmrRegion(1)], 2, out _);
        var with = Decoded([.. PageInfo(8, 2), .. Segment(2, 52, [0, 0, 0, 1, 0, 0, 0, 1]), .. Segment(3, 62, [0x00, 0x00, 0x12, 0x34, 1, 2]),
            .. MmrRegion(1)], 2, out var truncated);
        Assert.Equal(plain, with);
        Assert.False(truncated);
    }

    /// <summary>
    /// T.88, 7.4.15 (sentences 15 and 16): a single-byte coded comment extension (type 0x20000000: bit 29 set, not necessary) with a name and
    /// a value, null-terminated, the value holding ISO 8859-1 bytes above 127, and the closing empty string — PdfKnead does not interpret
    /// comments; the segment is passed over, the page is the same and nothing is reported.
    /// </summary>
    [Fact]
    public void CommentExtension_Latin1_PassedOver()
    {
        var plain = Decoded([.. PageInfo(8, 2), .. MmrRegion(1)], 2, out _);
        byte[] comment = [0x20, 0x00, 0x00, 0x00, (byte)'N', (byte)'o', (byte)'t', (byte)'e', 0x00, (byte)'C', 0xE9, 0xFC, 0x00, 0x00];
        var with = Decoded([.. PageInfo(8, 2), .. Segment(2, 62, comment), .. MmrRegion(1)], 2, out var truncated);
        Assert.Equal(plain, with);
        Assert.False(truncated);
    }

    /// <summary>
    /// T.88, 7.4.14 (FG 1021): an unknown extension segment flagged necessary (bit 31, with the reserved bit 29) carries data the page needs —
    /// the page is drawn as decoded and reported as incomplete; the same type without bit 31 is passed over without a report.
    /// </summary>
    [Fact]
    public void NecessaryUnknownExtension_ReportedAsIncomplete()
    {
        var plain = Decoded([.. PageInfo(8, 2), .. MmrRegion(1)], 2, out _);
        var necessary = Decoded([.. PageInfo(8, 2), .. Segment(2, 62, [0xA0, 0x00, 0x43, 0x21, 7]), .. MmrRegion(1)], 2, out var truncated);
        Assert.Equal(plain, necessary);
        Assert.True(truncated);
        Decoded([.. PageInfo(8, 2), .. Segment(2, 62, [0x20, 0x00, 0x43, 0x21, 7]), .. MmrRegion(1)], 2, out truncated);
        Assert.False(truncated);
    }

    /// <summary>
    /// T.88, 7.4.14 (Probe 23.11.2026, B2): an extension segment with no data — shorter than its type field — is passed over like any unknown
    /// segment; the region after it is drawn and nothing is reported. The first version of FG 1021 read beyond it and lost the rest of the page.
    /// </summary>
    [Fact]
    public void EmptyExtensionSegment_PassedOver_TheRegionAfterItDrawn()
    {
        var plain = Decoded([.. PageInfo(8, 2), .. MmrRegion(1)], 2, out _);
        Assert.Equal(plain, Decoded([.. PageInfo(8, 2), .. Segment(2, 62, []), .. MmrRegion(1)], 2, out var truncated));
        Assert.False(truncated);
    }

    /// <summary>
    /// T.88, A.2 with Table A.1 (rows as data): each line of the table — its prefix and value bits, the lowest and highest value it codes, and
    /// OOB — encoded bit by bit with the context rule of A.2 (PREV from 1, its nine low bits as the context, step 3) and decoded as the
    /// integer of that line. The data do not come from the own integer encoder, so an error shared by encoder and decoder would show.
    /// </summary>
    [Theory]
    [InlineData("00", 2, 0L, 0)]
    [InlineData("00", 2, 3L, 3)]
    [InlineData("1001", 0, 0L, -1)]
    [InlineData("101", 1, 0L, -2)]
    [InlineData("101", 1, 1L, -3)]
    [InlineData("010", 4, 0L, 4)]
    [InlineData("010", 4, 15L, 19)]
    [InlineData("110", 4, 0L, -4)]
    [InlineData("110", 4, 15L, -19)]
    [InlineData("0110", 6, 0L, 20)]
    [InlineData("0110", 6, 63L, 83)]
    [InlineData("1110", 6, 0L, -20)]
    [InlineData("1110", 6, 63L, -83)]
    [InlineData("01110", 8, 0L, 84)]
    [InlineData("01110", 8, 255L, 339)]
    [InlineData("11110", 8, 0L, -84)]
    [InlineData("11110", 8, 255L, -339)]
    [InlineData("011110", 12, 0L, 340)]
    [InlineData("011110", 12, 4095L, 4435)]
    [InlineData("111110", 12, 0L, -340)]
    [InlineData("111110", 12, 4095L, -4435)]
    [InlineData("011111", 32, 0L, 4436)]
    [InlineData("011111", 32, 1_000_000L, 1_004_436)]
    [InlineData("111111", 32, 0L, -4436)]
    [InlineData("111111", 32, 1_000_000L, -1_004_436)]
    [InlineData("1000", 0, 0L, null)]
    public void IntegerArithmeticDecoding_EveryLineOfTableA1(string prefix, int bits, long field, int? expected)
    {
        var decisions = prefix + (bits == 0 ? "" : Convert.ToString(field, 2).PadLeft(bits, '0'));
        var encoder = new MqEncoder();
        var cx = new byte[512];
        var prev = 1;
        foreach (var c in decisions)
        {
            var d = c - '0';
            encoder.Encode(cx, prev & 511, d);
            prev = prev < 256 ? prev << 1 | d : ((prev << 1 | d) & 511) | 256;
        }
        var data = encoder.Finish();
        Assert.Equal(expected, new MqDecoder(data, 0, data.Length).DecodeInt(new byte[512]));
    }

    /// <summary>T.88, 7.4.9 and 8.2: after the end of page segment (type 49) nothing more is drawn on the page (Zeile 443).</summary>
    [Fact]
    public void RegionAfterTheEndOfPage_NotDrawn()
    {
        Assert.Equal([0xFF, 0xFF], Decoded([.. PageInfo(8, 2), .. Segment(2, 49, []), .. MmrRegion(1)], 2, out _));
    }

    /// <summary>
    /// T.88, 7.4.8.5 and 8.2: the page's combination operator (here XOR) counts for every region unless bit 6 lets regions override it — two
    /// equal regions with the operator OR of their own cancel under XOR when bit 6 is 0, and stay when it is 1 (Zeile 441).
    /// </summary>
    [Fact]
    public void PageOperator_OverriddenOnlyWithBit6()
    {
        byte[] Page(byte flags) => [.. PageInfoWith(8, 2, flags), .. MmrRegion(1), .. MmrRegion(2)];
        Assert.Equal([0xFF, 0xFF], Decoded(Page(0x10), 2, out _));                       // XOR, not overridden: the second clears the first
        Assert.Equal([0b1100_0111, 0b1100_0111], Decoded(Page(0x50), 2, out _));         // XOR, overridden: the regions' OR
    }

    /// <summary>
    /// T.88, 7.4.8 and 7.4.6.2–7.4.6.3 for the encoder: its page information says resolution unknown (0) and „eventually lossless“ with the
    /// default pixel white and the operator OR (flags 0x01); its region flags are 0 (OR, no colour extension); template 0 with the AT pixels at
    /// their nominal places 3,−1, −3,−1, 2,−2, −2,−2 (Zeilen 240, 239, 461, 419).
    /// </summary>
    [Fact]
    public void Encoder_PageInformationRegionFlagsAtPixels()
    {
        var encoded = Jbig2Encoder.Generic([0b1100_0111, 0b1100_0111], 8, 2, 1);
        Assert.Equal(48, encoded[4]);
        Assert.Equal(new byte[8], encoded[(11 + 8)..(11 + 16)]);                          // X and Y resolution
        Assert.Equal(0x01, encoded[11 + 16]);
        var region = 11 + 19;
        Assert.Equal(38, encoded[region + 4]);
        Assert.Equal(0, encoded[region + 11 + 16]);                                      // region flags
        Assert.Equal(0, encoded[region + 28]);                                           // MMR 0, template 0, no TPGDON
        Assert.Equal(new sbyte[] { 3, -1, -3, -1, 2, -2, -2, -2 }, encoded[(region + 29)..(region + 37)].Select(b => (sbyte)b).ToArray());
    }

    /// <summary>
    /// T.88, 7.2.4: the referred-to segment count in its long form (top three bits 7, a 29-bit count and the retention bits) — here a count of
    /// 0 — reads like the short form (Zeile 426).
    /// </summary>
    [Fact]
    public void LongFormSegmentHeader_ReadsLikeTheShortForm()
    {
        var region = MmrRegion(1);
        var data = region[11..];
        byte[] longForm = [0, 0, 0, 1, 38, 0xE0, 0, 0, 0, 0x00, 1, (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, .. data];
        Assert.Equal(Decoded([.. PageInfo(8, 2), .. region], 2, out _), Decoded([.. PageInfo(8, 2), .. longForm], 2, out var truncated));
        Assert.False(truncated);
    }

    /// <summary>
    /// ISO 32000-2, 7.4.7 with T.88, 7.4.2: the symbol dictionary in /JBIG2Globals (page association 0), the text region in the image stream
    /// referring to it — the same page as with both in one stream (Zeile 453).
    /// </summary>
    [Fact]
    public void Globals_DictionaryOutsideThePageStream()
    {
        byte[] dictionary = [0x00, 0x01, 0, 0, 0, 1, 0, 0, 0, 1, 0xB8, 0x7E, 0x00, 0xE0, 0xA0, 0x00, 0x40];
        byte[] text = [.. RegionInfo(8, 4), 0x00, 0x11, 0x00, 0x00, 0, 0, 0, 2, 0x01, .. new byte[17], 0x00, 0x0D, 0x10];
        var inOne = Jbig2Decoder.Decode([.. PageInfo(8, 4), .. Segment(1, 0, dictionary), .. Segment(2, 6, text, 1)], null, 8, 4, ref Unused);
        byte[] globals = [0, 0, 0, 1, 0, 0, 0, (byte)(dictionary.Length >> 24), (byte)(dictionary.Length >> 16), (byte)(dictionary.Length >> 8), (byte)dictionary.Length, .. dictionary];
        var truncated = false;
        var split = Jbig2Decoder.Decode([.. PageInfo(8, 4), .. Segment(2, 6, text, 1)], globals, 8, 4, ref truncated);
        Assert.Equal(inOne, split);
        Assert.False(truncated);
        Assert.Equal([0b0001_0001, 0b0101_0101, 0xFF, 0xFF], split);
    }

    private static bool Unused;

    /// <summary>
    /// T.88, 7.2.7 with 7.4.6.4: an arithmetic generic region of unknown length ends with 0xFF 0xAC and the row count, which may be smaller than
    /// its height — here the encoder's two rows in a region of height 3 (Zeile 439; MMR: GenericRegionWithUnknownLength_FewerRowsThanTheHeight).
    /// </summary>
    [Fact]
    public void ArithmeticRegionOfUnknownLength_FewerRows()
    {
        var encoded = Jbig2Encoder.Generic([0b1100_0111, 0b1100_0111], 8, 2, 1);
        var region = 11 + 19;
        var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(encoded.AsSpan(region + 7));
        var data = encoded[(region + 11)..(region + 11 + length)];
        data[7] = 3;                                                                    // region height 3
        byte[] end = data.AsSpan(data.Length - 2).SequenceEqual(new byte[] { 0xFF, 0xAC }) ? [] : [0xFF, 0xAC];
        byte[] header = [0, 0, 0, 1, 38, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF];
        var image = Decoded([.. PageInfo(8, 3), .. header, .. data, .. end, 0, 0, 0, 2], 3, out var truncated);
        Assert.Equal([0b1100_0111, 0b1100_0111, 0xFF], image);
        Assert.False(truncated);
    }

    /// <summary>
    /// T.88, 7.4.8.2, 7.4.8.6 and 7.4.10: a page of unknown height (0xFFFFFFFF), striped with stripes of at most 2 rows; each end of stripe
    /// segment (type 50) names the last row so far, the regions below it go on the grown page (Zeilen 442, 414).
    /// </summary>
    [Fact]
    public void UnknownPageHeight_GrowsWithItsStripes()
    {
        byte[] Stripe(int number, int lastRow) => Segment(number, 50, [0, 0, 0, (byte)lastRow]);
        var image = Decoded([.. PageInfoWith(8, 0xFFFFFFFF, 0, 0x8002), .. MmrRegion(1, 0), .. Stripe(2, 1), .. MmrRegion(3, 2), .. Stripe(4, 3)], 4, out var truncated);
        Assert.Equal([0b1100_0111, 0b1100_0111, 0b1100_0111, 0b1100_0111], image);
        Assert.False(truncated);
    }

    /// <summary>
    /// T.88, Annex D (D.4.1, D.4.2, D.2, D.3): a stand-alone file with its header, in the sequential organisation (each header followed by its
    /// data) and in the random-access one (all headers first, the end of file segment last among them, then the data in the same order) — the
    /// same page as the embedded segments (Zeilen 452, 238).
    /// </summary>
    [Fact]
    public void StandAloneFile_SequentialAndRandomAccess()
    {
        var embedded = Decoded([.. PageInfo(8, 2), .. MmrRegion(1)], 2, out _);
        byte[] head = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];
        byte[][] segments = [PageInfo(8, 2), MmrRegion(1), Segment(2, 49, []), Segment(3, 51, [])];
        Assert.Equal(embedded, Decoded([.. head, 0x03, .. segments.SelectMany(s => s)], 2, out var sequentialTruncated));
        Assert.False(sequentialTruncated);
        byte[] random = [.. head, 0x02, .. segments.SelectMany(s => s[..11]), .. segments.SelectMany(s => s[11..])];
        Assert.Equal(embedded, Decoded(random, 2, out var randomTruncated));
        Assert.False(randomTruncated);
    }

    /// <summary>
    /// T.88 B.2, B.2.1, B.3 (Bericht M2, Zeile 447): a table segment built by hand — HTPS 3, HTRS 2, HTLOW −3, HTHIGH 5, the lines
    /// (2, 1), (2, 2), (3, 1), lower range line 3, upper 4, OOB 4 — gives the prefix codes of B.3 (00, 01, 100, lower 101, upper 1100, OOB
    /// 1101) and decodes the values at both ends of each line, below HTLOW and from HTHIGH up with 32 range bits, and OOB; without HTOOB the
    /// code of OOB is not a code. The test files with custom tables (Jbig2ExpectedImages) show it only through the image.
    /// </summary>
    [Fact]
    public void TableSegment_BuiltByHand_RangesAndOob()
    {
        static byte[] Segment(bool oob)
        {
            var bits = new List<int>();
            void Put(long v, int n) { for (var i = n - 1; i >= 0; i--) bits.Add((int)(v >> i & 1)); }
            foreach (var (prefix, range) in new[] { (2, 1), (2, 2), (3, 1) }) { Put(prefix, 3); Put(range, 2); }
            Put(3, 3);
            Put(4, 3);
            if (oob) Put(4, 3);
            var bytes = new List<byte> { (byte)((oob ? 1 : 0) | 2 << 1 | 1 << 4), 0xFF, 0xFF, 0xFF, 0xFD, 0, 0, 0, 5 };
            for (var i = 0; i < bits.Count; i += 8) bytes.Add((byte)Enumerable.Range(0, 8).Sum(k => i + k < bits.Count ? bits[i + k] << (7 - k) : 0));
            return [.. bytes];
        }
        static Jbig2BitReader Codes(string code)
        {
            code = code.Replace(" ", "");
            var data = new byte[(code.Length + 7) / 8 + 4];
            for (var i = 0; i < code.Length; i++) if (code[i] == '1') data[i / 8] |= (byte)(0x80 >> (i % 8));
            return new Jbig2BitReader(data, 0, data.Length);
        }
        var bitsOf32 = (long v) => Convert.ToString(v, 2).PadLeft(32, '0');
        var segment = Segment(oob: true);
        var table = Jbig2Huffman.FromSegment(segment, 0, segment.Length);
        var reader = Codes("00 0  00 1  01 00  01 11  100 0  100 1  101 " + bitsOf32(0) + " 101 " + bitsOf32(96) + " 1100 " + bitsOf32(0) +
            " 1100 " + bitsOf32(995) + " 1101");
        Assert.Equal([-3, -2, -1, 2, 3, 4, -4, -100, 5, 1000], Enumerable.Range(0, 10).Select(_ => table.Value(reader)));
        Assert.Null(table.Decode(reader));

        var plain = Segment(oob: false);
        var withoutOob = Jbig2Huffman.FromSegment(plain, 0, plain.Length);
        Assert.Equal(-1, withoutOob.Value(Codes("01 00")));
        Assert.Throws<Jbig2Exception>(() => withoutOob.Decode(Codes("1101 0000")));
    }

    /// <summary>
    /// T.88 6.5.5, 6.5.9, 7.4.2 (Bericht M2, Zeile 236): height class and width differences may be zero or negative — the test files of
    /// SerenityOS have width differences of zero, but no negative ones and no height class difference below one. A Huffman dictionary built by
    /// hand with tables B.5 (SDHUFFDH) and B.3 (SDHUFFDW), the codes written as the tables of B.5 and B.3 print them, collective bitmaps
    /// uncompressed (BMSIZE 0, 6.5.9): height classes 3, then −1 (height 2), then 0 (height 2 again); widths 4, −2 (2), 0 (2); 5; 3, −1 (2) —
    /// six symbols, all exported (B.1: runs 0 and 6).
    /// </summary>
    [Fact]
    public void SymbolDictionary_HuffmanByHand_NegativeAndZeroDifferences()
    {
        var bits = new List<int>();
        void Code(string code) => bits.AddRange(code.Where(ch => ch is '0' or '1').Select(ch => ch - '0'));
        void Value(long v, int n) { for (var i = n - 1; i >= 0; i--) bits.Add((int)(v >> i & 1)); }
        void Bytes(params byte[] bytes)
        {
            while (bits.Count % 8 != 0) bits.Add(0);
            foreach (var b in bytes) Value(b, 8);
        }
        // B.5: 1 → 0, 2 → 10, 3 → 110, −255…0 → 1111110 + (VAL + 255) in 8 bits; B.3: 0 → 0, 3…10 → 1110 + (VAL − 3) in 3 bits,
        // −256…−1 → 11111110 + (VAL + 256) in 8 bits, OOB → 111110; B.1: 0…15 → 0 + VAL in 4 bits
        Code("110");                                         // HCDH +3
        Code("1110"); Value(1, 3);                           // DW +4
        Code("11111110"); Value(254, 8);                     // DW −2
        Code("0");                                           // DW 0
        Code("111110");                                      // OOB
        Code("0"); Value(0, 4);                              // BMSIZE 0: uncompressed
        Bytes(0b1111_10_11, 0b1001_01_00, 0b1111_10_11);     // 4×3 | 2×3 | 2×3
        Code("1111110"); Value(254, 8);                      // HCDH −1
        Code("1110"); Value(2, 3);                           // DW +5
        Code("111110");
        Code("0"); Value(0, 4);
        Bytes(0b10101_000, 0b01010_000);
        Code("1111110"); Value(255, 8);                      // HCDH 0
        Code("1110"); Value(0, 3);                           // DW +3
        Code("11111110"); Value(255, 8);                     // DW −1
        Code("111110");
        Code("0"); Value(0, 4);
        Bytes(0b111_01_000, 0b101_10_000);
        Code("0"); Value(0, 4);                              // export runs: 0 not exported,
        Code("0"); Value(6, 4);                              // 6 exported
        while (bits.Count % 8 != 0) bits.Add(0);
        byte[] head = [0, 1 | 1 << 2 | 1 << 4, 0, 0, 0, 6, 0, 0, 0, 6];   // SDHUFF, SDHUFFDH B.5, SDHUFFDW B.3; 6 exported, 6 new
        byte[] data = [.. head, .. Enumerable.Range(0, bits.Count / 8).Select(i => (byte)Enumerable.Range(0, 8).Sum(k => bits[i * 8 + k] << (7 - k)))];

        var dictionary = Jbig2Regions.SymbolDictionary(data, 0, data.Length, [], new Queue<Jbig2Huffman>(), null, new Jbig2Budget());
        string Rows(Jbig2Bitmap b) => string.Join("/", Enumerable.Range(0, b.Height).Select(y => string.Concat(Enumerable.Range(0, b.Width).Select(x => b.Get(x, y)))));
        Assert.Equal(["1111/1001/1111", "10/01/10", "11/00/11", "10101/01010", "111/101", "01/10"], dictionary.Symbols.Select(Rows));
    }

    /// <summary>
    /// ISO 32000-2, 7.4.7 sentences 30–35 (after the pattern of the example, own data): an image XObject with /Filter [/ASCIIHexDecode
    /// /JBIG2Decode] and /DecodeParms [null &lt;&lt;/JBIG2Globals …&gt;&gt;] — the page stream (page information and text region) hex-encoded, the
    /// symbol dictionary in the globals stream — draws as the same image read without the ASCII step.
    /// </summary>
    [Fact]
    public void Pdf_AsciiHexThenJbig2_WithGlobalsInTheirOwnStream()
    {
        byte[] dictionary = [0x00, 0x01, 0, 0, 0, 1, 0, 0, 0, 1, 0xB8, 0x7E, 0x00, 0xE0, 0xA0, 0x00, 0x40];
        byte[] text = [.. RegionInfo(8, 4), 0x00, 0x11, 0x00, 0x00, 0, 0, 0, 2, 0x01, .. new byte[17], 0x00, 0x0D, 0x10];
        byte[] globals = [0, 0, 0, 1, 0, 0, 0, (byte)(dictionary.Length >> 24), (byte)(dictionary.Length >> 16), (byte)(dictionary.Length >> 8), (byte)dictionary.Length, .. dictionary];
        byte[] page = [.. PageInfo(8, 4), .. Segment(2, 6, text, 1)];
        string L(byte[] b) => System.Text.Encoding.Latin1.GetString(b);
        var hex = Convert.ToHexString(page) + ">";
        byte[] Drawn(string image)
        {
            using var doc = Core.Pdf.Document.PdfDocument.Open(DocumentStructureNormTests.FileOf(
                "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 80 40] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
                "<< /Length 30 >>\nstream\nq 80 0 0 40 0 0 cm /Im0 Do Q   \nendstream", image,
                $"<< /Length {globals.Length} >>\nstream\n{L(globals)}\nendstream"));
            var source = Assert.Single(Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]).Images);
            return Assert.IsType<DecodedImage>(PdfImageDecoder.Decode(source, doc)).Rgba;
        }
        const string head = "/Type /XObject /Subtype /Image /Width 8 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 1";
        var chained = Drawn($"<< {head} /Filter [/ASCIIHexDecode /JBIG2Decode] /DecodeParms [null << /JBIG2Globals 6 0 R >>] /Length {hex.Length} >>\nstream\n{hex}\nendstream");
        var direct = Drawn($"<< {head} /Filter /JBIG2Decode /DecodeParms << /JBIG2Globals 6 0 R >> /Length {page.Length} >>\nstream\n{L(page)}\nendstream");
        Assert.Equal(direct, chained);
        Assert.Contains(chained, b => b == 0);
        Assert.Contains(chained, b => b == 255);
    }
}
