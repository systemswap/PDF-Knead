using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-03: OpenType Common Table Formats 2.8 (contextual and chained contextual lookups), 2.9 (Device and VariationIndex
/// tables) and 2.10 (feature variations), checked on <see cref="TextShaper"/> with GSUB and GPOS tables built into Liberation Sans.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// A part of a font table for the ot-03 tests: big-endian fields and offsets to other parts. <see cref="Build"/> lays the parts out
    /// breadth-first (in the order they are first referenced; parts marked <see cref="AtEnd"/> last) and writes each offset relative to
    /// the part that holds it.
    /// </summary>
    private sealed class Ot03Part
    {
        private readonly List<(int Size, int Value, Ot03Part? Target)> _fields = [];

        private bool _last;

        /// <summary>Position of the part in the table written last by <see cref="Build"/>.</summary>
        public int Position { get; private set; }

        private int Size => _fields.Sum(f => f.Size);

        public Ot03Part U16(params int[] values)
        {
            foreach (var v in values) _fields.Add((2, v & 0xFFFF, null));
            return this;
        }

        public Ot03Part U32(uint value)
        {
            _fields.Add((4, unchecked((int)value), null));
            return this;
        }

        public Ot03Part Tag(string tag) => U16((tag[0] << 8) | tag[1], (tag[2] << 8) | tag[3]);

        /// <summary>A 16-bit offset to <paramref name="target"/>; null writes NULL.</summary>
        public Ot03Part Off(Ot03Part? target)
        {
            _fields.Add((2, 0, target));
            return this;
        }

        /// <summary>A 32-bit offset to <paramref name="target"/>; null writes NULL.</summary>
        public Ot03Part Off32(Ot03Part? target)
        {
            _fields.Add((4, 0, target));
            return this;
        }

        /// <summary>Places this part after all others.</summary>
        public Ot03Part AtEnd()
        {
            _last = true;
            return this;
        }

        public byte[] Build()
        {
            var order = new List<Ot03Part> { this };
            for (var i = 0; i < order.Count; i++)
                foreach (var f in order[i]._fields)
                    if (f.Target is { } t && !order.Contains(t)) order.Add(t);
            order = [this, .. order.Skip(1).Where(p => !p._last), .. order.Skip(1).Where(p => p._last)];
            var position = 0;
            foreach (var part in order)
            {
                part.Position = position;
                position += part.Size;
            }
            var bytes = new byte[position];
            foreach (var part in order)
            {
                var at = part.Position;
                foreach (var (size, value, target) in part._fields)
                {
                    long v = value;
                    if (target is not null)
                    {
                        v = target.Position - part.Position;
                        Assert.InRange(v, 0, size == 2 ? 0xFFFF : int.MaxValue);
                    }
                    for (var k = size - 1; k >= 0; k--) bytes[at++] = (byte)(v >> (8 * k));
                }
            }
            return bytes;
        }
    }

    private static readonly OpenTypeFont Ot03Font = OpenTypeFont.Parse(ShaperLimitTests.Liberation);

    private static int Ot03G(char c)
    {
        var g = Ot03Font.GlyphForUnicode(c);
        Assert.NotEqual(0, g);
        return g;
    }

    private static int[] Ot03Glyphs(string chars) => [.. chars.Select(Ot03G)];

    private static int[] Ot03Shape(TextShaper shaper, string text, IEnumerable<string>? features = null) =>
        [.. shaper.Shape(text, features).Select(g => g.Glyph)];

    /// <summary>A layout table header (GSUB or GPOS) with one script whose default language system names every feature given.</summary>
    private static Ot03Part Ot03Header(Ot03Part[] lookups, (string Tag, int[] Lookups)[] features, string script = "latn",
        Ot03Part? featureVariations = null)
    {
        var featureList = new Ot03Part().U16(features.Length);
        foreach (var (tag, indices) in features) featureList.Tag(tag).Off(new Ot03Part().U16(0, indices.Length).U16(indices));
        var langSys = new Ot03Part().U16(0, 0xFFFF, features.Length).U16([.. Enumerable.Range(0, features.Length)]);
        var scriptList = new Ot03Part().U16(1).Tag(script).Off(new Ot03Part().Off(langSys).U16(0));
        var lookupList = new Ot03Part().U16(lookups.Length);
        foreach (var lookup in lookups) lookupList.Off(lookup);
        var header = new Ot03Part().U16(1, featureVariations is null ? 0 : 1).Off(scriptList).Off(featureList).Off(lookupList);
        if (featureVariations is not null) header.Off32(featureVariations);
        return header;
    }

    /// <summary>A GSUB whose feature „liga“ names lookup 0; the other lookups are reached only from it.</summary>
    private static Ot03Part Ot03Liga(params Ot03Part[] lookups) => Ot03Header(lookups, [("liga", [0])]);

    /// <summary>Liberation Sans with the GSUB given, without GPOS, and with the GDEF given (none if null).</summary>
    private static TextShaper Ot03Shaper(Ot03Part gsub, byte[]? gdef = null) =>
        TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(ShaperLimitTests.Liberation,
            new Dictionary<string, byte[]?> { ["GSUB"] = gsub.Build(), ["GPOS"] = null, ["GDEF"] = gdef })));

    /// <summary>Liberation Sans with the GPOS given, without GSUB and the kern table, its own GDEF kept.</summary>
    private static TextShaper Ot03GposShaper(Ot03Part gpos, params (string Tag, byte[]? Data)[] more)
    {
        var tables = new Dictionary<string, byte[]?> { ["GPOS"] = gpos.Build(), ["GSUB"] = null, ["kern"] = null };
        foreach (var (tag, data) in more) tables[tag] = data;
        return TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, tables)));
    }

    private static Ot03Part Ot03Lookup(int type, int flag, params Ot03Part[] subtables)
    {
        var lookup = new Ot03Part().U16(type, flag, subtables.Length);
        foreach (var s in subtables) lookup.Off(s);
        return lookup;
    }

    /// <summary>Coverage format 1 of the glyphs (sorted).</summary>
    private static Ot03Part Ot03Cov(params int[] glyphs)
    {
        var sorted = glyphs.Distinct().Order().ToArray();
        return new Ot03Part().U16(1, sorted.Length).U16(sorted);
    }

    /// <summary>ClassDef format 2, one range per glyph.</summary>
    private static Ot03Part Ot03Classes(params (int Glyph, int Class)[] classes)
    {
        var part = new Ot03Part().U16(2, classes.Length);
        foreach (var (g, c) in classes.OrderBy(x => x.Glyph)) part.U16(g, g, c);
        return part;
    }

    /// <summary>Single substitution lookup (type 1, format 2) for the pairs of characters given.</summary>
    private static Ot03Part Ot03Single(params (char From, char To)[] pairs)
    {
        var sorted = pairs.Select(p => (From: Ot03G(p.From), To: Ot03G(p.To))).OrderBy(p => p.From).ToArray();
        var sub = new Ot03Part().U16(2).Off(Ot03Cov([.. sorted.Select(p => p.From)])).U16(sorted.Length).U16([.. sorted.Select(p => p.To)]);
        return Ot03Lookup(1, 0, sub);
    }

    /// <summary>Ligature substitution lookup (type 4): the characters → the glyph of <paramref name="ligature"/>.</summary>
    private static Ot03Part Ot03Ligature(string components, char ligature, int flag = 0)
    {
        var lig = new Ot03Part().U16(Ot03G(ligature), components.Length).U16([.. components.Skip(1).Select(Ot03G)]);
        var set = new Ot03Part().U16(1).Off(lig);
        return Ot03Lookup(4, flag, new Ot03Part().U16(1).Off(Ot03Cov(Ot03G(components[0]))).U16(1).Off(set));
    }

    private static Ot03Part Ot03Records(Ot03Part part, (int Seq, int Lookup)[] records)
    {
        foreach (var (seq, lookup) in records) part.U16(seq, lookup);
        return part;
    }

    /// <summary>Context format 3 (type 5): one coverage per input position.</summary>
    private static Ot03Part Ot03ContextF3(int[][] input, params (int Seq, int Lookup)[] records)
    {
        var sub = new Ot03Part().U16(3, input.Length, records.Length);
        foreach (var glyphs in input) sub.Off(Ot03Cov(glyphs));
        return Ot03Records(sub, records);
    }

    /// <summary>SequenceRule / ClassSequenceRule: the input from the second position (glyphs or classes) and the records.</summary>
    private static Ot03Part Ot03Rule(int[] inputRest, params (int Seq, int Lookup)[] records) =>
        Ot03Records(new Ot03Part().U16(inputRest.Length + 1, records.Length).U16(inputRest), records);

    /// <summary>A rule set of the rules given.</summary>
    private static Ot03Part Ot03Set(params Ot03Part[] rules)
    {
        var set = new Ot03Part().U16(rules.Length);
        foreach (var r in rules) set.Off(r);
        return set;
    }

    /// <summary>Context format 1 (type 5): coverage and one rule set offset per entry (null = NULL).</summary>
    private static Ot03Part Ot03ContextF1(Ot03Part coverage, params Ot03Part?[] sets)
    {
        var sub = new Ot03Part().U16(1).Off(coverage).U16(sets.Length);
        foreach (var s in sets) sub.Off(s);
        return sub;
    }

    /// <summary>Context format 2 (type 5): coverage, ClassDef and one rule set offset per class (null = NULL).</summary>
    private static Ot03Part Ot03ContextF2(Ot03Part coverage, Ot03Part classDef, params Ot03Part?[] sets)
    {
        var sub = new Ot03Part().U16(2).Off(coverage).Off(classDef).U16(sets.Length);
        foreach (var s in sets) sub.Off(s);
        return sub;
    }

    /// <summary>ChainedSequenceRule / ChainedClassSequenceRule: backtrack (nearest first), input from the second position, lookahead.</summary>
    private static Ot03Part Ot03ChainRule(int[] backtrack, int[] inputRest, int[] lookahead, params (int Seq, int Lookup)[] records) =>
        Ot03Records(new Ot03Part().U16(backtrack.Length).U16(backtrack).U16(inputRest.Length + 1).U16(inputRest)
            .U16(lookahead.Length).U16(lookahead).U16(records.Length), records);

    /// <summary>Chained context format 1 (type 6).</summary>
    private static Ot03Part Ot03ChainF1(Ot03Part coverage, params Ot03Part?[] sets) => Ot03ContextF1(coverage, sets);

    /// <summary>Chained context format 2 (type 6): ClassDefs of backtrack, input and lookahead (null = NULL).</summary>
    private static Ot03Part Ot03ChainF2(Ot03Part coverage, Ot03Part? back, Ot03Part input, Ot03Part? ahead, params Ot03Part?[] sets)
    {
        var sub = new Ot03Part().U16(2).Off(coverage).Off(back).Off(input).Off(ahead).U16(sets.Length);
        foreach (var s in sets) sub.Off(s);
        return sub;
    }

    /// <summary>Chained context format 3 (type 6): one coverage per position of backtrack (nearest first), input and lookahead.</summary>
    private static Ot03Part Ot03ChainF3(int[][] backtrack, int[][] input, int[][] lookahead, params (int Seq, int Lookup)[] records)
    {
        var sub = new Ot03Part().U16(3);
        foreach (var sequence in new[] { backtrack, input, lookahead })
        {
            sub.U16(sequence.Length);
            foreach (var glyphs in sequence) sub.Off(Ot03Cov(glyphs));
        }
        return Ot03Records(sub.U16(records.Length), records);
    }

    private static int[] Ot03Gl(char c) => [Ot03G(c)];

    // ------------------------------------------------------------ 2.8

    /// <summary>
    /// OT 2.8 sentences 1–3: contextual substitution is GSUB lookup type 5. A format 3 subtable (coverage {a}, record (0 → a→x)) under
    /// type 5 turns „ab“ into „xb“; the same subtable under the undefined type 0 changes nothing.
    /// </summary>
    [Theory]
    [InlineData(5, "xb")]
    [InlineData(0, "ab")]
    public void Ot03_ContextualSubstitution_IsLookupType5(int type, string expected)
    {
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(type, 0, Ot03ContextF3([Ot03Gl('a')], (0, 1))), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs(expected), Ot03Shape(shaper, "ab"));
    }

    /// <summary>
    /// OT 2.8 sentences 4 and 19: contextual (type 7) and chained contextual (type 8) positioning apply their nested lookups like the GSUB
    /// contexts. „kern“ names only a type 7 or 8 lookup (input {A}, lookahead {V} for type 8, record (0 → single positioning XAdvance −200)):
    /// „AV“ takes the −200 the nested lookup gives when named directly, with „-kern“ it keeps its widths (docs/FEHLERGEDAECHTNIS.md, No. 1161;
    /// before both types were passed over).
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void Ot03_ContextualPositioningTypes7And8_ApplyTheirNestedLookups(int type)
    {
        int a = Ot03G('A'), v = Ot03G('V');
        var context = type == 7
            ? new Ot03Part().U16(3, 1, 1).Off(Ot03Cov(a)).U16(0, 1)
            : new Ot03Part().U16(3, 0, 1).Off(Ot03Cov(a)).U16(1).Off(Ot03Cov(v)).U16(1, 0, 1);
        var single = Ot03Lookup(1, 0, new Ot03Part().U16(1).Off(Ot03Cov(a)).U16(4, -200));
        var shaper = Ot03GposShaper(Ot03Header([Ot03Lookup(type, 0, context), single], [("kern", [0])]));
        Assert.Equal(Ot03Font.AdvanceWidth(a), shaper.Shape("AV", ["-kern"])[0].XAdvance);
        Assert.Equal(Ot03Font.AdvanceWidth(a) - 200, shaper.Shape("AV")[0].XAdvance);
        var direct = Ot03GposShaper(Ot03Header([Ot03Lookup(type, 0, context), single], [("kern", [1])]));
        Assert.Equal(Ot03Font.AdvanceWidth(a) - 200, direct.Shape("AV")[0].XAdvance);
    }

    /// <summary>
    /// OT 2.8 sentences 5–7: a context names an input sequence and actions as nested lookups; it acts only when the whole sequence
    /// matches. Format 3 {a}{b}{c}, record (0 → a→x): „abc“ → „xbc“, „abd“ stays.
    /// </summary>
    [Fact]
    public void Ot03_Context_ActsOnlyWhenTheWholeSequenceMatches()
    {
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, Ot03ContextF3([Ot03Gl('a'), Ot03Gl('b'), Ot03Gl('c')], (0, 1))), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs("xbc"), Ot03Shape(shaper, "abc"));
        Assert.Equal(Ot03Glyphs("abd"), Ot03Shape(shaper, "abd"));
    }

    /// <summary>
    /// OT 2.8 sentences 8–9: the lookupFlag of the context lookup applies while its sequence is matched. GDEF makes „m“ a mark; format 3
    /// {a}{b} with IgnoreMarks (8): „amb“ → „xmb“; without the flag the mark breaks the sequence and „amb“ stays.
    /// </summary>
    [Theory]
    [InlineData(8, "xmb")]
    [InlineData(0, "amb")]
    public void Ot03_ContextLookupFlags_SkipMarksWhileMatching(int flag, string expected)
    {
        var gdef = new Ot03Part().U16(1, 0).Off(Ot03Classes((Ot03G('m'), 3))).U16(0, 0, 0).Build();
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, flag, Ot03ContextF3([Ot03Gl('a'), Ot03Gl('b')], (0, 1))), Ot03Single(('a', 'x'))), gdef);
        Assert.Equal(Ot03Glyphs(expected), Ot03Shape(shaper, "amb"));
    }

    /// <summary>
    /// OT 2.8 sentences 10–11: sequences are in logical order, also for right-to-left text. Noto Sans Arabic with a chained format 3 rule
    /// (input {beh}, lookahead {alef}, record (0 → beh→teh)): „با“ (beh before alef in logical order) is changed, „اب“ is not.
    /// </summary>
    [Fact]
    public void Ot03_ChainedContext_MatchesInLogicalOrder()
    {
        var arabic = EmbeddedFonts.ArabicOpenType("NotoSansArabic-Regular.ttf");
        int beh = arabic.GlyphForUnicode(0x0628), alef = arabic.GlyphForUnicode(0x0627), teh = arabic.GlyphForUnicode(0x062A);
        Assert.True(beh > 0 && alef > 0 && teh > 0);
        var single = Ot03Lookup(1, 0, new Ot03Part().U16(2).Off(Ot03Cov(beh)).U16(1, teh));
        var gsub = Ot03Header([Ot03Lookup(6, 0, Ot03ChainF3([], [[beh]], [[alef]], (0, 1))), single], [("liga", [0])], "arab");
        var shaper = TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(arabic.FileData,
            new Dictionary<string, byte[]?> { ["GSUB"] = gsub.Build(), ["GPOS"] = null })));
        Assert.Equal([teh, alef], Ot03Shape(shaper, "با"));
        Assert.Equal([alef, beh], Ot03Shape(shaper, "اب"));
    }

    /// <summary>
    /// OT 2.8 sentences 12–16: type 5 has three formats (glyph IDs, classes, coverages). The sequence „a b“ with the record (0 → a→x) in
    /// formats 1, 2 and 3 turns „ab“ into „xb“ each; an undefined format 4 changes nothing.
    /// </summary>
    [Theory]
    [InlineData(1, "xb")]
    [InlineData(2, "xb")]
    [InlineData(3, "xb")]
    [InlineData(4, "ab")]
    public void Ot03_ContextFormats1To3_SameSequence(int format, string expected)
    {
        int a = Ot03G('a'), b = Ot03G('b');
        var sub = format switch
        {
            1 => Ot03ContextF1(Ot03Cov(a), Ot03Set(Ot03Rule([b], (0, 1)))),
            2 => Ot03ContextF2(Ot03Cov(a), Ot03Classes((a, 1), (b, 2)), null, Ot03Set(Ot03Rule([2], (0, 1)))),
            3 => Ot03ContextF3([[a], [b]], (0, 1)),
            _ => new Ot03Part().U16(4, 2, 1).Off(Ot03Cov(a)).Off(Ot03Cov(b)).U16(0, 1),
        };
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, sub), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs(expected), Ot03Shape(shaper, "ab"));
    }

    /// <summary>
    /// OT 2.8 sentences 17–18 and 20: chained contexts (type 6) add a backtrack before and a lookahead after the input. Format 3 with
    /// backtrack {c}, input {a}, lookahead {b}, record (0 → a→x): „cab“ → „cxb“, „dab“ stays.
    /// </summary>
    [Fact]
    public void Ot03_ChainedContext_BacktrackAndLookaheadMustMatch()
    {
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, Ot03ChainF3([Ot03Gl('c')], [Ot03Gl('a')], [Ot03Gl('b')], (0, 1))), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs("cxb"), Ot03Shape(shaper, "cab"));
        Assert.Equal(Ot03Glyphs("dab"), Ot03Shape(shaper, "dab"));
    }

    /// <summary>
    /// OT 2.8 sentences 21–22: actions only on input glyphs, and after a match the shaper goes on right after the input, not after the
    /// lookahead. Format 3, input {a}, lookahead {a}, record (0 → a→x): „aaa“ → „xxa“ (the lookahead glyph starts a match itself);
    /// a record with sequenceIndex 1 (pointing into the lookahead) does nothing.
    /// </summary>
    [Theory]
    [InlineData(0, "xxa")]
    [InlineData(1, "aaa")]
    public void Ot03_ChainedContext_GoesOnAfterTheInputNotTheLookahead(int sequenceIndex, string expected)
    {
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, Ot03ChainF3([], [Ot03Gl('a')], [Ot03Gl('a')], (sequenceIndex, 1))), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs(expected), Ot03Shape(shaper, "aaa"));
    }

    /// <summary>
    /// OT 2.8 sentences 23–28: chained contexts have three formats as well, the action is a nested lookup of the own table (here a GSUB
    /// single substitution). Backtrack c, input a, lookahead b in formats 1, 2 and 3: „cab“ → „cxb“ each, „dab“ stays.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Ot03_ChainedContextFormats1To3_SameRule(int format)
    {
        int a = Ot03G('a'), b = Ot03G('b'), c = Ot03G('c');
        var sub = format switch
        {
            1 => Ot03ChainF1(Ot03Cov(a), Ot03Set(Ot03ChainRule([c], [], [b], (0, 1)))),
            2 => Ot03ChainF2(Ot03Cov(a), Ot03Classes((c, 1)), Ot03Classes((a, 1)), Ot03Classes((b, 1)), null,
                Ot03Set(Ot03ChainRule([1], [], [1], (0, 1)))),
            _ => Ot03ChainF3([[c]], [[a]], [[b]], (0, 1)),
        };
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, sub), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs("cxb"), Ot03Shape(shaper, "cab"));
        Assert.Equal(Ot03Glyphs("dab"), Ot03Shape(shaper, "dab"));
    }

    /// <summary>
    /// OT 2.8 sentences 29–31: the pattern is matched before any action, and the actions do not match it again. Format 3 {a}{b} with
    /// records (0 → a→x) and (1 → b→y): after the first record the pattern no longer matches, the second still acts („xy“). The draft
    /// expected records (0 → ligature a b → L) and (2 → c→z) on „abc“ to give „Lz“; since No. 1142 (GSUB 21.4.6) a record counts its place
    /// in the sequence as the records before left it, place 2 no longer exists after the ligature, and the result is „Lc“.
    /// </summary>
    [Fact]
    public void Ot03_Context_MatchedBeforeActions_ActionsDoNotMatchAgain()
    {
        var two = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, Ot03ContextF3([Ot03Gl('a'), Ot03Gl('b')], (0, 1), (1, 2))), Ot03Single(('a', 'x')), Ot03Single(('b', 'y'))));
        Assert.Equal(Ot03Glyphs("xy"), Ot03Shape(two, "ab"));
        var ligature = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, Ot03ContextF3([Ot03Gl('a'), Ot03Gl('b'), Ot03Gl('c')], (0, 1), (2, 2))),
            Ot03Ligature("ab", 'L'), Ot03Single(('c', 'z'))));
        Assert.Equal(Ot03Glyphs("Lc"), Ot03Shape(ligature, "abc"));
    }

    /// <summary>
    /// OT 2.8.1 sentences 1–6: a SequenceLookup record names a zero-based place in the input and a zero-based LookupList index. Format 3
    /// {a}{b}: record (1 → b→y) gives „ay“; a place beyond the input (5) and a lookup index beyond the LookupList (7) change nothing.
    /// </summary>
    [Theory]
    [InlineData(1, 1, "ay")]
    [InlineData(5, 1, "ab")]
    [InlineData(1, 7, "ab")]
    public void Ot03_SequenceLookupRecord_PlaceAndLookupIndex(int sequenceIndex, int lookupIndex, string expected)
    {
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, Ot03ContextF3([Ot03Gl('a'), Ot03Gl('b')], (sequenceIndex, lookupIndex))), Ot03Single(('b', 'y'))));
        Assert.Equal(Ot03Glyphs(expected), Ot03Shape(shaper, "ab"));
    }

    /// <summary>
    /// OT 2.8.2 sentences 1–4: format 1 holds several sequences of glyph IDs, the first glyph of each in the coverage. Coverage {a, c}
    /// with the rules (a b → a→x) and (c d → c→z): „abcd“ → „xbzd“.
    /// </summary>
    [Fact]
    public void Ot03_ContextFormat1_SeveralSequences()
    {
        int a = Ot03G('a'), b = Ot03G('b'), c = Ot03G('c'), d = Ot03G('d');
        Assert.True(a < c);
        var sub = Ot03ContextF1(Ot03Cov(a, c), Ot03Set(Ot03Rule([b], (0, 1))), Ot03Set(Ot03Rule([d], (0, 2))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, sub), Ot03Single(('a', 'x')), Ot03Single(('c', 'z'))));
        Assert.Equal(Ot03Glyphs("xbzd"), Ot03Shape(shaper, "abcd"));
    }

    /// <summary>
    /// OT 2.8.2 sentences 5–8 and 20–26: sequences with the same first glyph share a rule set, whose rules are tried in their order and
    /// the first matching one applies. Rules (a b c → a→x) before (a b → a→y): „abc“ → „xbc“, „abd“ → „ybd“; in the other order „abc“ → „ybc“.
    /// </summary>
    [Fact]
    public void Ot03_ContextFormat1_FirstMatchingRuleOfTheSetApplies()
    {
        int b = Ot03G('b'), c = Ot03G('c');
        TextShaper Shaper(bool longFirst)
        {
            Ot03Part abc = Ot03Rule([b, c], (0, 1)), ab = Ot03Rule([b], (0, 2));
            var sub = Ot03ContextF1(Ot03Cov(Ot03G('a')), longFirst ? Ot03Set(abc, ab) : Ot03Set(ab, abc));
            return Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, sub), Ot03Single(('a', 'x')), Ot03Single(('a', 'y'))));
        }
        Assert.Equal(Ot03Glyphs("xbc"), Ot03Shape(Shaper(true), "abc"));
        Assert.Equal(Ot03Glyphs("ybd"), Ot03Shape(Shaper(true), "abd"));
        Assert.Equal(Ot03Glyphs("ybc"), Ot03Shape(Shaper(false), "abc"));
    }

    /// <summary>
    /// OT 2.8.2 sentences 9–13: SequenceContextFormat1 — format, coverage offset from the start of the subtable, rule set count. The
    /// table of the sentences 1–4 with its coverage placed behind the rule sets: same result „xbzd“.
    /// </summary>
    [Fact]
    public void Ot03_ContextFormat1_CoverageOffsetFromTheSubtable()
    {
        int a = Ot03G('a'), b = Ot03G('b'), c = Ot03G('c'), d = Ot03G('d');
        var coverage = Ot03Cov(a, c).AtEnd();
        var first = Ot03Set(Ot03Rule([b], (0, 1)));
        var sub = Ot03ContextF1(coverage, first, Ot03Set(Ot03Rule([d], (0, 2))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, sub), Ot03Single(('a', 'x')), Ot03Single(('c', 'z'))));
        Assert.Equal(Ot03Glyphs("xbzd"), Ot03Shape(shaper, "abcd"));
        Assert.True(coverage.Position > first.Position);
    }

    /// <summary>
    /// OT 2.8.2 sentence 14: rule set offsets may be NULL. Coverage {a, b}, offsets [NULL, rule set for b (b → b→y)]: „ab“ → „ay“, a stays
    /// and nothing of the subtable header is read as a rule. The draft described the header read as rules; fixed since No. 1144.
    /// </summary>
    [Fact]
    public void Ot03_ContextFormat1_NullRuleSet_IsNone()
    {
        var sub = Ot03ContextF1(Ot03Cov(Ot03G('a'), Ot03G('b')), null, Ot03Set(Ot03Rule([], (0, 1))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, sub), Ot03Single(('a', 'x'), ('b', 'y'))));
        Assert.Equal(Ot03Glyphs("ay"), Ot03Shape(shaper, "ab"));
    }

    /// <summary>
    /// OT 2.8.2 sentences 15–19: one rule set per covered glyph in coverage order; a count lower than the coverage leaves the glyphs
    /// beyond it unmatched, rule sets beyond the coverage are never reached. Coverage {a, b} with count 1: „ab“ → „xb“; count 3 with a
    /// third rule set for e (not covered): „abe“ → „xye“.
    /// </summary>
    [Fact]
    public void Ot03_ContextFormat1_RuleSetCountAgainstCoverage()
    {
        int a = Ot03G('a'), b = Ot03G('b');
        Assert.True(a < b);
        var nested = Ot03Single(('a', 'x'), ('b', 'y'), ('e', 'z'));
        var one = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, Ot03ContextF1(Ot03Cov(a, b), Ot03Set(Ot03Rule([], (0, 1))))), nested));
        Assert.Equal(Ot03Glyphs("xb"), Ot03Shape(one, "ab"));
        var three = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, Ot03ContextF1(Ot03Cov(a, b),
            Ot03Set(Ot03Rule([], (0, 1))), Ot03Set(Ot03Rule([], (0, 1))), Ot03Set(Ot03Rule([], (0, 1))))), Ot03Single(('a', 'x'), ('b', 'y'), ('e', 'z'))));
        Assert.Equal(Ot03Glyphs("xye"), Ot03Shape(three, "abe"));
    }

    /// <summary>
    /// OT 2.8.2 sentences 30–35: SequenceRule — glyphCount, seqLookupCount, input from the second glyph, records. A rule of glyphCount 3,
    /// input [b, c] and the records (0 → a→x), (2 → c→z): „abc“ → „xbz“.
    /// </summary>
    [Fact]
    public void Ot03_ContextFormat1_SequenceRuleFields()
    {
        var sub = Ot03ContextF1(Ot03Cov(Ot03G('a')), Ot03Set(Ot03Rule([Ot03G('b'), Ot03G('c')], (0, 1), (2, 2))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, sub), Ot03Single(('a', 'x')), Ot03Single(('c', 'z'))));
        Assert.Equal(Ot03Glyphs("xbz"), Ot03Shape(shaper, "abc"));
    }

    /// <summary>
    /// OT 2.8.2 sentences 36–41: the records are processed in their order, each on the result of the one before. Rule (a b) with
    /// (0 → a→x) then (0 → x→y): „ab“ → „yb“; in the other order the second no longer finds an a: „xb“. (A glyphCount of 0:
    /// ShaperNormTests.IndexBeyondItsArrayAndRuleOfNoGlyphs_ChangeNothing, No. 1144.)
    /// </summary>
    [Theory]
    [InlineData(false, "yb")]
    [InlineData(true, "xb")]
    public void Ot03_ContextFormat1_RecordsInTheirOrder(bool reversed, string expected)
    {
        (int, int)[] records = reversed ? [(0, 2), (0, 1)] : [(0, 1), (0, 2)];
        var sub = Ot03ContextF1(Ot03Cov(Ot03G('a')), Ot03Set(Ot03Rule([Ot03G('b')], records)));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, sub), Ot03Single(('a', 'x')), Ot03Single(('x', 'y'))));
        Assert.Equal(Ot03Glyphs(expected), Ot03Shape(shaper, "ab"));
    }

    /// <summary>
    /// OT 2.8.3 sentences 1–4, 6–7: format 2 matches classes of a ClassDef. a, c in class 1, b, d in class 2, rule (1 2) with the record
    /// (0 → a→x, c→z): „ab“ → „xb“, „cd“ → „zd“, „ac“ stays.
    /// </summary>
    [Fact]
    public void Ot03_ContextFormat2_MatchesClasses()
    {
        int a = Ot03G('a'), b = Ot03G('b'), c = Ot03G('c'), d = Ot03G('d');
        var sub = Ot03ContextF2(Ot03Cov(a, c), Ot03Classes((a, 1), (c, 1), (b, 2), (d, 2)), null, Ot03Set(Ot03Rule([2], (0, 1))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, sub), Ot03Single(('a', 'x'), ('c', 'z'))));
        Assert.Equal(Ot03Glyphs("xb"), Ot03Shape(shaper, "ab"));
        Assert.Equal(Ot03Glyphs("zd"), Ot03Shape(shaper, "cd"));
        Assert.Equal(Ot03Glyphs("ac"), Ot03Shape(shaper, "ac"));
    }

    /// <summary>
    /// OT 2.8.3 sentences 8–10, 12: the coverage holds the glyphs that can begin a pattern. a and c in class 1, coverage only {a}:
    /// „ab“ → „xb“, „cb“ stays.
    /// </summary>
    [Fact]
    public void Ot03_ContextFormat2_CoverageDecidesTheFirstGlyph()
    {
        int a = Ot03G('a'), b = Ot03G('b'), c = Ot03G('c');
        var sub = Ot03ContextF2(Ot03Cov(a), Ot03Classes((a, 1), (c, 1), (b, 2)), null, Ot03Set(Ot03Rule([2], (0, 1))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, sub), Ot03Single(('a', 'x'), ('c', 'z'))));
        Assert.Equal(Ot03Glyphs("xb"), Ot03Shape(shaper, "ab"));
        Assert.Equal(Ot03Glyphs("cb"), Ot03Shape(shaper, "cb"));
    }

    /// <summary>
    /// OT 2.8.3 sentences 13–17: every glyph has one class, a glyph the ClassDef does not name has class 0, and the class selects the rule
    /// set. e (covered, no class entry) with a rule set for class 0 holding the rule (0 0): „ee“ → „xe“; „ea“ (a in class 1) stays.
    /// </summary>
    [Fact]
    public void Ot03_ContextFormat2_GlyphWithoutClassEntry_IsClass0()
    {
        int a = Ot03G('a'), e = Ot03G('e');
        var sub = Ot03ContextF2(Ot03Cov(e), Ot03Classes((a, 1)), Ot03Set(Ot03Rule([0], (0, 1))), null);
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, sub), Ot03Single(('e', 'x'))));
        Assert.Equal(Ot03Glyphs("xe"), Ot03Shape(shaper, "ee"));
        Assert.Equal(Ot03Glyphs("ea"), Ot03Shape(shaper, "ea"));
    }

    /// <summary>
    /// OT 2.8.3 sentences 18–19: a ClassSequenceRule has the layout of a SequenceRule but holds classes; the referring format decides.
    /// The same rule bytes (input [glyph ID of b]) under format 1 match „ab“, under format 2 (d in the class whose number is b's glyph
    /// ID) they match „ad“ and not „ab“.
    /// </summary>
    [Fact]
    public void Ot03_SameRuleBytes_GlyphIdsInFormat1_ClassesInFormat2()
    {
        int a = Ot03G('a'), b = Ot03G('b'), d = Ot03G('d');
        var format1 = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, Ot03ContextF1(Ot03Cov(a), Ot03Set(Ot03Rule([b], (0, 1))))), Ot03Single(('a', 'x'))));
        var format2 = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, Ot03ContextF2(Ot03Cov(a), Ot03Classes((a, 1), (d, b)), null,
            Ot03Set(Ot03Rule([b], (0, 1))))), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs("xb"), Ot03Shape(format1, "ab"));
        Assert.Equal(Ot03Glyphs("ad"), Ot03Shape(format1, "ad"));
        Assert.Equal(Ot03Glyphs("ab"), Ot03Shape(format2, "ab"));
        Assert.Equal(Ot03Glyphs("xd"), Ot03Shape(format2, "ad"));
    }

    /// <summary>
    /// OT 2.8.3 sentences 20–29: SequenceContextFormat2 with one rule set offset per class, NULL for a class without patterns. Three
    /// classes (a in 1, c in 2), offset for class 2 NULL: „a“ → „x“, „c“ stays, no exception.
    /// </summary>
    [Fact]
    public void Ot03_ContextFormat2_NullRuleSetForAClass()
    {
        int a = Ot03G('a'), c = Ot03G('c');
        var sub = Ot03ContextF2(Ot03Cov(a, c), Ot03Classes((a, 1), (c, 2)), null, Ot03Set(Ot03Rule([], (0, 1))), null);
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, sub), Ot03Single(('a', 'x'), ('c', 'z'))));
        Assert.Equal(Ot03Glyphs("x"), Ot03Shape(shaper, "a"));
        Assert.Equal(Ot03Glyphs("c"), Ot03Shape(shaper, "c"));
    }

    /// <summary>
    /// OT 2.8.3 sentences 30–36: the rules of a ClassSequenceRuleSet are tried in their order, the first matching applies. (1 2 1 → a→x)
    /// before (1 2 → a→y): „aba“ → „xba“, „abb“ → „ybb“.
    /// </summary>
    [Fact]
    public void Ot03_ContextFormat2_FirstMatchingRuleOfTheSetApplies()
    {
        int a = Ot03G('a'), b = Ot03G('b');
        var sub = Ot03ContextF2(Ot03Cov(a), Ot03Classes((a, 1), (b, 2)), null, Ot03Set(Ot03Rule([2, 1], (0, 1)), Ot03Rule([2], (0, 2))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, sub), Ot03Single(('a', 'x')), Ot03Single(('a', 'y'))));
        Assert.Equal(Ot03Glyphs("xba"), Ot03Shape(shaper, "aba"));
        Assert.Equal(Ot03Glyphs("ybb"), Ot03Shape(shaper, "abb"));
    }

    /// <summary>
    /// OT 2.8.3 sentences 37–47: ClassSequenceRule — classes from the second position, records in their order, each on the result of
    /// the one before. Rule (1 2) with (0 → a→x), (0 → x→y): „ab“ → „yb“.
    /// </summary>
    [Fact]
    public void Ot03_ContextFormat2_RecordsInTheirOrder()
    {
        int a = Ot03G('a'), b = Ot03G('b');
        var sub = Ot03ContextF2(Ot03Cov(a), Ot03Classes((a, 1), (b, 2)), null, Ot03Set(Ot03Rule([2], (0, 1), (0, 2))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, sub), Ot03Single(('a', 'x')), Ot03Single(('x', 'y'))));
        Assert.Equal(Ot03Glyphs("yb"), Ot03Shape(shaper, "ab"));
    }

    /// <summary>
    /// OT 2.8.4 sentences 1–11: format 3 is one pattern of coverages, one per position. Coverages {a, c}, {b} with (0 → a→x, c→z):
    /// „ab“ → „xb“, „cb“ → „zb“, „ba“ stays; a glyphCount of 0 never matches.
    /// </summary>
    [Fact]
    public void Ot03_ContextFormat3_OneCoveragePerPosition()
    {
        int a = Ot03G('a'), b = Ot03G('b'), c = Ot03G('c');
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, Ot03ContextF3([[a, c], [b]], (0, 1))), Ot03Single(('a', 'x'), ('c', 'z'))));
        Assert.Equal(Ot03Glyphs("xb"), Ot03Shape(shaper, "ab"));
        Assert.Equal(Ot03Glyphs("zb"), Ot03Shape(shaper, "cb"));
        Assert.Equal(Ot03Glyphs("ba"), Ot03Shape(shaper, "ba"));
        var none = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, new Ot03Part().U16(3, 0, 1).U16(0, 1)), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs("ab"), Ot03Shape(none, "ab"));
    }

    /// <summary>
    /// OT 2.8.4 sentences 12–14: the records act in their order, each on the result of the one before. {a}{b} with (1 → b→y) then
    /// (0 → a→x): „ab“ → „xy“; with (0 → a→x), (0 → x→z): „zb“.
    /// </summary>
    [Fact]
    public void Ot03_ContextFormat3_RecordsInTheirOrder()
    {
        var first = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, Ot03ContextF3([Ot03Gl('a'), Ot03Gl('b')], (1, 1), (0, 2))), Ot03Single(('b', 'y')), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs("xy"), Ot03Shape(first, "ab"));
        var second = Ot03Shaper(Ot03Liga(Ot03Lookup(5, 0, Ot03ContextF3([Ot03Gl('a'), Ot03Gl('b')], (0, 1), (0, 2))), Ot03Single(('a', 'x')), Ot03Single(('x', 'z'))));
        Assert.Equal(Ot03Glyphs("zb"), Ot03Shape(second, "ab"));
    }

    /// <summary>
    /// OT 2.8.5 sentences 1–4: chained format 1 — input, backtrack and lookahead of glyph IDs, the first input glyph in the coverage.
    /// Rule backtrack [c], input (a), lookahead [b], (0 → a→x): „cab“ → „cxb“, „cad“ stays.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat1_GlyphSequences()
    {
        var sub = Ot03ChainF1(Ot03Cov(Ot03G('a')), Ot03Set(Ot03ChainRule([Ot03G('c')], [], [Ot03G('b')], (0, 1))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, sub), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs("cxb"), Ot03Shape(shaper, "cab"));
        Assert.Equal(Ot03Glyphs("cad"), Ot03Shape(shaper, "cad"));
    }

    /// <summary>
    /// OT 2.8.5 sentences 5–8 and 33–39: rules with the same first input glyph share a ChainedSequenceRuleSet, the first matching rule in
    /// list order applies. (backtrack c, a, lookahead b → a→x) before (a → a→y): „cab“ → „cxb“, „dab“ → „dyb“; in the other order „cab“ →
    /// „cyb“.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat1_FirstMatchingRuleOfTheSetApplies()
    {
        TextShaper Shaper(bool chainedFirst)
        {
            Ot03Part chained = Ot03ChainRule([Ot03G('c')], [], [Ot03G('b')], (0, 1)), plain = Ot03ChainRule([], [], [], (0, 2));
            var sub = Ot03ChainF1(Ot03Cov(Ot03G('a')), chainedFirst ? Ot03Set(chained, plain) : Ot03Set(plain, chained));
            return Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, sub), Ot03Single(('a', 'x')), Ot03Single(('a', 'y'))));
        }
        Assert.Equal(Ot03Glyphs("cxb"), Ot03Shape(Shaper(true), "cab"));
        Assert.Equal(Ot03Glyphs("dyb"), Ot03Shape(Shaper(true), "dab"));
        Assert.Equal(Ot03Glyphs("cyb"), Ot03Shape(Shaper(false), "cab"));
    }

    /// <summary>
    /// OT 2.8.5 sentences 9–12 and 21: input, backtrack and lookahead must all match, the lookup flags apply in all three, the backtrack
    /// runs backwards from the glyph before the input (index 0 = nearest), the lookahead forwards. Backtrack [b, a], input (m), lookahead
    /// [w, x], (0 → m→y): „abmwx“ → „abywx“, „bamwx“ stays; with IgnoreMarks a mark between a and b is passed over, without it breaks the
    /// match.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat1_BacktrackReversed_FlagsInAllSequences()
    {
        var acute = Ot03Font.GlyphForUnicode('́');
        var gdef = new Ot03Part().U16(1, 0).Off(Ot03Classes((acute, 3))).U16(0, 0, 0).Build();
        TextShaper Shaper(int flag) => Ot03Shaper(Ot03Liga(Ot03Lookup(6, flag, Ot03ChainF1(Ot03Cov(Ot03G('m')),
            Ot03Set(Ot03ChainRule([Ot03G('b'), Ot03G('a')], [], [Ot03G('w'), Ot03G('x')], (0, 1))))), Ot03Single(('m', 'y'))), gdef);
        Assert.Equal(Ot03Glyphs("abywx"), Ot03Shape(Shaper(0), "abmwx"));
        Assert.Equal(Ot03Glyphs("bamwx"), Ot03Shape(Shaper(0), "bamwx"));
        Assert.Equal([Ot03G('a'), acute, .. Ot03Glyphs("bywx")], Ot03Shape(Shaper(8), "ábmwx"));
        Assert.Equal([Ot03G('a'), acute, .. Ot03Glyphs("bmwx")], Ot03Shape(Shaper(0), "ábmwx"));
    }

    /// <summary>
    /// OT 2.8.5 sentences 22–26: ChainedSequenceContextFormat1 — format, coverage offset from the subtable, rule set count. The rule of
    /// the sentences 1–4 with its coverage behind the rule sets: same result „cxb“.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat1_CoverageOffsetFromTheSubtable()
    {
        var coverage = Ot03Cov(Ot03G('a')).AtEnd();
        var set = Ot03Set(Ot03ChainRule([Ot03G('c')], [], [Ot03G('b')], (0, 1)));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, Ot03ChainF1(coverage, set)), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs("cxb"), Ot03Shape(shaper, "cab"));
        Assert.True(coverage.Position > set.Position);
    }

    /// <summary>
    /// OT 2.8.5 sentence 27: ChainedSequenceRuleSet offsets may be NULL. Coverage {a, b}, offsets [NULL, rule set for b (b → b→y)]:
    /// „ab“ → „ay“, nothing of the header read as a rule. The draft described the header read as rules; fixed since No. 1144.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat1_NullRuleSet_IsNone()
    {
        var sub = Ot03ChainF1(Ot03Cov(Ot03G('a'), Ot03G('b')), null, Ot03Set(Ot03ChainRule([], [], [], (0, 1))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, sub), Ot03Single(('a', 'x'), ('b', 'y'))));
        Assert.Equal(Ot03Glyphs("ay"), Ot03Shape(shaper, "ab"));
    }

    /// <summary>
    /// OT 2.8.5 sentences 28–32: one rule set per covered glyph in coverage order; covered glyphs beyond the count are passed over.
    /// Coverage {a, b}, count 1: „ab“ → „xb“.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat1_RuleSetCountAgainstCoverage()
    {
        Assert.True(Ot03G('a') < Ot03G('b'));
        var sub = Ot03ChainF1(Ot03Cov(Ot03G('a'), Ot03G('b')), Ot03Set(Ot03ChainRule([], [], [], (0, 1))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, sub), Ot03Single(('a', 'x'), ('b', 'y'))));
        Assert.Equal(Ot03Glyphs("xb"), Ot03Shape(shaper, "ab"));
    }

    /// <summary>
    /// OT 2.8.5 sentences 40–49: ChainedSequenceRule — backtrack count and glyphs, input count and glyphs from the second, lookahead
    /// count and glyphs, records. Two backtrack glyphs [q, p], input a b c, lookahead [d], records (0 → a→x), (2 → c→z): „pqabcd“ → „pqxbzd“.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat1_RuleFields()
    {
        var rule = Ot03ChainRule([Ot03G('q'), Ot03G('p')], [Ot03G('b'), Ot03G('c')], [Ot03G('d')], (0, 1), (2, 2));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, Ot03ChainF1(Ot03Cov(Ot03G('a')), Ot03Set(rule))), Ot03Single(('a', 'x')), Ot03Single(('c', 'z'))));
        Assert.Equal(Ot03Glyphs("pqxbzd"), Ot03Shape(shaper, "pqabcd"));
    }

    /// <summary>
    /// OT 2.8.5 sentences 50–54: inputGlyphCount counts the first glyph, records act in their order on the result before. Input (a b),
    /// (0 → a→x), (0 → x→y): „ab“ → „yb“. A rule with inputGlyphCount 0 never matches (the draft described it as a match; fixed since
    /// No. 1144).
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat1_RecordsInTheirOrder_NoInputNoMatch()
    {
        var sub = Ot03ChainF1(Ot03Cov(Ot03G('a')), Ot03Set(Ot03ChainRule([], [Ot03G('b')], [], (0, 1), (0, 2))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, sub), Ot03Single(('a', 'x')), Ot03Single(('x', 'y'))));
        Assert.Equal(Ot03Glyphs("yb"), Ot03Shape(shaper, "ab"));
        // backtrack 0, inputGlyphCount 0, lookahead 0, one record (0 → a→x)
        var empty = new Ot03Part().U16(0, 0, 0, 1, 0, 1);
        var none = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, Ot03ChainF1(Ot03Cov(Ot03G('a')), Ot03Set(empty))), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs("ab"), Ot03Shape(none, "ab"));
    }

    /// <summary>
    /// OT 2.8.6 sentences 1–5: chained format 2 matches classes of three ClassDefs. Backtrack c → 1, input a → 1, lookahead b → 1, rule
    /// ([1], (1), [1]) with (0 → a→x): „cab“ → „cxb“; with c only in the input ClassDef (backtrack ClassDef d → 1) „cab“ stays.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat2_ThreeClassDefs()
    {
        int a = Ot03G('a'), b = Ot03G('b'), c = Ot03G('c'), d = Ot03G('d');
        TextShaper Shaper(Ot03Part back, Ot03Part input) => Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, Ot03ChainF2(Ot03Cov(a), back, input,
            Ot03Classes((b, 1)), null, Ot03Set(Ot03ChainRule([1], [], [1], (0, 1))))), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs("cxb"), Ot03Shape(Shaper(Ot03Classes((c, 1)), Ot03Classes((a, 1))), "cab"));
        Assert.Equal(Ot03Glyphs("cab"), Ot03Shape(Shaper(Ot03Classes((d, 1)), Ot03Classes((a, 1), (c, 1))), "cab"));
    }

    /// <summary>
    /// OT 2.8.6 sentences 6–8: the coverage holds the glyphs that can begin an input pattern. a in input class 1, but coverage {c}:
    /// „cab“ stays.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat2_CoverageDecidesTheFirstGlyph()
    {
        int a = Ot03G('a'), b = Ot03G('b'), c = Ot03G('c');
        TextShaper Shaper(Ot03Part coverage) => Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, Ot03ChainF2(coverage, Ot03Classes((c, 1)), Ot03Classes((a, 1)),
            Ot03Classes((b, 1)), null, Ot03Set(Ot03ChainRule([1], [], [1], (0, 1))))), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs("cab"), Ot03Shape(Shaper(Ot03Cov(c)), "cab"));
        Assert.Equal(Ot03Glyphs("cxb"), Ot03Shape(Shaper(Ot03Cov(a)), "cab"));
    }

    /// <summary>
    /// OT 2.8.6 sentences 9–16: the backtrack classes run backwards (index 0 = nearest glyph). Backtrack ClassDef p → 1, q → 2, rule
    /// backtrack [2, 1], input (1 = a): „pqa“ → „pqx“, „qpa“ stays.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat2_BacktrackClassesReversed()
    {
        int a = Ot03G('a'), p = Ot03G('p'), q = Ot03G('q');
        var sub = Ot03ChainF2(Ot03Cov(a), Ot03Classes((p, 1), (q, 2)), Ot03Classes((a, 1)), null, null, Ot03Set(Ot03ChainRule([2, 1], [], [], (0, 1))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, sub), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs("pqx"), Ot03Shape(shaper, "pqa"));
        Assert.Equal(Ot03Glyphs("qpa"), Ot03Shape(shaper, "qpa"));
    }

    /// <summary>
    /// OT 2.8.6 sentences 17–28: ChainedSequenceContextFormat2 with one rule set offset per input class, NULL for a class without
    /// patterns. a in class 1 (offset NULL), c in class 2: „a“ stays, „c“ → „z“.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat2_NullRuleSetForAClass()
    {
        int a = Ot03G('a'), c = Ot03G('c');
        var sub = Ot03ChainF2(Ot03Cov(a, c), null, Ot03Classes((a, 1), (c, 2)), null, null, null, Ot03Set(Ot03ChainRule([], [], [], (0, 1))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, sub), Ot03Single(('a', 'x'), ('c', 'z'))));
        Assert.Equal(Ot03Glyphs("a"), Ot03Shape(shaper, "a"));
        Assert.Equal(Ot03Glyphs("z"), Ot03Shape(shaper, "c"));
    }

    /// <summary>
    /// OT 2.8.6 sentences 29–35: the rules of a ChainedClassSequenceRuleSet are tried in their order. ([1], (1), [1] → a→x) before
    /// ((1) → a→y): with the backtrack and lookahead matching („cab“) the first, otherwise („dab“) the second.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat2_FirstMatchingRuleOfTheSetApplies()
    {
        int a = Ot03G('a'), b = Ot03G('b'), c = Ot03G('c');
        var sub = Ot03ChainF2(Ot03Cov(a), Ot03Classes((c, 1)), Ot03Classes((a, 1)), Ot03Classes((b, 1)), null,
            Ot03Set(Ot03ChainRule([1], [], [1], (0, 1)), Ot03ChainRule([], [], [], (0, 2))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, sub), Ot03Single(('a', 'x')), Ot03Single(('a', 'y'))));
        Assert.Equal(Ot03Glyphs("cxb"), Ot03Shape(shaper, "cab"));
        Assert.Equal(Ot03Glyphs("dyb"), Ot03Shape(shaper, "dab"));
    }

    /// <summary>
    /// OT 2.8.6 sentences 36–45: ChainedClassSequenceRule — one backtrack class, two input classes, two lookahead classes, record
    /// (1 → b→y): „pabcd“ → „paycd“, the record acts on the second input glyph.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat2_RuleFields()
    {
        int a = Ot03G('a'), b = Ot03G('b'), c = Ot03G('c'), d = Ot03G('d'), p = Ot03G('p');
        var sub = Ot03ChainF2(Ot03Cov(a), Ot03Classes((p, 1)), Ot03Classes((a, 1), (b, 2)), Ot03Classes((c, 1), (d, 2)), null,
            Ot03Set(Ot03ChainRule([1], [2], [1, 2], (1, 1))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, sub), Ot03Single(('b', 'y'))));
        Assert.Equal(Ot03Glyphs("paycd"), Ot03Shape(shaper, "pabcd"));
        Assert.Equal(Ot03Glyphs("pabdc"), Ot03Shape(shaper, "pabdc"));
    }

    /// <summary>
    /// OT 2.8.6 sentences 46–50: inputGlyphCount counts the first position, records act in their order. Input (1 2) with (0 → a→x),
    /// (0 → x→y): „ab“ → „yb“.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat2_RecordsInTheirOrder()
    {
        int a = Ot03G('a'), b = Ot03G('b');
        var sub = Ot03ChainF2(Ot03Cov(a), null, Ot03Classes((a, 1), (b, 2)), null, null, Ot03Set(Ot03ChainRule([], [2], [], (0, 1), (0, 2))));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, sub), Ot03Single(('a', 'x')), Ot03Single(('x', 'y'))));
        Assert.Equal(Ot03Glyphs("yb"), Ot03Shape(shaper, "ab"));
    }

    /// <summary>
    /// OT 2.8.7 sentences 1–4: chained format 3 is one pattern of coverage lists for backtrack, input and lookahead. Backtrack [{c}],
    /// input [{a}, {b}], lookahead [{d}], (0 → a→x): „cabd“ → „cxbd“, „cab“ stays.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat3_CoverageLists()
    {
        var sub = Ot03ChainF3([Ot03Gl('c')], [Ot03Gl('a'), Ot03Gl('b')], [Ot03Gl('d')], (0, 1));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, sub), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs("cxbd"), Ot03Shape(shaper, "cabd"));
        Assert.Equal(Ot03Glyphs("cab"), Ot03Shape(shaper, "cab"));
    }

    /// <summary>
    /// OT 2.8.7 sentences 5–16: ChainedSequenceContextFormat3 — counts and offset lists one after the other, offsets from the start of the
    /// subtable. The rule of the sentences 1–4 with its coverages at the end of the table: same result; inputGlyphCount 0 never matches.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat3_OffsetsFromTheSubtable_NoInputNoMatch()
    {
        Ot03Part back = Ot03Cov(Ot03G('c')).AtEnd(), first = Ot03Cov(Ot03G('a')).AtEnd(), second = Ot03Cov(Ot03G('b')).AtEnd(), ahead = Ot03Cov(Ot03G('d')).AtEnd();
        var sub = new Ot03Part().U16(3, 1).Off(back).U16(2).Off(first).Off(second).U16(1).Off(ahead).U16(1, 0, 1);
        var single = Ot03Single(('a', 'x'));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, sub), single));
        Assert.Equal(Ot03Glyphs("cxbd"), Ot03Shape(shaper, "cabd"));
        Assert.True(back.Position > sub.Position && ahead.Position > back.Position);
        var none = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, new Ot03Part().U16(3, 0, 0, 0, 1, 0, 1)), Ot03Single(('a', 'x'))));
        Assert.Equal(Ot03Glyphs("ab"), Ot03Shape(none, "ab"));
    }

    /// <summary>
    /// OT 2.8.7 sentences 17–19: records act in their order, each on the result before. Input [{a}, {b}], (1 → b→y), then (0 → ligature
    /// a y → L): „ab“ → „L“.
    /// </summary>
    [Fact]
    public void Ot03_ChainedFormat3_RecordsInTheirOrder()
    {
        var sub = Ot03ChainF3([], [Ot03Gl('a'), Ot03Gl('b')], [], (1, 1), (0, 2));
        var shaper = Ot03Shaper(Ot03Liga(Ot03Lookup(6, 0, sub), Ot03Single(('b', 'y')), Ot03Ligature("ay", 'L')));
        Assert.Equal(Ot03Glyphs("L"), Ot03Shape(shaper, "ab"));
    }

    // ------------------------------------------------------------ 2.9

    /// <summary>
    /// OT 2.9 sentences 1–7: Device and VariationIndex tables adjust values for a pixel size or an instance; the shaper works in font
    /// units and leaves them out. Pair positioning format 1 with valueFormat1 0x0044 (XAdvance and XAdvDevice) and a device table with
    /// large deltas: „AV“ moves by the XAdvance only (−80), and the second pair record behind the four-byte value is read right („AW“, −40).
    /// </summary>
    [Fact]
    public void Ot03_PairValueWithDeviceTable_OnlyTheValue()
    {
        int a = Ot03G('A'), v = Ot03G('V'), w = Ot03G('W');
        Assert.True(v < w);
        var device = new Ot03Part().U16(8, 12, 3, 0x7F7F, 0x7F7F, 0x7F00);
        var pairSet = new Ot03Part().U16(2).U16(v, -80).Off(device).U16(w, -40).Off(device);
        var gpos = Ot03Header([Ot03Lookup(2, 0, new Ot03Part().U16(1).Off(Ot03Cov(a)).U16(0x0044, 0, 1).Off(pairSet))], [("kern", [0])]);
        var shaper = Ot03GposShaper(gpos);
        Assert.Equal(Ot03Font.AdvanceWidth(a) - 80, shaper.Shape("AV")[0].XAdvance);
        Assert.Equal(Ot03Font.AdvanceWidth(a) - 40, shaper.Shape("AW")[0].XAdvance);
    }

    /// <summary>
    /// OT 2.9 sentences 8–15: the pixel corrections of device tables are not applied. Mark-to-base with a base anchor of format 3 (with
    /// device tables of large deltas for x and y): the mark is placed exactly as with the anchor of format 1 with the same coordinates.
    /// </summary>
    [Fact]
    public void Ot03_AnchorFormat3_PlacedByItsCoordinatesOnly()
    {
        var acute = Ot03Font.GlyphForUnicode('́');
        (double X, double Y) Mark(int anchorFormat)
        {
            var device = new Ot03Part().U16(8, 40, 3).U16([.. Enumerable.Repeat(0x7F7F, 17)]);
            var baseAnchor = anchorFormat == 3 ? new Ot03Part().U16(3, 300, 700).Off(device).Off(device) : new Ot03Part().U16(1, 300, 700);
            var markArray = new Ot03Part().U16(1, 0).Off(new Ot03Part().U16(1, 0, 500));
            var baseArray = new Ot03Part().U16(1).Off(baseAnchor);
            var sub = new Ot03Part().U16(1).Off(Ot03Cov(acute)).Off(Ot03Cov(Ot03G('a'))).U16(1).Off(markArray).Off(baseArray);
            var shaped = Ot03GposShaper(Ot03Header([Ot03Lookup(4, 0, sub)], [("kern", [0])])).Shape("á")[1];
            return (shaped.XOffset, shaped.YOffset);
        }
        var plain = Mark(1);
        Assert.NotEqual(0, plain.Y);
        Assert.Equal(plain, Mark(3));
    }

    /// <summary>
    /// OT 2.9 sentences 16–22: in a variable font the adjustment of an instance comes from a VariationIndex table into the
    /// ItemVariationStore; the shaper sets the default instance, whose deltas are zero. Single positioning XAdvance −50 with an
    /// XAdvDevice offset to a VariationIndex table (0x8000), in Liberation Sans with an fvar table: the advance changes by −50 only.
    /// </summary>
    [Fact]
    public void Ot03_VariationIndexInAVariableFont_DefaultInstanceValue()
    {
        var a = Ot03G('A');
        // fvar 1.0: one axis „wght“ 100…400…900, no named instances
        var fvar = new Ot03Part().U16(1, 0, 16, 2, 1, 20, 0, 8).Tag("wght").U32(0x00640000).U32(0x01900000).U32(0x03840000).U16(0, 256).Build();
        var single = new Ot03Part().U16(1).Off(Ot03Cov(a)).U16(0x0044, -50).Off(new Ot03Part().U16(0, 0, 0x8000));
        var shaper = Ot03GposShaper(Ot03Header([Ot03Lookup(1, 0, single)], [("kern", [0])]), ("fvar", fvar));
        Assert.Equal(Ot03Font.AdvanceWidth(a) - 50, shaper.Shape("A")[0].XAdvance);
    }

    /// <summary>
    /// OT 2.9 sentences 23–34, 36–47, 49–59: whatever a Device or VariationIndex table holds — delta formats 1, 2, 3 (2-, 4-, 8-bit values,
    /// among them startSize 8, endSize 12, format 2 with the deltas 0x123F 0xF000), the VariationIndex format 0x8000 with indices outside
    /// any store, startSize above endSize with the reserved format 0x7FFC, a VariationIndex whose first fields read as sizes would be
    /// nonsense — the value of single positioning (XAdvance −50 with an XAdvDevice offset) applies alone, without an exception.
    /// </summary>
    [Theory]
    [InlineData(8, 12, 1, new[] { 0x5540 })]
    [InlineData(8, 12, 2, new[] { 0x123F, 0xF000 })]
    [InlineData(8, 12, 3, new[] { 0x7F80, 0x7F80, 0x7F00 })]
    [InlineData(0, 0, 0x8000, new int[0])]
    [InlineData(0x0100, 0x0005, 0x8000, new int[0])]
    [InlineData(12, 8, 0x7FFC, new[] { 0xFFFF })]
    [InlineData(0xFFFF, 0x0001, 0x8000, new int[0])]
    public void Ot03_DeviceAndVariationIndexTables_LeaveOnlyTheValue(int first, int second, int deltaFormat, int[] deltas)
    {
        var a = Ot03G('A');
        var single = new Ot03Part().U16(1).Off(Ot03Cov(a)).U16(0x0044, -50).Off(new Ot03Part().U16(first, second, deltaFormat).U16(deltas));
        var shaper = Ot03GposShaper(Ot03Header([Ot03Lookup(1, 0, single)], [("kern", [0])]));
        Assert.Equal(Ot03Font.AdvanceWidth(a) - 50, shaper.Shape("A")[0].XAdvance);
    }

    // ------------------------------------------------------------ 2.10

    /// <summary>
    /// A GSUB 1.1 with lookups 0 (a→x), 1 (a→y), 2 (a→z) and the features „smcp“ (index 0, lookup 1), „liga“ (index 1, lookup 0) and
    /// „dlig“ (index 2, lookup 2), and the FeatureVariations given (null: none, version 1.0).
    /// </summary>
    private static Ot03Part Ot03VariedGsub(Ot03Part? featureVariations) =>
        Ot03Header([Ot03Single(('a', 'x')), Ot03Single(('a', 'y')), Ot03Single(('a', 'z'))],
            [("smcp", [1]), ("liga", [0]), ("dlig", [2])], featureVariations: featureVariations);

    /// <summary>FeatureVariations with one record: the ConditionSet given (null = NULL) and a substitution of the features given by a feature table of lookup 1.</summary>
    private static Ot03Part Ot03FeatureVariations(Ot03Part? conditionSet, params int[] featureIndices)
    {
        var substitution = new Ot03Part().U16(1, 0, featureIndices.Length);
        foreach (var index in featureIndices) substitution.U16(index).Off32(new Ot03Part().U16(0, 1, 1));
        return new Ot03Part().U16(1, 0).U32(1).Off32(conditionSet).Off32(substitution);
    }

    /// <summary>
    /// OT 2.10 / 21.2 (FG 1154): FeatureVariations count in a variable font, for its default instance (every normalized coordinate 0). A record
    /// substituting „liga“ (index 1) by a feature table of lookup 1 (a→y): without fvar the language system's own lookup applies („a“ → „x“);
    /// with fvar an empty ConditionSet, a NULL ConditionSet offset (both match every instance) and a format 1 condition over [−0.5, 0.5] (it
    /// holds the default instance) substitute („a“ → „y“), a condition of the unknown format 9 does not hold („x“).
    /// </summary>
    [Theory]
    [InlineData("leer", false, "x")]
    [InlineData("NULL", false, "x")]
    [InlineData("Format9", false, "x")]
    [InlineData("Format1", false, "x")]
    [InlineData("leer", true, "y")]
    [InlineData("NULL", true, "y")]
    [InlineData("Format9", true, "x")]
    [InlineData("Format1", true, "y")]
    public void Ot03_FeatureVariations_CountForTheDefaultInstanceOfAVariableFont(string condition, bool variable, string expected)
    {
        var conditionSet = condition switch
        {
            "leer" => new Ot03Part().U16(0),
            "NULL" => null,
            "Format9" => new Ot03Part().U16(1).Off32(new Ot03Part().U16(9, 0, 0, 0)),
            _ => new Ot03Part().U16(1).Off32(new Ot03Part().U16(1, 0, 0xE000, 0x2000)),
        };
        var fvar = new Ot03Part().U16(1, 0, 16, 2, 1, 20, 0, 8).Tag("wght").U32(0x00640000).U32(0x01900000).U32(0x03840000).U16(0, 256).Build();
        var font = ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
        {
            ["GSUB"] = Ot03VariedGsub(Ot03FeatureVariations(conditionSet, 1)).Build(),
            ["GPOS"] = null,
            ["GDEF"] = null,
            ["fvar"] = variable ? fvar : null,
        });
        Assert.Equal(Ot03Glyphs(expected), Ot03Shape(TextShaper.For(OpenTypeFont.Parse(font)), "a"));
    }

    /// <summary>
    /// OT 2.10.1, 2.10.3.1 and 2.10.4 (FG 1155), in a variable font with one axis: a FeatureVariations table of major version 2 is not read
    /// („a“ → „x“); a condition on axis 5 does not hold („x“); a first record whose substitution table has version 2 is passed over and the
    /// second record (empty ConditionSet, „liga“ → lookup 2, a→z) applies („z“).
    /// </summary>
    [Fact]
    public void Ot03_FeatureVariations_VersionsAndAxisIndexChecked()
    {
        var fvar = new Ot03Part().U16(1, 0, 16, 2, 1, 20, 0, 8).Tag("wght").U32(0x00640000).U32(0x01900000).U32(0x03840000).U16(0, 256).Build();
        string Shape(Ot03Part featureVariations) => string.Join(" ", Ot03Shape(TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(
            ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
            {
                ["GSUB"] = Ot03VariedGsub(featureVariations).Build(), ["GPOS"] = null, ["GDEF"] = null, ["fvar"] = fvar,
            }))), "a"));
        Ot03Part Substitution(int version, int lookup) => new Ot03Part().U16(version, 0, 1).U16(1).Off32(new Ot03Part().U16(0, 1, lookup));
        var x = string.Join(" ", Ot03Glyphs("x"));
        Assert.Equal(x, Shape(new Ot03Part().U16(2, 0).U32(1).Off32(new Ot03Part().U16(0)).Off32(Substitution(1, 1))));
        Assert.Equal(x, Shape(new Ot03Part().U16(1, 0).U32(1).Off32(new Ot03Part().U16(1).Off32(new Ot03Part().U16(1, 5, 0xE000, 0x2000)))
            .Off32(Substitution(1, 1))));
        Assert.Equal(string.Join(" ", Ot03Glyphs("z")), Shape(new Ot03Part().U16(1, 0).U32(2).Off32(new Ot03Part().U16(0)).Off32(Substitution(2, 1))
            .Off32(new Ot03Part().U16(0)).Off32(Substitution(1, 2))));
    }

    /// <summary>
    /// OT 2.10.1 sentences 15–21 (today's behaviour): a FeatureVariations table claiming 0xFFFFFFFF records and cut off after its count
    /// at the end of the GSUB — no exception, the lookups of the language system apply: „a“ → „x“.
    /// </summary>
    [Fact]
    public void Ot03_FeatureVariations_TruncatedWithHugeCount_NoException()
    {
        var shaper = Ot03Shaper(Ot03VariedGsub(new Ot03Part().U16(1, 0).U32(0xFFFFFFFF).AtEnd()));
        Assert.Equal(Ot03Glyphs("x"), Ot03Shape(shaper, "a"));
    }

    /// <summary>
    /// OT 2.10.4 sentences 16–21 (today's behaviour): substitution records for the feature indices 0 and 2 (by a table of lookup 1,
    /// a→y). Feature index 1 („liga“) has no record and keeps its table („a“ → „x“); index 2 („dlig“) keeps its own table too, since the
    /// records are not read („a“ with „dlig“ and without „liga“ → „z“, with the substitution it would be „y“).
    /// </summary>
    [Fact]
    public void Ot03_FeatureTableSubstitution_RecordsAreNotRead()
    {
        var shaper = Ot03Shaper(Ot03VariedGsub(Ot03FeatureVariations(new Ot03Part().U16(0), 0, 2)));
        Assert.Equal(Ot03Glyphs("x"), Ot03Shape(shaper, "a"));
        Assert.Equal(Ot03Glyphs("z"), Ot03Shape(shaper, "a", ["-liga", "dlig"]));
    }
}
