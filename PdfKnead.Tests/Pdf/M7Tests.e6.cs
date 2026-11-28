using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, decisions on programs that cannot be scrubbed and on the licence bits (docs/FEHLERGEDAECHTNIS.md, No. 1164): when redacting, a font
/// program the scrubbers refuse is rebuilt from the outlines the display draws, one the display does not read leaves the file, both sets of a
/// hybrid Type 1 font are scrubbed, a collection is scrubbed as its first font; compressing and the PDF/A copy leave programs whose licence
/// forbids changing them as they are.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// A page of 300 × 200 with /F1 (object 5; descriptor 6, program 7, a CIDFont as object 8 where given) and the content given; the program
    /// stream with its dictionary entries.
    /// </summary>
    private static byte[] E6Pdf(string font, string descriptor, string programEntries, byte[] program, string content, string? cidFont = null)
    {
        var data = Encoding.Latin1.GetString(program);
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", font, descriptor,
            $"<< /Length {data.Length} {programEntries} >>\nstream\n{data}\nendstream",
        ];
        return DocumentStructureNormTests.FileOf(cidFont is null ? objects : [.. objects, cidFont]);
    }

    /// <summary>The content of the probes: the first glyph at (20, 50), the second at (170, 50), size 50 — a bar over (10, 30)–(120, 150) hides the first.</summary>
    private static string E6Content(string first, string second) => $"BT /F1 50 Tf 20 50 Td {first} Tj 150 0 Td {second} Tj ET";

    private static readonly PdfRectangle E6Bar = new(10, 30, 120, 150);

    /// <summary>The glyph a font of the redacted file draws for a code, the same as before: width, text and the outline in text space (within 10⁻⁵ em).</summary>
    private static void E6SameGlyph(PdfGlyph expected, PdfGlyph actual)
    {
        Assert.Equal(expected.Width, actual.Width, 6);
        Assert.Equal(expected.Text, actual.Text);
        static List<(double X, double Y)> Points(PdfGlyph g)
        {
            var points = new List<(double, double)>();
            for (var i = 0; i + 1 < g.Outline.Points.Count; i += 2)
            {
                var (x, y) = (g.Outline.Points[i], g.Outline.Points[i + 1]);
                points.Add((g.ToText.A * x + g.ToText.C * y + g.ToText.E, g.ToText.B * x + g.ToText.D * y + g.ToText.F));
            }
            return points;
        }
        var (a, b) = (Points(expected), Points(actual));
        Assert.False(a.Count == 0, "the glyph draws nothing");
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].X, b[i].X, 5);
            Assert.Equal(a[i].Y, b[i].Y, 5);
        }
    }

    /// <summary>Whether a CFF program has a glyph whose outline lies on the box given (font units): the trace of a removed glyph.</summary>
    private static bool E6HasGlyphWithin(byte[] cff, RectD box)
    {
        var font = CffFont.Parse(cff);
        for (var g = 0; g < font.GlyphCount; g++)
            if (font.Outline(g) is { IsEmpty: false } outline && outline.Bounds.Equals(box)) return true;
        return false;
    }

    /// <summary>A charstring of glyph A: a square from (100, 0) to (400, 300), width 600.</summary>
    private static byte[] E6SquareA => Cff01Cs(600, 100, 0, "rmoveto", 300, 0, "rlineto", 0, 300, "rlineto", -300, 0, "rlineto", "endchar");

    private static readonly RectD E6SquareBox = RectD.FromLtrb(100, 0, 400, 300);

    /// <summary>
    /// Finding S3, question 6 (redaction; ISO 32000-2 9.9 with Adobe TN 5177 4.4 and 4.5): a bare CFF program (FontFile3 /Type1C) whose kept glyph
    /// B computes with „mul“, which the scrubber cannot flatten — redacting the A rebuilds the font from what the display draws: the new program
    /// has no glyph of A's square, B draws as before (outline, width, text „B“), the A draws nothing. Before the program stayed byte for byte,
    /// with the glyph of the redacted text.
    /// </summary>
    [Fact]
    public void Redaction_CffWithArithmetic_RebuiltFromTheOutlines()
    {
        var b = Cff01Cs(600, 0, 0, "rmoveto", 50, 2, "mul", 0, "rlineto", 0, 100, "rlineto", "endchar");
        var cff = Cff01Build(new Cff01Spec { CharStrings = [[14], E6SquareA, b], Charset = [0, 0, 34, 0, 35] });
        Assert.Throws<FontFormatException>(() => CffScrubber.Scrub(cff, [1, 2]));
        var file = E6Pdf("<< /Type /Font /Subtype /Type1 /BaseFont /CffProbe /FirstChar 65 /LastChar 66 /Widths [600 600] /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /CffProbe /Flags 32 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile3 7 0 R >>",
            "/Subtype /Type1C", cff, E6Content("(A)", "(B)"));
        using var doc = PdfDocument.Open(file);
        var before = PdfFont.Get(Iso04F1(doc), doc);
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, E6Bar)));
        var font = Iso04F1(after);
        var program = font.GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile3"))!;
        var data = after.GetStreamData(program);
        Assert.NotEqual(cff, data);
        Assert.False(E6HasGlyphWithin(data, E6SquareBox));
        var redrawn = PdfFont.Get(font, after);
        E6SameGlyph(before.Glyph('B', 1), redrawn.Glyph('B', 1));
        Assert.Equal("B", redrawn.Glyph('B', 1).Text);
        Assert.True(redrawn.Glyph('A', 1).Outline.IsEmpty);
    }

    /// <summary>
    /// Question 6 (redaction; TN 5177 4.5): a CID-keyed CFF program under CIDFontType0 whose CID 2 uses put and get — redacting CID 1 rebuilds the
    /// descendant from what the display draws: a CID-keyed program (FontFile3 /CIDFontType0C) without the square of CID 1, CID 2 drawn as before;
    /// the encoding and the CIDs stay. Before the program stayed whole.
    /// </summary>
    [Fact]
    public void Redaction_CidCffWithPutGet_RebuiltFromTheOutlines()
    {
        var putGet = Cff01Cs(600, 0, 0, "rmoveto", 100, 3, "put", 3, "get", 0, "rlineto", 0, 100, "rlineto", "endchar");
        var cff = Cff01Cid([[14], E6SquareA, putGet], [0, 0, 0, 0], [new Cff01Fd([], [])], charset: [0, 0, 1, 0, 2]);
        Assert.Throws<FontFormatException>(() => CffScrubber.Scrub(cff, [1, 2]));
        var file = E6Pdf("<< /Type /Font /Subtype /Type0 /BaseFont /CidProbe /Encoding /Identity-H /DescendantFonts [8 0 R] >>",
            "<< /Type /FontDescriptor /FontName /CidProbe /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 /FontFile3 7 0 R >>",
            "/Subtype /CIDFontType0C", cff, E6Content("<0001>", "<0002>"),
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /CidProbe /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 6 0 R /DW 600 >>");
        using var doc = PdfDocument.Open(file);
        var before = PdfFont.Get(Iso04F1(doc), doc);
        Assert.False(before.Glyph(2, 2).Outline.IsEmpty);
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, E6Bar)));
        var font = Iso04F1(after);
        var kid = font.GetArray(PdfName.Get("DescendantFonts"))!.Get(0) as PdfDictionary;
        var program = kid!.GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile3"))!;
        Assert.Equal("CIDFontType0C", program.Dictionary.GetName(PdfName.Subtype)?.Value);
        var data = after.GetStreamData(program);
        Assert.NotEqual(cff, data);
        Assert.True(CffFont.Parse(data).IsCidKeyed);
        Assert.False(E6HasGlyphWithin(data, E6SquareBox));
        var redrawn = PdfFont.Get(font, after);
        E6SameGlyph(before.Glyph(2, 2), redrawn.Glyph(2, 2));
        Assert.True(redrawn.Glyph(1, 2).Outline.IsEmpty);
    }

    /// <summary>
    /// Finding B53, question 6 and the reader's decision on unreadable programs (redaction, M6): text „AB“ in a synthetic Type 1 font, which the
    /// display does not read (it draws the substitute, as two of the three reference readers do) — redacting the A takes the program out of the
    /// file; B is drawn by the substitute as before. Before the program stayed whole, with the glyphs of the redacted text.
    /// </summary>
    [Fact]
    public void Redaction_UnreadableType1_ProgramLeavesTheFile()
    {
        var synthetic = T1PaketSynthetic();
        Assert.Null(FontProgram.FromBytes(synthetic));
        var clear = synthetic.AsSpan().IndexOf("eexec"u8) + 6;
        using var doc = PdfDocument.Open(T1Paket.Pdf((synthetic, clear, synthetic.Length - clear - T1Paket.Zeros.Length), E6Content("(A)", "(B)")));
        var before = PdfFont.Get(Iso04F1(doc), doc);
        var output = RedactionTests.WithBars(doc, (0, E6Bar));
        using var after = PdfDocument.Open(output);
        var descriptor = Iso04F1(after).GetDictionary(PdfName.Get("FontDescriptor"))!;
        Assert.False(descriptor.ContainsKey(PdfName.Get("FontFile")));
        Assert.False(descriptor.ContainsKey(PdfName.Get("FontFile3")));
        Assert.True(output.AsSpan().IndexOf("KneadTestOblique"u8) < 0);
        E6SameGlyph(before.Glyph('B', 1), PdfFont.Get(Iso04F1(after), after).Glyph('B', 1));
    }

    /// <summary>A hybrid Type 1 program in the order of T1 9.2: Subrs for low, then for high resolution, then the CharStrings likewise.</summary>
    private static (byte[] Data, int Clear, int Encrypted) E6Hybrid()
    {
        string Subrs(string subr4)
        {
            string[] subrs = [.. T1Paket.DefaultSubrs[..4], subr4];
            var text = new StringBuilder($"/Subrs {subrs.Length} array\n");
            for (var i = 0; i < subrs.Length; i++)
            {
                var bytes = Type1Sample.Encrypt([0, 0, 0, 0, .. T1Paket.Charstring(subrs[i])], 4330);
                text.Append($"dup {i} {bytes.Length} RD ").Append(Encoding.Latin1.GetString(bytes)).Append(" NP\n");
            }
            return text.Append("ND\n").ToString();
        }
        string CharStrings(string a)
        {
            var text = new StringBuilder("2 index /CharStrings 3 dict dup begin\n");
            foreach (var (name, code) in new[] { (".notdef", T1Paket.NotDef), ("A", a), ("B", "0 600 hsbw 50 50 rmoveto 100 0 rlineto 0 100 rlineto closepath endchar") })
            {
                var bytes = Type1Sample.Encrypt([0, 0, 0, 0, .. T1Paket.Charstring(code)], 4330);
                text.Append($"/{name} {bytes.Length} RD ").Append(Encoding.Latin1.GetString(bytes)).Append(" ND\n");
            }
            return text.Append("end\n").ToString();
        }
        var priv = "dup /Private 8 dict dup begin\n/RD{string currentfile exch readstring pop}executeonly def\n/ND{noaccess def}executeonly def\n" +
                   "/NP{noaccess put}executeonly def\n/lenIV 4 def\n/hires false def\n" + Subrs("100 0 rlineto return") + "save hires {restore} if\n" +
                   Subrs("0 100 rlineto return") + CharStrings("0 600 hsbw 0 0 rmoveto 4 callsubr 50 50 rlineto closepath endchar") +
                   "save hires {restore} if\n" + CharStrings("0 600 hsbw 0 0 rmoveto 4 callsubr 60 60 rlineto closepath endchar") +
                   "end\nreadonly put\nnoaccess put\ndup /FontName get exch definefont pop\nmark currentfile closefile\n";
        var clear = Encoding.Latin1.GetBytes("%!PS-AdobeFont-1.0: KneadTest 001\n12 dict begin\n/FontName /KneadTest def\n/FontType 1 def\n" +
                                             "/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n/FontBBox {0 0 1000 1000} readonly def\n/Encoding StandardEncoding def\ncurrentdict end\ncurrentfile eexec\n");
        var encrypted = Type1Sample.Encrypt([0, 0, 0, 0, .. Encoding.Latin1.GetBytes(priv)], 55665);
        return ([.. clear, .. encrypted, .. Encoding.ASCII.GetBytes(T1Paket.Zeros)], clear.Length, encrypted.Length);
    }

    /// <summary>
    /// Finding S8, question 6 (cleaning; T1 9.2 sentences 6–10): both sets of a hybrid font are scrubbed — kept down to B, neither group of
    /// CharStrings names A any more, both say 2 entries, and subroutine 4 (called by A only) is an empty „return“ in both groups of Subrs; the
    /// reader takes A and B from the first set before and B the same after. Before the second set stayed as it was, with the A.
    /// </summary>
    [Fact]
    public void Type1_HybridFont_CleaningScrubsBothSets()
    {
        var program = E6Hybrid();
        var font = Type1Font.Parse(program.Data);
        Assert.Equal(new (float, float)[] { (0, 0), (100, 0), (150, 50) }, T1Paket.Points(font.Outline("A")));
        var (data, clear, encrypted, _) = Type1Scrubber.Scrub(program.Data, new HashSet<string> { "B" }, program.Clear, program.Encrypted);
        var priv = T1Paket.Private(data, clear, encrypted);
        Assert.DoesNotMatch(@"/A \d+ RD ", priv);
        Assert.Equal(2, Regex.Matches(priv, @"/B \d+ RD ").Count);
        Assert.Equal(2, Regex.Matches(priv, @"/CharStrings 2 dict").Count);
        Assert.Equal(2, Regex.Matches(priv, @"dup 4 5 RD ").Count);
        T1Paket.SameGlyph(font, Type1Font.Parse(data), "B");
    }

    /// <summary>
    /// Finding S9 a, question 6 (redaction; ISO 32000-2 9.9: FontFile2 holds a single font): a TrueType collection embedded as FontFile2, whose first
    /// font the display draws, is scrubbed as that font — after redacting the A the program is a font file of its own (no „ttcf“), A's glyph is
    /// empty and B draws as before. Before the scrubber looked for the tables at the collection header and the collection stayed whole.
    /// </summary>
    [Fact]
    public void Redaction_TrueTypeCollection_ScrubbedAsItsFirstFont()
    {
        var single = Ot30WithFsType(0x0000);
        var collection = Ot27CollectionOf(single);
        using var doc = PdfDocument.Open(PdfARuleCases.WithTrueType(program: () => collection)());
        var before = PdfFont.Get(Iso04F1(doc), doc);
        Assert.False(before.Glyph('A', 1).Outline.IsEmpty);
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(70, 690, 87, 730))));
        var font = Iso04F1(after);
        var data = after.GetStreamData(font.GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile2"))!);
        Assert.False(data.AsSpan().StartsWith("ttcf"u8));
        var scrubbed = OpenTypeFont.Parse(data);
        Assert.True(scrubbed.Outline(OpenTypeFont.Parse(single).GlyphForUnicode('A')).IsEmpty);
        E6SameGlyph(before.Glyph('B', 1), PdfFont.Get(font, after).Glyph('B', 1));
    }

    /// <summary>
    /// OT 31.2.5 sentences 8 and 13–14 with question 8 (PDF/A): the copy rebuilds every font — but not an embedded TrueType font whose licence is
    /// „restricted“ (fsType 0x0002) or forbids subsetting (bit 8): its program stays byte for byte, and the result names the font. Before both
    /// were rebuilt.
    /// </summary>
    [Theory]
    [InlineData(0x0002)]
    [InlineData(0x0100)]
    public void Ot30_LicenceKeptFont_NotRebuiltForPdfA(int fsType)
    {
        var program = Ot30WithFsType(fsType);
        using var doc = PdfDocument.Open(PdfARuleCases.WithTrueType(program: () => program)());
        var copy = PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A2b);
        using var written = PdfDocument.Open(copy.Bytes);
        var stream = Iso04F1(written).GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile2"))!;
        Assert.Equal(program, written.GetStreamData(stream));
        Assert.Contains("LiberationSans", copy.GetType().GetProperty("LicenceKeptFonts")?.GetValue(copy) as IEnumerable<string> ?? []);
    }

    /// <summary>
    /// OT 31.2.5 sentence 8 with question 8 (redaction, the deliberate deviation): redacting text in an embedded font of fsType 0x0002 scrubs its
    /// program all the same — nothing of the redacted text may stay; the A's glyph is empty afterwards, B draws as before.
    /// </summary>
    [Fact]
    public void Ot30_RestrictedEmbeddedFont_ScrubbedWhenRedacting()
    {
        var program = Ot30WithFsType(0x0002);
        using var doc = PdfDocument.Open(PdfARuleCases.WithTrueType(program: () => program)());
        var before = PdfFont.Get(Iso04F1(doc), doc);
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(70, 690, 87, 730))));
        var font = Iso04F1(after);
        var data = after.GetStreamData(font.GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile2"))!);
        Assert.True(OpenTypeFont.Parse(data).Outline(OpenTypeFont.Parse(program).GlyphForUnicode('A')).IsEmpty);
        E6SameGlyph(before.Glyph('B', 1), PdfFont.Get(font, after).Glyph('B', 1));
    }
}
