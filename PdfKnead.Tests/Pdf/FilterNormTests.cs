using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Conformity check, module M2: ISO 32000-2, 7.4.1 to 7.4.5 (filter chains, ASCII filters, LZW, Flate, predictors, run length). The encoders
/// here are written from the norm's description, independently of PDF-Knead's code, so that the decoders are checked against something
/// foreign (docs/normabgleich/M2-filter.md names the row each test proves).
/// </summary>
public sealed class FilterNormTests
{
    private static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries)
    {
        var d = new PdfDictionary();
        foreach (var (k, v) in entries) d.Set(PdfName.Get(k), v);
        return d;
    }

    private static byte[] Decode(byte[] data, PdfDictionary dict) => StreamFilters.Decode(data, dict).Data;

    private static byte[] Sample(int length, int seed)
    {
        var rng = new Random(seed);
        var b = new byte[length];
        rng.NextBytes(b);
        return b;
    }

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    // ------------------------------------------------------------ 7.4.1 chains and parameters

    /// <summary>7.4.1: filters in the order of the array are applied one after the other; DecodeParms as an array with null for a filter without parameters.</summary>
    [Fact]
    public void Chain_InArrayOrder_ParametersPerFilter()
    {
        var original = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcabcabd ", 300)));
        var lzw = LzwEncode(original, earlyChange: 0);
        var encoded = Ascii85Encode(lzw);
        var dict = Dict(("Filter", new PdfArray { PdfName.Get("ASCII85Decode"), PdfName.Get("LZWDecode") }),
            ("DecodeParms", new PdfArray { PdfNull.Instance, Dict(("EarlyChange", PdfInteger.Of(0))) }));
        Assert.Equal(original, Decode(encoded, dict));
    }

    /// <summary>
    /// 7.4.1 sentences 48, 49, 52, 53: after the pattern of the standard's example (own data) — a page content packed with Flate and then
    /// encoded in ASCII base-85, /Filter [/ASCII85Decode /FlateDecode] without /DecodeParms, decodes to the unfiltered content.
    /// </summary>
    [Fact]
    public void Chain_Ascii85ThenFlate_AsTheExample()
    {
        var content = Encoding.ASCII.GetBytes("2 J BT /F1 12 Tf 72.5 712 TD (Ein ungefilterter Inhalt) Tj T* (zweite Zeile) Tj ET");
        var dict = Dict(("Filter", new PdfArray { PdfName.Get("ASCII85Decode"), PdfName.Get("FlateDecode") }));
        Assert.Equal(content, Decode(Ascii85Encode(Zlib(content)), dict));
    }

    /// <summary>7.4.1, Table 6: a filter that is not one of the standard filters (nor a registered extension) makes the stream unreadable.</summary>
    [Fact]
    public void UnknownFilter_IsAnError() =>
        Assert.Throws<PdfException>(() => Decode([1, 2, 3], Dict(("Filter", PdfName.Get("NoSuchDecode")))));

    // ------------------------------------------------------------ 7.4.2 / 7.4.3 ASCII filters

    /// <summary>
    /// 7.4.2: one byte per pair of hex digits, upper or lower case; white space ignored; „>“ ends the data; an odd final digit counts as
    /// followed by 0. Other characters are passed over instead of being an error, as in widely used readers (sonderregeln.md, A70).
    /// </summary>
    [Fact]
    public void AsciiHex_PairsWhiteSpaceEodOddDigit()
    {
        var dict = Dict(("Filter", PdfName.Get("ASCIIHexDecode")));
        Assert.Equal(new byte[] { 0x4A, 0xbc, 0xD0 }, Decode("4a B\nc\tD>FF"u8.ToArray(), dict));
        Assert.Equal(new byte[] { 0x12, 0x30 }, Decode("123>"u8.ToArray(), dict));
        Assert.Equal(new byte[] { 0x12 }, Decode("1x2>"u8.ToArray(), dict));
    }

    /// <summary>The ASCII base-85 encoder exactly as 7.4.3 describes it (groups of four, „z“ for zeros, partial last group, „~>“).</summary>
    private static byte[] Ascii85Encode(byte[] data)
    {
        var sb = new StringBuilder();
        var i = 0;
        Span<char> c = stackalloc char[5];
        for (; i + 4 <= data.Length; i += 4)
        {
            var v = (uint)(data[i] << 24 | data[i + 1] << 16 | data[i + 2] << 8 | data[i + 3]);
            if (v == 0)
            {
                sb.Append('z');
                continue;
            }
            for (var k = 4; k >= 0; k--, v /= 85) c[k] = (char)(v % 85 + 33);
            sb.Append(c);
        }
        var n = data.Length - i;
        if (n > 0)
        {
            var pad = new byte[4];
            Array.Copy(data, i, pad, 0, n);
            var v = (uint)(pad[0] << 24 | pad[1] << 16 | pad[2] << 8 | pad[3]);
            for (var k = 4; k >= 0; k--, v /= 85) c[k] = (char)(v % 85 + 33);
            sb.Append(c[..(n + 1)]);
        }
        sb.Append("~>");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>7.4.3: base-85 groups, „z“ for four zero bytes, partial final group, white space ignored, „~>“ as end — every length 0 to 41.</summary>
    [Fact]
    public void Ascii85_AgainstTheNormsEncoder()
    {
        var dict = Dict(("Filter", PdfName.Get("ASCII85Decode")));
        for (var length = 0; length <= 41; length++)
        {
            var data = Sample(length, length);
            if (length >= 8) Array.Clear(data, 4, 4);   // a group of zeros: „z“
            var encoded = Ascii85Encode(data);
            Assert.Equal(data, Decode(encoded, dict));
            var spaced = Encoding.ASCII.GetBytes(string.Join("\n ", Encoding.ASCII.GetString(encoded).Chunk(3).Select(c => new string(c))));
            Assert.Equal(data, Decode(spaced, dict));
        }
    }

    // ------------------------------------------------------------ 7.4.4 LZW and Flate

    /// <summary>
    /// The LZW encoder exactly as 7.4.4.2 describes it: a clear-table code first, codes of 9 to 12 bits packed high-order bit first, a new
    /// entry per emitted code, one bit more as soon as the table needs it (EarlyChange: one code early), a clear-table code when the table
    /// is full, the EOD code 257 at the end and zero bits to fill the last byte.
    /// </summary>
    private static byte[] LzwEncode(byte[] data, int earlyChange)
    {
        var bits = new List<bool>();
        var codeLength = 9;
        void Emit(int code)
        {
            for (var b = codeLength - 1; b >= 0; b--) bits.Add(((code >> b) & 1) == 1);
        }
        var table = new Dictionary<string, int>();
        void Reset()
        {
            table.Clear();
            for (var c = 0; c < 256; c++) table[((char)c).ToString()] = c;
            codeLength = 9;
        }
        Reset();
        Emit(256);
        var next = 258;
        var w = "";
        foreach (var b in data)
        {
            var wc = w + (char)b;
            if (table.ContainsKey(wc))
            {
                w = wc;
                continue;
            }
            Emit(table[w]);
            table[wc] = next++;
            if (next + earlyChange > (1 << codeLength) && codeLength < 12) codeLength++;
            if (next == 4096)   // entry 4095 made: the table is full
            {
                Emit(256);
                Reset();
                next = 258;
            }
            w = ((char)b).ToString();
        }
        if (w.Length > 0)
        {
            Emit(table[w]);
            next++;
            if (next + earlyChange > (1 << codeLength) && codeLength < 12) codeLength++;
        }
        Emit(257);
        while (bits.Count % 8 != 0) bits.Add(false);
        var output = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
            if (bits[i]) output[i >> 3] |= (byte)(0x80 >> (i & 7));
        return output;
    }

    /// <summary>
    /// 7.4.4.2 and Table 8: LZW against the norm's encoder — through 10, 11 and 12 bits and a full table that is cleared, with EarlyChange 1
    /// (default) and 0.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Lzw_AgainstTheNormsEncoder(int earlyChange)
    {
        var rng = new Random(5);
        var data = new byte[60_000];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(rng.Next(6) == 0 ? rng.Next(256) : 'a' + i % 13);
        var dict = earlyChange == 1
            ? Dict(("Filter", PdfName.Get("LZWDecode")))
            : Dict(("Filter", PdfName.Get("LZWDecode")), ("DecodeParms", Dict(("EarlyChange", PdfInteger.Of(0)))));
        Assert.Equal(data, Decode(LzwEncode(data, earlyChange), dict));
    }

    /// <summary>
    /// 7.4.4.2, Table 7 (sentences 17–40, 45, 46): the encoding of the standard's example sequence — clear-table, then codes of 9 bits for
    /// single characters and table entries from 258 on, EOD 257 — packed high-order bit first; the norm's encoder of this class writes exactly
    /// these codes and PDF-Knead decodes them back (EarlyChange 1, the default).
    /// </summary>
    [Fact]
    public void Lzw_Table7_EncodedAndDecoded()
    {
        byte[] input = [45, 45, 45, 45, 45, 65, 45, 45, 45, 66];
        // the codes of Table 7: 256 (clear), 45, 258 (45 45), 258, 65, 259 (45 45 45), 66, 257 (EOD) — 9 bits each, high-order bit first
        var bits = string.Concat(new[] { 256, 45, 258, 258, 65, 259, 66, 257 }.Select(c => Convert.ToString(c, 2).PadLeft(9, '0')));
        var packed = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
            if (bits[i] == '1') packed[i >> 3] |= (byte)(0x80 >> (i & 7));
        Assert.Equal(packed, LzwEncode(input, 1));
        Assert.Equal(input, Decode(packed, Dict(("Filter", PdfName.Get("LZWDecode")))));
    }

    /// <summary>
    /// 7.4.1, Table 6 (column „Parameters“): ASCIIHexDecode, ASCII85Decode and RunLengthDecode take no parameters — a /DecodeParms beside them,
    /// even one naming a predictor, changes nothing; FlateDecode and LZWDecode take theirs (the predictor counts there, for both).
    /// </summary>
    [Fact]
    public void Table6_FiltersWithoutParameters_IgnoreDecodeParms()
    {
        var parms = Dict(("Predictor", PdfInteger.Of(12)), ("Columns", PdfInteger.Of(2)), ("Colors", PdfInteger.Of(1)));
        foreach (var (filter, data) in new (string, byte[])[]
                 {
                     ("ASCIIHexDecode", "41 42 43 44>"u8.ToArray()),
                     ("ASCII85Decode", "5sdq,~>"u8.ToArray()),
                     ("RunLengthDecode", [3, (byte)'A', (byte)'B', (byte)'C', (byte)'D', 128]),
                 })
        {
            var plain = Decode(data, Dict(("Filter", PdfName.Get(filter))));
            Assert.Equal("ABCD"u8.ToArray(), plain);
            Assert.Equal(plain, Decode(data, Dict(("Filter", PdfName.Get(filter)), ("DecodeParms", parms))));
        }
        byte[] rows = [2, 1, 2, 2, 3, 4];   // two PNG rows of two bytes, the second „Up“
        Assert.Equal([1, 2, 4, 6], Decode(Zlib(rows), Dict(("Filter", PdfName.Get("FlateDecode")), ("DecodeParms", parms))));
        Assert.Equal([1, 2, 4, 6], Decode(LzwEncode(rows, 1), Dict(("Filter", PdfName.Get("LZWDecode")), ("DecodeParms", parms))));
    }

    /// <summary>
    /// 7.4.4.4, Table 9: the PNG tags are 0 to 4 (None, Sub, Up, Average, Paeth). A row with another tag (5, 7, 255) keeps its bytes, as two of
    /// the reference readers read it, and the stream counts as damaged — in the whole stream and in the image rows (before: read as None
    /// without a note; docs/FEHLERGEDAECHTNIS.md, No. 1009, S-A A118).
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(255)]
    public void Png_RowTagOutsideTable9_DataAsTheyAre_Damage(int tag)
    {
        const int width = 4;
        byte[] rows = [0, 10, 20, 30, 40, (byte)tag, 50, 60, 70, 80, 2, 1, 1, 1, 1];
        var result = StreamFilters.Decode(Zlib(rows), Predicted(12, 1, 8, width));
        Assert.Equal([10, 20, 30, 40, 50, 60, 70, 80, 51, 61, 71, 81], result.Data);
        Assert.True(result.Truncated);
        var valid = StreamFilters.Decode(Zlib([0, 10, 20, 30, 40]), Predicted(12, 1, 8, width));
        Assert.False(valid.Truncated);

        var zlib = Zlib(rows);
        var image = $"<< /Type /XObject /Subtype /Image /Width {width} /Height 3 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode " +
                    $"/DecodeParms << /Predictor 12 /Columns {width} >> /Length {zlib.Length} >>\nstream\n{Latin1(zlib)}\nendstream";
        using var doc = Core.Pdf.Document.PdfDocument.Open(DocumentStructureNormTests.FileOf(
            ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
             "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
             "<< /Length 0 >>\nstream\n\nendstream", image]));
        using var reader = Assert.IsType<Core.Pdf.Graphics.ImageRows>(Core.Pdf.Graphics.ImageRows.Open(doc, Assert.IsType<PdfStream>(doc.GetObject(5)), width));
        var read = new byte[3 * width];
        for (var y = 0; y < 3; y++) Assert.True(reader.Read(read.AsSpan(y * width, width)));
        Assert.Equal(result.Data, read);
        Assert.True(reader.Truncated);
    }

    /// <summary>
    /// 7.4.4.1: Flate is zlib (RFC 1950) around Deflate (RFC 1951). A header RFC 1950 does not allow — none at all (raw Deflate), a compression
    /// method other than 8, a wrong FCHECK, a preset dictionary (FDICT; PDF supplies none) — gives no data and counts as damage, as RFC 1950,
    /// 2.3 asks and the reference readers do (S-A A08; docs/FEHLERGEDAECHTNIS.md, No. 937: before, raw Deflate was tried from byte 0 and from
    /// byte 2, and PDF-Knead showed what no other reader showed). A window above 32 KB is read, as one reader reads it, and counts as damage;
    /// so does a missing or wrong Adler-32 sum. The same for the whole stream and the rented buffer of the content; an empty stream is no damage.
    /// </summary>
    [Fact]
    public void Flate_HeaderAsRfc1950AllowsIt_OtherwiseNoDataAndDamage()
    {
        var data = Sample(5000, 3);
        var dict = Dict(("Filter", PdfName.Get("FlateDecode")));
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) d.Write(data);
        var deflate = ms.ToArray();
        var adler = Zlib(data)[^4..];
        static byte[] Header(int cmf, int flags) => [(byte)cmf, (byte)(flags + (31 - (cmf * 256 + flags) % 31) % 31)];
        void Both(byte[] expected, bool damage, byte[] stream)
        {
            var whole = StreamFilters.Decode(stream, dict);
            using var pooled = StreamFilters.DecodePooled(stream, dict, out var damaged);
            Assert.Equal(expected, whole.Data);
            Assert.Equal(expected, pooled.Span.ToArray());
            Assert.Equal(damage, whole.Truncated);
            Assert.Equal(damage, damaged);
        }
        Both(data, false, Zlib(data));
        Both([], false, []);
        Both([], true, deflate);                                          // no header
        Both([], true, [.. Header(0x7F, 0x80), .. deflate, .. adler]);   // CM 15
        Both(data, true, [.. Header(0x88, 0x80), .. deflate, .. adler]); // CINFO 8: read, damage
        Both([], true, [0x78, 0x9D, .. deflate, .. adler]);              // FCHECK wrong
        Both([], true, [.. Header(0x78, 0xA0), .. deflate, .. adler]);   // FDICT, the data right after the header
        Both([], true, [.. Header(0x78, 0xA0), 0, 0, 0, 1, .. deflate, .. adler]);   // FDICT with DICTID
        // RFC 1950, 2.3: the Adler-32 sum is checked — wrong: the data shown, damage (S-A A71); cut: what came before shown, damage (A09); bytes
        // after the sum (one stream in six of the collections) are no damage
        var zlib = Zlib(data);
        Both(data, false, [.. zlib, 13, 10, .. "  nach der Summe"u8]);
        // a stream that ends at a flush point without the sum (as a widely used writer does in incremental updates): whole, no damage
        using (var flushed = new MemoryStream())
        {
            var z = new ZLibStream(flushed, CompressionLevel.Optimal, leaveOpen: true);
            z.Write(data);
            z.Flush();
            var unfinished = flushed.ToArray();
            Assert.True(unfinished.AsSpan().EndsWith((byte[])[0, 0, 0xFF, 0xFF]));
            Both(data, false, unfinished);
            Both(data, false, [.. unfinished, 13, 10]);
        }
        Both(data, true, [.. zlib[..^4], 0, 0, 0, 0]);
        var cut = StreamFilters.Decode(zlib[..(zlib.Length / 2)], dict);
        Assert.True(cut.Truncated);
        Assert.True(cut.Data.Length > 0 && data.AsSpan().StartsWith(cut.Data));
    }

    /// <summary>
    /// Security of M2 (PLAN §6 point 8): parameters from the file that size buffers stay bounded by the data — a predictor row of 2^24 columns
    /// of 32 colours at 16 bits and a CCITT image of 2^30 columns and rows, each with a few bytes of data, allocate a few megabytes at most
    /// and end at once (the limits stood without a test of their own; probe of M2, 19.11.2026).
    /// </summary>
    [Fact]
    public void CraftedParameters_BuffersStayBoundedByTheData()
    {
        var tiny = Core.Pdf.Filters.Flate.Encode(new byte[64]);
        foreach (var predictor in new[] { 12, 2 })
        {
            var dict = Dict(("Filter", PdfName.Get("FlateDecode")), ("DecodeParms", Dict(("Predictor", PdfInteger.Of(predictor)),
                ("Colors", PdfInteger.Of(32)), ("BitsPerComponent", PdfInteger.Of(16)), ("Columns", PdfInteger.Of(1 << 24)))));
            var before = GC.GetAllocatedBytesForCurrentThread();
            _ = StreamFilters.Decode(tiny, dict);
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 8 << 20, $"Prädiktor {predictor}");
        }
        // CCITT: the working rows follow the column limit (2^20: a few megabytes), the image rows only the rows decoded — not /Rows and not the
        // length of the data (before No. 961 a kilobyte took nearly a gigabyte at once)
        var ccitt = Dict(("Filter", PdfName.Get("CCITTFaxDecode")), ("DecodeParms", Dict(("K", PdfInteger.Of(-1)),
            ("Columns", PdfInteger.Of(1 << 30)), ("Rows", PdfInteger.Of(1 << 30)))));
        foreach (var data in new[] { new byte[] { 0x00, 0x10, 0x01, 0x00, 0x10, 0x01 }, new byte[1024] })
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            _ = StreamFilters.Decode(data, ccitt);
            var ccittBytes = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.True(ccittBytes < 16 << 20, $"CCITT, {data.Length} Bytes: {ccittBytes >> 20} MB");
        }
    }

    /// <summary>
    /// S-A A67 for the keys of M2 (7.3.9: an entry whose reference leads nowhere counts as absent): /DecodeParms into the void lets the
    /// abbreviation /DP count, as if /DecodeParms were missing — and so does /ColorSpace into the void beside /CS in an image
    /// (docs/FEHLERGEDAECHTNIS.md, No. 948; before, the key into the void won, and the parameters and the colour space were lost).
    /// /JBIG2Globals, /SMaskInData, /ColorTransform and /Name of /Crypt into the void already counted as absent (each read through its value).
    /// </summary>
    [Fact]
    public void KeysIntoTheVoid_CountAsAbsent()
    {
        // two rows of PNG filter type None (0) with three bytes each: with the predictor ABCDEF, without it the type bytes stay in
        var zlib = Core.Pdf.Filters.Flate.Encode([0, (byte)'A', (byte)'B', (byte)'C', 0, (byte)'D', (byte)'E', (byte)'F']);
        var image = Core.Pdf.Filters.Flate.Encode([0xFF, 0x00, 0x00]);   // one red pixel
        var file = DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>",
            "<< /Length 26 >>\nstream\nq 9 0 0 9 0 0 cm /Im1 Do Q\nendstream",
            $"<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace 91 0 R /CS /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode " +
            $"/Length {image.Length} >>\nstream\n{Latin1(image)}\nendstream",
            $"<< /Length {zlib.Length} /Filter /FlateDecode /DecodeParms 90 0 R /DP << /Predictor 12 /Columns 3 >> >>\nstream\n{Latin1(zlib)}\nendstream");
        using var doc = Core.Pdf.Document.PdfDocument.Open(file);
        Assert.Equal("ABCDEF"u8.ToArray(), doc.DecodeStream(Assert.IsType<PdfStream>(doc.GetObject(6))).Data);
        var page = Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]);
        var decoded = Assert.IsType<Core.Pdf.Graphics.DecodedImage>(Core.Pdf.Graphics.PdfImageDecoder.Decode(Assert.Single(page.Images), doc));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, decoded.Rgba[..4]);
    }

    /// <summary>
    /// A Flate stream whose zlib header RFC 1950 does not allow gives no data to the display (as with the reference readers, No. 937) — but
    /// the leak search still finds a word in it: it can be recovered from the file (docs/FEHLERGEDAECHTNIS.md, No. 947; before, only the
    /// display decoded such streams, and after No. 937 nothing did).
    /// </summary>
    [Theory]
    [InlineData("ohne")]
    [InlineData("fcheck")]
    [InlineData("cm15")]
    public void LeakSearch_FindsWordsInStreamsWithoutAValidZlibHeader(string header)
    {
        var zlib = Core.Pdf.Filters.Flate.Encode("BT /F1 24 Tf 72 700 Td (GEHEIMWORT) Tj ET"u8);
        var deflate = zlib[2..];
        byte[] stream = header switch
        {
            "ohne" => deflate[..^4],
            "fcheck" => [0x78, 0x00, .. deflate],
            _ => [0x7F, 0x00, .. deflate],
        };
        var bytes = TestFiles.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            [4] = ("/Filter/FlateDecode", System.Text.Encoding.Latin1.GetString(stream)),
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        });
        using (var doc = Core.Pdf.Document.PdfDocument.Open(bytes))
            Assert.Empty(doc.DecodeStream(Assert.IsType<PdfStream>(doc.GetObject(4))).Data);
        Assert.NotEmpty(PdfKnead.Cli.LeakSearch.Find(bytes, ["GEHEIMWORT"], null, out _));
    }

    /// <summary>
    /// RFC 1950, 8.2: Adler-32 sixteen bytes at a time gives what the sum byte by byte gives — at the edges of the blocks of 5552 bytes, for
    /// the largest bytes (where the sums grow fastest), from an offset start, and for the common example „Wikipedia“ (0x11E60398).
    /// </summary>
    [Fact]
    public void Adler32_VectorsLikeByteByByte()
    {
        static uint Plain(ReadOnlySpan<byte> data)
        {
            uint a = 1, b = 0;
            foreach (var x in data)
            {
                a = (a + x) % 65521;
                b = (b + a) % 65521;
            }
            return b << 16 | a;
        }
        Assert.Equal(0x11E60398u, Core.Pdf.Filters.Flate.Adler32("Wikipedia"u8));
        Assert.Equal(1u, Core.Pdf.Filters.Flate.Adler32([]));
        var random = new Random(946);
        foreach (var length in new[] { 1, 15, 16, 17, 31, 32, 100, 5551, 5552, 5553, 5568, 11104, 11105, 65536, 200_003 })
        {
            var data = new byte[length];
            random.NextBytes(data);
            Assert.Equal(Plain(data), Core.Pdf.Filters.Flate.Adler32(data));
            Array.Fill(data, (byte)0xFF);
            Assert.Equal(Plain(data), Core.Pdf.Filters.Flate.Adler32(data));
            // an offset start: the chunks do not begin at the start of an array
            random.NextBytes(data);
            Assert.Equal(Plain(data.AsSpan(length / 3)), Core.Pdf.Filters.Flate.Adler32(data.AsSpan(length / 3)));
        }
    }

    /// <summary>
    /// Brotli (extension of the PDF Association) through the rented buffer of the content gives the same data and the same damage as the
    /// whole stream (docs/FEHLERGEDAECHTNIS.md, No. 936, Nachtrag: before a new array per stream).
    /// </summary>
    [Fact]
    public void Brotli_PooledLikeTheWholeStream()
    {
        var data = Sample(200_000, 7);
        var packed = Core.Pdf.Filters.Brotli.Encode(data);
        var dict = Dict(("Filter", PdfName.Get("BrotliDecode")));
        foreach (var (stream, damaged) in new[] { (packed, false), (packed[..(packed.Length / 2)], true) })
        {
            var whole = StreamFilters.Decode(stream, dict);
            using var pooled = StreamFilters.DecodePooled(stream, dict, out var pooledDamage);
            Assert.Equal(whole.Data, pooled.Span.ToArray());
            Assert.Equal(whole.Truncated, pooledDamage);
            if (!damaged) Assert.Equal(data, whole.Data);
        }
    }

    /// <summary>
    /// 7.4.4.4, Table 8: /Predictor 1 none, 2 TIFF, 10–15 PNG; any other value is not assigned — the data are read as they are, as the reference
    /// readers read them, and the stream counts as damage (S-A A73; docs/FEHLERGEDAECHTNIS.md, No. 938). The same in the image rows the display
    /// reads large images with: before, the whole stream read 3–9 as PNG and the rows as none.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(16)]
    public void Predictor_ValueTable8DoesNotAssign_DataAsTheyAre_Damage_AlikeInImageRows(int predictor)
    {
        const int width = 8, height = 4;
        var samples = Sample(width * height, 11);
        var png = new byte[height * (width + 1)];
        for (var y = 0; y < height; y++)
        {
            png[y * (width + 1)] = 1;   // Sub
            for (var x = 0; x < width; x++)
                png[y * (width + 1) + 1 + x] = (byte)(samples[y * width + x] - (x > 0 ? samples[y * width + x - 1] : 0));
        }
        var parms = Dict(("Predictor", PdfInteger.Of(predictor)), ("Columns", PdfInteger.Of(width)));
        var dict = Dict(("Filter", PdfName.Get("FlateDecode")), ("DecodeParms", parms));
        var result = StreamFilters.Decode(Zlib(png), dict);
        Assert.Equal(png, result.Data);
        Assert.True(result.Truncated);
        // PNG with the same data: the samples (the row names its filter)
        var valid = StreamFilters.Decode(Zlib(png), Dict(("Filter", PdfName.Get("FlateDecode")),
            ("DecodeParms", Dict(("Predictor", PdfInteger.Of(12)), ("Columns", PdfInteger.Of(width))))));
        Assert.Equal(samples, valid.Data);
        Assert.False(valid.Truncated);

        // the image rows read the same bytes and report the same damage
        var zlib = Zlib(png);
        var image = $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent 8 " +
                    $"/Filter /FlateDecode /DecodeParms << /Predictor {predictor} /Columns {width} >> /Length {zlib.Length} >>\nstream\n{Latin1(zlib)}\nendstream";
        using var doc = Core.Pdf.Document.PdfDocument.Open(DocumentStructureNormTests.FileOf(
            ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
             "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
             "<< /Length 0 >>\nstream\n\nendstream", image]));
        var stream = Assert.IsType<PdfStream>(doc.GetObject(5));
        using var rows = Assert.IsType<Core.Pdf.Graphics.ImageRows>(Core.Pdf.Graphics.ImageRows.Open(doc, stream, width));
        var read = new byte[height * width];
        for (var y = 0; y < height; y++) Assert.True(rows.Read(read.AsSpan(y * width, width)));
        Assert.Equal(png[..(height * width)], read);
        Assert.True(rows.Truncated);
    }

    /// <summary>The image rows take the zlib header by the same rule as the whole stream (No. 937): raw Deflate gives no row and damage.</summary>
    [Fact]
    public void ImageRows_ZlibHeaderAsTheWholeStream()
    {
        var samples = Sample(32, 5);
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) d.Write(samples);
        var raw = ms.ToArray();
        var image = $"<< /Type /XObject /Subtype /Image /Width 8 /Height 4 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode " +
                    $"/Length {raw.Length} >>\nstream\n{Latin1(raw)}\nendstream";
        using var doc = Core.Pdf.Document.PdfDocument.Open(DocumentStructureNormTests.FileOf(
            ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
             "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
             "<< /Length 0 >>\nstream\n\nendstream", image]));
        var stream = Assert.IsType<PdfStream>(doc.GetObject(5));
        using var rows = Assert.IsType<Core.Pdf.Graphics.ImageRows>(Core.Pdf.Graphics.ImageRows.Open(doc, stream, 8));
        Assert.False(rows.Read(new byte[8]));
        Assert.True(rows.Truncated);
        Assert.True(doc.DecodeStream(stream).Truncated);
        Assert.Empty(doc.DecodeStream(stream).Data);
    }

    // ------------------------------------------------------------ 7.4.4.4 predictors

    private static int Bpp(int colors, int bits) => Math.Max(1, (colors * bits + 7) / 8);

    /// <summary>One PNG row filter of the given type, written from the PNG recommendation the norm names (distance: one pixel in whole bytes).</summary>
    private static byte[] PngEncode(byte[] samples, int width, int height, int colors, int bits, Func<int, int> type)
    {
        var stride = (width * colors * bits + 7) / 8;
        var bpp = Bpp(colors, bits);
        var output = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var t = type(y);
            output[y * (stride + 1)] = (byte)t;
            for (var i = 0; i < stride; i++)
            {
                int x = samples[y * stride + i];
                int a = i >= bpp ? samples[y * stride + i - bpp] : 0;
                int b = y > 0 ? samples[(y - 1) * stride + i] : 0;
                int c = i >= bpp && y > 0 ? samples[(y - 1) * stride + i - bpp] : 0;
                var p = a + b - c;
                int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                var predicted = t switch
                {
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    4 => pa <= pb && pa <= pc ? a : pb <= pc ? b : c,
                    _ => 0,
                };
                output[y * (stride + 1) + 1 + i] = (byte)(x - predicted);
            }
        }
        return output;
    }

    private static PdfDictionary Predicted(int predictor, int colors, int bits, int columns) =>
        Dict(("Filter", PdfName.Get("FlateDecode")), ("DecodeParms", Dict(("Predictor", PdfInteger.Of(predictor)), ("Colors", PdfInteger.Of(colors)),
            ("BitsPerComponent", PdfInteger.Of(bits)), ("Columns", PdfInteger.Of(columns)))));

    /// <summary>
    /// 7.4.4.4, Tables 9 and 10: every PNG filter type, each row with its own tag; any Predictor from 10 to 15 reads the same; the distance
    /// is one pixel in whole bytes (also for pixels PNG does not know, such as three components of 4 bits); samples outside the image are 0.
    /// </summary>
    [Theory]
    [InlineData(1, 8)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(1, 1)]
    [InlineData(1, 4)]
    [InlineData(3, 4)]
    [InlineData(3, 2)]
    [InlineData(5, 4)]
    public void Png_AllTypes_AnyPredictorFrom10(int colors, int bits)
    {
        const int width = 23, height = 15;
        var stride = (width * colors * bits + 7) / 8;
        var samples = Sample(stride * height, colors * 100 + bits);
        var rows = PngEncode(samples, width, height, colors, bits, y => y % 5);
        foreach (var predictor in new[] { 10, 12, 15 })
            Assert.Equal(samples, Decode(Zlib(rows), Predicted(predictor, colors, bits, width)));
    }

    /// <summary>TIFF predictor 2 as 7.4.4.4 describes it: each component from the same component of the pixel to its left, modulo 2^bits.</summary>
    private static byte[] TiffEncode(byte[] samples, int width, int height, int colors, int bits)
    {
        var stride = (width * colors * bits + 7) / 8;
        var output = new byte[samples.Length];
        long Get(byte[] s, int row, long index)
        {
            if (bits == 16) return s[row * stride + index * 2] << 8 | s[row * stride + index * 2 + 1];
            var at = index * bits;
            return (s[row * stride + (int)(at >> 3)] >> (8 - bits - (int)(at & 7))) & ((1 << bits) - 1);
        }
        void Put(int row, long index, long value)
        {
            if (bits == 16)
            {
                output[row * stride + index * 2] = (byte)(value >> 8);
                output[row * stride + index * 2 + 1] = (byte)value;
                return;
            }
            var at = index * bits;
            var shift = 8 - bits - (int)(at & 7);
            output[row * stride + (int)(at >> 3)] |= (byte)((value & ((1 << bits) - 1)) << shift);
        }
        for (var y = 0; y < height; y++)
            for (long s = 0; s < (long)width * colors; s++)
                Put(y, s, Get(samples, y, s) - (s >= colors ? Get(samples, y, s - colors) : 0) + (1L << bits));
        return output;
    }

    /// <summary>7.4.4.4: TIFF predictor 2 at every depth the table allows (1, 2, 4, 8, 16) and with several components.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(1, 4)]
    [InlineData(3, 8)]
    [InlineData(2, 16)]
    public void Tiff2_EveryDepth(int colors, int bits)
    {
        const int width = 19, height = 7;
        var stride = (width * colors * bits + 7) / 8;
        var samples = Sample(stride * height, colors * 10 + bits);
        // padding bits at the end of a row are not samples: keep them 0 in both
        var used = width * colors * bits;
        if (used % 8 != 0)
            for (var y = 0; y < height; y++) samples[y * stride + stride - 1] &= (byte)(0xFF << (8 - used % 8));
        Assert.Equal(samples, Decode(Zlib(TiffEncode(samples, width, height, colors, bits)), Predicted(2, colors, bits, width)));
    }

    /// <summary>
    /// 7.4.4.4 for the writer: PDF-Knead's own PNG predictor (used when compressing images losslessly) reads back exactly — also for pixel sizes
    /// that are not whole bytes (docs/FEHLERGEDAECHTNIS.md, No. 728: three components of 4 bits came back corrupt).
    /// </summary>
    [Theory]
    [InlineData(1, 8)]
    [InlineData(3, 8)]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 4)]
    [InlineData(3, 16)]
    public void Writer_PngPredictor_ReadsBackExactly(int colors, int bits)
    {
        const int width = 40, height = 12;
        var stride = (width * colors * bits + 7) / 8;
        var samples = new byte[stride * height];
        for (var y = 0; y < height; y++)
            for (var i = 0; i < stride; i++) samples[y * stride + i] = (byte)(i * 3 + y * 5);   // smooth: Sub, Average and Paeth win
        var rows = PngPredictor.Encode(samples, width, height, colors, bits);
        Assert.Contains(Enumerable.Range(0, height), y => rows[y * (stride + 1)] is 1 or 3 or 4);
        Assert.Equal(samples, Decode(Zlib(rows), Predicted(15, colors, bits, width)));
    }

    /// <summary>7.4.4.3: Predictor 1 or absent means no predictor.</summary>
    [Fact]
    public void Predictor1_IsNone()
    {
        var data = Sample(100, 9);
        Assert.Equal(data, Decode(Zlib(data), Predicted(1, 3, 8, 10)));
        Assert.Equal(data, Decode(Zlib(data), Dict(("Filter", PdfName.Get("FlateDecode")), ("DecodeParms", new PdfDictionary()))));
    }

    /// <summary>
    /// Table 8: Colors, BitsPerComponent and Columns count only with a Predictor above 1 (<see cref="Predictor1_IsNone"/>) and default to 1, 8
    /// and 1 (docs/normabgleich/M2-filter.md, row 13): without the three entries PNG „Up“ and TIFF predictor 2 read rows of one byte, as
    /// with them given so — one row over all the data would read otherwise.
    /// </summary>
    [Fact]
    public void Predictor_ColorsBitsColumns_DefaultOneEightOne()
    {
        byte[] rows = [2, 5, 2, 3, 2, 250];   // three rows of one byte, each with the tag „Up“
        var png = Dict(("Filter", PdfName.Get("FlateDecode")), ("DecodeParms", Dict(("Predictor", PdfInteger.Of(12)))));
        Assert.Equal([5, 8, 2], Decode(Zlib(rows), png));
        Assert.Equal(Decode(Zlib(rows), Predicted(12, 1, 8, 1)), Decode(Zlib(rows), png));
        var tiff = Dict(("Filter", PdfName.Get("FlateDecode")), ("DecodeParms", Dict(("Predictor", PdfInteger.Of(2)))));
        Assert.Equal([5, 3, 250], Decode(Zlib([5, 3, 250]), tiff));   // one pixel per row: nothing to its left
    }

    /// <summary>
    /// Table 8 (7.4.4.3 sentences 13, 14, 16, 17): /Colors is 1 or greater since PDF 1.3 — a predictor of 33 and 40 colours decodes as it is
    /// given, TIFF and PNG (before, clamped to 32 without a note: wrong data; docs/FEHLERGEDAECHTNIS.md, No. 1007). Of the reference readers
    /// only one reads such a stream (S-A A117).
    /// </summary>
    [Theory]
    [InlineData(33)]
    [InlineData(40)]
    public void Predictor_ColorsAbove32_DecodedAsGiven(int colors)
    {
        const int width = 3, height = 4;
        var samples = Sample(colors * width * height, colors);
        var tiff = StreamFilters.Decode(Zlib(TiffEncode(samples, width, height, colors, 8)), Predicted(2, colors, 8, width));
        Assert.Equal(samples, tiff.Data);
        Assert.False(tiff.Truncated);
        var png = StreamFilters.Decode(Zlib(PngEncode(samples, width, height, colors, 8, y => y % 5)), Predicted(12, colors, 8, width));
        Assert.Equal(samples, png.Data);
        Assert.False(png.Truncated);
    }

    /// <summary>
    /// Table 8 (7.4.4.3 sentences 16, 20, 23): /Colors below 1, /BitsPerComponent other than 1, 2, 4, 8, 16 and /Columns below 1 are values the
    /// table does not allow — the data as they are and the stream damaged, the rule of an unassigned /Predictor (No. 938); the image rows hand
    /// such a stream to the whole decoding, which reads it by the same rule (before: clamped without a note, 3 and 12 bits passed; No. 1007).
    /// So do the bounds against malicious files, /Colors above <see cref="Predictor.MaxColors"/> and /Columns above 2^24.
    /// </summary>
    [Theory]
    [InlineData(0, 8, 8)]
    [InlineData(-3, 8, 8)]
    [InlineData(1, 3, 8)]
    [InlineData(1, 12, 8)]
    [InlineData(1, 32, 8)]
    [InlineData(1, 8, 0)]
    [InlineData(Predictor.MaxColors + 1, 8, 1)]
    [InlineData(1, 8, (1 << 24) + 1)]
    public void Predictor_ValueTable8DoesNotAllow_DataAsTheyAre_Damage(int colors, int bits, int columns)
    {
        var data = Sample(64, 7);
        foreach (var predictor in new[] { 2, 12 })
        {
            var result = StreamFilters.Decode(Zlib(data), Predicted(predictor, colors, bits, columns));
            Assert.Equal(data, result.Data);
            Assert.True(result.Truncated);
        }
        var zlib = Zlib(data);
        var image = $"<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode " +
                    $"/DecodeParms << /Predictor 12 /Colors {colors} /BitsPerComponent {bits} /Columns {columns} >> /Length {zlib.Length} >>\nstream\n{Latin1(zlib)}\nendstream";
        using var doc = Core.Pdf.Document.PdfDocument.Open(DocumentStructureNormTests.FileOf(
            ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
             "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
             "<< /Length 0 >>\nstream\n\nendstream", image]));
        Assert.Null(Core.Pdf.Graphics.ImageRows.Open(doc, Assert.IsType<PdfStream>(doc.GetObject(5)), 8));
    }

    // ------------------------------------------------------------ 7.4.5 run length

    /// <summary>7.4.5: length 0–127 copies length + 1 bytes, 129–255 repeats one byte 257 − length times, 128 is the end.</summary>
    [Fact]
    public void RunLength_LiteralRepeatAndEnd()
    {
        var dict = Dict(("Filter", PdfName.Get("RunLengthDecode")));
        byte[] encoded = [2, (byte)'a', (byte)'b', (byte)'c', 255, (byte)'x', 129, (byte)'y', 127, .. Enumerable.Repeat((byte)'z', 128), 128, (byte)'Q'];
        byte[] expected = [(byte)'a', (byte)'b', (byte)'c', (byte)'x', (byte)'x', .. Enumerable.Repeat((byte)'y', 128), .. Enumerable.Repeat((byte)'z', 128)];
        Assert.Equal(expected, Decode(encoded, dict));
    }

    // ---- Nachweise added in the Nacharbeit (12.11.2026) for rows that stood as „umgesetzt“ without one

    /// <summary>One page of 200 × 200 with the given content and the image /Im0 (a Latin-1 string of the whole object), rendered at 72 dpi.</summary>
    private static SkiaSharp.SKBitmap RenderedWith(string content, string? image = null)
    {
        var xobject = image is null ? "" : "/Resources << /XObject << /Im0 5 0 R >> >> ";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] {xobject}/Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        ];
        using var doc = Core.Pdf.Document.PdfDocument.Open(DocumentStructureNormTests.FileOf(image is null ? objects : [.. objects, image]));
        var page = doc.Pages[0];
        return Rendering.SkiaRenderer.RenderPage(Core.Pdf.Content.PageInterpreter.Build(doc, page), doc, page, 1, new Rendering.ImageCache());
    }

    private static bool Blue(SkiaSharp.SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, y) is { Blue: > 200, Red: < 60 };

    private static bool White(SkiaSharp.SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, y) is { Red: > 250, Green: > 250, Blue: > 250 };

    /// <summary>A Latin-1 string of the bytes (FileOf writes strings as Latin-1).</summary>
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    /// <summary>
    /// 7.4.7 with 8.9.7: JBIG2Decode is not used in inline images. Writer: packing a page with JBIG2 (the default for 1-bit images) leaves its
    /// 1-bit inline image without it. Reader: an inline image naming it anyway is decoded like the same data in an image XObject — display,
    /// text and redaction take images from the same decoder (S-A A86) (M2 row 30).
    /// </summary>
    [Fact]
    public void Jbig2_NotInInlineImages_WriterNeverPutsItThere_ReaderReadsItAlike()
    {
        // 16 × 16, the left half black (0 bits are black without /Decode in DeviceGray)
        var bits = new byte[2 * 16];
        for (var y = 0; y < 16; y++) bits[2 * y + 1] = 0xFF;
        var jbig2 = Core.Pdf.Filters.Jbig2.Jbig2Encoder.Generic(bits, 16, 16, 2);
        var inline = $"q 100 0 0 100 50 50 cm BI /W 16 /H 16 /BPC 1 /CS /G /F /JBIG2Decode /L {jbig2.Length} ID {Latin1(jbig2)} EI Q";
        using (var asInline = RenderedWith(inline))
        using (var asXObject = RenderedWith("q 100 0 0 100 50 50 cm /Im0 Do Q",
                   "<< /Type /XObject /Subtype /Image /Width 16 /Height 16 /BitsPerComponent 1 /ColorSpace /DeviceGray /Filter /JBIG2Decode " +
                   $"/Length {jbig2.Length} >>\nstream\n{Latin1(jbig2)}\nendstream"))
        {
            Assert.True(asXObject.GetPixel(75, 100).Red < 60);   // the black half
            Assert.True(White(asXObject, 125, 100));
            for (var x = 55; x < 150; x += 10) Assert.Equal(asXObject.GetPixel(x, 100), asInline.GetPixel(x, 100));
        }

        // the writer: a 1-bit inline image of 64 × 64 stays without JBIG2 when the page is packed with JBIG2
        var raw = Latin1([.. Enumerable.Range(0, 8 * 64).Select(i => (byte)(i % 8 < 4 ? 0x00 : 0xFF))]);
        var content = $"q 100 0 0 100 50 50 cm BI /W 64 /H 64 /BPC 1 /CS /G /L {raw.Length} ID {raw} EI Q";
        using var source = Core.Pdf.Document.PdfDocument.Open(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>", $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"));
        using var output = new MemoryStream();
        Core.Pdf.Writing.PdfWriter.Write(source, Core.Pdf.Editing.PageItem.From(source), output, new Core.Pdf.Writing.PdfSaveOptions
        {
            Compression = Core.Pdf.Writing.CompressionSettings.For(Core.Pdf.Writing.CompressionPreset.Lossless), ImageCodec = Rendering.SkiaImageCodec.Instance,
        });
        using var packed = Core.Pdf.Document.PdfDocument.Open(output.ToArray());
        var written = Latin1(packed.GetStreamData((PdfStream)packed.Pages[0].Dictionary.Get(PdfName.Contents)!));
        Assert.Contains("BI", written);
        Assert.DoesNotContain("JBIG2", written);
    }

    /// <summary>
    /// 7.4.9 with 8.6.6.3: a JPEG 2000 image in an /Indexed space is held to the limit of 256 colours — a /hival above 255 counts as 255 and the
    /// raw samples of the data are the indices. The data holds the value 128 in every sample; entry 128 of the palette is blue (M2 row 47).
    /// </summary>
    [Fact]
    public void Jpx_Indexed_AtMost256Colours_SamplesAreIndices()
    {
        var codestream = JpxRobustTests.Codestream(0, 4, 4, 1, 1, 0, 1, 0);   // 4 × 1, one 8-bit channel, every sample 128
        var palette = new byte[256 * 3];
        for (var i = 0; i < 256; i++) palette[3 * i] = 255;   // red …
        palette[3 * 128] = 0;
        palette[3 * 128 + 2] = 255;                          // … except entry 128
        var hex = Convert.ToHexString(palette);
        using var bitmap = RenderedWith("q 100 0 0 100 50 50 cm /Im0 Do Q",
            $"<< /Type /XObject /Subtype /Image /Width 4 /Height 1 /ColorSpace [/Indexed /DeviceRGB 300 <{hex}>] /Filter /JPXDecode " +
            $"/Length {codestream.Length} >>\nstream\n{Latin1(codestream)}\nendstream");
        Assert.True(Blue(bitmap, 100, 100));
    }

    /// <summary>
    /// 7.4.9 with 8.9.5.2: with a /ColorSpace /Decode counts for JPEG 2000 too — for /Indexed it maps the raw indices with the bits of the
    /// JPEG 2000 component (Table 90: [0 2^n − 1] by default). The data holds 128 in every sample; /Decode [255 0] makes it index 127, green
    /// in the palette (128 is blue). Before /Decode was passed over and 128 drawn (S-A A78; docs/FEHLERGEDAECHTNIS.md, No. 945). The blacking
    /// out reads the same indices.
    /// </summary>
    [Fact]
    public void Jpx_Indexed_DecodeMapsTheIndices()
    {
        var codestream = JpxRobustTests.Codestream(0, 4, 4, 1, 1, 0, 1, 0);   // 4 × 1, one 8-bit channel, every sample 128
        var palette = new byte[256 * 3];
        for (var i = 0; i < 256; i++) palette[3 * i] = 255;
        (palette[3 * 127], palette[3 * 127 + 1]) = (0, 255);
        (palette[3 * 128], palette[3 * 128 + 2]) = (0, 255);
        var image = $"<< /Type /XObject /Subtype /Image /Width 4 /Height 1 /ColorSpace [/Indexed /DeviceRGB 255 <{Convert.ToHexString(palette)}>] " +
                    $"/Decode [255 0] /Filter /JPXDecode /Length {codestream.Length} >>\nstream\n{Latin1(codestream)}\nendstream";
        using (var bitmap = RenderedWith("q 100 0 0 100 50 50 cm /Im0 Do Q", image))
            Assert.True(bitmap.GetPixel(100, 100) is { Green: > 200, Red: < 60, Blue: < 60 });
        using var doc = Core.Pdf.Document.PdfDocument.Open(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream", image));
        var stream = Assert.IsType<PdfStream>(doc.GetObject(5));
        var blanker = Core.Pdf.Editing.ImageBlanker.DecodeJpx(stream, doc.GetStreamData(stream), 1);
        Assert.NotNull(blanker);
        Assert.All(blanker.Value.Image.Samples, s => Assert.Equal(127, s));
    }

    /// <summary>
    /// 7.4.9: a JPEG 2000 stencil mask (/ImageMask true) has one channel of 1-bit samples; a set bit (1) does not paint, unless /Decode [1 0]
    /// turns it round. The data holds 1 in every sample (M2 row 51).
    /// </summary>
    [Theory]
    [InlineData("", false)]
    [InlineData("/Decode [1 0] ", true)]
    public void Jpx_StencilMask_OneBitChannel(string decode, bool paints)
    {
        var codestream = JpxRobustTests.Codestream(0, 4, 4, 1, 1, 0, 1, 0);
        codestream[42] = 0x00;   // Ssiz of the single component: 1 bit
        using var bitmap = RenderedWith("0 0 1 rg q 100 0 0 100 50 50 cm /Im0 Do Q",
            $"<< /Type /XObject /Subtype /Image /Width 4 /Height 1 /ImageMask true {decode}/Filter /JPXDecode " +
            $"/Length {codestream.Length} >>\nstream\n{Latin1(codestream)}\nendstream");
        Assert.Equal(paints, Blue(bitmap, 100, 100));
        if (!paints) Assert.True(White(bitmap, 100, 100));
    }

    /// <summary>
    /// 7.4.2, 7.4.3, 7.4.5: white space is skipped, any other character outside the filter's alphabet is an error — read past as before, but
    /// reported as damage (DecodeResult.Truncated), as Flate, LZW and CCITT already did; so a reader that must not trust damaged data (the
    /// cross-reference stream, FileStructureNormTests.XRefStream_Damaged_IsRebuiltFromTheFile) sees it (docs/FEHLERGEDAECHTNIS.md, No. 797).
    /// </summary>
    /// <summary>
    /// The repair of a damaged Flate stream on saving, by kind (decided on 19.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 963): written
    /// anew — with a valid header and sum, the same data — where the decoder read all there is: a wrong or missing sum (cut at the end), a
    /// window above 32 KB, a flush point without a sum; left as stored where it is valid (also with bytes behind its sum, Verzeichnis L, L52),
    /// where the decoder stops at damage with bytes behind it, and where the header gives no data at all (L53).
    /// </summary>
    [Fact]
    public void Flate_Repaired_OnlyWhereNothingIsLost()
    {
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 200).Select(i => $"Zeile {i} ")));
        var whole = Core.Pdf.Filters.Flate.Encode(data);
        var raw = whole[2..^4];
        var sum = whole[^4..];
        static byte[] Header(int cmf, int flgHigh) => [(byte)cmf, (byte)(flgHigh + (31 - (cmf * 256 + flgHigh) % 31) % 31)];
        var flushing = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(flushing, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(data);
            z.Flush();
        }
        var flushed = flushing.ToArray();
        flushed = flushed[..(flushed.AsSpan().LastIndexOf((ReadOnlySpan<byte>)[0, 0, 0xFF, 0xFF]) + 4)];
        foreach (var (what, stream) in new (string, byte[])[]
                 {
                     ("falsche Prüfsumme", [.. whole[..^4], 0, 0, 0, 0]),
                     ("am Ende abgeschnitten", whole[..(whole.Length * 2 / 3)]),
                     ("Fenster über 32 KB", [.. Header(0x88, 0x80), .. raw, .. sum]),
                     ("Synchronisationsmarke ohne Prüfsumme", flushed),
                 })
        {
            var repaired = Assert.IsType<byte[]>(Core.Pdf.Filters.Flate.Repaired(stream));
            var truncated = false;
            Assert.Equal(Core.Pdf.Filters.Flate.Decode(stream, ref truncated), Core.Pdf.Filters.Flate.Decode(repaired, ref truncated));
            truncated = false;
            _ = Core.Pdf.Filters.Flate.Decode(repaired, ref truncated);
            Assert.False(truncated, what);
        }
        var middle = (byte[])whole.Clone();
        for (var i = 10; i < 30; i++) middle[i] = 0xFF;
        Assert.Null(Core.Pdf.Filters.Flate.Repaired(whole));                                        // valid
        Assert.Null(Core.Pdf.Filters.Flate.Repaired([.. whole, .. "VERSTECKT"u8]));                  // bytes behind the sum (L52)
        Assert.Null(Core.Pdf.Filters.Flate.Repaired(raw));                                          // no zlib header: no data (L53)
        Assert.Null(Core.Pdf.Filters.Flate.Repaired(middle));                                       // damage in the middle: bytes behind it
    }

    /// <summary>
    /// S-A A70, measured against the three reference readers (probe of M2, 19.11.2026, `lesetoleranzen_m2b.py`): in ASCII85 a character outside
    /// the alphabet ends the data — the whole groups before it stay — and a leading „&lt;~“ gives nothing; all three readers have it so.
    /// Before, both were read past and PDF-Knead showed text no reader showed (docs/FEHLERGEDAECHTNIS.md, No. 962). ASCIIHex stays as it
    /// was: one reader reads past foreign characters, the other two stop — PDF-Knead reads like the one that shows.
    /// </summary>
    [Fact]
    public void Ascii85_ForeignCharacterEndsTheData_AsTheReadersHaveIt()
    {
        static (string Text, bool Damaged) Run(string filter, string data)
        {
            var result = StreamFilters.Decode(Encoding.ASCII.GetBytes(data), Dict(("Filter", PdfName.Get(filter))));
            return (Encoding.Latin1.GetString(result.Data), result.Truncated);
        }
        var encoded = Encoding.ASCII.GetString(Ascii85Encode("Erste Zeile Zweite Zeile"u8.ToArray()));
        // after the first two groups (eight bytes) a foreign character: the eight bytes stay, the rest does not
        Assert.Equal(("Erste Ze", true), Run("ASCII85Decode", encoded[..10] + "{" + encoded[10..]));
        Assert.Equal(("", true), Run("ASCII85Decode", "<~" + encoded));
        Assert.Equal(("Erste Zeile Zweite Zeile", true), Run("ASCIIHexDecode",
            Convert.ToHexString("Erste Zeile "u8) + "zq" + Convert.ToHexString("Zweite Zeile"u8) + ">"));
    }

    [Fact]
    public void AsciiAndRunLength_DamageIsReported()
    {
        static (string Text, bool Damaged) Run(string filter, byte[] data)
        {
            var result = StreamFilters.Decode(data, Dict(("Filter", PdfName.Get(filter))));
            return (Encoding.Latin1.GetString(result.Data), result.Truncated);
        }
        var max = Encoding.ASCII.GetString(Ascii85Encode("Max Mustermann"u8.ToArray()));
        Assert.Equal(("Max Mustermann", false), Run("ASCII85Decode", Encoding.ASCII.GetBytes(max)));
        Assert.Equal(("Max Mustermann", false), Run("ASCII85Decode", Encoding.ASCII.GetBytes(string.Join("\n ", max.Chunk(3).Select(c => new string(c))))));
        Assert.True(Run("ASCII85Decode", Encoding.ASCII.GetBytes("x" + max[1..])).Damaged);          // a character above „u“ in place of one
        Assert.True(Run("ASCII85Decode", Encoding.ASCII.GetBytes(max[..2] + "z" + max[2..])).Damaged);  // „z“ inside a group
        Assert.True(Run("ASCII85Decode", "!!!!!!~>"u8.ToArray()).Damaged);                              // a last group of one character
        Assert.True(Run("ASCII85Decode", "uuuuu~>"u8.ToArray()).Damaged);                               // above 2^32 - 1
        Assert.Equal(("\0\0\0\0", false), Run("ASCII85Decode", "z~>"u8.ToArray()));
        // the end „~>“, white space between allowed; a „~“ without „>“ is damage, the data before it still decoded (No. 899)
        Assert.Equal(("Max Mustermann", false), Run("ASCII85Decode", Encoding.ASCII.GetBytes(max.Replace("~>", "~ \n>"))));
        Assert.Equal(("Max Mustermann", true), Run("ASCII85Decode", Encoding.ASCII.GetBytes(max.Replace("~>", "~x"))));
        Assert.Equal(("Max Mustermann", true), Run("ASCII85Decode", Encoding.ASCII.GetBytes(max.Replace("~>", "~"))));

        Assert.Equal(("Max", false), Run("ASCIIHexDecode", "4D 61\n78>"u8.ToArray()));
        Assert.Equal(("Map", false), Run("ASCIIHexDecode", "4d617>"u8.ToArray()));                     // an odd last digit counts as followed by 0
        Assert.True(Run("ASCIIHexDecode", "4D6G78>"u8.ToArray()).Damaged);

        Assert.Equal(("Max", false), Run("RunLengthDecode", [2, (byte)'M', (byte)'a', (byte)'x', 128]));
        Assert.Equal(("xxx", false), Run("RunLengthDecode", [254, (byte)'x']));                       // the end without 128 is no damage
        Assert.True(Run("RunLengthDecode", [5, (byte)'M', (byte)'a']).Damaged);                         // a literal run beyond the data
        Assert.True(Run("RunLengthDecode", [2, (byte)'M', (byte)'a', (byte)'x', 0xFE]).Damaged);        // a repeat without its byte
    }
}
