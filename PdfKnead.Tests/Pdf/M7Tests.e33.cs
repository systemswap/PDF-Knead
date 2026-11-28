using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package E33 (decision on question 79): a CID TrueType font that is not embedded, Adobe-Identity or Adobe-UCS, Identity-H, without
/// /ToUnicode, whose CIDs select no glyph (no installed font, no glyph order; special rule A258) is drawn as two of the three reference readers
/// draw it — the CID as an index into the standard Macintosh glyph order of the substitute — while its text stays the CID read as Unicode
/// (docs/FEHLERGEDAECHTNIS.md, No. 1245). Before, nothing was drawn.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>The font of <see cref="E25CidFont"/> without /ToUnicode, with /W giving CIDs 0041 and 0042 the widths 250 and 750.</summary>
    private static PdfDictionary E33Font(string name, int flags, string ordering = "Identity")
    {
        var font = E25CidFont(name, flags, 0, toUnicode: false, ordering: ordering);
        var cid = (PdfDictionary)font.GetArray(PdfName.Get("DescendantFonts"))!.Get(0)!;
        cid.Set(PdfName.Get("W"), new PdfArray([PdfInteger.Of(0x41), new PdfArray([PdfInteger.Of(250), PdfInteger.Of(750)])]));
        return font;
    }

    /// <summary>
    /// ISO 32000-2 9.7.4.2 and 9.10.2 sentence 11 with A258 (measured in package E31, probes T9, T13, T15, T17, T18): codes 0041 0042 draw the
    /// glyphs „asciicircum“ and „underscore“ (indices 65 and 66 of the standard Macintosh order) of the substitute of the family — Liberation Sans for
    /// „ProbeSans“, Liberation Serif Bold for „ProbeSerif-Bold“ —, advance by /W (250 and 750) and leave ink on the page at their place. The text
    /// stays „AB“, the second reading „^_“; the search finds the place by either and „Suchen und schwärzen“ removes both glyphs. Before, the glyphs
    /// were empty and the page blank.
    /// </summary>
    [Fact]
    public void E33_NonEmbeddedCidFont_NoGlyph_DrawnInTheStandardMacintoshOrder()
    {
        SystemFonts.DisabledForTests = true;
        try
        {
            foreach (var (name, flags, file) in new[] { ("ProbeSans", 32, "LiberationSans-Regular.ttf"), ("ProbeSerif-Bold", 34, "LiberationSerif-Bold.ttf") })
                foreach (var ordering in new[] { "Identity", "UCS" })
                {
                    var substitute = EmbeddedFonts.StandardOpenType(file);
                    var (doc, list, font) = Iso03PageFont("BT /F1 20 Tf 20 100 Td <00410042> Tj ET", _ => E33Font(name, flags, ordering));
                    using (doc)
                    {
                        var circumflex = font.Glyph(0x41, 2);
                        var underscore = font.Glyph(0x42, 2);
                        Assert.True(Iso03SameOutline(substitute.Outline(substitute.GlyphByName("asciicircum")), circumflex.Outline), $"{name} {ordering}: ^");
                        Assert.True(Iso03SameOutline(substitute.Outline(substitute.GlyphByName("underscore")), underscore.Outline), $"{name} {ordering}: _");
                        Assert.False(circumflex.Outline.IsEmpty);
                        Assert.Equal((0.25, 0.75), (circumflex.Width, underscore.Width));
                        Assert.Equal(("A", "^"), (circumflex.Text, circumflex.AlternateText));
                        Assert.Equal("AB", string.Concat(list.Text.Select(t => t.Text)));

                        using var bitmap = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
                        Assert.True(E33Dark(bitmap, 20, 100, 25, 120), $"{name} {ordering}: ^ nicht gezeichnet");    // above the baseline in the first 250
                        Assert.True(E33Dark(bitmap, 25, 90, 40, 100), $"{name} {ordering}: _ nicht gezeichnet");     // below it after the first 250

                        var text = PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0]));
                        Assert.Equal("AB", text.Text);
                        Assert.Equal("^_", text.Alternate?.Text);
                        var hit = Assert.Single(text.Find("AB"));
                        Assert.Equal(hit, Assert.Single(text.Find("^_")));
                        var redacted = Redactor.Apply(doc, doc.Pages[0], text.RedactionAreas(hit));
                        Assert.Equal(2, redacted.RemovedGlyphs);
                        Assert.Equal("AB", redacted.RemovedText);
                    }
                }
        }
        finally
        {
            SystemFonts.DisabledForTests = false;
        }
    }

    /// <summary>
    /// The limits of the order (A258): every CID from 1 to 257 draws the glyph of its name in the standard Macintosh order where the substitute
    /// has it (by name, or by the character of the name), CID 0 (.notdef), CIDs of 258 and more (0102, 4E2D) draw nothing, as before; their text
    /// stays the CID as Unicode (U+4E2D) or none (0). A font of another registry draws nothing and keeps no text, as before.
    /// </summary>
    [Fact]
    public void E33_StandardMacintoshOrder_OnlyCidsBelow258()
    {
        SystemFonts.DisabledForTests = true;
        try
        {
            var sans = EmbeddedFonts.StandardOpenType("LiberationSans-Regular.ttf");
            var (doc, _, font) = Iso03PageFont("", _ => E33Font("ProbeSans", 32));
            using (doc)
            {
                var drawn = 0;
                for (var cid = 1; cid < 258; cid++)
                {
                    var name = FontTables.MacGlyphNames[cid];
                    var gid = sans.GlyphByName(name) is > 0 and var g ? g
                        : GlyphNames.ToUnicode(name) is { Length: > 0 } t && sans.GlyphForText(t) is > 0 and var h ? h : -1;
                    var outline = font.Glyph((uint)cid, 2).Outline;
                    if (gid > 0) Assert.True(Iso03SameOutline(sans.Outline(gid), outline), $"CID {cid} ({name})");
                    else Assert.True(outline.IsEmpty, $"CID {cid} ({name})");
                    if (!outline.IsEmpty) drawn++;
                }
                Assert.True(drawn > 200, drawn.ToString());
                Assert.True(Iso03SameOutline(sans.Outline(sans.GlyphForUnicode('A')), font.Glyph(0x24, 2).Outline));   // index 36: A
                foreach (var cid in new uint[] { 0, 0x102, 0x4E2D })
                    Assert.True(font.Glyph(cid, 2).Outline.IsEmpty, $"CID {cid:X4}");
                Assert.Equal("中", font.Glyph(0x4E2D, 2).Text);
                Assert.Null(font.Glyph(0x4E2D, 2).AlternateText);
            }
            var (other, _, otherFont) = Iso03PageFont("", _ => E25CidFont("ProbeSans", 32, 0, toUnicode: false, registry: "Probe", ordering: "Probe"));
            using (other)
            {
                Assert.True(otherFont.Glyph(0x41, 2).Outline.IsEmpty);
                Assert.True(string.IsNullOrEmpty(otherFont.Glyph(0x41, 2).Text));
            }
        }
        finally
        {
            SystemFonts.DisabledForTests = false;
        }
    }

    /// <summary>Whether a dark pixel lies in the box [x0, x1] × [y0, y1] in page coordinates (y up) of a page rendered at 1 pixel per point.</summary>
    private static bool E33Dark(SKBitmap bitmap, int x0, int y0, int x1, int y1)
    {
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                if (bitmap.GetPixel(x, bitmap.Height - 1 - y).Red < 128) return true;
        return false;
    }
}
