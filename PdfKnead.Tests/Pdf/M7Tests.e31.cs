using System.Diagnostics;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package E31 (findings of package E25): a CID TrueType font that is not embedded and has no /ToUnicode is drawn by the reference readers in
/// two ways — the CID as Unicode, or the CID as a glyph number. PDF-Knead draws one of them and reads the text of what it draws (special rules
/// A210, A258; where the CID selects no glyph of its own it draws the glyph number and reads the CID as Unicode, No. 1245); the search, „Suchen und schwärzen“ and what reads the page for them find a place by the other reading as well, so that nothing
/// stays visible in a reader that shows it the other way (docs/FEHLERGEDAECHTNIS.md, No. 1235). Copying keeps the text of the drawn glyph.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>A page with one string of <paramref name="codes"/> in a font made by <paramref name="font"/>, its page text and the font, with the installed fonts left out.</summary>
    private static (Core.Pdf.Document.PdfDocument Doc, PageText Text) E31Page(string codes, Func<Core.Pdf.Objects.PdfDictionary> font)
    {
        var (doc, _, _) = Iso03PageFont($"BT /F1 20 Tf 20 100 Td <{codes}> Tj ET", _ => font());
        return (doc, PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0])));
    }

    /// <summary>
    /// Security, ISO 32000-2 9.10.2 sentence 11 with A210/A258 (measured in package E25, probe T1): a CID TrueType font that is not embedded,
    /// Adobe-Identity or Adobe-UCS, Identity-H, no /ToUnicode, whose name has a glyph order (Arial): codes 0041 0042 draw the glyphs 65 and 66 of
    /// Arial and read „^_“ as before, but a reference reader draws and reads „AB“ — the search finds the place by „AB“ (also in lower case and with FindAll)
    /// and by „^_“, „Suchen und schwärzen“ removes both glyphs; the second reading is „AB“ and maps back onto the same characters. A CID beyond
    /// the glyph order (U+4E2D) draws nothing and now reads as Unicode, as two of the three readers read it. Before, „AB“ found nothing — the
    /// place stayed visible in that reader after „Suchen und schwärzen“.
    /// </summary>
    [Fact]
    public void E31_NonEmbeddedCidFont_DrawnAsGlyphNumbers_SearchAndRedactionFindTheCidAsUnicode()
    {
        SystemFonts.DisabledForTests = true;
        try
        {
            foreach (var ordering in new[] { "Identity", "UCS" })
            {
                var (doc, text) = E31Page("00410042", () => E25CidFont("Arial", 32, 0, toUnicode: false, ordering: ordering));
                using (doc)
                {
                    Assert.Equal("^_", text.Text);
                    Assert.Equal("AB", text.Alternate?.Text);
                    Assert.Equal(new TextRange(0, 2), text.FromAlternate(new TextRange(0, 2)));
                    var hit = Assert.Single(text.Find("AB"));
                    Assert.Equal(new TextRange(0, 2), hit);
                    Assert.Equal(hit, Assert.Single(text.Find("ab")));
                    Assert.Equal(hit, Assert.Single(text.Find("^_")));
                    Assert.Equal(hit, Assert.Single(text.FindAll(["AB", "XY"])[0]));
                    Assert.Empty(text.Find("XY"));
                    var redacted = Redactor.Apply(doc, doc.Pages[0], text.RedactionAreas(hit));
                    Assert.Equal(2, redacted.RemovedGlyphs);
                    Assert.Equal("^_", redacted.RemovedText);
                }
            }
            var (beyond, beyondText) = E31Page("00414E2D", () => E25CidFont("Arial", 32, 0, toUnicode: false));
            using (beyond)
            {
                Assert.Equal("^\u4E2D", beyondText.Text);
                Assert.Single(beyondText.Find("A\u4E2D"));
            }
        }
        finally
        {
            SystemFonts.DisabledForTests = false;
        }
    }

    /// <summary>
    /// The same with the installed Arial (Windows): the CIDs are glyphs of the installed font (A210), the text „^_“, the search finds „AB“.
    /// </summary>
    [Fact]
    public void E31_NonEmbeddedCidFont_InstalledFont_SearchFindsTheCidAsUnicode()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "arial.ttf");
        if (!OperatingSystem.IsWindows() || !File.Exists(file)) return;
        var (doc, text) = E31Page("00410042", () => E25CidFont("Arial", 32, 0, toUnicode: false));
        using (doc)
        {
            Assert.Equal("^_", text.Text);
            Assert.Equal(new TextRange(0, 2), Assert.Single(text.Find("AB")));
        }
    }

    /// <summary>
    /// The other way round, ISO 32000-2 9.10.3 with A210 (FG 1198, probes U1a–U1f): /ToUnicode /Identity-H as a name reads codes 0041 0042 as „AB“
    /// and draws those characters, but a reference reader draws the CIDs as glyph numbers — through the glyph order of Arial „^_“; the search finds the
    /// place by either, the text stays „AB“. And where the CID selects no glyph of its own (A258: no installed font, no glyph order; probes T9, T13,
    /// T15, T17), two reference readers and PDF-Knead (No. 1245) draw the CIDs as glyph numbers of the substitute in the standard Macintosh order
    /// („^_“), the third reader draws and the text here reads „AB“: found by either as well. Before, only „AB“ was found in both cases.
    /// </summary>
    [Fact]
    public void E31_ReadingsOfTheOtherReaders_ToUnicodeNameAndNoGlyph_FoundByEither()
    {
        SystemFonts.DisabledForTests = true;
        try
        {
            foreach (var font in new Func<Core.Pdf.Objects.PdfDictionary>[]
                     {
                         () => E25CidFont("Arial", 32, 0),                       // /ToUnicode /Identity-H as a name
                         () => E25CidFont("ProbeSans", 32, 0, toUnicode: false), // no glyph: the CID as Unicode is the text
                     })
            {
                var (doc, text) = E31Page("00410042", font);
                using (doc)
                {
                    Assert.Equal("AB", text.Text);
                    Assert.Equal("^_", text.Alternate?.Text);
                    Assert.Equal(new TextRange(0, 2), Assert.Single(text.Find("^_")));
                    Assert.Equal(new TextRange(0, 2), Assert.Single(text.Find("AB")));
                }
            }
        }
        finally
        {
            SystemFonts.DisabledForTests = false;
        }
    }

    /// <summary>
    /// Every other font keeps one reading: an embedded CID TrueType font, a CID font of another registry, a simple font — no second reading, the
    /// search as before.
    /// </summary>
    [Fact]
    public void E31_OtherFonts_NoSecondReading()
    {
        SystemFonts.DisabledForTests = true;
        try
        {
            var (embedded, _, _) = Iso03PageFont("BT /F1 20 Tf 20 100 Td <00240025> Tj ET", pdf => Iso03CidTrueType(pdf, Core.Pdf.Objects.PdfName.Get("Identity-H")));
            using (embedded)
                Assert.Null(PageText.Create(PageInterpreter.BuildText(embedded, embedded.Pages[0])).Alternate);
            var (other, otherText) = E31Page("00410042", () => E25CidFont("Arial", 32, 0, toUnicode: false, registry: "Probe", ordering: "Probe"));
            using (other)
                Assert.Null(otherText.Alternate);
            var (simple, _) = InterpreterTests.Page("BT /F1 20 Tf 20 100 Td (AB) Tj ET",
                _ => TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica"))))))));
            using (simple)
            {
                var text = PageText.Create(PageInterpreter.BuildText(simple, simple.Pages[0]));
                Assert.Null(text.Alternate);
                Assert.Single(text.Find("AB"));
            }
        }
        finally
        {
            SystemFonts.DisabledForTests = false;
        }
    }

    /// <summary>
    /// Security: the second reading stays linear on crafted pages — 200 000 overprinted copies of one glyph whose second readings alternate
    /// (read once here, 200 000 characters there: every hit there stands for the one glyph here) and a glyph with a replacement text of 200 000
    /// characters: the searches give one hit each and end within seconds (no search back or along per hit).
    /// </summary>
    [Fact]
    public void E31_SecondReading_LinearOnCraftedPages()
    {
        const int n = 200_000;
        var copies = new TextGlyph[n];
        for (var i = 0; i < n; i++) copies[i] = new TextGlyph("x", 10, 10, 20, 10, 20, 22, 10, 22, 12, true, Alternate: i % 2 == 0 ? "a" : "b");
        var long_ = new TextGlyph[]
        {
            new(new string('a', n), 10, 10, 200, 10, 200, 22, 10, 22, 12, true),
            new("x", 210, 10, 220, 10, 220, 22, 210, 22, 12, true, Alternate: "y"),
        };
        var watch = Stopwatch.StartNew();
        var text = PageText.Create(copies);
        Assert.Equal("x", text.Text);
        Assert.Equal(new TextRange(0, 1), Assert.Single(text.Find("a")));
        Assert.Equal(new TextRange(0, 1), Assert.Single(text.FindAll(["b"])[0]));
        var longText = PageText.Create(long_);
        Assert.Equal(n, longText.Find("a").Count);   // each character of the replacement text, once
        Assert.Single(longText.Find("y"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
    }
}
