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
}
