using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package uni-17 (Unicode chapter 23, special areas and format characters): the rows of the draft that still asked for a test — own text
/// written without deprecated format or tag characters, the same character in two fonts, noncharacters in /ToUnicode, signatures of other
/// encoding forms in attachments, the interlinear annotation characters and the replacement character.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>A ToUnicode CMap with one-byte codes and the bfchar lines given.</summary>
    private static CMap Uni17ToUnicode(string bfchar) =>
        CMap.Parse(Encoding.ASCII.GetBytes("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n1 begincodespacerange <00> <FF> endcodespacerange\n" +
            $"{bfchar.Split('\n').Length} beginbfchar\n{bfchar}\nendbfchar\nendcmap end end"));

    /// <summary>
    /// UNI 23.2.6 sentences 22–28 and 23.3.1 sentences 6–8 (own texts): PDF-Knead inserts no deprecated format characters and no tags — own
    /// text „abc“ saved is „abc“ in the block data and in the text of the page, without a character of U+206A–U+206F or U+E0001.
    /// </summary>
    [Fact]
    public void Uni17_OwnText_NoDeprecatedFormatOrTagCharactersWritten()
    {
        var saved = UniTestsSaved(UniTestsElement("abc"));
        using (var doc = PdfDocument.Open(saved))
            Assert.Equal("abc", Assert.Single(PageItem.From(doc)[0].Elements.OfType<RichTextElement>()).Content.Text);
        var page = UniTestsPageText(saved);
        Assert.Equal("abc", page.Trim('\r', '\n')[Uni04BlankText.Length..].Trim('\r', '\n'));
        Assert.DoesNotContain(page.EnumerateRunes(), r => r.Value is >= 0x206A and <= 0x206F or 0xE0001);
    }

    /// <summary>
    /// UNI 23.4 sentences 42–44 (own texts): a visible difference is made by the choice of font — the same character „a“ in two spans with
    /// different fonts (Liberation Sans and a font of its own whose cmap gives „a“ glyph 100) is drawn with two faces and two different glyphs.
    /// </summary>
    [Fact]
    public void Uni17_SameCharacterInTwoFonts_TwoDifferentGlyphs()
    {
        var family = "Uni17 " + Guid.NewGuid().ToString("N");
        E11UseFont(family, OpenTypeFont.Parse(E17Font(null, null, ('a', 'a', E12Base))), false);
        var text = new RichText([new RichSpan("a", new RichStyle { Family = "Liberation Sans", Size = 12 }), new RichSpan("a", new RichStyle { Family = family, Size = 12 })]);
        var glyphs = Assert.Single(RichLayout.Layout(text, 1000, BlockAlignment.Left, 1.15, null, 0).Lines).Glyphs;
        Assert.Equal(2, glyphs.Count);
        Assert.NotSame(glyphs[0].Face, glyphs[1].Face);
        Assert.Equal(E12Base, glyphs[1].Glyph);
        Assert.NotEqual(glyphs[0].Glyph, glyphs[1].Glyph);
    }

    /// <summary>
    /// UNI 23.7.1 sentences 8–15 (reader): a noncharacter in /ToUnicode is kept as the text of its code, not replaced — destinations &lt;FFFF&gt;
    /// and &lt;FDD0&gt; give U+FFFF and U+FDD0; a text string with U+FFFF decodes to it as well.
    /// </summary>
    [Fact]
    public void Uni17_NoncharacterInToUnicode_KeptAsText()
    {
        var cmap = Uni17ToUnicode("<01> <FFFF>\n<02> <FDD0>");
        Assert.Equal("\uFFFF", cmap.ToUnicode(1, 1));
        Assert.Equal("\uFDD0", cmap.ToUnicode(2, 1));
        Assert.Equal("a\uFFFF", PdfTextEncoding.Decode([0xFE, 0xFF, 0x00, 0x61, 0xFF, 0xFF]));
    }

    /// <summary>
    /// UNI 23.8.1 sentences 47–56 (reader): PDF-Knead does not read UTF-7 and the other encodings of Table 23-7 — an attachment beginning with
    /// the UTF-7 signature 2B 2F 76 38 and text is searched without an exception, the signature not cut off (the search sees „+/v8Probe“).
    /// </summary>
    [Fact]
    public void Uni17_AttachmentWithUtf7Signature_SearchedUncut()
    {
        byte[] data = [0x2B, 0x2F, 0x76, 0x38, .. Encoding.ASCII.GetBytes("Probe Muster")];
        Assert.Equal(AttachmentSearch.Hit, AttachmentText.Search(data, t => t?.Contains("+/v8Probe", StringComparison.Ordinal) == true));
        Assert.Equal(AttachmentSearch.Hit, AttachmentText.Search(data, t => t?.Contains("Muster", StringComparison.Ordinal) == true));
    }

    /// <summary>
    /// UNI 23.8.3 sentences 26–31 (reader): the interlinear annotation characters are ordinary neutrals (Bidi class ON); the page text keeps a
    /// text with an unpaired terminator unchanged, and copying as plain text passes over unpaired terminators and separators — every other
    /// character stays — and handles nested annotations (the inner annotating text goes with its annotation).
    /// </summary>
    [Fact]
    public void Uni17_AnnotationCharacters_NeutralUnpairedPassedOverNestingAllowed()
    {
        for (var c = 0xFFF9; c <= 0xFFFB; c++) Assert.Equal(BidiClass.ON, Bidi.ClassOf(c));
        Assert.Equal("a\uFFFBb", E20Text("a\uFFFBb").Text);
        Assert.Equal("ab", PageText.ForPlainText("a\uFFFBb"));
        Assert.Equal("ab", PageText.ForPlainText("a\uFFFAb"));
        Assert.Equal("abcf", PageText.ForPlainText("a\uFFF9b\uFFF9c\uFFFAd\uFFFB\uFFFAe\uFFFBf"));
    }

    /// <summary>
    /// UNI 23.8.3 sentences 26–31 (copying): a paragraph break ends an open annotation — „a“ anchor „b“ separator „c“ line break „d“ copied as
    /// plain text keeps the line break and „d“ (the annotating text „c“ goes). Today the copy drops everything after the separator up to a
    /// terminator that never comes, so the text after the paragraph break is lost.
    /// </summary>
    [Fact]
    public void Uni17_AnnotationCharacters_ParagraphBreakEndsAnOpenAnnotation()
    {
        Assert.Equal("ab\nd", PageText.ForPlainText("a\uFFF9b\uFFFAc\nd"));
        Assert.Equal("ab\u2029d", PageText.ForPlainText("a\uFFF9b\uFFFAc\u2029d"));
    }

    /// <summary>
    /// UNI 23.8.4 sentences 6–11 (writer, reader): U+FFFD stands for what cannot be given as text — in PDF/A level a a code without text gets
    /// U+FFFD as its text; a text string ending in an unpaired high surrogate and a ToUnicode destination of an unpaired surrogate decode to
    /// U+FFFD.
    /// </summary>
    [Fact]
    public void Uni17_ReplacementCharacter_ForCodesWithoutTextAndBrokenSurrogates()
    {
        Assert.Equal("\uFFFD", Core.Pdf.Writing.PdfWriter.PdfAText(null, 7, 1, false, accessible: true));
        Assert.Equal("\uFFFD", PdfTextEncoding.Decode([0xFE, 0xFF, 0xD8, 0x00]));
        Assert.Equal("\uFFFD", Uni17ToUnicode("<01> <D800>").ToUnicode(1, 1));
    }
}
