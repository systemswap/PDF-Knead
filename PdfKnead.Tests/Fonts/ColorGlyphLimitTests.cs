using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Imaging;

namespace PdfKnead.Tests.Fonts;

/// <summary>
/// Limits of the colour glyph readers (<see cref="ColorGlyphs"/>: CBLC/CBDT, sbix, SVG) against crafted tables
/// (docs/FEHLERGEDAECHTNIS.md, No. 1138). The tables are built here in Liberation Sans; random damage hardly ever produces these cases.
/// </summary>
public sealed class ColorGlyphLimitTests
{
    private static readonly byte[] Liberation = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");

    private static readonly int M = OpenTypeFont.Parse(Liberation).GlyphForUnicode('M');

    private sealed class Table
    {
        public readonly List<byte> Bytes = [];

        public Table U16(int v)
        {
            Bytes.Add((byte)(v >> 8));
            Bytes.Add((byte)v);
            return this;
        }

        public Table U32(long v) => U16((int)(v >> 16)).U16((int)v);

        public Table Add(ReadOnlySpan<byte> data)
        {
            Bytes.AddRange(data.ToArray());
            return this;
        }

        public byte[] ToArray() => [.. Bytes];
    }

    /// <summary>A transparent PNG of the size given (compresses to little).</summary>
    private static byte[] Png(int width, int height)
    {
        using var output = new MemoryStream();
        PngEncoder.Encode(width, height, 8, PngColorType.Rgba, (_, row) => row.Clear(), output, level: CompressionLevel.Fastest);
        return output.ToArray();
    }

    /// <summary>CBLC with one 32-bit strike at 96 ppem whose index subtable (format 1) names glyph „M“ with one image of the format given.</summary>
    private static byte[] Cblc(int format, int imageLength)
    {
        var t = new Table();
        t.U32(0x00030000).U32(1);                    // version 3.0, one strike
        t.U32(56).U32(8 + 16).U32(1).U32(0);         // index subtable array at 56, its size, one subtable, colorRef
        t.Add(new byte[24]);                         // line metrics
        t.U16(M).U16(M).Add([96, 96, 32, 1]);        // the glyph, ppem, 32 bits per pixel, horizontal
        t.U16(M).U16(M).U32(8);                      // its subtable 8 bytes after the array
        t.U16(1).U16(format).U32(4).U32(0).U32(imageLength);   // index format 1, image data at 4
        return t.ToArray();
    }

    /// <summary>
    /// A CBDT image of format 17 or 18 whose data are shorter than its metrics, at the very end of the file: the metrics were read before
    /// the length was checked, and the read behind the file threw.
    /// </summary>
    [Theory]
    [InlineData(17)]
    [InlineData(18)]
    public void CbdtImageShorterThanItsMetrics_AtTheEndOfTheFile_IsNoImage(int format)
    {
        byte[] cbdt = [0, 3, 0, 0, 0x7F, 0x7F];
        var font = ColorFontBuilder.WithTables(Liberation, new Dictionary<string, byte[]?> { ["CBLC"] = Cblc(format, 2), ["CBDT"] = cbdt });
        // the CBDT table moved to the very end of the file, without padding
        var moved = new byte[font.Length + cbdt.Length];
        font.CopyTo(moved, 0);
        cbdt.CopyTo(moved, font.Length);
        var tables = (font[4] << 8) | font[5];
        for (var i = 0; i < tables; i++)
        {
            var record = 12 + 16 * i;
            if (Encoding.ASCII.GetString(font, record, 4) != "CBDT") continue;
            for (var k = 0; k < 4; k++) moved[record + 8 + k] = (byte)(font.Length >> (24 - 8 * k));
        }
        var color = ColorGlyphs.For(OpenTypeFont.Parse(moved));
        Assert.NotNull(color);
        Assert.Null(color.Glyph(M));
    }

    /// <summary>
    /// A CBDT image whose PNG is 4 000 × 4 000 pixels though its metrics are bytes (OpenType CBDT 5.3: at most 255 × 255): it was decoded
    /// (64 MB) and kept in the cache. The size is checked in the PNG header now.
    /// </summary>
    [Fact]
    public void CbdtImageLargerThanItsMetricsAllow_IsNotDecoded()
    {
        var png = Png(4000, 4000);
        var cbdt = new Table().U32(0x00030000).Add([10, 10, 0, 10, 10]).U32(png.Length).Add(png).ToArray();   // small metrics, the PNG
        var font = ColorFontBuilder.WithTables(Liberation, new Dictionary<string, byte[]?> { ["CBLC"] = Cblc(17, cbdt.Length - 4), ["CBDT"] = cbdt });
        var color = ColorGlyphs.For(OpenTypeFont.Parse(font));
        Assert.NotNull(color);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(color.Glyph(M));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 20_000_000);
    }

    /// <summary>
    /// sbix offsets are Offset32 from the strike. One of 2³¹ or more was read as negative and pointed before the strike: here to an image
    /// record the table holds in front of it, which was shown for „M“.
    /// </summary>
    [Fact]
    public void SbixOffsetPointingBeforeTheStrike_IsNoImage()
    {
        var glyphs = OpenTypeFont.Parse(Liberation).GlyphCount;
        var png = Png(8, 8);
        var record = new Table().U16(0).U16(0).Add("png "u8).Add(png).ToArray();
        // header (8), one strike offset (4), the record, then the strike: ppem, ppi, glyphs + 1 offsets
        var strike = 12 + record.Length;
        var t = new Table().U16(1).U16(1).U32(1).U32(strike).Add(record);
        t.U16(96).U16(72);
        var empty = 4 + 4L * (glyphs + 1);
        for (var g = 0; g <= glyphs; g++)
            t.U32(g == M ? (uint)(12 - strike) : g == M + 1 ? (uint)(12 - strike + record.Length) : (uint)empty);
        var font = ColorFontBuilder.WithTables(Liberation, new Dictionary<string, byte[]?> { ["sbix"] = t.ToArray() });
        var color = ColorGlyphs.For(OpenTypeFont.Parse(font));
        Assert.NotNull(color);
        Assert.Null(color.Glyph(M));
    }

    /// <summary>
    /// One gzip-compressed SVG document of 12 MB (mostly a comment) named by 40 records, one per glyph: each glyph unpacked it again,
    /// some half a gigabyte for 40 glyphs. The last document unpacked is kept now.
    /// </summary>
    [Fact]
    public void SvgDocumentSharedByManyRecords_IsUnpackedOnce()
    {
        var png = Png(16, 16);
        var svg = $"""<svg xmlns="http://www.w3.org/2000/svg"><!--{new string('A', 12_000_000)}--><image x="0" y="-1000" width="1000" height="1000" href="data:image/png;base64,{Convert.ToBase64String(png)}"/></svg>""";
        byte[] document;
        using (var output = new MemoryStream())
        {
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes(svg));
            document = output.ToArray();
        }
        const int records = 40;
        var t = new Table().U16(0).U32(10).U32(0).U16(records);
        var offset = 2 + 12 * records;
        for (var g = 1; g <= records; g++) t.U16(g).U16(g).U32(offset).U32(document.Length);
        t.Add(document);
        var font = ColorFontBuilder.WithTables(Liberation, new Dictionary<string, byte[]?> { ["SVG "] = t.ToArray() });
        var color = ColorGlyphs.For(OpenTypeFont.Parse(font));
        Assert.NotNull(color);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var g = 1; g <= records; g++) Assert.NotNull(color.Glyph(g));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 150_000_000, $"{allocated / 1_000_000} MB angelegt");
    }
}
