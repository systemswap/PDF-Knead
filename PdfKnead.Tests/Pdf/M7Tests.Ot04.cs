using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-04: the examples of the OpenType Common Table Formats (2.11, examples 1–9) as test vectors for <see cref="LayoutTable"/>,
/// <see cref="TableData"/>, <see cref="GlyphDefinitions"/> and <see cref="TextShaper"/>. The bytes of the examples are written out; the GSUB
/// and GPOS around them come from the ot-03 helpers where the example does not fix them.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>Big-endian bytes: an int is a uint16, a string a four-character tag.</summary>
    private static byte[] Ot04Bytes(params object[] fields)
    {
        var bytes = new List<byte>();
        foreach (var field in fields)
            if (field is string tag) bytes.AddRange(tag.Select(c => (byte)c));
            else
            {
                var v = (int)field;
                bytes.Add((byte)(v >> 8));
                bytes.Add((byte)v);
            }
        return [.. bytes];
    }

    /// <summary>A GSUB of version 1.0 from its ScriptList, FeatureList and LookupList, in this order behind the header.</summary>
    private static byte[] Ot04Layout(byte[] scriptList, byte[] featureList, byte[] lookupList) =>
        [.. Ot04Bytes(1, 0, 10, 10 + scriptList.Length, 10 + scriptList.Length + featureList.Length), .. scriptList, .. featureList, .. lookupList];

    /// <summary>A LookupList of <paramref name="count"/> single substitution lookups without subtables.</summary>
    private static byte[] Ot04EmptyLookups(int count)
    {
        var fields = new List<object> { count };
        for (var i = 0; i < count; i++) fields.Add(2 + count * 2 + i * 6);
        for (var i = 0; i < count; i++) fields.AddRange([1, 0, 0]);
        return Ot04Bytes([.. fields]);
    }

    // ------------------------------------------------------------ Example 1

    /// <summary>
    /// OT 2.11.1 sentences 1–15: the ScriptList of example 1 (three scripts hani, kana, latn in alphabetical order, Script tables at 0x14,
    /// 0x18, 0x1C), each Script table with its own default language system naming its own „liga“ feature (lookups 0, 1, 2). The shaper
    /// asks for „latn“ and gets only the lookup of the Latin feature; for a script the list lacks (no DFLT either) the first Script table.
    /// </summary>
    [Fact]
    public void Ot04_Example1_ScriptList_EachScriptItsOwnFeatures()
    {
        var scriptList = Ot04Bytes(3, "hani", 0x14, "kana", 0x18, "latn", 0x1C,
            0x0C, 0, 0x10, 0, 0x14, 0,                                         // Script tables: default LangSys at 0x20, 0x28, 0x30
            0, 0xFFFF, 1, 0, 0, 0xFFFF, 1, 1, 0, 0xFFFF, 1, 2);                // LangSys: feature 0, 1, 2
        var featureList = Ot04Bytes(3, "liga", 20, "liga", 26, "liga", 32, 0, 1, 0, 0, 1, 1, 0, 1, 2);
        var table = new LayoutTable(Ot04Layout(scriptList, featureList, Ot04EmptyLookups(3)), 7);
        Assert.Equal(Ot04Bytes(3, "hani", 0x14, "kana", 0x18, "latn", 0x1C), scriptList[..20]);
        Assert.Equal([2], table.LookupsFor(["liga"], "latn"));
        Assert.Equal([1], table.LookupsFor(["liga"], "kana"));
        Assert.Equal([0], table.LookupsFor(["liga"], "hani"));
        Assert.Equal([0], table.LookupsFor(["liga"], "arab"));
    }

    // ------------------------------------------------------------ Example 2

    /// <summary>
    /// The ScriptList of example 2 under „arab“: the Script table 000A 0001 „URD “ 0016 (with the default language system offset given),
    /// the default LangSys (no required feature, features 0 init, 1 fina, 2 medi) and the Urdu LangSys (required feature 3, same features).
    /// </summary>
    private static byte[] Ot04Example2ScriptList(int defaultLangSys) => Ot04Bytes(1, "arab", 8,
        defaultLangSys, 1, "URD ", 0x16,
        0, 0xFFFF, 3, 0, 1, 2,
        0, 3, 3, 0, 1, 2);

    /// <summary>The FeatureList for example 2: init → lookup 2, fina → 0, medi → 1, and feature 3 („locl“, the Urdu digits) → lookup 3.</summary>
    private static readonly byte[] Ot04Example2Features = Ot04Bytes(4, "init", 26, "fina", 32, "medi", 38, "locl", 44, 0, 1, 2, 0, 1, 0, 0, 1, 1, 0, 1, 3);

    /// <summary>
    /// OT 2.11.2 sentences 12–19: the Script table of example 2 — offset 0x0A to the default language system, one LangSysRecord „URD “ at
    /// 0x16. The lookups of „init“ come over the default language system at 0x0A (lookup 2 only); the same bytes with a default offset of
    /// 0 make the reader take the language system of the first record, at 0x16 (with its required feature: lookups 2 and 3).
    /// </summary>
    [Fact]
    public void Ot04_Example2_ScriptTable_DefaultLanguageSystemFirst()
    {
        var original = new LayoutTable(Ot04Layout(Ot04Example2ScriptList(0x0A), Ot04Example2Features, Ot04EmptyLookups(4)), 7);
        Assert.Equal([2], original.LookupsFor(["init"], "arab"));
        var noDefault = new LayoutTable(Ot04Layout(Ot04Example2ScriptList(0), Ot04Example2Features, Ot04EmptyLookups(4)), 7);
        Assert.Equal([2, 3], noDefault.LookupsFor(["init"], "arab"));
    }

    /// <summary>
    /// OT 2.11.2 sentences 20–26: the default LangSys of example 2 (0000 FFFF 0003 0000 0001 0002) — lookupOrderOffset NULL, no required
    /// feature, three feature indices in any order. init, fina, medi name lookups 2, 0, 1; together they come in LookupList order
    /// [0, 1, 2]; a feature the language system does not name („kern“) brings nothing, nor does a required feature (0xFFFF).
    /// </summary>
    [Fact]
    public void Ot04_Example2_DefaultLanguageSystem_FeaturesInLookupListOrder()
    {
        var table = new LayoutTable(Ot04Layout(Ot04Example2ScriptList(0x0A), Ot04Example2Features, Ot04EmptyLookups(4)), 7);
        Assert.Equal([0, 1, 2], table.LookupsFor(["init", "fina", "medi"], "arab"));
        Assert.Equal([], table.LookupsFor(["kern"], "arab"));
    }

    /// <summary>
    /// OT 2.11.2 sentences 27–33: the Urdu LangSys of example 2 has the required feature 3 (Urdu digits) and the same three features.
    /// With the Urdu language system as the default (offset 0x16), the lookups of „init“ include those of feature 3; with the original
    /// bytes (default at 0x0A) they do not.
    /// </summary>
    [Fact]
    public void Ot04_Example2_UrduLanguageSystem_RequiredFeatureComesAlong()
    {
        var urdu = new LayoutTable(Ot04Layout(Ot04Example2ScriptList(0x16), Ot04Example2Features, Ot04EmptyLookups(4)), 7);
        Assert.Equal([2, 3], urdu.LookupsFor(["init"], "arab"));
        var original = new LayoutTable(Ot04Layout(Ot04Example2ScriptList(0x0A), Ot04Example2Features, Ot04EmptyLookups(4)), 7);
        Assert.Equal([2], original.LookupsFor(["init"], "arab"));
    }

    /// <summary>
    /// OT 2.11.2 sentences 4–11 (today's behaviour; open question on language systems): the shaper has no text language and never picks a
    /// language-specific language system. Noto Sans Arabic with the GSUB of example 2, the required feature 3 of the Urdu language system
    /// replacing the Farsi four (U+06F4) by another glyph: a word ending in U+06F4 keeps the Farsi glyph of the cmap. Only when the Urdu
    /// language system is made the default does the replacement act — so the lookup itself works.
    /// </summary>
    [Fact]
    public void Ot04_Example2_UrduDigits_NotAppliedWithoutTextLanguage()
    {
        var arabic = EmbeddedFonts.ArabicOpenType("NotoSansArabic-Regular.ttf");
        int farsi = arabic.GlyphForUnicode(0x06F4), other = arabic.GlyphForUnicode(0x0664);
        Assert.True(farsi > 0 && other > 0 && farsi != other);
        // lookups 0–2 without subtables, lookup 3 (at 28): single substitution format 2, U+06F4 → the other glyph
        var lookups = Ot04Bytes(4, 10, 16, 22, 28, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 1, 8, 2, 8, 1, other, 1, 1, farsi);
        TextShaper Shaper(int defaultLangSys) => TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(arabic.FileData,
            new Dictionary<string, byte[]?>
            {
                ["GSUB"] = Ot04Layout(Ot04Example2ScriptList(defaultLangSys), Ot04Example2Features, lookups),
                ["GPOS"] = null,
            })));
        const string word = "بب۴";
        Assert.Equal(farsi, Shaper(0x0A).Shape(word)[^1].Glyph);
        Assert.Equal(other, Shaper(0x16).Shape(word)[^1].Glyph);
    }

    // ------------------------------------------------------------ Example 3

    /// <summary>The FeatureList of example 3: three „liga“ features at 0x14, 0x1A, 0x22 with the lookups [1], [0, 1] and [0, 1, 2] (or the third in reverse order).</summary>
    private static byte[] Ot04Example3Features(bool reversed = false) => Ot04Bytes(
        [3, "liga", 0x14, "liga", 0x1A, "liga", 0x22, 0, 1, 1, 0, 2, 0, 1, 0, 3, .. (reversed ? new object[] { 2, 1, 0 } : [0, 1, 2])]);

    /// <summary>A ScriptList with „latn“ whose default language system names the one feature index given.</summary>
    private static byte[] Ot04LatinNaming(int featureIndex) => Ot04Bytes(1, "latn", 8, 4, 0, 0, 0xFFFF, 1, featureIndex);

    /// <summary>
    /// OT 2.11.3 sentences 1–6, 10–11: of the three „liga“ features of example 3 only the one the language system names applies, with all
    /// its lookups. The default language system names feature 1: „liga“ brings lookups [0, 1], not [0, 1, 2].
    /// </summary>
    [Fact]
    public void Ot04_Example3_OnlyTheFeatureOfTheLanguageSystem()
    {
        var table = new LayoutTable(Ot04Layout(Ot04LatinNaming(1), Ot04Example3Features(), Ot04EmptyLookups(3)), 7);
        Assert.Equal([0, 1], table.LookupsFor(["liga"], "latn"));
    }

    /// <summary>
    /// OT 2.11.3 sentences 7–9, 12–13 (today's behaviour; open question on language systems): language systems „DEU “ (feature 2, with the
    /// eszett ligature) and „TRK “ (feature 0, without fi) under „latn“, the default naming feature 1. Lookup 0 is f i → fi, lookup 1 f l → fl,
    /// lookup 2 s s → ß. The shaper always takes the default: „fi“ becomes the fi ligature (also for Turkish text), „fl“ the fl ligature,
    /// and „ss“ never the eszett.
    /// </summary>
    [Fact]
    public void Ot04_Example3_LanguageSystemsAreNeverChosen()
    {
        int G(int c)
        {
            var g = Ot03Font.GlyphForUnicode(c);
            Assert.NotEqual(0, g);
            return g;
        }
        // ligature lookup of 32 bytes: lookup (type 4, one subtable at +8), subtable (coverage at +8, one set at +14), coverage, set, ligature
        object[] Ligature(int first, int second, int ligature) => [4, 0, 1, 8, 1, 8, 1, 14, 1, 1, G(first), 1, 4, G(ligature), 2, G(second)];
        var lookups = Ot04Bytes([3, 8, 40, 72, .. Ligature('f', 'i', 0xFB01), .. Ligature('f', 'l', 0xFB02), .. Ligature('s', 's', 0xDF)]);
        var scriptList = Ot04Bytes(1, "latn", 8, 16, 2, "DEU ", 24, "TRK ", 32, 0, 0xFFFF, 1, 1, 0, 0xFFFF, 1, 2, 0, 0xFFFF, 1, 0);
        var shaper = TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, new Dictionary<string, byte[]?>
        {
            ["GSUB"] = Ot04Layout(scriptList, Ot04Example3Features(), lookups),
            ["GPOS"] = null,
        })));
        Assert.Equal([G(0xFB01)], shaper.Shape("fi").Select(g => g.Glyph).ToArray());
        Assert.Equal([G(0xFB02)], shaper.Shape("fl").Select(g => g.Glyph).ToArray());
        Assert.Equal([G('s'), G('s')], shaper.Shape("ss").Select(g => g.Glyph).ToArray());
    }

    /// <summary>
    /// OT 2.11.3 sentences 14–26: the FeatureList of example 3 (0003, three times „liga“ with 0014, 001A, 0022). A language system reaches
    /// a feature by its index, not its tag: index 0 → lookups [1], index 2 → [0, 1, 2].
    /// </summary>
    [Fact]
    public void Ot04_Example3_FeatureList_ByIndex()
    {
        Assert.Equal(Ot04Bytes(3, "liga", 0x14, "liga", 0x1A, "liga", 0x22), Ot04Example3Features()[..20]);
        Assert.Equal([1], new LayoutTable(Ot04Layout(Ot04LatinNaming(0), Ot04Example3Features(), Ot04EmptyLookups(3)), 7).LookupsFor(["liga"], "latn"));
        Assert.Equal([0, 1, 2], new LayoutTable(Ot04Layout(Ot04LatinNaming(2), Ot04Example3Features(), Ot04EmptyLookups(3)), 7).LookupsFor(["liga"], "latn"));
    }

    /// <summary>
    /// OT 2.11.3 sentences 27–41: the feature tables of example 3 (featureParamsOffset NULL, 1, 2 or 3 lookup indices in any order). The
    /// third with its indices reversed (0002 0001 0000) still brings [0, 1, 2].
    /// </summary>
    [Fact]
    public void Ot04_Example3_FeatureTables_IndicesInAnyOrder()
    {
        var table = new LayoutTable(Ot04Layout(Ot04LatinNaming(2), Ot04Example3Features(reversed: true), Ot04EmptyLookups(3)), 7);
        Assert.Equal([0, 1, 2], table.LookupsFor(["liga"], "latn"));
    }

    // ------------------------------------------------------------ Example 4

    /// <summary>The LookupList of example 4 (three ligature lookups, flag 0x000C, one subtable each at 0x18, 0x28, 0x38), padded so that the subtables lie in the table.</summary>
    private static readonly byte[] Ot04Example4Lookups =
        [.. Ot04Bytes(3, 0x08, 0x10, 0x18, 4, 0x0C, 1, 0x18, 4, 0x0C, 1, 0x28, 4, 0x0C, 1, 0x38), .. new byte[0x60 - 0x20]];

    /// <summary>
    /// OT 2.11.4 sentences 1–10: the LookupList of example 4 — lookupCount 3, offsets 0x08, 0x10, 0x18 from its start. Three lookups of type
    /// 4 with their subtables at LookupList + 0x08 + 0x18, + 0x10 + 0x28, + 0x18 + 0x38; an index beyond them is an empty lookup.
    /// </summary>
    [Fact]
    public void Ot04_Example4_LookupList()
    {
        var table = new LayoutTable(Ot04Bytes(1, 0, 0, 0, 10).Concat(Ot04Example4Lookups).ToArray(), 7);
        Assert.Equal(3, table.LookupCount);
        int[] subtables = [10 + 0x08 + 0x18, 10 + 0x10 + 0x28, 10 + 0x18 + 0x38];
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(4, table.Lookup(i).Type);
            Assert.Equal([subtables[i]], table.Lookup(i).Subtables);
        }
        Assert.Equal(0, table.Lookup(3).Type);
        Assert.Empty(table.Lookup(3).Subtables);
    }

    /// <summary>
    /// OT 2.11.4 sentences 11–25: the lookups of example 4 have lookupFlag 0x000C (ignore ligatures and marks). With GDEF classes 1, 2, 3
    /// such a lookup skips class 2 and 3, not class 1; the ligature f i under that flag forms across a mark (class 3) between f and i, and
    /// the mark stays.
    /// </summary>
    [Fact]
    public void Ot04_Example4_LookupFlag000C_SkipsLigaturesAndMarks()
    {
        var table = new LayoutTable(Ot04Bytes(1, 0, 0, 0, 10).Concat(Ot04Example4Lookups).ToArray(), 7);
        Assert.Equal(0x000C, table.Lookup(0).Flag);
        var classes = new GlyphDefinitions(Ot04Bytes(1, 0, 12, 0, 0, 0, 2, 3, 10, 10, 1, 11, 11, 2, 12, 12, 3));
        var lookup = new Lookup(4, 0x000C, [], -1);
        Assert.False(classes.Skips(lookup, 10));
        Assert.True(classes.Skips(lookup, 11));
        Assert.True(classes.Skips(lookup, 12));

        var acute = Ot03Font.GlyphForUnicode('́');
        var gdef = new Ot03Part().U16(1, 0).Off(Ot03Classes((acute, 3))).U16(0, 0, 0).Build();
        var shaper = Ot03Shaper(Ot03Liga(Ot03Ligature("fi", 'ﬁ', 0x000C)), gdef);
        Assert.Equal([Ot03G('ﬁ'), acute], Ot03Shape(shaper, "f́i"));
    }

    // ------------------------------------------------------------ Examples 5–8

    /// <summary>
    /// OT 2.11.5 sentences 1–12: the Coverage of example 5 (format 1, five glyphs 0x38, 0x3B, 0x41, 0x42, 0x4A in ascending order): each
    /// glyph has its place in the list, glyphs in between or behind are not covered.
    /// </summary>
    [Fact]
    public void Ot04_Example5_CoverageFormat1()
    {
        var t = new TableData(Ot04Bytes(1, 5, 0x38, 0x3B, 0x41, 0x42, 0x4A));
        Assert.Equal(0, t.Coverage(0, 0x38));
        Assert.Equal(1, t.Coverage(0, 0x3B));
        Assert.Equal(2, t.Coverage(0, 0x41));
        Assert.Equal(3, t.Coverage(0, 0x42));
        Assert.Equal(4, t.Coverage(0, 0x4A));
        Assert.Equal(-1, t.Coverage(0, 0x39));
        Assert.Equal(-1, t.Coverage(0, 0x4B));
    }

    /// <summary>
    /// OT 2.11.6 sentences 1–13: the Coverage of example 6 (format 2, one range 0x4E–0x57, startCoverageIndex 0): the first glyph has index
    /// 0, the next 1 and so on; outside the range nothing. With startCoverageIndex 5 the first glyph has index 5.
    /// </summary>
    [Fact]
    public void Ot04_Example6_CoverageFormat2()
    {
        var t = new TableData(Ot04Bytes(2, 1, 0x4E, 0x57, 0));
        Assert.Equal(0, t.Coverage(0, 0x4E));
        Assert.Equal(1, t.Coverage(0, 0x4F));
        Assert.Equal(9, t.Coverage(0, 0x57));
        Assert.Equal(-1, t.Coverage(0, 0x4D));
        Assert.Equal(-1, t.Coverage(0, 0x58));
        Assert.Equal(5, new TableData(Ot04Bytes(2, 1, 0x4E, 0x57, 5)).Coverage(0, 0x4E));
    }

    /// <summary>The ClassDef of example 7: format 1, startGlyph 0x32 („a“), 26 class values for a to z.</summary>
    private static readonly TableData Ot04Example7 = new(Ot04Bytes(1, 0x32, 0x1A,
        0, 1, 0, 1, 0, 1, 2, 1, 0, 2, 1, 1, 0, 0, 0, 2, 2, 0, 0, 1, 0, 0, 0, 0, 2, 0));

    /// <summary>
    /// OT 2.11.7 sentences 1–20: the ClassDef of example 7 for a to j — a 0, b 1, g 2, i 0 (the value of the array counts, not the comment
    /// beside it); glyphs before or behind the array are class 0.
    /// </summary>
    [Fact]
    public void Ot04_Example7_ClassDefFormat1_AToJ()
    {
        Assert.Equal(0, Ot04Example7.Class(0, 0x32));
        Assert.Equal(1, Ot04Example7.Class(0, 0x33));
        Assert.Equal(2, Ot04Example7.Class(0, 0x38));
        Assert.Equal(0, Ot04Example7.Class(0, 0x3A));
        Assert.Equal(2, Ot04Example7.Class(0, 0x3B));
        Assert.Equal(0, Ot04Example7.Class(0, 0x31));
        Assert.Equal(0, Ot04Example7.Class(0, 0x4C));
    }

    /// <summary>OT 2.11.7 sentences 21–36: the ClassDef of example 7 for k to z — p 2, t 1, y 2, z 0 (the last glyph of the array).</summary>
    [Fact]
    public void Ot04_Example7_ClassDefFormat1_KToZ()
    {
        Assert.Equal(1, Ot04Example7.Class(0, 0x3C));
        Assert.Equal(2, Ot04Example7.Class(0, 0x41));
        Assert.Equal(1, Ot04Example7.Class(0, 0x45));
        Assert.Equal(2, Ot04Example7.Class(0, 0x4A));
        Assert.Equal(0, Ot04Example7.Class(0, 0x4B));
    }

    /// <summary>The ClassDef of example 8: format 2, ranges 0x30–0x31 class 2, 0x40–0x41 class 3, 0xD2–0xD3 class 1.</summary>
    private static readonly TableData Ot04Example8 = new(Ot04Bytes(2, 3, 0x30, 0x31, 2, 0x40, 0x41, 3, 0xD2, 0xD3, 1));

    /// <summary>
    /// OT 2.11.8 sentences 1–8: in the ClassDef of example 8 only classes 1–3 are listed, every other glyph is class 0 — one between the
    /// ranges (0x32, 0x42) and one before the first (0x2F).
    /// </summary>
    [Fact]
    public void Ot04_Example8_ClassDefFormat2_UnlistedGlyphsAreClass0()
    {
        Assert.Equal(0, Ot04Example8.Class(0, 0x32));
        Assert.Equal(0, Ot04Example8.Class(0, 0x42));
        Assert.Equal(0, Ot04Example8.Class(0, 0x2F));
    }

    /// <summary>OT 2.11.8 sentences 9–25: the three ranges of example 8 give their classes, the glyph behind the last range class 0.</summary>
    [Fact]
    public void Ot04_Example8_ClassDefFormat2_Ranges()
    {
        Assert.Equal(2, Ot04Example8.Class(0, 0x30));
        Assert.Equal(2, Ot04Example8.Class(0, 0x31));
        Assert.Equal(3, Ot04Example8.Class(0, 0x40));
        Assert.Equal(3, Ot04Example8.Class(0, 0x41));
        Assert.Equal(1, Ot04Example8.Class(0, 0xD2));
        Assert.Equal(1, Ot04Example8.Class(0, 0xD3));
        Assert.Equal(0, Ot04Example8.Class(0, 0xD4));
    }

    // ------------------------------------------------------------ Example 9

    /// <summary>
    /// OT 2.11.9 sentences 1–14: the device table of example 9 (11 to 15 ppem, delta format 1, 000B 000F 0001 5540) adjusts for pixel
    /// sizes; the shaper sets in font units and leaves it out. Single positioning with valueFormat 0x0011 (XPlacement 120 and XPlaDevice
    /// to that table) moves „A“ by exactly 120; pair positioning with valueFormat1 0x0014 (XAdvance −70 and XPlaDevice) and valueFormat2
    /// 0x0001 (XPlacement 33) reads the second value record behind the four bytes of the first.
    /// </summary>
    [Fact]
    public void Ot04_Example9_DeviceTable_LeftOut()
    {
        int a = Ot03G('A'), v = Ot03G('V');
        var device = new Ot03Part().U16(0x000B, 0x000F, 0x0001, 0x5540);
        var single = Ot03Lookup(1, 0, new Ot03Part().U16(1).Off(Ot03Cov(a)).U16(0x0011, 120).Off(device));
        Assert.Equal(120, Ot03GposShaper(Ot03Header([single], [("kern", [0])])).Shape("A")[0].XOffset);

        var pairSet = new Ot03Part().U16(1).U16(v, -70).Off(new Ot03Part().U16(0x000B, 0x000F, 0x0001, 0x5540)).U16(33);
        var pair = Ot03Lookup(2, 0, new Ot03Part().U16(1).Off(Ot03Cov(a)).U16(0x0014, 0x0001, 1).Off(pairSet));
        var shaped = Ot03GposShaper(Ot03Header([pair], [("kern", [0])])).Shape("AV");
        Assert.Equal(Ot03Font.AdvanceWidth(a) - 70, shaped[0].XAdvance);
        Assert.Equal(33, shaped[1].XOffset);
    }
}
