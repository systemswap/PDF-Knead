using System.Buffers.Binary;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-02 (OpenType, common table formats of GSUB and GPOS: scripts, language systems, features, lookups, lookup flags, coverage
/// and class definition tables). The tables are built by hand in Liberation Sans (or Noto Sans Arabic where the text must be Arabic).
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ builder (shared with package ot-01)

    /// <summary>Big-endian bytes with labels; 16- and 32-bit offsets are filled in once every label is placed.</summary>
    internal sealed class Ot02Bytes
    {
        private readonly List<byte> _bytes = [];
        private readonly Dictionary<string, int> _labels = [];
        private readonly List<(int At, string Target, int From, bool Wide)> _offsets = [];
        private int _next;

        public int Here => _bytes.Count;

        public string NewLabel() => "#" + _next++;

        public Ot02Bytes U16(int v)
        {
            _bytes.Add((byte)(v >> 8));
            _bytes.Add((byte)v);
            return this;
        }

        public Ot02Bytes U32(long v) => U16((int)(v >> 16)).U16((int)v);

        public Ot02Bytes Tag(string tag)
        {
            foreach (var c in tag) _bytes.Add((byte)c);
            return this;
        }

        public Ot02Bytes Label(string name)
        {
            _labels[name] = Here;
            return this;
        }

        /// <summary>A 16-bit offset to <paramref name="target"/>, counted from the position <paramref name="from"/>.</summary>
        public Ot02Bytes Off16(string target, int from)
        {
            _offsets.Add((Here, target, from, false));
            return U16(0);
        }

        public Ot02Bytes Off32(string target, int from)
        {
            _offsets.Add((Here, target, from, true));
            return U32(0);
        }

        public int Position(string label) => _labels[label];

        public byte[] ToArray()
        {
            var bytes = _bytes.ToArray();
            foreach (var (at, target, from, wide) in _offsets)
            {
                var value = _labels[target] - from;
                Assert.InRange(value, 1, wide ? int.MaxValue : 0xFFFF);
                if (wide) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at), (uint)value);
                else BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(at), (ushort)value);
            }
            return bytes;
        }
    }

    /// <summary>A language system: tag (ignored for the default one), feature indices, required feature, lookupOrderOffset.</summary>
    internal sealed record Ot02Lang(string Tag, int[] Features, int Required = 0xFFFF, int LookupOrder = 0);

    /// <summary>A script with its default language system (null: offset NULL) and further language systems in the order given.</summary>
    internal sealed record Ot02Script(string Tag, Ot02Lang? Default, params Ot02Lang[] Languages);

    /// <summary>A feature: tag, lookup indices, and its featureParamsOffset (a raw value, or a parameter table written after it).</summary>
    internal sealed record Ot02Feature(string Tag, int[] Lookups, int RawParams = 0, bool ParamsTable = false);

    private static readonly OpenTypeFont Ot02Font = OpenTypeFont.Parse(ShaperLimitTests.Liberation);

    private static int Ot02G(char c) => Ot02Font.GlyphForUnicode(c);

    private static int[] Ot02Gs(string text) => [.. text.Select(Ot02G)];

    /// <summary>A GSUB or GPOS: header (1.1 with a FeatureVariations offset when one is given), ScriptList, FeatureList (in the order given), LookupList.</summary>
    internal static byte[] Ot02Layout(Ot02Script[] scripts, Ot02Feature[] features, Action<Ot02Bytes>[] lookups, int major = 1, int minor = 0,
        Action<Ot02Bytes>? featureVariations = null, bool lookupsReversed = false, int lookupCount = -1)
    {
        var b = new Ot02Bytes();
        string scriptList = b.NewLabel(), featureList = b.NewLabel(), lookupList = b.NewLabel(), variations = b.NewLabel();
        b.U16(major).U16(minor).Off16(scriptList, 0).Off16(featureList, 0).Off16(lookupList, 0);
        if (featureVariations is not null) b.Off32(variations, 0);

        b.Label(scriptList);
        var at = b.Here;
        var scriptLabels = scripts.Select(_ => b.NewLabel()).ToArray();
        b.U16(scripts.Length);
        for (var i = 0; i < scripts.Length; i++) b.Tag(scripts[i].Tag).Off16(scriptLabels[i], at);
        for (var i = 0; i < scripts.Length; i++)
        {
            b.Label(scriptLabels[i]);
            var script = b.Here;
            var def = b.NewLabel();
            var langLabels = scripts[i].Languages.Select(_ => b.NewLabel()).ToArray();
            if (scripts[i].Default is not null) b.Off16(def, script);
            else b.U16(0);
            b.U16(langLabels.Length);
            for (var k = 0; k < langLabels.Length; k++) b.Tag(scripts[i].Languages[k].Tag).Off16(langLabels[k], script);
            if (scripts[i].Default is { } d)
            {
                b.Label(def);
                Ot02LangSys(b, d);
            }
            for (var k = 0; k < langLabels.Length; k++)
            {
                b.Label(langLabels[k]);
                Ot02LangSys(b, scripts[i].Languages[k]);
            }
        }

        b.Label(featureList);
        at = b.Here;
        var featureLabels = features.Select(_ => b.NewLabel()).ToArray();
        b.U16(features.Length);
        for (var i = 0; i < features.Length; i++) b.Tag(features[i].Tag).Off16(featureLabels[i], at);
        for (var i = 0; i < features.Length; i++)
        {
            b.Label(featureLabels[i]);
            var feature = b.Here;
            var parameters = b.NewLabel();
            if (features[i].ParamsTable) b.Off16(parameters, feature);
            else b.U16(features[i].RawParams);
            b.U16(features[i].Lookups.Length);
            foreach (var l in features[i].Lookups) b.U16(l);
            if (features[i].ParamsTable) b.Label(parameters).U16(0).U16(256);   // stylistic set parameters: version 0, UI name ID 256
        }

        b.Label(lookupList);
        at = b.Here;
        var lookupLabels = lookups.Select(_ => b.NewLabel()).ToArray();
        b.U16(lookupCount >= 0 ? lookupCount : lookups.Length);
        foreach (var l in lookupLabels) b.Off16(l, at);
        var order = Enumerable.Range(0, lookups.Length);
        foreach (var i in lookupsReversed ? order.Reverse() : order)
        {
            b.Label(lookupLabels[i]);
            lookups[i](b);
        }
        if (featureVariations is not null)
        {
            b.Label(variations);
            featureVariations(b);
        }
        return b.ToArray();
    }

    private static void Ot02LangSys(Ot02Bytes b, Ot02Lang lang)
    {
        b.U16(lang.LookupOrder).U16(lang.Required).U16(lang.Features.Length);
        foreach (var f in lang.Features) b.U16(f);
    }

    /// <summary>One script „latn“ whose default language system names every feature.</summary>
    internal static byte[] Ot02Latin(Ot02Feature[] features, params Action<Ot02Bytes>[] lookups) =>
        Ot02Layout([new Ot02Script("latn", new Ot02Lang("", [.. Enumerable.Range(0, features.Length)]))], features, lookups);

    internal static void Ot02Coverage(Ot02Bytes b, IEnumerable<int> glyphs)
    {
        var sorted = glyphs.Distinct().Order().ToArray();
        b.U16(1).U16(sorted.Length);
        foreach (var g in sorted) b.U16(g);
    }

    internal static void Ot02ClassDef(Ot02Bytes b, IReadOnlyDictionary<int, int> classes)
    {
        var sorted = classes.OrderBy(c => c.Key).ToArray();
        b.U16(2).U16(sorted.Length);
        foreach (var (glyph, cls) in sorted) b.U16(glyph).U16(glyph).U16(cls);
    }

    /// <summary>A lookup table with its subtables written right after it; markFilteringSet only when not negative.</summary>
    internal static Action<Ot02Bytes> Ot02Lookup(int type, int flag, int markSet, params Action<Ot02Bytes>[] subtables) => b =>
    {
        var start = b.Here;
        var labels = subtables.Select(_ => b.NewLabel()).ToArray();
        b.U16(type).U16(flag).U16(subtables.Length);
        foreach (var l in labels) b.Off16(l, start);
        if (markSet >= 0) b.U16(markSet);
        for (var i = 0; i < subtables.Length; i++)
        {
            b.Label(labels[i]);
            subtables[i](b);
        }
    };

    internal static Action<Ot02Bytes> Ot02Lookup(int type, params Action<Ot02Bytes>[] subtables) => Ot02Lookup(type, 0, -1, subtables);

    /// <summary>Single substitution format 2.</summary>
    internal static Action<Ot02Bytes> Ot02Single(params (int From, int To)[] map) => b =>
    {
        var start = b.Here;
        var coverage = b.NewLabel();
        var sorted = map.OrderBy(m => m.From).ToArray();
        b.U16(2).Off16(coverage, start).U16(sorted.Length);
        foreach (var m in sorted) b.U16(m.To);
        b.Label(coverage);
        Ot02Coverage(b, sorted.Select(m => m.From));
    };

    /// <summary>Single substitution format 1 (delta).</summary>
    internal static Action<Ot02Bytes> Ot02SingleDelta(int[] glyphs, int delta) => b =>
    {
        var start = b.Here;
        var coverage = b.NewLabel();
        b.U16(1).Off16(coverage, start).U16(delta & 0xFFFF);
        b.Label(coverage);
        Ot02Coverage(b, glyphs);
    };

    /// <summary>Ligature substitution format 1; ligatures with the same first glyph share a set, in the order given.</summary>
    internal static Action<Ot02Bytes> Ot02Ligature(params (int[] Components, int Glyph)[] ligatures) => b =>
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
        Ot02Coverage(b, groups.Select(g => g.Key));
    };

    /// <summary>Multiple substitution format 1: coverage, then one sequence per entry of <paramref name="sequences"/>.</summary>
    internal static Action<Ot02Bytes> Ot02Multiple(int[] coverage, params int[][] sequences) => b =>
    {
        var start = b.Here;
        var cov = b.NewLabel();
        var labels = sequences.Select(_ => b.NewLabel()).ToArray();
        b.U16(1).Off16(cov, start).U16(sequences.Length);
        foreach (var l in labels) b.Off16(l, start);
        for (var i = 0; i < sequences.Length; i++)
        {
            b.Label(labels[i]).U16(sequences[i].Length);
            foreach (var g in sequences[i]) b.U16(g);
        }
        b.Label(cov);
        Ot02Coverage(b, coverage);
    };

    /// <summary>Contextual substitution format 3 (GSUB 5): one coverage per input glyph, then the records.</summary>
    internal static Action<Ot02Bytes> Ot02Context(int[][] input, params (int Sequence, int Lookup)[] records) => b =>
    {
        var start = b.Here;
        var labels = input.Select(_ => b.NewLabel()).ToArray();
        b.U16(3).U16(input.Length).U16(records.Length);
        foreach (var l in labels) b.Off16(l, start);
        foreach (var (sequence, lookup) in records) b.U16(sequence).U16(lookup);
        for (var i = 0; i < input.Length; i++)
        {
            b.Label(labels[i]);
            Ot02Coverage(b, input[i]);
        }
    };

    /// <summary>Chained contextual substitution format 3 (GSUB 6); the backtrack from the glyph nearest to the input.</summary>
    internal static Action<Ot02Bytes> Ot02Chain(int[][] backtrack, int[][] input, int[][] lookahead, params (int Sequence, int Lookup)[] records) => b =>
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
        foreach (var (sequence, lookup) in records) b.U16(sequence).U16(lookup);
        k = 0;
        foreach (var glyphs in backtrack.Concat(input).Concat(lookahead))
        {
            b.Label(labels[k++]);
            Ot02Coverage(b, glyphs);
        }
    };

    /// <summary>Chained contextual substitution format 1 (GSUB 6) with one rule set of one rule for the first input glyph.</summary>
    internal static Action<Ot02Bytes> Ot02ChainGlyphs(int[] backtrack, int[] input, int[] lookahead, params (int Sequence, int Lookup)[] records) => b =>
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
        foreach (var (sequence, lookup) in records) b.U16(sequence).U16(lookup);
        b.Label(coverage);
        Ot02Coverage(b, [input[0]]);
    };

    /// <summary>An extension subtable (GSUB 7 / GPOS 9) pointing to <paramref name="inner"/> of the lookup type given.</summary>
    internal static Action<Ot02Bytes> Ot02Extension(int type, Action<Ot02Bytes> inner) => b =>
    {
        var start = b.Here;
        var label = b.NewLabel();
        b.U16(1).U16(type).Off32(label, start);
        b.Label(label);
        inner(b);
    };

    /// <summary>Pair positioning format 1 with XAdvance (and value format 2 as given) for each pair.</summary>
    internal static Action<Ot02Bytes> Ot02Pairs(int valueFormat2, params (int First, int Second, int Advance)[] pairs) => b =>
    {
        var start = b.Here;
        var coverage = b.NewLabel();
        var groups = pairs.GroupBy(p => p.First).OrderBy(g => g.Key).ToArray();
        var sets = groups.Select(_ => b.NewLabel()).ToArray();
        b.U16(1).Off16(coverage, start).U16(4).U16(valueFormat2).U16(groups.Length);
        foreach (var s in sets) b.Off16(s, start);
        var size2 = 2 * System.Numerics.BitOperations.PopCount((uint)valueFormat2);
        for (var i = 0; i < groups.Length; i++)
        {
            var members = groups[i].OrderBy(p => p.Second).ToArray();
            b.Label(sets[i]).U16(members.Length);
            foreach (var p in members)
            {
                b.U16(p.Second).U16(p.Advance & 0xFFFF);
                for (var k = 0; k < size2; k += 2) b.U16(0);
            }
        }
        b.Label(coverage);
        Ot02Coverage(b, groups.Select(g => g.Key));
    };

    /// <summary>A GDEF: glyph classes, mark attachment classes, mark glyph sets (whose offset is written when given, whatever the version).</summary>
    internal static byte[] Ot02Gdef(IReadOnlyDictionary<int, int> classes, IReadOnlyDictionary<int, int>? markClasses = null, int[][]? markSets = null,
        int minor = 0)
    {
        var b = new Ot02Bytes();
        string glyphClasses = b.NewLabel(), attachClasses = b.NewLabel(), sets = b.NewLabel();
        b.U16(1).U16(minor).Off16(glyphClasses, 0).U16(0).U16(0);
        if (markClasses is not null) b.Off16(attachClasses, 0);
        else b.U16(0);
        if (markSets is not null) b.Off16(sets, 0);
        else if (minor >= 2) b.U16(0);
        b.Label(glyphClasses);
        Ot02ClassDef(b, classes);
        if (markClasses is not null)
        {
            b.Label(attachClasses);
            Ot02ClassDef(b, markClasses);
        }
        if (markSets is not null)
        {
            b.Label(sets);
            var at = b.Here;
            var labels = markSets.Select(_ => b.NewLabel()).ToArray();
            b.U16(1).U16(markSets.Length);
            foreach (var l in labels) b.Off32(l, at);
            for (var i = 0; i < markSets.Length; i++)
            {
                b.Label(labels[i]);
                Ot02Coverage(b, markSets[i]);
            }
        }
        return b.ToArray();
    }

    /// <summary>A shaper for Liberation Sans (or the font given) with the GSUB, GPOS and GDEF given, without the kern table.</summary>
    internal static TextShaper Ot02Shaper(byte[]? gsub = null, byte[]? gpos = null, byte[]? gdef = null, byte[]? font = null) =>
        TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(font ?? ShaperLimitTests.Liberation,
            new Dictionary<string, byte[]?> { ["GSUB"] = gsub, ["GPOS"] = gpos, ["GDEF"] = gdef, ["kern"] = null })));

    internal static int[] Ot02Shape(TextShaper shaper, string text, params string[] features) =>
        [.. shaper.Shape(text, features).Select(g => g.Glyph)];

    private static readonly byte[] Ot02Arabic = EmbeddedFonts.ArabicOpenType("NotoSansArabic-Regular.ttf").FileData;

    private static readonly OpenTypeFont Ot02ArabicFont = OpenTypeFont.Parse(Ot02Arabic);

    // ------------------------------------------------------------ OT 2.2, 2.3

    /// <summary>
    /// OT 2.2 sentences 4–6: the values of GPOS are those of the default instance, and PDF-Knead always sets the default instance — in a variable
    /// font (fvar, gvar) pair kerning of −80 for „AV“ moves by exactly −80.
    /// </summary>
    [Fact]
    public void Ot02_VariableFont_GposValuesOfTheDefaultInstance()
    {
        int a = Ot02G('A'), v = Ot02G('V');
        var gpos = Ot02Layout([new Ot02Script("latn", new Ot02Lang("", [0]))], [new Ot02Feature("kern", [0])],
            [Ot02Lookup(2, Ot02Pairs(0, (a, v, -80)))]);
        var shaper = Ot02Shaper(gpos: gpos, font: Ot01Variable(ShaperLimitTests.Liberation));
        Assert.Equal(Ot02Font.AdvanceWidth(a) - 80, shaper.Shape("AV")[0].XAdvance);
    }

    /// <summary>
    /// OT 2.1 sentences 18–19, OT 2.2 sentences 11–14, OT 2.3 sentences 25–28 and OT 1.3 sentences 19–22: a GSUB 1.1 whose FeatureVariations
    /// replace „liga“ (lookup 0, a→b) by lookup 1 (a→c) under a condition that includes the default instance — PDF-Knead chooses no instance and
    /// reads the 1.0 fields only: the lookups of the feature table itself ([0], as in GSUB 1.0), „a“ becomes b.
    /// </summary>
    [Fact]
    public void Ot02_FeatureVariations_AreNotRead_TheFeatureTableApplies()
    {
        int a = Ot02G('a'), b = Ot02G('b'), c = Ot02G('c');
        Ot02Script[] scripts = [new("latn", new Ot02Lang("", [0]))];
        Ot02Feature[] features = [new("liga", [0])];
        Action<Ot02Bytes>[] lookups = [Ot02Lookup(1, Ot02Single((a, b))), Ot02Lookup(1, Ot02Single((a, c)))];
        var gsub11 = Ot02Layout(scripts, features, lookups, minor: 1, featureVariations: t =>
        {
            var start = t.Here;
            string set = t.NewLabel(), substitution = t.NewLabel(), condition = t.NewLabel(), alternate = t.NewLabel();
            t.U16(1).U16(0).U32(1).Off32(set, start).Off32(substitution, start);
            t.Label(set);
            var setAt = t.Here;
            t.U16(1).Off32(condition, setAt);
            t.Label(condition).U16(1).U16(0).U16(0xC000).U16(0x4000);           // axis 0 from −1 to +1: the default instance included
            t.Label(substitution);
            var substitutionAt = t.Here;
            t.U16(1).U16(0).U16(1).U16(0).Off32(alternate, substitutionAt);     // feature 0 → the alternate feature table
            t.Label(alternate).U16(0).U16(1).U16(1);                             // lookup 1
        });
        var gsub10 = Ot02Layout(scripts, features, lookups);
        Assert.Equal([0], new LayoutTable(gsub11, 7).LookupsFor(["liga"]));
        Assert.Equal(new LayoutTable(gsub10, 7).LookupsFor(["liga"]), new LayoutTable(gsub11, 7).LookupsFor(["liga"]));
        Assert.Equal([b], Ot02Shape(Ot02Shaper(gsub11), "a"));
    }

    /// <summary>
    /// OT 2.2 sentences 7–10, OT 2.3 sentences 21–22 and 23–24: a value record may point to a device table (sizes in pixels) or, in a variable
    /// font, to a VariationIndex table; PDF-Knead sets text in font units for the default instance, so only the value itself counts — single
    /// positioning with XAdvance +50 and XAdvDevice (value format 0x0044) changes the advance by exactly 50 with either table.
    /// </summary>
    [Theory]
    [InlineData(false)]   // device table: 12 ppem, +3 (4-bit deltas)
    [InlineData(true)]    // VariationIndex table (delta format 0x8000)
    public void Ot02_SinglePositioning_DeviceOrVariationIndex_OnlyTheValueCounts(bool variationIndex)
    {
        var a = Ot02G('a');
        var gpos = Ot02Layout([new Ot02Script("latn", new Ot02Lang("", [0]))], [new Ot02Feature("kern", [0])],
        [
            Ot02Lookup(1, b =>
            {
                var start = b.Here;
                string coverage = b.NewLabel(), device = b.NewLabel();
                b.U16(1).Off16(coverage, start).U16(0x0044).U16(50).Off16(device, start);
                b.Label(device);
                if (variationIndex) b.U16(0).U16(0).U16(0x8000);
                else b.U16(12).U16(12).U16(2).U16(0x3000);
                b.Label(coverage);
                Ot02Coverage(b, [a]);
            }),
        ]);
        var shaped = Ot02Shaper(gpos: gpos).Shape("a");
        Assert.Equal(Ot02Font.AdvanceWidth(a) + 50, shaped[0].XAdvance);
    }

    /// <summary>
    /// OT 2.3 sentences 18–20: positioning can apply to classes of glyphs given by a ClassDef — pair positioning format 2 with ClassDef1 {A → 1},
    /// ClassDef2 {V → 1} and −80 for the class pair (1, 1) moves „AV“ by −80 and leaves „AX“ alone.
    /// </summary>
    [Fact]
    public void Ot02_PairPositioningFormat2_ByClasses()
    {
        int a = Ot02G('A'), v = Ot02G('V');
        var gpos = Ot02Layout([new Ot02Script("latn", new Ot02Lang("", [0]))], [new Ot02Feature("kern", [0])],
        [
            Ot02Lookup(2, b =>
            {
                var start = b.Here;
                string coverage = b.NewLabel(), first = b.NewLabel(), second = b.NewLabel();
                b.U16(2).Off16(coverage, start).U16(4).U16(0).Off16(first, start).Off16(second, start).U16(2).U16(2);
                b.U16(0).U16(0).U16(0).U16(-80 & 0xFFFF);                                  // class 1 × class 2 records
                b.Label(coverage);
                Ot02Coverage(b, [a]);
                b.Label(first);
                Ot02ClassDef(b, new Dictionary<int, int> { [a] = 1 });
                b.Label(second);
                Ot02ClassDef(b, new Dictionary<int, int> { [v] = 1 });
            }),
        ]);
        var shaper = Ot02Shaper(gpos: gpos);
        Assert.Equal(Ot02Font.AdvanceWidth(a) - 80, shaper.Shape("AV")[0].XAdvance);
        Assert.Equal(Ot02Font.AdvanceWidth(a), shaper.Shape("AX")[0].XAdvance);
    }

    // ------------------------------------------------------------ OT 2.4

    /// <summary>
    /// OT 2.4.1 sentences 12–15: neutral characters (here the Arabic comma) run with the Arabic text beside them and take its script table —
    /// a single substitution under „arab“ {locl} replaces the comma in „سلام،“; the comma alone is neutral text and is not shaped with „arab“.
    /// </summary>
    [Fact]
    public void Ot02_ArabicLocl_NeutralCommaRunsWithTheArabicText()
    {
        int comma = Ot02ArabicFont.GlyphForUnicode('،'), semicolon = Ot02ArabicFont.GlyphForUnicode('؛');
        Assert.True(comma > 0 && semicolon > 0);
        var gsub = Ot02Layout([new Ot02Script("DFLT", new Ot02Lang("", [])), new Ot02Script("arab", new Ot02Lang("", [0]))],
            [new Ot02Feature("locl", [0])], [Ot02Lookup(1, Ot02Single((comma, semicolon)))]);
        var shaper = Ot02Shaper(gsub, font: Ot02Arabic);
        Assert.Equal(semicolon, Ot02Shape(shaper, "سلام،")[^1]);
        Assert.Equal([comma], Ot02Shape(shaper, "،"));
    }

    /// <summary>
    /// OT 2.4.1 sentence 16 and OT 2.4.2 sentence 10: without a default language system (offset NULL; required of DFLT, allowed elsewhere) the
    /// reader does not fail — it takes the first language system of the script („TRK “ under DFLT, „ROM “ under latn), and with none there are no
    /// lookups.
    /// </summary>
    [Theory]
    [InlineData("DFLT", "TRK ", true)]
    [InlineData("DFLT", "TRK ", false)]
    [InlineData("latn", "ROM ", true)]
    [InlineData("latn", "ROM ", false)]
    public void Ot02_NoDefaultLanguageSystem_TakesTheFirstOrNone(string script, string language, bool withLanguage)
    {
        var langs = withLanguage ? new[] { new Ot02Lang(language, [0]) } : [];
        var table = new LayoutTable(Ot02Layout([new Ot02Script(script, null, langs)], [new Ot02Feature("liga", [0])],
            [Ot02Lookup(1, Ot02Single((Ot02G('a'), Ot02G('b'))))]), 7);
        int[] expected = withLanguage ? [0] : [];
        Assert.Equal(expected, table.LookupsFor(["liga"]));
    }

    /// <summary>
    /// OT 2.4.1 sentences 19–20 (today's behaviour): DFLT with a default language system („liga“ → lookup 0) and „TRK “ („liga“ → lookup 1) —
    /// PDF-Knead names no language when shaping, so the default language system applies: [0].
    /// </summary>
    [Fact]
    public void Ot02_DfltWithLanguageSystems_TheDefaultOneApplies()
    {
        var table = new LayoutTable(Ot02Layout([new Ot02Script("DFLT", new Ot02Lang("", [0]), new Ot02Lang("TRK ", [1]))],
            [new Ot02Feature("liga", [0]), new Ot02Feature("liga", [1])], [Ot02Lookup(1), Ot02Lookup(1)]), 7);
        Assert.Equal([0], table.LookupsFor(["liga"], "DFLT"));
    }

    /// <summary>
    /// OT 2.4.2 sentence 6: the language system records must be sorted by tag — a requirement on the font; the reader does not depend on it:
    /// „TRK “ before „DEU “ gives the same lookups as „DEU “ before „TRK “.
    /// </summary>
    [Fact]
    public void Ot02_UnsortedLanguageSystems_ReadTheSame()
    {
        Ot02Feature[] features = [new("liga", [0]), new("liga", [1]), new("liga", [2])];
        Action<Ot02Bytes>[] lookups = [Ot02Lookup(1), Ot02Lookup(1), Ot02Lookup(1)];
        var unsorted = new LayoutTable(Ot02Layout([new Ot02Script("latn", new Ot02Lang("", [0]), new Ot02Lang("TRK ", [1]), new Ot02Lang("DEU ", [2]))],
            features, lookups), 7);
        var sorted = new LayoutTable(Ot02Layout([new Ot02Script("latn", new Ot02Lang("", [0]), new Ot02Lang("DEU ", [2]), new Ot02Lang("TRK ", [1]))],
            features, lookups), 7);
        Assert.Equal([0], unsorted.LookupsFor(["liga"]));
        Assert.Equal(sorted.LookupsFor(["liga"]), unsorted.LookupsFor(["liga"]));
    }

    /// <summary>
    /// OT 2.4.3 sentences 1–3 and 5–8: a language system names its features by index into the FeatureList; the required feature (one index,
    /// 0xFFFF for none) always applies, the others only when asked for; an index beyond the FeatureList is passed over.
    /// </summary>
    [Fact]
    public void Ot02_RequiredFeature_AlwaysApplies_IndicesBeyondTheListAreIgnored()
    {
        Ot02Feature[] features = [new("liga", [0]), new("rqd ", [2])];
        Action<Ot02Bytes>[] lookups = [Ot02Lookup(1), Ot02Lookup(1), Ot02Lookup(1)];
        LayoutTable Table(int required, int[] indices) =>
            new(Ot02Layout([new Ot02Script("latn", new Ot02Lang("", indices, required))], features, lookups), 7);
        Assert.Equal([2], Table(1, [0]).LookupsFor(["kern"]));
        Assert.Equal([0, 2], Table(1, [0]).LookupsFor(["liga"]));
        Assert.Equal([], Table(0xFFFF, [0]).LookupsFor(["kern"]));
        Assert.Equal([0], Table(0xFFFF, [0]).LookupsFor(["liga"]));
        Assert.Equal([0], Table(0xFFFF, [0, 9]).LookupsFor(["liga"]));
    }

    /// <summary>
    /// OT 2.4.3 sentences 13–20: lookupOrderOffset is reserved (passed over, here 0x1234), the feature indices may stand in any order — [1, 0]
    /// with „liga“ → lookup 3 and „clig“ → lookup 1 gives the lookups [1, 3], as [0, 1] does.
    /// </summary>
    [Fact]
    public void Ot02_LangSysFields_LookupOrderPassedOver_IndicesInAnyOrder()
    {
        Ot02Feature[] features = [new("liga", [3]), new("clig", [1])];
        Action<Ot02Bytes>[] lookups = [Ot02Lookup(1), Ot02Lookup(1), Ot02Lookup(1), Ot02Lookup(1)];
        var reversed = new LayoutTable(Ot02Layout([new Ot02Script("latn", new Ot02Lang("", [1, 0], LookupOrder: 0x1234))], features, lookups), 7);
        var plain = new LayoutTable(Ot02Layout([new Ot02Script("latn", new Ot02Lang("", [0, 1]))], features, lookups), 7);
        Assert.Equal([1, 3], reversed.LookupsFor(["liga", "clig"]));
        Assert.Equal(plain.LookupsFor(["liga", "clig"]), reversed.LookupsFor(["liga", "clig"]));
    }

    // ------------------------------------------------------------ OT 2.5.1–2.5.4

    /// <summary>
    /// OT 2.5.1 sentences 8–12: the lookups of all chosen features run in LookupList order, so those of different features alternate — „liga“
    /// → [2, 0], „clig“ → [1], lookup 0 a→b, 1 b→c, 2 c→d: with both features „a“ becomes d, with „liga“ alone b.
    /// </summary>
    [Fact]
    public void Ot02_LookupsOfSeveralFeatures_RunInLookupListOrder()
    {
        int a = Ot02G('a'), b = Ot02G('b'), c = Ot02G('c'), d = Ot02G('d');
        var shaper = Ot02Shaper(Ot02Latin([new("liga", [2, 0]), new("clig", [1])],
            Ot02Lookup(1, Ot02Single((a, b))), Ot02Lookup(1, Ot02Single((b, c))), Ot02Lookup(1, Ot02Single((c, d)))));
        Assert.Equal([d], Ot02Shape(shaper, "a"));
        Assert.Equal([b], Ot02Shape(shaper, "a", "-clig"));
    }

    /// <summary>
    /// OT 2.5.1 sentences 13–17: GSUB features may be processed in stages, within a stage in LookupList order — Arabic: „ccmp“ → [3], „init“ →
    /// [1], „rlig“ → [0, 2], with lookup 3 x→y1, 1 y1→y2, 0 y2→y3, 2 y3→y4: the initial letter goes through 3, 1, 0, 2 and ends as y4; the final
    /// one, which „init“ does not touch, stays y1.
    /// </summary>
    [Fact]
    public void Ot02_ArabicStages_EachInLookupListOrder()
    {
        int G(char ch) => Ot02ArabicFont.GlyphForUnicode(ch);
        int x = G('ب'), y1 = G('ت'), y2 = G('ث'), y3 = G('ج'), y4 = G('ح');
        var gsub = Ot02Layout([new Ot02Script("arab", new Ot02Lang("", [0, 1, 2]))],
            [new Ot02Feature("ccmp", [3]), new Ot02Feature("init", [1]), new Ot02Feature("rlig", [0, 2])],
            [Ot02Lookup(1, Ot02Single((y2, y3))), Ot02Lookup(1, Ot02Single((y1, y2))), Ot02Lookup(1, Ot02Single((y3, y4))), Ot02Lookup(1, Ot02Single((x, y1)))]);
        Assert.Equal([y4, y1], Ot02Shape(Ot02Shaper(gsub, font: Ot02Arabic), "بب"));
    }

    /// <summary>
    /// OT 2.5.2 sentences 1–9: one tag can stand several times in the FeatureList, a feature table per script; the language system reaches its
    /// own by index — „liga“ (0 → lookup 0) for latn, „liga“ (1 → lookup 1) for cyrl.
    /// </summary>
    [Fact]
    public void Ot02_SameFeatureTagTwice_EachScriptItsOwnTable()
    {
        var table = new LayoutTable(Ot02Layout(
            [new Ot02Script("cyrl", new Ot02Lang("", [1])), new Ot02Script("latn", new Ot02Lang("", [0]))],
            [new Ot02Feature("liga", [0]), new Ot02Feature("liga", [1])], [Ot02Lookup(1), Ot02Lookup(1)]), 7);
        Assert.Equal([0], table.LookupsFor(["liga"], "latn"));
        Assert.Equal([1], table.LookupsFor(["liga"], "cyrl"));
    }

    /// <summary>OT 2.5.2 sentences 10–11: feature records should be sorted by tag; with „liga“ before „ccmp“ the reader still finds the lookups of „ccmp“.</summary>
    [Fact]
    public void Ot02_UnsortedFeatureList_FindsEachFeature()
    {
        var table = new LayoutTable(Ot02Latin([new("liga", [0]), new("ccmp", [1])], Ot02Lookup(1), Ot02Lookup(1)), 7);
        Assert.Equal([1], table.LookupsFor(["ccmp"]));
    }

    /// <summary>
    /// OT 2.5.3 sentences 5–11 and 12–14: feature parameters exist only for some features and are not needed to find the lookups — „ss01“ with a
    /// parameter table, and „liga“ whose featureParamsOffset 0xFFFF points nowhere, each give their lookup without error.
    /// </summary>
    [Theory]
    [InlineData("ss01", true, 0)]
    [InlineData("liga", false, 0xFFFF)]
    public void Ot02_FeatureParams_AreNotNeededForTheLookups(string tag, bool table, int raw)
    {
        var layout = new LayoutTable(Ot02Latin([new(tag, [0], raw, table)], Ot02Lookup(1)), 7);
        Assert.Equal([0], layout.LookupsFor([tag]));
    }

    /// <summary>OT 2.5.3 sentences 15–18: the lookup indices of the chosen features are gathered and sorted — „liga“ → [5, 2], „calt“ → [4] give [2, 4, 5].</summary>
    [Fact]
    public void Ot02_LookupIndicesOfTheChosenFeatures_AreSorted()
    {
        var table = new LayoutTable(Ot02Latin([new("liga", [5, 2]), new("calt", [4])], [.. Enumerable.Range(0, 6).Select(_ => Ot02Lookup(1))]), 7);
        Assert.Equal([2, 4, 5], table.LookupsFor(["liga", "calt"]));
    }

    /// <summary>
    /// OT 2.5.4 sentences 1–3: the order of the LookupList, not the place of the tables in the data, decides the order of processing — lookup 0
    /// (a→b) stored last still runs before lookup 1 (b→c): „a“ becomes c.
    /// </summary>
    [Fact]
    public void Ot02_LookupListOrder_NotTheStorageOrder()
    {
        int a = Ot02G('a'), b = Ot02G('b'), c = Ot02G('c'), d = Ot02G('d');
        var gsub = Ot02Layout([new Ot02Script("latn", new Ot02Lang("", [0]))], [new Ot02Feature("liga", [0, 1])],
            [Ot02Lookup(1, Ot02Single((a, b))), Ot02Lookup(1, Ot02Single((b, c))), Ot02Lookup(1, Ot02Single((c, d)))], lookupsReversed: true);
        var table = new LayoutTable(gsub, 7);
        Assert.True(table.Lookup(0).Subtables[0] > table.Lookup(1).Subtables[0]);
        Assert.Equal([c], Ot02Shape(Ot02Shaper(gsub), "a"));
    }

    /// <summary>OT 2.5.4 sentences 5–8: lookupCount bounds the list — with two lookups a feature naming index 5 reaches an empty lookup; shaping changes nothing and does not fail.</summary>
    [Fact]
    public void Ot02_LookupIndexBeyondLookupCount_IsEmpty()
    {
        int a = Ot02G('a'), b = Ot02G('b');
        var gsub = Ot02Latin([new("liga", [5])], Ot02Lookup(1, Ot02Single((a, b))), Ot02Lookup(1, Ot02Single((a, b))));
        Assert.Empty(new LayoutTable(gsub, 7).Lookup(5).Subtables);
        Assert.Equal([a], Ot02Shape(Ot02Shaper(gsub), "a"));
    }

    // ------------------------------------------------------------ OT 2.5.5

    /// <summary>
    /// OT 2.5.5 sentences 6–12: a lookup holds subtables of its one type, several of them allowed — a single substitution lookup with a format 1
    /// subtable (coverage list {a}, delta to b) and a format 2 subtable (coverage range x–z, substitutes A, B, C) turns „ay“ into „bB“; the same
    /// two subtables behind two extension subtables (type 7) are both resolved.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ot02_LookupOfSeveralSubtablesOfOneType(bool extension)
    {
        int a = Ot02G('a'), x = Ot02G('x'), z = Ot02G('z');
        Assert.Equal(2, z - x);
        Action<Ot02Bytes> list = Ot02SingleDelta([a], Ot02G('b') - a);
        Action<Ot02Bytes> range = b =>
        {
            var start = b.Here;
            var coverage = b.NewLabel();
            b.U16(2).Off16(coverage, start).U16(3).U16(Ot02G('A')).U16(Ot02G('B')).U16(Ot02G('C'));
            b.Label(coverage).U16(2).U16(1).U16(x).U16(z).U16(0);
        };
        var lookup = extension ? Ot02Lookup(7, Ot02Extension(1, list), Ot02Extension(1, range)) : Ot02Lookup(1, list, range);
        Assert.Equal(Ot02Gs("bB"), Ot02Shape(Ot02Shaper(Ot02Latin([new("liga", [0])], lookup)), "ay"));
    }

    /// <summary>OT 2.5.5 sentences 13–18: each lookup runs over the whole sequence before the next, which works on its results — lookup 0 a→b, lookup 1 b→c: „ab“ becomes „cc“.</summary>
    [Fact]
    public void Ot02_NextLookup_WorksOnTheResultsOfThePrevious()
    {
        int a = Ot02G('a'), b = Ot02G('b'), c = Ot02G('c');
        var shaper = Ot02Shaper(Ot02Latin([new("liga", [0, 1])], Ot02Lookup(1, Ot02Single((a, b))), Ot02Lookup(1, Ot02Single((b, c)))));
        Assert.Equal([c, c], Ot02Shape(shaper, "ab"));
    }

    /// <summary>
    /// OT 2.5.5 sentences 22–27: where a pattern matches, the operation is done and the glyphs of the input sequence are skipped — a lookup with
    /// the ligatures f f → X and f → F turns „ff“ into X alone (the second f is not substituted again); a lookup with f f only turns „fff“ into X
    /// and a single f (not f and X).
    /// </summary>
    [Fact]
    public void Ot02_MatchedInputSequence_IsSkipped()
    {
        int f = Ot02G('f'), lig = Ot02G('X'), capital = Ot02G('F');
        var both = Ot02Shaper(Ot02Latin([new("liga", [0])], Ot02Lookup(4, Ot02Ligature(([f, f], lig), ([f], capital)))));
        Assert.Equal([lig], Ot02Shape(both, "ff"));
        var pair = Ot02Shaper(Ot02Latin([new("liga", [0])], Ot02Lookup(4, Ot02Ligature(([f, f], lig)))));
        Assert.Equal([lig, f], Ot02Shape(pair, "fff"));
    }

    /// <summary>
    /// OT 2.5.5 sentences 28–29: after pair positioning the next glyph can be the second of the pair — pairs A-V and V-A (−50 each) with value
    /// format 2 = 0 kern both pairs of „AVA“ (−100); with a second value record (XPlacement 0) the second glyph is skipped and only the first pair
    /// counts (−50).
    /// </summary>
    [Theory]
    [InlineData(0, -100)]
    [InlineData(1, -50)]
    public void Ot02_PairPositioning_SecondGlyphMayStartTheNextPair(int valueFormat2, int expected)
    {
        int a = Ot02G('A'), v = Ot02G('V');
        var gpos = Ot02Layout([new Ot02Script("latn", new Ot02Lang("", [0]))], [new Ot02Feature("kern", [0])],
            [Ot02Lookup(2, Ot02Pairs(valueFormat2, (a, v, -50), (v, a, -50)))]);
        var total = Ot02Shaper(gpos: gpos).Shape("AVA").Sum(g => g.XAdvance);
        Assert.Equal(2 * Ot02Font.AdvanceWidth(a) + Ot02Font.AdvanceWidth(v) + expected, total);
    }

    /// <summary>
    /// OT 2.5.5 sentences 30–33 (and OT 2.6 sentences 1–3): the subtables are tried in order at the current glyph; the first that matches is
    /// applied and no further one — both covering a (a→b, a→c): b; the first covering only x (its coverage lacks a, it is passed over): c.
    /// </summary>
    [Theory]
    [InlineData(true, 'b')]
    [InlineData(false, 'c')]
    public void Ot02_FirstMatchingSubtable_IsApplied(bool firstCoversA, char expected)
    {
        int a = Ot02G('a'), x = Ot02G('x');
        var first = firstCoversA ? Ot02Single((a, Ot02G('b'))) : Ot02Single((x, Ot02G('X')));
        var shaper = Ot02Shaper(Ot02Latin([new("liga", [0])], Ot02Lookup(1, first, Ot02Single((a, Ot02G('c'))))));
        Assert.Equal([Ot02G(expected)], Ot02Shape(shaper, "a"));
    }

    /// <summary>
    /// OT 2.5.5 sentences 34–35 and 37–38: chained context with backtrack {x}, input {a} and lookahead {y}, nested a→b — „xay“ becomes „xby“
    /// (x and y stay, only the input is changed), „xaz“ stays.
    /// </summary>
    [Fact]
    public void Ot02_ChainedContext_ChangesOnlyTheInput()
    {
        int a = Ot02G('a'), x = Ot02G('x'), y = Ot02G('y');
        var shaper = Ot02Shaper(Ot02Latin([new("liga", [0])],
            Ot02Lookup(6, Ot02Chain([[x]], [[a]], [[y]], (0, 1))), Ot02Lookup(1, Ot02Single((a, Ot02G('b'))))));
        Assert.Equal(Ot02Gs("xby"), Ot02Shape(shaper, "xay"));
        Assert.Equal(Ot02Gs("xaz"), Ot02Shape(shaper, "xaz"));
    }

    /// <summary>OT 2.5.5 sentence 36: backtrack and lookahead must match too — chained context format 1 [x] a [y]: „ay“ and „xa“ stay, „xay“ is substituted.</summary>
    [Fact]
    public void Ot02_ChainedContext_BacktrackAndLookaheadMustMatch()
    {
        int a = Ot02G('a'), x = Ot02G('x'), y = Ot02G('y');
        var shaper = Ot02Shaper(Ot02Latin([new("liga", [0])],
            Ot02Lookup(6, Ot02ChainGlyphs([x], [a], [y], (0, 1))), Ot02Lookup(1, Ot02Single((a, Ot02G('b'))))));
        Assert.Equal(Ot02Gs("ay"), Ot02Shape(shaper, "ay"));
        Assert.Equal(Ot02Gs("xa"), Ot02Shape(shaper, "xa"));
        Assert.Equal(Ot02Gs("xby"), Ot02Shape(shaper, "xay"));
    }

    /// <summary>OT 2.5.5 sentence 39: after a chained context the next glyph is the one after the input, not after the lookahead — input {a}, lookahead {a}: „aaa“ becomes „bba“.</summary>
    [Fact]
    public void Ot02_ChainedContext_ContinuesAfterTheInput()
    {
        int a = Ot02G('a');
        var shaper = Ot02Shaper(Ot02Latin([new("liga", [0])],
            Ot02Lookup(6, Ot02Chain([], [[a]], [[a]], (0, 1))), Ot02Lookup(1, Ot02Single((a, Ot02G('b'))))));
        Assert.Equal(Ot02Gs("bba"), Ot02Shape(shaper, "aaa"));
    }

    /// <summary>
    /// OT 2.5.5 sentences 40–43: a lookup table gives its type, flags, subtable offsets from its own start and the markFilteringSet — type 4,
    /// flags 0x0018, two subtables and set 1 read as (4, 0x18, the two absolute positions, 1).
    /// </summary>
    [Fact]
    public void Ot02_LookupTable_FieldsAndSubtablePositions()
    {
        var b = new Ot02Bytes();
        string list = b.NewLabel(), lookup = b.NewLabel(), first = b.NewLabel(), second = b.NewLabel();
        b.U16(1).U16(0).U16(0).U16(0).Off16(list, 0);          // header: no scripts, no features
        b.Label(list).U16(1).Off16(lookup, b.Position(list));
        b.Label(lookup);
        var at = b.Here;
        b.U16(4).U16(0x0018).U16(2).Off16(first, at).Off16(second, at).U16(1);
        b.Label(first).U16(1).U16(0).U16(0);
        b.Label(second).U16(1).U16(0).U16(0);
        var data = b.ToArray();
        var read = new LayoutTable(data, 7).Lookup(0);
        Assert.Equal((4, 0x18, 1), (read.Type, read.Flag, read.MarkFilteringSet));
        Assert.Equal([b.Position(first), b.Position(second)], read.Subtables);
    }

    /// <summary>OT 2.5.5 sentences 44–52: markFilteringSet is there only with USE_MARK_FILTERING_SET — flags 0x0008 with a word behind the offsets: −1; 0x0010: the word (7).</summary>
    [Theory]
    [InlineData(0x0008, -1)]
    [InlineData(0x0010, 7)]
    public void Ot02_MarkFilteringSet_OnlyWithItsFlag(int flag, int expected)
    {
        var data = Ot02Latin([new("liga", [0])], Ot02Lookup(1, flag, 7, Ot02Single((Ot02G('a'), Ot02G('b')))));
        Assert.Equal(expected, new LayoutTable(data, 7).Lookup(0).MarkFilteringSet);
    }

    /// <summary>
    /// OT 2.5.5 sentences 53–60: the low bits of the flags are instructions, 0x00E0 reserved, the high byte a mark attachment class — flags
    /// 0x00E0 alone do not skip the mark m; 0x0200 (class 2) skips m of mark attachment class 1.
    /// </summary>
    [Fact]
    public void Ot02_LookupFlagBits_ReservedIgnored_HighByteIsAMarkClass()
    {
        var m = Ot02G('m');
        var gdef = new GlyphDefinitions(Ot02Gdef(new Dictionary<int, int> { [m] = 3 }, new Dictionary<int, int> { [m] = 1 }));
        Assert.False(gdef.Skips(new Lookup(1, 0x00E0, [], -1), m));
        Assert.True(gdef.Skips(new Lookup(1, 0x0200, [], -1), m));
    }

    /// <summary>Ligature a + c → X under the flags given, with GDEF classes b = base, l = ligature, m = mark.</summary>
    private static int[] Ot02LigatureWithFlags(int flag, string text, byte[]? gdef)
    {
        int a = Ot02G('a'), c = Ot02G('c');
        var gsub = Ot02Latin([new("liga", [0])], Ot02Lookup(4, flag, -1, Ot02Ligature(([a, c], Ot02G('X')))));
        return Ot02Shape(Ot02Shaper(gsub, gdef: gdef), text);
    }

    private static byte[] Ot02ClassesBlm() =>
        Ot02Gdef(new Dictionary<int, int> { [Ot02G('b')] = 1, [Ot02G('l')] = 2, [Ot02G('m')] = 3 });

    /// <summary>
    /// OT 2.5.5 sentences 63–65: IGNORE_BASE_GLYPHS, IGNORE_LIGATURES and IGNORE_MARKS skip glyphs of GDEF class 1, 2 and 3 — the ligature a + c
    /// forms over m with 0x8, over l with 0x4, over b with 0x2, and not without a flag.
    /// </summary>
    [Theory]
    [InlineData(0x8, "amc", "Xm")]
    [InlineData(0x4, "alc", "Xl")]
    [InlineData(0x2, "abc", "Xb")]
    [InlineData(0x0, "amc", "amc")]
    public void Ot02_IgnoreFlags_SkipTheirGlyphClass(int flag, string text, string expected)
    {
        Assert.Equal(Ot02Gs(expected), Ot02LigatureWithFlags(flag, text, Ot02ClassesBlm()));
    }

    /// <summary>
    /// OT 2.5.5 sentences 66–69 and 82–83: with USE_MARK_FILTERING_SET (0x0010) marks outside the set are skipped — set 0 = {m1}: the ligature
    /// forms over m2, not over m1; the set comes from GDEF 1.2, a GDEF 1.0 has none (its offset behind the header is not read), so every mark is
    /// outside and both form.
    /// </summary>
    [Theory]
    [InlineData(2, false, true)]
    [InlineData(2, true, false)]
    [InlineData(0, false, true)]
    [InlineData(0, true, true)]
    public void Ot02_MarkFilteringSet_SkipsMarksOutsideTheSet(int gdefMinor, bool markInSet, bool ligature)
    {
        int m1 = Ot02G('m'), m2 = Ot02G('n');
        var gdef = Ot02Gdef(new Dictionary<int, int> { [m1] = 3, [m2] = 3 }, markSets: [[m1]], minor: gdefMinor);
        var text = markInSet ? "amc" : "anc";
        int a = Ot02G('a'), c = Ot02G('c');
        var gsub = Ot02Latin([new("liga", [0])], Ot02Lookup(4, 0x0010, 0, Ot02Ligature(([a, c], Ot02G('X')))));
        var expected = ligature ? [Ot02G('X'), markInSet ? m1 : m2] : Ot02Gs(text);
        Assert.Equal(expected, Ot02Shape(Ot02Shaper(gsub, gdef: gdef), text));
    }

    /// <summary>
    /// OT 2.5.5 sentences 66–69 and 80–81: with a mark attachment class in the flags (0x0100: class 1) marks of other classes are skipped and only
    /// marks of that class are processed — m1 (class 1) interrupts the ligature a + c, m2 (class 2) does not.
    /// </summary>
    [Theory]
    [InlineData("anc", true)]
    [InlineData("amc", false)]
    public void Ot02_MarkAttachmentClassFilter_SkipsMarksOfOtherClasses(string text, bool ligature)
    {
        int m1 = Ot02G('m'), m2 = Ot02G('n');
        var gdef = Ot02Gdef(new Dictionary<int, int> { [m1] = 3, [m2] = 3 }, new Dictionary<int, int> { [m1] = 1, [m2] = 2 });
        var expected = ligature ? [Ot02G('X'), m2] : Ot02Gs(text);
        Assert.Equal(expected, Ot02LigatureWithFlags(0x0100, text, gdef));
    }

    /// <summary>
    /// OT 2.5.5 sentences 70–71: RIGHT_TO_LEFT applies only to cursive attachment and does not set the direction — a single substitution a→b
    /// with flag 0x0001 works as without; a single positioning with that flag leaves Latin text left to right (clusters in text order, bidi level 0).
    /// </summary>
    [Fact]
    public void Ot02_RightToLeftFlag_OnlyForCursiveAttachment()
    {
        int a = Ot02G('a'), b = Ot02G('b');
        var gsub = Ot02Latin([new("liga", [0])], Ot02Lookup(1, 0x0001, -1, Ot02Single((a, b))));
        Assert.Equal([b], Ot02Shape(Ot02Shaper(gsub), "a"));
        var gpos = Ot02Layout([new Ot02Script("latn", new Ot02Lang("", [0]))], [new Ot02Feature("kern", [0])],
        [
            Ot02Lookup(1, 0x0001, -1, t =>
            {
                var start = t.Here;
                var coverage = t.NewLabel();
                t.U16(1).Off16(coverage, start).U16(4).U16(10);
                t.Label(coverage);
                Ot02Coverage(t, [a]);
            }),
        ]);
        var shaped = Ot02Shaper(gpos: gpos).Shape("ab");
        Assert.Equal([0, 1], shaped.Select(g => g.Cluster));
        Assert.Equal(Ot02Font.AdvanceWidth(a) + 10, shaped[0].XAdvance);
        Assert.All(Bidi.Levels("ab"), level => Assert.Equal(0, level));
    }

    /// <summary>
    /// OT 2.5.5 sentences 72–75 (today's reading): the flags act on the further glyphs of the input; a current glyph the flags exclude is passed
    /// over as well — a single substitution m→n with IGNORE_MARKS leaves the mark m; the ligature a + c with IGNORE_MARKS forms over m, which
    /// stays behind it.
    /// </summary>
    [Fact]
    public void Ot02_IgnoreMarks_CurrentMarkPassedOver_FurtherMarksSkipped()
    {
        int m = Ot02G('m');
        var gdef = Ot02Gdef(new Dictionary<int, int> { [m] = 3 });
        var single = Ot02Latin([new("liga", [0])], Ot02Lookup(1, 0x8, -1, Ot02Single((m, Ot02G('n')))));
        Assert.Equal([m], Ot02Shape(Ot02Shaper(single, gdef: gdef), "m"));
        Assert.Equal([Ot02G('X'), m], Ot02LigatureWithFlags(0x8, "amc", gdef));
    }

    /// <summary>
    /// OT 2.5.5 sentences 76–77: in a chained context the flags act on backtrack and lookahead too, the classes from GDEF — backtrack {x}, input
    /// {a} with IGNORE_MARKS: „xma“ is substituted (m skipped), without the flag not.
    /// </summary>
    [Theory]
    [InlineData(0x8, true)]
    [InlineData(0x0, false)]
    public void Ot02_ChainedContext_FlagsActOnTheBacktrack(int flag, bool substituted)
    {
        int a = Ot02G('a'), x = Ot02G('x'), m = Ot02G('m');
        var gsub = Ot02Latin([new("liga", [0])],
            Ot02Lookup(6, flag, -1, Ot02Chain([[x]], [[a]], [], (0, 1))), Ot02Lookup(1, Ot02Single((a, Ot02G('b')))));
        var shaped = Ot02Shape(Ot02Shaper(gsub, gdef: Ot02Gdef(new Dictionary<int, int> { [m] = 3 })), "xma");
        Assert.Equal(substituted ? Ot02Gs("xmb") : Ot02Gs("xma"), shaped);
    }

    /// <summary>
    /// OT 2.5.5 sentences 78–79: with IGNORE_MARKS the marks are passed over as if not in the sequence — „amc“ forms the ligature (text „ac“) and
    /// m stays behind it with its own text; without a GDEF nothing is skipped (the font lacks the class table), „amc“ stays, without error.
    /// </summary>
    [Fact]
    public void Ot02_IgnoreMarks_NeedsTheClassTable()
    {
        int a = Ot02G('a'), c = Ot02G('c'), m = Ot02G('m');
        var gsub = Ot02Latin([new("liga", [0])], Ot02Lookup(4, 0x8, -1, Ot02Ligature(([a, c], Ot02G('X')))));
        var shaped = Ot02Shaper(gsub, gdef: Ot02Gdef(new Dictionary<int, int> { [m] = 3 })).Shape("amc");
        Assert.Equal([Ot02G('X'), m], shaped.Select(g => g.Glyph));
        Assert.Equal(["ac", "m"], shaped.Select(g => g.Text));
        Assert.Equal(Ot02Gs("amc"), Ot02Shape(Ot02Shaper(gsub), "amc"));
    }

    /// <summary>
    /// OT 2.5.5 sentences 84–85: precedence — IGNORE_MARKS over the mark filtering set over the mark attachment class: 0x0118 skips every mark;
    /// 0x0110 with set {m2} skips m1 (mark class 1, outside the set) and keeps m2.
    /// </summary>
    [Fact]
    public void Ot02_MarkFilterPrecedence()
    {
        int m1 = Ot02G('m'), m2 = Ot02G('n');
        var gdef = new GlyphDefinitions(Ot02Gdef(new Dictionary<int, int> { [m1] = 3, [m2] = 3 }, new Dictionary<int, int> { [m1] = 1, [m2] = 2 },
            [[m2]], 2));
        Assert.True(gdef.Skips(new Lookup(4, 0x0118, [], 0), m1));
        Assert.True(gdef.Skips(new Lookup(4, 0x0118, [], 0), m2));
        Assert.True(gdef.Skips(new Lookup(4, 0x0110, [], 0), m1));
        Assert.False(gdef.Skips(new Lookup(4, 0x0110, [], 0), m2));
    }

    /// <summary>
    /// OT 2.5.5 sentences 91–97: the main lookup of a context filters with its pattern and flags, the action is in nested lookups — context
    /// format 3 over {a}{c} with IGNORE_MARKS, nested at place 1 c→d: „amc“ becomes „amd“.
    /// </summary>
    [Fact]
    public void Ot02_Context_MainLookupMatchesWithItsFlags()
    {
        int a = Ot02G('a'), c = Ot02G('c'), m = Ot02G('m');
        var gsub = Ot02Latin([new("liga", [0])],
            Ot02Lookup(5, 0x8, -1, Ot02Context([[a], [c]], (1, 1))), Ot02Lookup(1, Ot02Single((c, Ot02G('d')))));
        Assert.Equal(Ot02Gs("amd"), Ot02Shape(Ot02Shaper(gsub, gdef: Ot02Gdef(new Dictionary<int, int> { [m] = 3 })), "amc"));
    }

    /// <summary>
    /// OT 2.5.5 sentences 98–102: the nested lookups run with their own flags, not those of the main lookup — context over {a}{m}{c} without flags,
    /// nested at place 0 a ligature a + c with IGNORE_MARKS: the ligature forms and m stays; the same nested lookup without the flag forms none.
    /// </summary>
    [Theory]
    [InlineData(0x8, true)]
    [InlineData(0x0, false)]
    public void Ot02_NestedLookups_UseTheirOwnFlags(int nestedFlag, bool ligature)
    {
        int a = Ot02G('a'), c = Ot02G('c'), m = Ot02G('m');
        var gsub = Ot02Latin([new("liga", [0])],
            Ot02Lookup(5, Ot02Context([[a], [m], [c]], (0, 1))), Ot02Lookup(4, nestedFlag, -1, Ot02Ligature(([a, c], Ot02G('X')))));
        var shaped = Ot02Shape(Ot02Shaper(gsub, gdef: Ot02Gdef(new Dictionary<int, int> { [m] = 3 })), "amc");
        Assert.Equal(ligature ? [Ot02G('X'), m] : Ot02Gs("amc"), shaped);
    }

    // ------------------------------------------------------------ OT 2.6, 2.7

    /// <summary>
    /// OT 2.6 sentences 4–8 (and OT 1.3 sentences 23–24, a format not defined): coverage format 1 is a list, format 2 ranges, any other format
    /// covers nothing.
    /// </summary>
    [Fact]
    public void Ot02_CoverageFormats_ListRangesOtherwiseNothing()
    {
        Assert.Equal(1, new TableData([0, 1, 0, 2, 0, 5, 0, 9]).Coverage(0, 9));
        Assert.Equal(5, new TableData([0, 2, 0, 1, 0, 10, 0, 20, 0, 0]).Coverage(0, 15));
        Assert.Equal(-1, new TableData([0, 3, 0, 1, 0, 9]).Coverage(0, 9));
    }

    /// <summary>
    /// OT 2.6 sentences 9–12: the coverage index fetches the values of the subtable — single substitution format 2 with coverage {a, e} and
    /// substitutes [A, E]; a coverage index beyond the sequences of a multiple substitution substitutes nothing, without error.
    /// </summary>
    [Fact]
    public void Ot02_CoverageIndex_SelectsTheValues()
    {
        int a = Ot02G('a'), e = Ot02G('e'), b = Ot02G('b');
        var single = Ot02Shaper(Ot02Latin([new("liga", [0])], Ot02Lookup(1, Ot02Single((a, Ot02G('A')), (e, Ot02G('E'))))));
        Assert.Equal(Ot02Gs("EA"), Ot02Shape(single, "ea"));
        var multiple = Ot02Shaper(Ot02Latin([new("liga", [0])], Ot02Lookup(2, Ot02Multiple([a, b], [Ot02G('A'), Ot02G('A')]))));
        Assert.Equal(Ot02Gs("AAb"), Ot02Shape(multiple, "ab"));
    }

    /// <summary>OT 2.6.1 sentences 1 and 3: coverage format 1 [3, 7, 12] gives the indices 0, 1, 2 by place, −1 for 8 and 13.</summary>
    [Fact]
    public void Ot02_CoverageFormat1_IndexIsThePlace()
    {
        var t = new TableData([0, 1, 0, 3, 0, 3, 0, 7, 0, 12]);
        Assert.Equal([0, 1, 2, -1, -1], new[] { 3, 7, 12, 8, 13 }.Select(g => t.Coverage(0, g)));
    }

    /// <summary>OT 2.6.1 sentence 2: the list must be sorted for the binary search; an unsorted list [12, 3, 7] gives some result for 7 and no error or endless loop.</summary>
    [Fact]
    public void Ot02_CoverageFormat1_Unsorted_EndsWithoutError()
    {
        Assert.InRange(new TableData([0, 1, 0, 3, 0, 12, 0, 3, 0, 7]).Coverage(0, 7), -1, 2);
    }

    /// <summary>OT 2.6.1 sentences 5–9: glyphCount 1000 with only two glyphs in the data — reads beyond count as 0, no error, −1 for a glyph not listed.</summary>
    [Fact]
    public void Ot02_CoverageFormat1_CountBeyondTheData()
    {
        var t = new TableData([0, 1, 0x03, 0xE8, 0, 3, 0, 7]);
        Assert.Equal(-1, t.Coverage(0, 50));
    }

    /// <summary>OT 2.6.2 sentences 1–2, 4–5 and 7: coverage format 2 [10–12 → 0], [20–21 → 3]: 0 and 2 for 10 and 12, 3 and 4 for 20 and 21, −1 for 15.</summary>
    [Fact]
    public void Ot02_CoverageFormat2_IndexFromTheRangeStart()
    {
        var t = new TableData([0, 2, 0, 2, 0, 10, 0, 12, 0, 0, 0, 20, 0, 21, 0, 3]);
        Assert.Equal([0, 2, 3, 4, -1], new[] { 10, 12, 20, 21, 15 }.Select(g => t.Coverage(0, g)));
    }

    /// <summary>
    /// OT 2.6.2 sentences 3 and 6: ranges must be sorted and their startCoverageIndex continuous — unsorted ranges [20–21 → 0], [10–12 → 9] give
    /// results without error; a single substitution format 2 whose coverage claims index 9 with two substitutes changes nothing (since No. 1144 the
    /// index is checked against the count; before, it read beyond the array).
    /// </summary>
    [Fact]
    public void Ot02_CoverageFormat2_BrokenRanges_StayWithinTheData()
    {
        var t = new TableData([0, 2, 0, 2, 0, 20, 0, 21, 0, 0, 0, 10, 0, 12, 0, 9]);
        foreach (var g in new[] { 10, 11, 12, 20, 21 }) Assert.InRange(t.Coverage(0, g), -1, 11);
        int a = Ot02G('a');
        var gsub = Ot02Latin([new("liga", [0])], Ot02Lookup(1, b =>
        {
            var start = b.Here;
            var coverage = b.NewLabel();
            b.U16(2).Off16(coverage, start).U16(2).U16(Ot02G('A')).U16(Ot02G('B'));
            b.Label(coverage).U16(2).U16(1).U16(a).U16(a).U16(9);
        }));
        Assert.Equal([a], Ot02Shape(Ot02Shaper(gsub), "a"));
    }

    /// <summary>OT 2.6.2 sentences 9–18: the fields of a range — one range 0–65535 starting at index 0 gives 65535 for glyph 65535.</summary>
    [Fact]
    public void Ot02_CoverageFormat2_FullRange()
    {
        Assert.Equal(65535, new TableData([0, 2, 0, 1, 0, 0, 0xFF, 0xFF, 0, 0]).Coverage(0, 65535));
    }

    /// <summary>
    /// OT 2.7 sentences 1–2 and 5–9: a ClassDef assigns classes 1, 2, …, every other glyph is class 0; both formats give the same classes for the
    /// same assignment {10 → 1, 11 → 2}, glyph 12 is class 0, an undefined format 3 gives class 0.
    /// </summary>
    [Fact]
    public void Ot02_ClassDef_BothFormatsSameClasses_OthersZero()
    {
        var format1 = new TableData([0, 1, 0, 10, 0, 2, 0, 1, 0, 2]);
        var format2 = new TableData([0, 2, 0, 2, 0, 10, 0, 10, 0, 1, 0, 11, 0, 11, 0, 2]);
        foreach (var g in new[] { 10, 11, 12 }) Assert.Equal(format1.Class(0, g), format2.Class(0, g));
        Assert.Equal([1, 2, 0], new[] { 10, 11, 12 }.Select(g => format1.Class(0, g)));
        Assert.Equal(0, new TableData([0, 3, 0, 10, 0, 1, 0, 1]).Class(0, 10));
    }

    /// <summary>OT 2.7.1 sentences 1–6: format 1 start 20 [1, 0, 3] — classes 1, 0, 3 for 20, 21, 22; 0 for 19 and 23 outside the range.</summary>
    [Fact]
    public void Ot02_ClassDefFormat1_ValuesFromTheStartGlyph()
    {
        var t = new TableData([0, 1, 0, 20, 0, 3, 0, 1, 0, 0, 0, 3]);
        Assert.Equal([1, 0, 3, 0, 0], new[] { 20, 21, 22, 19, 23 }.Select(g => t.Class(0, g)));
    }

    /// <summary>OT 2.7.1 sentences 8–13: glyphCount 500 with three values in the data — glyphs behind the data are class 0, no error.</summary>
    [Fact]
    public void Ot02_ClassDefFormat1_CountBeyondTheData()
    {
        var t = new TableData([0, 1, 0, 20, 0x01, 0xF4, 0, 1, 0, 2, 0, 3]);
        Assert.Equal(3, t.Class(0, 22));
        Assert.Equal(0, t.Class(0, 400));
    }

    /// <summary>OT 2.7.2 sentences 1–2, 4 and 6: format 2 [10–12 → 2], [30–30 → 5] — 2 for 11, 5 for 30, 0 for 20 (not covered).</summary>
    [Fact]
    public void Ot02_ClassDefFormat2_Ranges()
    {
        var t = new TableData([0, 2, 0, 2, 0, 10, 0, 12, 0, 2, 0, 30, 0, 30, 0, 5]);
        Assert.Equal([2, 5, 0], new[] { 11, 30, 20 }.Select(g => t.Class(0, g)));
    }

    /// <summary>OT 2.7.2 sentences 3 and 5: ranges must be sorted and must not overlap; [30–30 → 5], [10–40 → 2] give one of the classes for 30, no error.</summary>
    [Fact]
    public void Ot02_ClassDefFormat2_UnsortedOverlapping_EndsWithoutError()
    {
        Assert.Contains(new TableData([0, 2, 0, 2, 0, 30, 0, 30, 0, 5, 0, 10, 0, 40, 0, 2]).Class(0, 30), new[] { 0, 2, 5 });
    }

    /// <summary>OT 2.7.2 sentences 8–17: the fields of a class range — one range 0–65535 of class 7 gives 7 for 65535 and for 0.</summary>
    [Fact]
    public void Ot02_ClassDefFormat2_FullRange()
    {
        var t = new TableData([0, 2, 0, 1, 0, 0, 0xFF, 0xFF, 0, 7]);
        Assert.Equal(7, t.Class(0, 65535));
        Assert.Equal(7, t.Class(0, 0));
    }
}
