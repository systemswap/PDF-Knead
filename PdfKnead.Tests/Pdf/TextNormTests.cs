using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Text and fonts (ISO 32000-2, chapter 9; Konformitätsabgleich M7, docs/normabgleich/M7-text-schriften.md). Nachweise „TN.“ in the report.
/// </summary>
public sealed class TextNormTests
{
    /// <summary>A Type 3 font whose glyph "a" is a filled square of the full em (1000 units).</summary>
    private const string Type3Font =
        "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /a 6 0 R >> " +
        "/Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [1000] /Resources << >> >>";

    private const string SquareProcedure = "<< /Length 25 >>\nstream\n1000 0 d0 0 0 1000 1000 re f\nendstream";

    /// <summary>One page of 200 × 200 with /F1 (Helvetica) and /T3 (<see cref="Type3Font"/>), rendered at 72 dpi (y counted from the top).</summary>
    private static SKBitmap Rendered(string content)
    {
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 7 0 R /T3 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", Type3Font, SquareProcedure,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        return SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, new ImageCache());
    }

    private static bool Blue(SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, y) is { Blue: > 200, Red: < 60 };

    private static bool Red(SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, y) is { Red: > 200, Blue: < 60 };

    /// <summary>
    /// 9.3.1 (Tf): text of size 0 marks no pixel and clips none — also in a clipping mode. A blue area painted after it covers the page.
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(5)]
    public void ZeroSizedText_NeitherMarksNorClips(int mode)
    {
        using var bitmap = Rendered($"5 w 1 0 0 RG BT /F1 0 Tf {mode} Tr 50 100 Td (HHHH) Tj ET 0 0 1 rg 0 0 100 200 re f");
        Assert.True(Blue(bitmap, 50, 100));
        Assert.True(Blue(bitmap, 10, 10));
        Assert.False(Red(bitmap, 150, 100));
    }

    /// <summary>The text of the page with the given content and /F1.</summary>
    private static string TextOf(string content, string font)
    {
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", font);
        using var doc = PdfDocument.Open(file);
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        return string.Concat(list.Text.Select(t => t.Text));
    }

    /// <summary>
    /// 9.6.5.1 (Table 112): a non-embedded, nonsymbolic Type 1 font without /Encoding uses StandardEncoding — 0x27 is quoteright (’), 0x60
    /// quoteleft (‘). The substitute program the reader draws it with is a TrueType font; that must not make it WinAnsi (' and `). A TrueType
    /// font keeps WinAnsi (docs/FEHLERGEDAECHTNIS.md, No. 571, E-11).
    /// </summary>
    [Fact]
    public void Type1_NotEmbedded_WithoutEncoding_UsesStandardEncoding()
    {
        Assert.Equal("’‘", TextOf("BT /F1 12 Tf 10 10 Td <2760> Tj ET", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"));
        Assert.Equal("'`", TextOf("BT /F1 12 Tf 10 10 Td <2760> Tj ET", "<< /Type /Font /Subtype /TrueType /BaseFont /Arial >>"));
    }

    /// <summary>
    /// A code without a glyph name in a non-embedded font (0x9B is empty in StandardEncoding): /ToUnicode decides — here „º“, the glyph of
    /// 0xEB (ordmasculine); without /ToUnicode nothing (the code as Latin-1 is a control character). Before, the raw code went through the
    /// MacRoman cmap of the substitute program and drew „õ“ (special rule A85; docs/FEHLERGEDAECHTNIS.md, No. 769).
    /// </summary>
    [Fact]
    public void NotEmbedded_CodeWithoutName_GoesByToUnicode()
    {
        const string toUnicode = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <00> <FF> endcodespacerange " +
                                 "1 beginbfchar <9B> <00BA> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end";
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 5 0 R >>", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {toUnicode.Length} >>\nstream\n{toUnicode}\nendstream");
        using var doc = PdfDocument.Open(file);
        var withMap = PdfKnead.Core.Pdf.Fonts.PdfFont.Get((PdfKnead.Core.Pdf.Objects.PdfDictionary)doc.GetObject(3), doc);
        var without = PdfKnead.Core.Pdf.Fonts.PdfFont.Get((PdfKnead.Core.Pdf.Objects.PdfDictionary)doc.GetObject(4), doc);
        var mapped = withMap.Glyph(0x9B, 1).Outline;
        var ordmasculine = withMap.Glyph(0xEB, 1).Outline;
        Assert.False(mapped.IsEmpty);
        Assert.Equal(ordmasculine.Bounds, mapped.Bounds);
        Assert.True(without.Glyph(0x9B, 1).Outline.IsEmpty);
        // a code the file names .notdef draws nothing, also with a Latin-1 meaning (test file issue11403: 0xC2 appeared as „Â“)
        var named = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /Differences [194 /.notdef] >> >>");
        using var namedDoc = PdfDocument.Open(named);
        var notdef = PdfKnead.Core.Pdf.Fonts.PdfFont.Get((PdfKnead.Core.Pdf.Objects.PdfDictionary)namedDoc.GetObject(3), namedDoc);
        Assert.True(notdef.Glyph(0xC2, 1).Outline.IsEmpty);
        Assert.False(notdef.Glyph(0x41, 1).Outline.IsEmpty);
        // ... but the space named .notdef keeps the width of the space (test file issue4304: without /Widths the words ran together)
        var spaced = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding << /Type /Encoding /Differences [32 /.notdef] >> >>");
        using var spacedDoc = PdfDocument.Open(spaced);
        var times = PdfKnead.Core.Pdf.Fonts.PdfFont.Get((PdfKnead.Core.Pdf.Objects.PdfDictionary)spacedDoc.GetObject(3), spacedDoc);
        Assert.InRange(times.Glyph(0x20, 1).Width, 0.2, 0.3);
    }

    /// <summary>A Type 0 font (Identity-H unless <paramref name="encoding"/> names another object) over a non-embedded CIDFontType2 with the given /W.</summary>
    private static string Type0(string w, string encoding = "/Identity-H", string toUnicode = "") =>
        $"<< /Type /Font /Subtype /Type0 /BaseFont /Muster /Encoding {encoding} {toUnicode} /DescendantFonts [<< /Type /Font /Subtype /CIDFontType2 " +
        $"/BaseFont /Muster /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /DW 1000 /W {w} >>] >>";

    /// <summary>
    /// 9.7.4.3: a CID given more than once in /W takes its first width — CID 1 twice in lists (500, then 900), CID 2 first in a range (300),
    /// then in a list (700). Before, the later list won (docs/FEHLERGEDAECHTNIS.md, No. 770).
    /// </summary>
    [Fact]
    public void CidWidths_TheFirstSpecificationCounts()
    {
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            Type0("[1 [500] 1 [900] 2 2 300 2 [700] 3 [400]]"));
        using var doc = PdfDocument.Open(file);
        var font = PdfKnead.Core.Pdf.Fonts.PdfFont.Get((PdfKnead.Core.Pdf.Objects.PdfDictionary)doc.GetObject(3), doc);
        Assert.Equal(0.5, font.Glyph(1, 2).Width, 6);
        Assert.Equal(0.3, font.Glyph(2, 2).Width, 6);
        Assert.Equal(0.4, font.Glyph(3, 2).Width, 6);
        Assert.Equal(1.0, font.Glyph(4, 2).Width, 6);
    }

    /// <summary>
    /// 9.7.6.2 and 9.7.6.3: codespace ranges match byte by byte; a code that matches none takes the length of the range with the longest partial
    /// match. CMap with one-byte codes 00–80 and two-byte codes 8140–9FFC: the bytes 81 20 41 are a broken two-byte code (81 starts it, 20 is
    /// outside 40–FC) and the one-byte code 41 ("A"). Before, the shortest length was taken: 81 alone, then 20 read as a code of its own ("X").
    /// </summary>
    [Fact]
    public void Codespace_BrokenCode_TakesTheLongestPartialMatch()
    {
        const string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Muster def 2 begincodespacerange <00> <80> " +
                            "<8140> <9FFC> endcodespacerange 1 begincidrange <00> <80> 1 endcidrange 1 begincidrange <8140> <9FFC> 200 endcidrange " +
                            "endcmap CMapName currentdict /CMap defineresource pop end end";
        const string toUnicode = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 2 begincodespacerange <00> <80> <8140> <9FFC> " +
                                 "endcodespacerange 2 beginbfchar <20> <0058> <41> <0041> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end";
        const string content = "BT /F1 12 Tf 10 10 Td <812041> Tj ET";
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            Type0("[]", "6 0 R", "/ToUnicode 7 0 R"),
            $"<< /Type /CMap /CMapName /Muster /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /Length {cmap.Length} >>\nstream\n{cmap}\nendstream",
            $"<< /Length {toUnicode.Length} >>\nstream\n{toUnicode}\nendstream");
        using var doc = PdfDocument.Open(file);
        var text = string.Concat(PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text));
        Assert.DoesNotContain("X", text);
        Assert.EndsWith("A", text);
    }

    /// <summary>
    /// 9.7.5.3 (Table 118): /UseCMap of an embedded CMap — here a stream with CMap A (code 41 → CID 5) — supplies what CMap B (42 → CID 6) does
    /// not repeat; with the operator "usecmap" in B and without it. Widths per CID tell which CID a code selected. Before, 41 became CID 0
    /// (width /DW 1000) (docs/FEHLERGEDAECHTNIS.md, No. 772).
    /// </summary>
    [Theory]
    [InlineData("/A usecmap ")]
    [InlineData("")]
    public void EmbeddedCMap_UseCMapSuppliesTheRest(string operatorUse)
    {
        const string a = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /A def 1 begincodespacerange <00> <FF> " +
                         "endcodespacerange 1 begincidchar <41> 5 endcidchar endcmap CMapName currentdict /CMap defineresource pop end end";
        var b = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /B def " + operatorUse +
                "1 begincidchar <42> 6 endcidchar endcmap CMapName currentdict /CMap defineresource pop end end";
        const string info = "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>";
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            Type0("[5 [300] 6 [400]]", "4 0 R"),
            $"<< /Type /CMap /CMapName /B {info} /UseCMap 5 0 R /Length {b.Length} >>\nstream\n{b}\nendstream",
            $"<< /Type /CMap /CMapName /A {info} /Length {a.Length} >>\nstream\n{a}\nendstream");
        using var doc = PdfDocument.Open(file);
        var font = PdfKnead.Core.Pdf.Fonts.PdfFont.Get((PdfKnead.Core.Pdf.Objects.PdfDictionary)doc.GetObject(3), doc);
        Assert.Equal(0.3, font.Glyph(0x41, 1).Width, 6);
        Assert.Equal(0.4, font.Glyph(0x42, 1).Width, 6);
    }

    /// <summary>
    /// 9.10.3: a ToUnicode CMap may be based on another one through /UseCMap — here B (42 → „Y“) on A (41 → „Z“): „AB“ reads „ZY“. Before,
    /// /UseCMap was not read and 41 had no text (docs/FEHLERGEDAECHTNIS.md, No. 772).
    /// </summary>
    [Fact]
    public void ToUnicode_UseCMapSuppliesTheRest()
    {
        const string a = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 begincodespacerange <00> <FF> endcodespacerange " +
                         "1 beginbfchar <41> <005A> endbfchar endcmap CMapName currentdict /CMap defineresource pop end end";
        const string b = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap 1 beginbfchar <42> <0059> endbfchar endcmap CMapName " +
                         "currentdict /CMap defineresource pop end end";
        const string content = "BT /F1 12 Tf 10 10 Td (AB) Tj ET";
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 6 0 R >>",
            $"<< /UseCMap 7 0 R /Length {b.Length} >>\nstream\n{b}\nendstream",
            $"<< /Length {a.Length} >>\nstream\n{a}\nendstream");
        using var doc = PdfDocument.Open(file);
        Assert.Equal("ZY", string.Concat(PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text)));
    }

    /// <summary>
    /// 9.3.6: with a Type 3 font, modes 3 and 7 show nothing, modes 4 to 7 add nothing to the clip; mode 4 still fills the glyph. The glyph
    /// is a red square of 50 at (20, 20); blue painted after the text object is not clipped.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    public void Type3_ClippingModes_AddNothingToTheClip(int mode)
    {
        using var bitmap = Rendered($"1 0 0 rg BT /T3 50 Tf {mode} Tr 20 20 Td (a) Tj ET 0 0 1 rg 100 100 50 50 re f");
        Assert.Equal(mode == 4, Red(bitmap, 45, 155));
        Assert.True(Blue(bitmap, 125, 75));
    }

    // ---- Nachweise added in the Nacharbeit (12.11.2026) for rows that stood as „umgesetzt“ without one

    /// <summary>
    /// 9.6.2.3: a multiple master font (MMType1) is read like Type 1 — not embedded, the instance name with its underscores stands for the font
    /// and the text follows StandardEncoding (0x27 is ’); embedded, its program is an ordinary Type 1 program and draws the same glyph as the same
    /// font declared /Type1 (M7 row 12).
    /// </summary>
    [Fact]
    public void MultipleMaster_ReadLikeType1()
    {
        Assert.Equal("Ab’", TextOf("BT /F1 12 Tf 10 10 Td (Ab') Tj ET", "<< /Type /Font /Subtype /MMType1 /BaseFont /MyriadMM_400_600_ >>"));

        var pdf = TestPdf.Simple(1);
        var sample = Fonts.Type1Sample.Build();
        var file = pdf.Add(new Core.Pdf.Objects.PdfStream(TestPdf.Dict(("Length1", Core.Pdf.Objects.PdfInteger.Of(sample.Clear)),
            ("Length2", Core.Pdf.Objects.PdfInteger.Of(sample.Encrypted)),
            ("Length3", Core.Pdf.Objects.PdfInteger.Of(sample.Data.Length - sample.Clear - sample.Encrypted))), sample.Data));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("Probe_400_600_")),
            ("Flags", Core.Pdf.Objects.PdfInteger.Of(4)), ("FontBBox", TestPdf.Box(0, 0, 1000, 1000)), ("ItalicAngle", Core.Pdf.Objects.PdfInteger.Of(0)),
            ("Ascent", Core.Pdf.Objects.PdfInteger.Of(800)), ("Descent", Core.Pdf.Objects.PdfInteger.Of(-200)), ("CapHeight", Core.Pdf.Objects.PdfInteger.Of(700)),
            ("StemV", Core.Pdf.Objects.PdfInteger.Of(80)), ("FontFile", TestPdf.Ref(file))));
        Core.Pdf.Objects.PdfDictionary Font(string subtype) => TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N(subtype)),
            ("BaseFont", TestPdf.N("Probe_400_600_")), ("FirstChar", Core.Pdf.Objects.PdfInteger.Of(65)), ("LastChar", Core.Pdf.Objects.PdfInteger.Of(66)),
            ("Widths", new Core.Pdf.Objects.PdfArray([Core.Pdf.Objects.PdfInteger.Of(600), Core.Pdf.Objects.PdfInteger.Of(600)])),
            ("FontDescriptor", TestPdf.Ref(descriptor)));
        var multiple = pdf.Add(Font("MMType1"));
        var single = pdf.Add(Font("Type1"));
        using var doc = PdfDocument.Open(pdf.Build());
        var a = Core.Pdf.Fonts.PdfFont.Get((Core.Pdf.Objects.PdfDictionary)doc.GetObject(multiple)!, doc).Glyph(65, 1);
        var b = Core.Pdf.Fonts.PdfFont.Get((Core.Pdf.Objects.PdfDictionary)doc.GetObject(single)!, doc).Glyph(65, 1);
        Assert.False(a.Outline.IsEmpty);
        Assert.Equal(b.Outline.Bounds, a.Outline.Bounds);
    }
}
