using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-25: OpenType GSUB 21.4 (header and the eight lookup types), checked on <see cref="TextShaper"/> with GSUB tables built
/// into Liberation Sans or Noto Sans Arabic (parts of the ot-03 tests, helpers of the ot-24 tests).
/// </summary>
public sealed partial class M7NormTests
{
    private static readonly OpenTypeFont Ot25Arabic = EmbeddedFonts.ArabicOpenType("NotoSansArabic-Regular.ttf");

    private static int Ot25A(int c)
    {
        var g = Ot25Arabic.GlyphForUnicode(c);
        Assert.NotEqual(0, g);
        return g;
    }

    /// <summary>Noto Sans Arabic with the GSUB given (script „arab“) and without GPOS.</summary>
    private static TextShaper Ot25ArabicShaper(Ot03Part gsub) =>
        TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(Ot25Arabic.FileData,
            new Dictionary<string, byte[]?> { ["GSUB"] = gsub.Build(), ["GPOS"] = null })));

    // ------------------------------------------------------------ 21.4.1

    /// <summary>
    /// OT 21.4.1 sentences 11–17: a header of version 1.1 has the fields of 1.0 and minorVersion 1. A GSUB 1.1 with featureVariationsOffset 0
    /// and „liga“ for „a a“ → X gives the ligature as the same GSUB of version 1.0 does.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Ot25_Gsub11WithoutFeatureVariations_ReadLikeVersion10(int minor)
    {
        var gsub = Ot24Layout([Ot03Ligature("aa", 'X')], [("liga", [0])], [("latn", [0])], minor);
        Assert.Equal(minor, gsub.Build()[3]);
        Assert.Equal(Ot03Glyphs("X"), Ot03Shape(Ot03Shaper(gsub), "aa"));
    }

    // ------------------------------------------------------------ 21.4.2

    /// <summary>
    /// OT 21.4.2 sentences 1–4, 5–9, 10–14 and 16–20: single substitution format 1 adds deltaGlyphID (int16, the coverage offset from the
    /// subtable) to every covered glyph, modulo 65536; the coverage index is not needed. Coverage {a, c} (not adjacent) with delta +5 shifts
    /// both by 5, one glyph each; a negative delta (b → a) works; glyph 5 with delta −10 gives 65531. A subtable of the undefined format 3
    /// with the same fields is not applied (the draft described it as read as format 2; fixed since No. 1144).
    /// </summary>
    [Fact]
    public void Ot25_SingleFormat1_DeltaModulo65536()
    {
        int a = Ot03G('a'), b = Ot03G('b'), c = Ot03G('c');
        Assert.True(c - a > 1);
        var plus5 = Ot03Shaper(Ot03Liga(Ot03Lookup(1, 0, Ot24Delta(5, a, c))));
        Assert.Equal([a + 5, b, c + 5], Ot03Shape(plus5, "abc"));
        var back = Ot03Shaper(Ot03Liga(Ot03Lookup(1, 0, Ot24Delta(a - b, b))));
        Assert.Equal([a], Ot03Shape(back, "b"));
        var below = Ot03Shaper(Ot03Liga(Ot03Lookup(1, 0, Ot24Delta(-10, 5))));
        Assert.Equal([65531], Ot03Shape(below, Ot24Text(5)));
        var format3 = Ot03Shaper(Ot03Liga(Ot03Lookup(1, 0, new Ot03Part().U16(3).Off(Ot03Cov(a)).U16(1, b))));
        Assert.Equal([a], Ot03Shape(format3, "a"));
    }

    /// <summary>
    /// OT 21.4.2 sentences 21–24 and 28–33: single substitution format 2 takes the substitute at the coverage index of the input glyph. Three
    /// covered glyphs a, c, e with the substitutes X, Y, Z — with a coverage list and with a coverage of one range a–e (indices from the
    /// range start, so the substitutes stand at 0, 2, 4): „abcde“ → „XbYdZ“.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ot25_SingleFormat2_SubstituteAtTheCoverageIndex(bool range)
    {
        int a = Ot03G('a'), e = Ot03G('e');
        Assert.Equal(4, e - a);
        int[] glyphs = Ot03Glyphs("ace"), substitutes = Ot03Glyphs("XYZ");
        var sub = range
            ? new Ot03Part().U16(2).Off(new Ot03Part().U16(2, 1, a, e, 0)).U16(5).U16(substitutes[0], Ot03G('b'), substitutes[1], Ot03G('d'), substitutes[2])
            : new Ot03Part().U16(2).Off(Ot03Cov(glyphs)).U16(3).U16(substitutes);
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(1, 0, sub)));
        Assert.Equal(Ot03Glyphs("XbYdZ"), Ot03Shape(shaper, "abcde"));
    }

    // ------------------------------------------------------------ 21.4.3

    /// <summary>
    /// OT 21.4.3 sentences 1–2: multiple substitution, measured on Noto Sans Arabic — its „ccmp“ (type 2) splits the isolated beh (glyph 606)
    /// into the glyphs 14 and 316 (body and dot); the first keeps the text „ب“, the second has none.
    /// </summary>
    [Fact]
    public void Ot25_NotoArabicCcmp_SplitsBehIntoTwoGlyphs()
    {
        Assert.Equal(606, Ot25A(0x0628));
        var shaped = TextShaper.For(Ot25Arabic).Shape("ب");
        Assert.Equal([14, 316], shaped.Select(g => g.Glyph).ToArray());
        Assert.Equal(["ب", ""], shaped.Select(g => g.Text).ToArray());
    }

    /// <summary>
    /// OT 21.4.3 sentences 3–7, 11–12, 14–20 and 21–25: MultipleSubstFormat1 names one Sequence per coverage index, the sequence holds
    /// glyphCount glyphs in its order. Coverage {a, b} with the sequences [x] and [x, y, z]: „ab“ → „xxyz“; with sequenceCount 1 the second
    /// covered glyph b has no sequence and stays („ab“ → „xb“); a sequence of glyphCount 0 for a changes nothing and b is not skipped (it is
    /// still substituted by its own sequence).
    /// </summary>
    [Fact]
    public void Ot25_MultipleSubstitution_OneSequencePerCoverageIndex()
    {
        int a = Ot03G('a'), b = Ot03G('b');
        Assert.True(a < b);
        TextShaper Shaper(int count, params int[][] sequences)
        {
            var sub = new Ot03Part().U16(1).Off(Ot03Cov(a, b)).U16(count);
            foreach (var s in sequences) sub.Off(new Ot03Part().U16(s.Length).U16(s));
            return Ot03Shaper(Ot03Liga(Ot03Lookup(2, 0, sub)));
        }
        Assert.Equal(Ot03Glyphs("xxyz"), Ot03Shape(Shaper(2, Ot03Glyphs("x"), Ot03Glyphs("xyz")), "ab"));
        Assert.Equal(Ot03Glyphs("xb"), Ot03Shape(Shaper(1, Ot03Glyphs("x")), "ab"));
        Assert.Equal(Ot03Glyphs("ayz"), Ot03Shape(Shaper(2, [], Ot03Glyphs("yz")), "ab"));
    }

    /// <summary>
    /// OT 21.4.3 sentences 8–10: the output sequence is in writing order — for right-to-left text the rightmost glyph first. Noto Sans Arabic
    /// with „ccmp“ (type 2) beh → [teh, theh]: the shaper gives teh before theh in logical order; placed by the Unicode Bidirectional
    /// Algorithm (levels of the glyphs from their cluster, as the appearance of right-to-left lines does) theh stands left of teh.
    /// </summary>
    [Fact]
    public void Ot25_MultipleSubstitution_RightToLeft_LogicalThenVisualOrder()
    {
        int beh = Ot25A(0x0628), teh = Ot25A(0x062A), theh = Ot25A(0x062B);
        var multiple = Ot03Lookup(2, 0, new Ot03Part().U16(1).Off(Ot03Cov(beh)).U16(1).Off(new Ot03Part().U16(2, teh, theh)));
        var shaped = Ot25ArabicShaper(Ot03Header([multiple], [("ccmp", [0])], "arab")).Shape("ب");
        Assert.Equal([teh, theh], shaped.Select(g => g.Glyph).ToArray());
        var levels = Bidi.Levels("ب");
        var order = Bidi.VisualOrder([.. shaped.Select(g => levels[g.Cluster])]);
        Assert.Equal([theh, teh], order.Select(k => shaped[k].Glyph).ToArray());
    }

    // ------------------------------------------------------------ 21.4.5

    /// <summary>
    /// OT 21.4.5 sentence 19: the order of the ligatures in a LigatureSet is their precedence. Set for a with [a a a → X] before [a a → Y]:
    /// „aaa“ → X; in the other order → Y and a.
    /// </summary>
    [Theory]
    [InlineData(true, "X")]
    [InlineData(false, "Ya")]
    public void Ot25_LigatureSet_OrderIsPrecedence(bool longFirst, string expected)
    {
        int a = Ot03G('a');
        Ot03Part three = new Ot03Part().U16(Ot03G('X'), 3, a, a), two = new Ot03Part().U16(Ot03G('Y'), 2, a);
        var set = new Ot03Part().U16(2).Off(longFirst ? three : two).Off(longFirst ? two : three);
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(4, 0, new Ot03Part().U16(1).Off(Ot03Cov(a)).U16(1).Off(set))));
        Assert.Equal(Ot03Glyphs(expected), Ot03Shape(shaper, "aaa"));
    }

    /// <summary>
    /// OT 21.4.5 sentences 28–30: the components stand in logical order, for right-to-left text the rightmost first. Noto Sans Arabic (its own
    /// lam-alef is made of connecting forms, not one ligature glyph) with „rlig“: ligature lam, alef → teh. „لا“ (lam before alef in logical
    /// order, alef left of lam on the page) becomes teh; „ال“ stays.
    /// </summary>
    [Fact]
    public void Ot25_ArabicLigature_ComponentsInLogicalOrder()
    {
        int lam = Ot25A(0x0644), alef = Ot25A(0x0627), teh = Ot25A(0x062A);
        var set = new Ot03Part().U16(1).Off(new Ot03Part().U16(teh, 2, alef));
        var ligature = Ot03Lookup(4, 0, new Ot03Part().U16(1).Off(Ot03Cov(lam)).U16(1).Off(set));
        var shaper = Ot25ArabicShaper(Ot03Header([ligature], [("rlig", [0])], "arab"));
        Assert.Equal([teh], Ot03Shape(shaper, "لا"));
        Assert.Equal([alef, lam], Ot03Shape(shaper, "ال"));
    }

    // ------------------------------------------------------------ 21.4.6

    /// <summary>
    /// OT 21.4.6 sentences 9–10: the pattern is matched before any substitution; the substitutions change the sequence, not the match.
    /// Format 3 {a}{b} with the records (0 → a→b) and (1 → b→c): „ab“ → „bc“ — the b made at place 0 is not hit by the second record, the
    /// pattern is not matched again.
    /// </summary>
    [Fact]
    public void Ot25_Context_SubstitutionsDoNotChangeTheMatch()
    {
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, Ot03ContextF3([Ot03Gl('a'), Ot03Gl('b')], (0, 1), (1, 2))),
            Ot03Single(('a', 'b')), Ot03Single(('b', 'c'))));
        Assert.Equal(Ot03Glyphs("bc"), Ot03Shape(shaper, "ab"));
    }

    /// <summary>
    /// OT 21.4.6 sentences 24–25, 30–31: format 1 is a context of a fixed glyph sequence (SequenceContextFormat1). Rule „a b c“ with the
    /// records (0 → a→c) and (2 → c→a): „abc“ → „cba“.
    /// </summary>
    [Fact]
    public void Ot25_ContextFormat1_TwoSingleSubstitutionsReverseAbc()
    {
        var sub = Ot03ContextF1(Ot03Cov(Ot03G('a')), Ot03Set(Ot03Rule(Ot03Glyphs("bc"), (0, 1), (2, 2))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, sub), Ot03Single(('a', 'c')), Ot03Single(('c', 'a'))));
        Assert.Equal(Ot03Glyphs("cba"), Ot03Shape(shaper, "abc"));
    }

    /// <summary>
    /// OT 21.4.6 sentences 43–49, 56–57: format 3 holds one coverage per place, a glyph may stand in several of them. Coverages {a}, {a, b}
    /// with (0 → a→x): „aa“ → „xa“, „ab“ → „xb“, „ba“ stays.
    /// </summary>
    [Fact]
    public void Ot25_ContextFormat3_GlyphInSeveralCoverages()
    {
        int a = Ot03G('a'), b = Ot03G('b');
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, Ot03ContextF3([[a], [a, b]], (0, 1))), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs("xa"), Ot03Shape(shaper, "aa"));
        Assert.Equal(Ot03Glyphs("xb"), Ot03Shape(shaper, "ab"));
        Assert.Equal(Ot03Glyphs("ba"), Ot03Shape(shaper, "ba"));
    }

    // ------------------------------------------------------------ 21.4.8

    /// <summary>
    /// OT 21.4.8 sentences 1–4 and 14–17: the reader proceeds as if the lookup had the type of the extension and the subtable stood in its
    /// place. „liga“ as type 7 around a ligature subtable (f i → X) gives the same as the plain ligature lookup; a single substitution in an
    /// extension, reached as the nested lookup of a context ({a}{b}, record (1 → b→y)), turns „ab“ into „ay“.
    /// </summary>
    [Fact]
    public void Ot25_Extension_ActsAsItsSubtable()
    {
        int f = Ot03G('f');
        Ot03Part Ligature() => new Ot03Part().U16(1).Off(Ot03Cov(f)).U16(1).Off(new Ot03Part().U16(1).Off(new Ot03Part().U16(Ot03G('X'), 2, Ot03G('i'))));
        var plain = Ot03Shaper(Ot03Liga(Ot03Lookup(4, 0, Ligature())));
        var wrapped = Ot03Shaper(Ot03Liga(Ot03Lookup(7, 0, Ot24Extension(4, Ligature()))));
        Assert.Equal(Ot03Glyphs("X"), Ot03Shape(plain, "fi"));
        Assert.Equal(Ot03Shape(plain, "fi"), Ot03Shape(wrapped, "fi"));
        var single = new Ot03Part().U16(2).Off(Ot03Cov(Ot03G('b'))).U16(1, Ot03G('y'));
        var nested = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, Ot03ContextF3([Ot03Gl('a'), Ot03Gl('b')], (1, 1))), Ot03Lookup(7, 0, Ot24Extension(1, single))));
        Assert.Equal(Ot03Glyphs("ay"), Ot03Shape(nested, "ab"));
    }

    /// <summary>
    /// OT 21.4.8 sentence 12: extensionLookupType must not be 7. An extension of type 7 around a single substitution (a→b) is not applied —
    /// „a“ stays and shaping ends.
    /// </summary>
    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void Ot25_ExtensionOfType7_IsNotApplied()
    {
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(7, 0, Ot24Extension(7, Ot24Delta(Ot03G('b') - Ot03G('a'), Ot03G('a'))))));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(Ot03Glyphs("a"), Ot03Shape(shaper, "a"));
        Assert.True(watch.Elapsed.TotalMilliseconds < 1000 * TestKind.Factor);
    }
}
