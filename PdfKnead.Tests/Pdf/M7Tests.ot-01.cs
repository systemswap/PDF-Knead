using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Fonts;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-01 (OpenType: file names, data types, table versions, the table directory and its checksums, collections, the table list,
/// scripts and language systems in short). Fonts are built from Liberation Sans and the Noto CJK collection.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers

    private static byte[] Ot01Liberation => ShaperLimitTests.Liberation;

    private static string Ot01Repo([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static byte[] Ot01Noto() => File.ReadAllBytes(Path.Combine(Ot01Repo(), "Schriften", "CJK", "NotoSansCJK-Regular.ttc"));

    /// <summary>The tables of a font in directory order (tags read as Latin-1, so every byte stays).</summary>
    private static List<(string Tag, byte[] Data)> Ot01Tables(byte[] font, int start = 0)
    {
        var list = new List<(string, byte[])>();
        int n = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(start + 4));
        for (var i = 0; i < n; i++)
        {
            var r = font.AsSpan(start + 12 + i * 16, 16);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(r[8..]);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(r[12..]);
            list.Add((Encoding.Latin1.GetString(r[..4]), font.AsSpan(offset, length).ToArray()));
        }
        return list;
    }

    /// <summary>The records of a table directory as written: tag, checksum, offset, length.</summary>
    private static List<(string Tag, uint Sum, int Offset, int Length)> Ot01Directory(byte[] font, int start = 0)
    {
        var list = new List<(string, uint, int, int)>();
        int n = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(start + 4));
        for (var i = 0; i < n; i++)
        {
            var r = font.AsSpan(start + 12 + i * 16, 16);
            list.Add((Encoding.Latin1.GetString(r[..4]), BinaryPrimitives.ReadUInt32BigEndian(r[4..]),
                (int)BinaryPrimitives.ReadUInt32BigEndian(r[8..]), (int)BinaryPrimitives.ReadUInt32BigEndian(r[12..])));
        }
        return list;
    }

    /// <summary>
    /// An sfnt with the tables in the order given (duplicates allowed, tags as Latin-1), each aligned to four bytes, offsets plus
    /// <paramref name="baseOffset"/> (for a collection); the search fields as computed unless <paramref name="search"/> gives one value for all.
    /// </summary>
    private static byte[] Ot01Sfnt(IReadOnlyList<(string Tag, byte[] Data)> tables, uint version = 0x00010000, int baseOffset = 0, int? search = null)
    {
        var n = tables.Count;
        var log = (int)Math.Log2(n);
        var header = 12 + 16 * n;
        var file = new byte[header + tables.Sum(t => (t.Data.Length + 3) & ~3)];
        BinaryPrimitives.WriteUInt32BigEndian(file, version);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(4), (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(6), (ushort)(search ?? 16 << log));
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(8), (ushort)(search ?? log));
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(10), (ushort)(search ?? 16 * n - (16 << log)));
        var at = header;
        for (var i = 0; i < n; i++)
        {
            var (tag, data) = tables[i];
            var r = file.AsSpan(12 + i * 16);
            Encoding.Latin1.GetBytes(tag, r[..4]);
            BinaryPrimitives.WriteUInt32BigEndian(r[4..], Ot01Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(r[8..], (uint)(at + baseOffset));
            BinaryPrimitives.WriteUInt32BigEndian(r[12..], (uint)data.Length);
            data.CopyTo(file, at);
            at += (data.Length + 3) & ~3;
        }
        return file;
    }

    /// <summary>The checksum of OpenType 1.4.2, written here independently of the code under test.</summary>
    private static uint Ot01Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var k = 0; k < 4; k++) word = (word << 8) | (i + k < data.Length ? data[i + k] : 0u);
            sum += word;
        }
        return sum;
    }

    private static byte[] Ot01With(byte[] font, params (string Tag, byte[]? Data)[] changed) =>
        ColorFontBuilder.WithTables(font, changed.ToDictionary(c => c.Tag, c => c.Data));

    private static byte[] Ot01Table(byte[] font, string tag) => Ot01Tables(font).First(t => t.Tag == tag).Data;

    /// <summary>The glyph data of a TrueType font, one array per glyph.</summary>
    private static byte[][] Ot01Glyphs(byte[] font)
    {
        var tables = Ot01Tables(font).ToDictionary(t => t.Tag, t => t.Data);
        var count = BinaryPrimitives.ReadUInt16BigEndian(tables["maxp"].AsSpan(4));
        var longLoca = BinaryPrimitives.ReadInt16BigEndian(tables["head"].AsSpan(50)) != 0;
        int Offset(int g) => longLoca
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(tables["loca"].AsSpan(g * 4))
            : BinaryPrimitives.ReadUInt16BigEndian(tables["loca"].AsSpan(g * 2)) * 2;
        return [.. Enumerable.Range(0, count).Select(g => tables["glyf"].AsSpan(Offset(g), Offset(g + 1) - Offset(g)).ToArray())];
    }

    /// <summary>The font with new glyph data: glyf and a long loca written anew, head set to long offsets.</summary>
    private static byte[] Ot01WithGlyphs(byte[] font, byte[][] glyphs)
    {
        var glyf = new List<byte>();
        var loca = new byte[(glyphs.Length + 1) * 4];
        for (var g = 0; g < glyphs.Length; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(g * 4), (uint)glyf.Count);
            glyf.AddRange(glyphs[g]);
            while (glyf.Count % 4 != 0) glyf.Add(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(glyphs.Length * 4), (uint)glyf.Count);
        var head = Ot01Table(font, "head");
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        return Ot01With(font, ("glyf", [.. glyf]), ("loca", loca), ("head", head));
    }

    /// <summary>A composite glyph of one component placed at (0, 0) (ARGS_ARE_XY_VALUES, byte arguments), with further flags and scale words.</summary>
    private static byte[] Ot01Composite(int component, int flags, params int[] scale)
    {
        var b = new Ot02Bytes().U16(-1 & 0xFFFF).U16(0).U16(0).U16(0).U16(0).U16(flags | 0x0002).U16(component).U16(0);
        foreach (var s in scale) b.U16(s);
        return b.ToArray();
    }

    /// <summary>
    /// Liberation Sans made variable: fvar with a weight axis 100–900 (default 400), STAT, and gvar moving every point of „A“ 100 units to the
    /// right at the weight 900 — the default instance is the plain glyf.
    /// </summary>
    private static byte[] Ot01Variable(byte[] font)
    {
        var parsed = OpenTypeFont.Parse(font);
        var a = parsed.GlyphForUnicode('A');
        var glyph = Ot01Glyphs(font)[a];
        int contours = BinaryPrimitives.ReadInt16BigEndian(glyph);
        Assert.True(contours > 0);
        var points = BinaryPrimitives.ReadUInt16BigEndian(glyph.AsSpan(10 + (contours - 1) * 2)) + 1 + 4;   // with the four phantom points
        var deltas = new List<byte>();
        for (var left = points; left > 0; left -= 64)
        {
            var run = Math.Min(64, left);
            deltas.Add((byte)(0x40 | (run - 1)));                                  // words: x + 100
            for (var k = 0; k < run; k++) deltas.AddRange([0, 100]);
        }
        for (var left = points; left > 0; left -= 64) deltas.Add((byte)(0x80 | (Math.Min(64, left) - 1)));   // y: zeros
        var data = new Ot02Bytes().U16(1).U16(10).U16(deltas.Count).U16(0x8000).U16(0x4000).ToArray().Concat(deltas).ToList();
        while (data.Count % 4 != 0) data.Add(0);
        var count = parsed.GlyphCount;
        var arrayAt = 20 + (count + 1) * 4;
        var gvar = new Ot02Bytes().U16(1).U16(0).U16(1).U16(0).U32(arrayAt).U16(count).U16(1).U32(arrayAt);
        for (var g = 0; g <= count; g++) gvar.U32(g <= a ? 0 : data.Count);
        var fvar = new Ot02Bytes().U16(1).U16(0).U16(16).U16(2).U16(1).U16(20).U16(0).U16(8)
            .Tag("wght").U32(100 << 16).U32(400 << 16).U32(900 << 16).U16(0).U16(256).ToArray();
        var stat = new Ot02Bytes().U16(1).U16(1).U16(8).U16(1).U32(20).U16(0).U32(0).U16(2).Tag("wght").U16(256).U16(0).ToArray();
        return Ot01With(font, ("fvar", fvar), ("gvar", [.. gvar.ToArray(), .. data]), ("STAT", stat));
    }

    /// <summary>A collection of one font; header 2.0 with or without a signature; signed: DSIG in the font's directory and (2.0) in the header.</summary>
    private static byte[] Ot01Collection(byte[] font, bool version2, bool signed)
    {
        var tables = Ot01Tables(font);
        byte[] dsig = [0, 0, 0, 1, 0, 0, 0, 0];
        if (signed) tables.Add(("DSIG", dsig));
        tables.Sort((x, y) => string.CompareOrdinal(x.Tag, y.Tag));
        var headerLength = version2 ? 28 : 16;
        var sfnt = Ot01Sfnt(tables, BinaryPrimitives.ReadUInt32BigEndian(font), headerLength);
        var header = new Ot02Bytes().Tag("ttcf").U32(version2 ? 0x00020000 : 0x00010000).U32(1).U32(headerLength);
        if (version2 && signed) header.Tag("DSIG").U32(dsig.Length).U32(headerLength + sfnt.Length);
        else if (version2) header.U32(0).U32(0).U32(0);
        return [.. header.ToArray(), .. sfnt, .. version2 && signed ? dsig : []];
    }

    private static byte[] Ot01Patch(byte[] font, string tag, int at, params byte[] bytes)
    {
        var table = Ot01Table(font, tag);
        bytes.CopyTo(table, at);
        return Ot01With(font, (tag, table));
    }

    // ------------------------------------------------------------ OT 1.1, 1.2

    /// <summary>
    /// OT 1.1 sentences 1–2: whether a font file is a collection is told by its header, not by its extension — the reading of the system fonts
    /// (<c>SystemFonts.Names</c>) takes a single font saved as „.TTC“ as a single font (usable for new text) and a collection saved as „.ttf“ as a
    /// collection (not usable).
    /// </summary>
    [Fact]
    public void Ot01_SystemFonts_CollectionIsToldByTheHeader()
    {
        var names = typeof(SystemFonts).GetMethod("Names", BindingFlags.NonPublic | BindingFlags.Static)!;
        var folder = Directory.CreateTempSubdirectory("pdfk-ot01-");
        try
        {
            var single = Path.Combine(folder.FullName, "single.TTC");
            var collection = Path.Combine(folder.FullName, "collection.ttf");
            File.WriteAllBytes(single, Ot01Liberation);
            File.WriteAllBytes(collection, Ot01Collection(Ot01Liberation, false, false));
            var a = (ITuple)names.Invoke(null, [single])!;
            var b = (ITuple)names.Invoke(null, [collection])!;
            Assert.NotEmpty((List<string>)a[0]!);
            Assert.True((bool)a[2]!);
            Assert.Equal((List<string>)a[0]!, (List<string>)b[0]!);
            Assert.False((bool)b[2]!);
        }
        finally
        {
            folder.Delete(true);
        }
    }

    /// <summary>OT 1.2 sentences 1–10: the integer types are big-endian and read at their limits — FF FE as uint16 65534 and int16 −2, 80 as int8 −128, 01 02 03 as uint24 66051, FF FF FF FF as uint32 4294967295 and int32 −1.</summary>
    [Fact]
    public void Ot01_FontReader_IntegerTypesBigEndian()
    {
        Assert.Equal(65534, new FontReader(new byte[] { 0xFF, 0xFE }).U16());
        Assert.Equal(-2, new FontReader(new byte[] { 0xFF, 0xFE }).I16());
        Assert.Equal(-128, new FontReader(new byte[] { 0x80 }).I8());
        Assert.Equal(0x80, new FontReader(new byte[] { 0x80 }).U8());
        Assert.Equal(66051u, new FontReader(new byte[] { 1, 2, 3 }).U24());
        Assert.Equal(4294967295u, new FontReader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }).U32());
        Assert.Equal(-1, new FontReader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }).I32());
    }

    /// <summary>OT 1.2 sentence 11: Fixed is a signed 16.16 number — 00 01 80 00 is 1.5, FF FF 80 00 is −0.5; the own OCR font writes head.fontRevision 1.0 as 00 01 00 00.</summary>
    [Fact]
    public void Ot01_Fixed_Signed1616()
    {
        Assert.Equal(1.5, new FontReader(new byte[] { 0, 1, 0x80, 0 }).Fixed());
        Assert.Equal(-0.5, new FontReader(new byte[] { 0xFF, 0xFF, 0x80, 0 }).Fixed());
        Assert.Equal(new byte[] { 0, 1, 0, 0 }, Ot01Table(OcrTextLayer.Glyphless, "head")[4..8]);
    }

    /// <summary>
    /// OT 1.2 sentences 14 and 24–33: F2DOT14 with the examples of the specification (7FFF 1.999939, 7000 1.75, 0001 0.000061, 0000 0, FFFF
    /// −0.000061, 8000 −2); a composite glyph with the scale 0x2000 draws its component at half the size.
    /// </summary>
    [Fact]
    public void Ot01_F2Dot14_ExamplesAndAScaledComponent()
    {
        (int Bits, double Value)[] examples = [(0x7FFF, 1.999939), (0x7000, 1.75), (0x0001, 0.000061), (0x0000, 0), (0xFFFF, -0.000061), (0x8000, -2)];
        foreach (var (bits, value) in examples)
            Assert.Equal(value, new FontReader(new byte[] { (byte)(bits >> 8), (byte)bits }).F2Dot14(), 6);
        var font = OpenTypeFont.Parse(Ot01Liberation);
        int a = font.GlyphForUnicode('A'), b = font.GlyphForUnicode('B');
        var glyphs = Ot01Glyphs(Ot01Liberation);
        glyphs[b] = Ot01Composite(a, 0x0008, 0x2000);
        var scaled = OpenTypeFont.Parse(Ot01WithGlyphs(Ot01Liberation, glyphs)).Outline(b).Bounds;
        var whole = font.Outline(a).Bounds;
        Assert.Equal(whole.Left / 2, scaled.Left, 3);
        Assert.Equal(whole.Right / 2, scaled.Right, 3);
        Assert.Equal(whole.Top / 2, scaled.Top, 3);
        Assert.Equal(whole.Bottom / 2, scaled.Bottom, 3);
    }

    /// <summary>OT 1.2 sentences 15–16: the LONGDATETIME stamps of head (created, modified; bytes 20–35) are not set anew — a subset keeps those of the source.</summary>
    [Fact]
    public void Ot01_Subset_KeepsTheTimeStampsOfHead()
    {
        var subset = TrueTypeSubsetter.Subset(Ot01Liberation, [36]);
        Assert.Equal(Ot01Table(Ot01Liberation, "head")[20..36], Ot01Table(subset, "head")[20..36]);
    }

    /// <summary>OT 1.2 sentences 17, 34 and 36–39: a tag is compared byte for byte, case counts — a table „Head“ is no „head“.</summary>
    [Fact]
    public void Ot01_TableTags_AreCaseSensitive()
    {
        var tables = Ot01Tables(Ot01Liberation).Select(t => t.Tag == "head" ? ("Head", t.Data) : t).OrderBy(t => t.Item1, StringComparer.Ordinal).ToList();
        var font = OpenTypeFont.Parse(Ot01Sfnt(tables));
        Assert.True(font.HasTable("Head"));
        Assert.False(font.HasTable("head"));
    }

    /// <summary>
    /// OT 1.2 sentences 35 and 40–41: own tags are written as the specification spells them („cvt “ as 63 76 74 20); a foreign tag with the byte
    /// 0x80 is today written with „?“ (0x3F) when a font is subset with all its tables (the bytes outside 0x20–0x7E are not kept — reported).
    /// </summary>
    [Fact]
    public void Ot01_Tags_OwnAsSpelled_ForeignHighByteBecomesQuestionMark()
    {
        var assembled = TrueTypeSubsetter.Assemble(new Dictionary<string, byte[]> { ["cvt "] = [0, 0] });
        Assert.Equal(new byte[] { 0x63, 0x76, 0x74, 0x20 }, assembled[12..16]);
        var tables = Ot01Tables(Ot01Liberation);
        tables.Add(("zz\u0080z", [1, 2, 3, 4]));
        tables.Sort((x, y) => string.CompareOrdinal(x.Tag, y.Tag));
        var subset = TrueTypeSubsetter.Subset(Ot01Sfnt(tables), [36], keepAllTables: true);
        var tags = Ot01Directory(subset).Select(d => d.Tag).ToList();
        Assert.Contains("zz?z", tags);
        Assert.DoesNotContain("zz\u0080z", tags);
    }

    /// <summary>
    /// OT 1.2 sentences 49–52: an offset to an optional table may be NULL and then means „none“ — a GSUB with ScriptList offset 0 has no lookups
    /// for any feature; a GDEF with glyphClassDef 0 gives class 0 to every glyph (the mark anchor of offset 0: ShaperNormTests.MarkWithoutUsableAnchor_StaysWhereItIs).
    /// </summary>
    [Fact]
    public void Ot01_NullOffsets_MeanNone()
    {
        var gsub = Ot02Latin([new("liga", [0])], Ot02Lookup(1, Ot02Single((Ot02G('a'), Ot02G('b')))));
        gsub[4] = gsub[5] = 0;
        Assert.Empty(new LayoutTable(gsub, 7).LookupsFor(["liga"]));
        Assert.Equal(Ot02Gs("a"), Ot02Shape(Ot02Shaper(gsub), "a"));
        var gdef = Ot02Gdef(new Dictionary<int, int> { [Ot02G('m')] = 3 });
        gdef[4] = gdef[5] = 0;
        Assert.Equal(0, new GlyphDefinitions(gdef).GlyphClass(Ot02G('m')));
    }

    /// <summary>OT 1.2 sentence 54: readers reckon with NULL where it is not allowed — a GSUB whose subtables have the coverage offset 0 shapes without an exception and changes nothing.</summary>
    [Fact]
    public void Ot01_CoverageOffsetNull_ShapesWithoutChange()
    {
        int a = Ot02G('a');
        var gsub = Ot02Latin([new("liga", [0, 1])],
            Ot02Lookup(1, b => b.U16(1).U16(0).U16(1)),
            Ot02Lookup(4, b => b.U16(1).U16(0).U16(0)));
        Assert.Equal(Ot02Gs("aa"), Ot02Shape(Ot02Shaper(gsub), "aa"));
    }

    // ------------------------------------------------------------ OT 1.3

    /// <summary>OT 1.3 sentences 1–9: CBLC is read in the major versions 2 and 3 — a CBLC of major version 4 gives no image source.</summary>
    [Fact]
    public void Ot01_CblcOfMajorVersion4_IsNotRead()
    {
        var font = ColorFontBuilder.Build(ColorFontBuilder.Kind.Cbdt, 96);
        Assert.NotNull(CbdtImages.TryCreate(OpenTypeFont.Parse(font)));
        Assert.Null(CbdtImages.TryCreate(OpenTypeFont.Parse(Ot01Patch(font, "CBLC", 0, 0, 4))));
    }

    /// <summary>
    /// OT 1.3 sentences 1–9 and 19–22: the minor version tells the fields of GDEF — 1.2 with a mark glyph set {m} finds m in set 0; the same
    /// bytes as 1.0 (the offset to the sets then lies behind the 1.0 header and is not read) find nothing.
    /// </summary>
    [Fact]
    public void Ot01_MarkGlyphSets_OnlyFromGdef12()
    {
        var m = Ot02G('m');
        Assert.True(new GlyphDefinitions(Ot02Gdef(new Dictionary<int, int> { [m] = 3 }, markSets: [[m]], minor: 2)).InMarkSet(0, m));
        Assert.False(new GlyphDefinitions(Ot02Gdef(new Dictionary<int, int> { [m] = 3 }, markSets: [[m]], minor: 0)).InMarkSet(0, m));
    }

    /// <summary>
    /// OT 1.3 sentences 10–16: Version16Dot16 — a post table of version 2.5 (0x00025000; here over the data of a 2.0 table) is read without
    /// exception (its names: <c>Ot32_PostVersion25_GivesMacintoshNames</c>, FG 1154); the PDF/A rebuild writes post 3.0 (00 03 00 00); maxp 0.5
    /// (0x00005000) with numGlyphs 3 gives three glyphs.
    /// </summary>
    [Fact]
    public void Ot01_Version16Dot16_PostAndMaxp()
    {
        var a = Ot02G('A');
        var post25 = OpenTypeFont.Parse(Ot01Patch(Ot01Liberation, "post", 0, 0, 2, 0x50, 0));
        _ = post25.GlyphName(a);
        Assert.False(post25.Outline(a).IsEmpty);
        var rebuilt = TrueTypeSubsetter.Rebuild(Ot01Liberation, new Dictionary<int, int> { [a] = 1366 }, [], null);
        Assert.Equal(new byte[] { 0, 3, 0, 0 }, Ot01Table(rebuilt, "post")[..4]);
        var maxp05 = OpenTypeFont.Parse(Ot01With(Ot01Liberation, ("maxp", [0, 0, 0x50, 0, 0, 3])));
        Assert.Equal(3, maxp05.GlyphCount);
    }

    /// <summary>OT 1.3 sentence 18 (and 27–28): readers check the major version — a GSUB of major version 2 is not applied („a“ stays).</summary>
    [Fact]
    public void Ot01_GsubOfMajorVersion2_IsNotApplied()
    {
        var gsub = Ot02Layout([new Ot02Script("latn", new Ot02Lang("", [0]))], [new Ot02Feature("liga", [0])],
            [Ot02Lookup(1, Ot02Single((Ot02G('a'), Ot02G('b'))))], major: 2);
        Assert.Equal(Ot02Gs("a"), Ot02Shape(Ot02Shaper(gsub), "a"));
    }

    /// <summary>OT 1.3 sentence 18 (and 27–28): an sbix table of version 2 is not read — the colour font has no colour glyphs then.</summary>
    [Fact]
    public void Ot01_SbixOfVersion2_IsNotRead()
    {
        var font = ColorFontBuilder.Build(ColorFontBuilder.Kind.Sbix, 128);
        Assert.NotNull(ColorGlyphs.For(OpenTypeFont.Parse(font)));
        Assert.Null(ColorGlyphs.For(OpenTypeFont.Parse(Ot01Patch(font, "sbix", 0, 0, 2))));
    }

    /// <summary>OT 1.3 sentence 18 (and 27–28): an SVG table of version 1 is not read — the colour font has no colour glyphs then.</summary>
    [Fact]
    public void Ot01_SvgOfVersion1_IsNotRead()
    {
        var font = ColorFontBuilder.Build(ColorFontBuilder.Kind.Svg, 320);
        Assert.NotNull(ColorGlyphs.For(OpenTypeFont.Parse(font)));
        Assert.Null(ColorGlyphs.For(OpenTypeFont.Parse(Ot01Patch(font, "SVG ", 0, 0, 1))));
    }

    /// <summary>OT 1.3 sentences 23–24: reserved bits are not relied upon — a composite glyph with the reserved flag 0x0010 draws as without it.</summary>
    [Fact]
    public void Ot01_CompositeWithReservedFlag_DrawsTheSame()
    {
        var font = OpenTypeFont.Parse(Ot01Liberation);
        int a = font.GlyphForUnicode('A'), b = font.GlyphForUnicode('B');
        var glyphs = Ot01Glyphs(Ot01Liberation);
        glyphs[b] = Ot01Composite(a, 0);
        var plain = OpenTypeFont.Parse(Ot01WithGlyphs(Ot01Liberation, glyphs)).Outline(b);
        glyphs[b] = Ot01Composite(a, 0x0010);
        var reserved = OpenTypeFont.Parse(Ot01WithGlyphs(Ot01Liberation, glyphs)).Outline(b);
        Assert.False(plain.IsEmpty);
        Assert.Equal(plain.Verbs, reserved.Verbs);
        Assert.Equal(plain.Points, reserved.Points);
    }

    /// <summary>
    /// OT 1.3 sentence 25: writers set reserved fields to zero — cmap format 12 written with its reserved word 0, format 4 with reservedPad 0
    /// after the end codes, the head of the own OCR font with only defined flag bits (0x000B).
    /// </summary>
    [Fact]
    public void Ot01_Writers_SetReservedFieldsToZero()
    {
        var format12 = typeof(TrueTypeSubsetter).GetMethod("Format12Of", BindingFlags.NonPublic | BindingFlags.Static)!;
        var format4 = typeof(TrueTypeSubsetter).GetMethod("Format4Of", BindingFlags.NonPublic | BindingFlags.Static)!;
        List<(int First, int Last, int Glyph)> runs = [(65, 70, 36)];
        var body12 = (byte[])format12.Invoke(null, [runs, 0])!;
        Assert.Equal(new byte[] { 0, 0 }, body12[2..4]);
        var body4 = (byte[])format4.Invoke(null, [runs, 0])!;
        var segments = BinaryPrimitives.ReadUInt16BigEndian(body4.AsSpan(6)) / 2;
        Assert.Equal(new byte[] { 0, 0 }, body4[(14 + segments * 2)..(16 + segments * 2)]);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(Ot01Table(OcrTextLayer.Glyphless, "head").AsSpan(16));
        Assert.Equal(0x000B, flags);
    }

    /// <summary>
    /// OT 1.3 sentence 26 (today's behaviour): PDF-Knead checks font programs not table by table — the PDF/A check of a file whose embedded
    /// TrueType program has the reserved head flag bit 15 set reports the same as for the unchanged program.
    /// </summary>
    [Fact]
    public void Ot01_PdfACheck_DoesNotLookAtReservedHeadFlags()
    {
        static List<string> Found(byte[] program)
        {
            var (pdf, page) = PdfACheckTests.Page("BT /F1 12 Tf 72 700 Td (AB) Tj ET", new PdfDictionary());
            var file = pdf.Add(new PdfStream(Dict(("Length1", PdfInteger.Of(program.Length))), program));
            var descriptor = pdf.Add(Dict(("Type", N("FontDescriptor")), ("FontName", N("LiberationSans")), ("Flags", PdfInteger.Of(32)),
                ("FontBBox", Box(-200, -300, 1200, 1000)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
                ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", Ref(file))));
            var font = Dict(("Type", N("Font")), ("Subtype", N("TrueType")), ("BaseFont", N("LiberationSans")), ("FirstChar", PdfInteger.Of(65)),
                ("LastChar", PdfInteger.Of(66)), ("Widths", Box(667, 667)), ("FontDescriptor", Ref(descriptor)), ("Encoding", N("WinAnsiEncoding")));
            page.Set(PdfName.Resources, Dict(("Font", Dict(("F1", Ref(pdf.Add(font)))))));
            using var doc = PdfDocument.Open(pdf.Build());
            return [.. PdfACheck.Check(doc).Select(v => v.ToString()).Order()];
        }
        var head = Ot01Table(Ot01Liberation, "head");
        head[16] |= 0x80;
        Assert.Equal(Found(Ot01Liberation), Found(Ot01With(Ot01Liberation, ("head", head))));
    }

    /// <summary>OT 1.3 sentences 27–28: an unknown major version means the table is not read — a GPOS of major version 2 positions nothing (widths as without it).</summary>
    [Fact]
    public void Ot01_GposOfMajorVersion2_IsNotApplied()
    {
        int a = Ot02G('A'), v = Ot02G('V');
        var gpos = Ot02Layout([new Ot02Script("latn", new Ot02Lang("", [0]))], [new Ot02Feature("kern", [0])],
            [Ot02Lookup(2, Ot02Pairs(0, (a, v, -80)))], major: 2);
        Assert.Equal(Ot02Font.AdvanceWidth(a), Ot02Shaper(gpos: gpos).Shape("AV")[0].XAdvance);
    }

    /// <summary>
    /// OT 1.3 sentences 27–28: a table of a major version the reader does not know is not used — GSUB, GPOS, GDEF, sbix and SVG so since FG
    /// 1149. head stays read whatever its version, a read tolerance (without it the font has no units per em and draws nothing; its fields
    /// have kept their places): a head of major version 2 gives the same units per em as version 1.
    /// </summary>
    [Fact]
    public void Ot01_HeadOfMajorVersion2_ReadTolerantly()
    {
        var head2 = OpenTypeFont.Parse(Ot01Patch(Ot01Liberation, "head", 0, 0, 2));
        Assert.Equal(OpenTypeFont.Parse(Ot01Liberation).UnitsPerEm, head2.UnitsPerEm);
        Assert.False(head2.Broken);
    }

    // ------------------------------------------------------------ OT 1.4

    /// <summary>OT 1.4.1 sentences 9–11, 13, 20–24 and 29: searchRange, entrySelector, rangeShift for 1, 10 and 16 tables — (16, 0, 0), (128, 3, 32), (256, 4, 0).</summary>
    [Theory]
    [InlineData(1, 16, 0, 0)]
    [InlineData(10, 128, 3, 32)]
    [InlineData(16, 256, 4, 0)]
    public void Ot01_Assemble_SearchFields(int tables, int searchRange, int entrySelector, int rangeShift)
    {
        var file = TrueTypeSubsetter.Assemble(Enumerable.Range(0, tables).ToDictionary(i => $"t{i:D3}", _ => new byte[] { 1 }));
        Assert.Equal((searchRange, entrySelector, rangeShift),
            ((int)BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(6)), (int)BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(8)),
                (int)BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(10))));
    }

    /// <summary>OT 1.4.1 sentences 16–17: „true“ does not belong in OpenType but is read like 1.0 (lenient); „typ1“ is refused.</summary>
    [Fact]
    public void Ot01_SfntVersionTrueReadAsTrueType_Typ1Refused()
    {
        var plain = OpenTypeFont.Parse(Ot01Liberation);
        var asTrue = OpenTypeFont.Parse(Ot01Sfnt(Ot01Tables(Ot01Liberation), 0x74727565));
        var a = plain.GlyphForUnicode('A');
        Assert.Equal(plain.GlyphCount, asTrue.GlyphCount);
        Assert.Equal(a, asTrue.GlyphForUnicode('A'));
        Assert.Equal(plain.Outline(a).Points, asTrue.Outline(a).Points);
        Assert.Throws<FontFormatException>(() => OpenTypeFont.Parse(Ot01Sfnt(Ot01Tables(Ot01Liberation), 0x74797031)));
    }

    /// <summary>OT 1.4.1 sentences 18–19 and 25–28: the search fields are not relied upon — with 0xFFFF in all three the font reads the same and can be subset.</summary>
    [Fact]
    public void Ot01_SearchFieldsWrong_ReadTheSame()
    {
        var broken = Ot01Sfnt(Ot01Tables(Ot01Liberation), search: 0xFFFF);
        var plain = OpenTypeFont.Parse(Ot01Liberation);
        var read = OpenTypeFont.Parse(broken);
        var a = plain.GlyphForUnicode('A');
        Assert.Equal(plain.GlyphCount, read.GlyphCount);
        Assert.Equal(plain.Outline(a).Points, read.Outline(a).Points);
        Assert.Equal(Ot01Tables(Ot01Liberation).Select(t => t.Tag), Ot01Tables(broken).Select(t => t.Tag));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(broken, [a])).Outline(a).IsEmpty);
    }

    /// <summary>OT 1.4.1 sentence 41: the records are written sorted by tag (byte order: capitals first) — post, OS/2, „cvt “, head, glyf become OS/2, „cvt “, glyf, head, post; the reader reads a font with its records in reverse order the same.</summary>
    [Fact]
    public void Ot01_Records_WrittenSortedByTag_ReadInAnyOrder()
    {
        var file = TrueTypeSubsetter.Assemble(new Dictionary<string, byte[]>
        {
            ["post"] = [1], ["OS/2"] = [2], ["cvt "] = [3], ["head"] = new byte[54], ["glyf"] = [5],
        });
        Assert.Equal(["OS/2", "cvt ", "glyf", "head", "post"], Ot01Directory(file).Select(d => d.Tag));
        var plain = OpenTypeFont.Parse(Ot01Liberation);
        var reversed = OpenTypeFont.Parse(Ot01Sfnt(Ot01Tables(Ot01Liberation).AsEnumerable().Reverse().ToList()));
        var a = plain.GlyphForUnicode('A');
        Assert.Equal((plain.GlyphCount, plain.UnitsPerEm, plain.PostScriptName), (reversed.GlyphCount, reversed.UnitsPerEm, reversed.PostScriptName));
        Assert.Equal(plain.Outline(a).Points, reversed.Outline(a).Points);
    }

    /// <summary>
    /// OT 1.4.1 sentences 44–45: one record per tag; with two, a reader may take either — but the display and the writers must take the same one:
    /// two glyf records (the second with „A“ empty), the display draws the first; the subset and the scrubbed font must keep that „A“.
    /// </summary>
    [Fact]
    public void Ot01_DuplicateTags_DisplayAndWritersTakeTheSameTable()
    {
        var font = OpenTypeFont.Parse(Ot01Liberation);
        var a = font.GlyphForUnicode('A');
        var glyphs = Ot01Glyphs(Ot01Liberation);
        var source = Ot01WithGlyphs(Ot01Liberation, glyphs);
        var tables = Ot01Tables(source);
        var glyf = tables.First(t => t.Tag == "glyf").Data;
        var second = (byte[])glyf.Clone();
        var start = (int)BinaryPrimitives.ReadUInt32BigEndian(Ot01Table(source, "loca").AsSpan(a * 4));
        second.AsSpan(start, glyphs[a].Length).Clear();
        tables.Insert(tables.FindIndex(t => t.Tag == "glyf") + 1, ("glyf", second));
        var doubled = Ot01Sfnt(tables);
        var shown = OpenTypeFont.Parse(doubled).Outline(a);
        Assert.False(shown.IsEmpty);
        Assert.Equal(shown.Points, OpenTypeFont.Parse(TrueTypeSubsetter.Subset(doubled, [a])).Outline(a).Points);
        Assert.Equal(shown.Points, OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(doubled, [a])).Outline(a).Points);
    }

    /// <summary>
    /// OT 1.4.1 sentences 44–45 (FG 1156): the first of two glyf records reaches beyond the file — the display reads it cut to the end of the
    /// file and draws „A“; the subset and the scrubbed font take the same record (before they passed it over and took the second, with „A“
    /// empty).
    /// </summary>
    [Fact]
    public void Ot01_DuplicateTags_FirstRecordBeyondTheFile_DisplayAndWritersAgree()
    {
        var font = OpenTypeFont.Parse(Ot01Liberation);
        var a = font.GlyphForUnicode('A');
        var glyphs = Ot01Glyphs(Ot01Liberation);
        var source = Ot01WithGlyphs(Ot01Liberation, glyphs);
        var tables = Ot01Tables(source);
        var glyf = tables.First(t => t.Tag == "glyf").Data;
        var second = (byte[])glyf.Clone();
        var start = (int)BinaryPrimitives.ReadUInt32BigEndian(Ot01Table(source, "loca").AsSpan(a * 4));
        second.AsSpan(start, glyphs[a].Length).Clear();
        tables.Insert(tables.FindIndex(t => t.Tag == "glyf") + 1, ("glyf", second));
        var doubled = Ot01Sfnt(tables);
        // the length of the first glyf record: far beyond the end of the file
        int count = BinaryPrimitives.ReadUInt16BigEndian(doubled.AsSpan(4));
        var record = Enumerable.Range(0, count).Select(i => 12 + i * 16).First(r => System.Text.Encoding.ASCII.GetString(doubled, r, 4) == "glyf");
        BinaryPrimitives.WriteUInt32BigEndian(doubled.AsSpan(record + 12), (uint)doubled.Length * 2);
        var shown = OpenTypeFont.Parse(doubled).Outline(a);
        Assert.False(shown.IsEmpty);
        Assert.Equal(shown.Points, OpenTypeFont.Parse(TrueTypeSubsetter.Subset(doubled, [a])).Outline(a).Points);
        Assert.Equal(shown.Points, OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(doubled, [a])).Outline(a).Points);
    }

    /// <summary>OT 1.4.1 sentences 46–49: tables of other specifications are allowed — a table „zzzz“ stays unchanged in a subset with all tables and in the scrubbed font.</summary>
    [Fact]
    public void Ot01_UnknownTable_StaysUnchanged()
    {
        byte[] own = [9, 8, 7, 6, 5];
        var font = Ot01With(Ot01Liberation, ("zzzz", own));
        Assert.Equal(own, Ot01Table(TrueTypeSubsetter.Subset(font, [36], keepAllTables: true), "zzzz"));
        Assert.Equal(own, Ot01Table(TrueTypeSubsetter.Scrub(font, [36]), "zzzz"));
    }

    /// <summary>The table tags of the OpenType table list (OT 1.6).</summary>
    private static readonly HashSet<string> Ot01RegisteredTags = new(StringComparer.Ordinal)
    {
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "gasp", "CFF ", "CFF2", "VORG",
        "SVG ", "EBDT", "EBLC", "EBSC", "CBDT", "CBLC", "sbix", "BASE", "GDEF", "GPOS", "GSUB", "JSTF", "MATH", "avar", "cvar", "fvar", "gvar",
        "HVAR", "MVAR", "STAT", "VVAR", "COLR", "CPAL", "DSIG", "hdmx", "kern", "LTSH", "MERG", "meta", "PCLT", "VDMX", "vhea", "vmtx",
    };

    /// <summary>OT 1.4.1 sentence 50: PDF-Knead defines no tables of its own — every tag of the OCR font and of a subset is one of the specification.</summary>
    [Fact]
    public void Ot01_OwnFonts_UseOnlyRegisteredTags()
    {
        Assert.All(Ot01Directory(OcrTextLayer.Glyphless), d => Assert.Contains(d.Tag, Ot01RegisteredTags));
        Assert.All(Ot01Directory(TrueTypeSubsetter.Subset(Ot01Liberation, [36])), d => Assert.Contains(d.Tag, Ot01RegisteredTags));
    }

    /// <summary>
    /// OT 1.4.1 sentences 52–54 and 55–58: each table starts on a four-byte boundary, padded with zeros, the record giving the real length; the
    /// tables follow one another without gaps or overlap — lengths 5, 2 and 54 (the data in the recommended order, head first: OT 3.26, FG 1151).
    /// </summary>
    [Fact]
    public void Ot01_Assemble_TablesAlignedPaddedAndContiguous()
    {
        var file = TrueTypeSubsetter.Assemble(new Dictionary<string, byte[]>
        {
            ["aaaa"] = [1, 1, 1, 1, 1], ["bbbb"] = [2, 2], ["head"] = Enumerable.Repeat((byte)3, 54).ToArray(),
        });
        Assert.Equal([5, 2, 54], Ot01Directory(file).Select(r => r.Length));
        var records = Ot01Directory(file).OrderBy(r => r.Offset).ToList();
        Assert.Equal(12 + 16 * 3, records[0].Offset);
        for (var i = 0; i < records.Count; i++)
        {
            Assert.Equal(0, records[i].Offset % 4);
            var padded = (records[i].Length + 3) & ~3;
            Assert.All(file[(records[i].Offset + records[i].Length)..(records[i].Offset + padded)], b => Assert.Equal(0, b));
            if (i + 1 < records.Count) Assert.Equal(records[i].Offset + padded, records[i + 1].Offset);
            else Assert.Equal(file.Length, records[i].Offset + padded);
        }
    }

    /// <summary>OT 1.4.2 sentences 1–4: the checksum of each table in the directory of a subset is the unsigned sum of its 32-bit words (head: see the next test).</summary>
    [Fact]
    public void Ot01_Subset_TableChecksums()
    {
        var subset = TrueTypeSubsetter.Subset(Ot01Liberation, [36, 37]);
        foreach (var (tag, sum, offset, length) in Ot01Directory(subset).Where(d => d.Tag != "head"))
            Assert.True(Ot01Checksum(subset.AsSpan(offset, length)) == sum, tag);
    }

    /// <summary>OT 1.4.2 sentences 5–6: the length in the record is not padded, the sum reckons as if padded with zeros — 01 02 03 04 05 sums to 0x06020304, its record has length 5.</summary>
    [Fact]
    public void Ot01_Checksum_OfAnUnpaddedLength()
    {
        var record = Ot01Directory(TrueTypeSubsetter.Assemble(new Dictionary<string, byte[]> { ["test"] = [1, 2, 3, 4, 5] }))[0];
        Assert.Equal((0x06020304u, 5), (record.Sum, record.Length));
    }

    /// <summary>
    /// OT 1.4.2 sentences 7–13: checkSumAdjustment — the files of subset and scrubbing sum to 0xB1B0AFBA, and the head record's checksum is the sum
    /// of head with checkSumAdjustment 0.
    /// </summary>
    [Fact]
    public void Ot01_SubsetAndScrub_CheckSumAdjustment()
    {
        foreach (var file in new[] { TrueTypeSubsetter.Subset(Ot01Liberation, [36]), TrueTypeSubsetter.Scrub(Ot01Liberation, [36]) })
        {
            Assert.Equal(0xB1B0AFBAu, Ot01Checksum(file));
            var head = Ot01Directory(file).First(d => d.Tag == "head");
            var data = file.AsSpan(head.Offset, head.Length).ToArray();
            data.AsSpan(8, 4).Clear();
            Assert.Equal(Ot01Checksum(data), head.Sum);
        }
    }

    /// <summary>OT 1.4.2 sentence 14: PDF-Knead compares no checksums — Liberation Sans with wrong checksums in every record reads as the original.</summary>
    [Fact]
    public void Ot01_WrongChecksums_ReadTheSame()
    {
        var wrong = (byte[])Ot01Liberation.Clone();
        int n = BinaryPrimitives.ReadUInt16BigEndian(wrong.AsSpan(4));
        for (var i = 0; i < n; i++) BinaryPrimitives.WriteUInt32BigEndian(wrong.AsSpan(12 + i * 16 + 4), 0xDEADBEEF);
        var plain = OpenTypeFont.Parse(Ot01Liberation);
        var read = OpenTypeFont.Parse(wrong);
        var a = plain.GlyphForUnicode('A');
        Assert.Equal((plain.GlyphCount, plain.UnitsPerEm, plain.Intact), (read.GlyphCount, read.UnitsPerEm, read.Intact));
        Assert.Equal(plain.Outline(a).Points, read.Outline(a).Points);
    }

    // ------------------------------------------------------------ OT 1.4.2 (collections), 1.5

    /// <summary>
    /// OT 1.4.2 sentences 15–16 and OT 1.5.1 sentences 17–18: PDF-Knead writes no collections; the first font of the Noto CJK collection taken out
    /// is a font of its own („OTTO“, checksums of its own, the file summing to 0xB1B0AFBA, no DSIG) with the glyph numbers of the collection.
    /// </summary>
    [Fact]
    public void Ot01_FirstOfCollection_IsASingleFontWithTheSameGlyphs()
    {
        var noto = Ot01Noto();
        var first = TrueTypeSubsetter.FirstOfCollection(noto);
        Assert.Equal("OTTO"u8.ToArray(), first[..4]);
        Assert.Equal(0xB1B0AFBAu, Ot01Checksum(first));
        Assert.DoesNotContain(Ot01Directory(first), d => d.Tag == "DSIG");
        var before = OpenTypeFont.Parse(noto, 0);
        var after = OpenTypeFont.Parse(first);
        Assert.Equal(before.GlyphCount, after.GlyphCount);
        foreach (var c in "漢字Aあ")
        {
            var g = before.GlyphForUnicode(c);
            Assert.Equal(g, after.GlyphForUnicode(c));
            Assert.Equal(before.Outline(g).Points, after.Outline(g).Points);
        }
    }

    /// <summary>OT 1.5 sentences 12–14: a variable font (fvar, gvar) is read in its default instance — „A“ draws the glyf outline without the variation.</summary>
    [Fact]
    public void Ot01_VariableFont_DrawsTheDefaultInstance()
    {
        var plain = OpenTypeFont.Parse(Ot01Liberation);
        var variable = OpenTypeFont.Parse(Ot01Variable(Ot01Liberation));
        var a = plain.GlyphForUnicode('A');
        Assert.True(variable.HasTable("fvar") && variable.HasTable("gvar"));
        Assert.Equal(plain.Outline(a).Points, variable.Outline(a).Points);
    }

    /// <summary>
    /// OT 1.5.2 sentences 1–2 and 4, and 15–24: a collection header 2.0 with the fields of a digital signature (dsigTag „DSIG“, length, offset)
    /// reads the same fonts as header 1.0; the signature is not evaluated.
    /// </summary>
    [Fact]
    public void Ot01_CollectionHeader2_ReadsTheSameFonts()
    {
        var v1 = OpenTypeFont.Parse(Ot01Collection(Ot01Liberation, false, false));
        var v2 = OpenTypeFont.Parse(Ot01Collection(Ot01Liberation, true, true));
        var a = v1.GlyphForUnicode('A');
        Assert.True(a > 0);
        Assert.Equal((v1.GlyphCount, v1.UnitsPerEm, v1.PostScriptName), (v2.GlyphCount, v2.UnitsPerEm, v2.PostScriptName));
        Assert.Equal(a, v2.GlyphForUnicode('A'));
        Assert.Equal(v1.Outline(a).Points, v2.Outline(a).Points);
    }

    /// <summary>OT 1.5.2 sentence 3: PDF-Knead writes no signed collections — the first font of a signed collection is taken out without DSIG.</summary>
    [Fact]
    public void Ot01_FirstOfSignedCollection_HasNoDsig()
    {
        var first = TrueTypeSubsetter.FirstOfCollection(Ot01Collection(Ot01Liberation, true, true));
        Assert.DoesNotContain(Ot01Directory(first), d => d.Tag == "DSIG");
        Assert.Contains(Ot01Directory(first), d => d.Tag == "glyf");
    }

    // ------------------------------------------------------------ OT 1.6

    /// <summary>
    /// OT 1.6.1 sentences 1–10: the required tables — the own OCR font has cmap, head, hhea, hmtx, maxp, name, OS/2 and post; the subset for a
    /// simple TrueType font has the tables kept for every subset plus cmap, post and name.
    /// </summary>
    [Fact]
    public void Ot01_RequiredTables_OcrFontAndSubsetWithCmap()
    {
        string[] required = ["cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post"];
        var ocr = Ot01Directory(OcrTextLayer.Glyphless).Select(d => d.Tag).ToHashSet();
        Assert.Superset(required.ToHashSet(), ocr);
        string[] keep = ["head", "hhea", "maxp", "OS/2", "hmtx", "cvt ", "fpgm", "prep", "loca", "glyf"];
        var expected = keep.Concat(["cmap", "post", "name"]).Where(t => OpenTypeFont.Parse(Ot01Liberation).HasTable(t)).Order(StringComparer.Ordinal);
        Assert.Equal(expected, Ot01Directory(TrueTypeSubsetter.SubsetWithCmap(Ot01Liberation, [36])).Select(d => d.Tag));
    }

    /// <summary>OT 1.6.2 sentences 3–4 and 7: the hinting tables are optional and go unchanged into a subset — „cvt “, fpgm and prep byte for byte.</summary>
    [Fact]
    public void Ot01_Subset_KeepsHintingTablesUnchanged()
    {
        var subset = TrueTypeSubsetter.Subset(Ot01Liberation, [36]);
        foreach (var tag in new[] { "cvt ", "fpgm", "prep" }) Assert.Equal(Ot01Table(Ot01Liberation, tag), Ot01Table(subset, tag));
    }

    /// <summary>OT 1.6.2 sentence 8: gasp is optional — left out of a subset, kept in a subset with all tables.</summary>
    [Fact]
    public void Ot01_Gasp_LeftOutOfASubset_KeptWithAllTables()
    {
        Assert.True(OpenTypeFont.Parse(Ot01Liberation).HasTable("gasp"));
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(Ot01Liberation, [36])).HasTable("gasp"));
        Assert.Equal(Ot01Table(Ot01Liberation, "gasp"), Ot01Table(TrueTypeSubsetter.Subset(Ot01Liberation, [36], keepAllTables: true), "gasp"));
    }

    /// <summary>The CFF-based font of the Noto collection with the tables given (others left out).</summary>
    private static Dictionary<string, byte[]> Ot01NotoTables(params string[] tags)
    {
        var noto = OpenTypeFont.Parse(Ot01Noto());
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in tags)
            if (noto.Table(tag) is { } t) tables[tag] = noto.FileData.AsSpan(t.Offset, t.Length).ToArray();
        return tables;
    }

    /// <summary>
    /// OT 1.6.3 sentence 4 (question 1, docs/FEHLERGEDAECHTNIS.md, No. 1158): CFF outlines in a CFF2 table (Compact Font Format 2.0) are read in the
    /// default instance and scrubbed — glyph 1 of a CFF2 font draws its rectangle, and scrubbing it keeps glyph 1 and empties glyph 2 (before, the
    /// font had no outlines and the redaction left the program unchanged). Data of CFF version 1 put into a table „CFF2“ are no CFF2 (major
    /// version 1): that font stays without outlines, and scrubbing fails as for any unreadable program.
    /// </summary>
    [Fact]
    public void Ot01_Cff2_IsReadAndScrubbed()
    {
        var file = Cff2FontOt08(Cff2GlyphsOt08("100 0 rmoveto 200 0 rlineto 0 200 rlineto", "0 0 rmoveto 0 300 rlineto 300 0 rlineto"), 3);
        var font = OpenTypeFont.Parse(file);
        Assert.False(font.Outline(1).IsEmpty || font.Outline(2).IsEmpty);
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(file, [1]));
        Assert.Equal(font.Outline(1).Points, scrubbed.Outline(1).Points);
        Assert.True(scrubbed.Outline(2).IsEmpty);

        var tables = Ot01NotoTables("CFF ", "cmap", "head", "hhea", "hmtx", "maxp");
        tables["CFF2"] = tables["CFF "];
        tables.Remove("CFF ");
        var version1 = TrueTypeSubsetter.Finish(tables);
        var wrong = OpenTypeFont.Parse(version1);
        var g = wrong.GlyphForUnicode('漢');
        Assert.True(g > 0);
        Assert.Null(wrong.Cff);
        Assert.True(wrong.Outline(g).IsEmpty);
        Assert.False(wrong.HasTable("glyf") || wrong.HasTable("CFF "));
        Assert.Throws<FontFormatException>(() => TrueTypeSubsetter.Scrub(version1, [g]));
    }

    /// <summary>
    /// OT 1.6.3 sentences 5–6: VORG is not read, PDF sets the vertical metrics itself — an OpenType CFF font with a VORG of default origin 500,
    /// set vertically (Identity-V without /W2), has its origin at 880 from the default /DW2.
    /// </summary>
    [Fact]
    public void Ot01_Vorg_IsNotRead_TheOriginComesFromDw2()
    {
        var tables = Ot01NotoTables("CFF ", "cmap", "head", "hhea", "hmtx", "maxp");
        tables["VORG"] = new Ot02Bytes().U16(1).U16(0).U16(500).U16(0).ToArray();
        var program = TrueTypeSubsetter.Finish(tables);
        var parsed = OpenTypeFont.Parse(program);
        var cid = parsed.Cff!.CidOf(parsed.GlyphForUnicode('漢'));
        Assert.True(cid > 0);
        using var doc = PdfDocument.Open(Simple(pages: 1).Build());
        var descriptor = Dict(("Type", N("FontDescriptor")), ("FontName", N("Erfunden")), ("Flags", PdfInteger.Of(4)),
            ("FontFile3", new PdfStream(Dict(("Subtype", N("OpenType"))), program)));
        var font = PdfFont.Get(Dict(("Type", N("Font")), ("Subtype", N("Type0")), ("BaseFont", N("Erfunden")), ("Encoding", N("Identity-V")),
            ("DescendantFonts", new PdfArray([Dict(("Type", N("Font")), ("Subtype", N("CIDFontType0")), ("BaseFont", N("Erfunden")),
                ("CIDSystemInfo", Dict(("Registry", new PdfString("Adobe"u8.ToArray())), ("Ordering", new PdfString("Identity"u8.ToArray())),
                    ("Supplement", PdfInteger.Of(0)))), ("FontDescriptor", descriptor))]))), doc);
        Assert.True(font.Vertical);
        Assert.True(((CompositeFont)font).HasEmbeddedProgram);   // the program with VORG is the one read
        var glyph = font.Glyph((uint)cid, 2);
        Assert.Equal(0.88, glyph.OriginY, 6);
        Assert.Equal(-1.0, glyph.VerticalAdvance, 6);
    }

    /// <summary>OT 1.6.5 sentences 2–4 and 8–10: monochrome bitmaps (EBLC, EBDT, EBSC) are not read — „A“ draws from its outline as without them; scrubbing leaves them out.</summary>
    [Fact]
    public void Ot01_Bitmaps_NotReadAndDroppedWhenScrubbing()
    {
        byte[] header = [0, 2, 0, 0, 0, 0, 0, 0];
        var font = Ot01With(Ot01Liberation, ("EBLC", header), ("EBDT", [0, 2, 0, 0]), ("EBSC", header));
        var a = Ot02G('A');
        Assert.Equal(Ot02Font.Outline(a).Points, OpenTypeFont.Parse(font).Outline(a).Points);
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, [a]));
        Assert.False(scrubbed.HasTable("EBLC") || scrubbed.HasTable("EBDT") || scrubbed.HasTable("EBSC"));
    }

    /// <summary>
    /// OT 1.6.6 sentences 3 and 7–8, and OT 2.3 sentences 9–11: BASE, JSTF and MATH are not used — shaping gives the same glyphs and advances as
    /// without them; scrubbing leaves all three out, a subset BASE and JSTF.
    /// </summary>
    [Fact]
    public void Ot01_BaseJstfMath_NotUsedAndLeftOut()
    {
        var font = Ot01With(Ot01Liberation, ("BASE", [0, 1, 0, 0, 0, 0, 0, 0]), ("JSTF", [0, 1, 0, 0, 0, 0]), ("MATH", [0, 1, 0, 0, 0, 0, 0, 0, 0, 0]));
        const string text = "AVA fi office";
        var with = TextShaper.For(OpenTypeFont.Parse(font)).Shape(text);
        var without = TextShaper.For(OpenTypeFont.Parse(Ot01Liberation)).Shape(text);
        Assert.Equal(without.Select(g => (g.Glyph, g.XAdvance, g.XOffset)), with.Select(g => (g.Glyph, g.XAdvance, g.XOffset)));
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, [36]));
        Assert.False(scrubbed.HasTable("BASE") || scrubbed.HasTable("JSTF") || scrubbed.HasTable("MATH"));
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(font, [36]));
        Assert.False(subset.HasTable("BASE") || subset.HasTable("JSTF"));
    }

    /// <summary>OT 1.6.7 sentences 1–9: a subset of a variable font is a font of the default instance — without fvar, gvar and STAT, „A“ as in glyf.</summary>
    [Fact]
    public void Ot01_SubsetOfAVariableFont_IsTheDefaultInstance()
    {
        var a = Ot02G('A');
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(Ot01Variable(Ot01Liberation), [a]));
        Assert.False(subset.HasTable("fvar") || subset.HasTable("gvar") || subset.HasTable("STAT"));
        Assert.Equal(Ot02Font.Outline(a).Points, subset.Outline(a).Points);
    }

    /// <summary>OT 1.6.8 sentences 2–3 (today's behaviour): COLR and CPAL are not read — no colour glyphs, „A“ draws from its outline; scrubbing leaves both out.</summary>
    [Fact]
    public void Ot01_ColrCpal_NotReadAndDroppedWhenScrubbing()
    {
        var a = Ot02G('A');
        var colr = new Ot02Bytes().U16(0).U16(1).U32(14).U32(20).U16(1).U16(a).U16(0).U16(1).U16(a).U16(0).ToArray();
        var cpal = new Ot02Bytes().U16(0).U16(1).U16(1).U16(1).U32(14).U16(0).U16(0x00FF).U16(0x00FF).ToArray();
        var font = Ot01With(Ot01Liberation, ("COLR", colr), ("CPAL", cpal));
        var parsed = OpenTypeFont.Parse(font);
        Assert.Null(ColorGlyphs.For(parsed));
        Assert.Equal(Ot02Font.Outline(a).Points, parsed.Outline(a).Points);
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, [a]));
        Assert.False(scrubbed.HasTable("COLR") || scrubbed.HasTable("CPAL"));
    }

    /// <summary>OT 1.6.9 sentences 1–2: a font written anew carries no signature that no longer fits — a subset with all tables has every table but DSIG.</summary>
    [Fact]
    public void Ot01_SubsetWithAllTables_DropsOnlyDsig()
    {
        var font = Ot01With(Ot01Liberation, ("DSIG", [0, 0, 0, 1, 0, 0, 0, 0]));
        var tags = Ot01Directory(TrueTypeSubsetter.Subset(font, [36], keepAllTables: true)).Select(d => d.Tag);
        Assert.Equal(Ot01Directory(Ot01Liberation).Select(d => d.Tag).Where(t => t != "DSIG").Order(StringComparer.Ordinal), tags);
    }

    /// <summary>
    /// OT 1.6.9 sentences 11–12: vhea and vmtx — scrubbing the CFF font of Noto CJK clears height and top side bearing of the glyphs removed (the
    /// advance of the last full record stays) and keeps those of the glyphs kept.
    /// </summary>
    [Fact]
    public void Ot01_Scrub_ClearsVerticalMetricsOfRemovedGlyphs()
    {
        var tables = Ot01NotoTables("CFF ", "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "vhea", "vmtx");
        Assert.True(tables.ContainsKey("vhea") && tables.ContainsKey("vmtx"));
        var otf = TrueTypeSubsetter.Finish(tables);
        var font = OpenTypeFont.Parse(otf);
        int[] kept = [.. "漢字A".Select(c => font.GlyphForUnicode(c))];
        int[] removed = [.. "秘密Z".Select(c => font.GlyphForUnicode(c))];
        var scrubbed = TrueTypeSubsetter.Scrub(otf, kept);
        var full = BinaryPrimitives.ReadUInt16BigEndian(tables["vhea"].AsSpan(34));
        (int Advance, int Bearing) Metrics(byte[] vmtx, int g) => g < full
            ? (BinaryPrimitives.ReadUInt16BigEndian(vmtx.AsSpan(g * 4)), BinaryPrimitives.ReadInt16BigEndian(vmtx.AsSpan(g * 4 + 2)))
            : (-1, BinaryPrimitives.ReadInt16BigEndian(vmtx.AsSpan(full * 4 + (g - full) * 2)));
        var before = tables["vmtx"];
        var after = Ot01Table(scrubbed, "vmtx");
        foreach (var g in kept) Assert.Equal(Metrics(before, g), Metrics(after, g));
        foreach (var g in removed)
        {
            Assert.NotEqual((0, 0), Metrics(before, g));
            Assert.Equal(g < full - 1 ? (0, 0) : (Metrics(before, g).Advance, 0), Metrics(after, g));
        }
    }

    /// <summary>OT 1.6.9 sentences 3, 5–10 and 13–14: device metrics and extra data (hdmx, LTSH, VDMX, meta) are left out when scrubbing.</summary>
    [Fact]
    public void Ot01_Scrub_DropsDeviceMetricsAndMeta()
    {
        var font = Ot01With(Ot01Liberation, ("hdmx", [0, 0, 0, 0, 0, 0, 0, 0]), ("LTSH", [0, 0, 0, 0]), ("VDMX", [0, 0, 0, 0, 0, 0]),
            ("meta", [0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 16, 0, 0, 0, 0]));
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, [36]));
        foreach (var tag in new[] { "hdmx", "LTSH", "VDMX", "meta" }) Assert.False(scrubbed.HasTable(tag), tag);
    }

    // ------------------------------------------------------------ OT 2.1

    /// <summary>OT 2.1 sentences 4 and 7: a font may carry several scripts; one without „latn“, with DFLT and „cyrl“ only, shapes Latin text with the lookups of DFLT.</summary>
    [Fact]
    public void Ot01_FontWithoutLatn_ShapesWithDflt()
    {
        int a = Ot02G('a');
        var gsub = Ot02Layout([new Ot02Script("DFLT", new Ot02Lang("", [0])), new Ot02Script("cyrl", new Ot02Lang("", [1]))],
            [new Ot02Feature("liga", [0]), new Ot02Feature("liga", [1])],
            [Ot02Lookup(1, Ot02Single((a, Ot02G('b')))), Ot02Lookup(1, Ot02Single((a, Ot02G('c'))))]);
        Assert.Equal(Ot02Gs("b"), Ot02Shape(Ot02Shaper(gsub), "a"));
    }

    /// <summary>
    /// OT 2.1 sentences 8, 12 and 14–15: per run either the default language system or one language system applies, never both — with a default
    /// one („liga“) and „TRK “ („smcp“) only the default one's lookups; without a default one those of the first language system.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ot01_OneLanguageSystemPerRun(bool withDefault)
    {
        var gsub = Ot02Layout([new Ot02Script("latn", withDefault ? new Ot02Lang("", [0]) : null, new Ot02Lang("TRK ", [1]))],
            [new Ot02Feature("liga", [0]), new Ot02Feature("smcp", [1])], [Ot02Lookup(1), Ot02Lookup(1)]);
        Assert.Equal(withDefault ? [0] : new[] { 1 }, new LayoutTable(gsub, 7).LookupsFor(["liga", "smcp"]));
    }
}
