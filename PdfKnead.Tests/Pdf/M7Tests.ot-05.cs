using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-05 (OpenType specification, recommendations for OpenType fonts and the avar table): the tests the drafts of the
/// sentence-by-sentence comparison asked for — tables kept or dropped by subsets, the font of the text recognition, names, kerning and
/// variable fonts (only their default instance is drawn).
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (ot-05)

    private static string Ot05Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static readonly Lazy<byte[]> Ot05SansFile = new(() => EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"));

    private static readonly Lazy<OpenTypeFont> Ot05Sans = new(() => OpenTypeFont.Parse(Ot05SansFile.Value));

    /// <summary>The Noto Sans CJK collection of the repository (CFF outlines, ten faces).</summary>
    private static readonly Lazy<byte[]> Ot05NotoCollection = new(() => File.ReadAllBytes(Path.Combine(Ot05Repo(), "Schriften", "CJK", "NotoSansCJK-Regular.ttc")));

    /// <summary>
    /// An OpenType font with CFF outlines made of the tables of the Japanese face of Noto Sans CJK (with BASE and VORG, 65 535 glyphs and
    /// 65 532 hmtx records), as FontScrubTests builds it.
    /// </summary>
    private static readonly Lazy<byte[]> Ot05NotoExcerpt = new(() =>
    {
        var noto = OpenTypeFont.Parse(Ot05NotoCollection.Value);
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in (string[])["CFF ", "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "vhea", "vmtx", "BASE", "VORG"])
            if (noto.Table(tag) is { } t) tables[tag] = noto.FileData.AsSpan(t.Offset, t.Length).ToArray();
        return TrueTypeSubsetter.Finish(tables);
    });

    /// <summary>The excerpt scrubbed down to the glyphs of „漢字A“.</summary>
    private static readonly Lazy<byte[]> Ot05NotoScrubbed = new(() =>
    {
        var font = OpenTypeFont.Parse(Ot05NotoExcerpt.Value);
        return TrueTypeSubsetter.Scrub(Ot05NotoExcerpt.Value, "漢字A".Select(c => font.GlyphForUnicode(c)));
    });

    private static int Ot05U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data[at..]);

    private static uint Ot05U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data[at..]);

    /// <summary>Table directory of a font file: tag → offset and length.</summary>
    private static Dictionary<string, (int Offset, int Length)> Ot05Directory(byte[] font)
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        for (var i = 0; i < Ot05U16(font, 4); i++)
        {
            var at = 12 + 16 * i;
            result[Encoding.ASCII.GetString(font, at, 4)] = ((int)Ot05U32(font, at + 8), (int)Ot05U32(font, at + 12));
        }
        return result;
    }

    private static byte[] Ot05Table(byte[] font, string tag) => Ot05Directory(font) is var d && d.TryGetValue(tag, out var t)
        ? font.AsSpan(t.Offset, t.Length).ToArray() : [];

    /// <summary>A name table (format 0) of the records given, in the order given; text of platform 1 in Latin-1, otherwise UTF-16BE.</summary>
    private static byte[] Ot05Name(params (int Platform, int Encoding, int Language, int Id, string Text)[] records)
    {
        var strings = new List<byte>();
        var table = new List<byte>();
        void U16(int v) => table.AddRange([(byte)(v >> 8), (byte)v]);
        U16(0);
        U16(records.Length);
        U16(6 + 12 * records.Length);
        foreach (var (platform, encoding, language, id, text) in records)
        {
            var bytes = platform == 1 ? Encoding.Latin1.GetBytes(text) : Encoding.BigEndianUnicode.GetBytes(text);
            U16(platform);
            U16(encoding);
            U16(language);
            U16(id);
            U16(bytes.Length);
            U16(strings.Count);
            strings.AddRange(bytes);
        }
        table.AddRange(strings);
        return [.. table];
    }

    /// <summary>Liberation Sans with tables added, replaced or (null) removed.</summary>
    private static byte[] Ot05SansWith(params (string Tag, byte[]? Data)[] tables) =>
        ColorFontBuilder.WithTables(Ot05SansFile.Value, tables.ToDictionary(t => t.Tag, t => t.Data));

    /// <summary>The head table of Liberation Sans with other flags.</summary>
    private static byte[] Ot05HeadWithFlags(Func<int, int> flags)
    {
        var head = Ot05Table(Ot05SansFile.Value, "head");
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(16), (ushort)flags(Ot05U16(head, 16)));
        return head;
    }

    /// <summary>The glyph indices of a few letters of Liberation Sans.</summary>
    private static int[] Ot05Glyphs(string text) => [.. text.Select(c => Ot05Sans.Value.GlyphForUnicode(c))];

    /// <summary>The variation tables of a variable font with one axis „wght“ 100–900, default 400, and no instances.</summary>
    private static (byte[] Fvar, byte[] Gvar) Ot05VariationTables(int glyphCount)
    {
        var fvar = new byte[16 + 20];
        BinaryPrimitives.WriteUInt16BigEndian(fvar, 1);                  // version 1.0
        BinaryPrimitives.WriteUInt16BigEndian(fvar.AsSpan(4), 16);       // axes at 16
        BinaryPrimitives.WriteUInt16BigEndian(fvar.AsSpan(6), 2);        // reserved
        BinaryPrimitives.WriteUInt16BigEndian(fvar.AsSpan(8), 1);        // one axis
        BinaryPrimitives.WriteUInt16BigEndian(fvar.AsSpan(10), 20);      // axis record size
        BinaryPrimitives.WriteUInt16BigEndian(fvar.AsSpan(12), 0);       // no instances
        BinaryPrimitives.WriteUInt16BigEndian(fvar.AsSpan(14), 8);       // instance size
        "wght"u8.CopyTo(fvar.AsSpan(16));
        BinaryPrimitives.WriteInt32BigEndian(fvar.AsSpan(20), 100 << 16);
        BinaryPrimitives.WriteInt32BigEndian(fvar.AsSpan(24), 400 << 16);
        BinaryPrimitives.WriteInt32BigEndian(fvar.AsSpan(28), 900 << 16);
        BinaryPrimitives.WriteUInt16BigEndian(fvar.AsSpan(34), 256);     // axis name ID
        // gvar without variation data: every glyph's offset is 0
        var gvar = new byte[20 + (glyphCount + 1) * 2];
        BinaryPrimitives.WriteUInt16BigEndian(gvar, 1);
        BinaryPrimitives.WriteUInt16BigEndian(gvar.AsSpan(4), 1);        // one axis
        BinaryPrimitives.WriteUInt32BigEndian(gvar.AsSpan(8), 20);       // shared tuples (none)
        BinaryPrimitives.WriteUInt16BigEndian(gvar.AsSpan(12), (ushort)glyphCount);
        BinaryPrimitives.WriteUInt32BigEndian(gvar.AsSpan(16), (uint)gvar.Length);
        return (fvar, gvar);
    }

    /// <summary>An avar table for one axis with the maps given (F2DOT14 pairs from → to).</summary>
    private static byte[] Ot05Avar(params (double From, double To)[] maps)
    {
        var avar = new byte[8 + 2 + 4 * maps.Length];
        BinaryPrimitives.WriteUInt16BigEndian(avar, 1);
        BinaryPrimitives.WriteUInt16BigEndian(avar.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt16BigEndian(avar.AsSpan(8), (ushort)maps.Length);
        for (var i = 0; i < maps.Length; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(avar.AsSpan(10 + 4 * i), (short)Math.Round(maps[i].From * 16384));
            BinaryPrimitives.WriteInt16BigEndian(avar.AsSpan(12 + 4 * i), (short)Math.Round(maps[i].To * 16384));
        }
        return avar;
    }

    private static bool Ot05SameOutline(GlyphOutline a, GlyphOutline b) => a.Verbs.SequenceEqual(b.Verbs) && a.Points.SequenceEqual(b.Points);

    /// <summary>
    /// Liberation Sans with the instructions of one simple glyph replaced (glyf rewritten with long loca, every glyph aligned to 4 bytes).
    /// </summary>
    private static byte[] Ot05WithInstructions(int glyph, byte[] instructions)
    {
        var font = Ot05SansFile.Value;
        var head = Ot05Table(font, "head");
        var loca = Ot05Table(font, "loca");
        var glyf = Ot05Table(font, "glyf");
        var longLoca = BinaryPrimitives.ReadInt16BigEndian(head.AsSpan(50)) != 0;
        var count = Ot05Sans.Value.GlyphCount;
        int Offset(int g) => longLoca ? (int)Ot05U32(loca, g * 4) : Ot05U16(loca, g * 2) * 2;
        var newGlyf = new List<byte>();
        var newLoca = new byte[(count + 1) * 4];
        for (var g = 0; g < count; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(g * 4), (uint)newGlyf.Count);
            var data = glyf.AsSpan(Offset(g), Offset(g + 1) - Offset(g));
            if (g == glyph)
            {
                var contours = BinaryPrimitives.ReadInt16BigEndian(data);
                Assert.True(contours > 0, "keine einfache Glyphe");
                var at = 10 + 2 * contours;
                var old = Ot05U16(data, at);
                newGlyf.AddRange(data[..at].ToArray());
                newGlyf.AddRange([(byte)(instructions.Length >> 8), (byte)instructions.Length]);
                newGlyf.AddRange(instructions);
                newGlyf.AddRange(data[(at + 2 + old)..].ToArray());
            }
            else newGlyf.AddRange(data.ToArray());
            while (newGlyf.Count % 4 != 0) newGlyf.Add(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(count * 4), (uint)newGlyf.Count);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        return Ot05SansWith(("glyf", [.. newGlyf]), ("loca", newLoca), ("head", head));
    }

    /// <summary>A kern table (version 0) of the subtables given, each with its coverage and body.</summary>
    private static byte[] Ot05Kern(params (int Coverage, byte[] Body)[] subtables)
    {
        var kern = new List<byte> { 0, 0, 0, (byte)subtables.Length };
        foreach (var (coverage, body) in subtables)
        {
            var length = 6 + body.Length;
            kern.AddRange([0, 0, (byte)(length >> 8), (byte)length, (byte)(coverage >> 8), (byte)coverage]);
            kern.AddRange(body);
        }
        return [.. kern];
    }

    /// <summary>The body of a format 0 kern subtable with one pair.</summary>
    private static byte[] Ot05KernPair(int left, int right, int value)
    {
        var body = new byte[8 + 6];
        BinaryPrimitives.WriteUInt16BigEndian(body, 1);                  // one pair
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2), 6);        // search range
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(8), (ushort)left);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(10), (ushort)right);
        BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(12), (short)value);
        return body;
    }

    /// <summary>The keys, family name and usability <c>SystemFonts</c> reads from a font file (its private method, by reflection).</summary>
    private static (List<string> Keys, string? Family, bool Usable) Ot05SystemFontNames(byte[] font)
    {
        var file = Path.Combine(Path.GetTempPath(), $"m7-ot05-{Guid.NewGuid():N}.ttf");
        File.WriteAllBytes(file, font);
        try
        {
            var names = typeof(SystemFonts).GetMethod("Names", BindingFlags.NonPublic | BindingFlags.Static)!;
            return ((List<string>, string?, bool))names.Invoke(null, [file])!;
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static readonly Lazy<OpenTypeFont> Ot05Glyphless = new(() => OpenTypeFont.Parse(OcrTextLayer.Glyphless));

    private static byte[] Ot05GlyphlessTable(string tag) => Ot05Table(OcrTextLayer.Glyphless, tag);

    // ------------------------------------------------------------ 3.3 table alignment and checksums

    /// <summary>
    /// OT 3.3 sentences 1–4 (writer): tables begin on multiples of four bytes and are padded with zeros, and the checksum adjustment makes the
    /// sum of the whole file 0xB1B0AFBA — tables of 5, 3 and 54 bytes assembled.
    /// </summary>
    [Fact]
    public void AssembledFont_TablesAlignedToFourBytes_PaddedWithZeros_FileChecksumMatches()
    {
        var file = TrueTypeSubsetter.Assemble(new Dictionary<string, byte[]>
        {
            ["aaaa"] = [1, 2, 3, 4, 5], ["bbbb"] = [6, 7, 8], ["head"] = new byte[54],
        });
        foreach (var (_, (offset, length)) in Ot05Directory(file))
        {
            Assert.Equal(0, offset % 4);
            for (var k = offset + length; k < ((offset + length + 3) & ~3); k++) Assert.Equal(0, file[k]);
        }
        Assert.Equal(0, file.Length % 4);
        uint sum = 0;
        for (var i = 0; i < file.Length; i += 4) sum += Ot05U32(file, i);
        Assert.Equal(0xB1B0AFBAu, sum);
    }

    /// <summary>
    /// OT 3.3 sentence 5 (writer and reader): a checksum over a table whose length is no multiple of four counts the missing bytes as zeros
    /// (01 02 03 04 05 gives 0x06020304); a font whose table checksum is wrong is read as with the right one (checksums are not checked).
    /// </summary>
    [Fact]
    public void TableChecksum_ShortLastWordCountsAsZeros_WrongChecksumIsTolerated()
    {
        var file = TrueTypeSubsetter.Assemble(new Dictionary<string, byte[]> { ["aaaa"] = [1, 2, 3, 4, 5], ["head"] = new byte[54] });
        Assert.Equal(0x06020304u, Ot05U32(file, 12 + 4));   // the record of „aaaa“ comes first
        var wrong = (byte[])Ot05SansFile.Value.Clone();
        for (var i = 0; i < Ot05U16(wrong, 4); i++)
            if (Encoding.ASCII.GetString(wrong, 12 + 16 * i, 4) == "glyf") wrong[12 + 16 * i + 4] ^= 0xFF;
        var font = OpenTypeFont.Parse(wrong);
        Assert.True(font.Intact);
        var a = Ot05Sans.Value.GlyphForUnicode('A');
        Assert.True(Ot05SameOutline(Ot05Sans.Value.Outline(a), font.Outline(a)));
    }

    // ------------------------------------------------------------ 3.5 BASE

    /// <summary>
    /// OT 3.5 sentences 1–6 (writer): BASE is neither read nor written — a subset of Liberation Sans with a BASE table and the scrubbed
    /// excerpt of Noto Sans CJK (with BASE) have no BASE.
    /// </summary>
    [Fact]
    public void SubsetAndScrub_DropTheBaseTable()
    {
        byte[] baseTable = [0, 1, 0, 0, 0, 0, 0, 0];
        var sans = Ot05SansWith(("BASE", baseTable));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(sans, Ot05Glyphs("AB"))).HasTable("BASE"));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(sans, Ot05Glyphs("AB"))).HasTable("BASE"));
        Assert.True(OpenTypeFont.Parse(Ot05NotoExcerpt.Value).HasTable("BASE"));
        Assert.False(OpenTypeFont.Parse(Ot05NotoScrubbed.Value).HasTable("BASE"));
    }

    // ------------------------------------------------------------ 3.6 last resort font

    /// <summary>
    /// OT 3.6 sentences 8–9 (reader): a cmap (0,6) of format 13 maps a whole group to one glyph — the group 0x0000–0xFFFF to glyph 5.
    /// </summary>
    [Fact]
    public void Cmap_Format13LastResort_MapsTheGroupToOneGlyph()
    {
        var font = OpenTypeFont.Parse(Ot05SansWith(("cmap", Ot05LastResortCmap())));
        Assert.Equal(5, font.Lookup(0, 6, 0x1234));
        Assert.Equal(5, font.Lookup(0, 6, 0x0041));
        Assert.Equal(5, font.GlyphForUnicode(0x4E00));
    }

    /// <summary>
    /// OT 3.6 sentences 8–9 (writer): a last resort font keeps its single (0,6) subtable of format 13 when it is scrubbed — one group, not
    /// one run per code in format 12.
    /// </summary>
    [Fact]
    public void ScrubbedLastResortFont_KeepsFormat13WithOneGroup()
    {
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(Ot05SansWith(("cmap", Ot05LastResortCmap())), [5]));
        Assert.Equal(new (int, int)[] { (0, 6) }, scrubbed.CmapSubtables);
        var at = scrubbed.CmapOffset(0, 6);
        Assert.Equal(13, Ot05U16(scrubbed.FileData, at));
        Assert.Equal(1u, Ot05U32(scrubbed.FileData, at + 12));
        Assert.Equal(5, scrubbed.Lookup(0, 6, 0x1234));
    }

    /// <summary>A cmap with one (0,6) subtable of format 13: the group 0x0000–0xFFFF to glyph 5.</summary>
    private static byte[] Ot05LastResortCmap()
    {
        var cmap = new byte[12 + 16 + 12];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 6);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), 12);
        var f = cmap.AsSpan(12);
        BinaryPrimitives.WriteUInt16BigEndian(f, 13);
        BinaryPrimitives.WriteUInt32BigEndian(f[4..], 28);
        BinaryPrimitives.WriteUInt32BigEndian(f[12..], 1);
        BinaryPrimitives.WriteUInt32BigEndian(f[16..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(f[20..], 0xFFFF);
        BinaryPrimitives.WriteUInt32BigEndian(f[24..], 5);
        return cmap;
    }

    // ------------------------------------------------------------ 3.7, 3.8, 3.22 cvt, fpgm, prep

    /// <summary>
    /// OT 3.7 sentence 1, 3.8 sentence 1 and 3.22 sentence 1 (writer): „cvt “, fpgm and prep only where instructions need them — the font of
    /// the text recognition has no instructions and none of these tables; a subset of Liberation Sans keeps them byte for byte as the source.
    /// </summary>
    [Fact]
    public void GlyphlessFontHasNoInstructionTables_SubsetKeepsThemAsTheSource()
    {
        var subset = TrueTypeSubsetter.Subset(Ot05SansFile.Value, Ot05Glyphs("AB"));
        foreach (var tag in (string[])["cvt ", "fpgm", "prep"])
        {
            Assert.False(Ot05Glyphless.Value.HasTable(tag), tag);
            var source = Ot05Table(Ot05SansFile.Value, tag);
            Assert.NotEmpty(source);
            Assert.Equal(source, Ot05Table(subset, tag));
        }
    }

    // ------------------------------------------------------------ 3.9, 3.33, 4.1, 4.2 variable fonts

    /// <summary>
    /// OT 3.9 sentences 1–2, 3.33 sentences 1–7 and 4.2 sentences 1–10 (writer): variations are not read — a subset of a variable font (fvar,
    /// gvar, avar) has none of these tables and its outlines are those of the default instance; kept with all tables (compressing), avar stays
    /// byte for byte.
    /// </summary>
    [Fact]
    public void SubsetOfVariableFont_DropsVariationTables_KeepAllTablesKeepsAvar()
    {
        var (fvar, gvar) = Ot05VariationTables(Ot05Sans.Value.GlyphCount);
        var avar = Ot05Avar((-1, -1), (0, 0), (0.5, 0.75), (1, 1));
        var variable = Ot05SansWith(("fvar", fvar), ("gvar", gvar), ("avar", avar));
        var glyphs = Ot05Glyphs("AB");
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(variable, glyphs));
        foreach (var tag in (string[])["fvar", "gvar", "avar"]) Assert.False(subset.HasTable(tag), tag);
        foreach (var g in glyphs) Assert.True(Ot05SameOutline(Ot05Sans.Value.Outline(g), subset.Outline(g)));
        var all = TrueTypeSubsetter.Subset(variable, glyphs, keepAllTables: true);
        Assert.Equal(avar, Ot05Table(all, "avar"));
    }

    /// <summary>
    /// OT 4.1 sentences 1–6 and 4.2 sentences 21–29 (reader): no coordinates are normalised and avar is not read — a variable font (wght
    /// 100–900, default 400) draws its default instance, also with an avar whose map lacks 0 → 0.
    /// </summary>
    [Fact]
    public void VariableFont_DrawsTheDefaultInstance_WhateverAvarSays()
    {
        var (fvar, gvar) = Ot05VariationTables(Ot05Sans.Value.GlyphCount);
        var proper = OpenTypeFont.Parse(Ot05SansWith(("fvar", fvar), ("gvar", gvar), ("avar", Ot05Avar((-1, -1), (0, 0), (1, 1)))));
        var without = OpenTypeFont.Parse(Ot05SansWith(("fvar", fvar), ("gvar", gvar), ("avar", Ot05Avar((-1, -1), (0, 0.5), (1, 1)))));
        foreach (var g in Ot05Glyphs("AHg"))
        {
            Assert.True(Ot05SameOutline(Ot05Sans.Value.Outline(g), proper.Outline(g)));
            Assert.True(Ot05SameOutline(Ot05Sans.Value.Outline(g), without.Outline(g)));
        }
    }

    // ------------------------------------------------------------ 3.10 DSIG, 3.11 hdmx, 3.17 LTSH, 3.23 VDMX

    /// <summary>OT 3.10 sentences 1–3 (writer): a signature would no longer match — a subset with all tables kept has no DSIG.</summary>
    [Fact]
    public void SubsetKeepingAllTables_DropsTheSignature()
    {
        var signed = Ot05SansWith(("DSIG", [0, 0, 0, 1, 0, 0, 0, 0]));
        Assert.True(OpenTypeFont.Parse(signed).HasTable("DSIG"));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(signed, Ot05Glyphs("AB"), keepAllTables: true)).HasTable("DSIG"));
    }

    /// <summary>
    /// OT 3.11 sentences 1–12 (writer): device widths are not needed without hinting — a font with hdmx whose head flags have neither bit 2 nor
    /// bit 4: subset and scrubbed without hdmx.
    /// </summary>
    [Fact]
    public void SubsetAndScrub_DropHdmx()
    {
        var font = Ot05SansWith(("hdmx", [0, 0, 0, 0, 0, 0, 0, 0]), ("head", Ot05HeadWithFlags(f => f & ~0x14)));
        Assert.True(OpenTypeFont.Parse(font).HasTable("hdmx"));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(font, Ot05Glyphs("AB"))).HasTable("hdmx"));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, Ot05Glyphs("AB"))).HasTable("hdmx"));
    }

    /// <summary>
    /// OT 3.17 sentences 1–2 (writer, today's behaviour): a font with LTSH and head flag bit 2 — the subset has no LTSH, and the flag stays
    /// as in the source.
    /// </summary>
    [Fact]
    public void Subset_DropsLtsh_HeadFlagUnchanged()
    {
        var count = Ot05Sans.Value.GlyphCount;
        var ltsh = new byte[4 + count];
        BinaryPrimitives.WriteUInt16BigEndian(ltsh.AsSpan(2), (ushort)count);
        Array.Fill(ltsh, (byte)1, 4, count);
        var font = Ot05SansWith(("LTSH", ltsh), ("head", Ot05HeadWithFlags(f => f | 0x04)));
        var subset = TrueTypeSubsetter.Subset(font, Ot05Glyphs("AB"));
        Assert.False(OpenTypeFont.Parse(subset).HasTable("LTSH"));
        Assert.Equal(Ot05U16(Ot05Table(font, "head"), 16), Ot05U16(Ot05Table(subset, "head"), 16));
        Assert.NotEqual(0, Ot05U16(Ot05Table(subset, "head"), 16) & 0x04);
    }

    /// <summary>OT 3.23 sentences 1–4 (writer): VDMX is not read — a font with VDMX: subset and scrubbed without it.</summary>
    [Fact]
    public void SubsetAndScrub_DropVdmx()
    {
        var font = Ot05SansWith(("VDMX", [0, 0, 0, 0, 0, 0]));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(font, Ot05Glyphs("AB"))).HasTable("VDMX"));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, Ot05Glyphs("AB"))).HasTable("VDMX"));
    }

    // ------------------------------------------------------------ 3.12 head of the text recognition font

    /// <summary>OT 3.12 sentences 1–2 (writer): fontRevision is a Fixed 16.16 — the font of the text recognition has 1.0 (00 01 00 00).</summary>
    [Fact]
    public void GlyphlessFont_FontRevisionIsFixedOne()
    {
        Assert.Equal(new byte[] { 0, 1, 0, 0 }, Ot05GlyphlessTable("head")[4..8]);
    }

    /// <summary>OT 3.12 sentence 4 (writer): the font of the text recognition is not compressed — head flag bit 13 is clear.</summary>
    [Fact]
    public void GlyphlessFont_HeadFlagBit13Clear()
    {
        Assert.Equal(0, Ot05U16(Ot05GlyphlessTable("head"), 16) & (1 << 13));
    }

    // ------------------------------------------------------------ 3.14 hmtx in CFF fonts

    /// <summary>
    /// OT 3.14 sentence 3 (reader and writer): Noto Sans CJK has fewer hmtx records (65 532) than glyphs (65 535) — the last glyphs take the
    /// width of the last record, and scrubbing keeps numberOfHMetrics of the source.
    /// </summary>
    [Fact]
    public void CffFontWithFewerHmtxRecords_LastWidthRepeats_ScrubKeepsTheCount()
    {
        var font = OpenTypeFont.Parse(Ot05NotoExcerpt.Value);
        var hhea = Ot05Table(Ot05NotoExcerpt.Value, "hhea");
        var hmtx = Ot05Table(Ot05NotoExcerpt.Value, "hmtx");
        var records = Ot05U16(hhea, 34);
        Assert.True(records < font.GlyphCount);
        var last = Ot05U16(hmtx, (records - 1) * 4);
        Assert.Equal(last, font.AdvanceWidth(font.GlyphCount - 1));
        Assert.Equal(records, Ot05U16(Ot05Table(Ot05NotoScrubbed.Value, "hhea"), 34));
    }

    // ------------------------------------------------------------ 3.15 kern

    /// <summary>
    /// OT 3.15 sentences 1–5 (reader): a font without GPOS whose kern table has two subtables of format 0 and one of format 2 — „AV“ is
    /// narrowed by the sum of the two format 0 values; format 2 has no effect.
    /// </summary>
    [Fact]
    public void KernTable_FormatZeroSubtablesAdd_FormatTwoIgnored()
    {
        var a = Ot05Sans.Value.GlyphForUnicode('A');
        var v = Ot05Sans.Value.GlyphForUnicode('V');
        var kern = Ot05Kern((0x0001, Ot05KernPair(a, v, -50)), (0x0201, new byte[8]), (0x0001, Ot05KernPair(a, v, -30)));
        var shaper = TextShaper.For(OpenTypeFont.Parse(Ot05SansWith(("kern", kern), ("GPOS", null), ("GSUB", null))));
        var kerned = shaper.Shape("AV");
        var plain = shaper.Shape("AV", ["-kern"]);
        Assert.Equal(plain[0].XAdvance - 80, kerned[0].XAdvance);
        Assert.Equal(plain[1].XAdvance, kerned[1].XAdvance);
    }

    /// <summary>
    /// OT 3.15 sentence 6 (reader): without kern lookups in GPOS, the kern table comes before the other GPOS features — a font with a kern pair
    /// (A, acute) and a GPOS of only a mark lookup: the acute lands at the base anchor of the kerned A (x 300), as when kerning came first.
    /// </summary>
    [Fact(Skip = "M7-Frage 16 (offen): die kern-Tabelle wirkt nach den GPOS-Marken, die Marke verrutscht um den Unterschneidungswert (TextShaper.Shape)")]
    public void KernTable_AppliedBeforeMarkPositioning()
    {
        var a = Ot05Sans.Value.GlyphForUnicode('A');
        var acute = Ot05Sans.Value.GlyphForUnicode('́');
        Assert.True(acute > 0);
        var gpos = ShaperLimitTests.Gsub(1, [0], t =>
        {
            t.U16(1).U16(4);
            t.U16(4).U16(0).U16(1).U16(8);                               // lookup type 4 (mark to base), one subtable at +8
            t.U16(1).U16(12).U16(18).U16(1).U16(24).U16(36);             // MarkBasePos format 1
            t.U16(1).U16(1).U16(acute);                                   // mark coverage
            t.U16(1).U16(1).U16(a);                                       // base coverage
            t.U16(1).U16(0).U16(6);                                       // mark array: class 0, anchor at +6
            t.U16(1).U16(0).U16(0);                                       // mark anchor (0, 0)
            t.U16(1).U16(4);                                              // base array: one record, its anchor at +4
            t.U16(1).U16(300).U16(700);                                   // base anchor (300, 700)
        }, "mark");
        var kern = Ot05Kern((0x0001, Ot05KernPair(a, acute, -200)));
        var shaper = TextShaper.For(OpenTypeFont.Parse(Ot05SansWith(("kern", kern), ("GPOS", gpos), ("GSUB", null))));
        var shaped = shaper.Shape("Á");
        Assert.Equal(300, shaped[0].XAdvance + shaped[1].XOffset, 3);
        Assert.Equal(700, shaped[1].YOffset, 3);
    }

    /// <summary>
    /// OT 3.15 sentences 7–8 (reader): without GPOS the kern table applies, whatever the script — Liberation Sans without GPOS: „AV“ is
    /// narrower than with „kern“ switched off.
    /// </summary>
    [Fact]
    public void FontWithOnlyAKernTable_KernsAV()
    {
        var shaper = TextShaper.For(OpenTypeFont.Parse(Ot05SansWith(("GPOS", null), ("GSUB", null))));
        Assert.True(shaper.Shape("AV")[0].XAdvance < shaper.Shape("AV", ["-kern"])[0].XAdvance);
    }

    // ------------------------------------------------------------ 3.16 loca

    /// <summary>OT 3.16 sentence 1 (writer): every loca offset written is a multiple of four — subset, scrubbed and rebuilt Liberation Sans.</summary>
    [Fact]
    public void SubsetScrubAndRebuild_LocaOffsetsAligned()
    {
        var glyphs = Ot05Glyphs("AHgé");
        var advances = glyphs.Distinct().ToDictionary(g => g, _ => 1000);
        foreach (var file in new[]
                 {
                     TrueTypeSubsetter.Subset(Ot05SansFile.Value, glyphs), TrueTypeSubsetter.Scrub(Ot05SansFile.Value, glyphs),
                     TrueTypeSubsetter.Rebuild(Ot05SansFile.Value, advances, [(glyphs[0], 600)], null),
                 })
        {
            Assert.True(OpenTypeFont.Parse(file).LongLoca);
            var loca = Ot05Table(file, "loca");
            for (var i = 0; i < loca.Length; i += 4) Assert.Equal(0u, Ot05U32(loca, i) % 4);
        }
    }

    // ------------------------------------------------------------ 3.19 names

    /// <summary>
    /// OT 3.19 sentences 5–7 (reader and writer): a name table with only Macintosh records (1,0) gives the family name; the font of the text
    /// recognition writes its names in US English (0x0409).
    /// </summary>
    [Fact]
    public void MacOnlyNames_GiveTheFamily_GlyphlessNamesInUsEnglish()
    {
        var font = OpenTypeFont.Parse(Ot05SansWith(("name", Ot05Name((1, 0, 0, 1, "Muster"), (1, 0, 0, 6, "Muster-Regular")))));
        Assert.Equal("Muster", font.FamilyName);
        Assert.Equal("Muster-Regular", font.PostScriptName);
        var name = Ot05GlyphlessTable("name");
        var count = Ot05U16(name, 2);
        Assert.True(count > 0);
        for (var i = 0; i < count; i++) Assert.Equal(0x0409, Ot05U16(name, 6 + 12 * i + 4));
    }

    /// <summary>OT 3.19 sentence 8 (writer): name records sorted by platform, encoding, language and name ID — the text recognition font.</summary>
    [Fact]
    public void GlyphlessFont_NameRecordsSorted()
    {
        var name = Ot05GlyphlessTable("name");
        var keys = Enumerable.Range(0, Ot05U16(name, 2))
            .Select(i => ((long)Ot05U16(name, 6 + 12 * i) << 48) | ((long)Ot05U16(name, 8 + 12 * i) << 32) | ((long)Ot05U16(name, 10 + 12 * i) << 16) | (long)Ot05U16(name, 12 + 12 * i))
            .ToList();
        Assert.Equal(keys.Order(), keys);
    }

    /// <summary>
    /// OT 3.19.1 sentences 10–16 (reader): the typographic family name (ID 16) goes before ID 1 — a font with ID 16 „Muster“ and ID 1
    /// „Muster Light“ is offered as the family „Muster“.
    /// </summary>
    [Fact]
    public void SystemFont_TypographicFamilyNameGoesFirst()
    {
        var font = Ot05SansWith(("name", Ot05Name((3, 1, 0x409, 1, "Muster Light"), (3, 1, 0x409, 2, "Regular"), (3, 1, 0x409, 6, "Muster-Light"),
            (3, 1, 0x409, 16, "Muster"), (3, 1, 0x409, 17, "Light"))));
        var (keys, family, _) = Ot05SystemFontNames(font);
        Assert.Equal("Muster", family);
        Assert.Contains("musterlight", keys);
    }

    /// <summary>
    /// OT 3.19.1 sentences 17–24 (writer): name ID 6 in exactly two records, (1,0,0) and (3,1,0x409), with the same text — the font of the text
    /// recognition.
    /// </summary>
    [Fact(Skip = "M7-Befund: die OCR-Textschrift hat Name-ID 6 nur für (3,1,0x409), ohne Mac-Eintrag (1,0,0) (OcrTextLayer.BuildGlyphless)")]
    public void GlyphlessFont_PostScriptNameForMacAndWindows()
    {
        var name = Ot05GlyphlessTable("name");
        var strings = Ot05U16(name, 4);
        var six = new List<(int, int, int, string)>();
        for (var i = 0; i < Ot05U16(name, 2); i++)
        {
            var r = 6 + 12 * i;
            if (Ot05U16(name, r + 6) != 6) continue;
            var bytes = name.AsSpan(strings + Ot05U16(name, r + 10), Ot05U16(name, r + 8));
            var text = Ot05U16(name, r) == 1 ? Encoding.Latin1.GetString(bytes) : Encoding.BigEndianUnicode.GetString(bytes);
            six.Add((Ot05U16(name, r), Ot05U16(name, r + 2), Ot05U16(name, r + 4), text));
        }
        Assert.Equal(2, six.Count);
        Assert.Contains(six, s => s.Item1 == 1 && s.Item2 == 0 && s.Item3 == 0);
        Assert.Contains(six, s => s.Item1 == 3 && s.Item2 == 1 && s.Item3 == 0x409);
        Assert.Single(six.Select(s => s.Item4).Distinct());
    }

    /// <summary>
    /// OT 3.19.1 sentences 25–26 (writer): a PostScript name for /BaseFont is at most 127 characters long (the name is cut to 63) — a font
    /// whose name ID 6 has 200 letters.
    /// </summary>
    [Fact]
    public void PostScriptNameOf200Letters_BaseFontAtMost127()
    {
        var font = Ot05SansWith(("name", Ot05Name((3, 1, 0x409, 6, new string('M', 200)))));
        var face = ElementFace.For("probe-m7-ot05-langer-name", () => OpenTypeFont.Parse(font), false, false, false);
        Assert.InRange(face.BaseName.Length, 1, 127);
        Assert.All(face.BaseName, c => Assert.Equal('M', c));
    }

    /// <summary>
    /// OT 3.19.1 sentences 27–36 (reader): name ID 20 is not read — a font with ID 6 „Muster-Regular“ and ID 20 „Muster-Bold“ has the
    /// PostScript name of ID 6.
    /// </summary>
    [Fact]
    public void NameId20_DoesNotReplaceThePostScriptName()
    {
        var font = OpenTypeFont.Parse(Ot05SansWith(("name", Ot05Name((3, 1, 0x409, 1, "Muster"), (3, 1, 0x409, 6, "Muster-Regular"),
            (3, 1, 0x409, 20, "Muster-Bold")))));
        Assert.Equal("Muster-Regular", font.PostScriptName);
    }

    /// <summary>
    /// OT 3.19.1 sentences 51–57 (reader): post format 4 is not read — Liberation Sans with post format 4 has no glyph names, and its characters
    /// are found through the cmap.
    /// </summary>
    [Fact]
    public void PostFormat4_GivesNoGlyphNames_CharactersThroughTheCmap()
    {
        var count = Ot05Sans.Value.GlyphCount;
        var post = new byte[32 + 2 * count];
        BinaryPrimitives.WriteUInt32BigEndian(post, 0x00040000);
        for (var g = 0; g < count; g++) BinaryPrimitives.WriteUInt16BigEndian(post.AsSpan(32 + 2 * g), (ushort)g);
        var font = OpenTypeFont.Parse(Ot05SansWith(("post", post)));
        var a = Ot05Sans.Value.GlyphForUnicode('A');
        Assert.Null(font.GlyphName(a));
        Assert.Equal(-1, font.GlyphByName("A"));
        Assert.Equal(a, font.GlyphForUnicode('A'));
    }

    // ------------------------------------------------------------ 3.20.2, 3.28 metrics of the text recognition font

    /// <summary>OT 3.20.2 sentences 6–14 (writer): the typographic ascender and descender span one em — the text recognition font.</summary>
    [Fact]
    public void GlyphlessFont_TypoMetricsSpanOneEm()
    {
        var os2 = Ot05GlyphlessTable("OS/2");
        var ascender = BinaryPrimitives.ReadInt16BigEndian(os2.AsSpan(68));
        var descender = BinaryPrimitives.ReadInt16BigEndian(os2.AsSpan(70));
        Assert.Equal(Ot05Glyphless.Value.UnitsPerEm, ascender - descender);
    }

    /// <summary>
    /// OT 3.28 sentences 22–27 (writer): hhea.ascender = usWinAscent = sTypoAscender and hhea.lineGap = sTypoLineGap = 0 — the text
    /// recognition font.
    /// </summary>
    [Fact]
    public void GlyphlessFont_HheaWinAndTypoAscenderAgree_NoLineGap()
    {
        var os2 = Ot05GlyphlessTable("OS/2");
        var hhea = Ot05GlyphlessTable("hhea");
        var typo = BinaryPrimitives.ReadInt16BigEndian(os2.AsSpan(68));
        Assert.Equal(typo, BinaryPrimitives.ReadInt16BigEndian(hhea.AsSpan(4)));
        Assert.Equal(typo, Ot05U16(os2, 74));
        Assert.Equal(0, BinaryPrimitives.ReadInt16BigEndian(hhea.AsSpan(8)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16BigEndian(os2.AsSpan(72)));
    }

    // ------------------------------------------------------------ 3.21 glyph names

    /// <summary>
    /// OT 3.21 sentence 1 (reader): in a font with CFF outlines the glyph names come from the CFF, not from post — a CFF of three glyphs in the
    /// tables of a TrueType font whose post (format 2) names them otherwise.
    /// </summary>
    [Fact]
    public void OpenTypeCff_GlyphNamesFromTheCffNotFromPost()
    {
        var sans = Ot05Sans.Value;
        (string, GlyphOutline, double) Glyph(string name, char c) => (name, sans.Outline(sans.GlyphForUnicode(c)), sans.AdvanceWidth(sans.GlyphForUnicode(c)));
        var cff = CffBuilder.Build("M7Probe", new PdfKnead.Core.Pdf.Graphics.Matrix(1.0 / sans.UnitsPerEm, 0, 0, 1.0 / sans.UnitsPerEm, 0, 0),
            [Glyph("eins", 'A'), Glyph("zwei", 'B'), Glyph("drei", 'C')]);
        var font = OpenTypeFont.Parse(Ot05SansWith(("CFF ", cff), ("glyf", null), ("loca", null)));
        Assert.NotNull(font.Cff);
        Assert.Equal(0x00020000u, Ot05U32(font.TableData("post"), 0));
        Assert.Equal("eins", font.GlyphName(1));
        Assert.Equal("drei", font.GlyphName(3));
        Assert.Equal(2, font.GlyphByName("zwei"));
    }

    /// <summary>OT 3.21 sentences 2–3 (reader): post format 1 gives the 258 Macintosh standard names — glyph 3 is „space“.</summary>
    [Fact]
    public void PostFormat1_GivesTheMacintoshStandardNames()
    {
        var post = Ot05Table(Ot05SansFile.Value, "post")[..32];
        BinaryPrimitives.WriteUInt32BigEndian(post, 0x00010000);
        var font = OpenTypeFont.Parse(Ot05SansWith(("post", post)));
        Assert.Equal(".notdef", font.GlyphName(0));
        Assert.Equal("space", font.GlyphName(3));
        Assert.Equal(3, font.GlyphByName("space"));
    }

    /// <summary>
    /// OT 3.21 sentence 4 (writer, today's behaviour): a rebuilt font has post format 3 (no glyph names, they would name the old glyphs); a
    /// subset with cmap keeps the names of the source.
    /// </summary>
    [Fact]
    public void RebuiltFontHasPostFormat3_SubsetWithCmapKeepsTheNames()
    {
        var a = Ot05Sans.Value.GlyphForUnicode('A');
        var rebuilt = TrueTypeSubsetter.Rebuild(Ot05SansFile.Value, new Dictionary<int, int> { [a] = 1000 }, [], null);
        Assert.Equal(0x00030000u, Ot05U32(Ot05Table(rebuilt, "post"), 0));
        var subset = TrueTypeSubsetter.SubsetWithCmap(Ot05SansFile.Value, [a]);
        Assert.Equal(Ot05Table(Ot05SansFile.Value, "post"), Ot05Table(subset, "post"));
        Assert.Equal("A", OpenTypeFont.Parse(subset).GlyphName(a));
    }

    // ------------------------------------------------------------ 3.25 collections, 3.26 table order

    /// <summary>
    /// OT 3.25 sentences 1–2 (writer): no collections are built — the first font of the Noto Sans CJK collection taken out on its own is an
    /// „OTTO“ file with the tables of that font (without DSIG), each as in the collection.
    /// </summary>
    [Fact]
    public void FirstOfCollection_IsTheFirstFontWithItsTables()
    {
        var collection = Ot05NotoCollection.Value;
        var first = OpenTypeFont.Parse(collection);
        var single = TrueTypeSubsetter.FirstOfCollection(collection);
        Assert.Equal("OTTO"u8.ToArray(), single[..4]);
        var directory = Ot05Directory(single);
        var expected = new[] { "BASE", "CFF ", "GDEF", "GPOS", "GSUB", "OS/2", "VORG", "cmap", "head", "hhea", "hmtx", "maxp", "name", "post", "vhea", "vmtx" }
            .Where(first.HasTable).Where(t => t != "DSIG").Order(StringComparer.Ordinal);
        Assert.Equal(expected, directory.Keys.Order(StringComparer.Ordinal));
        foreach (var (tag, (offset, length)) in directory)
            if (tag != "head") Assert.True(first.TableData(tag).SequenceEqual(single.AsSpan(offset, length)), tag);
    }

    /// <summary>
    /// OT 3.26 sentences 1–4 (writer): the recommended order of the table data for TrueType outlines — in a subset of Liberation Sans head, hhea,
    /// maxp, OS/2, hmtx, cvt, fpgm, prep, loca and glyf follow each other in this order.
    /// </summary>
    [Fact(Skip = "M7-Befund: die Tabellendaten liegen nach Kennung sortiert statt in der empfohlenen Reihenfolge (TrueTypeSubsetter.Assemble)")]
    public void Subset_TableDataInTheRecommendedOrder()
    {
        var directory = Ot05Directory(TrueTypeSubsetter.Subset(Ot05SansFile.Value, Ot05Glyphs("AB")));
        string[] order = ["head", "hhea", "maxp", "OS/2", "hmtx", "cvt ", "fpgm", "prep", "loca", "glyf"];
        var offsets = order.Select(t => directory[t].Offset).ToList();
        Assert.Equal(offsets.Order(), offsets);
    }

    // ------------------------------------------------------------ 3.30 dropout control, 3.31 bitmaps

    /// <summary>
    /// OT 3.30 sentences 1–5 (reader): instructions are never executed — a glyph whose instructions switch dropout control on (SCANCTRL) has the
    /// outline of the same glyph without instructions.
    /// </summary>
    [Fact]
    public void GlyphWithScanControlInstructions_SameOutlineAsWithout()
    {
        var h = Ot05Sans.Value.GlyphForUnicode('H');
        var scanControl = OpenTypeFont.Parse(Ot05WithInstructions(h, [0xB0, 0xFF, 0x85]));   // PUSHB[0] 255, SCANCTRL
        var none = OpenTypeFont.Parse(Ot05WithInstructions(h, []));
        Assert.False(none.Outline(h).IsEmpty);
        Assert.True(Ot05SameOutline(none.Outline(h), scanControl.Outline(h)));
        Assert.True(Ot05SameOutline(Ot05Sans.Value.Outline(h), scanControl.Outline(h)));
    }

    /// <summary>
    /// OT 3.31 sentences 1–11 (reader and writer): embedded bitmaps (EBLC, EBDT, EBSC) are not read — a font with only EBLC/EBDT (without glyf
    /// and loca) is not intact and has no outlines; scrubbed, a font drops EBDT, EBLC and EBSC.
    /// </summary>
    [Fact]
    public void BitmapTables_NotRead_DroppedWhenScrubbing()
    {
        byte[] eblc = [0, 2, 0, 0, 0, 0, 0, 0], ebdt = [0, 2, 0, 0], ebsc = [0, 2, 0, 0, 0, 0, 0, 0];
        var bitmapOnly = OpenTypeFont.Parse(Ot05SansWith(("EBLC", eblc), ("EBDT", ebdt), ("glyf", null), ("loca", null)));
        Assert.False(bitmapOnly.Intact);
        Assert.True(bitmapOnly.Outline(Ot05Sans.Value.GlyphForUnicode('A')).IsEmpty);
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(Ot05SansWith(("EBLC", eblc), ("EBDT", ebdt), ("EBSC", ebsc)), Ot05Glyphs("AB")));
        foreach (var tag in (string[])["EBLC", "EBDT", "EBSC"]) Assert.False(scrubbed.HasTable(tag), tag);
    }

    // ------------------------------------------------------------ 3.34 optical sizes

    /// <summary>
    /// OT 3.34 sentences 1–4 (reader, today's behaviour): fonts are chosen by name, not by optical size — a system font with a STAT table gives
    /// the same keys as without it.
    /// </summary>
    [Fact]
    public void SystemFont_WithStat_ChosenByNameOnly()
    {
        var names = Ot05Name((3, 1, 0x409, 1, "Muster"), (3, 1, 0x409, 2, "Regular"), (3, 1, 0x409, 4, "Muster Regular"), (3, 1, 0x409, 6, "Muster-Regular"));
        byte[] stat = [0, 1, 0, 2, 0, 8, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2];
        var plain = Ot05SystemFontNames(Ot05SansWith(("name", names)));
        var withStat = Ot05SystemFontNames(Ot05SansWith(("name", names), ("STAT", stat)));
        Assert.Equal(plain.Keys, withStat.Keys);
        Assert.Equal(plain.Family, withStat.Family);
    }
}
