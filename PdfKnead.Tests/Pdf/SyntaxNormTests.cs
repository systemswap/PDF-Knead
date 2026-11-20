using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.IO;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Parsing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Conformity check, module M1: ISO 32000-2, 7.2 (lexical conventions) and 7.3 (objects), one test per requirement or group of requirements
/// (docs/normabgleich/M1-syntax.md names the row each test proves). Reading and writing; tolerances of other readers are documented in
/// docs/normabgleich/sonderregeln.md (A-numbers).
/// </summary>
public sealed partial class SyntaxNormTests
{
    private static List<PdfToken> Tokens(byte[] bytes)
    {
        var lexer = new PdfLexer(PdfSource.FromBytes(bytes));
        var list = new List<PdfToken>();
        for (var t = lexer.Next(); t.Kind != PdfTokenKind.EndOfFile; t = lexer.Next()) list.Add(t);
        return list;
    }

    private static List<PdfToken> Tokens(string text) => Tokens(Encoding.Latin1.GetBytes(text));

    private static PdfObject Parse(string text) => new PdfParser(new PdfLexer(PdfSource.FromBytes(Encoding.Latin1.GetBytes(text)))).ParseObject();

    private static byte[] Written(PdfObject obj) => PdfSerializer.ToBytes(obj);

    // ------------------------------------------------------------ 7.2.3 character set

    /// <summary>7.2.3, Table 1: the six white-space characters each separate tokens.</summary>
    [Fact]
    public void WhiteSpace_AllSix_SeparateTokens()
    {
        var tokens = Tokens("1\u00002\t3\n4\f5\r6 7");
        Assert.Equal([1L, 2, 3, 4, 5, 6, 7], tokens.Select(t => t.Integer));
        Assert.All(tokens, t => Assert.Equal(PdfTokenKind.Integer, t.Kind));
    }

    /// <summary>7.2.3: CR, LF and CR LF are end-of-line markers; CR LF counts as one (seen in a literal string, where each EOL becomes one LF).</summary>
    [Fact]
    public void EndOfLine_CrLfAndCrLf_EachOneMarker()
    {
        var s = (PdfString)Parse("(a\rb\nc\r\nd)");
        Assert.Equal("a\nb\nc\nd"u8.ToArray(), s.Bytes);
    }

    /// <summary>7.2.3, Table 2: the delimiters end the token before them and are not part of it.</summary>
    [Fact]
    public void Delimiters_EndThePrecedingToken()
    {
        var tokens = Tokens("abc(x)def<41>ghi[1]jkl/Name%c\nmno<<>>");
        Assert.Equal(["abc", "x", "def", "A", "ghi", "", "1", "", "jkl", "Name", "mno", "", ""],
            tokens.Select(t => t.Kind == PdfTokenKind.Integer ? t.Integer.ToString() : t.Text));
    }

    /// <summary>
    /// 7.2.3: curly braces are delimiters only in PostScript calculator functions, elsewhere regular characters. PDF-Knead reads them as
    /// delimiters everywhere, as widely used readers do (sonderregeln.md, A64), and writes them in names only as #7B and #7D — so every
    /// reader reads the same name.
    /// </summary>
    [Fact]
    public void CurlyBraces_ReadAsDelimiters_WrittenEscapedInNames()
    {
        Assert.Equal(["a", "{", "b"], Tokens("/a{b").Select(t => t.Text));
        Assert.Equal("/a#7Bb#7D", Encoding.ASCII.GetString(Written(PdfName.Get("a{b}"))));
    }

    /// <summary>7.2.3: syntax is case-sensitive — „True“ is not the boolean.</summary>
    [Fact]
    public void Keywords_CaseSensitive()
    {
        Assert.Same(PdfBoolean.True, Parse("true"));
        Assert.Same(PdfNull.Instance, Parse("True"));
        Assert.Same(PdfNull.Instance, Parse("NULL"));
    }

    /// <summary>7.2.3: bytes outside ASCII are regular characters (part of names and keywords).</summary>
    [Fact]
    public void BytesAboveAscii_AreRegularCharacters()
    {
        var tokens = Tokens("/aäb xÿy");
        Assert.Equal(PdfTokenKind.Name, tokens[0].Kind);
        Assert.Equal("aäb", tokens[0].Text);
        Assert.Equal("xÿy", tokens[1].Text);
        Assert.Equal(2, tokens.Count);
    }

    // ------------------------------------------------------------ 7.2.4 comments

    /// <summary>7.2.4: a comment runs to the end of the line, counts as one white-space character and is ignored — also in a content stream.</summary>
    [Fact]
    public void Comment_IsWhiteSpace_ToTheEndOfTheLine()
    {
        Assert.Equal(["first", "second"], Tokens("first%(not a string) /NotAName\rsecond").Select(t => t.Text));
        Assert.Equal(["x", "y"], Tokens("x%c\ny").Select(t => t.Text));
        Assert.Equal(["1", "2"], Tokens("1%\r\n2").Select(t => t.Integer.ToString()));
    }

    // ------------------------------------------------------------ 7.3.3 numbers

    /// <summary>
    /// 7.3.3 sentence 11 (M1 row 18): where a real number is expected, an integer may stand — read as the same number by the getters every
    /// reader of PDF-Knead uses (a page box of integers, a matrix of both kinds).
    /// </summary>
    [Fact]
    public void IntegerWhereRealExpected_SameNumber()
    {
        var box = Assert.IsType<PdfArray>(Parse("[0 0 612 792.5]"));
        Assert.Equal(new double?[] { 0, 0, 612, 792.5 }, Enumerable.Range(0, 4).Select(i => box.GetNumber(i)).ToArray());
        var dict = Assert.IsType<PdfDictionary>(Parse("<< /W 3 /R 3.0 >>"));
        Assert.Equal(dict.GetNumber(PdfName.Get("R")), dict.GetNumber(PdfName.Get("W")));
        Assert.Equal(3d, Parse("3").AsNumber());
    }

    /// <summary>7.3.3: integers with optional sign; reals with a leading, trailing or embedded point.</summary>
    [Fact]
    public void Numbers_IntegerAndRealForms()
    {
        Assert.Equal([12L, -7, 0, 5], Tokens("12 -7 0 +5").Select(t => t.Integer));
        var reals = Tokens("3.5 -.25 4. +0.5");
        Assert.All(reals, t => Assert.Equal(PdfTokenKind.Real, t.Kind));
        Assert.Equal([3.5, -0.25, 4.0, 0.5], reals.Select(t => t.Real));
    }

    /// <summary>7.3.3: a writer uses neither exponents nor other radices — also for very small and very large values.</summary>
    [Theory]
    [InlineData(6.02e23)]
    [InlineData(1e-7)]
    [InlineData(-123456789012.5)]
    [InlineData(0.1)]
    public void Reals_WrittenWithoutExponent(double value)
    {
        var text = Encoding.ASCII.GetString(Written(new PdfReal(value)));
        Assert.Matches("^-?[0-9]*\\.?[0-9]*$", text);
        Assert.DoesNotContain('#', text);
        var back = Parse(text);
        Assert.Equal(value, back is PdfReal r ? r.Value : ((PdfInteger)back).Value, 1e-12 * Math.Max(1, Math.Abs(value)));
    }

    /// <summary>7.3.3: where a real is expected an integer may stand; a whole value written as real reads back as the same number.</summary>
    [Fact]
    public void WholeReal_WrittenAsInteger()
    {
        Assert.Equal("90", Encoding.ASCII.GetString(Written(new PdfReal(90))));
        Assert.Equal(90, ((PdfInteger)Parse("90")).Value);
    }

    // ------------------------------------------------------------ 7.3.4 strings

    /// <summary>7.3.4.2, Table 3: every escape sequence; an unknown one drops the backslash.</summary>
    [Fact]
    public void LiteralString_Escapes()
    {
        var s = (PdfString)Parse(@"(\n\r\t\b\f\(\)\\\101\q)");
        Assert.Equal(new byte[] { 10, 13, 9, 8, 12, (byte)'(', (byte)')', (byte)'\\', (byte)'A', (byte)'q' }, s.Bytes);
    }

    /// <summary>7.3.4.2: balanced parentheses need no escape; a backslash at the end of a line continues the string (CR, LF, CR LF).</summary>
    [Fact]
    public void LiteralString_BalancedParenthesesAndContinuation()
    {
        Assert.Equal("a(b)c"u8.ToArray(), ((PdfString)Parse("(a(b)c)")).Bytes);
        Assert.Equal("onetwothreefour"u8.ToArray(), ((PdfString)Parse("(one\\\ntwo\\\rthree\\\r\nfour)")).Bytes);
    }

    /// <summary>7.3.4.2: one to three octal digits; overflow of the high-order bits is ignored; three digits end the escape.</summary>
    [Fact]
    public void LiteralString_Octal()
    {
        Assert.Equal(new byte[] { 5, (byte)'3' }, ((PdfString)Parse(@"(\0053)")).Bytes);
        Assert.Equal(new byte[] { 0x2B }, ((PdfString)Parse(@"(\53)")).Bytes);
        Assert.Equal(new byte[] { 0xFF }, ((PdfString)Parse(@"(\777)")).Bytes);
        Assert.Equal(new byte[] { 7 }, ((PdfString)Parse(@"(\7)")).Bytes);
    }

    /// <summary>
    /// 7.3.4.2: the writer escapes unbalanced parentheses and the backslash, and writes CR and LF as \r and \n — a raw CR would be read back as
    /// LF. Every byte value survives writing and reading.
    /// </summary>
    [Fact]
    public void LiteralString_EveryByte_SurvivesWriting()
    {
        var bytes = Enumerable.Range(0, 256).Select(b => (byte)b).Concat("()\r\n\r)("u8.ToArray()).ToArray();
        var written = Written(new PdfString(bytes));
        Assert.DoesNotContain((byte)'\r', written);
        Assert.Equal(bytes, ((PdfString)Parse(Encoding.Latin1.GetString(written))).Bytes);
    }

    /// <summary>7.3.4.3: hexadecimal strings — white space ignored, upper and lower case, an odd final digit counts as followed by 0.</summary>
    [Fact]
    public void HexString_WhiteSpaceCaseAndOddDigit()
    {
        Assert.Equal(new byte[] { 0x90, 0x1F, 0xA0 }, ((PdfString)Parse("<90 1f\nA>")).Bytes);
        Assert.Equal(Array.Empty<byte>(), ((PdfString)Parse("<>")).Bytes);
    }

    // ------------------------------------------------------------ 7.3.5 names

    /// <summary>7.3.5: #xx stands for one byte; names are compared byte for byte after the escapes are expanded.</summary>
    [Fact]
    public void Name_HashEscapes_AndIdentity()
    {
        Assert.Same(PdfName.Get("Lime Green"), Parse("/Lime#20Green"));
        Assert.Same(PdfName.Get("AB"), Parse("/A#42"));
        Assert.NotSame(PdfName.Get("ab"), Parse("/AB"));
    }

    /// <summary>
    /// 7.3.5 sentence 43 (M1 row 34): the length of a name may be limited — PDF-Knead does not limit it: a name of 4 000 bytes reads and writes
    /// back whole.
    /// </summary>
    [Fact]
    public void Name_Long_NotLimited()
    {
        var name = new string('n', 4000);
        var read = Assert.IsType<PdfName>(Parse("/" + name));
        Assert.Equal(name, read.Value);
        Assert.Equal("/" + name, Encoding.ASCII.GetString(Written(read)));
    }

    /// <summary>7.3.5: the slash alone is the empty name; the writer keeps it apart from what follows.</summary>
    [Fact]
    public void Name_Empty_IsValid_AndStaysSeparate()
    {
        var array = (PdfArray)Parse("[/ /A]");
        Assert.Equal("", ((PdfName)array[0]).Value);
        Assert.Equal("A", ((PdfName)array[1]).Value);
        var written = Encoding.ASCII.GetString(Written(new PdfArray { PdfName.Get(""), PdfInteger.Of(7) }));
        Assert.Equal(2, ((PdfArray)Parse(written)).Count);
    }

    /// <summary>
    /// 7.3.5 a)–c) and the recommendation for characters outside ! to ~: the writer writes #, white space, delimiters and every byte outside
    /// 21h–7Eh as #xx; every byte value from 1 to 255 survives.
    /// </summary>
    [Fact]
    public void Name_Writer_EscapesWhatItMust()
    {
        var all = new string(Enumerable.Range(1, 255).Select(b => (char)b).ToArray());
        var written = Written(PdfName.Get(all));
        Assert.All(written.Skip(1), b => Assert.InRange(b, (byte)0x21, (byte)0x7E));
        Assert.Contains("#23", Encoding.ASCII.GetString(written));
        Assert.Contains("#20", Encoding.ASCII.GetString(written));
        Assert.Equal(all, ((PdfName)Parse(Encoding.ASCII.GetString(written))).Value);
    }

    // ------------------------------------------------------------ 7.3.6 / 7.3.7 arrays and dictionaries

    /// <summary>7.3.6: arrays may mix any objects, nest, and be empty.</summary>
    [Fact]
    public void Array_Heterogeneous_Nested_Empty()
    {
        var a = (PdfArray)Parse("[549 3.14 false (Ralph) /SomeName [] [[1]] <<>>]");
        Assert.Equal(8, a.Count);
        Assert.Empty((PdfArray)a[5]);
        Assert.Empty((PdfArray)Parse("[]"));
    }

    /// <summary>7.3.7 and 7.3.9: an entry whose value is null is treated as absent.</summary>
    [Fact]
    public void Dictionary_NullValue_IsAbsent()
    {
        var d = (PdfDictionary)Parse("<</A 1/B null/C 2>>");
        Assert.False(d.ContainsKey(PdfName.Get("B")));
        Assert.Equal(2, d.Count);
    }

    /// <summary>
    /// 7.3.7: keys are unique; for a file that repeats one, the last value counts, as in widely used readers (sonderregeln.md, A68) —
    /// one parser for display, text, redaction and cleanup. The order of the entries carries no meaning.
    /// </summary>
    [Fact]
    public void Dictionary_RepeatedKey_LastCounts_OrderIgnored()
    {
        var d = (PdfDictionary)Parse("<</K 1/K 2>>");
        Assert.Equal(2L, d.GetInteger(PdfName.Get("K")));
        Assert.Equal(1, d.Count);
        var a = (PdfDictionary)Parse("<</X 1/Y 2>>");
        var b = (PdfDictionary)Parse("<</Y 2/X 1>>");
        Assert.Equal(a.GetInteger(PdfName.Get("X")), b.GetInteger(PdfName.Get("X")));
        Assert.Equal(a.GetInteger(PdfName.Get("Y")), b.GetInteger(PdfName.Get("Y")));
        Assert.Equal(0, ((PdfDictionary)Parse("<<>>")).Count);
    }

    // ------------------------------------------------------------ 7.3.8 streams

    private static byte[] Saved(byte[] file)
    {
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions());
        return output.ToArray();
    }

    [GeneratedRegex(@"(\d+) (\d+) obj\b")]
    private static partial Regex ObjectHeader();

    /// <summary>
    /// 7.3.8.1, 7.3.8.2 and 7.3.10 for the writer: every stream is indirect, „stream“ is followed by LF (never CR alone), /Length is direct and
    /// counts exactly the data, an end of line stands before „endstream“, only white space between „endstream“ and „endobj“; a new file has
    /// generation 0 throughout.
    /// </summary>
    [Fact]
    public void Writer_StreamFramingAndGenerations()
    {
        var output = Saved(TestPdf.Simple(2).Build());
        var text = Encoding.Latin1.GetString(output);
        Assert.All(ObjectHeader().Matches(text), m => Assert.Equal("0", m.Groups[2].Value));
        Assert.DoesNotMatch(@"stream\r(?!\n)", text);
        Assert.DoesNotMatch(@"endstream[^e]*?[^\s]endobj", text.Replace("endstream\nendobj", ""));
        using var doc = PdfDocument.Open(output);
        var streams = 0;
        for (var n = 1; n <= doc.HighestObjectNumber; n++)
        {
            if (doc.GetObject(n) is not PdfStream s || s.DataOffset < 0) continue;
            streams++;
            Assert.IsType<PdfInteger>(s.Dictionary.GetRaw(PdfName.Length));
            Assert.Equal(s.Dictionary.GetInteger(PdfName.Length), s.DataLength);
            Assert.Equal("\nendstream", text.Substring((int)(s.DataOffset + s.DataLength), 10));
            Assert.Equal('\n', text[(int)s.DataOffset - 1]);
        }
        Assert.True(streams > 0);
    }

    /// <summary>7.3.8.1: „stream“ followed by CR LF or by LF; the end of line before „endstream“ is not part of the data; /Length may be indirect.</summary>
    [Fact]
    public void Reader_StreamEolAndIndirectLength()
    {
        var file = CraftedFileTests.Build(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/A 3 0 R/B 4 0 R>>",
            [2] = "<</Type/Pages/Kids[]/Count 0>>",
            [3] = "<</Length 5 0 R>>\nstream\r\nabcd\r\nendstream",
            [4] = "<</Length 3>>\nstream\nxyz\nendstream",
            [5] = "4",
        });
        using var doc = PdfDocument.Open(file);
        Assert.Equal("abcd"u8.ToArray(), doc.GetStreamData(doc.Catalog!.GetStream(PdfName.Get("A"))!));
        Assert.Equal("xyz"u8.ToArray(), doc.GetStreamData(doc.Catalog!.GetStream(PdfName.Get("B"))!));
    }

    // ------------------------------------------------------------ 7.3.9 / 7.3.10 null and indirect objects

    /// <summary>7.3.9 and 7.3.10: a reference to an object that does not exist is the null object, not an error.</summary>
    [Fact]
    public void Reference_ToMissingObject_IsNull()
    {
        var file = CraftedFileTests.Build(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/Missing 17 0 R>>",
            [2] = "<</Type/Pages/Kids[]/Count 0>>",
        });
        using var doc = PdfDocument.Open(file);
        Assert.Same(PdfNull.Instance, doc.Catalog!.Get(PdfName.Get("Missing")));
        Assert.Null(doc.Catalog!.GetDictionary(PdfName.Get("Missing")));
    }

    /// <summary>
    /// 7.3.10: an object that consists only of a reference stands for its target; chains are allowed (docs/FEHLERGEDAECHTNIS.md, No. 727 —
    /// the reference was handed out as such, so a dictionary or a page behind it counted as missing). A loop ends as null.
    /// </summary>
    [Fact]
    public void Reference_Chain_LeadsToTheTarget()
    {
        var file = CraftedFileTests.Build(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/MarkInfo 8 0 R/Loop 10 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            [3] = "4 0 R",
            [4] = "<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 300]>>",
            [8] = "9 0 R",
            [9] = "<</Marked true>>",
            [10] = "11 0 R",
            [11] = "10 0 R",
        });
        using var doc = PdfDocument.Open(file);
        Assert.Equal(true, (doc.Catalog!.GetDictionary(PdfName.Get("MarkInfo"))?.Get(PdfName.Get("Marked")) as PdfBoolean)?.Value);
        Assert.Single(doc.Pages);
        Assert.Equal(300, doc.Pages[0].MediaBox.Height, 3);
        Assert.Same(PdfNull.Instance, doc.Catalog!.Get(PdfName.Get("Loop")));
    }

    /// <summary>
    /// 7.3.10: object number and generation together identify an object. A reference with another generation than the object finds it all
    /// the same, as in widely used readers (sonderregeln.md, A65).
    /// </summary>
    [Fact]
    public void Reference_WithOtherGeneration_FindsTheObject()
    {
        var file = CraftedFileTests.Build(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/X 3 1 R>>",
            [2] = "<</Type/Pages/Kids[]/Count 0>>",
            [3] = "<</Found true>>",
        });
        using var doc = PdfDocument.Open(file);
        Assert.NotNull(doc.Catalog!.GetDictionary(PdfName.Get("X")));
    }

    /// <summary>
    /// 7.3.8.2, Table 5: /DL gives the decoded length. Where saving re-encodes an image (here downsampled), the old value would be wrong: it
    /// goes (docs/FEHLERGEDAECHTNIS.md, No. 726).
    /// </summary>
    [Fact]
    public void DecodedLength_ReencodedImage_NoStaleValue()
    {
        var pdf = TestPdf.Simple(1, null);
        const int size = 900;
        var data = new byte[size * size * 3];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)((i / 3 % size + i / 3 / size) & 0xFF);
        var image = pdf.Add(TestPdf.FlateStream(data, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(size)), ("Height", PdfInteger.Of(size)), ("BitsPerComponent", PdfInteger.Of(8)),
            ("ColorSpace", TestPdf.N("DeviceRGB")), ("DL", PdfInteger.Of(data.Length)))));
        var content = pdf.Add(TestPdf.FlateStream("q 144 0 0 144 72 300 cm /Im0 Do Q"u8.ToArray()));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Contents, TestPdf.Ref(content));
        page.Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        using var original = PdfDocument.Open(pdf.Build());
        using var output = new MemoryStream();
        PdfWriter.Write(original, PageItem.From(original), output,
            new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.Screen), ImageCodec = Rendering.SkiaImageCodec.Instance });
        using var doc = PdfDocument.Open(output.ToArray());
        var written = (PdfStream)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Im0"));
        Assert.True(written.Dictionary.GetInteger(PdfName.Get("Width")) < size, "the image was not downsampled — the test does not test anything");
        if (written.Dictionary.GetInteger(PdfName.Get("DL")) is { } dl) Assert.Equal(doc.DecodeStream(written).Data.Length, dl);
    }

    /// <summary>
    /// 7.3.8.1: large data — page descriptions and images — are streams. The writer writes the content of a page (here held as an array of two
    /// streams) and every image as streams (M1 row 45).
    /// </summary>
    [Fact]
    public void Writer_PageContentAndImages_AreStreams()
    {
        const string first = "q 100 0 0 100 50 50 cm /Im0 Do Q", second = "0 0 1 rg 10 10 20 20 re f";
        using var saved = DocumentStructureNormTests.Saved(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Im0 6 0 R >> >> /Contents [4 0 R 5 0 R] >>",
            $"<< /Length {first.Length} >>\nstream\n{first}\nendstream", $"<< /Length {second.Length} >>\nstream\n{second}\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 1 >>\nstream\n\x80\nendstream"));
        var contents = saved.Pages[0].Dictionary.Get(PdfName.Contents);
        List<PdfObject?> parts = contents is PdfArray array ? [.. Enumerable.Range(0, array.Count).Select(array.Get)] : [contents];
        Assert.NotEmpty(parts);
        Assert.All(parts, p => Assert.IsType<PdfStream>(p));
        Assert.IsType<PdfStream>(saved.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Im0")));
    }

    /// <summary>
    /// 7.3.10: any value may stand directly or as an indirect reference, with the same meaning — the kids of the page tree, the page count,
    /// the media box, the rotation, the font and the length of the content read alike either way (M1 row 61).
    /// </summary>
    [Fact]
    public void DirectOrIndirect_SameMeaning()
    {
        const string content = "BT /F1 12 Tf 20 20 Td (Hallo) Tj ET";
        using var direct = PdfDocument.Open(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Rotate 90 /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"));
        using var indirect = PdfDocument.Open(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids 5 0 R /Count 6 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox 7 0 R /Rotate 8 0 R /Resources << /Font << /F1 9 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length 10 0 R >>\nstream\n{content}\nendstream",
            "[3 0 R]", "1", "[0 0 300 400]", "90", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"{content.Length}"));
        Assert.Equal(direct.PageCount, indirect.PageCount);
        Assert.Equal(direct.Pages[0].MediaBox, indirect.Pages[0].MediaBox);
        Assert.Equal(90, indirect.Pages[0].Rotate);
        string Text(PdfDocument doc) => string.Concat(Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text));
        Assert.Equal("Hallo", Text(direct));
        Assert.Equal(Text(direct), Text(indirect));
    }

    /// <summary>
    /// S-A A01 „überall gleich“: no reader of PDF-Knead takes a number that is none — „NaN“, „Infinity“ or a value beyond double — as a
    /// number: the lexer reads an integer of 400 digits as the largest double (before: infinity, which the writer wrote as 0), a Type 1
    /// /FontMatrix with „Infinity“ is no matrix, a CFF real „1E400“ reads as 0, a CMap target „Infinity“ maps nothing
    /// (docs/FEHLERGEDAECHTNIS.md, No. 929, 933).
    /// </summary>
    [Fact]
    public void NonFiniteNumbers_TakenByNoReader()
    {
        var huge = Parse("1" + new string('0', 400));
        Assert.True(huge.AsNumber() is { } h && double.IsFinite(h) && h > 1e308);
        Assert.Equal(huge.AsNumber(), Parse(Encoding.ASCII.GetString(Written(huge))).AsNumber());

        var type1 = Encoding.Latin1.GetString(PdfKnead.Tests.Fonts.Type1Builder.Build());
        Assert.Contains("[0.001 0 0 0.001 0 0]", type1);
        var font = Core.Fonts.Type1Font.Parse(Encoding.Latin1.GetBytes(type1.Replace("[0.001 0 0 0.001 0 0]", "[Infinity 0 0 0.001 0 0]")));
        Assert.Equal(new Core.Pdf.Graphics.Matrix(0.001, 0, 0, 0.001, 0, 0), font.FontMatrix);

        // a real operand (30) 1E400 (nibbles 1 B 4 0 0 F), then the operator UnderlinePosition (12 3)
        var dict = Core.Fonts.CffFont.Dict.Parse([30, 0x1B, 0x40, 0x0F, 12, 3]);
        Assert.Equal(0, dict.Number(1203));

        var cmap = Core.Pdf.Fonts.CMap.Parse(Encoding.ASCII.GetBytes("""
            /CIDInit /ProcSet findresource begin 12 dict begin begincmap
            1 begincodespacerange <00> <FF> endcodespacerange
            1 begincidrange <41> <42> Infinity endcidrange
            1 begincidchar <43> NaN endcidchar
            endcmap CMapName currentdict /CMap defineresource pop end end
            """));
        Assert.Equal(0, cmap.ToCid(0x41, 1));
        Assert.Equal(0, cmap.ToCid(0x43, 1));
    }

    /// <summary>
    /// 7.3.3 (S-A A01): a number has at most one sign and one point. The parser still reads „--24“, „700.5.5“, „-“ and „1.5e3“ (as the
    /// display draws them); <see cref="ContentConformance.Canonical"/> gives the form 7.3.3 allows with that value, nothing for a well-formed
    /// number or a token that is no number (docs/FEHLERGEDAECHTNIS.md, No. 930).
    /// </summary>
    [Theory]
    [InlineData("--24", "24")]
    [InlineData("+-5", "-5")]
    [InlineData("700.5.5", "700.5")]
    [InlineData("-", "0")]
    [InlineData(".", "0")]
    [InlineData("1.5e3", "1.5")]
    [InlineData("24", null)]
    [InlineData("-.5", null)]
    [InlineData("4.", null)]
    [InlineData("12abc", null)]
    public void MalformedNumber_CanonicalForm(string token, string? expected)
    {
        var canonical = ContentConformance.Canonical(Encoding.ASCII.GetBytes(token));
        Assert.Equal(expected, canonical is null ? null : Encoding.ASCII.GetString(canonical));
        // as strict readers fail on it, PDF/A counts it as a number out of range (as „-“ alone before)
        if (expected is not null) Assert.NotEqual(ContentFaults.None, ContentConformance.Scan(Encoding.ASCII.GetBytes($"q {token} w Q")));
    }

    /// <summary>
    /// 7.3.3 for the writer (S-A A01, question 2 of the Baustein S: written as the norm asks?): a saved copy keeps no malformed number in its
    /// content streams — the page content, a form and the appearance of an annotation get the value the display reads, in the form of 7.3.3;
    /// what only looks like such a number — in a string, in the data of an inline image — stays byte for byte, and the page reads the same
    /// before and after. Before, content streams were copied as they were, and other readers read „--24“ each their own way (one as −24, the
    /// others dropped the operator; measured with lesetoleranz.py, docs/FEHLERGEDAECHTNIS.md, No. 930).
    /// </summary>
    [Fact]
    public void Writer_MalformedNumbersInContent_WrittenAsTheDisplayReadsThem()
    {
        const string page = "BT /F1 --24 Tf 72 700.5.5 Td (1.2.3 --5) Tj ET" + "\n" + "1 0 0 rg 10 10 - 20 re f /Fm0 Do" + "\n" + "BI /W 2 /H 1 /CS /G /BPC 8 ID -- EI";
        const string form = "0.5.5 g 0 0 10 10 re f", appearance = "0 0 1 rg 0 0 --20 10 re f";
        var file = DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> /Contents 4 0 R /Annots [7 0 R] >>",
            $"<< /Length {page.Length} >>\nstream\n{page}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length {form.Length} >>\nstream\n{form}\nendstream",
            "<< /Type /Annot /Subtype /Square /Rect [100 100 120 110] /AP << /N 8 0 R >> >>",
            $"<< /BBox [0 0 20 10] /Length {appearance.Length} >>\nstream\n{appearance}\nendstream");
        using var original = PdfDocument.Open(file);
        using var saved = DocumentStructureNormTests.Saved(file);
        string Data(PdfObject? obj) => Encoding.Latin1.GetString(saved.GetStreamData(Assert.IsType<PdfStream>(obj)));
        var pageDict = saved.Pages[0].Dictionary;
        var contents = pageDict.Get(PdfName.Contents);
        var written = contents is PdfArray parts ? string.Concat(Enumerable.Range(0, parts.Count).Select(i => Data(parts.Get(i)))) : Data(contents);
        Assert.Contains("BT /F1 24 Tf 72 700.5 Td (1.2.3 --5) Tj ET", written);
        Assert.Contains("10 10 0 20 re f", written);
        Assert.Contains("ID -- EI", written);
        var formWritten = Data(saved.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Fm0")));
        Assert.Equal("0.5 g 0 0 10 10 re f", formWritten.Trim());
        var annotation = Assert.IsType<PdfDictionary>(pageDict.GetArray(PdfName.Get("Annots"))!.Get(0));
        Assert.Equal("0 0 1 rg 0 0 20 10 re f", Data(annotation.GetDictionary(PdfName.Get("AP"))!.Get(PdfName.Get("N"))).Trim());
        // the page reads the same: the same glyphs at the same places, in the same size
        static List<(string, float, float, float)> Glyphs(PdfDocument doc) =>
            [.. Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => (t.Text, t.X0, t.Y0, t.Size))];
        Assert.Equal(Glyphs(original), Glyphs(saved));
        Assert.Equal("1.2.3 --5", string.Concat(Glyphs(saved).Select(g => g.Item1)));
    }

    /// <summary>
    /// No. 930, the other content streams a page draws: a tiling pattern, the glyph of a Type 3 font and the group of a soft mask get their
    /// numbers in the form of 7.3.3 too — found through the resources (<see cref="ContentStreams"/>).
    /// </summary>
    [Fact]
    public void Writer_MalformedNumbers_InPatternGlyphAndMaskGroup()
    {
        const string page = "/GS0 gs /Pattern cs /P0 scn 0 0 50 50 re f BT /T3 1 Tf 100 100 Td (a) Tj ET";
        const string pattern = "0 0 --5 5 re f", glyph = "10 0 d0 0 0 7.5.5 7 re f", group = "0.5.5 g 0 0 200 200 re f";
        var file = DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Pattern << /P0 5 0 R >> /Font << /T3 6 0 R >> /ExtGState << /GS0 8 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {page.Length} >>\nstream\n{page}\nendstream",
            $"<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 5 5] /XStep 5 /YStep 5 /Resources << >> /Length {pattern.Length} >>\nstream\n{pattern}\nendstream",
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 10 10] /FontMatrix [0.1 0 0 0.1 0 0] /CharProcs << /a 7 0 R >> /Encoding << /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [10] /Resources << >> >>",
            $"<< /Length {glyph.Length} >>\nstream\n{glyph}\nendstream",
            "<< /Type /ExtGState /SMask << /S /Luminosity /G 9 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceGray >> /Resources << >> /Length {group.Length} >>\nstream\n{group}\nendstream");
        using var saved = DocumentStructureNormTests.Saved(file);
        string Data(PdfObject? obj) => Encoding.Latin1.GetString(saved.GetStreamData(Assert.IsType<PdfStream>(obj))).Trim();
        var resources = saved.Pages[0].Resources!;
        Assert.Equal("0 0 5 5 re f", Data(resources.GetDictionary(PdfName.Get("Pattern"))!.Get(PdfName.Get("P0"))));
        var font = resources.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("T3"))!;
        Assert.Equal("10 0 d0 0 0 7.5 7 re f", Data(font.GetDictionary(PdfName.Get("CharProcs"))!.Get(PdfName.Get("a"))));
        var mask = resources.GetDictionary(PdfName.Get("ExtGState"))!.GetDictionary(PdfName.Get("GS0"))!.GetDictionary(PdfName.Get("SMask"))!;
        Assert.Equal("0.5 g 0 0 200 200 re f", Data(mask.Get(PdfName.Get("G"))));
    }

    /// <summary>
    /// Security (PLAN §6 point 8) for the walk of <see cref="ContentStreams"/> (No. 930): forms that draw each other in a ring and a chain
    /// deeper than its limit — the save ends, the forms down to the depth the display draws are written in the form of 7.3.3, the ones below
    /// stay byte for byte (No. 934: before, the walk stopped at 16, the display at 24).
    /// </summary>
    [Fact]
    public void Writer_ContentWalk_RingAndDeepChain_EndWithinItsLimit()
    {
        const int forms = 30;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /X 5 0 R >> >> /Contents 4 0 R >>",
            "<< /Length 5 >>\nstream\n/X Do\nendstream",
        };
        for (var k = 0; k < forms; k++)
        {
            // form k draws form k + 1; the last draws the first again (a ring)
            var next = k + 1 < forms ? 6 + k : 5;
            var content = $"0 0 --{k + 1} 5 re f /X Do";
            objects.Add($"<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /XObject << /X {next} 0 R >> >> /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        using var saved = DocumentStructureNormTests.Saved(DocumentStructureNormTests.FileOf([.. objects]));
        var form = saved.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("X"));
        for (var k = 0; k < forms; k++)
        {
            var stream = Assert.IsType<PdfStream>(form);
            var data = Encoding.Latin1.GetString(saved.GetStreamData(stream)).Trim();
            Assert.Equal(k <= Core.Pdf.Content.PageInterpreter.MaxFormDepth ? $"0 0 {k + 1} 5 re f /X Do" : $"0 0 --{k + 1} 5 re f /X Do", data);
            form = stream.Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("X"));
        }
    }

    /// <summary>
    /// 7.3.8.1, Table 5 (M1 row 48, S-A A66): a stream that names an external file (/F, /FFilter, /FDecodeParms) — PDF-Knead reads the bytes
    /// in the stream with its /Filter, as two of three reference readers do, and loads no file; the copy keeps /F and the bytes (L39, M16).
    /// </summary>
    [Fact]
    public void ExternalStreamData_TheBytesInTheStreamRead_NoFileLoaded()
    {
        const string content = "BT /F1 12 Tf 20 20 Td (Im Strom) Tj ET";
        var file = DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /F (extern-inhalt.txt) /FFilter /FlateDecode /FDecodeParms << /Predictor 12 >> /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        string Text(PdfDocument doc) => string.Concat(Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text));
        using var original = PdfDocument.Open(file);
        Assert.Equal(content, Encoding.Latin1.GetString(original.GetStreamData(Assert.IsType<PdfStream>(original.Pages[0].Dictionary.Get(PdfName.Contents)))).Trim());
        Assert.Equal("Im Strom", Text(original));
        using var saved = DocumentStructureNormTests.Saved(file);
        var written = Assert.IsType<PdfStream>(saved.Pages[0].Dictionary.Get(PdfName.Contents));
        Assert.Equal("extern-inhalt.txt", Encoding.Latin1.GetString(Assert.IsType<PdfString>(written.Dictionary.Get(PdfName.Get("F"))).Bytes));
        Assert.Equal(content, Encoding.Latin1.GetString(saved.GetStreamData(written)).Trim());
        Assert.Equal("Im Strom", Text(saved));
    }

    /// <summary>
    /// S-A A67 for the keys of M1 (7.3.7, 7.3.9, 7.3.8.2 Table 5): an entry of a stream dictionary whose reference leads nowhere counts as
    /// absent — /Length (the data end at „endstream“), /Filter and /DecodeParms (the data are not decoded), /F, /FFilter, /FDecodeParms (no
    /// external file), /DL; the saved copy carries none of them, and the content reads the same.
    /// </summary>
    [Fact]
    public void StreamKeys_EntryIntoTheVoid_CountsAsAbsent()
    {
        const string content = "BT /F1 12 Tf 20 20 Td (Leer) Tj ET";
        var file = DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length 90 0 R /Filter 91 0 R /DecodeParms 92 0 R /F 93 0 R /FFilter 94 0 R /FDecodeParms 95 0 R /DL 96 0 R >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        using var original = PdfDocument.Open(file);
        var stream = Assert.IsType<PdfStream>(original.Pages[0].Dictionary.Get(PdfName.Contents));
        Assert.Equal(content, Encoding.Latin1.GetString(original.GetStreamData(stream)).Trim());
        using var saved = DocumentStructureNormTests.Saved(file);
        var written = Assert.IsType<PdfStream>(saved.Pages[0].Dictionary.Get(PdfName.Contents));
        foreach (var key in new[] { "F", "FFilter", "FDecodeParms", "DL", "DecodeParms" })
            Assert.Null(written.Dictionary.Get(PdfName.Get(key)) is PdfNull ? null : written.Dictionary.Get(PdfName.Get(key)));
        Assert.Equal(content, Encoding.Latin1.GetString(saved.GetStreamData(written)).Trim());
        string Text(PdfDocument doc) => string.Concat(Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text));
        Assert.Equal("Leer", Text(saved));
    }

    /// <summary>
    /// 7.3.4.2 (M1 row 27): a string encrypted by the writer keeps the rules of literal strings — the writer writes encrypted strings as
    /// hexadecimal strings, so no byte of the cipher text can end the string early or be read as an escape; opened with the password, the
    /// string reads as before, parentheses and backslash included.
    /// </summary>
    [Fact]
    public void Writer_EncryptedStrings_Hexadecimal()
    {
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R /Extra 4 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>", @"<< /T (Klammer \( und \\ Ende) >>");
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output,
            new PdfSaveOptions { Version = "1.7", ObjectStreams = false, Protection = new PdfProtection("geheim", "Besitzer", Core.Pdf.Security.PdfPermissions.All) });
        var text = Encoding.Latin1.GetString(output.ToArray());
        Assert.Matches(@"/T\s*<[0-9A-Fa-f\s]+>", text);
        Assert.DoesNotMatch(@"/T\s*\(", text);
        using var saved = PdfDocument.Open(output.ToArray(), "geheim");
        Assert.Equal(@"Klammer ( und \ Ende", Encoding.Latin1.GetString(saved.Catalog!.GetDictionary(PdfName.Get("Extra"))!.GetString(PdfName.Get("T"))!.Bytes));
    }

    /// <summary>
    /// 7.3.7 (M1 row 38): the keys of a dictionary are names. A value where a key belongs is no key — the parser passes over it with the
    /// value after it, and the entries around it stay; the writer writes every key as a name.
    /// </summary>
    [Fact]
    public void Dictionary_KeysAreNames()
    {
        var d = Assert.IsType<PdfDictionary>(Parse("<< /A 1 (kein Schluessel) 2 /B 3 >>"));
        Assert.Equal(1, d.GetInteger(PdfName.Get("A")));
        Assert.Equal(3, d.GetInteger(PdfName.Get("B")));
        Assert.All(d.Entries, e => Assert.IsType<PdfName>(e.Key));
        Assert.Matches(@"^<</A 1/B 3>>$", Encoding.Latin1.GetString(Written(d)).Replace(" /", "/"));
    }

    /// <summary>7.3.7 (M1 row 43): a dictionary stands in double angle brackets — read so, written so, also empty and nested.</summary>
    [Theory]
    [InlineData("<< >>", 0)]
    [InlineData("<< /A << /B 1 >> >>", 1)]
    public void Dictionary_InDoubleAngleBrackets(string text, int count)
    {
        var d = Assert.IsType<PdfDictionary>(Parse(text));
        Assert.Equal(count, d.Count);
        var written = Encoding.Latin1.GetString(Written(d));
        Assert.StartsWith("<<", written);
        Assert.EndsWith(">>", written);
        Assert.Equal(count, Assert.IsType<PdfDictionary>(Parse(written)).Count);
    }

    /// <summary>
    /// 7.3.8.2, Table 5, /Filter and /DecodeParms (M1 rows 51, 52): the writer packs only streams without a filter, and repacking replaces the
    /// chain — no filter twice (a Flate stream is not packed with Flate again); with several filters /DecodeParms stays an array in step with
    /// /Filter (null for a filter without parameters) or goes as a whole; the data decode as before.
    /// </summary>
    [Fact]
    public void Writer_FilterChain_NoFilterTwice_ParametersInStep()
    {
        var plain = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Daten im Strom, mehrfach. ", 40)));
        using var packed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(plain);
        var flate = Encoding.Latin1.GetString(packed.ToArray());
        var hex = Convert.ToHexString(packed.ToArray()) + ">";
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R /A 4 0 R /B 5 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            $"<< /Filter /FlateDecode /Length {flate.Length} >>\nstream\n{flate}\nendstream",
            $"<< /Filter [/ASCIIHexDecode /FlateDecode] /DecodeParms [null << /Predictor 1 >>] /Length {hex.Length} >>\nstream\n{hex}\nendstream");
        using var saved = DocumentStructureNormTests.Saved(file);
        foreach (var key in new[] { "A", "B" })
        {
            var stream = Assert.IsType<PdfStream>(saved.Catalog!.Get(PdfName.Get(key)));
            var chain = PdfKnead.Core.Pdf.Filters.StreamFilters.Chain(stream.Dictionary);
            Assert.Equal(chain.Count, chain.Select(f => f.Name).Distinct().Count());
            if (stream.Dictionary.Get(PdfName.DecodeParms) is PdfArray parms && stream.Dictionary.Get(PdfName.Filter) is PdfArray filters)
                Assert.Equal(filters.Count, parms.Count);
            Assert.Equal(plain, saved.GetStreamData(stream));
        }
    }

    /// <summary>
    /// 7.3.6, 7.3.7, 7.8.2 (S-A A02 in content): an array or dictionary that an operator ends before its closing bracket is a broken operand —
    /// it goes, the operator is read with the operands before it, and what follows is drawn and read. Before, the array or dictionary took in
    /// the rest of the stream: PDF-Knead showed nothing after it and found none of its text, while two reference readers show it — hidden
    /// text for search and redaction (measured with lesetoleranz.py, A02f, A02g; docs/FEHLERGEDAECHTNIS.md, No. 931). A property list that
    /// goes cannot put its /ActualText over the text either. The saved copy leaves the broken part out; the page reads the same.
    /// </summary>
    [Theory]
    [InlineData("BT /F1 24 Tf 72 700 Td [(TJ ohne) -250 (Klammer) TJ ET\nBT /F1 24 Tf 72 600 Td (Zweite Zeile) Tj ET", "Zweite Zeile", "[(TJ ohne)")]
    [InlineData("/Span << /ActualText (Ersatz) BDC\nBT /F1 24 Tf 72 700 Td (Eigenschaften ohne Ende) Tj ET\nEMC", "Eigenschaften ohne Ende", "/ActualText")]
    [InlineData("BT /F1 24 Tf 72 700 Td [[(innen) TJ (Danach) Tj ET", "Danach", "[[(innen)")]
    public void UnfinishedArrayOrDictionary_EndsAtTheOperator(string content, string shown, string gone)
    {
        var file = DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        static string Text(PdfDocument doc) => string.Concat(Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text));
        using var original = PdfDocument.Open(file);
        Assert.Equal(shown, Text(original));
        using var saved = DocumentStructureNormTests.Saved(file);
        var written = Encoding.Latin1.GetString(saved.GetStreamData(Assert.IsType<PdfStream>(saved.Pages[0].Dictionary.Get(PdfName.Contents))));
        Assert.DoesNotContain(gone, written);
        Assert.Equal(shown, Text(saved));
        // strict readers stop at an operator with too few operands: none is left (a TJ without its array goes, BDC without its list is BMC)
        Assert.DoesNotMatch(@"(^|\s)TJ", written.Replace("] TJ", ""));
        Assert.DoesNotContain("BDC", written);
        var parser = new Core.Pdf.Content.ContentParser(Encoding.Latin1.GetBytes(written));
        while (parser.Next() is not null) Assert.Null(parser.Discarded);
    }

    /// <summary>
    /// A damaged Flate content stream with a malformed number, by kind of damage (decided on 19.11.2026; docs/FEHLERGEDAECHTNIS.md,
    /// No. 937, 963): damaged in the middle — the decoder stops with bytes behind the damage — it is copied byte for byte (written anew it would
    /// lose them, and a repair tool may still read them); cut at the end — the decoder read all there is, only the Adler-32 sum is missing — it
    /// is written anew as RFC 1950 and 7.3.3 ask: a valid sum, the number in the allowed form, the same text shown.
    /// </summary>
    [Fact]
    public void Writer_DamagedContentStream_ByKindOfDamage()
    {
        var content = "BT /F1 24 Tf 72 --700 Td (Erste Zeile) Tj ET\n" +
                      string.Concat(Enumerable.Range(0, 60).Select(i => $"BT /F1 10 Tf 72 {650 - 10 * i} Td (Zeile {i}) Tj ET\n"));
        using var packed = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(Encoding.Latin1.GetBytes(content));
        var whole = packed.ToArray();
        var cut = whole[..(int)(whole.Length * 2 / 3)];
        var middle = (byte[])whole.Clone();
        for (var i = whole.Length / 2; i < whole.Length / 2 + 8; i++) middle[i] = 0xFF;
        byte[] FileWith(byte[] data) => DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Filter /FlateDecode /Length {data.Length} >>\nstream\n{Encoding.Latin1.GetString(data)}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        string Text(PdfDocument doc) => string.Concat(Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text));

        foreach (var data in new[] { middle, cut })
        {
            var file = FileWith(data);
            using var original = PdfDocument.Open(file);
            Assert.True(original.DecodeStream(Assert.IsType<PdfStream>(original.Pages[0].Dictionary.Get(PdfName.Contents))).Truncated);
            using var saved = DocumentStructureNormTests.Saved(file);
            var copy = Assert.IsType<PdfStream>(saved.Pages[0].Dictionary.Get(PdfName.Contents));
            Assert.Equal(Text(original), Text(saved));
            if (ReferenceEquals(data, middle))
            {
                Assert.Equal(middle, saved.GetRawStreamData(copy));
                continue;
            }
            var decoded = saved.DecodeStream(copy);
            Assert.False(decoded.Truncated);
            var written = Encoding.Latin1.GetString(decoded.Data);
            Assert.DoesNotContain("--700", written);
            Assert.Contains("(Erste Zeile) Tj", written);
        }
    }

    /// <summary>A content stream with only well-formed numbers is copied as it is (No. 930: only what 7.3.3 does not allow is written anew).</summary>
    [Fact]
    public void Writer_WellFormedContent_NotRewritten() =>
        Assert.Null(ContentConformance.CanonicalSyntax(Encoding.ASCII.GetBytes("q 1 0 0 1 -5 .5 cm (--24 1.2.3) Tj Q")));

    /// <summary>
    /// The check before the parser passes over strings, comments and names as the lexer reads them — a CID text or a date in a string no
    /// longer sends every content through the parser (No. 936) —, and still finds every malformed number outside them: after a hexadecimal
    /// string, after a string with an escaped bracket, in a buffer longer than the content.
    /// </summary>
    [Theory]
    [InlineData("<00A1> Tj 1.2.3 0 Td", "<00A1> Tj 1.2 0 Td")]
    [InlineData(@"(a\) (b) 1.2.3) Tj --5 0 Td", @"(a\) (b) 1.2.3) Tj 5 0 Td")]
    [InlineData("((nested) 1.2.3) Tj 0 --7 Td", "((nested) 1.2.3) Tj 0 7 Td")]
    [InlineData("% 1.2.3 in a comment\n1.2.3 0 Td", "% 1.2.3 in a comment\n1.2 0 Td")]
    [InlineData("/N1.2.3 gs 1.2.3 0 Td", "/N1.2.3 gs 1.2 0 Td")]
    [InlineData("<00A1 1.2.3> Tj (18.11.2026) Tj % --5\n/N-1 gs [<0041> -250 (a)] TJ", null)]
    public void CanonicalSyntax_CheckBeforeTheParser_PassesOverStringsOnly(string content, string? expected)
    {
        var bytes = Encoding.ASCII.GetBytes(content);
        // what goes through the parser: only the content with something to repair (before No. 936 also the last one)
        Assert.Equal(expected is not null, ContentConformance.Suspect(bytes, bytes.Length));
        var canonical = ContentConformance.CanonicalSyntax(bytes);
        Assert.Equal(expected, canonical is null ? null : Encoding.ASCII.GetString(canonical));
        // the same over a rented buffer with bytes after the content (a malformed number there is not part of it)
        var buffer = new byte[bytes.Length + 8];
        bytes.CopyTo(buffer, 0);
        "  --9.9.9"u8[..8].CopyTo(buffer.AsSpan(bytes.Length));
        var pooled = ContentConformance.CanonicalSyntax(buffer, bytes.Length);
        Assert.Equal(expected, pooled is null ? null : Encoding.ASCII.GetString(pooled));
    }
}
