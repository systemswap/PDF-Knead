using System.Diagnostics;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, the decisions on UAX #53 (Arabic mark rendering, revision 10; docs/FEHLERGEDAECHTNIS.md, No. 1180): the saved page reads the marks as
/// typed (/ActualText around a reordered cluster, read by the text extraction as two of the three reference readers do), a precomposed letter is
/// drawn decomposed where the order puts a mark inside its own and the font has the parts, sequences over 30 non-starters are ordered in pieces
/// of 30 (UAX #15), and the backspace key deletes the outermost mark (in <c>ArabicMarkBackspaceUiTests</c>). Recomposing after the reordering
/// (3.2 sentences 9–11) is done for display where the font has the composed letter (No. 1193).
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (E16)

    /// <summary>
    /// Liberation Sans with a cmap for the Arabic blocks — U+0600 … U+06FF to glyphs 100 …, U+0870 … U+08FF to 400 … — CGJ (600), the alef
    /// overlay U+10EFC (601) and the space, and without layout tables: the glyphs come in the order the shaper gives the characters.
    /// <paramref name="withoutAlef"/> leaves U+0627 out (a font without the parts of alef-madda).
    /// </summary>
    private static byte[] E16Font(bool withoutAlef = false) => ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
    {
        ["cmap"] = withoutAlef
            ? E11Cmap((' ', ' ', Ot02G(' ')), (0x034F, 0x034F, 600), (0x0600, 0x0626, 100), (0x0628, 0x06FF, 140), (0x0870, 0x08FF, 400), (0x10EFC, 0x10EFC, 601))
            : E11Cmap((' ', ' ', Ot02G(' ')), (0x034F, 0x034F, 600), (0x0600, 0x06FF, 100), (0x0870, 0x08FF, 400), (0x10EFC, 0x10EFC, 601)),
        ["GSUB"] = null, ["GPOS"] = null, ["GDEF"] = null, ["kern"] = null,
    });

    private static readonly Lazy<OpenTypeFont> E16Arabic = new(() => OpenTypeFont.Parse(E16Font()));

    private static readonly Lazy<OpenTypeFont> E16ArabicWithoutAlef = new(() => OpenTypeFont.Parse(E16Font(withoutAlef: true)));

    /// <summary>The characters of the glyphs in drawing order (from the glyph numbers of <see cref="E16Font"/>).</summary>
    internal static string E16Drawn(string text, bool withoutAlef = false) =>
        string.Concat(TextShaper.For((withoutAlef ? E16ArabicWithoutAlef : E16Arabic).Value).Shape(text).Select(g => g.Glyph switch
        {
            >= 100 and < 356 => char.ConvertFromUtf32(0x0600 + g.Glyph - 100),
            >= 400 and < 544 => char.ConvertFromUtf32(0x0870 + g.Glyph - 400),
            600 => "\u034F",
            601 => "\U00010EFC",
            _ => g.Glyph == Ot02G(' ') ? " " : "?",
        }));

    // ------------------------------------------------------------ question 67: pieces of 30

    /// <summary>
    /// UAX53 3.2 sentences 5–8 with UAX #15 (stream-safe text format, 30 non-starters): a sequence longer than 30 non-starters is ordered in pieces
    /// of 30, as if a CGJ stood after every 30th — beh with fifteen pairs of kasra and shadda and one more kasra draws fifteen shaddas, fifteen
    /// kasras and the last kasra on its own; the mark a precomposed letter holds counts in the first piece (alef-hamza with fifteen pairs: 29
    /// typed marks in the first piece, the last shadda alone); two sequences of 20 apart from a CGJ are ordered each. The text stays as typed, and
    /// 60 000 marks are shaped within the work budget. Before, a sequence of more than 30 stayed as typed.
    /// </summary>
    [Fact]
    public void E16_Amtra_LongSequencesOrderedInChunksOfThirty()
    {
        var twenty = string.Concat(Enumerable.Repeat("\u0650\u0651", 10));
        var ordered = new string('\u0651', 10) + new string('\u0650', 10);
        Assert.Equal("\u0628" + ordered + "\u034F" + ordered, E16Drawn("\u0628" + twenty + "\u034F" + twenty));
        var long31 = "\u0628" + string.Concat(Enumerable.Repeat("\u0650\u0651", 15)) + "\u0650";
        Assert.Equal("\u0628" + new string('\u0651', 15) + new string('\u0650', 15) + "\u0650", E16Drawn(long31));
        var withHamza = "\u0623" + string.Concat(Enumerable.Repeat("\u0650\u0651", 15));
        Assert.Equal("\u0623" + new string('\u0651', 14) + new string('\u0650', 15) + "\u0651", E16Drawn(withHamza));
        // three pieces: 30, 30 and 1
        var long61 = "\u0628" + string.Concat(Enumerable.Repeat("\u0650\u0651", 30)) + "\u0650";
        var piece = new string('\u0651', 15) + new string('\u0650', 15);
        Assert.Equal("\u0628" + piece + piece + "\u0650", E16Drawn(long61));
        Assert.Equal(long61, string.Concat(TextShaper.For(E16Arabic.Value).Shape(long61).Select(g => g.Text)));
        // effort
        var many = "\u0628" + string.Concat(Enumerable.Repeat("\u0650\u0651", 30_000));
        var watch = Stopwatch.StartNew();
        var shaped = TextShaper.For(E16Arabic.Value).Shape(many);
        Assert.True(watch.ElapsedMilliseconds < 5000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
        Assert.Equal(many.Length, shaped.Length);
        Assert.Equal(many, string.Concat(shaped.Select(g => g.Text)));
    }

    // ------------------------------------------------------------ question 66: precomposed letters

    /// <summary>
    /// UAX53 2 sentence 3, 3.2 sentences 5 and 12–13, 5.1: canonically equivalent text looks the same — where AMTRA puts a typed mark inside the
    /// mark a precomposed letter holds and the font has the letter's parts, the letter is drawn decomposed: alef-madda with superscript alef as
    /// alef, superscript alef, madda (the order the decomposed text gets; the skipped composition of 3.2 sentence 13 is not shown), alef-madda
    /// with fatha as alef, fatha, madda. Where the letter's mark stays innermost, the letter keeps its glyph (alef-madda with hamza above,
    /// alef-hamza with fatha and shadda). Without the parts in the font the letter keeps its glyph and the marks after it are ordered as after the
    /// decomposed letter. In Noto Sans Arabic and Noto Naskh Arabic both spellings give the same glyphs at the same places; the text stays as
    /// typed. Before, the letter's mark always stayed in its glyph, innermost.
    /// </summary>
    [Fact]
    public void E16_Amtra_PrecomposedBase_DecomposedWhereTheFontHasTheParts()
    {
        Assert.Equal("\u0627\u0670\u0653", E16Drawn("\u0622\u0670"));
        Assert.Equal("\u0627\u0670\u0653", E16Drawn("\u0627\u0653\u0670"));
        Assert.Equal("\u0627\u064E\u0653", E16Drawn("\u0622\u064E"));
        Assert.Equal("\u0627\u064E\u0653\u0654", E16Drawn("\u0622\u064E\u0654"));
        Assert.Equal("\u0622\u0654", E16Drawn("\u0622\u0654"));
        Assert.Equal("\u0623\u0651\u064E", E16Drawn("\u0623\u064E\u0651"));
        Assert.Equal("\u0628 \u0627\u0670\u0653\u0628", E16Drawn("\u0628 \u0622\u0670\u0628"));
        Assert.Equal("\u0622\u0670", E16Drawn("\u0622\u0670", withoutAlef: true));
        Assert.Equal("\u0622\u064E\u0654", E16Drawn("\u0622\u064E\u0654", withoutAlef: true));
        foreach (var file in new[] { "NotoSansArabic-Regular.ttf", "NotoNaskhArabic-Regular.ttf" })
        {
            var shaper = TextShaper.For(EmbeddedFonts.ArabicOpenType(file));
            foreach (var (precomposed, decomposed) in new[] { ("\u0622\u0670", "\u0627\u0670\u0653"), ("\u0622\u064E", "\u0627\u064E\u0653"), ("\u0628\u0622\u0670", "\u0628\u0627\u0670\u0653") })
            {
                var first = shaper.Shape(precomposed);
                Assert.Equal(E11Drawn(shaper.Shape(decomposed)), E11Drawn(first));
                Assert.Equal(precomposed, string.Concat(first.Select(g => g.Text)));
            }
        }
    }

    // ------------------------------------------------------------ question 65: recomposition for display (No. 1193)

    /// <summary>
    /// UAX53 3.2 sentences 9–13 (Kann; done since No. 1193): after the reordering the letter is composed with the mark immediately after it
    /// where the font has the composed letter — yeh with hamza above is drawn with the glyph of U+0626, also with a fatha after it (the hamza, a
    /// modifier mark, stays next to the letter); no mark is skipped (alef, superscript alef, madda stays as ordered); the glyph shows the text as
    /// typed. Before, yeh and hamza kept their glyphs.
    /// </summary>
    [Fact]
    public void E16_Amtra_Recomposition_WithTheMarkImmediatelyAfter()
    {
        Assert.Equal("\u0626", E16Drawn("\u064A\u0654"));
        Assert.Equal("\u0626\u064E", E16Drawn("\u064A\u0654\u064E"));
        Assert.Equal("\u0626\u064E", E16Drawn("\u0626\u064E"));
        Assert.Equal("\u0627\u0670\u0653", E16Drawn("\u0627\u0670\u0653"));
        Assert.Equal(["\u064A\u0654", "\u064E"], TextShaper.For(E16Arabic.Value).Shape("\u064A\u0654\u064E").Select(g => g.Text));
    }

    // ------------------------------------------------------------ question 63: the text of the saved page

    /// <summary>
    /// UAX53 0 sentences 13–16, 5.6 sentences 1–2: a reordered sequence becomes one cluster with its letter — beh, kasra, shadda is drawn beh,
    /// shadda, kasra, and its first glyph carries the text as typed, the others none, all three in the cluster of the beh (in a longer text
    /// counted in the whole text); a sequence that keeps its order leaves each glyph its own character. Before, each reordered glyph kept its
    /// character, so the text read in drawing order was the reordered one.
    /// </summary>
    [Fact]
    public void E16_Amtra_ReorderedMarks_OneClusterWithTheTypedText()
    {
        var shaper = TextShaper.For(E16Arabic.Value);
        Assert.Equal("\u0628\u0651\u0650", E16Drawn("\u0628\u0650\u0651"));
        var shaped = shaper.Shape("\u0628\u0650\u0651");
        Assert.Equal(["\u0628\u0650\u0651", "", ""], shaped.Select(g => g.Text));
        Assert.All(shaped, g => Assert.Equal(0, g.Cluster));
        Assert.Equal(["\u0628", "\u064E"], shaper.Shape("\u0628\u064E").Select(g => g.Text));
        var longer = shaper.Shape("\u0644\u064E\u0627 \u0628\u0650\u0651");
        Assert.Equal(["\u0644", "\u064E", "\u0627", " ", "\u0628\u0650\u0651", "", ""], longer.Select(g => g.Text));
        Assert.Equal([0, 1, 2, 3, 4, 4, 4], longer.Select(g => g.Cluster));
    }

    /// <summary>
    /// UAX53 0 sentences 13–16 (Soll: the stored text is not changed by the display): the appearance of own formatted text writes a reordered
    /// cluster within one /ActualText with the text as typed (ISO 32000-2, 14.9.4) — beh, kasra, shadda as „/Span &lt;&lt;/ActualText
    /// &lt;FEFF062806500651&gt;&gt;&gt; BDC“ —, and so does plain text. Before, no replacement text was written and the glyphs read the marks in
    /// drawing order.
    /// </summary>
    [Fact]
    public void E16_Amtra_Appearance_ActualTextAroundTheCluster()
    {
        var rich = System.Text.Encoding.Latin1.GetString(ElementAppearance.For(UniTestsElement("a \u0628\u0650\u0651 c")).Form.RawData!);
        Assert.Contains("/Span <</ActualText <FEFF062806500651>>> BDC", rich, StringComparison.Ordinal);
        var plain = new TextElement { Rect = new PdfRectangle(0, 0, 300, 40), Text = "\u0628\u0650\u0651" };
        var content = System.Text.Encoding.Latin1.GetString(ElementAppearance.For(plain).Form.RawData!);
        Assert.Contains("/Span <</ActualText <FEFF062806500651>>> BDC", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// UAX53 0 sentences 13–16, 5.6 sentences 1–2: own text saved and read back from the page gives the marks as typed — beh with kasra and shadda
    /// (drawn shadda first, and with the shadda-kasra form of the font), beh with fatha and shadda next to two behs (the glyph of beh also
    /// outside the cluster), lam-alef with fatha and shadda, and alef-madda with fatha (drawn decomposed). Before, the text read the marks in
    /// drawing order, partly on lines of their own.
    /// </summary>
    [Fact]
    public void E16_Amtra_ExtractedText_AsTyped()
    {
        foreach (var stored in new[]
                 {
                     "a \u0628\u0650\u0651 c", "a \u0628\u064E\u0651 \u0628\u0628 c", "a \u0644\u064E\u0651\u0627 c", "a \u0622\u064E c",
                 })
            Assert.Equal(stored, UniTestsElementLine(stored));
    }

    /// <summary>
    /// UAX53 0 sentences 13–16 with the block comparison of No. 1177: own text with reordered marks is the same block again after saving and
    /// opening — the cluster drawn right to left starts with its marks inside the /ActualText; its letter, the first glyph that advances, takes
    /// the text.
    /// </summary>
    [Fact]
    public void E16_Amtra_ReopenedAsTheSameText()
    {
        foreach (var stored in new[] { "a \u0628\u0650\u0651 c", "\u0628\u064E\u0651 \u0628\u064E", "\u0644\u064E\u0651\u0627 \u0622\u064E", "\u0628\u0651\u0650\u0628 \u0628\u0628" })
        {
            using var doc = PdfDocument.Open(UniTestsSaved(UniTestsElement(stored)));
            var element = Assert.Single(PageItem.From(doc)[0].Elements.OfType<RichTextElement>());
            Assert.Equal(stored, element.Content.Text);
        }
    }

    /// <summary>
    /// UAX53 0 sentences 13–16, read back: in own text „a \u0628\u0650\u0651 c“ saved, the search finds the letter with its marks as typed on one hit, and
    /// „Suchen und schwärzen“ of it removes the glyphs of the cluster (letter, dot, marks) and nothing of „a“ and „c“. Before, the typed text was
    /// not found (the page read the marks in drawing order).
    /// </summary>
    [Fact]
    public void E16_Amtra_SearchAndRedact_HitsTheClusterGlyphs()
    {
        using var doc = PdfDocument.Open(UniTestsSaved(UniTestsElement("a \u0628\u0650\u0651 c")));
        var page = doc.Pages[0];
        var text = PageText.Create(PageInterpreter.BuildText(doc, page));
        var hit = Assert.Single(text.Find("\u0628\u0650\u0651"));
        var redacted = Redactor.Apply(doc, page, text.RedactionAreas(hit));
        Assert.True(redacted.RemovedGlyphs >= 3, $"{redacted.RemovedGlyphs} {redacted.RemovedText}");
        Assert.DoesNotContain("a", redacted.RemovedText);
        Assert.DoesNotContain("c", redacted.RemovedText);
    }

    // ------------------------------------------------------------ No. 1182: marks of right-to-left letters read back

    /// <summary>The page text of own plain text (a text element in Liberation Sans) saved, from the line that holds „a“.</summary>
    private static string E16PlainLine(string stored)
    {
        RichFonts.SystemFontsOff = true;
        try
        {
            using var blank = PdfDocument.Open(TestPdf.Simple(1).Build());
            using var output = new MemoryStream();
            Core.Pdf.Writing.PdfWriter.Write(blank, [PageItem.From(blank)[0].WithElements([new TextElement { Rect = new PdfRectangle(0, 0, 300, 40), Text = stored }])], output);
            return Assert.Single(UniTestsPageText(output.ToArray()).Split('\n'), l => l.Contains('a', StringComparison.Ordinal)).Trim();
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
    }

    /// <summary>
    /// UNI 9.1.1 sentences 73–85, 9.2.1 sentences 42–47 (reading own text; docs/FEHLERGEDAECHTNIS.md, No. 1182): plain text with Hebrew points
    /// is read back from the saved page with each mark after its letter — bet with sheva and meteg, shin with shin dot and qamats; the
    /// appearance writes the letter with its stacked marks within one /ActualText. Beh, kasra, shadda (drawn with the placeholder glyph, the
    /// plain text font has no Arabic) reads as typed as a cluster of reordered marks. Before, the extraction put the marks before their letter.
    /// (Formatted text: <c>Uni04_OwnTextWithMarks_ExtractedWithEachMarkAfterItsBase</c>.)
    /// </summary>
    [Fact]
    public void E16_PlainText_MarksReadAfterTheirLetter()
    {
        foreach (var stored in new[] { "a \u05D1\u05B0\u05BD c", "a \u05E9\u05C1\u05B8 c", "a \u0628\u0650\u0651 c" })
            Assert.Equal(stored, E16PlainLine(stored));
    }

    // ------------------------------------------------------------ /ActualText read

    /// <summary>A one-page file (300 × 200, Helvetica as F1) with the given content.</summary>
    private static byte[] E16File(string content) => DocumentStructureNormTests.FileOf(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 5 0 R >> /Properties << /P1 << /ActualText (NM) >> >> >> /Contents 4 0 R >>",
        $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    /// <summary>The page text of a file with the given content, with the glyphs read.</summary>
    private static PageText E16Read(string content)
    {
        using var doc = PdfDocument.Open(E16File(content));
        return PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0]));
    }

    /// <summary>
    /// ISO 32000-2, 14.9.4 sentences 5–7 (measured m7-e16, probes f1–f5: two of three reference readers read the replacement text, the third the glyphs): a
    /// marked-content sequence with /ActualText reads as its text in place of the characters drawn within it — „bc“ under „XY“ reads „a XY d“, an
    /// empty replacement hides the hyphen („a b“), a named property list counts as an inline one, the inner of two nested ones is part of the outer
    /// („a XY d“), a language escape is left out, and the text lies on the quadrilateral of all its characters (two glyphs drawn apart, the second
    /// first: one glyph whose quadrilateral reaches over both, the line not taller than the letters that advance). Before, the replacement text
    /// was ignored.
    /// </summary>
    [Fact]
    public void E16_ActualText_ReadAsReplacement()
    {
        Assert.Equal("a XY d", E16Read("BT /F1 24 Tf 50 100 Td (a ) Tj /Span <</ActualText (XY)>> BDC (bc) Tj EMC ( d) Tj ET").Text);
        Assert.Equal("a b", E16Read("BT /F1 24 Tf 50 100 Td (a ) Tj /Span <</ActualText ()>> BDC (-) Tj EMC (b) Tj ET").Text);
        Assert.Equal("a NM d", E16Read("BT /F1 24 Tf 50 100 Td (a ) Tj /Span /P1 BDC (bc) Tj EMC ( d) Tj ET").Text);
        Assert.Equal("a XY d", E16Read("BT /F1 24 Tf 50 100 Td (a ) Tj /Span <</ActualText (XY)>> BDC (b) Tj /Span <</ActualText (Q)>> BDC (c) Tj EMC EMC ( d) Tj ET").Text);
        Assert.Equal("a XY d", E16Read("BT /F1 24 Tf 50 100 Td (a ) Tj /Span <</ActualText <FEFF001B0064006500 1B00580059>>> BDC (bc) Tj EMC ( d) Tj ET").Text);
        var apart = E16Read("BT /F1 24 Tf 50 100 Td (a ) Tj ET /Span <</ActualText (MN)>> BDC BT /F1 24 Tf 100 110 Td (') Tj ET BT /F1 24 Tf 95 100 Td (o) Tj ET EMC BT /F1 24 Tf 120 100 Td (z) Tj ET");
        Assert.Equal("a MN z", apart.Text);
        var mn = Assert.Single(apart.GlyphsOf(Assert.Single(apart.Find("MN"))));
        var bounds = new TextQuad(mn.X0, mn.Y0, mn.X1, mn.Y1, mn.X2, mn.Y2, mn.X3, mn.Y3).Bounds;
        Assert.InRange(bounds.Left, 94.5, 95.5);
        Assert.True(bounds.Right > 104, bounds.Right.ToString());
        Assert.InRange(Math.Min(bounds.Top, bounds.Bottom), 100 + 24 * -0.25, 100 + 24 * -0.15);
        // a sequence left open ends with the page content
        Assert.Equal("a XY", E16Read("BT /F1 24 Tf 50 100 Td (a ) Tj /Span <</ActualText (XY)>> BDC (bc) Tj ET").Text);
    }
}
