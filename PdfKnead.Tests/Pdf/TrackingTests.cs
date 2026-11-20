using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Tracking of a run (spacing between its characters beyond their advance) as the block detection measures it and editing takes it over
/// (docs/FEHLERGEDAECHTNIS.md, No. 609).
/// </summary>
public sealed class TrackingTests
{
    /// <summary>
    /// A list number „ 2. “ set with a wide word spacing (Tw 21) that a TJ shift after each space takes back — as a report of a bank
    /// writes its notes. The shift lies next to a space and is no tracking; taken as one it made −5.26 pt at 7 pt, and the new text ran
    /// backwards on one spot („PKE0x0“ read „0x0E K P“, found with `speichertest --textbearbeiten` over DocLayNet).
    /// </summary>
    [Fact]
    public void ShiftBesideASpace_IsNoTracking_NewTextRunsForwards()
    {
        using var doc = TextBlockTests.Page(
            "BT /F1 7 Tf 72 700 Td 21.05 Tw [( 2. ) 3007 ( ) 3007] TJ 0 Tw (Average annual salary includes bonus, overtime pay and other benefits.) Tj ET");
        var block = Assert.Single(TextBlocks.Find(doc, doc.Pages[0]));
        Assert.All(block.Lines.SelectMany(l => l.Runs), r => Assert.InRange(r.Tracking, -0.3 * r.Format.Size, 0.3 * r.Format.Size));
        var element = EditedText.FromBlock(doc, block);
        var edited = element with { Content = new RichText("PKE0x0", element.Content.StyleAt(0)) };
        var glyphs = RichLayout.Of(edited).Lines.SelectMany(l => l.Glyphs).ToList();
        Assert.All(glyphs, g => Assert.True(g.Advance > 0, $"„{g.Text}“ advance {g.Advance}"));
        Assert.True(glyphs.Zip(glyphs.Skip(1)).All(p => p.Second.X > p.First.X), "the characters follow each other");
    }

    /// <summary>Real tracking between letters (TJ adjustments of a few units each) is still measured (protection).</summary>
    [Fact]
    public void TrackingBetweenLetters_StillMeasured()
    {
        using var doc = TextBlockTests.Page("BT /F1 10 Tf 72 700 Td [(G) -50 (e) -50 (s) -50 (p) -50 (e) -50 (r) -50 (r) -50 (t)] TJ ET");
        var block = Assert.Single(TextBlocks.Find(doc, doc.Pages[0]));
        Assert.InRange(Assert.Single(block.Lines.SelectMany(l => l.Runs)).Tracking, 0.45, 0.55);
    }
}
