using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-41 (OpenType: the table MATH and the layout tags for mathematical typesetting). PDF-Knead sets no formulas: MATH is not read
/// and is left out when writing (M7NormTests.Fg1172_Compression_LeavesOutTablesThatDependOnGlyphs, M7NormTests.Ot01_BaseJstfMath_NotUsedAndLeftOut);
/// the tests here show that the script tag „math“ is never requested and that the features of mathematical accents are not switched on by
/// default. Fonts: Liberation Sans with a GSUB built by hand.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (ot-41)

    private static readonly Lazy<OpenTypeFont> Ot41Sans = new(() => OpenTypeFont.Parse(ShaperLimitTests.Liberation));

    private static int Ot41Glyph(int codePoint) => Ot41Sans.Value.GlyphForUnicode(codePoint);

    /// <summary>The shaper of Liberation Sans with the GSUB given and without GPOS and the kern table.</summary>
    private static TextShaper Ot41Shaper(byte[] gsub) =>
        TextShaper.For(OpenTypeFont.Parse(ColorFontBuilder.WithTables(ShaperLimitTests.Liberation,
            new Dictionary<string, byte[]?> { ["GSUB"] = gsub, ["GPOS"] = null, ["kern"] = null })));

    /// <summary>
    /// A GSUB with several scripts: each script record (tags in the order given, which must be sorted) has only a default language system naming
    /// the feature indices given; the FeatureList holds the features with their lookup indices; <paramref name="lookups"/> writes the LookupList.
    /// </summary>
    private static byte[] Ot41Gsub((string Tag, int[] Features)[] scripts, (string Tag, int[] Lookups)[] features, Action<ShaperLimitTests.Table> lookups)
    {
        var scriptList = new ShaperLimitTests.Table().U16(scripts.Length);
        var at = 2 + 6 * scripts.Length;
        foreach (var (tag, indices) in scripts)
        {
            scriptList.Tag(tag).U16(at);
            at += 4 + 6 + 2 * indices.Length;   // Script (default LangSys at +4, no records) and its LangSys
        }
        foreach (var (_, indices) in scripts)
            scriptList.U16(4).U16(0).U16(0).U16(0xFFFF).U16(indices.Length).Repeat(indices.Length, i => indices[i]);
        var featureList = new ShaperLimitTests.Table().U16(features.Length);
        at = 2 + 6 * features.Length;
        foreach (var (tag, indices) in features)
        {
            featureList.Tag(tag).U16(at);
            at += 4 + 2 * indices.Length;
        }
        foreach (var (_, indices) in features) featureList.U16(0).U16(indices.Length).Repeat(indices.Length, i => indices[i]);
        var lookupList = new ShaperLimitTests.Table();
        lookups(lookupList);
        var header = new ShaperLimitTests.Table().U16(1).U16(0).U16(10).U16(10 + scriptList.Length).U16(10 + scriptList.Length + featureList.Length);
        return [.. header.ToArray(), .. scriptList.ToArray(), .. featureList.ToArray(), .. lookupList.ToArray()];
    }

    /// <summary>A LookupList with one single substitution (format 2) of <paramref name="from"/> by <paramref name="to"/>.</summary>
    private static void Ot41Single(ShaperLimitTests.Table t, int from, int to)
    {
        t.U16(1).U16(4);                         // LookupList: one lookup at +4
        t.U16(1).U16(0).U16(1).U16(8);           // single substitution, one subtable at +8
        t.U16(2).U16(8).U16(1).U16(to);          // format 2: coverage at +8, one substitute
        t.U16(1).U16(1).U16(from);               // coverage format 1
    }

    /// <summary>A LookupList with one ligature substitution of <paramref name="first"/>, <paramref name="second"/> by <paramref name="ligature"/>.</summary>
    private static void Ot41Ligature(ShaperLimitTests.Table t, int first, int second, int ligature)
    {
        t.U16(1).U16(4);                         // LookupList: one lookup at +4
        t.U16(4).U16(0).U16(1).U16(8);           // ligature substitution, one subtable at +8
        t.U16(1).U16(8).U16(1).U16(14);          // format 1: coverage at +8, one ligature set at +14
        t.U16(1).U16(1).U16(first);              // coverage format 1
        t.U16(1).U16(4);                         // ligature set: one ligature at +4
        t.U16(ligature).U16(2).U16(second);      // the ligature and its second component
    }

    // ------------------------------------------------------------ OT 50.3: layout tags for mathematical typesetting

    /// <summary>
    /// OT 50.3 sentences 1–6 (script tag „math“, only with the default language system): PDF-Knead sets no formulas and never requests the script
    /// „math“ — Liberation Sans with a GSUB whose scripts are „latn“ (no feature) and „math“ (the ligature „ab“ → „x“ under „liga“): shaping „ab“
    /// keeps „a“, „b“, because the Latin text takes the language system of „latn“; with the same feature under „latn“ the ligature applies (the
    /// lookup itself works).
    /// </summary>
    [Fact]
    public void Ot41_MathScriptTag_NeverRequested()
    {
        int a = Ot41Glyph('a'), b = Ot41Glyph('b'), x = Ot41Glyph('x');
        var mathOnly = Ot41Shaper(Ot41Gsub([("latn", []), ("math", [0])], [("liga", [0])], t => Ot41Ligature(t, a, b, x)));
        Assert.Equal([a, b], mathOnly.Shape("ab").Select(g => g.Glyph));
        var latinToo = Ot41Shaper(Ot41Gsub([("latn", [0]), ("math", [0])], [("liga", [0])], t => Ot41Ligature(t, a, b, x)));
        Assert.Equal([x], latinToo.Shape("ab").Select(g => g.Glyph));
    }

    /// <summary>
    /// OT 50.3 sentences 16–21 (feature „flac“, flattened accent forms): not one of the default features and not asked for by the layout of own text
    /// — Liberation Sans with a GSUB feature „flac“ (single substitution „a“ → „x“): shaping „a“ without further features gives „a“, with „flac“
    /// switched on „x“.
    /// </summary>
    [Fact]
    public void Ot41_Flac_NotADefaultFeature()
    {
        int a = Ot41Glyph('a'), x = Ot41Glyph('x');
        var shaper = Ot41Shaper(ShaperLimitTests.Gsub(1, [0], t => Ot41Single(t, a, x), "flac"));
        Assert.DoesNotContain("flac", TextShaper.DefaultSubstitutions);
        Assert.Equal([a], shaper.Shape("a").Select(g => g.Glyph));
        Assert.Equal([x], shaper.Shape("a", ["flac"]).Select(g => g.Glyph));
    }

    /// <summary>
    /// OT 50.3 sentences 22–25 (feature „dtls“, dotless forms as accent bases): not one of the default features — Liberation Sans with a GSUB
    /// feature „dtls“ (single substitution „i“ → dotless „ı“): shaping „i“ without further features gives the glyph of the cmap, with „dtls“
    /// switched on the dotless one.
    /// </summary>
    [Fact]
    public void Ot41_Dtls_NotADefaultFeature()
    {
        int i = Ot41Glyph('i'), dotless = Ot41Glyph(0x0131);
        Assert.NotEqual(0, dotless);
        var shaper = Ot41Shaper(ShaperLimitTests.Gsub(1, [0], t => Ot41Single(t, i, dotless), "dtls"));
        Assert.DoesNotContain("dtls", TextShaper.DefaultSubstitutions);
        Assert.Equal([i], shaper.Shape("i").Select(g => g.Glyph));
        Assert.Equal([dotless], shaper.Shape("i", ["dtls"]).Select(g => g.Glyph));
    }
}
