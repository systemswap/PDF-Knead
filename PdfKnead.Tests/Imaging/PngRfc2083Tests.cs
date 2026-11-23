using System.Buffers.Binary;
using System.IO.Compression;
using PdfKnead.Core.Imaging;

namespace PdfKnead.Tests.Imaging;

/// <summary>
/// The PNG reader and writer against RFC 2083 (PNG 1.0; M2 rows of RFC 2083): what the RFC asks of a decoder is checked and refused, what it
/// leaves to the decoder is passed over, and the writer names no more palette entries than the bit depth can (docs/FEHLERGEDAECHTNIS.md,
/// No. 940, 941).
/// </summary>
public class PngRfc2083Tests
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A chunk with its CRC; <paramref name="badCrc"/> spoils the CRC.</summary>
    private static byte[] Chunk(string type, byte[] body, bool badCrc = false)
    {
        var bytes = new byte[12 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)body.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        body.CopyTo(bytes, 8);
        var crc = Crc32.Update(Crc32.Update(0xFFFFFFFF, bytes.AsSpan(4, 4)), body) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8 + body.Length), badCrc ? crc ^ 1 : crc);
        return bytes;
    }

    /// <summary>IHDR of a 2 × 1 image; <paramref name="tail"/> are the bytes 10–12 (compression, filter, interlace method).</summary>
    private static byte[] Header(byte depth, byte colorType, params byte[] tail)
    {
        var b = new byte[10 + Math.Max(3, tail.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(b, 2);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), 1);
        b[8] = depth;
        b[9] = colorType;
        tail.CopyTo(b, 10);
        return b;
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.SmallestSize)) z.Write(data);
        return output.ToArray();
    }

    /// <summary>One row of a 2 × 1 grey image, 8 bits: filter type, then the two samples.</summary>
    private static readonly byte[] GreyRow = [0, 10, 200];

    private static byte[] Png(params byte[][] chunks) => [.. Signature, .. chunks.SelectMany(c => c)];

    private static byte[] Ok(params byte[][] middle) =>
        Png([Chunk("IHDR", Header(8, 0)), .. middle, Chunk("IDAT", Zlib(GreyRow)), Chunk("IEND", [])]);

    /// <summary>A valid file reads; an ancillary chunk the reader does not know, or one with a wrong CRC, is passed over (3.3, 10.1).</summary>
    [Fact]
    public void Valid_AndAncillaryChunksPassedOver()
    {
        foreach (var file in new[] { Ok(), Ok(Chunk("teSt", [1, 2, 3])), Ok(Chunk("tEXt", "Title\0Probe"u8.ToArray(), badCrc: true)) })
        {
            var image = PngDecoder.Decode(file);
            Assert.Equal(new byte[] { 10, 10, 10, 255, 200, 200, 200, 255 }, image.Pixels);
        }
    }

    public static TheoryData<string, byte[]> Refused => new()
    {
        // 3.3, 4.4, 10.1: an unknown critical chunk cannot be shown safely
        { "unbekannter kritischer Chunk", Ok(Chunk("CRIT", [1])) },
        // 4.1.1, 10.1: compression, filter and interlace method
        { "Kompressionsverfahren 1", Png(Chunk("IHDR", Header(8, 0, 1, 0, 0)), Chunk("IDAT", Zlib(GreyRow)), Chunk("IEND", [])) },
        { "Filterverfahren 1", Png(Chunk("IHDR", Header(8, 0, 0, 1, 0)), Chunk("IDAT", Zlib(GreyRow)), Chunk("IEND", [])) },
        { "Zeilensprungverfahren 2", Png(Chunk("IHDR", Header(8, 0, 0, 0, 2)), Chunk("IDAT", Zlib(GreyRow)), Chunk("IEND", [])) },
        // 10.1: IHDR of another length than 13
        { "IHDR 14 Bytes", Png(Chunk("IHDR", Header(8, 0, 0, 0, 0, 0)), Chunk("IDAT", Zlib(GreyRow)), Chunk("IEND", [])) },
        // 4.1.1, 4.1.2: colour type 3 needs a palette; one palette, before the image data, a whole number of entries
        { "Farbart 3 ohne Palette", Png(Chunk("IHDR", Header(8, 3)), Chunk("IDAT", Zlib([0, 0, 1])), Chunk("IEND", [])) },
        { "zwei Paletten", Png(Chunk("IHDR", Header(8, 3)), Chunk("PLTE", new byte[6]), Chunk("PLTE", new byte[6]), Chunk("IDAT", Zlib([0, 0, 1])), Chunk("IEND", [])) },
        { "Palette nach den Bilddaten", Png(Chunk("IHDR", Header(8, 3)), Chunk("IDAT", Zlib([0, 0, 1])), Chunk("PLTE", new byte[6]), Chunk("IEND", [])) },
        { "Palette mit 5 Bytes", Png(Chunk("IHDR", Header(8, 3)), Chunk("PLTE", new byte[5]), Chunk("IDAT", Zlib([0, 0, 1])), Chunk("IEND", [])) },
        // 3.2, 4.1.1: IHDR first; 4.1.3: the image data in one run
        { "IHDR nicht zuerst", Png(Chunk("teSt", [1]), Chunk("IHDR", Header(8, 0)), Chunk("IDAT", Zlib(GreyRow)), Chunk("IEND", [])) },
        { "Bilddaten unterbrochen", Png(Chunk("IHDR", Header(8, 0)), Chunk("IDAT", Zlib(GreyRow)[..4]), Chunk("teSt", [1]), Chunk("IDAT", Zlib(GreyRow)[4..]), Chunk("IEND", [])) },
        // 10.1: a critical chunk with a wrong CRC
        { "Prüfsumme der Bilddaten falsch", Png(Chunk("IHDR", Header(8, 0)), Chunk("IDAT", Zlib(GreyRow), badCrc: true), Chunk("IEND", [])) },
        // 6: filter method 0 knows the row filters 0 to 4
        { "Zeilenfilter 5", Png(Chunk("IHDR", Header(8, 0)), Chunk("IDAT", Zlib([5, 10, 200])), Chunk("IEND", [])) },
        // 4.1.1: width and height 1 to 2^31 − 1; bit depths 1, 2, 4, 8, 16 and colour types 0, 2, 3, 4, 6 only in the allowed pairs
        { "Breite 0", Png(Chunk("IHDR", HeaderOf(0, 1, 8, 0)), Chunk("IDAT", Zlib([0])), Chunk("IEND", [])) },
        { "Höhe 2^31", Png(Chunk("IHDR", HeaderOf(1, int.MinValue, 8, 0)), Chunk("IDAT", Zlib([0, 1])), Chunk("IEND", [])) },
        { "Bittiefe 3", Png(Chunk("IHDR", Header(3, 0)), Chunk("IDAT", Zlib([0, 0])), Chunk("IEND", [])) },
        { "Farbart 5", Png(Chunk("IHDR", Header(8, 5)), Chunk("IDAT", Zlib(GreyRow)), Chunk("IEND", [])) },
        { "Farbart 2 mit 4 Bit", Png(Chunk("IHDR", Header(4, 2)), Chunk("IDAT", Zlib([0, 0, 0, 0])), Chunk("IEND", [])) },
        { "Farbart 3 mit 16 Bit", Png(Chunk("IHDR", Header(16, 3)), Chunk("PLTE", new byte[6]), Chunk("IDAT", Zlib([0, 0, 0, 0, 1])), Chunk("IEND", [])) },
        { "Farbart 4 mit 2 Bit", Png(Chunk("IHDR", Header(2, 4)), Chunk("IDAT", Zlib([0, 0])), Chunk("IEND", [])) },
    };

    /// <summary>
    /// Security of M2 (PLAN §6 point 8): a header naming more than 2^25 pixels (here 65 536 × 65 536) is refused before anything is allocated
    /// for the image (the limit stood without a test of its own; probe of M2, 19.11.2026).
    /// </summary>
    [Fact]
    public void Size_BeyondTheLimit_RefusedBeforeMemory()
    {
        var header = Header(8, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header, 65536);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 65536);
        var file = Png(Chunk("IHDR", header), Chunk("IDAT", Zlib(GreyRow)), Chunk("IEND", []));
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(file));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1 << 20);
    }

    /// <summary>
    /// A header naming 4 000 × 4 000 pixels over one row of data (found by the PNG fuzzing of M2, 19.11.2026): as a scanner page (data must
    /// cover the page) refused before the image takes its 64 MB; as a glyph or signature the truncated image still shows what it has, as other
    /// readers show it (docs/FEHLERGEDAECHTNIS.md, No. 965).
    /// </summary>
    [Fact]
    public void TinyDataHugeImage_ScannerPageRefusedBeforeMemory_GlyphShowsWhatItHas()
    {
        var header = Header(8, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header, 4000);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 4000);
        var row = new byte[4001];
        row[1] = 77;
        var file = Png(Chunk("IHDR", header), Chunk("IDAT", Zlib(row)), Chunk("IEND", []));
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(file, complete: true));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 4 << 20);
        Assert.ThrowsAny<PdfKnead.Core.Scanning.ScanException>(() => PdfKnead.Core.Scanning.Escl.EsclImage.Decode(file, "image/png", 300));
        var glyph = PngDecoder.Decode(file);
        Assert.Equal((4000, 4000), (glyph.Width, glyph.Height));
    }

    /// <summary>What RFC 2083 asks a decoder to treat as an error is refused with an InvalidDataException (No. 940; before, read anyway).</summary>
    [Theory]
    [MemberData(nameof(Refused))]
    public void Refused_AsRfc2083Asks(string what, byte[] file)
    {
        Assert.False(string.IsNullOrEmpty(what));
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(file));
    }

    /// <summary>
    /// 4.1.2: the palette has no more entries than the bit depth can name — an indexed PDF image of 2 bits with 256 colours in its colour
    /// space gives 4 (No. 941); the image reads back alike.
    /// </summary>
    [Fact]
    public void Writer_PaletteNoLongerThanTheBitDepthNames()
    {
        var palette = new byte[256 * 3];
        for (var i = 0; i < 256; i++) (palette[3 * i], palette[3 * i + 1], palette[3 * i + 2]) = ((byte)i, (byte)(255 - i), 7);
        var alpha = Enumerable.Repeat((byte)128, 256).ToArray();
        using var output = new MemoryStream();
        PngEncoder.Encode(4, 1, 2, PngColorType.Palette, (_, row) => row[0] = 0b00_01_10_11, output, palette, alpha);
        var file = output.ToArray();
        int? plte = null, trns = null;
        for (var at = 8; at + 12 <= file.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at));
            var type = System.Text.Encoding.ASCII.GetString(file, at + 4, 4);
            if (type == "PLTE") plte = length;
            if (type == "tRNS") trns = length;
            at += 12 + length;
        }
        Assert.Equal(12, plte);
        Assert.Equal(4, trns);
        var image = PngDecoder.Decode(file);
        for (var x = 0; x < 4; x++)
            Assert.Equal(new[] { (byte)x, (byte)(255 - x), (byte)7, (byte)128 }, image.Pixels[(4 * x)..(4 * x + 4)]);
    }

    // ---- rows of RFC 2083 split on 20.11.2026 (the check for collective rows): each rule its own test

    /// <summary>The chunks of a PNG file: type and contents.</summary>
    private static List<(string Type, byte[] Body)> Chunks(byte[] file)
    {
        var list = new List<(string, byte[])>();
        for (var at = 8; at + 12 <= file.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at));
            list.Add((System.Text.Encoding.ASCII.GetString(file, at + 4, 4), file[(at + 8)..(at + 8 + length)]));
            at += 12 + length;
        }
        return list;
    }

    /// <summary>
    /// 3.2, 4.1.3, 4.1.4: the writer's file has at least one IDAT and ends with an empty IEND; a palette file has the order IHDR, PLTE, tRNS,
    /// IDAT, IEND; image data above the size of one chunk go into several IDAT right behind each other. The reader refuses a file without image
    /// data (Bericht M2, Zeilen 360, 361).
    /// </summary>
    [Fact]
    public void Writer_ChunkOrder_IdatAndIend()
    {
        using var palette = new MemoryStream();
        PngEncoder.Encode(4, 2, 8, PngColorType.Palette, (y, row) => { for (var x = 0; x < row.Length; x++) row[x] = (byte)((x + y) % 3); }, palette,
            palette: [255, 0, 0, 0, 255, 0, 0, 0, 255], paletteAlpha: [0, 128]);
        var chunks = Chunks(palette.ToArray());
        Assert.Equal(["IHDR", "PLTE", "tRNS", "IDAT", "IEND"], chunks.Select(c => c.Type).Distinct().ToArray());
        Assert.Equal("IEND", chunks[^1].Type);
        Assert.Empty(chunks[^1].Body);

        // random RGB data do not pack: more than one chunk of image data, all in one run
        var random = new Random(7);
        using var large = new MemoryStream();
        PngEncoder.Encode(1200, 1000, 8, PngColorType.Rgb, (_, row) => random.NextBytes(row), large);
        var types = Chunks(large.ToArray()).Select(c => c.Type).ToList();
        Assert.True(types.Count(c => c == "IDAT") > 1);
        var first = types.IndexOf("IDAT");
        Assert.All(types.Skip(first).Take(types.Count(c => c == "IDAT")), c => Assert.Equal("IDAT", c));
        Assert.Equal("IEND", types[^1]);

        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(Png(Chunk("IHDR", Header(8, 0)), Chunk("IEND", []))));
    }

    /// <summary>
    /// 3.3: the third letter of a chunk type is reserved and lower case in no defined chunk — an ancillary chunk with a lower case third letter is
    /// not known, passed over, the image reads (Zeile 365). 10.1: an ancillary chunk behind the image data, before IEND, is passed over; a
    /// transparency chunk there does not apply (tRNS belongs before IDAT, 4.2.9; Zeile 372).
    /// </summary>
    [Fact]
    public void AncillaryChunks_ReservedLetterAndBehindTheData_PassedOver()
    {
        Assert.Equal(new byte[] { 10, 10, 10, 255, 200, 200, 200, 255 }, PngDecoder.Decode(Ok(Chunk("tesT", [1, 2]))).Pixels);
        var late = Png(Chunk("IHDR", Header(8, 0)), Chunk("IDAT", Zlib(GreyRow)), Chunk("teSt", [1]), Chunk("tRNS", [0, 10]), Chunk("IEND", []));
        Assert.Equal(new byte[] { 10, 10, 10, 255, 200, 200, 200, 255 }, PngDecoder.Decode(late).Pixels);
        var early = Png(Chunk("IHDR", Header(8, 0)), Chunk("tRNS", [0, 10]), Chunk("IDAT", Zlib(GreyRow)), Chunk("IEND", []));
        Assert.Equal(0, PngDecoder.Decode(early).Pixels[3]);   // before the data the key applies
    }

    /// <summary>
    /// 5, 4.1.3: the image data are one zlib stream (method 8, FDICT 0 — the writer's begins with 0x78) that may be divided among the IDAT chunks
    /// at any byte; a stream with a preset dictionary is refused (Zeile 367).
    /// </summary>
    [Fact]
    public void Zlib_AcrossChunks_NoPresetDictionary()
    {
        using var written = new MemoryStream();
        PngEncoder.Encode(2, 1, 8, PngColorType.Gray, (_, row) => { row[0] = 10; row[1] = 200; }, written);
        var idat = Chunks(written.ToArray()).First(c => c.Type == "IDAT").Body;
        Assert.Equal(0x78, idat[0]);
        Assert.Equal(0, idat[1] & 0x20);
        var z = Zlib(GreyRow);
        for (var cut = 1; cut < z.Length; cut++)
        {
            var split = Png(Chunk("IHDR", Header(8, 0)), Chunk("IDAT", z[..cut]), Chunk("IDAT", z[cut..]), Chunk("IEND", []));
            Assert.Equal(new byte[] { 10, 10, 10, 255, 200, 200, 200, 255 }, PngDecoder.Decode(split).Pixels);
        }
        byte[] withDictionary = [0x78, 0xBB, 0, 0, 0, 1, .. z[2..]];   // FDICT set (0x78BB is a multiple of 31), DICTID 1
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(Png(Chunk("IHDR", Header(8, 0)), Chunk("IDAT", withDictionary), Chunk("IEND", []))));
    }

    /// <summary>
    /// 2.6, 6.4, 6.6: the first row of each Adam7 pass has no row above it — Up and Paeth take it as zeros. A 2 × 1 grey image: pass 1 (pixel 0) with
    /// Up, pass 6 (pixel 1) with Paeth; both read as the raw values (Zeile 198).
    /// </summary>
    [Fact]
    public void Adam7_FirstRowOfAPass_UpAndPaethAgainstZeros()
    {
        var file = Png(Chunk("IHDR", Header(8, 0, 0, 0, 1)), Chunk("IDAT", Zlib([2, 10, 4, 200])), Chunk("IEND", []));
        Assert.Equal(new byte[] { 10, 10, 10, 255, 200, 200, 200, 255 }, PngDecoder.Decode(file).Pixels);
    }

    /// <summary>IHDR of a <paramref name="width"/> × <paramref name="height"/> image, compression and filter method 0.</summary>
    private static byte[] HeaderOf(int width, int height, byte depth, byte colorType, byte interlace = 0)
    {
        var b = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)height);
        (b[8], b[9], b[12]) = (depth, colorType, interlace);
        return b;
    }

    /// <summary>
    /// 2.3 Sätze 9–15: pixels smaller than a byte are packed without gaps, the leftmost in the high bits; every scanline begins on a byte
    /// boundary, and the bits left over at its end are unspecified. Grey with 2 bits, three pixels a row (values 3, 1, 2 → 255, 85, 170), the
    /// two spare bits once 00 and once 11; grey with 1 bit, nine pixels a row (two bytes, seven spare bits), once zeros and once ones: each
    /// time the same image.
    /// </summary>
    [Fact]
    public void LowBitDepths_PackedFromTheHighBits_SpareBitsAtTheRowEndIgnored()
    {
        var two = PngDecoder.Decode(Png(Chunk("IHDR", HeaderOf(3, 2, 2, 0)), Chunk("IDAT", Zlib([0, 0b11_01_10_00, 0, 0b11_01_10_11])), Chunk("IEND", [])));
        byte[] row = [255, 255, 255, 255, 85, 85, 85, 255, 170, 170, 170, 255];
        Assert.Equal([.. row, .. row], two.Pixels);
        var one = PngDecoder.Decode(Png(Chunk("IHDR", HeaderOf(9, 2, 1, 0)), Chunk("IDAT", Zlib([0, 0b1010_1010, 0b1000_0000, 0, 0b1010_1010, 0b1111_1111])),
            Chunk("IEND", [])));
        Assert.Equal(one.Pixels.AsSpan(0, 36).ToArray(), one.Pixels.AsSpan(36, 36).ToArray());
        Assert.Equal([255, 0, 255, 0, 255, 0, 255, 0, 255], Enumerable.Range(0, 9).Select(x => (int)one.Pixels[x * 4]));
    }

    /// <summary>
    /// 2.4 Sätze 3, 8–13, 18: alpha 0 is fully transparent, 2^bitdepth − 1 fully opaque, stored after the colour samples with their depth; the
    /// colour of a pixel does not depend on its alpha (not premultiplied); without an alpha channel and tRNS every pixel is opaque. RGBA with 8
    /// bits: a pixel (10, 20, 30) with alpha 0 keeps its colour, (40, 50, 60) with alpha 128 too; the same colours as RGB are opaque.
    /// </summary>
    [Fact]
    public void Alpha_NotPremultiplied_WithoutAlphaOpaque()
    {
        var rgba = PngDecoder.Decode(Png(Chunk("IHDR", HeaderOf(2, 1, 8, 6)), Chunk("IDAT", Zlib([0, 10, 20, 30, 0, 40, 50, 60, 128])), Chunk("IEND", [])));
        Assert.Equal(new byte[] { 10, 20, 30, 0, 40, 50, 60, 128 }, rgba.Pixels);
        var rgb = PngDecoder.Decode(Png(Chunk("IHDR", HeaderOf(2, 1, 8, 2)), Chunk("IDAT", Zlib([0, 10, 20, 30, 40, 50, 60])), Chunk("IEND", [])));
        Assert.Equal(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 }, rgb.Pixels);
    }

    /// <summary>
    /// 2.6 Sätze 7–24: Adam7 — the pass of each pixel by the 8 × 8 pattern, within a pass left to right and top to bottom, each pass laid out
    /// as an image of its own with a filter byte per row; an image narrower or lower than five pixels has empty passes, and they carry no
    /// filter byte at all. Own images of 1 × 1, 3 × 2, 4 × 5 and 9 × 9 grey pixels, written pass by pass here: the decoder gives back every
    /// pixel.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 5)]
    [InlineData(9, 9)]
    public void Adam7_SmallImages_EmptyPassesWithoutFilterBytes(int width, int height)
    {
        (int X, int Y, int Dx, int Dy)[] passes = [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)];
        byte Value(int x, int y) => (byte)(x * 37 + y * 101 + 7);
        var data = new List<byte>();
        foreach (var (x0, y0, dx, dy) in passes)
            for (var y = y0; y < height; y += dy)
            {
                if (x0 >= width) break;   // an empty pass: no row, no filter byte
                data.Add(0);
                for (var x = x0; x < width; x += dx) data.Add(Value(x, y));
            }
        var image = PngDecoder.Decode(Png(Chunk("IHDR", HeaderOf(width, height, 8, 0, 1)), Chunk("IDAT", Zlib([.. data])), Chunk("IEND", [])));
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                Assert.Equal(Value(x, y), image.Pixels[(y * width + x) * 4]);
    }

    /// <summary>
    /// 4.1.2 Satz 12: a palette may have fewer entries than the bit depth names — two entries with 2 bits, the indices used 0 and 1, read; an
    /// unused or repeated entry is no error (Satz 21).
    /// </summary>
    [Fact]
    public void Palette_FewerEntriesThanTheDepthNames()
    {
        var image = PngDecoder.Decode(Png(Chunk("IHDR", Header(2, 3)), Chunk("PLTE", [10, 20, 30, 10, 20, 30]), Chunk("IDAT", Zlib([0, 0b00_01_0000])),
            Chunk("IEND", [])));
        Assert.Equal(new byte[] { 10, 20, 30, 255, 10, 20, 30, 255 }, image.Pixels);
    }

    /// <summary>
    /// 4.1.2 Satz 13 (FG 1013): an index beyond a short palette is an error — read as the reference implementation draws it (the palette filled
    /// up to 256 entries with black, opaque), the image reported as damaged; a scanned page with it is refused. Before, the pixel stayed
    /// silently transparent.
    /// </summary>
    [Fact]
    public void PaletteIndexBeyond_BlackOpaque_ReportedAsDamaged()
    {
        var file = Png(Chunk("IHDR", Header(2, 3)), Chunk("PLTE", [10, 20, 30, 40, 50, 60]), Chunk("IDAT", Zlib([0, 0b01_11_0000])), Chunk("IEND", []));
        var image = PngDecoder.Decode(file);
        Assert.True(image.Damaged);
        Assert.Equal(new byte[] { 40, 50, 60, 255, 0, 0, 0, 255 }, image.Pixels);
        Assert.False(PngDecoder.Decode(Ok()).Damaged);
        Assert.ThrowsAny<PdfKnead.Core.Scanning.ScanException>(() => PdfKnead.Core.Scanning.Escl.EsclImage.Decode(file, "image/png", 300));
    }

    /// <summary>
    /// 4.1.3 Sätze 9–13: the compressed data are the concatenation of all IDAT chunks, divided however the encoder likes — even one byte a chunk,
    /// even IDAT chunks of no length: the same image as from one chunk.
    /// </summary>
    [Fact]
    public void Idat_AnyDivision_EvenOneByteAndEmptyChunks()
    {
        var data = Zlib([0, 10, 200]);
        var chunks = new List<byte[]> { Chunk("IHDR", Header(8, 0)), Chunk("IDAT", []) };
        foreach (var b in data) chunks.AddRange([Chunk("IDAT", [b]), Chunk("IDAT", [])]);
        chunks.Add(Chunk("IEND", []));
        Assert.Equal(PngDecoder.Decode(Ok()).Pixels, PngDecoder.Decode(Png([.. chunks])).Pixels);
    }

    /// <summary>
    /// 4.2.9 Sätze 4–10: tRNS of an indexed image — one alpha byte per palette entry, as an 8-bit alpha channel; fewer bytes than entries, and
    /// the rest are opaque (255). Three entries, tRNS of one byte 0: index 0 transparent, 1 and 2 opaque.
    /// </summary>
    [Fact]
    public void Transparency_Palette_ShorterThanThePalette_RestOpaque()
    {
        var image = PngDecoder.Decode(Png(Chunk("IHDR", HeaderOf(3, 1, 2, 3)), Chunk("PLTE", [1, 2, 3, 4, 5, 6, 7, 8, 9]), Chunk("tRNS", [0]),
            Chunk("IDAT", Zlib([0, 0b00_01_10_00])), Chunk("IEND", [])));
        Assert.Equal(new byte[] { 1, 2, 3, 0, 4, 5, 6, 255, 7, 8, 9, 255 }, image.Pixels);
    }

    /// <summary>
    /// 4.2.9 Sätze 11–14, 21: grey with 2 bits — the key in two bytes, whatever the depth; pixels of exactly that level are transparent, the
    /// others opaque. tRNS is prohibited for colour types 4 and 6 (they have alpha already): a tRNS chunk in an RGBA image changes nothing, the
    /// alpha channel stands; one of another length than two bytes for grey is passed over (10.1).
    /// </summary>
    [Fact]
    public void Transparency_GreyKeyInTwoBytes_NoneWithAnAlphaChannel()
    {
        var grey = PngDecoder.Decode(Png(Chunk("IHDR", HeaderOf(3, 1, 2, 0)), Chunk("tRNS", [0, 2]), Chunk("IDAT", Zlib([0, 0b10_01_10_00])),
            Chunk("IEND", [])));
        Assert.Equal([0, 255, 0], Enumerable.Range(0, 3).Select(x => (int)grey.Pixels[x * 4 + 3]));
        var rgba = PngDecoder.Decode(Png(Chunk("IHDR", HeaderOf(1, 1, 8, 6)), Chunk("tRNS", [0, 10, 0, 20, 0, 30]), Chunk("IDAT", Zlib([0, 10, 20, 30, 200])),
            Chunk("IEND", [])));
        Assert.Equal(new byte[] { 10, 20, 30, 200 }, rgba.Pixels);
        // 10.1: tRNS of an unexpected length (four bytes for grey) is passed over as a whole — every pixel opaque
        var longKey = PngDecoder.Decode(Png(Chunk("IHDR", HeaderOf(3, 1, 2, 0)), Chunk("tRNS", [0, 2, 0, 1]), Chunk("IDAT", Zlib([0, 0b10_01_10_00])),
            Chunk("IEND", [])));
        Assert.Equal([255, 255, 255], Enumerable.Range(0, 3).Select(x => (int)longKey.Pixels[x * 4 + 3]));
    }

    /// <summary>
    /// 4.2.5 Sätze 4–9: pHYs — pixels per unit on both axes and the unit, 1 the metre (an inch exactly 0.0254 m), 0 unknown (only the aspect
    /// ratio). A scanned PNG with 11 811 pixels per metre counts as 300 dpi; the same with unit 0, without pHYs, or with a wrong CRC (10.1),
    /// gets the resolution asked for (200).
    /// </summary>
    [Theory]
    [InlineData(1, 300, false)]
    [InlineData(0, 200, false)]
    [InlineData(-1, 200, false)]
    [InlineData(1, 200, true)]
    public void PhysicalPixelDimensions_OnlyTheMetreGivesAResolution(int unit, int dpi, bool badCrc)
    {
        var phys = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(phys, 11811);
        BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4), 11811);
        phys[8] = (byte)Math.Max(0, unit);
        byte[][] middle = unit < 0 ? [] : [Chunk("pHYs", phys, badCrc)];
        var scanned = PdfKnead.Core.Scanning.Escl.EsclImage.Decode(Ok(middle), "image/png", 200);
        Assert.Equal(dpi, scanned.Dpi);
    }

    /// <summary>The filter of RFC 2083, 6.2–6.6 applied to <paramref name="raw"/> (rows of equal length), written here after the formulas.</summary>
    private static byte[] Filtered(byte[][] raw, int bpp, Func<int, int> typeOfRow)
    {
        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;   // left, above, upper left
        }
        var output = new List<byte>();
        for (var y = 0; y < raw.Length; y++)
        {
            var type = typeOfRow(y);
            output.Add((byte)type);
            for (var x = 0; x < raw[y].Length; x++)
            {
                int left = x >= bpp ? raw[y][x - bpp] : 0, up = y > 0 ? raw[y - 1][x] : 0, upLeft = x >= bpp && y > 0 ? raw[y - 1][x - bpp] : 0;
                var predicted = type switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, _ => Paeth(left, up, upLeft) };
                output.Add((byte)(raw[y][x] - predicted));
            }
        }
        return [.. output];
    }

    /// <summary>
    /// 6.1–6.6: the five filters work on bytes, not on pixels, with bpp the bytes of a whole pixel (at least 1); left of the row and above the
    /// first row count as 0; Average takes the floor of the sum without wrapping at 256, Paeth computes its predictor without wrapping and breaks
    /// ties left, above, upper left. Own data, filtered here after the formulas and each row with another filter: grey with 8 bits (values 0–3,
    /// many ties) and 1 bit (bpp 1), RGB with 16 bits (bpp 6, values 0–255 in every byte, sums above 255) — the decoder gives back each image.
    /// </summary>
    [Theory]
    [InlineData(0, 8, 1, 4)]
    [InlineData(0, 1, 1, 2)]
    [InlineData(2, 16, 6, 256)]
    public void Filters_ByteWise_AsTheFormulasOf6(int colorType, int depth, int bpp, int range)
    {
        const int width = 13, height = 10;
        var random = new Random(2083 + depth);
        var rowBytes = (width * depth * (colorType == 2 ? 3 : 1) + 7) / 8;
        var raw = Enumerable.Range(0, height).Select(_ => Enumerable.Range(0, rowBytes).Select(_ => (byte)random.Next(range)).ToArray()).ToArray();
        if (depth == 1) raw = raw.Select(r => r.Select(b => (byte)random.Next(256)).ToArray()).ToArray();
        Assert.Equal(bpp, Math.Max(1, depth * (colorType == 2 ? 3 : 1) / 8));
        var image = PngDecoder.Decode(Png(Chunk("IHDR", HeaderOf(width, height, (byte)depth, (byte)colorType)),
            Chunk("IDAT", Zlib(Filtered(raw, bpp, y => y % 5))), Chunk("IEND", [])));
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * 4;
                if (colorType == 2)
                    for (var c = 0; c < 3; c++)
                        Assert.Equal(((raw[y][x * 6 + 2 * c] << 8 | raw[y][x * 6 + 2 * c + 1]) * 255 + 32767) / 65535, image.Pixels[at + c]);
                else if (depth == 8)
                    Assert.Equal(raw[y][x], image.Pixels[at]);
                else
                    Assert.Equal((raw[y][x / 8] >> (7 - x % 8) & 1) * 255, image.Pixels[at]);
            }
    }

    /// <summary>The filter type byte of every row of a file the writer made (no interlacing).</summary>
    private static List<int> FilterTypes(byte[] file, int rowBytes)
    {
        var data = new MemoryStream();
        for (var at = 8; at + 12 <= file.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at));
            if (System.Text.Encoding.ASCII.GetString(file, at + 4, 4) == "IDAT") data.Write(file, at + 8, length);
            at += 12 + length;
        }
        data.Position = 0;
        using var z = new ZLibStream(data, CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        return [.. raw.ToArray().Where((_, i) => i % (rowBytes + 1) == 0).Select(b => (int)b)];
    }

    /// <summary>
    /// 9.6: the writer filters palette images and depths below 8 bits with filter 0 (Sätze 2, 4), truecolor and grey per row adaptively — the
    /// smallest sum of the output bytes taken as signed differences, for every filter (Sätze 8–10). A grey image of 8 bits whose rows are
    /// 255, 1, 255, 1, …: in the first row filter 0 sums 20 signed (each byte ±1), Sub and Paeth 39, so filter 0 wins (counted unsigned, as
    /// before FG 1015, it summed 2 560 and lost to Sub); in the rows below, Up gives zeros. A palette and a 4-bit grey image of noise stay at
    /// filter 0 in every row.
    /// </summary>
    [Fact]
    public void Writer_FilterSelection_As96()
    {
        var random = new Random(96);
        byte[] Noise(int n) => [.. Enumerable.Range(0, n).Select(_ => (byte)random.Next(256))];
        using var palette = new MemoryStream();
        var indices = Noise(20 * 8);
        PngEncoder.Encode(20, 8, 8, PngColorType.Palette, (y, row) => indices.AsSpan(y * 20, 20).CopyTo(row), palette, palette: Noise(256 * 3));
        Assert.All(FilterTypes(palette.ToArray(), 20), t => Assert.Equal(0, t));
        using var grey4 = new MemoryStream();
        var packed = Noise(10 * 8);
        PngEncoder.Encode(20, 8, 4, PngColorType.Gray, (y, row) => packed.AsSpan(y * 10, 10).CopyTo(row), grey4);
        Assert.All(FilterTypes(grey4.ToArray(), 10), t => Assert.Equal(0, t));
        using var flat = new MemoryStream();
        PngEncoder.Encode(20, 4, 8, PngColorType.Gray, (_, row) =>
        {
            for (var x = 0; x < row.Length; x++) row[x] = x % 2 == 0 ? (byte)255 : (byte)1;
        }, flat);
        Assert.Equal([0, 2, 2, 2], FilterTypes(flat.ToArray(), 20));
    }

    /// <summary>
    /// 10.4 Sätze 1–11: 16-bit samples to 8 bits by the most accurate equation, ROUND(v · 255 / 65535), not by dropping the lower byte (which is
    /// up to one step off): 255 → 1, 32 896 → 128, 65 280 → 254, 65 535 → 255, 0 → 0. The tRNS key is still compared with the whole 16 bits
    /// first (Satz 17).
    /// </summary>
    [Fact]
    public void SixteenBits_ScaledByTheLinearEquation()
    {
        ushort[] values = [255, 32896, 65280, 65535, 0];
        var data = new List<byte> { 0 };
        foreach (var v in values) data.AddRange([(byte)(v >> 8), (byte)v]);
        var image = PngDecoder.Decode(Png(Chunk("IHDR", HeaderOf(5, 1, 16, 0)), Chunk("tRNS", [0xFF, 0x00]), Chunk("IDAT", Zlib([.. data])),
            Chunk("IEND", [])));
        Assert.Equal([1, 128, 254, 255, 0], Enumerable.Range(0, 5).Select(x => (int)image.Pixels[x * 4]));
        Assert.Equal([255, 255, 0, 255, 255], Enumerable.Range(0, 5).Select(x => (int)image.Pixels[x * 4 + 3]));
    }

    /// <summary>
    /// 3.1: the first eight bytes of a PNG file are always 137 80 78 71 13 10 26 10 — any one of them changed (the high bit, the line endings,
    /// the end-of-file byte), and it is no PNG file.
    /// </summary>
    [Fact]
    public void Signature_TheEightBytes()
    {
        var file = Ok();
        Assert.True(PngDecoder.IsPng(file));
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, file[..8]);
        for (var i = 0; i < 8; i++)
        {
            var spoilt = (byte[])file.Clone();
            spoilt[i] ^= i is 0 ? (byte)0x80 : (byte)0x01;
            Assert.False(PngDecoder.IsPng(spoilt), $"Byte {i}");
        }
    }

    /// <summary>
    /// 3.4: the CRC of ISO 3309 — polynomial with pre- and post-conditioning, register all ones, bytes from the least significant bit on, the
    /// result inverted and stored most significant byte first. The CRC of the empty IEND chunk over its type is the well-known X'AE426082', as
    /// in every PNG file; the reader accepts the chunk only with it.
    /// </summary>
    [Fact]
    public void Crc_AsIso3309()
    {
        Assert.Equal(0xAE426082u, Crc32.Update(0xFFFFFFFF, "IEND"u8) ^ 0xFFFFFFFF);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 }, Chunk("IEND", []));
    }

    /// <summary>
    /// 8.4 (with 10.8): drawing a colour glyph — source over destination, not premultiplied: opacity 0 leaves the destination, 255 replaces it,
    /// 128 mixes by its share; on a transparent destination the source stands as it is (Zeile 379). The mixing is done in the stored values,
    /// not in linear light (S-B, decided on 20.11.2026: as PDF mixes in the blending colour space).
    /// </summary>
    [Fact]
    public void Draw_SourceOverDestination()
    {
        var target = new RgbaImage(3, 1, [100, 100, 100, 255, 100, 100, 100, 255, 0, 0, 0, 0]);
        var source = new RgbaImage(3, 1, [200, 0, 0, 0, 200, 0, 0, 128, 200, 0, 0, 128]);
        target.Draw(source, 0, 0);
        Assert.Equal(new byte[] { 100, 100, 100, 255 }, target.Pixels[..4]);
        var a = 128 / 255.0;
        Assert.Equal(new byte[] { (byte)Math.Round(200 * a + 100 * (1 - a)), (byte)Math.Round(100 * (1 - a)), (byte)Math.Round(100 * (1 - a)), 255 },
            target.Pixels[4..8]);
        Assert.Equal(new byte[] { 200, 0, 0, 128 }, target.Pixels[8..12]);
        var opaque = new RgbaImage(1, 1, [100, 100, 100, 255]);
        opaque.Draw(new RgbaImage(1, 1, [1, 2, 3, 255]), 0, 0);
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, opaque.Pixels);
    }

    /// <summary>
    /// RFC 2083, 3.2 (sentences 6, 9): a chunk length never exceeds 2^31 − 1, and type codes are binary values, not character strings. A length
    /// of 2^31 behind the image data ends the reading (as data cut short) — the image before it reads; a length is never taken as a negative
    /// count. A chunk „idat“ (lower case: another type) holding the image data is not IDAT — the file has no image data and is refused.
    /// </summary>
    [Fact]
    public void ChunkLengthAbove2To31_EndsTheReading_TypeCodesComparedAsBytes()
    {
        var huge = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(huge, 0x80000000u);
        "teXt"u8.CopyTo(huge.AsSpan(4));
        var image = PngDecoder.Decode(Png(Chunk("IHDR", Header(8, 0)), Chunk("IDAT", Zlib(GreyRow)), huge));
        Assert.Equal((2, 1), (image.Width, image.Height));
        Assert.Equal(new byte[] { 10, 10, 10, 255, 200, 200, 200, 255 }, image.Pixels);
        Assert.Throws<InvalidDataException>(() => PngDecoder.Decode(Png(Chunk("IHDR", Header(8, 0)), Chunk("idat", Zlib(GreyRow)), Chunk("IEND", []))));
    }

    /// <summary>
    /// RFC 2083, 4.2.6 (sentences 12, 16): each depth sBIT names is above zero and at most the sample depth (8 for palette images), and sBIT
    /// stands before PLTE and the first IDAT. A palette image and a grey image with alpha written with significant bits: sBIT comes before PLTE
    /// and IDAT with the values given; values of 0 or above the sample depth are not written at all.
    /// </summary>
    [Fact]
    public void SignificantBits_BeforePaletteAndImageData_OnlyValidValues()
    {
        using var palette = new MemoryStream();
        PngEncoder.Encode(4, 2, 2, PngColorType.Palette, (y, row) => row.Fill((byte)(y * 0x55)), palette,
            palette: [0, 0, 0, 85, 85, 85, 170, 170, 170, 255, 255, 255], significantBits: [2, 2, 2]);
        var chunks = Chunks(palette.ToArray());
        var types = chunks.Select(c => c.Type).ToList();
        Assert.Equal(new byte[] { 2, 2, 2 }, chunks.Single(c => c.Type == "sBIT").Body);
        Assert.True(types.IndexOf("sBIT") < types.IndexOf("PLTE"));
        Assert.True(types.IndexOf("sBIT") < types.IndexOf("IDAT"));

        using var grey = new MemoryStream();
        PngEncoder.Encode(2, 1, 8, PngColorType.GrayAlpha, (_, row) => row.Fill(0x11), grey, significantBits: [4, 2]);
        var greyTypes = Chunks(grey.ToArray()).Select(c => c.Type).ToList();
        Assert.True(greyTypes.IndexOf("sBIT") is > 0 and var at && at < greyTypes.IndexOf("IDAT"));

        foreach (var wrong in new byte[][] { [0, 2], [9, 2] })
        {
            using var output = new MemoryStream();
            PngEncoder.Encode(2, 1, 8, PngColorType.GrayAlpha, (_, row) => row.Fill(0x11), output, significantBits: wrong);
            Assert.DoesNotContain(Chunks(output.ToArray()), c => c.Type == "sBIT");
        }
    }
}
