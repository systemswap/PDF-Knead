using System.Diagnostics;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;

namespace PdfKnead.Tests.Fonts;

/// <summary>
/// Limits of <see cref="TextShaper"/> against crafted GSUB tables (docs/FEHLERGEDAECHTNIS.md, No. 1137): counts, offsets and rule sets
/// from the font are filled up to 65 535 entries, which random damage hardly ever produces, so the tables are built here.
/// </summary>
public sealed class ShaperLimitTests
{
    /// <summary>A table written big-endian, with the position of each part known before it is written.</summary>
    private sealed class Table
    {
        private readonly List<byte> _bytes = [];

        public int Length => _bytes.Count;

        public Table U16(int v)
        {
            _bytes.Add((byte)(v >> 8));
            _bytes.Add((byte)v);
            return this;
        }

        public Table Tag(string tag)
        {
            foreach (var c in tag) _bytes.Add((byte)c);
            return this;
        }

        public Table Repeat(int count, Func<int, int> value)
        {
            for (var i = 0; i < count; i++) U16(value(i));
            return this;
        }

        public byte[] ToArray() => [.. _bytes];
    }

    private static readonly byte[] Liberation = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");

    /// <summary>Liberation Sans with the GSUB given and without GPOS, and its glyph of „a“.</summary>
    private static (TextShaper Shaper, int A) With(Func<int, byte[]> gsub)
    {
        var a = OpenTypeFont.Parse(Liberation).GlyphForUnicode('a');
        var font = ColorFontBuilder.WithTables(Liberation, new Dictionary<string, byte[]?> { ["GSUB"] = gsub(a), ["GPOS"] = null });
        return (TextShaper.For(OpenTypeFont.Parse(font)), a);
    }

    /// <summary>
    /// A GSUB: a ScriptList with „latn“ whose default language system names feature 0 „liga“ <paramref name="langSysEntries"/> times,
    /// the FeatureList with that feature and the lookup indices given, and the LookupList <paramref name="lookups"/> writes (offsets in it
    /// relative to its start). The parts are placed so that each starts within the 16-bit offsets of the header.
    /// </summary>
    private static byte[] Gsub(int langSysEntries, int[] featureLookups, Action<Table> lookups)
    {
        var script = new Table().U16(1).Tag("latn").U16(8)                   // one record, script at +8
            .U16(4).U16(0)                                                       // default language system at +4
            .U16(0).U16(0xFFFF).U16(langSysEntries).Repeat(langSysEntries, _ => 0);
        var feature = new Table().U16(1).Tag("liga").U16(8)                  // one record, feature at +8
            .U16(0).U16(featureLookups.Length).Repeat(featureLookups.Length, i => featureLookups[i]);
        var lookup = new Table();
        lookups(lookup);
        // a long language system goes last, else the ScriptList first
        Table[] order = script.Length > 0x1000 ? [feature, lookup, script] : [script, feature, lookup];
        var at = new Dictionary<Table, int>();
        var position = 10;
        foreach (var part in order)
        {
            Assert.True(position <= 0xFFFF, "Teil zu weit hinten");
            at[part] = position;
            position += part.Length;
        }
        var gsub = new List<byte>(position);
        gsub.AddRange(new Table().U16(1).U16(0).U16(at[script]).U16(at[feature]).U16(at[lookup]).ToArray());
        foreach (var part in order) gsub.AddRange(part.ToArray());
        return [.. gsub];
    }

    private static void Bounded(Action shape, long bytes, int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        shape();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < bytes, $"{allocated / 1_000_000} MB angelegt");
        Assert.True(watch.ElapsedMilliseconds < milliseconds * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// A context rule set of 65 535 rules, all one rule that lies in the rule offsets themselves and claims 65 534 input glyphs. A rule longer than the run never matches; before, each attempt allocated its positions first
    /// (256 KB), 16 GB per glyph of the text.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void RuleLongerThanTheRun_IsNotAllocated()
    {
        var (shaper, _) = With(a => Gsub(1, [0], t =>
        {
            t.U16(1).U16(4);                                  // LookupList: one lookup at +4
            t.U16(5).U16(0).U16(1).U16(8);                    // context substitution, one subtable at +8
            t.U16(1).U16(8).U16(1).U16(14);                   // format 1: coverage at +8, one rule set at +14
            t.U16(1).U16(1).U16(a);                           // coverage: „a“
            t.U16(0xFFFF).Repeat(0xFFFF, _ => 0xFFFE);        // 65 535 rules at +0xFFFE: inside this list, 65 534 glyphs each
        }));
        Bounded(() => Assert.Equal(3, shaper.Shape("aaa").Length), 200_000_000, 3000);
    }

    /// <summary>
    /// One lookup with 30 000 subtables, all one chained context subtable whose rule set holds 32 000 rules that do not match: 960
    /// million rule attempts per glyph, while only the lookup attempt was counted (hours for these four glyphs). Each subtable and rule
    /// tried counts now.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void ManySubtablesAndRules_AreCounted()
    {
        const int subtables = 30_000, rules = 32_000;
        var (shaper, _) = With(a => Gsub(1, [0], t =>
        {
            t.U16(1).U16(4);
            var lookup = t.Length;
            var sub = 6 + subtables * 2;
            t.U16(6).U16(0).U16(subtables).Repeat(subtables, _ => sub);
            Assert.Equal(lookup + sub, t.Length);
            t.U16(1).U16(8).U16(1).U16(14);
            t.U16(1).U16(1).U16(a);
            const int rule = 2 + rules * 2;
            t.U16(rules).Repeat(rules, _ => rule);
            t.U16(0).U16(2).U16(0).U16(0).U16(0);           // no backtrack, input „a“ + glyph 0, no lookahead, no records
        }));
        // each rule attempt leaves its match delegates behind (some 250 bytes): garbage bounded by the step budget, not the font
        Bounded(() => Assert.Equal(4, shaper.Shape("aaaa").Length), 1_000_000_000, 3000);
    }

    /// <summary>
    /// 4 000 LookupList entries naming one lookup table that claims 65 535 subtables at the end of the GSUB. Each entry kept its own
    /// array of subtable positions (256 KB, 1 GB in all; with 65 535 entries 16 GB); one table is read once now, with the subtables
    /// that fit in the table.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void LookupEntriesNamingOneTable_ShareIt()
    {
        const int entries = 4_000;
        var (shaper, _) = With(_ => Gsub(1, [.. Enumerable.Range(0, entries)], t =>
        {
            var table = 2 + entries * 2;
            t.U16(entries).Repeat(entries, _ => table);
            t.U16(1).U16(0).U16(0xFFFF);                      // single substitution, 65 535 subtables, none written
        }));
        Bounded(() =>
        {
            Assert.Contains(shaper.Describe(), l => l.Contains("liga"));
            Assert.Equal(2, shaper.Shape("aa").Length);
        }, 100_000_000, 3000);
    }

    /// <summary>
    /// A language system naming feature 0 65 535 times, the feature naming 30 000 lookups: two billion additions to the set of lookups
    /// before. Each feature table is read once now.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void FeatureNamedManyTimes_IsReadOnce()
    {
        var (shaper, _) = With(_ => Gsub(0xFFFF, [.. Enumerable.Range(0, 30_000)], t =>
        {
            t.U16(1).U16(4);
            t.U16(1).U16(0).U16(0);                           // one lookup without subtables
        }));
        Bounded(() => Assert.Equal(2, shaper.Shape("aa").Length), 100_000_000, 2000);
    }

    /// <summary>
    /// A legacy kern table of 65 535 subtables of six bytes, each claiming 65 535 pairs (the version of the next one) and searched for
    /// every pair of a 10 000-glyph run: ten billion steps, none of them counted before. The searches count as steps now.
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void LegacyKernTableOfManySubtables_IsCounted()
    {
        var kern = new Table().U16(0).U16(0xFFFF);
        // each subtable: version 0xFFFF (not checked; the pair count the one before reads), length 6, coverage 1 (horizontal, format 0)
        for (var i = 0; i < 0xFFFF; i++) kern.U16(0xFFFF).U16(6).U16(1);
        var font = ColorFontBuilder.WithTables(Liberation, new Dictionary<string, byte[]?> { ["kern"] = kern.ToArray(), ["GPOS"] = null });
        var shaper = TextShaper.For(OpenTypeFont.Parse(font));
        var text = new string('a', 10_000);
        Bounded(() => Assert.Equal(text.Length, shaper.Shape(text).Length), 100_000_000, 3000);
    }
}
