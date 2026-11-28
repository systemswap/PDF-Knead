using System.Buffers.Binary;
using System.Reflection;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, variable fonts and residues in the tables of written fonts (docs/FEHLERGEDAECHTNIS.md, No. 1159): every writer of a font program —
/// subset, compression with all tables, scrubbing (glyf, „CFF “ and CFF2 outlines) and the PDF/A rebuild — writes the static default instance,
/// without the tables of the variations; named instances of installed variable fonts are found through fvar. The limits and ranges that
/// describe the glyphs are computed anew from the glyphs kept.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (FG 1159)

    /// <summary>
    /// All eight tables of a variable font for <paramref name="glyphCount"/> glyphs: fvar (wght), gvar without deltas, avar, cvar without tuples,
    /// HVAR and VVAR (a delta for every glyph), MVAR (one record), STAT (one axis).
    /// </summary>
    private static (string Tag, byte[] Data)[] Fg1159VariationTables(int glyphCount) =>
    [
        ("fvar", Ot27Fvar()), ("gvar", Ot27Gvar(glyphCount, new Dictionary<int, byte[]>())), ("avar", Ot05Avar((-1, -1), (0, 0), (1, 1))),
        ("cvar", [0, 1, 0, 0, 0, 0, 0, 8]), ("HVAR", Ot27Hvar(glyphCount, false)), ("VVAR", Ot27Hvar(glyphCount, true)),
        ("MVAR", Ot32Mvar("hasc")), ("STAT", Ot18Stat()),
    ];

    private static readonly string[] Fg1159VariationTags = ["fvar", "gvar", "avar", "cvar", "HVAR", "VVAR", "MVAR", "STAT"];

    // ------------------------------------------------------------ question 2: the tables of the variations

    /// <summary>
    /// OT 41.2 sentences 22–34, OT 41.9 sentences 1–5, OT 44 sentence 10, OT 53 sentences 17–18, OT 59.1 sentences 1–3 (writer): PDF-Knead writes
    /// the static default instance — Liberation Sans with fvar, gvar, avar, cvar, HVAR, VVAR, MVAR and STAT: the subset, the subset keeping all
    /// other tables (compression), the scrubbed font (glyf outlines) and the PDF/A rebuild have none of the eight, and their outlines are those
    /// of glyf (before, compression and scrubbing kept them all, with the deltas of the glyphs they emptied).
    /// </summary>
    [Fact]
    public void VariationTables_LeftOutBySubsetCompressionScrubAndRebuild()
    {
        var plain = Ot27Sans.Value;
        var font = Ot27SansWith([.. Fg1159VariationTables(plain.GlyphCount).Select(t => (t.Tag, (byte[]?)t.Data))]);
        var a = Ot27Glyph('A');
        var written = new Dictionary<string, byte[]>
        {
            ["Teilschrift"] = TrueTypeSubsetter.Subset(font, [a]),
            ["Verkleinern"] = TrueTypeSubsetter.Subset(font, [a], keepAllTables: true),
            ["Bereinigen"] = TrueTypeSubsetter.Scrub(font, [a]),
            ["Neubau"] = TrueTypeSubsetter.Rebuild(font, new Dictionary<int, int> { [a] = 1366 }, [], null),
        };
        foreach (var (writer, file) in written)
        {
            var result = OpenTypeFont.Parse(file);
            foreach (var tag in Fg1159VariationTags) Assert.False(result.HasTable(tag), $"{writer}: {tag}");
            Assert.True(Ot27SameOutline(plain.Outline(a), result.Outline(a)), writer);
        }
        // what the compression keeps besides: the layout of the font
        Assert.True(OpenTypeFont.Parse(written["Verkleinern"]).HasTable("GSUB"));
    }

    /// <summary>
    /// OT 41.2 sentences 22–34 and OT 42.1 sentences 18–26 (writer): scrubbing an OpenType font with „CFF “ outlines leaves the eight tables of
    /// the variations out as well — the Noto font with fvar, gvar, avar, cvar, HVAR, VVAR, MVAR and STAT added, scrubbed to „漢字A“, has none of
    /// them (before, MVAR, STAT and the others stayed in this branch).
    /// </summary>
    [Fact]
    public void VariationTables_LeftOutWhenScrubbingCffOutlines()
    {
        var source = OpenTypeFont.Parse(Ot34Noto.Value);
        var font = ColorFontBuilder.WithTables(Ot34Noto.Value, Fg1159VariationTables(source.GlyphCount).ToDictionary(t => t.Tag, t => (byte[]?)t.Data));
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, Ot34Kept.Select(c => source.GlyphForUnicode(c))));
        Assert.True(scrubbed.HasTable("CFF "));
        foreach (var tag in Fg1159VariationTags) Assert.False(scrubbed.HasTable(tag), tag);
    }

    /// <summary>
    /// OT 1 (table overview, the tables of other systems; finding S9 d): scrubbing leaves out the tables that hold data per glyph for other
    /// systems — bitmaps (bdat, bloc, like EBDT and EBLC) and Graphite rules and glyph attributes (Silf, Glat, Gloc): Liberation Sans with all
    /// five, scrubbed to „A“, has none of them.
    /// </summary>
    [Fact]
    public void Scrub_LeavesOutBitmapAndGraphiteTables()
    {
        string[] tags = ["bdat", "bloc", "Silf", "Glat", "Gloc"];
        var font = Ot27SansWith([.. tags.Select(t => (t, (byte[]?)new byte[] { 0, 2, 0, 0, 0, 0, 0, 1 }))]);
        Assert.All(tags, t => Assert.True(OpenTypeFont.Parse(font).HasTable(t), t));
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, [Ot27Glyph('A')]));
        Assert.All(tags, t => Assert.False(scrubbed.HasTable(t), t));
    }

    /// <summary>
    /// OT 56 sentences 1–3, OT 56.3 sentences 1–2 and OT 56.10 sentence 1 (writer): PCLT describes the whole font (pitch = advance of the space,
    /// characterComplement = character sets the font offers) — scrubbing empties glyphs and their advances, so the scrubbed font has no PCLT
    /// (FG 1159; before it stayed byte for byte); compression keeps every advance and the cmap, and with them PCLT byte for byte; the subset and
    /// the PDF/A rebuild (also with a symbolic (3,0) cmap) carry none.
    /// </summary>
    [Fact]
    public void Ot44_Scrub_LeavesOutPclt()
    {
        var pclt = new byte[54];
        BinaryPrimitives.WriteUInt32BigEndian(pclt, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(pclt.AsSpan(8), (ushort)Ot27Sans.Value.AdvanceWidth(Ot27Glyph(' ')));
        pclt[17] = 0xFF;   // characterComplement (bytes 16–23): ASCII and Latin 1 among others
        var font = Ot27SansWith(("PCLT", pclt));
        var a = Ot27Glyph('A');
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, [a])).HasTable("PCLT"));
        Assert.Equal(pclt, Ot27Table(TrueTypeSubsetter.Subset(font, [a], keepAllTables: true), "PCLT"));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(font, [a])).HasTable("PCLT"));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Rebuild(font, new Dictionary<int, int> { [a] = 1366 }, [], [(0xF041, a)])).HasTable("PCLT"));
    }

    // ------------------------------------------------------------ question 2: named instances of installed fonts

    /// <summary>
    /// OT 16.1.3 sentences 1–15 and 26–28, OT 16.2 sentence 2 (reader, installed fonts): the index of system fonts finds the named instances of
    /// a variable font through fvar — a font folder with one variable font „Muster Var“ (instances „SemiLight Condensed“ and „Bold“ through
    /// their subfamily names, „Black“ with its own PostScript name „MusterVar-Schwarz“): „MusterVar-SemiLightCondensed“, „Muster Var Bold“ and
    /// „MusterVar-Schwarz“ find its file (before only names of the default instance and the family with bold or italic), and the family is
    /// offered once.
    /// </summary>
    [Fact]
    public void Ot18_NamedInstancesOfAVariableFont_InTheSystemFontIndex()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"m7-fg1159-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var name = Ot05Name((3, 1, 0x0409, 1, "Muster Var"), (3, 1, 0x0409, 2, "Regular"), (3, 1, 0x0409, 4, "Muster Var"),
                (3, 1, 0x0409, 6, "MusterVar-Regular"), (3, 1, 0x0409, 258, "SemiLight Condensed"), (3, 1, 0x0409, 259, "Bold"),
                (3, 1, 0x0409, 260, "Black"), (3, 1, 0x0409, 261, "MusterVar-Schwarz"));
            var fvar = Ot18Fvar([("wght", 300, 400, 900, 0, 256), ("wdth", 75, 100, 100, 0, 257)],
                [(258, [350, 87.5], 0xFFFF), (259, [700, 100], 0xFFFF), (260, [900, 100], 261)], postScriptNames: true);
            var file = Path.Combine(dir, "MusterVar.ttf");
            File.WriteAllBytes(file, Ot27SansWith(("name", name), ("fvar", fvar)));
            var build = typeof(SystemFonts).GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Static, [typeof(IEnumerable<string>)]);
            Assert.NotNull(build);
            var (paths, families) = ((Dictionary<string, string>, string[]))build.Invoke(null, [new[] { dir }])!;
            foreach (var instance in (string[])["MusterVar-SemiLightCondensed", "Muster Var Bold", "MusterVar-Schwarz", "MusterVar-Regular"])
                Assert.Equal(file, paths.GetValueOrDefault(SystemFonts.Key(instance)));
            Assert.Equal(["Muster Var"], families);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// OT 16.2 sentence 2 and OT 75.2 sentences 5–6 (installed font, Windows): the named instances of Bahnschrift are found through fvar, drawn
    /// in the default instance (the only one PDF-Knead draws) — „Bahnschrift-SemiLightCondensed“ finds the same font as „Bahnschrift“ (before
    /// none: the name has no bold or italic to strip), and typed text in the family „Bahnschrift SemiBold“ takes the installed font with bold
    /// faked, as the instance has weight 600 and the default instance 400.
    /// </summary>
    [Fact]
    public void Ot18_InstalledVariableFont_NamedInstancesFoundThroughFvar()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Ot18Bahnschrift)) return;
        var plain = SystemFonts.Find("Bahnschrift", false, false);
        Assert.NotNull(plain);
        Assert.Equal(400, plain.WeightClass);
        Assert.Same(plain, SystemFonts.Find("Bahnschrift-SemiLightCondensed", false, false));
        Assert.Same(plain, SystemFonts.Find("Bahnschrift-Bold", false, false));
        var semiBold = RichFonts.For(new RichStyle { Family = "Bahnschrift SemiBold" });
        Assert.True(semiBold.IsSystemFont);
        Assert.True(semiBold.FakeBold);
        Assert.False(semiBold.FakeItalic);
        var regular = RichFonts.For(new RichStyle { Family = "Bahnschrift" });
        Assert.True(regular.IsSystemFont);
        Assert.False(regular.FakeBold);
    }
}
