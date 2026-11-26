using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-22 (OpenType GPOS 20.4.8–20.4.11: contextual and chained contextual positioning, extension positioning, value records,
/// anchor tables, mark arrays), with GPOS and GDEF built by hand in Liberation Sans (builders of package ot-20).
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ builders: contexts (GPOS 7 and 8, the formats shared with GSUB)

    private static void Ot22Records(Ot20Bytes b, (int Sequence, int Lookup)[] records)
    {
        foreach (var (sequence, lookup) in records) b.U16(sequence).U16(lookup);
    }

    /// <summary>SequenceContextFormat1: one rule set (for the first input glyph) with one rule.</summary>
    private static Action<Ot20Bytes> Ot22Context1(int[] input, params (int Sequence, int Lookup)[] records) => b =>
    {
        var start = b.Here;
        string coverage = b.NewLabel(), set = b.NewLabel(), rule = b.NewLabel();
        b.U16(1).Off16(coverage, start).U16(1).Off16(set, start);
        b.Label(set);
        var setAt = b.Here;
        b.U16(1).Off16(rule, setAt);
        b.Label(rule).U16(input.Length).U16(records.Length);
        foreach (var g in input.Skip(1)) b.U16(g);
        Ot22Records(b, records);
        b.Label(coverage);
        Ot20Coverage(b, [input[0]]);
    };

    /// <summary>SequenceContextFormat2: coverage, ClassDef, one rule (classes, the first being that of the first glyph) in the set of its first class.</summary>
    private static Action<Ot20Bytes> Ot22Context2(int[] coverage, IReadOnlyDictionary<int, int> classes, int classCount, int[] rule,
        params (int Sequence, int Lookup)[] records) => b =>
    {
        var start = b.Here;
        string cov = b.NewLabel(), def = b.NewLabel(), set = b.NewLabel(), ruleLabel = b.NewLabel();
        b.U16(2).Off16(cov, start).Off16(def, start).U16(classCount);
        for (var c = 0; c < classCount; c++)
            if (c == rule[0]) b.Off16(set, start);
            else b.U16(0);
        b.Label(set);
        var setAt = b.Here;
        b.U16(1).Off16(ruleLabel, setAt);
        b.Label(ruleLabel).U16(rule.Length).U16(records.Length);
        foreach (var c in rule.Skip(1)) b.U16(c);
        Ot22Records(b, records);
        b.Label(cov);
        Ot20Coverage(b, coverage);
        b.Label(def);
        Ot20ClassDef(b, classes);
    };

    /// <summary>SequenceContextFormat3: one coverage per input glyph, then the records.</summary>
    private static Action<Ot20Bytes> Ot22Context3(int[][] input, params (int Sequence, int Lookup)[] records) => b =>
    {
        var start = b.Here;
        var labels = input.Select(_ => b.NewLabel()).ToArray();
        b.U16(3).U16(input.Length).U16(records.Length);
        foreach (var l in labels) b.Off16(l, start);
        Ot22Records(b, records);
        for (var i = 0; i < input.Length; i++)
        {
            b.Label(labels[i]);
            Ot20Coverage(b, input[i]);
        }
    };

    /// <summary>ChainedSequenceContextFormat1 with one rule set of one rule for the first input glyph (backtrack nearest first).</summary>
    private static Action<Ot20Bytes> Ot22Chain1(int[] backtrack, int[] input, int[] lookahead, params (int Sequence, int Lookup)[] records) => b =>
    {
        var start = b.Here;
        string coverage = b.NewLabel(), set = b.NewLabel(), rule = b.NewLabel();
        b.U16(1).Off16(coverage, start).U16(1).Off16(set, start);
        b.Label(set);
        var setAt = b.Here;
        b.U16(1).Off16(rule, setAt);
        b.Label(rule).U16(backtrack.Length);
        foreach (var g in backtrack) b.U16(g);
        b.U16(input.Length);
        foreach (var g in input.Skip(1)) b.U16(g);
        b.U16(lookahead.Length);
        foreach (var g in lookahead) b.U16(g);
        b.U16(records.Length);
        Ot22Records(b, records);
        b.Label(coverage);
        Ot20Coverage(b, [input[0]]);
    };

    /// <summary>ChainedSequenceContextFormat2: three ClassDefs, one rule of classes in the set of the first input class.</summary>
    private static Action<Ot20Bytes> Ot22Chain2(int[] coverage, IReadOnlyDictionary<int, int> backtrackClasses, IReadOnlyDictionary<int, int> inputClasses,
        IReadOnlyDictionary<int, int> lookaheadClasses, int classCount, (int[] Backtrack, int[] Input, int[] Lookahead) rule,
        params (int Sequence, int Lookup)[] records) => b =>
    {
        var start = b.Here;
        string cov = b.NewLabel(), bt = b.NewLabel(), inp = b.NewLabel(), la = b.NewLabel(), set = b.NewLabel(), ruleLabel = b.NewLabel();
        b.U16(2).Off16(cov, start).Off16(bt, start).Off16(inp, start).Off16(la, start).U16(classCount);
        for (var c = 0; c < classCount; c++)
            if (c == rule.Input[0]) b.Off16(set, start);
            else b.U16(0);
        b.Label(set);
        var setAt = b.Here;
        b.U16(1).Off16(ruleLabel, setAt);
        b.Label(ruleLabel).U16(rule.Backtrack.Length);
        foreach (var c in rule.Backtrack) b.U16(c);
        b.U16(rule.Input.Length);
        foreach (var c in rule.Input.Skip(1)) b.U16(c);
        b.U16(rule.Lookahead.Length);
        foreach (var c in rule.Lookahead) b.U16(c);
        b.U16(records.Length);
        Ot22Records(b, records);
        b.Label(cov);
        Ot20Coverage(b, coverage);
        foreach (var (label, classes) in new[] { (bt, backtrackClasses), (inp, inputClasses), (la, lookaheadClasses) })
        {
            b.Label(label);
            Ot20ClassDef(b, classes);
        }
    };

    /// <summary>ChainedSequenceContextFormat3: coverages of backtrack (nearest first), input and lookahead, then the records.</summary>
    private static Action<Ot20Bytes> Ot22Chain3(int[][] backtrack, int[][] input, int[][] lookahead, params (int Sequence, int Lookup)[] records) => b =>
    {
        var start = b.Here;
        var labels = backtrack.Concat(input).Concat(lookahead).Select(_ => b.NewLabel()).ToArray();
        var k = 0;
        b.U16(3).U16(backtrack.Length);
        foreach (var _ in backtrack) b.Off16(labels[k++], start);
        b.U16(input.Length);
        foreach (var _ in input) b.Off16(labels[k++], start);
        b.U16(lookahead.Length);
        foreach (var _ in lookahead) b.Off16(labels[k++], start);
        b.U16(records.Length);
        Ot22Records(b, records);
        k = 0;
        foreach (var glyphs in backtrack.Concat(input).Concat(lookahead))
        {
            b.Label(labels[k++]);
            Ot20Coverage(b, glyphs);
        }
    };

    private static int[] Ot22Gs(string text) => [.. text.Select(Ot20G)];

    /// <summary>
    /// The positions of <paramref name="text"/> with a GPOS whose „kern“ names only the context lookup 0 (nested lookups behind it), and with a
    /// GPOS whose „kern“ names the nested lookup <paramref name="nested"/> directly.
    /// </summary>
    private static (ShapedGlyph[] Context, ShapedGlyph[] Without, ShapedGlyph[] Direct) Ot22Contextual(int type, Action<Ot20Bytes> context, string text,
        int nested, params Action<Ot20Bytes>[] lookups)
    {
        var withContext = Ot20Shaper(Ot20Kern([Ot20Lookup(type, context), .. lookups])).Shape(text);
        var without = Ot20Shaper(null).Shape(text);
        var direct = Ot20Shaper(Ot20Layout([("kern", [nested])], [Ot20Lookup(type, context), .. lookups])).Shape(text);
        return (withContext, without, direct);
    }

    private static void Ot22SamePositions(ShapedGlyph[] expected, ShapedGlyph[] actual) =>
        Assert.Equal(expected.Select(g => (g.Glyph, g.XAdvance, g.XOffset, g.YOffset)), actual.Select(g => (g.Glyph, g.XAdvance, g.XOffset, g.YOffset)));

    // ------------------------------------------------------------ OT 20.4.8 (GPOS 7, docs/FEHLERGEDAECHTNIS.md, No. 1161)

    /// <summary>
    /// OT 20.4.8 sentences 1–5: contextual positioning applies nested lookups at places of the input sequence. Feature „kern“ with a single lookup
    /// of type 7, format 3 (coverages {A}{V}, record (1, lookup 1 = XPlacement −120)): V in „AV“ gets XOffset −120, as lookup 1 named directly
    /// gives, A stays (before No. 1161 the lookup was passed over).
    /// </summary>
    [Fact]
    public void Ot22_ContextFormat3_MovesTheGlyphAtItsPlace()
    {
        var (context, without, direct) = Ot22Contextual(7, Ot22Context3([[Ot20G('A')], [Ot20G('V')]], (1, 1)), "AV", 1,
            Ot20Lookup(1, Ot20Single1([Ot20G('V')], 1, -120)));
        Assert.Equal(-120, direct[1].XOffset);
        Ot22SamePositions(direct, context);
        Assert.Equal((without[0].XAdvance, 0.0), (context[0].XAdvance, context[0].XOffset));
    }

    /// <summary>
    /// OT 20.4.8 sentences 7–8: the records of a GPOS context name lookups of the GPOS LookupList, so they only position. Type 7, format 1 with the
    /// record (0, lookup 3), lookup 3 = YPlacement +200 for A (lookups 1 and 2 act on B only): A is raised by 200.
    /// </summary>
    [Fact]
    public void Ot22_ContextRecordNamesAGposLookup_ThatLookupPositions()
    {
        var (context, _, direct) = Ot22Contextual(7, Ot22Context1([Ot20G('A')], (0, 3)), "A", 3,
            Ot20Lookup(1, Ot20Single1([Ot20G('B')], 2, 1)), Ot20Lookup(1, Ot20Single1([Ot20G('B')], 2, 2)), Ot20Lookup(1, Ot20Single1([Ot20G('A')], 2, 200)));
        Assert.Equal(200, direct[0].YOffset);
        Assert.Equal(200, context[0].YOffset);
    }

    /// <summary>
    /// OT 20.4.8 sentences 9–10, 15: format 1 matches a sequence of glyphs. Coverage {T}, rule T o with the record (1, lookup 1 = YPlacement −50):
    /// o in „To“ is lowered by 50, in „Tx“ the rule does not match.
    /// </summary>
    [Fact]
    public void Ot22_ContextFormat1_GlyphSequence()
    {
        var (context, _, direct) = Ot22Contextual(7, Ot22Context1(Ot22Gs("To"), (1, 1)), "To", 1, Ot20Lookup(1, Ot20Single1([Ot20G('o'), Ot20G('x')], 2, -50)));
        Assert.Equal(-50, direct[1].YOffset);
        Ot22SamePositions(direct, context);
        Assert.Equal(0, Ot22Contextual(7, Ot22Context1(Ot22Gs("To"), (1, 1)), "Tx", 1, Ot20Lookup(1, Ot20Single1([Ot20G('o'), Ot20G('x')], 2, -50))).Context[1].YOffset);
    }

    /// <summary>
    /// OT 20.4.8 sentences 18–21, 26: format 2 matches classes. ClassDef {A, T → 1, comma → 2}, rule 1 2 with the record (1, lookup 1 = XPlacement
    /// −80): the comma in „T,“ and in „A,“ moves by −80, in „V,“ (V of class 0) it stays.
    /// </summary>
    [Fact]
    public void Ot22_ContextFormat2_Classes()
    {
        var classes = new Dictionary<int, int> { [Ot20G('A')] = 1, [Ot20G('T')] = 1, [Ot20G(',')] = 2 };
        var nested = Ot20Lookup(1, Ot20Single1([Ot20G(',')], 1, -80));
        foreach (var (text, expected) in new[] { ("T,", -80.0), ("A,", -80.0), ("V,", 0.0) })
            Assert.Equal(expected, Ot22Contextual(7, Ot22Context2(Ot22Gs("AT"), classes, 3, [1, 2], (1, 1)), text, 1, nested).Context[1].XOffset);
    }

    /// <summary>
    /// OT 20.4.8 sentences 29–35, 42: format 3 matches one coverage per place, a glyph may be in several. Coverages {A–Z} {I} {A–Z} with the record
    /// (1, lookup 1 = XAdvance +30): I in „AIA“ is 30 wider, the capitals around it are not changed.
    /// </summary>
    [Fact]
    public void Ot22_ContextFormat3_GlyphInSeveralCoverages()
    {
        var capitals = Ot22Gs("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        var (context, without, direct) = Ot22Contextual(7, Ot22Context3([capitals, [Ot20G('I')], capitals], (1, 1)), "AIA", 1,
            Ot20Lookup(1, Ot20Single1([Ot20G('I')], 4, 30)));
        Assert.Equal(Ot20Adv('I') + 30, context[1].XAdvance);
        Assert.Equal((without[0].XAdvance, without[2].XAdvance), (context[0].XAdvance, context[2].XAdvance));
        Assert.Equal(Ot20Adv('I') + 30, Ot22Contextual(7, Ot22Context3([capitals, [Ot20G('I')], capitals], (1, 1)), "III", 1,
            Ot20Lookup(1, Ot20Single1([Ot20G('I')], 4, 30))).Context[1].XAdvance);
    }

    /// <summary>
    /// OT 20.4.8 sentences 1–5 with the lookup flags: the input sequence passes over what the context lookup skips, and a record's place counts in
    /// it. Format 3 {A}{V} with IGNORE_MARKS and GDEF marking the acute as a mark, record (1, lookup 1 = YPlacement +70 for V and the acute):
    /// in „Á“ followed by V the V (third glyph) is raised, the acute in between is not.
    /// </summary>
    [Fact]
    public void Ot22_Context_PlacesInTheInputPassOverSkippedMarks()
    {
        var acute = Ot20G('́');
        var gpos = Ot20Kern(Ot20Lookup(7, 8, Ot22Context3([[Ot20G('A')], [Ot20G('V')]], (1, 1))),
            Ot20Lookup(1, Ot20Single1([Ot20G('V'), acute], 2, 70)));
        var shaped = Ot20Shaper(gpos, Ot20Gdef(new Dictionary<int, int> { [acute] = 3 })).Shape("ÁV");
        Assert.Equal([Ot20G('A'), acute, Ot20G('V')], shaped.Select(g => g.Glyph));
        Assert.Equal([0.0, 0, 70], shaped.Select(g => g.YOffset));
    }

    /// <summary>
    /// OT 20.4.8 sentences 7–8 and the work limits (docs/FEHLERGEDAECHTNIS.md, No. 1137): a context whose record names the context lookup itself
    /// nests no deeper than the limit, and a long text with such a context is shaped in bounded time; the glyphs and advances stay those of the
    /// font.
    /// </summary>
    [Fact]
    public void Ot22_ContextNamingItself_Bounded()
    {
        var gpos = Ot20Kern(Ot20Lookup(7, Ot22Context3([[Ot20G('A')]], (0, 0))));
        var shaper = Ot20Shaper(gpos);
        var text = new string('A', 20_000);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var shaped = shaper.Shape(text);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        Assert.Equal(text.Length, shaped.Length);
        Assert.All(shaped, g => Assert.Equal(Ot20Adv('A'), g.XAdvance));
    }

    // ------------------------------------------------------------ OT 20.4.9 (GPOS 8, No. 1161)

    /// <summary>Liberation Sans with the type of every lookup of type 8 in its GPOS set to 0 (no lookup type: the subtables are passed over).</summary>
    private static byte[] Ot22WithoutType8(byte[] gpos)
    {
        var copy = (byte[])gpos.Clone();
        var t = new TableData(gpos);
        var list = t.U16(8);
        for (var i = 0; i < t.U16(list); i++)
        {
            var at = list + t.U16(list + 2 + i * 2);
            if (t.U16(at) == 8) copy[at] = copy[at + 1] = 0;
        }
        return copy;
    }

    /// <summary>A GSUB or GPOS whose every script record names the script table of <paramref name="script"/>: the shaper takes its lookups for any text.</summary>
    private static byte[] Ot22OnlyScript(byte[] table, string script)
    {
        var copy = (byte[])table.Clone();
        var t = new TableData(table);
        var list = t.U16(4);
        var offset = -1;
        for (var i = 0; i < t.U16(list); i++)
            if (t.Tag(list + 2 + i * 6) == script) offset = t.U16(list + 2 + i * 6 + 4);
        Assert.True(offset > 0);
        for (var i = 0; i < t.U16(list); i++)
        {
            var at = list + 2 + i * 6 + 4;
            copy[at] = (byte)(offset >> 8);
            copy[at + 1] = (byte)offset;
        }
        return copy;
    }

    /// <summary>
    /// OT 20.1 sentences 53–57 and OT 20.4.9 sentences 1–5: chained contextual positioning. The bundled Liberation Sans Regular has lookups of type 8
    /// in its feature „mark“ of the script „hebr“ („latn“ names only mark-to-base lookups). Shaped with the „hebr“ lookups, a bet with qamats and
    /// etnahta and a dalet with sheva and tevir place their marks where the bundled shaping library places them for the font itself (the
    /// accent moved by the chained contexts beside the vowel point), and other than with the type 8 lookups taken out.
    /// </summary>
    [Fact]
    public void Ot22_LiberationChainedPositioning_HebrewMarksAsTheShapingLibrary()
    {
        var gpos = Ot20Lib.TableData("GPOS").ToArray();
        var table = new LayoutTable(gpos, 9);
        Assert.Contains(table.LookupsFor(["mark"], "hebr"), l => table.Lookup(l).Type == 8);
        TextShaper Hebrew(byte[] layout) => TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(ShaperLimitTests.Liberation,
            new Dictionary<string, byte[]?> { ["GPOS"] = Ot22OnlyScript(layout, "hebr"), ["GSUB"] = Ot22OnlyScript(Ot20Lib.TableData("GSUB").ToArray(), "hebr") })));
        var shaper = Hebrew(gpos);
        var stripped = Hebrew(Ot22WithoutType8(gpos));
        // the marks of one base letter: x from the origin of the base, y
        static (int Glyph, double X, double Y)[] Marks(ShapedGlyph[] g) => [.. g.Skip(1).Select(m => (m.Glyph, g[0].XAdvance + m.XOffset, m.YOffset)).OrderBy(m => m.Glyph)];
        foreach (var text in new[] { "בָ֑", "דְ֚" })
        {
            var own = shaper.Shape(text);
            // the library sets Hebrew right to left: in its visual order the marks (no advance) stand before their base, at its origin
            var hb = ShaperTests.HarfBuzz(ShaperLimitTests.Liberation, Ot20Lib.UnitsPerEm, text, []);
            Assert.Equal(own.Length, hb.Glyphs.Length);
            Assert.Equal(own[0].Glyph, hb.Glyphs[^1]);
            var expected = hb.Glyphs[..^1].Select((g, i) => (g, (double)hb.XOffsets[i], (double)hb.YOffsets[i])).OrderBy(m => m.g).ToArray();
            Assert.Equal(expected, Marks(own));
            Assert.NotEqual(Marks(own), Marks(stripped.Shape(text)));
        }
    }

    /// <summary>
    /// OT 20.4.9 sentences 1–5 and 20–27: format 3 of type 8 — coverages for backtrack, input and lookahead; only the input is adjusted. Backtrack
    /// {x}, input {a}, lookahead {y}, record (0, lookup 1 = YPlacement +100 for x, a and y): a in „xay“ is raised by 100, x and y are not; in
    /// „xaz“ (lookahead not met) and „bay“ (backtrack not met) nothing moves.
    /// </summary>
    [Fact]
    public void Ot22_ChainFormat3_OnlyTheInputIsAdjusted()
    {
        var chain = Ot22Chain3([[Ot20G('x')]], [[Ot20G('a')]], [[Ot20G('y')]], (0, 1));
        var nested = Ot20Lookup(1, Ot20Single1([Ot20G('x'), Ot20G('a'), Ot20G('y')], 2, 100));
        var (context, _, direct) = Ot22Contextual(8, chain, "xay", 1, nested);
        Assert.Equal([100.0, 100, 100], direct.Select(g => g.YOffset));
        Assert.Equal([0.0, 100, 0], context.Select(g => g.YOffset));
        foreach (var text in new[] { "xaz", "bay" })
            Assert.All(Ot22Contextual(8, chain, text, 1, nested).Context, g => Assert.Equal(0, g.YOffset));
    }

    /// <summary>
    /// OT 20.4.9 sentences 7–8, 10–11: format 1 of type 8 — sequences of glyphs. Backtrack [x], input [a], lookahead [y], record (0, lookup 1 =
    /// XPlacement +40): a in „xay“ moves by +40, in „xa“ (no lookahead at the end of the run) it stays.
    /// </summary>
    [Fact]
    public void Ot22_ChainFormat1_GlyphSequences()
    {
        var chain = Ot22Chain1([Ot20G('x')], [Ot20G('a')], [Ot20G('y')], (0, 1));
        var nested = Ot20Lookup(1, Ot20Single1([Ot20G('a')], 1, 40));
        var (context, _, direct) = Ot22Contextual(8, chain, "xay", 1, nested);
        Assert.Equal(40, direct[1].XOffset);
        Ot22SamePositions(direct, context);
        Assert.Equal(0, Ot22Contextual(8, chain, "xa", 1, nested).Context[1].XOffset);
    }

    /// <summary>
    /// OT 20.4.9 sentences 13–18: format 2 of type 8 — three ClassDefs for backtrack, input and lookahead. {x → 1}, {a → 1}, {y → 1}, record (0,
    /// lookup 1 = XPlacement +40): a in „xay“ moves by +40; in „yax“ (backtrack and lookahead of class 0 in their ClassDefs) it stays.
    /// </summary>
    [Fact]
    public void Ot22_ChainFormat2_ThreeClassDefs()
    {
        var chain = Ot22Chain2([Ot20G('a')], new Dictionary<int, int> { [Ot20G('x')] = 1 }, new Dictionary<int, int> { [Ot20G('a')] = 1 },
            new Dictionary<int, int> { [Ot20G('y')] = 1 }, 2, ([1], [1], [1]), (0, 1));
        var nested = Ot20Lookup(1, Ot20Single1([Ot20G('a')], 1, 40));
        Assert.Equal(40, Ot22Contextual(8, chain, "xay", 1, nested).Context[1].XOffset);
        Assert.Equal(0, Ot22Contextual(8, chain, "yax", 1, nested).Context[1].XOffset);
    }

    // ------------------------------------------------------------ OT 20.4.10

    /// <summary>
    /// The GPOS given (version 1.0) with lookup <paramref name="index"/> packed into extensions: a new LookupList and a lookup of type 9 behind the
    /// header, one PosExtensionFormat1 subtable per subtable (its 32-bit offset to the subtable where it was), the rest of the table moved behind.
    /// </summary>
    private static byte[] Ot22WrapAsExtension(byte[] gpos, int index)
    {
        var t = new TableData(gpos);
        Assert.Equal(0, t.U16(2));
        int lookupList = t.U16(8), count = t.U16(lookupList);
        int LookupAt(int j) => lookupList + t.U16(lookupList + 2 + j * 2);
        var at = LookupAt(index);
        int type = t.U16(at), flag = t.U16(at + 2), subs = t.U16(at + 4);
        var markSet = (flag & 0x10) != 0;
        int listSize = 2 + 2 * count, tableSize = 6 + 2 * subs + (markSet ? 2 : 0);
        var moved = listSize + tableSize + 8 * subs;
        var b = new Ot20Bytes();
        Assert.InRange(Math.Max(t.U16(4), t.U16(6)) + moved, 0, 0xFFFF);
        b.U16(1).U16(0).U16(t.U16(4) + moved).U16(t.U16(6) + moved).U16(10);
        b.U16(count);
        for (var j = 0; j < count; j++)
        {
            var offset = j == index ? listSize : LookupAt(j) + moved - 10;
            Assert.InRange(offset, 1, 0xFFFF);
            b.U16(offset);
        }
        b.U16(9).U16(flag).U16(subs);
        for (var i = 0; i < subs; i++) b.U16(tableSize + 8 * i);
        if (markSet) b.U16(t.U16(at + 6 + subs * 2));
        for (var i = 0; i < subs; i++)
        {
            var extension = 10 + listSize + tableSize + 8 * i;
            b.U16(1).U16(type).U32(at + t.U16(at + 6 + i * 2) + moved - extension);
        }
        b.Bytes(gpos.Skip(10));
        return b.ToArray();
    }

    /// <summary>
    /// OT 20.4.10 sentences 1–4: extension positioning reaches subtables by 32-bit offsets. Liberation Sans with its GPOS rebuilt so that the
    /// kerning lookup (type 2) is packed as type 9 with PosExtensionFormat1 subtables: „AVAV“ and „To“ have the same advances as with the original
    /// GPOS (no bundled font uses type 9).
    /// </summary>
    [Fact]
    public void Ot22_LiberationKerningPackedAsExtension_SameAdvances()
    {
        var gpos = Ot20Lib.TableData("GPOS").ToArray();
        var kern = Assert.Single(new LayoutTable(gpos, 9).LookupsFor(["kern"]));
        var wrapped = Ot22WrapAsExtension(gpos, kern);
        var table = new LayoutTable(wrapped, 9);
        Assert.Equal(9, table.T.U16(table.T.U16(8) + table.T.U16(table.T.U16(8) + 2 + kern * 2)));
        Assert.Equal(2, table.Lookup(kern).Type);
        var packed = TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(ShaperLimitTests.Liberation,
            new Dictionary<string, byte[]?> { ["GPOS"] = wrapped })));
        var original = TextShaper.For(Ot20Lib);
        foreach (var text in new[] { "AVAV", "To" })
        {
            Assert.NotEqual(text.Sum(Ot20Adv), original.Shape(text).Sum(g => g.XAdvance));
            Ot22SamePositions(original.Shape(text), packed.Shape(text));
        }
    }

    /// <summary>
    /// OT 20.4.10 sentences 5–10: PosExtensionFormat1 — format, extensionLookupType, extensionOffset (32 bits, from the start of the extension
    /// subtable). The extension 0001 0002 00000010 with a pair positioning 16 bytes behind it: the lookup reads as type 2 with its subtable at
    /// the extension + 16, and kerns A–V; with the extensionOffset 0xFFFFFFF0 there is no exception and no effect.
    /// </summary>
    [Fact]
    public void Ot22_ExtensionOffset_CountedFromTheExtension_HugeOffsetHarmless()
    {
        byte[] Gpos(long offset) => Ot20Kern(Ot20Lookup(9, b =>
        {
            b.U16(1).U16(2).U32(offset).U32(0).U32(0);                     // the extension and eight bytes of padding
            Ot20Pair1(4, 0, (Ot20G('A'), Ot20G('V'), [-80], []))(b);
        }));
        var gpos = Gpos(16);
        var table = new LayoutTable(gpos, 9);
        int list = table.T.U16(8), lookup = list + table.T.U16(list + 2), extension = lookup + table.T.U16(lookup + 6);
        Assert.Equal(2, table.Lookup(0).Type);
        Assert.Equal([extension + 16], table.Lookup(0).Subtables);
        Assert.Equal(Ot20Adv('A') - 80, Ot20Shaper(gpos).Shape("AV")[0].XAdvance);
        Assert.Equal(Ot20Adv('A'), Ot20Shaper(Gpos(0xFFFFFFF0)).Shape("AV")[0].XAdvance);
        // an offset beyond the table names no subtable: the end of the table, not 16 bytes before the extension (FG 1150)
        var huge = new LayoutTable(Gpos(0xFFFFFFF0), 9);
        Assert.Equal([huge.T.Length], huge.Lookup(0).Subtables);
    }

    /// <summary>
    /// OT 20.4.10 sentence 13: offsets to extensions count from the start of the PosExtensionFormat1 subtable. Two extensions of one lookup at
    /// different places, both with the extensionOffset 8: two different subtables, each 8 bytes behind its extension (A–V −80 and A–W −50).
    /// </summary>
    [Fact]
    public void Ot22_SameExtensionOffset_TwoSubtables()
    {
        var gpos = Ot20Kern(Ot20Lookup(9, Ot20Extension(2, Ot20Pair1(4, 0, (Ot20G('A'), Ot20G('V'), [-80], []))),
            Ot20Extension(2, Ot20Pair1(4, 0, (Ot20G('A'), Ot20G('W'), [-50], [])))));
        var table = new LayoutTable(gpos, 9);
        int list = table.T.U16(8), lookup = list + table.T.U16(list + 2);
        var extensions = new[] { lookup + table.T.U16(lookup + 6), lookup + table.T.U16(lookup + 8) };
        Assert.All(extensions, e => Assert.Equal(8, table.T.U32(e + 4)));
        Assert.Equal(extensions.Select(e => e + 8), table.Lookup(0).Subtables);
        var shaper = Ot20Shaper(gpos);
        Assert.Equal(Ot20Adv('A') - 80, shaper.Shape("AV")[0].XAdvance);
        Assert.Equal(Ot20Adv('A') - 50, shaper.Shape("AW")[0].XAdvance);
    }

    /// <summary>
    /// OT 20.4.10 sentences 14–16: a lookup of type 9 is processed as if it had the type of its extensions and each extension were replaced by its
    /// subtable — the lookup flags stay. Type 9 with IGNORE_MARKS over a pair positioning A–V −80 and GDEF with U+0301 a mark: „A“ U+0301 „V“ is
    /// kerned by −80, as with a lookup of type 2 and the same flags.
    /// </summary>
    [Fact]
    public void Ot22_ExtensionLookup_KeepsItsFlags()
    {
        var pair = Ot20Pair1(4, 0, (Ot20G('A'), Ot20G('V'), [-80], []));
        var gdef = Ot20Gdef(new Dictionary<int, int> { [Ot20G('́')] = 3 });
        var packed = Ot20Shaper(Ot20Kern(Ot20Lookup(9, 8, Ot20Extension(2, pair))), gdef).Shape("ÁV");
        var direct = Ot20Shaper(Ot20Kern(Ot20Lookup(2, 8, pair)), gdef).Shape("ÁV");
        Assert.Equal(Ot20Adv('A') - 80, packed[0].XAdvance);
        Ot22SamePositions(direct, packed);
    }

    // ------------------------------------------------------------ OT 20.4.11.1

    /// <summary>
    /// OT 20.4.11.1 sentences 3–4: a ValueRecord may name a Device table (or VariationIndex table) per value. Pair positioning format 1 with
    /// valueFormat1 0x0044 (XAdvance and its Device table, +7 at 10–12 ppem): „AV“ moves only by the XAdvance −80, and the next PairValueRecord
    /// (A–W −50) is read behind the four bytes of the value.
    /// </summary>
    [Fact]
    public void Ot22_PairValueWithDeviceTable_OnlyTheAdvance()
    {
        var shaper = Ot20Shaper(Ot20Kern(Ot20Lookup(2, b =>
        {
            var start = b.Here;
            string coverage = b.NewLabel(), set = b.NewLabel(), device = b.NewLabel();
            b.U16(1).Off16(coverage, start).U16(0x0044).U16(0).U16(1).Off16(set, start);
            b.Label(set);
            var setAt = b.Here;
            b.U16(2).U16(Ot20G('V')).U16(-80 & 0xFFFF).Off16(device, setAt).U16(Ot20G('W')).U16(-50 & 0xFFFF).Off16(device, setAt);
            b.Label(device).Bytes(Ot20Device());
            b.Label(coverage);
            Ot20Coverage(b, [Ot20G('A')]);
        })));
        Assert.Equal(Ot20Adv('A') - 80, shaper.Shape("AV")[0].XAdvance);
        Assert.Equal(Ot20Adv('A') - 50, shaper.Shape("AW")[0].XAdvance);
    }

    /// <summary>
    /// OT 20.4.11.1 sentences 5–10: the fields present follow the ValueFormat, in their defined order, a missing field moving the next one up.
    /// Single positioning format 1 with ValueFormat 0x000A (YPlacement +20, YAdvance +30): YOffset 20, XOffset 0, YAdvance 30 — the YAdvance is the
    /// second word, not the fourth.
    /// </summary>
    [Fact]
    public void Ot22_ValueRecord_FieldsMoveUp()
    {
        var shaped = Ot20Shaper(Ot20Kern(Ot20Lookup(1, Ot20Single1([Ot20G('A')], 0x000A, 20, 30)))).Shape("A");
        Assert.Equal((0.0, 20.0, Ot20Adv('A'), 30.0), (shaped[0].XOffset, shaped[0].YOffset, shaped[0].XAdvance, shaped[0].YAdvance));
    }

    /// <summary>
    /// OT 20.4.11.1 sentences 12–17: xPlacement, yPlacement, xAdvance, yAdvance, each a signed 16-bit value in design units. ValueFormat 0x000F
    /// with −1, −2, −3, −4: XOffset −1, YOffset −2, XAdvance 3 less, YAdvance −4.
    /// </summary>
    [Fact]
    public void Ot22_ValueRecord_FourSignedValues()
    {
        var shaped = Ot20Shaper(Ot20Kern(Ot20Lookup(1, Ot20Single1([Ot20G('A')], 0x000F, -1, -2, -3, -4)))).Shape("A");
        Assert.Equal((-1.0, -2.0, Ot20Adv('A') - 3, -4.0), (shaped[0].XOffset, shaped[0].YOffset, shaped[0].XAdvance, shaped[0].YAdvance));
    }

    /// <summary>
    /// OT 20.4.11.1 sentences 18–21: the offsets to Device or VariationIndex tables count from the parent table (here the SinglePos subtable), NULL
    /// allowed. Format 2 with ValueFormat 0x00F5 (XPlacement, XAdvance and all four device offsets, some NULL, some to a Device table) and three
    /// records: each record lies at the subtable + 8 + index × 12, and only XPlacement and XAdvance act.
    /// </summary>
    [Fact]
    public void Ot22_ValueRecordWithAllDeviceOffsets_RecordsOfTwelveBytes()
    {
        var shaper = Ot20Shaper(Ot20Kern(Ot20Lookup(1, b =>
        {
            var start = b.Here;
            string coverage = b.NewLabel(), device = b.NewLabel();
            b.U16(2).Off16(coverage, start).U16(0x00F5).U16(3);
            foreach (var (placement, advance) in new[] { (5, 10), (6, 20), (7, 30) })
            {
                b.U16(placement).U16(advance).Off16(device, start).U16(0).Off16(device, start).U16(0);
            }
            b.Label(device).Bytes(Ot20Device());
            b.Label(coverage);
            Ot20Coverage(b, Ot22Gs("ABC"));
        })));
        var shaped = shaper.Shape("ABC");
        Assert.Equal([5.0, 6, 7], shaped.Select(g => g.XOffset));
        Assert.Equal([Ot20Adv('A') + 10, Ot20Adv('B') + 20, Ot20Adv('C') + 30], shaped.Select(g => g.XAdvance));
        Assert.All(shaped, g => Assert.Equal((0.0, 0.0), (g.YOffset, g.YAdvance)));
    }

    /// <summary>
    /// OT 20.4.11.1 sentences 26–33 (and example 3): within a subtable one ValueFormat holds for all records of a place. Single positioning format 2
    /// with ValueFormat 0x0005 and the records 50/50, 25/25, 10/10: the three glyphs take their own XPlacement and XAdvance.
    /// </summary>
    [Fact]
    public void Ot22_SinglePositioningFormat2_OneValueFormatThreeRecords()
    {
        var shaped = Ot20Shaper(Ot20Kern(Ot20Lookup(1, Ot20Single2(Ot22Gs("ABC"), 0x0005, [[50, 50], [25, 25], [10, 10]])))).Shape("ABC");
        Assert.Equal([50.0, 25, 10], shaped.Select(g => g.XOffset));
        Assert.Equal([Ot20Adv('A') + 50, Ot20Adv('B') + 25, Ot20Adv('C') + 10], shaped.Select(g => g.XAdvance));
    }

    /// <summary>
    /// OT 20.4.11.1 sentences 41–51: the ValueFormat bits 0x0001–0x0080 and the reserved 0xFF00. ValueFormat 0x0102 (YPlacement and a reserved
    /// bit) with the records +10, +20: B stands 20 higher — the records are two bytes, the reserved bit does not count; 0x0080 alone (the
    /// XAdvance device offset) changes nothing.
    /// </summary>
    [Fact]
    public void Ot22_ValueFormat_ReservedBitsDoNotCount()
    {
        var shaped = Ot20Shaper(Ot20Kern(Ot20Lookup(1, Ot20Single2(Ot22Gs("AB"), 0x0102, [[10], [20]])))).Shape("AB");
        Assert.Equal([10.0, 20], shaped.Select(g => g.YOffset));
        var device = Ot20Shaper(Ot20Kern(Ot20Lookup(1, Ot20Single2(Ot22Gs("AB"), 0x0080, [[0], [0]])))).Shape("AB");
        Ot22SamePositions(Ot20Shaper(null).Shape("AB"), device);
    }

    // ------------------------------------------------------------ OT 20.4.11.2, 20.4.11.3

    private static TextShaper Ot22MarkShaper(Action<Ot20Bytes> markBase) =>
        Ot20Shaper(Ot20Layout([("mark", [0])], [Ot20Lookup(4, markBase)]), Ot20Gdef(new Dictionary<int, int>
        {
            [Ot20G('a')] = 1, [Ot20G('́')] = 3, [Ot20G('̂')] = 3,
        }));

    /// <summary>
    /// OT 20.4.11.2 sentences 27–37: AnchorFormat3 adds Device (or VariationIndex) tables per coordinate; without them the value holds for all
    /// sizes and instances. Mark-to-base whose mark anchor is format 3 (100, 0, xDeviceOffset to a Device table): the mark's place follows from
    /// 100/0 alone.
    /// </summary>
    [Fact]
    public void Ot22_AnchorFormat3_DeviceTablePassedOver()
    {
        var anchor3 = Ot20Raw(b =>
        {
            var device = b.NewLabel();
            b.U16(3).U16(100).U16(0).Off16(device, 0).U16(0);
            b.Label(device).Bytes(Ot20Device());
        });
        var shaped = Ot22MarkShaper(Ot20MarkAttach([(Ot20G('́'), 0, anchor3)], [(Ot20G('a'), [Ot20Anchor(500, 600)])], 1)).Shape("á");
        Assert.Equal((500 - 100 - Ot20Adv('a'), 600.0), (shaped[1].XOffset, shaped[1].YOffset));
    }

    /// <summary>
    /// OT 20.4.11.2 sentences 39–45: AnchorFormat3 — format, xCoordinate, yCoordinate, xDeviceOffset and yDeviceOffset from the anchor's start (NULL
    /// allowed). The anchor 0003 0064 FFCE 0000 000A (x 100, y −50, xDevice NULL, yDevice at +10) as the mark anchor: the mark is placed by
    /// (100, −50).
    /// </summary>
    [Fact]
    public void Ot22_AnchorFormat3_CoordinatesRead()
    {
        var anchor = Ot20Raw(b => b.U16(3).U16(100).U16(0xFFCE).U16(0).U16(10).Bytes(Ot20Device()));
        var shaped = Ot22MarkShaper(Ot20MarkAttach([(Ot20G('́'), 0, anchor)], [(Ot20G('a'), [Ot20Anchor(500, 600)])], 1)).Shape("á");
        Assert.Equal((500 - 100 - Ot20Adv('a'), 650.0), (shaped[1].XOffset, shaped[1].YOffset));
    }

    /// <summary>
    /// OT 20.4.11.3 sentence 5: class 0 is allowed and stands in the MarkRecord. Mark-to-base with two classes, the mark's record of class 0, the base
    /// anchors (300, 700) for class 0 and (300, −100) for class 1: the mark is placed at 300/700.
    /// </summary>
    [Fact]
    public void Ot22_MarkRecordOfClassZero_AnchorOfClassZero()
    {
        var shaped = Ot22MarkShaper(Ot20MarkAttach([(Ot20G('́'), 0, Ot20Anchor(0, 0))], [(Ot20G('a'), [Ot20Anchor(300, 700), Ot20Anchor(300, -100)])], 2))
            .Shape("á");
        Assert.Equal((300 - Ot20Adv('a'), 700.0), (shaped[1].XOffset, shaped[1].YOffset));
    }

    /// <summary>
    /// OT 20.4.11.3 sentences 8–15: markCount and the MarkRecords in the order of the mark coverage, each with class and anchor offset from the
    /// MarkArray. A MarkArray of markCount 1 under a coverage of two marks: the second mark of the coverage (U+0302) stays where it is, without an
    /// exception; the first (U+0301) is attached.
    /// </summary>
    [Fact]
    public void Ot22_MarkArrayShorterThanTheCoverage_MarkBeyondStays()
    {
        var shaper = Ot22MarkShaper(Ot20MarkAttach([(Ot20G('́'), 0, Ot20Anchor(0, 0)), (Ot20G('̂'), 0, Ot20Anchor(0, 0))],
            [(Ot20G('a'), [Ot20Anchor(500, 600)])], 1, markCount: 1));
        var beyond = shaper.Shape("â");
        Assert.Equal((0.0, 0.0, 0), (beyond[1].XOffset, beyond[1].YOffset, beyond[1].AttachBack));
        Assert.Equal(600, shaper.Shape("á")[1].YOffset);
    }
}
