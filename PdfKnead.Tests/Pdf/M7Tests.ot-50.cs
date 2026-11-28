using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Text;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-50 (OpenType feature registry, page 65: the registered features f–j). The optional features of this page stay off by default
/// and apply only when named; the justification alternates are not used (justified lines widen their spaces only); the joining forms isol and
/// fina apply where the joining types of the letters ask for them. The fonts are Liberation Sans with GSUB built by hand (helpers of packages
/// ot-49, ot-02, E11 and E12; Arabic letters through the cmap of the E12 tests).
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package ot-50)

    private const int Ot50Arabic = 0x0600;

    /// <summary>The glyph of an Arabic letter in <see cref="E12Font"/> for the Arabic block.</summary>
    private static int Ot50A(int c) => E12G(Ot50Arabic, c);

    // ------------------------------------------------------------ optional features: off by default

    /// <summary>
    /// The optional single substitutions of page 65, each off by default and applied only when named: OT 65.5 sentences 1–15 (flac: the A with
    /// tilde, precomposed and as a combining tilde — PDF-Knead sets no formulas), OT 65.7 sentences 1–13 (fwid: „A“), OT 65.11 sentences 1–14
    /// (hist: „s“), OT 65.12 sentences 1–16 (hkna: a hiragana), OT 65.15 sentences 1–13 (hojo: a kanji), OT 65.16 sentences 1–13 (hwid: the
    /// full-width A), OT 65.21 sentences 1–16 (jp78), OT 65.22 sentences 1–11 (jp83), OT 65.23 sentences 1–14 (jp90) and OT 65.24 sentences
    /// 1–11 (jp04), each with a kanji. A type 1 lookup to another glyph under the feature in the script of the text: the default shaping keeps
    /// the glyphs of the cmap; with the feature named, the character becomes the other glyph.
    /// </summary>
    [Theory]
    [InlineData("flac", "Ã", "Ã", "latn")]
    [InlineData("flac", "X\u0303", "\u0303", "latn")]   // (X with tilde has no composed character, No. 1197)
    [InlineData("fwid", "A", "A", "latn")]
    [InlineData("hist", "s", "s", "latn")]
    [InlineData("hkna", "あ", "あ", "kana")]
    [InlineData("hojo", "葛", "葛", "hani")]
    [InlineData("hwid", "Ａ", "Ａ", "latn")]
    [InlineData("jp78", "唖", "唖", "hani")]
    [InlineData("jp83", "噌", "噌", "hani")]
    [InlineData("jp90", "啞", "啞", "hani")]
    [InlineData("jp04", "辻", "辻", "hani")]
    public void Ot50_OptionalSingleSubstitution_OffByDefault_AppliedOnlyWhenNamed(string feature, string text, string from, string script) =>
        Ot49_OptionalSingleSubstitution_OffByDefault_AppliedOnlyWhenNamed(feature, text, from, script);

    /// <summary>
    /// OT 65.13 sentences 1–11 (hlig: long s and t into one ligature): an optional ligature, off by default and not applied by PDF-Knead of
    /// itself — the default shaping gives one glyph per character, with the feature named one glyph. (frac applies of itself to digits around
    /// the fraction slash: Ot50_Frac_AppliedToDigitsAroundTheFractionSlash_OtherwiseOnlyWhenNamed, docs/FEHLERGEDAECHTNIS.md, No. 1183.)
    /// </summary>
    [Theory]
    [InlineData("hlig", "ſt")]
    public void Ot50_OptionalLigature_OffByDefault_AppliedOnlyWhenNamed(string feature, string text) =>
        Ot49_OptionalLigature_OffByDefault_AppliedOnlyWhenNamed(feature, text);

    /// <summary>
    /// OT 65.14 sentences 1–18 (hngl, deprecated): a hanja replaced by its hangul only when hngl is named — a type 1 lookup from the hanja to the
    /// glyph of the hangul syllable: the default shaping keeps the hanja, with hngl the hangul glyph; a type 3 lookup offering the hangul as an
    /// alternate keeps the hanja even with hngl named (no choice of alternates).
    /// </summary>
    [Fact]
    public void Ot50_Hngl_HanjaToHangulOnlyWhenNamed_AlternatesNotChosen()
    {
        const string chars = "伽가", hanja = "伽";
        int han = Ot49Glyph(chars, 0x4F3D), hangul = Ot49Glyph(chars, 0xAC00);
        var single = Ot49Font(chars, Ot49Layout(["hani"], [("hngl", E11Sub((han, hangul)))]));
        Assert.Equal([han], Ot49Shape(single, hanja));
        Assert.Equal([hangul], Ot49Shape(single, hanja, "hngl"));
        var alternates = Ot49Font(chars, Ot49Layout(["hani"], [("hngl", Ot49Alternates(han, hangul, Ot49Form))]));
        Assert.Equal([han], Ot49Shape(alternates, hanja, "hngl"));
    }

    // ------------------------------------------------------------ 65.1 falt, 65.20 jalt

    /// <summary>
    /// OT 65.1 sentences 1–12 (falt) and OT 65.20 sentences 1–11 (jalt): justified lines widen their spaces only, no alternate of another width
    /// is used — a font with a type 1 lookup under the feature for a letter of every word (Latin „e“ for falt, Arabic Kaf for jalt): the text
    /// laid out justified has the same lines and glyphs as left aligned, none of them the alternate; every glyph but the spaces keeps its
    /// advance, and in the full lines the spaces grow until the line fills the width.
    /// </summary>
    [Theory]
    [InlineData("falt", "abce", "e")]
    [InlineData("jalt", "كك", "ك")]
    public void Ot50_JustificationAlternates_NotUsed_OnlyTheSpacesWiden(string feature, string word, string letter)
    {
        var c = letter[0];
        var arabic = c >= Ot50Arabic;
        var gsub = arabic
            ? Ot49Layout(["arab"], [(feature, E11Sub((Ot50A(c), Ot49Form)))])
            : Ot49Layout(["latn"], [(feature, E11Sub((Ot02G(c), Ot49Form)))]);
        var font = arabic ? E12Font(Ot50Arabic, gsub) : Ot49Font(word, gsub);
        var text = string.Join(" ", Enumerable.Repeat(word, 12));
        var family = "M7 Ot50 " + feature;
        var left = Ot49LaidOut(family, font, text, 60, BlockAlignment.Left);
        var justified = Ot49LaidOut(family, font, text, 60, BlockAlignment.Justify);
        Assert.True(justified.Lines.Count >= 3, $"{justified.Lines.Count} Zeilen");
        Assert.Equal(left.Lines.Select(l => (l.Start, l.End)), justified.Lines.Select(l => (l.Start, l.End)));
        for (var i = 0; i < justified.Lines.Count; i++)
        {
            var (plain, wide) = (left.Lines[i].Glyphs, justified.Lines[i].Glyphs);
            Assert.Equal(plain.Select(g => g.Glyph), wide.Select(g => g.Glyph));
            Assert.DoesNotContain(Ot49Form, wide.Select(g => g.Glyph));
            for (var k = 0; k < wide.Count; k++)
                if (wide[k].Text != " ") Assert.Equal(plain[k].Advance, wide[k].Advance, 9);
                else Assert.True(wide[k].Advance >= plain[k].Advance - 1e-9);
            if (!justified.Lines[i].EndsParagraph)
            {
                Assert.Equal(60, justified.Lines[i].X + justified.Lines[i].Width, 6);
                Assert.Contains(Enumerable.Range(0, wide.Count), k => wide[k].Text == " " && wide[k].Advance > plain[k].Advance + 1e-6);
            }
        }
    }

    // ------------------------------------------------------------ 65.4 fina, 65.18 isol

    /// <summary>
    /// OT 65.4 sentences 11–21 (fina): the program takes the joining forms from the joining types and heeds the joiners — a type 1 lookup Beh →
    /// other glyph under fina (script arab): of two Behs only the second, the final form, is replaced; with ZERO WIDTH NON-JOINER between them
    /// both stand alone and neither is replaced.
    /// </summary>
    [Fact]
    public void Ot50_Fina_OnlyTheFinalForm_NonJoinerBreaksTheJoin()
    {
        var beh = Ot50A(0x0628);
        var font = E12Font(Ot50Arabic, Ot49Layout(["arab"], [("fina", E11Sub((beh, Ot49Form)))]));
        Assert.Equal([beh, Ot49Form], Ot49Shape(font, "بب"));
        var apart = Ot49Shape(font, "ب\u200Cب");
        Assert.Equal(beh, apart[0]);
        Assert.Equal(beh, apart[^1]);
        Assert.DoesNotContain(Ot49Form, apart);
    }

    /// <summary>
    /// OT 65.18 sentences 13–25 (isol): the isolated form applies only where the letter has no joining neighbour — a type 1 lookup Heh → other
    /// glyph under isol (script arab): a Heh alone is replaced, two Hehs (initial and final form) are not.
    /// </summary>
    [Fact]
    public void Ot50_Isol_OnlyTheIsolatedForm()
    {
        var heh = Ot50A(0x0647);
        var font = E12Font(Ot50Arabic, Ot49Layout(["arab"], [("isol", E11Sub((heh, Ot49Form)))]));
        Assert.Equal([Ot49Form], Ot49Shape(font, "ه"));
        Assert.Equal([heh, heh], Ot49Shape(font, "هه"));
    }
}
