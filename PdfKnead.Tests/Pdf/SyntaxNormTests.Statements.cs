using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Parsing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Conformity check, module M1, the statements without a requirement word (kind DEF, counted since 22.11.2026; docs/FEHLERGEDAECHTNIS.md,
/// No. 1006): definitions, facts of the syntax and the examples of ISO 32000-2, 7.2 and 7.3, each checked where it says something a reader
/// or writer can get wrong (docs/normabgleich/M1-syntax.md names the row and the sentences of each test).
/// </summary>
public sealed partial class SyntaxNormTests
{
    // ------------------------------------------------------------ 7.2.2 representation

    /// <summary>
    /// 7.2.2 sentence 1: a file without encryption can consist of printable ASCII and white space alone — such a file (ASCIIHex content, an
    /// ASCII comment where the binary marker usually stands) reads like any other, and PDF-Knead finds its text.
    /// </summary>
    [Fact]
    public void AsciiOnlyFile_ReadsLikeAnyOther()
    {
        const string content = "BT /F1 12 Tf 20 20 Td (Nur ASCII) Tj ET";
        var hex = Convert.ToHexString(Encoding.ASCII.GetBytes(content)) + ">";
        var file = CraftedFileTests.Build(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 300 200]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>>>>>>>",
            [4] = ("/Filter/ASCIIHexDecode", hex),
        });
        // the builder's second line is the usual binary marker; the same number of ASCII bytes keeps every offset
        var marker = Array.IndexOf(file, (byte)'\n') + 1;
        Encoding.ASCII.GetBytes("%ASCI").CopyTo(file, marker);
        Assert.All(file, b => Assert.True(b is >= 0x20 and <= 0x7E || b is 0x0A or 0x0D or 0x09, $"Byte {b:X2}"));
        using var doc = PdfDocument.Open(file);
        Assert.Equal("Nur ASCII", string.Concat(PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text)));
    }

    // ------------------------------------------------------------ 7.2.3 character set

    /// <summary>
    /// 7.2.3 sentences 1–4: three classes of characters group the bytes into tokens — outside strings, streams and comments only: inside a
    /// string white space and delimiters are bytes of the string, inside a stream the data are not tokens at all, inside a comment nothing
    /// counts.
    /// </summary>
    [Fact]
    public void CharacterClasses_ApplyOutsideStringsStreamsAndComments()
    {
        Assert.Equal("a b/c[d]<e>%f", Encoding.Latin1.GetString(((PdfString)Parse("(a b/c[d]<e>%f)")).Bytes));
        Assert.Equal(["x", "y"], Tokens("x%(a b /c [d]\ny").Select(t => t.Text));
        var file = CraftedFileTests.Build(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/S 3 0 R>>",
            [2] = "<</Type/Pages/Kids[]/Count 0>>",
            [3] = ("", "endobj ( << /Fake 1 >> %"),
        });
        using var doc = PdfDocument.Open(file);
        Assert.Equal("endobj ( << /Fake 1 >> %"u8.ToArray(), doc.GetStreamData(doc.Catalog!.GetStream(PdfName.Get("S"))!));
    }

    // ------------------------------------------------------------ 7.2.4 comments

    /// <summary>
    /// 7.2.4 sentences 5 and 7: a comment separates the tokens around it — after the pattern of the standard's example (own values): a word, a
    /// comment holding a string, a name and a percent sign, and a number on the next line are two tokens.
    /// </summary>
    [Fact]
    public void Comment_ExampleOfTheStandard_TwoTokens()
    {
        var tokens = Tokens("abc%note (/%) more words\n123");
        Assert.Equal(2, tokens.Count);
        Assert.Equal("abc", tokens[0].Text);
        Assert.Equal(123, tokens[1].Integer);
    }

    /// <summary>
    /// 7.2.4 sentences 11 and 12: information for processors does not go into comments, names are the documented way (Annex E) — the writer
    /// writes no comment but the header, the binary marker and %%EOF; what PDF-Knead stores of its own stands in entries with names.
    /// </summary>
    [Fact]
    public void Writer_NoCommentsButHeaderMarkerAndEof()
    {
        var output = Saved(TestPdf.Simple(2).Build());
        var text = Encoding.Latin1.GetString(output);
        // stream data and strings may hold a percent sign as data; outside of them every % begins a comment
        var outside = System.Text.RegularExpressions.Regex.Replace(text, @"stream\r?\n.*?endstream", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        outside = System.Text.RegularExpressions.Regex.Replace(outside, @"\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f\s]*>", "");
        var comments = outside.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Contains('%')).Select(l => l[l.IndexOf('%')..]).ToList();
        Assert.StartsWith("%PDF-", comments[0]);
        Assert.All(comments.Skip(1).SkipLast(1), c => Assert.True(c.Skip(1).All(ch => ch >= 0x80), $"Kommentar „{c}“"));
        Assert.Equal("%%EOF", comments[^1]);
        Assert.InRange(comments.Count, 2, 3);
    }

    // ------------------------------------------------------------ 7.3.3 numbers

    /// <summary>
    /// 7.3.3 sentences 1–3, 12 and 14: integers and reals are two kinds of number; where a number is asked for, either kind serves — the
    /// integer 1 instead of 1.0, as the standard's example says.
    /// </summary>
    [Fact]
    public void Numbers_TwoKinds_EitherServesAsANumber()
    {
        Assert.IsType<PdfInteger>(Parse("1"));
        Assert.IsType<PdfReal>(Parse("1.0"));
        var array = (PdfArray)Parse("[1 1.0]");
        Assert.Equal(array.GetNumber(0), array.GetNumber(1));
        Assert.Equal("1", Encoding.ASCII.GetString(Written(new PdfReal(1.0))));
    }

    // ------------------------------------------------------------ 7.3.4 strings

    /// <summary>
    /// 7.3.4.2 sentences 5 and 26: after the pattern of the standard's examples (own values) — literal strings read with balanced parentheses,
    /// special characters and newlines kept, the empty string is empty; (\0053) is two bytes, \005 and the digit 3, (\053) and (\53) one
    /// byte, the plus sign.
    /// </summary>
    [Fact]
    public void LiteralString_ExamplesOfTheStandard()
    {
        string Read(string s) => Encoding.Latin1.GetString(((PdfString)Parse(s)).Bytes);
        Assert.Equal("Ein Text", Read("(Ein Text)"));
        Assert.Equal("erste Zeile\nzweite", Read("(erste Zeile\nzweite)"));
        Assert.Equal("Klammern (paarig) und Zeichen ( * ! & } ^ % ) bleiben", Read("(Klammern (paarig) und Zeichen ( * ! & } ^ % ) bleiben)"));
        Assert.Equal("", Read("()"));
        Assert.Equal("Nummer (7) drin", Read("(Nummer (7) drin)"));
        Assert.Equal("\u00053", Read(@"(\0053)"));
        Assert.Equal("+", Read(@"(\053)"));
        Assert.Equal("+", Read(@"(\53)"));
    }

    /// <summary>
    /// 7.3.4.3 sentences 4 and 9–11: each pair of digits is one byte, a missing last digit is 0 — after the pattern of the standard's examples
    /// (own values): &lt;7C2E51&gt; is 7C 2E 51, &lt;7C2E5&gt; is 7C 2E 50.
    /// </summary>
    [Fact]
    public void HexString_ExamplesOfTheStandard()
    {
        Assert.Equal(new byte[] { 0x7C, 0x2E, 0x51 }, ((PdfString)Parse("<7C2E51>")).Bytes);
        Assert.Equal(new byte[] { 0x7C, 0x2E, 0x50 }, ((PdfString)Parse("<7C2E5>")).Bytes);
    }

    // ------------------------------------------------------------ 7.3.5 names

    /// <summary>
    /// 7.3.5 sentence 12: no delimiter stands between the slash and the name — „/ Name“ is the empty name and a keyword, not the name Name;
    /// the writer writes the slash directly before the name.
    /// </summary>
    [Fact]
    public void Name_NoDelimiterAfterTheSlash()
    {
        var tokens = Tokens("/ Name");
        Assert.Equal(2, tokens.Count);
        Assert.Equal(PdfTokenKind.Name, tokens[0].Kind);
        Assert.Equal("", tokens[0].Text);
        Assert.NotEqual(PdfTokenKind.Name, tokens[1].Kind);
        Assert.Equal("/Name", Encoding.ASCII.GetString(Written(PdfName.Get("Name"))));
    }

    /// <summary>
    /// 7.3.5, Table 4 (sentences 17–39): after the pattern of each of the table's examples (own values) — a plain name, a long one, one with
    /// punctuation, digits and a dot, other regular characters, one beginning with a dot, and the escapes #20 (space), #28#29 (parentheses),
    /// #23 (number sign) and #xx for a regular character — each reads as the name it stands for and writes back.
    /// </summary>
    [Theory]
    [InlineData("/Feld7", "Feld7")]
    [InlineData("/EinRechtLangerNameOhneZeichen", "EinRechtLangerNameOhneZeichen")]
    [InlineData("/B;Name_mit-Zeichen**?", "B;Name_mit-Zeichen**?")]
    [InlineData("/3.4", "3.4")]
    [InlineData("/&&", "&&")]
    [InlineData("/@muster", "@muster")]
    [InlineData("/.leer", ".leer")]
    [InlineData("/Blau#20Grau", "Blau Grau")]
    [InlineData("/runde#28#29Klammern", "runde()Klammern")]
    [InlineData("/Nummer#23_7", "Nummer#_7")]
    [InlineData("/X#59", "XY")]
    public void Name_ExamplesOfTable4(string syntax, string name)
    {
        Assert.Equal(name, Assert.IsType<PdfName>(Parse(syntax)).Value);
        Assert.Same(PdfName.Get(name), Parse(Encoding.Latin1.GetString(Written(PdfName.Get(name)))));
    }

    /// <summary>7.3.5 sentences 44 and 45: a name's length is that of its bytes after the escapes — /K#20L has three characters, not six.</summary>
    [Fact]
    public void Name_LengthAfterTheEscapes()
    {
        Assert.Equal(3, ((PdfName)Parse("/K#20L")).Value.Length);
    }

    // ------------------------------------------------------------ 7.3.6 arrays

    /// <summary>
    /// 7.3.6 sentences 1, 7 and 8: an array is one-dimensional and ordered; higher dimensions are arrays in arrays, nested deep — PDF-Knead
    /// reads them to the depth <see cref="PdfLimits.MaxNesting"/> counted from 0 — 257 arrays in one another (a bound against malicious
    /// files, of the kind Annex C, „Nested objects“, foresees); one level more is refused, not a crash. Exactly at the bound.
    /// </summary>
    [Fact]
    public void Array_Ordered_NestedToTheLimit()
    {
        var a = (PdfArray)Parse("[3 1 2]");
        Assert.Equal([3L, 1, 2], a.Select(o => ((PdfInteger)o).Value));
        var matrix = (PdfArray)Parse("[[1 2] [3 4]]");
        Assert.Equal(4, ((PdfInteger)((PdfArray)matrix[1])[1]).Value);
        var arrays = PdfLimits.MaxNesting + 1;
        var deep = Parse(new string('[', arrays) + new string(']', arrays));
        var levels = 1;
        for (var o = deep; o is PdfArray { Count: > 0 } x; o = x[0]) levels++;
        Assert.Equal(arrays, levels);
        Assert.Throws<PdfLimitException>(() => Parse(new string('[', arrays + 1) + new string(']', arrays + 1)));
    }

    // ------------------------------------------------------------ 7.3.7 dictionaries

    /// <summary>
    /// 7.3.7 sentences 1, 2 and 16: entries are pairs of key and value — after the pattern of the standard's example (own values): names,
    /// numbers, a string and a nested dictionary with a boolean, read with all their entries.
    /// </summary>
    [Fact]
    public void Dictionary_ExampleOfTheStandard()
    {
        var d = Assert.IsType<PdfDictionary>(Parse(
            "<</Type /Probe /Subtype /Woerterbuch /Stand 0.02 /Zahl 34 /Text (ein String) " +
            "/Inneres << /A 0.5 /B false /C (zuletzt !) /D (Ende) >> >>"));
        Assert.Equal(6, d.Count);
        Assert.Equal("Probe", d.GetName(PdfName.Type)?.Value);
        Assert.Equal(34, d.GetInteger(PdfName.Get("Zahl")));
        var sub = d.GetDictionary(PdfName.Get("Inneres"))!;
        Assert.Equal(4, sub.Count);
        Assert.Equal("Ende", Encoding.Latin1.GetString(((PdfString)sub.Get(PdfName.Get("D"))).Bytes));
    }

    // ------------------------------------------------------------ 7.3.10 indirect objects

    /// <summary>
    /// 7.3.10 sentences 2, 10, 11, 14 and 25–28: after the pattern of the standard's examples (own values) — „12 0 obj (Wert) endobj“ is a
    /// string object with number 12 and generation 0, „12 0 R“ refers to it; a stream whose /Length is „8 0 R“, with object 8 after it holding the number (a comment
    /// behind it, as in the example), reads exactly its bytes.
    /// </summary>
    [Fact]
    public void IndirectObjects_ExamplesOfTheStandard()
    {
        const string content = "BT\n/F1 12 Tf\n72 700 Td\n(Laenge als Verweis) Tj\nET";
        var file = CraftedFileTests.Build(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/Str 12 0 R/S 7 0 R>>",
            [2] = "<</Type/Pages/Kids[]/Count 0>>",
            [7] = $"<</Length 8 0 R>> %Verweis auf Objekt 8\nstream\n{content}\nendstream",
            [8] = $"{content.Length} %die Laenge des Stroms davor",
            [12] = "(Wert)",
        });
        using var doc = PdfDocument.Open(file);
        Assert.Equal("Wert", Encoding.Latin1.GetString(((PdfString)doc.GetObject(12)!).Bytes));
        Assert.Equal("Wert", Encoding.Latin1.GetString(((PdfString)doc.Catalog!.Get(PdfName.Get("Str"))).Bytes));
        Assert.Equal(Encoding.Latin1.GetBytes(content), doc.GetStreamData(doc.Catalog!.GetStream(PdfName.Get("S"))!));
    }

    // ------------------------------------------------------------ added after the probe of 22.11.2026

    /// <summary>7.2.3 sentences 6 and 8: a sequence of white-space characters outside strings and streams counts as one.</summary>
    [Fact]
    public void WhiteSpace_SequenceCountsAsOne()
    {
        var tokens = Tokens("1 \t\r\n\f\u0000  2\r\n\r\n[ \n /A \t ]");
        Assert.Equal([PdfTokenKind.Integer, PdfTokenKind.Integer, PdfTokenKind.ArrayStart, PdfTokenKind.Name, PdfTokenKind.ArrayEnd],
            tokens.Select(t => t.Kind));
        Assert.Equal(3, ((PdfArray)Parse("[1 \r\n\t  2\n\n\n 3]")).Count);
    }

    /// <summary>
    /// 7.2.4 sentence 1: a comment also stands in a content stream — the content parser (its own code, not the lexer of the file) passes over
    /// it to the end of the line, operators and strings in it included.
    /// </summary>
    [Fact]
    public void Comment_InContentStream_PassedOver()
    {
        const string content = "BT /F1 12 Tf 20 20 Td %(Versteckt) Tj ET q\n(Sichtbar) Tj ET";
        using var doc = PdfDocument.Open(DocumentStructureNormTests.FileOf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"));
        Assert.Equal("Sichtbar", string.Concat(PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text)));
    }

    /// <summary>7.3.2 sentences 1 and 2: the booleans are the keywords true and false — read so and written so.</summary>
    [Fact]
    public void Booleans_ReadAndWritten()
    {
        Assert.Same(PdfBoolean.True, Parse("true"));
        Assert.Same(PdfBoolean.False, Parse("false"));
        Assert.Equal("true", Encoding.ASCII.GetString(Written(PdfBoolean.True)));
        Assert.Equal("false", Encoding.ASCII.GetString(Written(PdfBoolean.False)));
    }

    /// <summary>
    /// 7.3.9 sentences 1 and 2: there is one null object, unequal to any other — not false, not 0, not the empty string, name, array or
    /// dictionary — written as the keyword null.
    /// </summary>
    [Fact]
    public void Null_OneObject_UnequalToEveryOther()
    {
        Assert.Same(PdfNull.Instance, Parse("null"));
        foreach (var other in new[] { "false", "0", "()", "/", "[]", "<<>>" })
            Assert.NotEqual<PdfObject>(PdfNull.Instance, Parse(other));
        Assert.Equal("null", Encoding.ASCII.GetString(Written(PdfNull.Instance)));
    }

    /// <summary>
    /// 7.3.1 sentence 1: the nine kinds of object — boolean, integer, real, string, name, array, dictionary, stream, null — each read as its
    /// own kind.
    /// </summary>
    [Fact]
    public void NineKindsOfObjects()
    {
        var file = CraftedFileTests.Build(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/All[true 7 2.5 (s) /n [1] <</k 1>> 3 0 R null]>>",
            [2] = "<</Type/Pages/Kids[]/Count 0>>",
            [3] = ("", "data"),
        });
        using var doc = PdfDocument.Open(file);
        var all = doc.Catalog!.GetArray(PdfName.Get("All"))!;
        Type[] kinds = [typeof(PdfBoolean), typeof(PdfInteger), typeof(PdfReal), typeof(PdfString), typeof(PdfName), typeof(PdfArray),
            typeof(PdfDictionary), typeof(PdfStream), typeof(PdfNull)];
        Assert.Equal(kinds.Length, all.Count);
        for (var i = 0; i < kinds.Length; i++) Assert.IsType(kinds[i], all.Get(i));
    }

    /// <summary>
    /// 7.3.10 sentences 3 and 4: the object number is a positive integer — object 0 is no object (in the cross-reference table it heads the
    /// free list): a reference „0 0 R“ is null, also where the file defines „0 0 obj“; the writer numbers from 1.
    /// </summary>
    [Fact]
    public void ObjectNumber_Positive_ZeroIsNoObject()
    {
        var file = CraftedFileTests.Build(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/Zero 0 0 R>>",
            [2] = "<</Type/Pages/Kids[]/Count 0>>",
        });
        using (var doc = PdfDocument.Open(file))
            Assert.Same(PdfNull.Instance, doc.Catalog!.Get(PdfName.Get("Zero")));
        // the same objects with a planted „0 0 obj“ and no cross-reference table: the objects are found by scanning
        var text = Encoding.Latin1.GetString(file);
        text = "%PDF-1.7\n0 0 obj\n<</Planted true>>\nendobj\n" + text[(text.IndexOf("1 0 obj", StringComparison.Ordinal))..text.IndexOf("xref", StringComparison.Ordinal)]
               + "trailer\n<</Root 1 0 R>>\n%%EOF\n";
        using (var doc = PdfDocument.Open(Encoding.Latin1.GetBytes(text)))
            Assert.Same(PdfNull.Instance, doc.Catalog!.Get(PdfName.Get("Zero")));
        Assert.DoesNotMatch(@"(?<![\d.])0 \d+ obj\b", Encoding.Latin1.GetString(Saved(TestPdf.Simple(2).Build())));
    }

    /// <summary>
    /// 7.3.8.2, Table 5 (sentences 42, 43, 48): /DL is a non-negative integer and only a hint — a negative, zero, too small or not numeric
    /// value does not change the data read.
    /// </summary>
    [Theory]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("(text)")]
    public void DecodedLength_OnlyAHint(string value)
    {
        var data = Encoding.ASCII.GetBytes(new string('x', 500));
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        var file = CraftedFileTests.Build(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R/S 3 0 R>>",
            [2] = "<</Type/Pages/Kids[]/Count 0>>",
            [3] = ($"/Filter/FlateDecode/DL {value}", Encoding.Latin1.GetString(ms.ToArray())),
        });
        using var doc = PdfDocument.Open(file);
        Assert.Equal(data, doc.GetStreamData(doc.Catalog!.GetStream(PdfName.Get("S"))!));
    }

    // ------------------------------------------------------------ added after the second probe of 22.11.2026

    /// <summary>7.2.3, Table 2 (sentences 50–52): the closing curly brace is read as a delimiter too, as the opening one (S-A A64).</summary>
    [Fact]
    public void CurlyBraceClose_ReadAsDelimiter()
    {
        Assert.Equal(["a", "}", "b"], Tokens("/a}b").Select(t => t.Text));
    }

    /// <summary>
    /// 7.3.10 sentence 4: object 0 is no object also where the cross-reference table lists it as in use — the page's content stream as
    /// object 0 gives an empty page (none of the reference readers shows it; FG 1008).
    /// </summary>
    [Fact]
    public void ObjectNumber_Zero_InUseInTheTable_IsNoObject()
    {
        const string content = "BT /F1 24 Tf 72 700 Td (Objekt Null) Tj ET";
        var head = "%PDF-1.7\n";
        var o0 = head.Length;
        var body = $"0 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 0 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var text = new StringBuilder(head + body);
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n{o0:D10} 00000 n \n");
        foreach (var o in offsets) text.Append($"{o:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        using var doc = PdfDocument.Open(Encoding.Latin1.GetBytes(text.ToString()));
        Assert.Same(PdfNull.Instance, doc.GetObject(0));
        Assert.Empty(PageInterpreter.Build(doc, doc.Pages[0]).Text);
    }

    /// <summary>
    /// 7.3.8.2 sentences 4 and 5: a stream whose attributes imply a length (an image of 20 × 10 samples of 8 bits: 200 bytes) — /Length and the
    /// data agree with it: with 300 bytes only the first 200 are the image, with 100 bytes the image keeps its size and its first rows, and
    /// the rest does not break the reading (S-A A09).
    /// </summary>
    [Theory]
    [InlineData(200)]
    [InlineData(300)]
    [InlineData(100)]
    public void ImageLength_FromItsAttributes(int bytes)
    {
        var data = new string('\x40', Math.Min(bytes, 200)) + new string('\xFF', Math.Max(0, bytes - 200));
        var file = CraftedFileTests.Build(new()
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            [3] = "<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 100]/Resources<</XObject<</Im0 5 0 R>>>>/Contents 4 0 R>>",
            [4] = ("", "q 200 0 0 100 0 0 cm /Im0 Do Q"),
            [5] = ("/Type/XObject/Subtype/Image/Width 20/Height 10/ColorSpace/DeviceGray/BitsPerComponent 8", data),
        });
        using var doc = PdfDocument.Open(file);
        var image = Assert.Single(PageInterpreter.Build(doc, doc.Pages[0]).Images);
        var decoded = Assert.IsType<Core.Pdf.Graphics.DecodedImage>(Core.Pdf.Graphics.PdfImageDecoder.Decode(image, doc));
        Assert.Equal((20, 10), (decoded.Width, decoded.Height));
        var rgba = decoded.Rgba;
        var rows = Math.Min(bytes, 200) / 20;
        for (var i = 0; i < rows * 20; i++) Assert.Equal(0x40, rgba[i * 4]);
    }
}
