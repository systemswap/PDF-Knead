using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, FG 1211 (OpenType 3.20.2, 31.2.20, 31.2.25): the line spacing of new text is a multiple of the natural line height of its fonts —
/// ascender − descender + line gap, the typographic ones of OS/2 with USE_TYPO_METRICS, else those of hhea; 1.15 times the size where a font
/// gives none that can be used. Blocks and text elements of earlier versions, edited text of the page and form fields keep their spacing as a
/// multiple of the font size; a mark stored with the element tells the two apart.
/// </summary>
public sealed partial class M7NormTests
{
    private static readonly RichStyle Fg1211Sans = new() { Family = "Liberation Sans", Size = 10 };

    /// <summary>The natural line height of Liberation Sans (hhea 1854, −434, 67 of 2048 units; USE_TYPO_METRICS not set), in em units.</summary>
    private const double Fg1211SansHeight = (1854 + 434 + 67) / 2048.0;

    /// <summary>The natural line height of Liberation Mono (hhea 1705, −615, 0 of 2048 units), in em units.</summary>
    private const double Fg1211MonoHeight = (1705 + 615) / 2048.0;

    /// <summary>The distance of the first two baselines of a layout.</summary>
    private static double Fg1211Step(RichLayout layout) => layout.Lines[1].Baseline - layout.Lines[0].Baseline;

    /// <summary>A Liberation Sans copy with fsSelection <paramref name="flags"/> and sTypoAscender/Descender/LineGap given (2048 units per em).</summary>
    private static ElementFace Fg1211Face(int flags, int ascender, int descender, int lineGap) => Ot30Face(Ot30WithOs2(o =>
    {
        Ot30Set(o, 62, flags);
        Ot30Set(o, 68, ascender);
        Ot30Set(o, 70, descender);
        Ot30Set(o, 72, lineGap);
    }, 4));

    /// <summary>
    /// OT 31.2.20 sentences 11, 27–30, 31.2.25 sentences 1–4, 8–10: with USE_TYPO_METRICS the natural line height is sTypoAscender − sTypoDescender
    /// + sTypoLineGap (1600 + 400 + 200 of 2048), without the bit ascender − descender + lineGap of hhea (1854 + 434 + 67) — the typographic values
    /// are then not looked at.
    /// </summary>
    [Fact]
    public void Fg1211_LineHeight_TypoWithUseTypoMetrics_ElseHhea()
    {
        Assert.Equal(2200 / 2048.0, Fg1211Face(0x00C0, 1600, -400, 200).LineHeight, 9);
        Assert.Equal(Fg1211SansHeight, Fg1211Face(0x0040, 1600, -400, 200).LineHeight, 9);
        Assert.Equal(Fg1211SansHeight, ElementFace.For(ElementFont.Sans).LineHeight, 9);
        Assert.Equal(Fg1211MonoHeight, ElementFace.For(ElementFont.Mono).LineHeight, 9);
    }

    /// <summary>
    /// A crafted or broken font (security): line metrics that cannot be used — a height of 14.6 em or 0.29 em, an ascender at the baseline, a
    /// descender above it, no units per em — give 1.15 times the size, so that the lines are neither spread over pages nor put on top of each
    /// other; a negative line gap counts as 0.
    /// </summary>
    [Fact]
    public void Fg1211_LineHeight_UnusableMetrics_Fallback()
    {
        Assert.Equal(1.15, Fg1211Face(0x00C0, 30_000, -400, 0).LineHeight, 9);
        Assert.Equal(1.15, Fg1211Face(0x00C0, 500, -100, 0).LineHeight, 9);
        Assert.Equal(1.15, Fg1211Face(0x00C0, 0, -1600, 400).LineHeight, 9);
        Assert.Equal(1.15, Fg1211Face(0x00C0, 1600, 200, 400).LineHeight, 9);
        Assert.Equal(2000 / 2048.0, Fg1211Face(0x00C0, 1600, -400, -500).LineHeight, 9);
        Assert.Equal(1.15, ElementFace.NaturalLineHeight(800, -200, 0, 0), 9);
        Assert.Equal(1.15, ElementFace.NaturalLineHeight(short.MaxValue, short.MinValue, short.MaxValue, 16), 9);
    }

    /// <summary>
    /// OT 3.20.2 sentences 1–5 (new block): the baselines are the chosen factor times the natural line height of the fonts apart — single (1.0)
    /// in Liberation Sans 10 pt 11.499 points, 1.5 times 17.25 points; a line with an Arabic letter (drawn in the Arabic substitute font, line
    /// height 2.112 em) takes the larger height of its fonts. A block of the earlier kind keeps the factor times the size (1.15: 11.5 points).
    /// </summary>
    [Fact]
    public void Fg1211_NewBlock_SpacedByTheLineHeightOfItsFonts()
    {
        RichFonts.SystemFontsOff = true;
        try
        {
            RichLayout Layout(string text, double lineGap, bool fontLineHeight = true) => RichLayout.Of(new RichTextElement
            {
                Rect = new PdfRectangle(72, 500, 572, 700),
                Content = new RichText(text, Fg1211Sans),
                LineGap = lineGap,
                FontLineHeight = fontLineHeight,
            });
            var block = new RichTextElement { Rect = default, Content = new RichText("x", Fg1211Sans) };
            Assert.True(block.FontLineHeight);
            Assert.Equal(1.0, block.LineGap);
            Assert.Equal(10 * Fg1211SansHeight, Fg1211Step(Layout("Eins\nZwei", 1.0)), 6);
            Assert.Equal(15 * Fg1211SansHeight, Fg1211Step(Layout("Eins\nZwei", 1.5)), 6);
            var arabic = RichFonts.Arabic(0x0628, Fg1211Sans)!.Value.Face.LineHeight;
            Assert.Equal(2.112, arabic, 3);
            Assert.Equal(10 * arabic, Fg1211Step(Layout("Eins\nZwei " + (char)0x0628, 1.0)), 6);
            Assert.Equal(11.5, Fg1211Step(Layout("Eins\nZwei " + (char)0x0628, 1.15, fontLineHeight: false)), 6);
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
    }

    /// <summary>Saves the elements on a blank page (PDF 1.4: the data of the elements stays readable in the file) and reads them back.</summary>
    private static (byte[] File, IReadOnlyList<PageElement> Elements) Fg1211Saved(params PageElement[] elements)
    {
        using var blank = PdfDocument.Open(TestPdf.Simple(1).Build());
        using var output = new MemoryStream();
        PdfWriter.Write(blank, [PageItem.From(blank)[0].WithElements(elements)], output, new PdfSaveOptions { Version = "1.4" });
        var file = output.ToArray();
        using var doc = PdfDocument.Open(file);
        return (file, PageItem.From(doc)[0].Elements);
    }

    /// <summary>
    /// A block of the earlier kind (its spacing 1.15 times the font size, in Liberation Mono, whose line height 1.133 em differs) saved without
    /// the mark — as files of earlier versions have it — keeps its baselines when opened and laid out again; a new block keeps its kind. Before,
    /// there was only the factor times the size.
    /// </summary>
    [Fact]
    public void Fg1211_OldBlock_SameBaselinesAfterSavingAndOpening()
    {
        RichFonts.SystemFontsOff = true;
        try
        {
            var mono = new RichStyle { Family = "Liberation Mono", Size = 10 };
            var old = new RichTextElement
            {
                Rect = new PdfRectangle(72, 600, 400, 700), Content = new RichText("Erste Zeile\nZweite Zeile\nDritte", mono), LineGap = 1.15, FontLineHeight = false,
            };
            var (file, elements) = Fg1211Saved(old);
            Assert.True(file.AsSpan().IndexOf("ZeilenhoeheSchrift"u8) < 0);
            var back = Assert.Single(elements.OfType<RichTextElement>());
            Assert.False(back.FontLineHeight);
            Assert.Equal(1.15, back.LineGap, 6);
            Assert.Equal(RichLayout.Of(old).Lines.Select(l => l.Baseline), RichLayout.Of(back).Lines.Select(l => l.Baseline));
            Assert.Equal(11.5, Fg1211Step(RichLayout.Of(back)), 6);

            var (newFile, newElements) = Fg1211Saved(old with { LineGap = 1.0, FontLineHeight = true });
            Assert.True(newFile.AsSpan().IndexOf("ZeilenhoeheSchrift"u8) >= 0);
            var newBack = Assert.Single(newElements.OfType<RichTextElement>());
            Assert.True(newBack.FontLineHeight);
            Assert.Equal(10 * Fg1211MonoHeight, Fg1211Step(RichLayout.Of(newBack)), 6);
        }
        finally
        {
            RichFonts.SystemFontsOff = false;
        }
    }

    /// <summary>The y of the lines a text element draws (the „Tm“ of its appearance).</summary>
    private static double[] Fg1211LineYs(TextElement text) =>
        [.. Regex.Matches(Encoding.ASCII.GetString(ElementAppearance.For(text).Form.RawData), @"1 0 0 1 \S+ (\S+) Tm")
            .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))];

    /// <summary>
    /// Text elements (Liberation Mono 10 pt, two lines): inserted anew („Text einfügen“) the lines are the natural line height of the font apart
    /// (11.33 points) and the mark is saved with the element; a text element of an earlier version (no mark) keeps 11.5 points after saving and
    /// opening; a form field keeps 11.5 points even with the mark (other programs make its appearance again from the value).
    /// </summary>
    [Fact]
    public void Fg1211_TextElement_NewByLineHeight_OldAndFieldsKeepTheirSpacing()
    {
        var text = new TextElement { Rect = new PdfRectangle(72, 600, 300, 700), Text = "Eins\nZwei", Font = ElementFont.Mono, FontSize = 10 };
        double Step(TextElement t) => Fg1211LineYs(t) is var ys ? ys[0] - ys[1] : 0;
        Assert.Equal(10 * Fg1211MonoHeight, Step(text with { FontLineHeight = true }), 3);
        Assert.Equal(11.5, Step(text), 3);
        Assert.Equal(11.5, Step(text with { FontLineHeight = true, IsField = true, FieldName = "Feld" }), 3);
        Assert.Equal(10 * Fg1211MonoHeight, ElementAppearance.NeededHeight(text with { FontLineHeight = true }) - ElementAppearance.NeededHeight(text with
        {
            FontLineHeight = true, Text = "Eins",
        }), 6);

        var (file, elements) = Fg1211Saved(text, text with { FontLineHeight = true, Rect = new PdfRectangle(72, 400, 300, 500) });
        var back = elements.OfType<TextElement>().ToList();
        Assert.Equal(2, back.Count);
        var old = Assert.Single(back, e => !e.FontLineHeight);
        Assert.Equal(Fg1211LineYs(text), Fg1211LineYs(old));
        Assert.Single(back, e => e.FontLineHeight);
        Assert.True(file.AsSpan().IndexOf("ZeilenhoeheSchrift"u8) >= 0);
    }
}
