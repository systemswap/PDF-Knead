using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-28 (OpenType specification: hmtx, HVAR, kern, loca, maxp): the tests the drafts of the sentence-by-sentence comparison asked
/// for — advances come from hmtx (side bearings, phantom points and HVAR are not read), the legacy kern table, loca and maxp as written by
/// subsets, scrubbing and the font of the text recognition.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (ot-28; the variation tables come from ot-27)

    private static string Ot28Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static readonly Lazy<byte[]> Ot28NotoCollection = new(() => File.ReadAllBytes(Path.Combine(Ot28Repo(), "Schriften", "CJK", "NotoSansCJK-Regular.ttc")));

    /// <summary>An OpenType font with CFF outlines made of the tables of the first face of Noto Sans CJK (maxp 0.5, fewer hmtx records than glyphs).</summary>
    private static readonly Lazy<byte[]> Ot28NotoExcerpt = new(() =>
    {
        var noto = OpenTypeFont.Parse(Ot28NotoCollection.Value);
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in (string[])["CFF ", "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "vhea", "vmtx"])
            if (noto.Table(tag) is { } t) tables[tag] = noto.FileData.AsSpan(t.Offset, t.Length).ToArray();
        return TrueTypeSubsetter.Finish(tables);
    });

    /// <summary>All tables of a single font file by tag.</summary>
    private static Dictionary<string, byte[]> Ot28Tables(byte[] font)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var i = 0; i < Ot27U16(font, 4); i++)
        {
            var tag = System.Text.Encoding.ASCII.GetString(font, 12 + 16 * i, 4);
            result[tag] = Ot27Table(font, tag);
        }
        return result;
    }

    /// <summary>A collection (ttcf 1.0) of the faces given; a table array given to several faces is stored once and shared.</summary>
    private static byte[] Ot28Collection(params Dictionary<string, byte[]>[] faces)
    {
        var shared = new List<byte[]>();
        foreach (var face in faces)
            foreach (var table in face.Values)
                if (!shared.Any(s => ReferenceEquals(s, table))) shared.Add(table);
        var at = 12 + 4 * faces.Length + faces.Sum(f => 12 + 16 * f.Count);
        var offsets = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        foreach (var table in shared)
        {
            offsets[table] = at;
            at += (table.Length + 3) & ~3;
        }
        var result = new byte[at];
        "ttcf"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), (uint)faces.Length);
        var dir = 12 + 4 * faces.Length;
        for (var f = 0; f < faces.Length; f++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12 + 4 * f), (uint)dir);
            var tags = faces[f].Keys.Order(StringComparer.Ordinal).ToList();
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(dir), faces[f].ContainsKey("glyf") ? 0x00010000u : 0x4F54544Fu);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(dir + 4), (ushort)tags.Count);
            for (var i = 0; i < tags.Count; i++)
            {
                var record = dir + 12 + 16 * i;
                var table = faces[f][tags[i]];
                System.Text.Encoding.ASCII.GetBytes(tags[i], result.AsSpan(record, 4));
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(record + 8), (uint)offsets[table]);
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(record + 12), (uint)table.Length);
            }
            dir += 12 + 16 * tags.Count;
        }
        foreach (var table in shared) table.CopyTo(result, offsets[table]);
        return result;
    }

    /// <summary>A kern table (version 0) of the subtables given, each with its coverage and body.</summary>
    private static byte[] Ot28Kern(params (int Coverage, byte[] Body)[] subtables)
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
    private static byte[] Ot28KernPair(int left, int right, int value)
    {
        var body = new byte[8 + 6];
        BinaryPrimitives.WriteUInt16BigEndian(body, 1);                  // one pair
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(2), 6);        // search range
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(8), (ushort)left);
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(10), (ushort)right);
        BinaryPrimitives.WriteInt16BigEndian(body.AsSpan(12), (short)value);
        return body;
    }

    /// <summary>
    /// The body of a format 2 kern subtable (offsets from the start of the subtable, its 6-byte header included): a two-by-two class array,
    /// <paramref name="left"/> in left class 1, <paramref name="right"/> in right class 1, and the value of that cell.
    /// </summary>
    private static byte[] Ot28KernClasses(int left, int right, int value)
    {
        var body = new List<byte>();
        void U16(int v) => body.AddRange([(byte)(v >> 8), (byte)v]);
        U16(4);                                                          // rowWidth: two columns
        U16(14); U16(20); U16(26);                                       // left class table, right class table, kerning array
        U16(left); U16(1); U16(26 + 4);                                  // left class: offset of row 1 from the subtable start
        U16(right); U16(1); U16(2);                                      // right class: column 1
        U16(0); U16(0); U16(0); U16(value);                              // the array, row by row
        return [.. body];
    }

    /// <summary>The shaper of Liberation Sans with only the kern table given (no GSUB, no GPOS).</summary>
    private static TextShaper Ot28KernShaper(byte[] kern) =>
        TextShaper.For(OpenTypeFont.Parse(Ot27SansWith(("kern", kern), ("GPOS", null), ("GSUB", null))));

    /// <summary>How much „AV“ is narrowed at the „A“ (negative: narrower) and the y offset of the „V“.</summary>
    private static (double Kerning, double YOffset) Ot28KernAV(TextShaper shaper)
    {
        var kerned = shaper.Shape("AV");
        var plain = shaper.Shape("AV", ["-kern"]);
        Assert.Equal(plain[1].XAdvance, kerned[1].XAdvance);
        return (kerned[0].XAdvance - plain[0].XAdvance, kerned[1].YOffset);
    }

    /// <summary>The left side bearing of a glyph in an hmtx table (the glyph within the full records).</summary>
    private static int Ot28Lsb(byte[] font, int glyph) => Ot27I16(Ot27Table(font, "hmtx"), glyph * 4 + 2);

    // ------------------------------------------------------------ OT 25 hmtx

    /// <summary>
    /// OT 25 sentences 6–8 and 37–43 (reader): side bearings and phantom points are not used — Liberation Sans with the lsb of „A“ in hmtx set to
    /// 500: the outline (and its bounds) and the advance of „A“ are unchanged.
    /// </summary>
    [Fact]
    public void Ot28_LeftSideBearingChanged_OutlineAndAdvanceUnchanged()
    {
        var a = Ot27Glyph('A');
        var hmtx = Ot27Table(Ot27SansFile.Value, "hmtx");
        BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(a * 4 + 2), 500);
        var font = OpenTypeFont.Parse(Ot27SansWith(("hmtx", hmtx)));
        Assert.Equal(500, Ot28Lsb(font.FileData, a));
        Assert.True(Ot27SameOutline(Ot27Sans.Value.Outline(a), font.Outline(a)));
        Assert.Equal(Ot27Sans.Value.Outline(a).Bounds, font.Outline(a).Bounds);
        Assert.Equal(Ot27Sans.Value.AdvanceWidth(a), font.AdvanceWidth(a));
    }

    /// <summary>
    /// OT 25 sentences 9–12 (reader and writer, today's behaviour; question 2): Liberation Sans with fvar, gvar (deltas for the phantom points of
    /// „A“) and HVAR — advance and outline of „A“ as without these tables; a subset has none of them.
    /// </summary>
    [Fact]
    public void Ot28_PhantomPointDeltas_AdvanceAndOutlineUnchanged_SubsetStatic()
    {
        var plain = Ot27Sans.Value;
        var a = Ot27Glyph('A');
        var points = Ot27PointCount(plain, a);
        var gvar = Ot27Gvar(plain.GlyphCount, new Dictionary<int, byte[]> { [a] = Ot27GlyphDeltas(1, [points, points + 1], 100) });
        var file = Ot27SansWith(("fvar", Ot27Fvar()), ("gvar", gvar), ("HVAR", Ot27Hvar(plain.GlyphCount, false)));
        var font = OpenTypeFont.Parse(file);
        Assert.Equal(plain.AdvanceWidth(a), font.AdvanceWidth(a));
        Assert.True(Ot27SameOutline(plain.Outline(a), font.Outline(a)));
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(file, [a]));
        Assert.False(subset.HasTable("fvar") || subset.HasTable("gvar") || subset.HasTable("HVAR"));
    }

    /// <summary>
    /// OT 25 sentences 13–17 (reader): each font of a collection has its own hmtx — two faces sharing one 'CFF ' table (charstring widths 500)
    /// whose hmtx give glyph 1 the advances 700 and 800: face 0 has 700, face 1 has 800.
    /// </summary>
    [Fact]
    public void Ot28_OpenTypeCffCollection_EachFaceItsOwnHmtx()
    {
        var sans = Ot27Sans.Value;
        var cff = CffBuilder.Build("M7Probe", new Matrix(1.0 / sans.UnitsPerEm, 0, 0, 1.0 / sans.UnitsPerEm, 0, 0),
            [("eins", sans.Outline(Ot27Glyph('A')), 500), ("zwei", sans.Outline(Ot27Glyph('B')), 500)]);
        Dictionary<string, byte[]> Face(int advance)
        {
            var tables = Ot28Tables(Ot27SansFile.Value);
            tables.Remove("glyf");
            tables.Remove("loca");
            tables["CFF "] = cff;
            var hmtx = tables["hmtx"];
            BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(4), (ushort)advance);
            return tables;
        }
        var collection = Ot28Collection(Face(700), Face(800));
        var first = OpenTypeFont.Parse(collection, 0);
        var second = OpenTypeFont.Parse(collection, 1);
        Assert.Equal(first.Table("CFF "), second.Table("CFF "));
        Assert.Equal(500, first.Cff!.AdvanceWidth(1));
        Assert.Equal(700, first.AdvanceWidth(1));
        Assert.Equal(800, second.AdvanceWidth(1));
    }

    /// <summary>
    /// OT 25 sentences 18–20 (writer): scrubbing an OpenType font with CFF outlines (Noto Sans CJK) leaves the hmtx records of the glyphs kept
    /// byte for byte and sets advance and lsb of a glyph not kept to 0.
    /// </summary>
    [Fact]
    public void Ot28_ScrubOpenTypeCff_KeptMetricsStay_RemovedMetricsZero()
    {
        var source = Ot28NotoExcerpt.Value;
        var font = OpenTypeFont.Parse(source);
        var kept = "漢字A".Select(c => font.GlyphForUnicode(c)).ToArray();
        var removed = font.GlyphForUnicode('秘');
        var full = Ot27U16(Ot27Table(source, "hhea"), 34);
        Assert.True(removed > 0 && removed < full - 1);
        var before = Ot27Table(source, "hmtx");
        var after = Ot27Table(TrueTypeSubsetter.Scrub(source, kept), "hmtx");
        foreach (var g in kept) Assert.Equal(before[(g * 4)..(g * 4 + 4)], after[(g * 4)..(g * 4 + 4)]);
        Assert.NotEqual(0, Ot27U16(before, removed * 4));
        Assert.Equal((0, 0), (Ot27U16(after, removed * 4), Ot27I16(after, removed * 4 + 2)));
    }

    /// <summary>
    /// OT 25 sentences 21–24 (reader and writer): glyphs after the last full hmtx record take its advance — AmaticSC (fewer records than glyphs):
    /// the last glyph has the advance of the last record, also after scrubbing the glyph of that record.
    /// </summary>
    [Fact]
    public void Ot28_GlyphsAfterTheLastRecord_TakeItsAdvance_AlsoAfterScrubbingIt()
    {
        var file = EmbeddedFonts.LoadHandwriting("AmaticSC-Regular.ttf");
        var font = OpenTypeFont.Parse(file);
        var full = Ot27U16(Ot27Table(file, "hhea"), 34);
        Assert.True(full < font.GlyphCount, $"{full} Einträge, {font.GlyphCount} Glyphen");
        var last = font.GlyphCount - 1;
        Assert.Equal(font.AdvanceWidth(full - 1), font.AdvanceWidth(last));
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(file, Enumerable.Range(0, font.GlyphCount).Where(g => g != full - 1)));
        Assert.True(scrubbed.Outline(full - 1).IsEmpty || font.Outline(full - 1).IsEmpty);
        Assert.Equal(font.AdvanceWidth(last), scrubbed.AdvanceWidth(last));
    }

    /// <summary>
    /// OT 25 sentences 44–45 (writer): glyphs without contours have lsb 0 — the scrubbed Liberation Sans (only „A“ kept) has lsb 0 for „B“, and
    /// both empty glyphs of the text recognition font have lsb 0.
    /// </summary>
    [Fact]
    public void Ot28_ScrubbedAndGlyphlessFont_EmptyGlyphsHaveLsbZero()
    {
        var b = Ot27Glyph('B');
        Assert.NotEqual(0, Ot28Lsb(Ot27SansFile.Value, b));
        Assert.Equal(0, Ot28Lsb(TrueTypeSubsetter.Scrub(Ot27SansFile.Value, [Ot27Glyph('A')]), b));
        var hmtx = Ot27Table(OcrTextLayer.Glyphless, "hmtx");
        Assert.Equal((0, 0), (Ot27I16(hmtx, 2), Ot27I16(hmtx, 6)));
    }

    /// <summary>
    /// OT 25 sentences 44–45 (writer): glyphs without contours have lsb 0 — a subset of Liberation Sans with only „A“ and the PDF/A rebuild with only
    /// „A“ leave „B“ empty; its lsb should then be 0.
    /// </summary>
    [Fact]
    public void Ot28_SubsetAndRebuild_EmptyGlyphsHaveLsbZero()
    {
        var (a, b) = (Ot27Glyph('A'), Ot27Glyph('B'));
        var subset = TrueTypeSubsetter.Subset(Ot27SansFile.Value, [a]);
        var rebuilt = TrueTypeSubsetter.Rebuild(Ot27SansFile.Value, new Dictionary<int, int> { [a] = 1366 }, [], null);
        Assert.True(OpenTypeFont.Parse(subset).Outline(b).IsEmpty);
        Assert.True(OpenTypeFont.Parse(rebuilt).Outline(b).IsEmpty);
        Assert.Equal(0, Ot28Lsb(subset, b));
        Assert.Equal(0, Ot28Lsb(rebuilt, b));
    }

    // ------------------------------------------------------------ OT 26 HVAR

    /// <summary>
    /// OT 26 sentences 1–8, 10–12 and 13–22, OT 26.2 sentences 1–9 and 10–20, OT 26.3 sentences 1–10 (reader, today's behaviour; question 2): HVAR
    /// is not read — Liberation Sans with fvar (wght 100–900) and an HVAR 1.0 whose store adds 100 to every advance, without mappings (the glyph
    /// is the inner index) and with DeltaSetIndexMaps: the advances are those of hmtx.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ot28_Hvar_IsNotRead_AdvancesFromHmtx(bool mappings)
    {
        var plain = Ot27Sans.Value;
        var hvar = Ot27Hvar(plain.GlyphCount, mappings);
        Assert.Equal((1, 0), (Ot27U16(hvar, 0), Ot27U16(hvar, 2)));
        var font = OpenTypeFont.Parse(Ot27SansWith(("fvar", Ot27Fvar()), ("HVAR", hvar)));
        Assert.True(font.HasTable("HVAR"));
        foreach (var c in "AVg ") Assert.Equal(plain.AdvanceWidth(Ot27Glyph(c)), font.AdvanceWidth(Ot27Glyph(c)));
    }

    /// <summary>
    /// OT 26 sentences 9 and 23–26, OT 26.1 sentences 1–6 (writer, today's behaviour; question 2): a subset of Liberation Sans with fvar, gvar, HVAR
    /// (maps for advance, lsb and rsb) and hdmx has none of them; the scrubbed font keeps HVAR and fvar byte for byte and drops hdmx.
    /// </summary>
    [Fact]
    public void Ot28_SubsetDropsVariationTables_ScrubKeepsHvarAndFvarDropsHdmx()
    {
        var count = Ot27Sans.Value.GlyphCount;
        var (fvar, hvar) = (Ot27Fvar(), Ot27Hvar(count, true));
        var font = Ot27SansWith(("fvar", fvar), ("gvar", Ot27Gvar(count, new Dictionary<int, byte[]>())), ("HVAR", hvar), ("hdmx", new byte[8]));
        var glyphs = new[] { Ot27Glyph('A') };
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(font, glyphs));
        foreach (var tag in (string[])["fvar", "gvar", "HVAR", "hdmx"]) Assert.False(subset.HasTable(tag), tag);
        var scrubbed = TrueTypeSubsetter.Scrub(font, glyphs);
        Assert.Equal(hvar, Ot27Table(scrubbed, "HVAR"));
        Assert.Equal(fvar, Ot27Table(scrubbed, "fvar"));
        Assert.False(OpenTypeFont.Parse(scrubbed).HasTable("hdmx"));
    }

    // ------------------------------------------------------------ OT 27 kern

    /// <summary>
    /// OT 27 sentences 7–8, 10 and 26–27 (reader, today's behaviour; question 16): coverage bit 1 (minimum values) is not looked at — a format 0
    /// subtable with coverage 0x0003 and the pair A/V −200 narrows „AV“ by 200 (as kerning values would).
    /// </summary>
    [Fact]
    public void Ot28_KernMinimumValues_TodayAppliedAsKerning()
    {
        var (a, v) = (Ot27Glyph('A'), Ot27Glyph('V'));
        Assert.Equal((-200.0, 0.0), Ot28KernAV(Ot28KernShaper(Ot28Kern((0x0003, Ot28KernPair(a, v, -200))))));
    }

    /// <summary>
    /// OT 27 sentence 9 (reader): the values of several subtables add — two format 0 subtables with the pair A/V −50 and −30 narrow „AV“ by 80,
    /// in either order.
    /// </summary>
    [Theory]
    [InlineData(-50, -30)]
    [InlineData(-30, -50)]
    public void Ot28_KernSubtables_AddInEitherOrder(int first, int second)
    {
        var (a, v) = (Ot27Glyph('A'), Ot27Glyph('V'));
        var kern = Ot28Kern((0x0001, Ot28KernPair(a, v, first)), (0x0001, Ot28KernPair(a, v, second)));
        Assert.Equal(-80, Ot28KernAV(Ot28KernShaper(kern)).Kerning);
    }

    /// <summary>
    /// OT 27 sentences 28–33 (reader): cross-stream kerning is passed over — a subtable with coverage 0x0005 and the pair A/V 100 leaves the
    /// advances and the y offset unchanged.
    /// </summary>
    [Fact]
    public void Ot28_KernCrossStream_IsPassedOver()
    {
        var (a, v) = (Ot27Glyph('A'), Ot27Glyph('V'));
        Assert.Equal((0.0, 0.0), Ot28KernAV(Ot28KernShaper(Ot28Kern((0x0005, Ot28KernPair(a, v, 100))))));
    }

    /// <summary>
    /// OT 27 sentence 34 (reader, today's behaviour; question 16): the override bit 3 is not looked at — two subtables with A/V −50 and, with
    /// coverage 0x0009, −30: „AV“ is narrowed by 80 (by the rule: 30).
    /// </summary>
    [Fact]
    public void Ot28_KernOverride_TodayAdded()
    {
        var (a, v) = (Ot27Glyph('A'), Ot27Glyph('V'));
        var kern = Ot28Kern((0x0001, Ot28KernPair(a, v, -50)), (0x0009, Ot28KernPair(a, v, -30)));
        Assert.Equal(-80, Ot28KernAV(Ot28KernShaper(kern)).Kerning);
    }

    /// <summary>
    /// OT 27.2 sentences 1–9, 10–22 and 23–26 (reader, today's behaviour; question 16): format 2 is not read — a subtable of format 2 (coverage
    /// 0x0201) with „A“ in left class 1, „V“ in right class 1 and the value −120 leaves „AV“ unchanged.
    /// </summary>
    [Fact]
    public void Ot28_KernFormat2_TodayPassedOver()
    {
        var (a, v) = (Ot27Glyph('A'), Ot27Glyph('V'));
        var body = Ot28KernClasses(a, v, -120);
        Assert.Equal(-120, Ot27I16(body, 32 - 6));   // the cell of the left class of „A“ and the right class of „V“
        Assert.Equal((0.0, 0.0), Ot28KernAV(Ot28KernShaper(Ot28Kern((0x0201, body)))));
    }

    // ------------------------------------------------------------ OT 28 loca

    /// <summary>OT 28 sentence 3 (writer): the loca of a subset ascends — Liberation Sans with „A“ and „Ä“ (a composite): loca[n] ≤ loca[n + 1].</summary>
    [Fact]
    public void Ot28_Subset_LocaAscends()
    {
        var subset = TrueTypeSubsetter.Subset(Ot27SansFile.Value, [Ot27Glyph('A'), Ot27Glyph('Ä')]);
        var loca = Ot27Table(subset, "loca");
        for (var i = 0; i + 8 <= loca.Length; i += 4) Assert.True(Ot27U32(loca, i) <= Ot27U32(loca, i + 4), $"Eintrag {i / 4}");
    }

    // ------------------------------------------------------------ OT 29 maxp

    /// <summary>
    /// OT 29 sentences 1–3 (writer): the text recognition font has maxp 1.0 of 32 bytes; scrubbing an OpenType font with CFF outlines (Noto Sans CJK,
    /// maxp 0.5) keeps maxp 0.5 of 6 bytes.
    /// </summary>
    [Fact]
    public void Ot28_GlyphlessMaxp1Dot0_ScrubbedCffFontKeepsMaxp0Dot5()
    {
        var own = Ot27Table(OcrTextLayer.Glyphless, "maxp");
        Assert.Equal((0x00010000u, 32), (Ot27U32(own, 0), own.Length));
        var source = Ot28NotoExcerpt.Value;
        var font = OpenTypeFont.Parse(source);
        var maxp = Ot27Table(TrueTypeSubsetter.Scrub(source, "漢字A".Select(c => font.GlyphForUnicode(c))), "maxp");
        Assert.Equal((0x00005000u, 6), (Ot27U32(maxp, 0), maxp.Length));
    }

    /// <summary>OT 29 sentences 4–7 (reader): numGlyphs of maxp 0.5 — the first face of Noto Sans CJK has 65 535 glyphs.</summary>
    [Fact]
    public void Ot28_NotoCjk_GlyphCountFromMaxp0Dot5()
    {
        var font = OpenTypeFont.Parse(Ot28NotoCollection.Value);
        Assert.Equal(0x00005000u, Ot27U32(font.TableData("maxp"), 0));
        Assert.Equal(65535, font.GlyphCount);
    }

    /// <summary>
    /// OT 29 sentences 16–22 (writer): the text recognition font has maxZones 2 and maxStackElements and maxSizeOfInstructions 0 (no instructions);
    /// the PDF/A rebuild keeps maxp after numGlyphs byte for byte.
    /// </summary>
    [Fact]
    public void Ot28_GlyphlessMaxpWithoutInstructions_RebuildKeepsTheSourceValues()
    {
        var own = Ot27Table(OcrTextLayer.Glyphless, "maxp");
        Assert.Equal((2, 0, 0), (Ot27U16(own, 14), Ot27U16(own, 24), Ot27U16(own, 26)));
        var rebuilt = TrueTypeSubsetter.Rebuild(Ot27SansFile.Value, new Dictionary<int, int> { [Ot27Glyph('A')] = 1366 }, [], null);
        Assert.Equal(Ot27Table(Ot27SansFile.Value, "maxp")[6..], Ot27Table(rebuilt, "maxp")[6..]);
    }
}
