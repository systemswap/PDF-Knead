using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;

namespace PdfKnead.Tests.Fonts;

/// <summary>
/// Arabic script in text the program sets itself (decided on 17.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 910): the joining forms of
/// the Unicode Standard (9.2, ArabicShaping.txt), the OpenType Arabic features applied each where its form stands, marks on ligatures, and the
/// levels and visual order of the Unicode Bidirectional Algorithm (UAX #9). The words are invented examples („salam“, „marhaban“).
/// </summary>
public sealed class ArabicShapingTests
{
    private const string Salam = "سلام";             // seen, lam, alef, meem
    private const string Marhaban = "مرحبا";    // meem, reh, hah, beh, alef

    /// <summary>
    /// The forms from the joining types: seen (dual) begins the word, lam (dual) between seen and alef is medial, alef (right-joining) is final
    /// and does not join the meem after it, which stands isolated; reh (right-joining) breaks a word in the same way, a mark is skipped.
    /// </summary>
    [Fact]
    public void Joining_FormsFromTheJoiningTypes()
    {
        static ArabicJoining.Form[] Forms(string s) => ArabicJoining.Forms([.. s.EnumerateRunes().Select(r => r.Value)]);
        Assert.Equal([ArabicJoining.Form.Initial, ArabicJoining.Form.Medial, ArabicJoining.Form.Final, ArabicJoining.Form.Isolated], Forms(Salam));
        Assert.Equal([ArabicJoining.Form.Initial, ArabicJoining.Form.Final, ArabicJoining.Form.Initial, ArabicJoining.Form.Medial, ArabicJoining.Form.Final],
            Forms(Marhaban));
        // a fatha (mark, transparent) between seen and lam: the letters still join, the mark takes no form
        Assert.Equal([ArabicJoining.Form.Initial, ArabicJoining.Form.None, ArabicJoining.Form.Medial, ArabicJoining.Form.Final, ArabicJoining.Form.Isolated],
            Forms("سَلام"));
        Assert.Equal('D', ArabicJoining.TypeOf(0x0628));
        Assert.Equal('U', ArabicJoining.TypeOf('A'));
    }

    /// <summary>
    /// Shaped with Noto Sans Arabic: the initial seen is another glyph than the isolated one; lam and alef leave their plain forms for the
    /// lam-alef of the required ligatures (this font substitutes both letters by connecting forms rather than one ligature glyph); a fatha on
    /// a beh is attached to it (its base one glyph back, no advance, raised).
    /// </summary>
    [Fact]
    public void Shaping_FormsLigatureAndMarks()
    {
        var font = EmbeddedFonts.ArabicOpenType("NotoSansArabic-Regular.ttf");
        var shaper = TextShaper.For(font);
        var isolated = shaper.Shape("س");
        var word = shaper.Shape(Salam);
        Assert.Single(isolated);
        Assert.NotEqual(isolated[0].Glyph, word[0].Glyph);
        Assert.Equal(font.GlyphForUnicode(0x0633), isolated[0].Glyph);
        Assert.NotEqual(font.GlyphForUnicode(0x0644), word[1].Glyph);
        Assert.NotEqual(font.GlyphForUnicode(0x0627), word[2].Glyph);
        // beh with fatha (the font may split the beh into a body and its dot first): the fatha is attached to a glyph before it
        var marked = shaper.Shape("بَ");
        var fatha = Assert.Single(marked, g => g.Text == "َ");
        Assert.True(fatha.AttachBack >= 1);
        Assert.Equal(0, fatha.XAdvance);
        Assert.NotEqual(0, fatha.YOffset);
    }

    /// <summary>
    /// UAX #9: a paragraph beginning with Arabic is right to left (level 1), its digits get level 2 and keep their order; Latin text in it level
    /// 2; a paragraph beginning with Latin is left to right with the Arabic word at level 1. The visual order reverses the right-to-left runs.
    /// </summary>
    [Fact]
    public void Bidi_LevelsAndVisualOrder()
    {
        var rtl = Bidi.Levels(Salam + " 12");
        Assert.Equal([1, 1, 1, 1, 1, 2, 2], rtl);
        Assert.Equal([5, 6, 4, 3, 2, 1, 0], Bidi.VisualOrder(rtl));
        var ltr = Bidi.Levels("ab " + Salam + " c");
        Assert.Equal([0, 0, 0, 1, 1, 1, 1, 0, 0], ltr);
        Assert.Equal([0, 1, 2, 6, 5, 4, 3, 7, 8], Bidi.VisualOrder(ltr));
        Assert.False(Bidi.HasRightToLeft("Max Mustermann 12"));
        Assert.Equal(')', Bidi.Mirrored('('));
    }
}
