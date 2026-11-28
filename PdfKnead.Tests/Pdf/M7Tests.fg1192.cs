using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, FG 1192 (security): text with unpaired surrogates — left by an insertion inside a surrogate pair of text read from a file — is laid out,
/// drawn, broken hard and saved without an exception; the text stays as it is.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>The safe reading: an unpaired surrogate, an empty text and an index outside read as U+FFFD; valid characters as themselves.</summary>
    [Fact]
    public void Fg1192_TextRunes_UnpairedSurrogatesReadAsReplacement()
    {
        Assert.Equal(0xFFFD, TextRunes.First("\uD800a").Value);
        Assert.Equal(0xFFFD, TextRunes.First("\uDC00").Value);
        Assert.Equal(0xFFFD, TextRunes.Last("a\uD800").Value);
        Assert.Equal(0xFFFD, TextRunes.Last("a\uDC00").Value);
        Assert.Equal(0xFFFD, TextRunes.First("").Value);
        Assert.Equal(0xFFFD, TextRunes.RuneAt("ab", 5).Value);
        Assert.Equal(0x10400, TextRunes.First("\U00010400x").Value);
        Assert.Equal(0x10400, TextRunes.Last("x\U00010400").Value);
        Assert.Equal('b', TextRunes.RuneAt("ab", 1).Value);
    }

    /// <summary>
    /// A page whose embedded font (CID TrueType, no /ToUnicode) covers a character beyond the BMP (the probe of FG 1187): its block, edited as
    /// the user interface does it — text inserted at every place, also inside the surrogate pair, which leaves unpaired surrogates in a run of
    /// the font of the file —
    /// and laid out in widths down to a hard break inside every word, is drawn, its glyphs removed and the page saved without an exception
    /// (before, the appearance and the hard break threw ArgumentException: fuzz fuzz-m7b, about 145 samples, stage „textedit“).
    /// </summary>
    [Fact]
    public void Fg1192_UnpairedSurrogatesInEditedForeignText_LaidOutAndSavedWithoutException()
    {
        var data = E20KanaProbe().Pdf;   // an embedded CID TrueType font whose cmap covers a character beyond the BMP
        using var doc = PdfDocument.Open(data);
        var page = PageItem.From(doc)[0];
        var block = Assert.Single(TextBlocks.Find(doc, page.Page, CancellationToken.None));
        var original = EditedText.FromBlock(doc, block);
        Assert.Contains(original.Content.Text, char.IsSurrogate);   // (a character beyond the BMP the font of the file covers)
        foreach (var insert in new[] { "W", "\u0301", "\uD83D\uDE00", "\u0628", "\u0915\u093F", "\uD83D" })
            for (var at = 0; at <= original.Content.Length; at++)
                foreach (var width in new[] { 400.0, 20.0, 4.0 })
                {
                    var element = original with { Content = original.Content.Insert(at, insert) };
                    element = element with { Rect = element.Rect with { Right = element.Rect.Left + width } };
                    element = RichLayout.Fitted(element);
                    _ = ElementAppearance.For(element);
                }
        var removed = Redactor.RemoveGlyphs(doc, page.Page, block.Glyphs.ToHashSet(), block.Paths.ToHashSet())!;
        var at3 = original.Content.Text.ToList().FindIndex(char.IsHighSurrogate) + 1;   // inside the pair
        var edited = original with { Content = original.Content.Insert(at3, "W") };
        using var output = new MemoryStream();
        PdfWriter.Write(doc, [page.WithEditedPage(removed, [.. page.Elements, edited])], output);
        using var again = PdfDocument.Open(output.ToArray());
        Assert.NotEmpty(again.Pages);
    }

}
