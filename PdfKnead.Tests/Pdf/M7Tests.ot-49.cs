using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-49 (OpenType feature registry, page 64: the registered features a–e). PDF-Knead applies a fixed set of features by default
/// (<see cref="TextShaper.DefaultSubstitutions"/>, <see cref="TextShaper.DefaultPositioning"/>) and no caller names further ones: the optional
/// features of this page stay off, as the registry recommends, and apply only when named in <see cref="TextShaper.Shape(string, IEnumerable{string}?)"/>;
/// alternates of GSUB type 3 are never chosen outside the syllabic scripts. The fonts are Liberation Sans with GSUB or GPOS built by hand (the
/// builders of packages ot-02 and E11/E12); characters Liberation Sans lacks get a cmap of their own. The helpers named Ot49 are used by the
/// ot-50 tests as well.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package ot-49)

    /// <summary>The forms the lookups make (glyphs of Liberation Sans not reached through its cmap by the texts below).</summary>
    private const int Ot49Form = 900, Ot49Form2 = 901, Ot49Form3 = 902;

    /// <summary>The distinct characters of a text, in code point order.</summary>
    private static int[] Ot49Chars(string text) => [.. text.EnumerateRunes().Select(r => r.Value).Distinct().Order()];

    /// <summary>Whether Liberation Sans lacks a character of the text (then <see cref="Ot49Font"/> writes a cmap of its own).</summary>
    private static bool Ot49NeedsCmap(string chars) => chars.EnumerateRunes().Any(r => Ot02Font.GlyphForUnicode(r.Value) <= 0);

    /// <summary>The glyph of a character in <see cref="Ot49Font"/> built for <paramref name="chars"/>.</summary>
    private static int Ot49Glyph(string chars, int c) =>
        Ot49NeedsCmap(chars) ? 600 + Array.IndexOf(Ot49Chars(chars), c) : Ot02Font.GlyphForUnicode(c);

    /// <summary>The glyphs of a text in <see cref="Ot49Font"/> built for <paramref name="chars"/>, one per character.</summary>
    private static int[] Ot49Glyphs(string chars, string text) => [.. text.EnumerateRunes().Select(r => Ot49Glyph(chars, r.Value))];

    /// <summary>
    /// Liberation Sans with the GSUB and GPOS given, without GDEF and the kern table; when it lacks a character of <paramref name="chars"/>, a
    /// cmap mapping each distinct character of it to the glyphs from 600 on.
    /// </summary>
    private static byte[] Ot49Font(string chars, byte[]? gsub, byte[]? gpos = null)
    {
        var changed = new Dictionary<string, byte[]?> { ["GSUB"] = gsub, ["GPOS"] = gpos, ["GDEF"] = null, ["kern"] = null };
        if (Ot49NeedsCmap(chars)) changed["cmap"] = E11Cmap([.. Ot49Chars(chars).Select((c, i) => (c, c, 600 + i))]);
        return ColorFontBuilder.WithTables(ShaperLimitTests.Liberation, changed);
    }

    /// <summary>
    /// A GSUB or GPOS whose scripts each have a default language system naming every feature, one lookup per feature in the order given; the
    /// <paramref name="extra"/> lookups follow them in the LookupList and belong to no feature (lookups a contextual lookup calls).
    /// </summary>
    private static byte[] Ot49Layout(string[] scripts, (string Feature, Action<Ot02Bytes> Lookup)[] entries, params Action<Ot02Bytes>[] extra) =>
        Ot02Layout([.. scripts.Order(StringComparer.Ordinal).Select(s => new Ot02Script(s, new Ot02Lang("", [.. Enumerable.Range(0, entries.Length)])))],
            [.. entries.Select((e, i) => new Ot02Feature(e.Feature, [i]))], [.. entries.Select(e => e.Lookup), .. extra]);

    /// <summary>The glyphs the shaper of <paramref name="font"/> makes of <paramref name="text"/> with the features given (none: the defaults).</summary>
    private static int[] Ot49Shape(byte[] font, string text, params string[] features) =>
        [.. TextShaper.For(OpenTypeFont.Parse(font)).Shape(text, features).Select(g => g.Glyph)];

    /// <summary>An alternate substitution lookup (type 3, format 1) offering <paramref name="alternates"/> for one glyph.</summary>
    private static Action<Ot02Bytes> Ot49Alternates(int glyph, params int[] alternates) => Ot02Lookup(3, b =>
    {
        var start = b.Here;
        string coverage = b.NewLabel(), set = b.NewLabel();
        b.U16(1).Off16(coverage, start).U16(1).Off16(set, start);
        b.Label(set).U16(alternates.Length);
        foreach (var a in alternates) b.U16(a);
        b.Label(coverage);
        Ot02Coverage(b, [glyph]);
    });

    /// <summary>A ligature lookup (type 4) of the glyphs given into one.</summary>
    private static Action<Ot02Bytes> Ot49Ligature(int[] components, int glyph) => Ot02Lookup(4, Ot02Ligature((components, glyph)));

    /// <summary>A single positioning lookup (type 1, format 1) with value format 1 (XPlacement) for one glyph.</summary>
    private static Action<Ot02Bytes> Ot49Placement(int glyph, int xPlacement) => Ot02Lookup(1, b =>
    {
        var start = b.Here;
        var coverage = b.NewLabel();
        b.U16(1).Off16(coverage, start).U16(1).U16(xPlacement & 0xFFFF);
        b.Label(coverage);
        Ot02Coverage(b, [glyph]);
    });

    /// <summary>
    /// A GSUB of the script „latn“ with one feature whose featureParamsOffset points to the parameter table <paramref name="parameters"/>
    /// writes; the feature names all lookups given.
    /// </summary>
    private static byte[] Ot49WithParams(string tag, Action<Ot02Bytes> parameters, params Action<Ot02Bytes>[] lookups)
    {
        var b = new Ot02Bytes();
        string scriptList = b.NewLabel(), featureList = b.NewLabel(), lookupList = b.NewLabel();
        b.U16(1).U16(0).Off16(scriptList, 0).Off16(featureList, 0).Off16(lookupList, 0);
        b.Label(scriptList);
        var at = b.Here;
        var script = b.NewLabel();
        b.U16(1).Tag("latn").Off16(script, at);
        b.Label(script);
        var scriptStart = b.Here;
        var lang = b.NewLabel();
        b.Off16(lang, scriptStart).U16(0);
        b.Label(lang).U16(0).U16(0xFFFF).U16(1).U16(0);
        b.Label(featureList);
        at = b.Here;
        var feature = b.NewLabel();
        b.U16(1).Tag(tag).Off16(feature, at);
        b.Label(feature);
        var featureStart = b.Here;
        var table = b.NewLabel();
        b.Off16(table, featureStart).U16(lookups.Length);
        for (var i = 0; i < lookups.Length; i++) b.U16(i);
        b.Label(table);
        parameters(b);
        b.Label(lookupList);
        at = b.Here;
        var labels = lookups.Select(_ => b.NewLabel()).ToArray();
        b.U16(lookups.Length);
        foreach (var l in labels) b.Off16(l, at);
        for (var i = 0; i < lookups.Length; i++)
        {
            b.Label(labels[i]);
            lookups[i](b);
        }
        return b.ToArray();
    }

    /// <summary>
    /// The parameter table of a character variant feature: format 0, the three name IDs of label, tooltip and sample text (all
    /// <paramref name="label"/>, <paramref name="label"/> + 1, + 2, or NULL for 0), numNamedParameters, firstParamUiLabelNameId, charCount and
    /// the characters as uint24 (padded to an even length).
    /// </summary>
    private static Action<Ot02Bytes> Ot49CharacterVariantParams(int label, int numNamed, int firstName, int[] characters) => b =>
    {
        b.U16(0).U16(label).U16(label == 0 ? 0 : label + 1).U16(label == 0 ? 0 : label + 2).U16(numNamed).U16(firstName).U16(characters.Length);
        var bytes = characters.SelectMany(c => new[] { (byte)(c >> 16), (byte)(c >> 8), (byte)c }).ToList();
        if (bytes.Count % 2 == 1) bytes.Add(0);
        for (var i = 0; i < bytes.Count; i += 2) b.U16(bytes[i] << 8 | bytes[i + 1]);
    };

    /// <summary>Makes RichFonts use the font given for a family of its own and lays out <paramref name="text"/> in it (10 pt).</summary>
    private static RichLayout Ot49LaidOut(string family, byte[] font, string text, double width, BlockAlignment alignment)
    {
        E11UseFont(family, OpenTypeFont.Parse(font), false);
        return RichLayout.Layout(new RichText(text, new RichStyle { Family = family, Size = 10 }), width, alignment, 1.2, 10, 0);
    }

    // ------------------------------------------------------------ optional features: off by default

    /// <summary>
    /// The optional single substitutions of page 64, each off by default and applied only when named: OT 64.12 sentences 1–14 (case: the
    /// parenthesis of „(ABC)“), OT 64.22 sentences 1–16 and 59–77 (cv01: „g“), OT 64.23 sentences 1–14 (c2pc: „A“), OT 64.24 sentences 1–13
    /// (c2sc: „A“), OT 64.27 sentences 1–11 (dnom: the ones of „11/17“ — PDF-Knead applies neither frac nor dnom by itself), OT 64.28
    /// sentences 1–12 (dtls: the mathematical bold small i, PDF-Knead sets no formulas) and OT 64.29 sentences 1–13 (expt: a kanji). A type 1
    /// lookup to another glyph under the feature in the script of the text: the default shaping keeps the glyphs of the cmap; with the feature
    /// named, the character becomes the other glyph.
    /// </summary>
    [Theory]
    [InlineData("case", "(ABC)", "(", "latn")]
    [InlineData("cv01", "g", "g", "latn")]
    [InlineData("c2pc", "A", "A", "latn")]
    [InlineData("c2sc", "A", "A", "latn")]
    [InlineData("dnom", "11/17", "1", "latn")]
    [InlineData("dtls", "\U0001D422", "\U0001D422", "DFLT")]
    [InlineData("expt", "唖", "唖", "hani")]
    public void Ot49_OptionalSingleSubstitution_OffByDefault_AppliedOnlyWhenNamed(string feature, string text, string from, string script)
    {
        var c = char.ConvertToUtf32(from, 0);
        var font = Ot49Font(text, Ot49Layout([script], [(feature, E11Sub((Ot49Glyph(text, c), Ot49Form)))]));
        var cmap = Ot49Glyphs(text, text);
        Assert.Equal(cmap, Ot49Shape(font, text));
        var replaced = text.EnumerateRunes().Select(r => r.Value == c ? Ot49Form : Ot49Glyph(text, r.Value)).ToArray();
        Assert.Equal(replaced, Ot49Shape(font, text, feature));
    }

    /// <summary>
    /// OT 64.5 sentences 1–10 (afrc: „3/4“ into one fraction) and OT 64.26 sentences 1–13 (dlig: „ct“ into one ligature): optional ligatures,
    /// off by default — a ligature of all the characters under the feature: the default shaping gives one glyph per character, with the
    /// feature named one glyph.
    /// </summary>
    [Theory]
    [InlineData("afrc", "3/4")]
    [InlineData("dlig", "ct")]
    public void Ot49_OptionalLigature_OffByDefault_AppliedOnlyWhenNamed(string feature, string text)
    {
        var font = Ot49Font(text, Ot49Layout(["latn"], [(feature, Ot49Ligature(Ot49Glyphs(text, text), Ot49Form))]));
        Assert.Equal(Ot49Glyphs(text, text), Ot49Shape(font, text));
        Assert.Equal([Ot49Form], Ot49Shape(font, text, feature));
    }

    // ------------------------------------------------------------ 64.1 aalt

    /// <summary>
    /// OT 64.1 sentences 1–16 and 17–26 (aalt): PDF-Knead offers no choice of alternates. A type 3 lookup with three alternates for „a“ under
    /// aalt keeps the glyph of the cmap by default and with aalt named; a type 1 lookup (one alternate) for „b“ under aalt applies only when
    /// aalt is named.
    /// </summary>
    [Fact]
    public void Ot49_Aalt_AlternatesNeverChosen_SingleAlternateOnlyWhenNamed()
    {
        int a = Ot02G('a'), b = Ot02G('b');
        var font = Ot49Font("ab", Ot49Layout(["latn"], [("aalt", Ot49Alternates(a, Ot49Form, Ot49Form2, Ot49Form3)), ("aalt", E11Sub((b, Ot49Form)))]));
        Assert.Equal([a, b], Ot49Shape(font, "ab"));
        Assert.Equal([a, Ot49Form], Ot49Shape(font, "ab", "aalt"));
    }

    /// <summary>
    /// OT 64.1 sentences 17–26 (aalt in the program): the text layout of the editor names no optional feature and offers no palette of
    /// alternates — „ab“ laid out in a font with aalt lookups for both letters is set with the glyphs of the cmap.
    /// </summary>
    [Fact]
    public void Ot49_Aalt_TextLayout_SetsTheCmapGlyphs()
    {
        int a = Ot02G('a'), b = Ot02G('b');
        var font = Ot49Font("ab", Ot49Layout(["latn"], [("aalt", Ot49Alternates(a, Ot49Form, Ot49Form2)), ("aalt", E11Sub((b, Ot49Form3)))]));
        var layout = Ot49LaidOut("M7 Ot49 Aalt", font, "ab", 500, BlockAlignment.Left);
        Assert.Equal([a, b], layout.Lines[0].Glyphs.Select(g => g.Glyph));
    }

    // ------------------------------------------------------------ 64.7 apkn, 64.18 cpct, 64.25 dist

    /// <summary>
    /// OT 64.7 sentences 1–20 (apkn): kerning of the proportional kana is off by default and comes with palt — a pair adjustment of −50 for
    /// „ア“ before „ノ“ under apkn (script kana): the default shaping keeps the advance of the font; with palt and apkn named it is 50 smaller.
    /// </summary>
    [Fact]
    public void Ot49_Apkn_OffByDefault_WithPaltNamedKernsThePair()
    {
        const string text = "アノ";
        int a = Ot49Glyph(text, 0x30A2), no = Ot49Glyph(text, 0x30CE);
        var font = Ot49Font(text, null, Ot49Layout(["kana"], [("apkn", Ot02Lookup(2, Ot02Pairs(0, (a, no, -50))))]));
        var advance = OpenTypeFont.Parse(font).AdvanceWidth(a);
        var shaper = TextShaper.For(OpenTypeFont.Parse(font));
        Assert.Equal(advance, shaper.Shape(text)[0].XAdvance);
        Assert.Equal(advance - 50, shaper.Shape(text, ["palt", "apkn"])[0].XAdvance);
    }

    /// <summary>
    /// OT 64.18 sentences 1–10 (cpct): centred CJK punctuation is optional and off by default — a single adjustment with XPlacement +250 for
    /// the ideographic comma under cpct: the default shaping leaves it without offset, with cpct named it is moved by 250.
    /// </summary>
    [Fact]
    public void Ot49_Cpct_OffByDefault_PlacementOnlyWhenNamed()
    {
        const string text = "、";
        var font = Ot49Font(text, null, Ot49Layout(["DFLT", "hani", "kana"], [("cpct", Ot49Placement(Ot49Glyph(text, 0x3001), 250))]));
        var shaper = TextShaper.For(OpenTypeFont.Parse(font));
        Assert.Equal(0, shaper.Shape(text)[0].XOffset);
        Assert.Equal(250, shaper.Shape(text, ["cpct"])[0].XOffset);
    }

    /// <summary>
    /// OT 64.25 sentences 1–14 (dist): dist is applied by default for every script, and its effect adds to that of kern — an advance of −40
    /// for „A“ under dist and −10 under kern: the default shaping makes the advance 50 smaller; with dist switched off 10, with kern off 40.
    /// </summary>
    [Fact]
    public void Ot49_Dist_AppliedByDefault_AddsToKern()
    {
        var a = Ot02G('A');
        var font = Ot49Font("A", null, Ot49Layout(["latn"], [("dist", Ot02Lookup(1, E11SingleAdvance([a], -40))), ("kern", Ot02Lookup(1, E11SingleAdvance([a], -10)))]));
        var shaper = TextShaper.For(OpenTypeFont.Parse(font));
        var advance = Ot02Font.AdvanceWidth(a);
        Assert.Equal(advance - 50, shaper.Shape("A")[0].XAdvance);
        Assert.Equal(advance - 10, shaper.Shape("A", ["-dist"])[0].XAdvance);
        Assert.Equal(advance - 40, shaper.Shape("A", ["-kern"])[0].XAdvance);
    }

    // ------------------------------------------------------------ 64.20 cswh

    /// <summary>
    /// OT 64.20 sentences 1–16 (cswh): a contextual lookup under cswh that calls a type 1 lookup H → swash H: „HOLIDAY“ keeps its H by default
    /// and takes the swash with cswh named; when the called lookup offers alternates (type 3), H stays even with cswh named.
    /// </summary>
    [Fact]
    public void Ot49_Cswh_ContextualSwash_OnlyWhenNamed_AlternatesNotChosen()
    {
        const string text = "HOLIDAY";
        var h = Ot02G('H');
        var plain = Ot02Gs(text);
        int[] swashed = [Ot49Form, .. plain.Skip(1)];
        var single = Ot49Font(text, Ot49Layout(["latn"], [("cswh", Ot02Lookup(5, Ot02Context([[h]], (0, 1))))], E11Sub((h, Ot49Form))));
        Assert.Equal(plain, Ot49Shape(single, text));
        Assert.Equal(swashed, Ot49Shape(single, text, "cswh"));
        var alternates = Ot49Font(text, Ot49Layout(["latn"], [("cswh", Ot02Lookup(5, Ot02Context([[h]], (0, 1))))], Ot49Alternates(h, Ot49Form, Ot49Form2, Ot49Form3)));
        Assert.Equal(plain, Ot49Shape(alternates, text, "cswh"));
    }

    // ------------------------------------------------------------ 64.22 cv01 … cv99: the parameter table

    /// <summary>
    /// OT 64.22 sentences 17–21, 22–37, 38–49 and 50–58 (the parameter table of cv01 … cv99): the shaper needs only the lookup indices of the
    /// feature and reads nothing of the parameter table — „cv01“ with a table of format 0 (all name IDs NULL; two characters as uint24; three
    /// named parameters whose name IDs the font does not have; more named parameters than alternates with the last name ID above 32767) names
    /// its two lookups (type 1 g → other glyph, type 3 with alternates for h) without an exception; „gh“ keeps its glyphs by default, with cv01
    /// named g is replaced and h keeps the glyph of the cmap.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(256, 0, 0, 2)]
    [InlineData(256, 3, 300, 0)]
    [InlineData(256, 5, 32767, 1)]
    public void Ot49_CharacterVariantParams_NotNeededForTheLookups(int label, int numNamed, int firstName, int charCount)
    {
        int g = Ot02G('g'), h = Ot02G('h');
        int[] characters = [.. new[] { 'g', 0x0261, 'h' }.Take(charCount)];
        var gsub = Ot49WithParams("cv01", Ot49CharacterVariantParams(label, numNamed, firstName, characters),
            E11Sub((g, Ot49Form)), Ot49Alternates(h, Ot49Form2, Ot49Form3));
        Assert.Equal([0, 1], new LayoutTable(gsub, 7).LookupsFor(["cv01"]));
        var font = Ot49Font("gh", gsub);
        Assert.Equal([g, h], Ot49Shape(font, "gh"));
        Assert.Equal([Ot49Form, h], Ot49Shape(font, "gh", "cv01"));
    }
}
