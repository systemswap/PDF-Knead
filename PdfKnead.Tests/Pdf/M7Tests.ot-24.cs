using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-24: OpenType GSUB 21.1–21.3 (overview of the substitution types, processing of the lookups), checked on
/// <see cref="TextShaper"/> with GSUB tables built into Liberation Sans (parts and helpers of the ot-03 tests). The helpers named Ot24 are
/// used by the ot-25 and ot-26 tests as well.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// A layout table header with several scripts, each with a default language system naming the feature indices given, and the GSUB
    /// version 1.<paramref name="minor"/> (for 1.1 a NULL FeatureVariations offset).
    /// </summary>
    private static Ot03Part Ot24Layout(Ot03Part[] lookups, (string Tag, int[] Lookups)[] features, (string Script, int[] Features)[] scripts,
        int minor = 0)
    {
        var featureList = new Ot03Part().U16(features.Length);
        foreach (var (tag, indices) in features) featureList.Tag(tag).Off(new Ot03Part().U16(0, indices.Length).U16(indices));
        var scriptList = new Ot03Part().U16(scripts.Length);
        foreach (var (tag, indices) in scripts.OrderBy(s => s.Script, StringComparer.Ordinal))
            scriptList.Tag(tag).Off(new Ot03Part().Off(new Ot03Part().U16(0, 0xFFFF, indices.Length).U16(indices)).U16(0));
        var lookupList = new Ot03Part().U16(lookups.Length);
        foreach (var lookup in lookups) lookupList.Off(lookup);
        var header = new Ot03Part().U16(1, minor).Off(scriptList).Off(featureList).Off(lookupList);
        if (minor >= 1) header.U32(0);
        return header;
    }

    /// <summary>Single substitution lookup (type 1, format 2) by glyph IDs.</summary>
    private static Ot03Part Ot24Single(params (int From, int To)[] pairs)
    {
        var sorted = pairs.OrderBy(p => p.From).ToArray();
        return Ot03Lookup(1, 0, new Ot03Part().U16(2).Off(Ot03Cov([.. sorted.Select(p => p.From)])).U16(sorted.Length).U16([.. sorted.Select(p => p.To)]));
    }

    /// <summary>Single substitution subtable of format 1: the glyphs covered plus <paramref name="delta"/>.</summary>
    private static Ot03Part Ot24Delta(int delta, params int[] glyphs) => new Ot03Part().U16(1).Off(Ot03Cov(glyphs)).U16(delta);

    /// <summary>An extension subtable (type 7, format 1) of the lookup type given around <paramref name="inner"/> (32-bit offset).</summary>
    private static Ot03Part Ot24Extension(int type, Ot03Part inner) => new Ot03Part().U16(1, type).Off32(inner);

    /// <summary>Text whose characters map to the glyphs given in Liberation Sans (the first character with that glyph).</summary>
    private static string Ot24Text(params int[] glyphs) =>
        string.Concat(glyphs.Select(g => char.ConvertFromUtf32(Enumerable.Range(0x21, 0xD000).First(c => Ot03Font.GlyphForUnicode(c) == g))));

    // ------------------------------------------------------------ 21.1

    /// <summary>
    /// OT 21.1 sentences 14–16: single substitution replaces one glyph by another. One type 1 lookup under „liga“ with a format 1 subtable
    /// (coverage {10}, deltaGlyphID +3) and a format 2 subtable (coverage {5, 9}, substitutes [50, 90]) turns the glyphs 10, 5, 9 into 13,
    /// 50, 90; glyph 65535 (made by a lookup before) plus 3 gives 2 (modulo 65536).
    /// </summary>
    [Fact]
    public void Ot24_SingleSubstitution_DeltaFormat1AndListFormat2()
    {
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(1, 0, Ot24Delta(3, 10), new Ot03Part().U16(2).Off(Ot03Cov(5, 9)).U16(2, 50, 90))));
        Assert.Equal([13, 50, 90], Ot03Shape(shaper, Ot24Text(10, 5, 9)));
        var wrap = Ot03Shaper(Ot03Header([Ot24Single((Ot03G('a'), 65535)), Ot03Lookup(1, 0, Ot24Delta(3, 65535))], [("liga", [0, 1])]));
        Assert.Equal([2], Ot03Shape(wrap, "a"));
    }

    /// <summary>
    /// OT 21.1 sentences 17–18: multiple substitution replaces one glyph by several. Type 2 with coverage {a} and the sequence [x, y]:
    /// „ab“ → x, y, b; the text „a“ stays with x, y has none, both keep cluster 0. A sequence of length 0 (deleting, not allowed) leaves „ab“.
    /// </summary>
    [Fact]
    public void Ot24_MultipleSubstitution_SequenceWithTextAndCluster()
    {
        TextShaper Shaper(params char[] sequence) => Ot03Shaper(Ot03Liga(Ot03Lookup(2, 0, new Ot03Part().U16(1).Off(Ot03Cov(Ot03G('a'))).U16(1)
            .Off(new Ot03Part().U16(sequence.Length).U16([.. sequence.Select(Ot03G)])))));
        var shaped = Shaper('x', 'y').Shape("ab");
        Assert.Equal(Ot03Glyphs("xyb"), shaped.Select(g => g.Glyph).ToArray());
        Assert.Equal(["a", "", "b"], shaped.Select(g => g.Text).ToArray());
        Assert.Equal([0, 0, 1], shaped.Select(g => g.Cluster).ToArray());
        Assert.Equal(Ot03Glyphs("ab"), Ot03Shape(Shaper(), "ab"));
    }

    /// <summary>
    /// OT 21.1 sentences 19–22, OT 21.3 sentences 26 and 31 (type 3), OT 21.4.4 sentences 1, 4–8, 10–16, 17–20 (deliberately so): alternate
    /// substitution offers alternatives to choose from; PDF-Knead offers no choice of alternates and keeps the default glyph of the cmap,
    /// which sentence 22 allows. A type 3 lookup (coverage {&amp;} or {a}, three alternatives in an AlternateSet) under „salt“ switched on,
    /// or under „liga“, leaves „&amp;“ and „a“ as their cmap glyphs.
    /// </summary>
    [Theory]
    [InlineData("salt", '&')]
    [InlineData("liga", 'a')]
    public void Ot24_AlternateSubstitution_Type3_KeepsTheCmapGlyph(string feature, char c)
    {
        var alternates = new Ot03Part().U16(3).U16(Ot03G('x'), Ot03G('y'), Ot03G('z'));
        var lookup = Ot03Lookup(3, 0, new Ot03Part().U16(1).Off(Ot03Cov(Ot03G(c))).U16(1).Off(alternates));
        var shaper = Ot03Shaper(Ot03Header([lookup], [(feature, [0])]));
        Assert.Equal([Ot03G(c)], Ot03Shape(shaper, c.ToString(), [feature]));
    }

    /// <summary>
    /// OT 21.1 sentence 25: a ligature deletes the other glyphs of its sequence, not those skipped because of the lookup flags. GDEF makes
    /// the combining acute a mark (class 3); the ligature f i → X with IGNORE_MARKS (8) turns „f, acute, i“ into X followed by the mark;
    /// without the flag the mark breaks the sequence and nothing changes.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(0)]
    public void Ot24_LigatureWithIgnoreMarks_KeepsTheSkippedMark(int flag)
    {
        var acute = Ot03Font.GlyphForUnicode('\u0301');
        Assert.NotEqual(0, acute);
        var gdef = new Ot03Part().U16(1, 0).Off(Ot03Classes((acute, 3))).U16(0, 0, 0).Build();
        var shaper = Ot03Shaper(Ot03Liga(Ot03Ligature("fi", 'X', flag)), gdef);
        int[] expected = flag == 8 ? [Ot03G('X'), acute] : [Ot03G('f'), acute, Ot03G('i')];
        Assert.Equal(expected, Ot03Shape(shaper, "f\u0301i"));
    }

    /// <summary>
    /// OT 21.1 sentences 29–32: chained contextual substitution adds a backtrack and a lookahead around the input. Format 3 with backtrack
    /// {x}, input {a}, lookahead {y} and the nested a→b: „xay“ → „xby“; „xaz“, „xa“ (no lookahead at the end of the run) and „ay“ (no
    /// backtrack at its start) stay. Format 1 with the same rule leaves „ay“ as well.
    /// </summary>
    [Fact]
    public void Ot24_ChainedContext_BacktrackAndLookaheadAtTheEdgesOfTheRun()
    {
        int x = Ot03G('x'), a = Ot03G('a'), y = Ot03G('y');
        var format3 = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, Ot03ChainF3([[x]], [[a]], [[y]], (0, 1))), Ot03Single(('a', 'b'))));
        Assert.Equal(Ot03Glyphs("xby"), Ot03Shape(format3, "xay"));
        foreach (var text in new[] { "xaz", "xa", "ay" }) Assert.Equal(Ot03Glyphs(text), Ot03Shape(format3, text));
        var format1 = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, Ot03ChainF1(Ot03Cov(a), Ot03Set(Ot03ChainRule([x], [], [y], (0, 1))))), Ot03Single(('a', 'b'))));
        Assert.Equal(Ot03Glyphs("xby"), Ot03Shape(format1, "xay"));
        Assert.Equal(Ot03Glyphs("ay"), Ot03Shape(format1, "ay"));
    }

    /// <summary>
    /// OT 21.1 sentences 33–34, OT 21.3 sentences 26 and 31 (type 8), OT 21.4.9 sentences 1–6, 7–11, 12–16, 17–20, 22–31: reverse chaining
    /// single substitution runs from the end of the run to its start (docs/FEHLERGEDAECHTNIS.md, No. 1161; before passed over). A type 8 lookup
    /// (ReverseChainSingleSubstFormat1, coverage {a}, lookahead {b}, substitute x) under „calt“ turns „aab“ into „axb“ (the first a sees the x
    /// already made); with the lookahead {b, x} into „xxb“ — processed from the start it would give „axb“; without lookahead „a“ becomes „x“;
    /// with backtrack {c} „cab“ becomes „cxb“ and „dab“ stays.
    /// </summary>
    [Fact]
    public void Ot24_ReverseChaining_Type8_FromTheEndOfTheRun()
    {
        int a = Ot03G('a'), b = Ot03G('b'), c = Ot03G('c'), x = Ot03G('x');
        TextShaper Shaper(int[] backtrack, int[] lookahead)
        {
            var sub = new Ot03Part().U16(1).Off(Ot03Cov(a)).U16(backtrack.Length == 0 ? 0 : 1);
            if (backtrack.Length > 0) sub.Off(Ot03Cov(backtrack));
            sub.U16(lookahead.Length == 0 ? 0 : 1);
            if (lookahead.Length > 0) sub.Off(Ot03Cov(lookahead));
            return Ot03Shaper(Ot03Header([Ot03Lookup(8, 0, sub.U16(1, x))], [("calt", [0])]));
        }
        Assert.Equal(Ot03Glyphs("axb"), Ot03Shape(Shaper([], [b]), "aab"));
        Assert.Equal(Ot03Glyphs("xxb"), Ot03Shape(Shaper([], [b, x]), "aab"));
        Assert.Equal(Ot03Glyphs("x"), Ot03Shape(Shaper([], []), "a"));
        Assert.Equal(Ot03Glyphs("cxb"), Ot03Shape(Shaper([c], [b]), "cab"));
        Assert.Equal(Ot03Glyphs("dab"), Ot03Shape(Shaper([c], [b]), "dab"));
    }

    /// <summary>
    /// OT 21.4.9 sentences 1–6: type 8 substitutes one glyph and runs over the whole run backwards, so it is not applied as a nested lookup of a
    /// context. A chained context (format 3, input {a}) whose record names a type 8 lookup (coverage {a} → x) leaves „ab“; the type 8 lookup
    /// reached from the extension type (7) applies.
    /// </summary>
    [Fact]
    public void Ot24_ReverseChaining_NotNested_ButAsExtension()
    {
        int a = Ot03G('a'), x = Ot03G('x');
        var reverse = new Ot03Part().U16(1).Off(Ot03Cov(a)).U16(0, 0, 1, x);
        var nested = Ot03Shaper(Ot03Header([Ot03Lookup(6, 0, Ot03ChainF3([], [[a]], [], (0, 1))), Ot03Lookup(8, 0, reverse)], [("calt", [0])]));
        Assert.Equal(Ot03Glyphs("ab"), Ot03Shape(nested, "ab"));
        var extension = Ot03Shaper(Ot03Header([Ot03Lookup(7, 0, Ot24Extension(8, new Ot03Part().U16(1).Off(Ot03Cov(a)).U16(0, 0, 1, x)))], [("calt", [0])]));
        Assert.Equal(Ot03Glyphs("xb"), Ot03Shape(extension, "ab"));
    }

    /// <summary>
    /// OT 21.1 sentences 35–37 and OT 21.4.8 sentences 5–11: the extension type reaches subtables over 32-bit offsets. A type 7 lookup with
    /// two extension subtables, both of extensionLookupType 1 (a→b; c→d), the second subtable placed more than 65 535 bytes behind its
    /// extension: „ac“ → „bd“ — both are applied (before FG M5-8 only the first).
    /// </summary>
    [Fact]
    public void Ot24_Extension_TwoSubtables_OneBeyond64K()
    {
        int a = Ot03G('a'), b = Ot03G('b'), c = Ot03G('c'), d = Ot03G('d');
        // the padding hangs behind the first inner subtable (an offset past its fields, not read), the second one is placed after it
        var padding = new Ot03Part().U16(new int[36_000]);
        var first = new Ot03Part().U16(2).Off(Ot03Cov(a)).U16(1, b).Off(padding);
        var second = new Ot03Part().U16(2).Off(Ot03Cov(c).AtEnd()).U16(1, d).AtEnd();
        var gsub = Ot03Liga(Ot03Lookup(7, 0, Ot24Extension(1, first), Ot24Extension(1, second)));
        var shaper = Ot03Shaper(gsub);
        Assert.True(second.Position - first.Position > 0xFFFF);
        Assert.Equal(Ot03Glyphs("bd"), Ot03Shape(shaper, "ac"));
    }

    // ------------------------------------------------------------ 21.3

    /// <summary>
    /// OT 21.3 sentence 10 (docs/FEHLERGEDAECHTNIS.md, No. 1162): the current script is taken from the text. A GSUB with „liga“ for latn
    /// (Cyrillic а → X) and „liga“ for cyrl (а → Y): the Cyrillic „а“ becomes Y, the lookups of cyrl (before X: everything but Arabic ran
    /// under latn).
    /// </summary>
    [Fact]
    public void Ot24_ScriptFromTheText_CyrillicIsSetUnderCyrl()
    {
        var cyrillicA = Ot03Font.GlyphForUnicode('а');
        Assert.NotEqual(0, cyrillicA);
        var gsub = Ot24Layout([Ot24Single((cyrillicA, Ot03G('X'))), Ot24Single((cyrillicA, Ot03G('Y')))],
            [("liga", [0]), ("liga", [1])], [("latn", [0]), ("cyrl", [1])]);
        Assert.Equal(Ot03Glyphs("Y"), Ot03Shape(Ot03Shaper(gsub), "а"));
    }

    /// <summary>
    /// OT 21.3 sentences 19, 21–25, 27–30 and OT 21.4.8 sentence 13 (today's behaviour for the second part): all subtables of a lookup have
    /// one type. Type 7 with two extension subtables of type 1 (a→b, c→d): „ac“ → „bd“. A font against the rule (the second extension of
    /// type 4, a ligature c d → L) is read with the type of the first: the ligature subtable as single substitution format 1, its
    /// ligatureSetCount 1 as delta — „acd“ → b, the glyph after c, d; no exception.
    /// </summary>
    [Fact]
    public void Ot24_ExtensionSubtablesOfOneType_MixedOnesReadWithTheFirst()
    {
        int a = Ot03G('a'), b = Ot03G('b'), c = Ot03G('c'), d = Ot03G('d');
        var same = Ot03Shaper(Ot03Liga(Ot03Lookup(7, 0, Ot24Extension(1, Ot24Delta(b - a, a)), Ot24Extension(1, Ot24Delta(d - c, c)))));
        Assert.Equal(Ot03Glyphs("bd"), Ot03Shape(same, "ac"));
        var ligature = new Ot03Part().U16(1).Off(Ot03Cov(c)).U16(1).Off(new Ot03Part().U16(1).Off(new Ot03Part().U16(Ot03G('L'), 2, d)));
        var mixed = Ot03Shaper(Ot03Liga(Ot03Lookup(7, 0, Ot24Extension(1, Ot24Delta(b - a, a)), Ot24Extension(4, ligature))));
        Assert.Equal([b, c + 1, d], Ot03Shape(mixed, "acd"));
    }
}
