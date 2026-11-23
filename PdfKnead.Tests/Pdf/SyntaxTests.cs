using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.IO;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Parsing;
using PdfKnead.Core.Pdf.Security;

namespace PdfKnead.Tests.Pdf;

public sealed class LexerTests
{
    private static List<PdfToken> Tokens(string text)
    {
        var lexer = new PdfLexer(PdfSource.FromBytes(Encoding.Latin1.GetBytes(text)));
        var list = new List<PdfToken>();
        for (var t = lexer.Next(); t.Kind != PdfTokenKind.EndOfFile; t = lexer.Next()) list.Add(t);
        return list;
    }

    [Theory]
    [InlineData("123", 123L)]
    [InlineData("-17", -17L)]
    [InlineData("+5", 5L)]
    [InlineData("--5", 5L)]     // double sign: as in common readers
    [InlineData("-", 0L)]
    public void Integers(string text, long expected)
    {
        var t = Assert.Single(Tokens(text));
        Assert.Equal(PdfTokenKind.Integer, t.Kind);
        Assert.Equal(expected, t.Integer);
    }

    [Theory]
    [InlineData("3.25", 3.25)]
    [InlineData("-.5", -0.5)]
    [InlineData("4.", 4.0)]
    [InlineData("1.2.3", 1.2)]  // a second point ends the number
    [InlineData("99999999999999999999999", 1e23)]
    [InlineData("99999999999999999999.5.3", 1e20)]   // beyond long with a second point: was 0.5 (the whole text did not parse)
    [InlineData("-99999999999999999999.25", -1e20)]
    public void Reals(string text, double expected)
    {
        var t = Assert.Single(Tokens(text));
        Assert.Equal(PdfTokenKind.Real, t.Kind);
        Assert.Equal(expected, t.Real, 6);
    }

    [Fact]
    public void Strings_WithEscapesAndNesting()
    {
        var t = Assert.Single(Tokens(@"(a\(b\)c (nested) \101\n\\ end\
x)"));
        Assert.Equal("a(b)c (nested) A\n\\ endx", Encoding.Latin1.GetString(t.Bytes!));
    }

    [Fact]
    public void HexStrings_SkipWhitespace_PadOddDigit()
    {
        Assert.Equal([0x48, 0x65, 0x70], Assert.Single(Tokens("<48 65 7>")).Bytes);
    }

    [Fact]
    public void Names_DecodeHash()
    {
        var t = Assert.Single(Tokens("/A#20B#2"));
        Assert.Equal("A B#2", t.Name!.Value);
    }

    [Fact]
    public void Comments_AndStrayParentheses_AreSkipped()
    {
        var tokens = Tokens("% Kommentar\n1 )))) 2 % noch einer\r3");
        Assert.Equal([1L, 2L, 3L], tokens.Select(t => t.Integer));
    }

    [Fact]
    public void ManyStrayParentheses_DoNotOverflowTheStack()
    {
        var tokens = Tokens(new string(')', 200_000) + " 7");
        Assert.Equal(7, Assert.Single(tokens).Integer);
    }

    [Fact]
    public void Delimiters()
    {
        var kinds = Tokens("<< /K [1 2] >> true R").Select(t => t.Kind);
        Assert.Equal([PdfTokenKind.DictionaryStart, PdfTokenKind.Name, PdfTokenKind.ArrayStart, PdfTokenKind.Integer,
            PdfTokenKind.Integer, PdfTokenKind.ArrayEnd, PdfTokenKind.DictionaryEnd, PdfTokenKind.Keyword, PdfTokenKind.Keyword], kinds);
    }
}

public sealed class ParserTests
{
    private static PdfObject Parse(string text) =>
        new PdfParser(new PdfLexer(PdfSource.FromBytes(Encoding.Latin1.GetBytes(text)))).ParseObject();

    [Fact]
    public void Dictionary_WithReferencesAndNesting()
    {
        var d = Assert.IsType<PdfDictionary>(Parse("<< /Type /Page /Kids [1 0 R 2 0 R] /N 5 /Sub << /A (x) >> /Null null >>"));
        Assert.True(d.IsType(PdfName.Page));
        var kids = d.GetArray(PdfName.Kids)!;
        Assert.Equal(new PdfObjectId(2, 0), Assert.IsType<PdfReference>(kids[1]).Id);
        Assert.Equal(5, d.GetInteger(PdfName.N));
        Assert.Equal("x", d.GetDictionary(PdfName.Get("Sub"))!.GetString(PdfName.Get("A"))!.ToText());
        Assert.False(d.ContainsKey(PdfName.Get("Null")));   // null = key missing
    }

    [Fact]
    public void Array_OfNumbersIsNotMistakenForReferences()
    {
        var a = Assert.IsType<PdfArray>(Parse("[0 0 612 792]"));
        Assert.All(a, item => Assert.IsType<PdfInteger>(item));
    }

    [Fact]
    public void MissingDictionaryEnd_StopsAtEndobj()
    {
        var parser = new PdfParser(new PdfLexer(PdfSource.FromBytes("1 0 obj << /A 1 /B 2 endobj"u8.ToArray())));
        var (id, value) = parser.ParseIndirectObject(0)!.Value;
        Assert.Equal(1, id.Number);
        Assert.Equal(2, Assert.IsType<PdfDictionary>(value).GetInteger(PdfName.Get("B")));
    }

    [Fact]
    public void DeepNesting_IsRejected()
    {
        Assert.Throws<PdfLimitException>(() => Parse(new string('[', 10_000)));
    }

    [Fact]
    public void Stream_WithWrongLength_FindsEndstream()
    {
        var bytes = "5 0 obj << /Length 999 >> stream\r\nHALLO WELT\r\nendstream endobj"u8.ToArray();
        var (_, value) = new PdfParser(new PdfLexer(PdfSource.FromBytes(bytes))).ParseIndirectObject(0)!.Value;
        var s = Assert.IsType<PdfStream>(value);
        Assert.Equal("HALLO WELT", Encoding.ASCII.GetString(bytes, (int)s.DataOffset, (int)s.DataLength));
    }
}

public sealed class FilterTests
{
    private static byte[] Decode(byte[] data, string filter, PdfDictionary? parms = null)
    {
        var d = new PdfDictionary();
        d.Set(PdfName.Filter, PdfName.Get(filter));
        if (parms is not null) d.Set(PdfName.DecodeParms, parms);
        return StreamFilters.Decode(data, d).Data;
    }

    [Fact]
    public void Flate_RoundTrip()
    {
        var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("PDF-Knead ", 1000)));
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(text);
        Assert.Equal(text, Decode(ms.ToArray(), "FlateDecode"));
    }

    [Fact]
    public void Flate_Truncated_GivesWhatItCan()
    {
        var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 5000).Select(i => i.ToString())));
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true)) z.Write(text);
        var cut = ms.ToArray()[..(int)(ms.Length / 2)];
        var d = new PdfDictionary();
        d.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        // .NET does not report truncated Deflate data as an error; what matters is that the beginning arrives
        var result = StreamFilters.Decode(cut, d);
        Assert.True(result.Data.Length > 0);
        Assert.Equal(text[..result.Data.Length], result.Data);
    }

    [Fact]
    public void Lzw_ExampleFromTheStandard()
    {
        // ISO 32000-2, 7.4.4.2: "-----A---B"
        byte[] encoded = [0x80, 0x0B, 0x60, 0x50, 0x22, 0x0C, 0x0C, 0x85, 0x01];
        Assert.Equal("-----A---B", Encoding.ASCII.GetString(Decode(encoded, "LZWDecode")));
    }

    [Theory]
    [InlineData("9jqo^", "Man ")]
    [InlineData("F*2M7/c~>", "sure.")]
    [InlineData("z~>", "\0\0\0\0")]
    public void Ascii85(string encoded, string expected) =>
        Assert.Equal(expected, Encoding.Latin1.GetString(Decode(Encoding.ASCII.GetBytes(encoded), "ASCII85Decode")));

    [Fact]
    public void AsciiHex() => Assert.Equal("Hallo", Encoding.ASCII.GetString(Decode("48 61 6C 6c 6F>"u8.ToArray(), "AHx")));

    [Fact]
    public void RunLength() => Assert.Equal("abcxxx", Encoding.ASCII.GetString(Decode([2, (byte)'a', (byte)'b', (byte)'c', 254, (byte)'x', 128], "RunLengthDecode")));

    [Fact]
    public void PngPredictor_Up()
    {
        // Two rows of 3 bytes each; row 2 with the filter "Up"
        byte[] raw = [0, 1, 2, 3, 2, 1, 1, 1];
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(raw);
        var parms = new PdfDictionary();
        parms.Set(PdfName.Get("Predictor"), PdfInteger.Of(12));
        parms.Set(PdfName.Get("Columns"), PdfInteger.Of(3));
        Assert.Equal([1, 2, 3, 2, 3, 4], Decode(ms.ToArray(), "FlateDecode", parms));
    }

    [Fact]
    public void UnknownFilter_IsReported() =>
        Assert.Throws<PdfException>(() => Decode([1, 2, 3], "GibtEsNicht"));

    [Fact]
    public void ImageFilters_EndTheChain()
    {
        var d = new PdfDictionary();
        d.Set(PdfName.Filter, new PdfArray([PdfName.Get("AHx"), PdfName.Get("DCTDecode")]));
        var result = StreamFilters.Decode("FFD8>"u8.ToArray(), d);
        Assert.Equal("DCTDecode", result.ImageFilter!.Value);
        Assert.Equal([0xFF, 0xD8], result.Data);
    }
}

[Collection(nameof(LimitCollection))]
public sealed class LimitTests
{
    /// <summary>
    /// Memory budgets (decided on 11.11.2026: dynamic with a fixed ceiling; docs/FEHLERGEDAECHTNIS.md, No. 743): a quarter of what the
    /// runtime gives the program, never above the ceiling of the purpose and never below its floor — an 8 GB machine gets 2 GB for one large
    /// piece of work, a 32 GB one the ceiling. The stream limit follows it between 256 MB and 1 GB.
    /// </summary>
    [Fact]
    public void Budget_QuarterOfTheMachine_BetweenFloorAndCeiling()
    {
        const long gb = 1L << 30, mb = 1L << 20;
        Assert.Equal(4 * gb, PdfLimits.Budget(4 * gb, 64 * mb, 32 * gb));
        Assert.Equal(2 * gb, PdfLimits.Budget(4 * gb, 64 * mb, 8 * gb));
        Assert.Equal(128 * mb, PdfLimits.Budget(4 * gb, 64 * mb, 512 * mb));
        Assert.Equal(64 * mb, PdfLimits.Budget(4 * gb, 64 * mb, 100 * mb));
        Assert.Equal(32 * mb, PdfLimits.Budget(32 * mb, 64 * mb, 100 * mb));   // a floor above the ceiling: the ceiling
        Assert.Equal(1 * gb, PdfLimits.Budget(1 * gb, 256 * mb, 8 * gb));       // streams on an 8 GB machine
        Assert.Equal(256 * mb, PdfLimits.Budget(1 * gb, 256 * mb, 512 * mb));   // and on a very small one
        // this machine: within the bounds of the stream limit
        Assert.InRange(PdfLimits.MaxDecodedStreamBytes, 256 * mb, 1 * gb);
    }

    [Fact]
    public void DecompressionBomb_IsStopped()
    {
        PdfLimits.MaxDecodedStreamBytes = 1 << 20;
        try
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(new byte[8 << 20]);
            var d = new PdfDictionary();
            d.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
            Assert.Throws<PdfLimitException>(() => StreamFilters.Decode(ms.ToArray(), d));
        }
        finally
        {
            PdfLimits.DynamicStreamLimit();
        }
    }

    /// <summary>
    /// A chain of cross-reference streams (/Prev), each a small decompression bomb under the limit per stream: together they decode no more
    /// than one stream may (or 64 times the file); beyond that the table is not believed and the file is rebuilt (docs/FEHLERGEDAECHTNIS.md,
    /// No. 749). Streams whose data overlap could otherwise have one bomb decoded once per section.
    /// </summary>
    [Fact]
    public void XRefStreamChain_DecodedTogether_Limited()
    {
        PdfLimits.MaxDecodedStreamBytes = 4 << 20;
        try
        {
            var output = new MemoryStream();
            void Text(string s) => output.Write(Encoding.Latin1.GetBytes(s));
            Text("%PDF-1.7\n");
            var offsets = new List<long>();
            foreach (var body in new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                         "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>" })
            {
                offsets.Add(output.Position);
                Text($"{offsets.Count} 0 obj\n{body}\nendobj\n");
            }
            long previous = -1;
            for (var section = 0; section < 5; section++)
            {
                // four real entries (0 to 3), then 2 MB of zeros the /Index does not reach
                var data = new MemoryStream();
                using (var z = new ZLibStream(data, CompressionLevel.Optimal, leaveOpen: true))
                {
                    z.Write([0, 0, 0, 0, 0, 0xFF]);
                    foreach (var o in offsets) z.Write([1, (byte)(o >> 24), (byte)(o >> 16), (byte)(o >> 8), (byte)o, 0]);
                    z.Write(new byte[2 << 20]);
                }
                var at = output.Position;
                Text($"{10 + section} 0 obj\n<< /Type /XRef /Size {11 + section} /W [1 4 1] /Index [0 4] /Root 1 0 R /Filter /FlateDecode " +
                     $"/Length {data.Length}{(previous < 0 ? "" : $" /Prev {previous}")} >>\nstream\n");
                output.Write(data.ToArray());
                Text("\nendstream\nendobj\n");
                previous = at;
            }
            Text($"startxref\n{previous}\n%%EOF\n");
            var file = output.ToArray();
            using (var source = PdfSource.FromBytes(file))
                Assert.Throws<PdfLimitException>(() => XRefReader.Read(source, 0));   // 10 MB from about 10 KB
            using var doc = PdfKnead.Core.Pdf.Document.PdfDocument.Open(file);
            Assert.Equal(1, doc.PageCount);
        }
        finally
        {
            PdfLimits.DynamicStreamLimit();
        }
    }

    /// <summary>A page whose /Contents names <paramref name="mentions"/> times one Flate stream of <paramref name="size"/> NUL bytes (white space).</summary>
    private static byte[] PageNaming(int mentions, int size, bool asForm = false)
    {
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, true)) z.Write(new byte[size]);
        var data = packed.ToArray();
        var output = new MemoryStream();
        void Text(string t) => output.Write(Encoding.Latin1.GetBytes(t));
        Text("%PDF-1.7\n");
        var offsets = new List<long>();
        var refs = string.Join(" ", Enumerable.Repeat("4 0 R", mentions));
        var names = string.Join(" ", Enumerable.Range(0, mentions).Select(i => $"/F{i} 4 0 R"));
        var draw = string.Concat(Enumerable.Range(0, mentions).Select(i => $"/F{i} Do\n"));
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            asForm ? $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << {names} >> >> /Contents 5 0 R >>"
                   : $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents [{refs}] >>",
        };
        foreach (var body in bodies)
        {
            offsets.Add(output.Position);
            Text($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        offsets.Add(output.Position);
        Text($"4 0 obj\n<< {(asForm ? "/Type /XObject /Subtype /Form /BBox [0 0 1 1] " : "")}/Filter /FlateDecode /Length {data.Length} >>\nstream\n");
        output.Write(data);
        Text("\nendstream\nendobj\n");
        offsets.Add(output.Position);
        Text($"5 0 obj\n<< /Length {draw.Length} >>\nstream\n{draw}\nendstream\nendobj\n");
        var xref = output.Position;
        Text($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Text($"{o:D10} 00000 n \n");
        Text($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    /// <summary>
    /// /Contents naming one small decompression bomb 64 times (docs/FEHLERGEDAECHTNIS.md, No. 1002): all parts together decode to no more
    /// than one stream may — the page content is refused after the parts that reach the limit, before the rest is decoded. Before, every
    /// mention was decoded and all were held at once (24 GB and minutes from a file of 256 KB); the limit here is 1 MB, each part 512 KB.
    /// </summary>
    [Fact]
    public void ContentsArray_OneBombNamedAgain_StoppedAtTheLimitOfOneStream()
    {
        PdfLimits.MaxDecodedStreamBytes = 1 << 20;
        try
        {
            using var doc = PdfKnead.Core.Pdf.Document.PdfDocument.Open(PageNaming(64, 512 << 10));
            var before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Throws<PdfLimitException>(() => PdfKnead.Core.Pdf.Content.PageInterpreter.PageContent(doc, doc.Pages[0].Dictionary).Dispose());
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 << 20, "no more than a few parts decoded");
        }
        finally
        {
            PdfLimits.DynamicStreamLimit();
        }
    }

    /// <summary>
    /// One large form of nothing but white space drawn 200 times (docs/FEHLERGEDAECHTNIS.md, No. 1002): the bytes read count towards the work
    /// limit of a page, 64 to an operator — the display stops with its notice that the page is shown only in part, redaction refuses the
    /// page. Before, there was no operator to count: 64 uses of a form of 256 MB took six minutes over drawing, redacting and saving.
    /// </summary>
    [Fact]
    public void Form_OfWhiteSpaceDrawnAgainAndAgain_WithinTheWorkLimit()
    {
        using var doc = PdfKnead.Core.Pdf.Document.PdfDocument.Open(PageNaming(200, 8 << 20, asForm: true));   // 1.6 GB to read in all
        var watch = System.Diagnostics.Stopwatch.StartNew();
        PdfKnead.Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]);
        Assert.Contains(doc.Problems, p => p.Contains("zu aufwendig", StringComparison.Ordinal));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"{watch.Elapsed.TotalSeconds:0.0} s");
    }
}

[CollectionDefinition(nameof(LimitCollection), DisableParallelization = true)]
public sealed class LimitCollection;

public sealed class TextAndDateTests
{
    [Fact]
    public void Rc4_KnownVector() =>
        Assert.Equal("BBF316E8D940AF0AD3", Convert.ToHexString(Rc4.Apply("Key"u8, "Plaintext"u8)));

    [Theory]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x4D, 0x00, 0xFC }, "Mü")]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x4D, 0xC3, 0xBC }, "Mü")]
    [InlineData(new byte[] { 0x80, 0x20, 0x41, 0xA0 }, "• A€")]   // PDFDocEncoding
    public void TextStrings(byte[] bytes, string expected) => Assert.Equal(expected, PdfTextEncoding.Decode(bytes));

    [Theory]
    [InlineData("Größe 10 €")]
    [InlineData("日本語")]
    public void TextStrings_RoundTrip(string text) => Assert.Equal(text, PdfTextEncoding.Decode(PdfTextEncoding.Encode(text)));

    [Fact]
    public void Dates()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 14, 32, 5, TimeSpan.FromHours(2)),
            Core.Pdf.Document.PdfDate.Parse("D:20261015143205+02'00'"));
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), Core.Pdf.Document.PdfDate.Parse("D:2024"));
        Assert.Null(Core.Pdf.Document.PdfDate.Parse("gestern"));
        // year 0000 is a placeholder, not a date (docs/FEHLERGEDAECHTNIS.md, No. 508)
        Assert.Null(Core.Pdf.Document.PdfDate.Parse("D:00000000000000+00'00'"));
        // Time zone above 14 hours (found by fuzzing): clamped to 14 hours instead of an exception
        Assert.Equal(TimeSpan.FromHours(14), Core.Pdf.Document.PdfDate.Parse("D:20260101000000+14'59'")?.Offset);
        // the moment in UTC before year 1 or after 9999: no date, without an exception on the way (docs/FEHLERGEDAECHTNIS.md, No. 394)
        var (thrown, me) = (0, Environment.CurrentManagedThreadId);
        void Count(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == me) thrown++;   // tests of other classes run alongside
        }
        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            Assert.Null(Core.Pdf.Document.PdfDate.Parse("D:00010101000000+02'00'"));
            Assert.Null(Core.Pdf.Document.PdfDate.Parse("D:99991231235959-02'00'"));
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
        }
        Assert.Equal(0, thrown);
    }
}
