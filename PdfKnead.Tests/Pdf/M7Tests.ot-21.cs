using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-21 (OpenType GPOS 20.4.1–20.4.7: header, single, pair and cursive positioning, mark-to-base, mark-to-ligature and
/// mark-to-mark attachment), with GPOS, GSUB and GDEF built by hand in Liberation Sans (builders of package ot-20) and Noto Sans Arabic.
/// </summary>
public sealed partial class M7NormTests
{
    private static int Ot21Acute => Ot20G('́');
    private static int Ot21Grave => Ot20G('̀');
    private static int Ot21Circumflex => Ot20G('̂');
    private static int Ot21Tilde => Ot20G('̃');
    private static int Ot21Fi => Ot20G('ﬁ');
    private static int Ot21Fl => Ot20G('ﬂ');

    /// <summary>Ligature substitution format 1 (GSUB 4); ligatures with the same first glyph share a set.</summary>
    private static Action<Ot20Bytes> Ot21Ligature(params (int[] Components, int Glyph)[] ligatures) => b =>
    {
        var start = b.Here;
        var coverage = b.NewLabel();
        var groups = ligatures.GroupBy(l => l.Components[0]).OrderBy(g => g.Key).ToArray();
        var sets = groups.Select(_ => b.NewLabel()).ToArray();
        b.U16(1).Off16(coverage, start).U16(groups.Length);
        foreach (var s in sets) b.Off16(s, start);
        for (var i = 0; i < groups.Length; i++)
        {
            b.Label(sets[i]);
            var set = b.Here;
            var members = groups[i].ToArray();
            var labels = members.Select(_ => b.NewLabel()).ToArray();
            b.U16(members.Length);
            foreach (var l in labels) b.Off16(l, set);
            for (var k = 0; k < members.Length; k++)
            {
                b.Label(labels[k]).U16(members[k].Glyph).U16(members[k].Components.Length);
                foreach (var c in members[k].Components.Skip(1)) b.U16(c);
            }
        }
        b.Label(coverage);
        Ot20Coverage(b, groups.Select(g => g.Key));
    };

    /// <summary>A GSUB whose „liga“ joins the ligatures given, passing over marks (IGNORE_MARKS).</summary>
    private static byte[] Ot21Liga(params (int[] Components, int Glyph)[] ligatures) =>
        Ot20Layout([("liga", [0])], [Ot20Lookup(4, 8, Ot21Ligature(ligatures))]);

    /// <summary>GDEF: f, l, i bases, the ligature glyphs given ligatures, the four accents marks.</summary>
    private static byte[] Ot21Gdef(params int[] ligatures)
    {
        var classes = new Dictionary<int, int> { [Ot20G('f')] = 1, [Ot20G('i')] = 1, [Ot20G('l')] = 1, [Ot20G('a')] = 1 };
        foreach (var l in ligatures) classes[l] = 2;
        foreach (var m in new[] { Ot21Acute, Ot21Grave, Ot21Circumflex, Ot21Tilde }) classes[m] = 3;
        return Ot20Gdef(classes);
    }

    // ------------------------------------------------------------ OT 20.4.1

    /// <summary>
    /// OT 20.4.1 sentences 4 and 14–21: GPOS 1.1 adds a 32-bit offset to FeatureVariations. A variable font whose FeatureVariations put lookup 1
    /// (A–V −20) in place of lookup 0 (A–V −80) for „kern“ under a condition on axis 0 from +0.5 to +1.0 — not met at the default instance, in
    /// which PDF-Knead sets the text: the lookups of the normal FeatureList apply, as with version 1.0.
    /// </summary>
    [Fact]
    public void Ot21_FeatureVariationsOutsideTheDefaultInstance_FeatureListApplies()
    {
        var gpos = Ot20Layout([("kern", [0])],
            [Ot20Lookup(2, Ot20Pair1(4, 0, (Ot20G('A'), Ot20G('V'), [-80], []))), Ot20Lookup(2, Ot20Pair1(4, 0, (Ot20G('A'), Ot20G('V'), [-20], [])))],
            Ot20FeatureVariations(0x2000, 0x4000, 1));
        Assert.Equal(1, gpos[3]);                                                     // version 1.1
        var shaped = Ot20Shaper(gpos, more: new Dictionary<string, byte[]?> { ["fvar"] = Ot20Fvar() }).Shape("AV");
        Assert.Equal(Ot20Adv('A') - 80, shaped[0].XAdvance);
    }

    // ------------------------------------------------------------ OT 20.4.2

    /// <summary>
    /// OT 20.4.2 sentences 1–3: single positioning moves the placement or advance of one glyph. Format 1 for A with valueFormat 5 (XPlacement 10,
    /// XAdvance 20): in „AB“ A stands 10 further right and advances 20 more, B is unchanged.
    /// </summary>
    [Fact]
    public void Ot21_SinglePositioning_MovesOneGlyph()
    {
        var shaped = Ot20Shaper(Ot20Kern(Ot20Lookup(1, Ot20Single1([Ot20G('A')], 5, 10, 20)))).Shape("AB");
        Assert.Equal((10.0, Ot20Adv('A') + 20), (shaped[0].XOffset, shaped[0].XAdvance));
        Assert.Equal((0.0, Ot20Adv('B')), (shaped[1].XOffset, shaped[1].XAdvance));
    }

    /// <summary>
    /// OT 20.4.2 sentences 4–5, 7–8, 11–16: format 1 gives every glyph of its coverage the same value (coverage offset from the subtable,
    /// valueFormat, one ValueRecord). Coverage {A, B}, valueFormat 2 (YPlacement −50): both stand 50 lower, their advances unchanged.
    /// </summary>
    [Fact]
    public void Ot21_SinglePositioningFormat1_SameValueForAllCovered()
    {
        var shaped = Ot20Shaper(Ot20Kern(Ot20Lookup(1, Ot20Single1([Ot20G('A'), Ot20G('B')], 2, -50)))).Shape("AB");
        Assert.Equal([-50.0, -50.0], shaped.Select(g => g.YOffset));
        Assert.Equal([Ot20Adv('A'), Ot20Adv('B')], shaped.Select(g => g.XAdvance));
    }

    /// <summary>
    /// OT 20.4.2 sentences 17–19, 24, 26–31 and 33–37, 39: format 2 holds one ValueRecord per covered glyph in coverage order, all of the one
    /// valueFormat, behind format, coverage offset, valueFormat and valueCount. Coverage {A, B, C}, valueFormat 4, values 10, 30, 50: each
    /// glyph advances by its own value — the third value read at 8 + 2 × the record size.
    /// </summary>
    [Fact]
    public void Ot21_SinglePositioningFormat2_ValueByCoverageIndex()
    {
        var shaped = Ot20Shaper(Ot20Kern(Ot20Lookup(1, Ot20Single2([Ot20G('A'), Ot20G('B'), Ot20G('C')], 4, [[10], [30], [50]])))).Shape("ABC");
        Assert.Equal([Ot20Adv('A') + 10, Ot20Adv('B') + 30, Ot20Adv('C') + 50], shaped.Select(g => g.XAdvance));
    }

    // ------------------------------------------------------------ OT 20.4.3

    /// <summary>
    /// OT 20.4.3 sentences 9–15: valueFormat1 0 leaves the first glyph, valueFormat2 other than 0 means the lookup goes on after the second.
    /// Pairs A–V and V–A with valueFormat1 0 and valueFormat2 4 (−30, −40): in „AVA“ only V advances 30 less, A is unchanged, and V–A is not
    /// tried (the lookup goes on behind V), so the last A is unchanged too.
    /// </summary>
    [Fact]
    public void Ot21_PairFormat1_OnlySecondValue_LookupGoesOnBehindIt()
    {
        var shaped = Ot20Shaper(Ot20Kern(Ot20Lookup(2, Ot20Pair1(0, 4, (Ot20G('A'), Ot20G('V'), [], [-30]), (Ot20G('V'), Ot20G('A'), [], [-40])))))
            .Shape("AVA");
        Assert.Equal([Ot20Adv('A'), Ot20Adv('V') - 30, Ot20Adv('A')], shaped.Select(g => g.XAdvance));
    }

    /// <summary>
    /// OT 20.4.3 sentences 44–45, 48–51 and 70–80: format 2 pairs classes of glyphs (ClassDef1 of the first, ClassDef2 of the second); the
    /// records follow class 1 by class 1 at +16, each of class2Count records. Coverage {A}, A class 1, V class 2-side class 1, two classes each,
    /// −80 for (1, 1) and other values in the records no pair reaches: „AV“ −80 (record (1, 1) read at +16 + (1 × 2 + 1) × 2); „AA“ (record
    /// (1, 0) = 0) and „VA“ (V not covered) unchanged.
    /// </summary>
    [Fact]
    public void Ot21_PairFormat2_ClassPairs()
    {
        var a = Ot20G('A');
        var v = Ot20G('V');
        int[][] values = [[11, 22], [0, -80]];
        var shaper = Ot20Shaper(Ot20Kern(Ot20Lookup(2, Ot20Pair2([a], 4, 0, new Dictionary<int, int> { [a] = 1 }, new Dictionary<int, int> { [v] = 1 }, 2, 2,
            (c1, c2) => ([values[c1][c2]], [])))));
        Assert.Equal(Ot20Adv('A') - 80, shaper.Shape("AV")[0].XAdvance);
        Assert.Equal(Ot20Adv('A'), shaper.Shape("AA")[0].XAdvance);
        Assert.Equal([Ot20Adv('V'), Ot20Adv('A')], shaper.Shape("VA").Select(g => g.XAdvance));
    }

    /// <summary>
    /// OT 20.4.3 sentences 52–58: with valueFormat2 0 the second glyph is the next one for the lookup. Format 2 over A (class 1) and V (class 2),
    /// −80 for A–V and −60 for V–A: in „AVA“ both pairs are kerned.
    /// </summary>
    [Fact]
    public void Ot21_PairFormat2_WithoutSecondValue_SecondGlyphStartsTheNextPair()
    {
        var a = Ot20G('A');
        var v = Ot20G('V');
        var shaper = Ot20Shaper(Ot20Kern(Ot20Lookup(2, Ot20Pair2([a, v], 4, 0, new Dictionary<int, int> { [a] = 1, [v] = 2 },
            new Dictionary<int, int> { [v] = 1, [a] = 2 }, 3, 3, (c1, c2) => ([(c1, c2) switch { (1, 1) => -80, (2, 2) => -60, _ => 0 }], [])))));
        Assert.Equal([Ot20Adv('A') - 80, Ot20Adv('V') - 60, Ot20Adv('A')], shaper.Shape("AVA").Select(g => g.XAdvance));
    }

    /// <summary>
    /// OT 20.4.3 sentences 59–69: every glyph has a class (none: 0), ClassDef1 and ClassDef2 may be one table, the counts include class 0.
    /// One ClassDef for both sides (A class 2, V class 1), three classes, a value only for (2, 0): A before B (no class) is kerned, A before V
    /// (2, 1) is not.
    /// </summary>
    [Fact]
    public void Ot21_PairFormat2_OneClassDefForBothSides_ClassZeroForUnlisted()
    {
        var a = Ot20G('A');
        var shaper = Ot20Shaper(Ot20Kern(Ot20Lookup(2, Ot20Pair2([a], 4, 0, new Dictionary<int, int> { [a] = 2, [Ot20G('V')] = 1 }, null, 3, 3,
            (c1, c2) => ([(c1, c2) == (2, 0) ? -50 : 0], [])))));
        Assert.Equal(Ot20Adv('A') - 50, shaper.Shape("AB")[0].XAdvance);
        Assert.Equal(Ot20Adv('A'), shaper.Shape("AV")[0].XAdvance);
    }

    /// <summary>
    /// OT 20.4.3 sentences 81–82: each Class1Record holds one Class2Record per class of ClassDef2, class 0 included. A ClassDef2 giving V the
    /// class 5 while class2Count is 2: „AV“ is unchanged, without an exception.
    /// </summary>
    [Fact]
    public void Ot21_PairFormat2_ClassBeyondTheCount_NoPair()
    {
        var a = Ot20G('A');
        var shaper = Ot20Shaper(Ot20Kern(Ot20Lookup(2, Ot20Pair2([a], 4, 0, new Dictionary<int, int> { [a] = 1 }, new Dictionary<int, int> { [Ot20G('V')] = 5 },
            2, 2, (_, _) => ([-80], [])))));
        Assert.Equal(Ot20Adv('A') - 80, shaper.Shape("AB")[0].XAdvance);           // class (1, 0) is kerned
        Assert.Equal(Ot20Adv('A'), shaper.Shape("AV")[0].XAdvance);
    }

    /// <summary>
    /// OT 20.4.3 sentences 83–89, 91–94: a Class2Record holds two ValueRecords, empty where the valueFormat is 0; size and content follow from
    /// both valueFormats. valueFormat1 0 and valueFormat2 5 (records of four bytes, the second value only): in „AV“ V takes XPlacement 15 and
    /// XAdvance −25 from record (1, 1), A is unchanged.
    /// </summary>
    [Fact]
    public void Ot21_PairFormat2_RecordSizeFromBothValueFormats()
    {
        var a = Ot20G('A');
        int[][][] values = [[[1, 2], [3, 4]], [[5, 6], [15, -25]]];
        var shaped = Ot20Shaper(Ot20Kern(Ot20Lookup(2, Ot20Pair2([a], 0, 5, new Dictionary<int, int> { [a] = 1 }, new Dictionary<int, int> { [Ot20G('V')] = 1 },
            2, 2, (c1, c2) => ([], values[c1][c2]))))).Shape("AV");
        Assert.Equal((0.0, Ot20Adv('A')), (shaped[0].XOffset, shaped[0].XAdvance));
        Assert.Equal((15.0, Ot20Adv('V') - 25), (shaped[1].XOffset, shaped[1].XAdvance));
    }

    // ------------------------------------------------------------ OT 20.4.4

    /// <summary>Cursive attachment format 1: EntryExit records in coverage order (glyphs sorted), entryExitCount as given or their number.</summary>
    private static Action<Ot20Bytes> Ot21Cursive((int Glyph, byte[]? Entry, byte[]? Exit)[] records, int count = -1) => b =>
    {
        var start = b.Here;
        var coverage = b.NewLabel();
        var sorted = records.OrderBy(r => r.Glyph).ToArray();
        var anchors = new List<(string, byte[])>();
        b.U16(1).Off16(coverage, start).U16(count >= 0 ? count : sorted.Length);
        foreach (var r in sorted)
            foreach (var anchor in new[] { r.Entry, r.Exit })
                if (anchor is null) b.U16(0);
                else
                {
                    var label = b.NewLabel();
                    b.Off16(label, start);
                    anchors.Add((label, anchor));
                }
        foreach (var (label, anchor) in anchors) b.Label(label).Bytes(anchor);
        b.Label(coverage);
        Ot20Coverage(b, sorted.Select(r => r.Glyph));
    };

    private static TextShaper Ot21CursiveShaper((int Glyph, byte[]? Entry, byte[]? Exit)[] records, int count = -1) =>
        Ot20Shaper(Ot20Layout([("curs", [0])], [Ot20Lookup(3, Ot21Cursive(records, count))]));

    /// <summary>
    /// OT 20.4.4 sentences 10–19 and 25–28: CursivePosFormat1 — coverage, entryExitCount and the EntryExit records in coverage order, their anchor
    /// offsets counted from the start of the subtable (the anchors lie behind the records). B with the exit (450, 100), C with the entry (20, 30):
    /// B advances 430 so that the anchors meet, C is raised by 70; with entryExitCount 1 C has no record and nothing moves.
    /// </summary>
    [Fact]
    public void Ot21_CursiveAttachment_ExitMeetsEntry_RecordsBoundByTheirCount()
    {
        (int, byte[]?, byte[]?)[] records = [(Ot20G('B'), null, Ot20Anchor(450, 100)), (Ot20G('C'), Ot20Anchor(20, 30), null)];
        var shaped = Ot21CursiveShaper(records).Shape("BC");
        Assert.Equal(430, shaped[0].XAdvance);
        Assert.Equal(70, shaped[1].YOffset);
        var counted = Ot21CursiveShaper(records, 1).Shape("BC");
        Assert.Equal((Ot20Adv('B'), 0.0), (counted[0].XAdvance, counted[1].YOffset));
    }

    /// <summary>OT 20.4.4 sentences 20–23: without an anchor on one side (NULL) nothing moves. B's exit anchor NULL: „BC“ is unchanged.</summary>
    [Fact]
    public void Ot21_CursiveAttachment_NullExit_NothingMoves()
    {
        var shaped = Ot21CursiveShaper([(Ot20G('B'), null, null), (Ot20G('C'), Ot20Anchor(20, 30), null)]).Shape("BC");
        Assert.Equal((Ot20Adv('B'), 0.0), (shaped[0].XAdvance, shaped[1].YOffset));
    }

    // ------------------------------------------------------------ OT 20.4.5

    /// <summary>The anchors of base and mark from the last mark-to-base lookup of Noto Sans Arabic („mark“, „arab“) that attaches the mark to the base.</summary>
    private static ((int X, int Y) Base, (int X, int Y) Mark) Ot21NotoAnchors(OpenTypeFont font, int mark, int basis)
    {
        var gpos = new LayoutTable(font.TableData("GPOS").ToArray(), 9);
        var t = gpos.T;
        ((int, int), (int, int))? found = null;
        foreach (var index in gpos.LookupsFor(["mark"], "arab"))
        {
            var lookup = gpos.Lookup(index);
            if (lookup.Type != 4) continue;
            foreach (var sub in lookup.Subtables)
            {
                int markIndex = t.Coverage(sub + t.U16(sub + 2), mark), baseIndex = t.Coverage(sub + t.U16(sub + 4), basis);
                if (markIndex < 0 || baseIndex < 0) continue;
                int classCount = t.U16(sub + 6), markArray = sub + t.U16(sub + 8), baseArray = sub + t.U16(sub + 10);
                var record = markArray + 2 + markIndex * 4;
                var m = markArray + t.U16(record + 2);
                var ba = baseArray + t.U16(baseArray + 2 + (baseIndex * classCount + t.U16(record)) * 2);
                found = ((t.I16(ba + 2), t.I16(ba + 4)), (t.I16(m + 2), t.I16(m + 4)));
                break;
            }
        }
        Assert.NotNull(found);
        return found.Value;
    }

    /// <summary>
    /// OT 20.4.5 sentences 1, 3–6: mark-to-base moves the mark so that its anchor meets the base's anchor of its class; the base keeps its place and
    /// the advances stay. Noto Sans Arabic „بَ“ (the font splits off the dot of beh): the fatha's offset is the base anchor minus the mark anchor
    /// of the font's mark lookup, less the advances between them; the body of beh has the same offset and advance as in „ب“.
    /// </summary>
    [Fact]
    public void Ot21_MarkToBase_NotoFatha_AnchorsMeet_BaseStays()
    {
        var font = EmbeddedFonts.ArabicOpenType("NotoSansArabic-Regular.ttf");
        var shaper = TextShaper.For(font);
        var marked = shaper.Shape("بَ");
        var plain = shaper.Shape("ب");
        var f = Array.FindIndex(marked, g => g.Text == "َ");
        var b = f - marked[f].AttachBack;
        Assert.InRange(b, 0, f - 1);
        var (baseAnchor, markAnchor) = Ot21NotoAnchors(font, marked[f].Glyph, marked[b].Glyph);
        var between = Enumerable.Range(b, f - b).Sum(k => marked[k].XAdvance);
        Assert.Equal(marked[b].XOffset + baseAnchor.X - markAnchor.X - between, marked[f].XOffset);
        Assert.Equal(marked[b].YOffset + baseAnchor.Y - markAnchor.Y, marked[f].YOffset);
        Assert.Equal(0, marked[f].XAdvance);
        Assert.Equal((plain[0].Glyph, plain[0].XOffset, plain[0].XAdvance), (marked[b].Glyph, marked[b].XOffset, marked[b].XAdvance));
    }

    /// <summary>
    /// OT 20.4.5 sentences 38–44, 46–49: a BaseRecord holds one anchor offset per mark class, counted from the BaseArray, NULL allowed. The base's
    /// anchor for class 0 NULL: a mark of that class stays where it is, not attached.
    /// </summary>
    [Fact]
    public void Ot21_MarkToBase_NullBaseAnchor_MarkStays()
    {
        var shaped = Ot20Shaper(Ot20Kern(Ot20Lookup(4, Ot20MarkAttach([(Ot21Acute, 0, Ot20Anchor(0, 0))], [(Ot20G('a'), [null])], 1))), Ot21Gdef())
            .Shape("á");
        Assert.Equal((0.0, 0.0, 0), (shaped[1].XOffset, shaped[1].YOffset, shaped[1].AttachBack));
    }

    // ------------------------------------------------------------ OT 20.4.6

    /// <summary>GSUB f i → fi, GDEF, and a mark-to-ligature lookup under „mark“.</summary>
    private static TextShaper Ot21LigatureShaper(Action<Ot20Bytes> markLigature, params (int[] Components, int Glyph)[] ligatures) =>
        Ot20Shaper(Ot20Layout([("mark", [0])], [Ot20Lookup(5, markLigature)]), Ot21Gdef([.. ligatures.Select(l => l.Glyph)]), Ot21Liga(ligatures));

    private static (int[] Components, int Glyph) Ot21FiLigature => ([Ot20G('f'), Ot20G('i')], Ot20G('ﬁ'));

    /// <summary>
    /// OT 20.4.6 sentences 1–4 and 27–34: mark-to-ligature — each component of a ligature has its own anchor per mark class; the offsets of the
    /// subtable count from its start. Ligature „fi“ (two components, one anchor each) and U+0301: the mark is attached to the ligature (its base
    /// one back) at the anchor of its component (the last one, where the accent follows the i).
    /// </summary>
    [Fact]
    public void Ot21_MarkToLigature_AttachedAtTheAnchorOfTheComponent()
    {
        var shaper = Ot21LigatureShaper(Ot20MarkLigature([(Ot21Acute, 0, Ot20Anchor(0, 0))], [(Ot21Fi, [[Ot20Anchor(200, 700)], [Ot20Anchor(800, 750)]])], 1),
            Ot21FiLigature);
        var shaped = shaper.Shape("fí");
        Assert.Equal([Ot21Fi, Ot21Acute], shaped.Select(g => g.Glyph));
        Assert.Equal(1, shaped[1].AttachBack);
        Assert.Equal((800 - shaped[0].XAdvance, 750.0), (shaped[1].XOffset, shaped[1].YOffset));
    }

    /// <summary>
    /// OT 20.4.6 sentences 8–11: the anchors of a ligature form a table of component by mark class. Two mark classes (U+0301 class 0, U+0300 class 1):
    /// U+0300 after „fi“ takes the second anchor of the component.
    /// </summary>
    [Fact]
    public void Ot21_MarkToLigature_AnchorByComponentAndClass()
    {
        var shaper = Ot21LigatureShaper(Ot20MarkLigature([(Ot21Acute, 0, Ot20Anchor(0, 0)), (Ot21Grave, 1, Ot20Anchor(0, 0))],
            [(Ot21Fi, [[Ot20Anchor(200, 700), Ot20Anchor(250, -100)], [Ot20Anchor(800, 750), Ot20Anchor(850, -120)]])], 2), Ot21FiLigature);
        var shaped = shaper.Shape("fì");
        Assert.Equal((850 - shaped[0].XAdvance, -120.0), (shaped[1].XOffset, shaped[1].YOffset));
    }

    /// <summary>
    /// OT 20.4.6 sentence 15: the shaper looks back from the mark for the ligature before it. „fi“, then two marks (GDEF class 3), only the second
    /// in the mark coverage: it is attached to the ligature two glyphs back.
    /// </summary>
    [Fact]
    public void Ot21_MarkToLigature_LooksBackOverMarks()
    {
        var shaper = Ot21LigatureShaper(Ot20MarkLigature([(Ot21Acute, 0, Ot20Anchor(0, 0))], [(Ot21Fi, [[Ot20Anchor(200, 700)], [Ot20Anchor(800, 750)]])], 1),
            Ot21FiLigature);
        var shaped = shaper.Shape("fî́");
        Assert.Equal(2, shaped[2].AttachBack);
        Assert.Equal(750, shaped[2].YOffset);
    }

    /// <summary>
    /// OT 20.4.6 sentences 17–19: the anchors of mark and ligature are brought together; only the placement of the mark changes. Mark anchor
    /// (30, 40), ligature anchor (800, 750): the mark is offset by the difference, the ligature stays as in „fi“.
    /// </summary>
    [Fact]
    public void Ot21_MarkToLigature_OnlyTheMarkMoves()
    {
        var shaper = Ot21LigatureShaper(Ot20MarkLigature([(Ot21Acute, 0, Ot20Anchor(30, 40))], [(Ot21Fi, [[Ot20Anchor(200, 700)], [Ot20Anchor(800, 750)]])], 1),
            Ot21FiLigature);
        var shaped = shaper.Shape("fí");
        var plain = shaper.Shape("fi");
        Assert.Equal((800 - 30 - shaped[0].XAdvance, 750.0 - 40), (shaped[1].XOffset, shaped[1].YOffset));
        Assert.Equal((plain[0].Glyph, plain[0].XOffset, plain[0].YOffset, plain[0].XAdvance), (shaped[0].Glyph, shaped[0].XOffset, shaped[0].YOffset, shaped[0].XAdvance));
    }

    /// <summary>
    /// OT 20.4.6 sentences 20–26: MarkLigPosFormat1 — mark and ligature coverage, class count, MarkArray, LigatureArray. A ligature coverage
    /// naming another glyph (L) than the ligature formed: the mark is not attached.
    /// </summary>
    [Fact]
    public void Ot21_MarkToLigature_OnlyLigaturesOfTheCoverage()
    {
        var shaper = Ot21LigatureShaper(Ot20MarkLigature([(Ot21Acute, 0, Ot20Anchor(0, 0))], [(Ot20G('L'), [[Ot20Anchor(200, 700)], [Ot20Anchor(800, 750)]])], 1),
            Ot21FiLigature);
        var shaped = shaper.Shape("fí");
        Assert.Equal((0.0, 0.0, 0), (shaped[1].XOffset, shaped[1].YOffset, shaped[1].AttachBack));
    }

    /// <summary>
    /// OT 20.4.6 sentences 35–41: the LigatureArray holds one LigatureAttach offset per ligature in coverage order, counted from the LigatureArray.
    /// Two ligatures (fi, fl) with different anchors: each mark takes the anchor of its own ligature.
    /// </summary>
    [Fact]
    public void Ot21_MarkToLigature_EachLigatureItsAnchors()
    {
        Assert.NotEqual(0, Ot21Fl);
        var shaper = Ot21LigatureShaper(Ot20MarkLigature([(Ot21Acute, 0, Ot20Anchor(0, 0))],
            [(Ot21Fi, [[Ot20Anchor(200, 700)], [Ot20Anchor(800, 750)]]), (Ot21Fl, [[Ot20Anchor(210, 710)], [Ot20Anchor(820, 900)]])], 1),
            Ot21FiLigature, ([Ot20G('f'), Ot20G('l')], Ot21Fl));
        var shaped = shaper.Shape("fífĺ");
        Assert.Equal([Ot21Fi, Ot21Acute, Ot21Fl, Ot21Acute], shaped.Select(g => g.Glyph));
        Assert.Equal(750, shaped[1].YOffset);
        Assert.Equal(900, shaped[3].YOffset);
    }

    /// <summary>
    /// OT 20.4.6 sentences 42–49: LigatureAttach holds componentCount and the ComponentRecords in the order of the components. A ligature of
    /// three components (f f i) with the mark after it: the mark takes the anchor of the third record (where it belongs; PDF-Knead always takes
    /// the last component).
    /// </summary>
    [Fact]
    public void Ot21_MarkToLigature_ThreeComponents_MarkAtTheEndTakesTheThird()
    {
        var ffi = Ot20G('Q');
        var shaper = Ot21LigatureShaper(Ot20MarkLigature([(Ot21Acute, 0, Ot20Anchor(0, 0))],
            [(ffi, [[Ot20Anchor(100, 700)], [Ot20Anchor(400, 710)], [Ot20Anchor(700, 720)]])], 1), ([Ot20G('f'), Ot20G('f'), Ot20G('i')], ffi));
        var shaped = shaper.Shape("ffí");
        Assert.Equal([ffi, Ot21Acute], shaped.Select(g => g.Glyph));
        Assert.Equal((700 - shaped[0].XAdvance, 720.0), (shaped[1].XOffset, shaped[1].YOffset));
    }

    /// <summary>
    /// OT 20.1 sentence 51 and OT 20.4.6 sentences 5–7, 16 (open question 12: tracking the component of a mark through GSUB): the anchor depends
    /// on the component the mark belongs to, which the shaper is to track through the substitutions. Today PDF-Knead takes the last component:
    /// a mark after the first of three characters (f, mark, f, i → ligature with the mark passed over) and a mark between the two of „fi“ take
    /// the anchor of the last component (after the norm: of the first). Test of the present behaviour until the question is decided.
    /// </summary>
    [Fact]
    public void Ot21_MarkInsideALigature_TodayTheLastComponent()
    {
        var ffi = Ot20G('Q');
        var three = Ot21LigatureShaper(Ot20MarkLigature([(Ot21Acute, 0, Ot20Anchor(0, 0))],
            [(ffi, [[Ot20Anchor(100, 700)], [Ot20Anchor(400, 710)], [Ot20Anchor(700, 720)]])], 1), ([Ot20G('f'), Ot20G('f'), Ot20G('i')], ffi));
        var shaped = three.Shape("f́fi");
        Assert.Equal([ffi, Ot21Acute], shaped.Select(g => g.Glyph));
        Assert.Equal(720, shaped[1].YOffset);                                    // after the norm: 700 (first component)
        var two = Ot21LigatureShaper(Ot20MarkLigature([(Ot21Acute, 0, Ot20Anchor(0, 0))], [(Ot21Fi, [[Ot20Anchor(200, 700)], [Ot20Anchor(800, 750)]])], 1),
            Ot21FiLigature);
        shaped = two.Shape("f́i");
        Assert.Equal([Ot21Fi, Ot21Acute], shaped.Select(g => g.Glyph));
        Assert.Equal(750, shaped[1].YOffset);                                    // after the norm: 700
    }

    /// <summary>
    /// OT 20.4.6 sentences 50–53, 55–58: a ComponentRecord holds one anchor offset per class, NULL where the component has no anchor for it,
    /// counted from the LigatureAttach. The last component's anchor for class 0 NULL: the mark stays where it is, not attached.
    /// </summary>
    [Fact]
    public void Ot21_MarkToLigature_NullComponentAnchor_MarkStays()
    {
        var shaper = Ot21LigatureShaper(Ot20MarkLigature([(Ot21Acute, 0, Ot20Anchor(0, 0))], [(Ot21Fi, [[Ot20Anchor(200, 700)], [null]])], 1),
            Ot21FiLigature);
        var shaped = shaper.Shape("fí");
        Assert.Equal((0.0, 0.0, 0), (shaped[1].XOffset, shaped[1].YOffset, shaped[1].AttachBack));
    }

    // ------------------------------------------------------------ OT 20.4.7

    /// <summary>A GPOS with one mark-to-mark lookup (flag as given) under „mkmk“, and GDEF (marks class 3, mark attachment classes if given).</summary>
    private static TextShaper Ot21MarkMarkShaper(Action<Ot20Bytes> markMark, int flag = 0, IReadOnlyDictionary<int, int>? attachClasses = null)
    {
        var classes = new Dictionary<int, int> { [Ot20G('a')] = 1 };
        foreach (var m in new[] { Ot21Acute, Ot21Grave, Ot21Circumflex, Ot21Tilde }) classes[m] = 3;
        return Ot20Shaper(Ot20Layout([("mkmk", [0])], [Ot20Lookup(6, flag, markMark)]), Ot20Gdef(classes, attachClasses));
    }

    /// <summary>
    /// OT 20.4.7 sentences 1–6, 15–21 and 22–29: mark-to-mark has the form of mark-to-base — mark1 (to attach) with its anchor and class from
    /// the MarkArray, mark2 with one anchor per class, the offsets counted from the subtable. Mark1 {U+0301} with anchor (10, 0), mark2 {U+0302}
    /// with anchor (60, 500): in „a“ U+0302 U+0301 the acute is attached to the circumflex (its base one back) at that anchor.
    /// </summary>
    [Fact]
    public void Ot21_MarkToMark_Mark1AtTheAnchorOfMark2()
    {
        var shaped = Ot21MarkMarkShaper(Ot20MarkAttach([(Ot21Acute, 0, Ot20Anchor(10, 0))], [(Ot21Circumflex, [Ot20Anchor(60, 500)])], 1))
            .Shape("ấ");
        Assert.Equal(1, shaped[2].AttachBack);
        Assert.Equal((shaped[1].XOffset + 60 - 10 - shaped[1].XAdvance, 500.0), (shaped[2].XOffset, shaped[2].YOffset));
    }

    /// <summary>
    /// OT 20.4.7 sentence 9 (open question 12: mark-to-mark with the lookup's flags): mark2 is the glyph before mark1, passing over what the lookup
    /// flags skip. With MarkAttachmentType 1 and a mark of class 2 between mark2 (class 1) and mark1 (class 1), PDF-Knead today takes the glyph
    /// right before mark1 and attaches nothing (after the norm: mark1 at mark2). Test of the present behaviour until the question is decided.
    /// </summary>
    [Fact]
    public void Ot21_MarkToMark_SkippedMarkBetween_TodayNotAttached()
    {
        var shaper = Ot21MarkMarkShaper(Ot20MarkAttach([(Ot21Acute, 0, Ot20Anchor(10, 0))], [(Ot21Circumflex, [Ot20Anchor(60, 500)])], 1), 0x0100,
            new Dictionary<int, int> { [Ot21Acute] = 1, [Ot21Circumflex] = 1, [Ot21Tilde] = 2 });
        var shaped = shaper.Shape("ẫ́");
        Assert.Equal((0, 0.0), (shaped[3].AttachBack, shaped[3].YOffset));      // after the norm: AttachBack 2, YOffset 500
    }

    /// <summary>
    /// OT 20.4.7 sentences 10–12: the subtable applies only if that mark2 glyph is in the mark2 coverage, and then only the placement of mark1 changes.
    /// Tilde (not in the mark2 coverage) before the acute: nothing moves; circumflex before it: the acute's offsets change, its advance and the
    /// circumflex do not.
    /// </summary>
    [Fact]
    public void Ot21_MarkToMark_OnlyMark2OfTheCoverage_OnlyPlacementChanges()
    {
        var shaper = Ot21MarkMarkShaper(Ot20MarkAttach([(Ot21Acute, 0, Ot20Anchor(10, 0))], [(Ot21Circumflex, [Ot20Anchor(60, 500)])], 1));
        var outside = shaper.Shape("ã́");
        Assert.Equal((0.0, 0.0, 0), (outside[2].XOffset, outside[2].YOffset, outside[2].AttachBack));
        var plain = shaper.Shape("â");
        var inside = shaper.Shape("ấ");
        Assert.Equal(500, inside[2].YOffset);
        Assert.Equal(Ot20Lib.AdvanceWidth(Ot21Acute), inside[2].XAdvance);
        Assert.Equal((plain[1].XOffset, plain[1].YOffset, plain[1].XAdvance), (inside[1].XOffset, inside[1].YOffset, inside[1].XAdvance));
    }

    /// <summary>
    /// OT 20.4.7 sentences 13–14: the input of a mark lookup is the mark to place; with several marks the lookup applies several times. „a“ with
    /// two marks of the mark coverage of a mark-to-base lookup: both are attached to „a“ (two and one glyph back).
    /// </summary>
    [Fact]
    public void Ot21_MarkToBase_SeveralMarks_EachAttached()
    {
        var shaper = Ot20Shaper(Ot20Layout([("mark", [0])], [Ot20Lookup(4, Ot20MarkAttach([(Ot21Acute, 0, Ot20Anchor(0, 0)), (Ot21Circumflex, 0, Ot20Anchor(0, 0))],
            [(Ot20G('a'), [Ot20Anchor(500, 600)])], 1))]), Ot21Gdef());
        var shaped = shaper.Shape("ấ");
        Assert.Equal([0, 1, 2], shaped.Select(g => g.AttachBack));
        Assert.Equal([0.0, 600, 600], shaped.Select(g => g.YOffset));
    }

    /// <summary>
    /// OT 20.4.7 sentences 30–35: the Mark2Array holds one record per mark2 glyph in coverage order. Two mark2 glyphs (circumflex, tilde) with
    /// different anchors: the acute takes the anchor of the mark before it.
    /// </summary>
    [Fact]
    public void Ot21_MarkToMark_EachMark2ItsAnchor()
    {
        var shaper = Ot21MarkMarkShaper(Ot20MarkAttach([(Ot21Acute, 0, Ot20Anchor(10, 0))],
            [(Ot21Circumflex, [Ot20Anchor(60, 500)]), (Ot21Tilde, [Ot20Anchor(70, 600)])], 1));
        Assert.Equal(500, shaper.Shape("ấ")[2].YOffset);
        Assert.Equal(600, shaper.Shape("ã́")[2].YOffset);
    }

    /// <summary>
    /// OT 20.4.7 sentences 36–40, 42–45: a Mark2Record holds one anchor offset per mark1 class, counted from the Mark2Array, NULL allowed. The
    /// circumflex's anchor for class 0 NULL: the acute stays where it is.
    /// </summary>
    [Fact]
    public void Ot21_MarkToMark_NullMark2Anchor_Mark1Stays()
    {
        var shaped = Ot21MarkMarkShaper(Ot20MarkAttach([(Ot21Acute, 0, Ot20Anchor(10, 0))], [(Ot21Circumflex, [null])], 1)).Shape("ấ");
        Assert.Equal((0.0, 0.0, 0), (shaped[2].XOffset, shaped[2].YOffset, shaped[2].AttachBack));
    }
}
