using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-27 (OpenType specification: gvar, head, hhea): the tests the drafts of the sentence-by-sentence comparison asked for —
/// variable TrueType fonts are drawn in their default instance, the head and hhea tables of the font of the text recognition, and what
/// subsets, rebuilt fonts and new fonts take from head and hhea.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (ot-27; also used by ot-28)

    private static readonly Lazy<byte[]> Ot27SansFile = new(() => EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"));

    private static readonly Lazy<OpenTypeFont> Ot27Sans = new(() => OpenTypeFont.Parse(Ot27SansFile.Value));

    private static int Ot27U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data[at..]);

    private static int Ot27I16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadInt16BigEndian(data[at..]);

    private static uint Ot27U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32BigEndian(data[at..]);

    /// <summary>A table of a single font file (a copy); empty if the font has none.</summary>
    private static byte[] Ot27Table(byte[] font, string tag)
    {
        for (var i = 0; i < Ot27U16(font, 4); i++)
        {
            var at = 12 + 16 * i;
            if (Encoding.ASCII.GetString(font, at, 4) == tag)
                return font.AsSpan((int)Ot27U32(font, at + 8), (int)Ot27U32(font, at + 12)).ToArray();
        }
        return [];
    }

    /// <summary>A font with tables added, replaced or (null) removed.</summary>
    private static byte[] Ot27With(byte[] font, params (string Tag, byte[]? Data)[] tables) =>
        ColorFontBuilder.WithTables(font, tables.ToDictionary(t => t.Tag, t => t.Data));

    private static byte[] Ot27SansWith(params (string Tag, byte[]? Data)[] tables) => Ot27With(Ot27SansFile.Value, tables);

    private static int Ot27Glyph(char c) => Ot27Sans.Value.GlyphForUnicode(c);

    private static bool Ot27SameOutline(GlyphOutline a, GlyphOutline b) => a.Verbs.SequenceEqual(b.Verbs) && a.Points.SequenceEqual(b.Points);

    /// <summary>fvar with one axis „wght“ 100 … 900, default 400, no named instances.</summary>
    private static byte[] Ot27Fvar()
    {
        var f = new byte[16 + 20];
        BinaryPrimitives.WriteUInt32BigEndian(f, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), 16);     // axes at 16
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(6), 2);      // reserved
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(8), 1);      // one axis
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(10), 20);    // axis record size
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(14), 8);     // instance size (no instances)
        "wght"u8.CopyTo(f.AsSpan(16));
        BinaryPrimitives.WriteInt32BigEndian(f.AsSpan(20), 100 << 16);
        BinaryPrimitives.WriteInt32BigEndian(f.AsSpan(24), 400 << 16);
        BinaryPrimitives.WriteInt32BigEndian(f.AsSpan(28), 900 << 16);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(34), 256);   // axis name ID
        return f;
    }

    /// <summary>
    /// The variation data of one glyph: one tuple (peak 1.0 on every axis, embedded, private point numbers) that moves the points given by
    /// <paramref name="dx"/> in x and leaves y; at most 64 points, ascending.
    /// </summary>
    private static byte[] Ot27GlyphDeltas(int axisCount, int[] points, int dx)
    {
        var d = new List<byte>();
        d.Add((byte)points.Length);                                  // point count
        d.Add((byte)(points.Length - 1));                            // one run of byte point numbers
        for (var i = 0; i < points.Length; i++) d.Add((byte)(i == 0 ? points[0] : points[i] - points[i - 1]));
        d.Add((byte)(points.Length - 1));                            // x deltas as bytes
        foreach (var _ in points) d.Add((byte)(sbyte)dx);
        d.Add((byte)(0x80 | (points.Length - 1)));                   // y deltas: zeros
        var headerSize = 4 + 2 * axisCount;
        var g = new List<byte>();
        void U16(int v) => g.AddRange([(byte)(v >> 8), (byte)v]);
        U16(1);                                                      // one tuple, no shared points
        U16(4 + headerSize);                                         // serialized data after the header
        U16(d.Count);                                                // variationDataSize
        U16(0x8000 | 0x2000);                                        // embedded peak tuple, private point numbers
        for (var a = 0; a < axisCount; a++) U16(0x4000);             // peak 1.0
        g.AddRange(d);
        if (g.Count % 2 != 0) g.Add(0);
        return [.. g];
    }

    /// <summary>
    /// gvar for <paramref name="glyphCount"/> glyphs with <paramref name="axisCount"/> axes: the glyphs of <paramref name="data"/> have that variation
    /// data, every other glyph none (data size 0); long offsets, no shared tuples.
    /// </summary>
    private static byte[] Ot27Gvar(int glyphCount, IReadOnlyDictionary<int, byte[]> data, int axisCount = 1)
    {
        var header = 20 + (glyphCount + 1) * 4;
        var body = new List<byte>();
        var g = new byte[header];
        BinaryPrimitives.WriteUInt16BigEndian(g, 1);
        BinaryPrimitives.WriteUInt16BigEndian(g.AsSpan(4), (ushort)axisCount);
        BinaryPrimitives.WriteUInt32BigEndian(g.AsSpan(8), (uint)header);     // shared tuples (none)
        BinaryPrimitives.WriteUInt16BigEndian(g.AsSpan(12), (ushort)glyphCount);
        BinaryPrimitives.WriteUInt16BigEndian(g.AsSpan(14), 1);               // long offsets
        BinaryPrimitives.WriteUInt32BigEndian(g.AsSpan(16), (uint)header);
        for (var i = 0; i <= glyphCount; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(g.AsSpan(20 + i * 4), (uint)body.Count);
            if (i < glyphCount && data.TryGetValue(i, out var d)) body.AddRange(d);
        }
        return [.. g, .. body];
    }

    /// <summary>
    /// HVAR for <paramref name="glyphCount"/> glyphs: an ItemVariationStore with one region (peak 1.0 on one axis) and a delta of +100 for every
    /// glyph; with <paramref name="mappings"/> DeltaSetIndexMaps (glyph → inner index = glyph) for advance, left and right side bearing,
    /// otherwise none (the glyph index is the item).
    /// </summary>
    private static byte[] Ot27Hvar(int glyphCount, bool mappings)
    {
        var store = new List<byte>();
        void U16(List<byte> b, int v) => b.AddRange([(byte)(v >> 8), (byte)v]);
        void U32(List<byte> b, int v) => b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        U16(store, 1);                                               // format 1
        U32(store, 12);                                              // region list
        U16(store, 1);                                               // one ItemVariationData
        U32(store, 22);
        U16(store, 1); U16(store, 1);                                // one axis, one region
        U16(store, 0); U16(store, 0x4000); U16(store, 0x4000);       // start 0, peak 1, end 1
        U16(store, glyphCount); U16(store, 0); U16(store, 1); U16(store, 0);   // items, no word deltas, one region index: 0
        for (var g = 0; g < glyphCount; g++) store.Add(100);
        var map = new List<byte> { 0, 0x1F };                        // format 0, entries of 2 bytes with 16 inner bits
        U16(map, glyphCount);
        for (var g = 0; g < glyphCount; g++) U16(map, g);
        var hvar = new List<byte>();
        U16(hvar, 1); U16(hvar, 0);                                  // version 1.0
        U32(hvar, 20);                                               // store
        var mapAt = mappings ? 20 + store.Count : 0;
        U32(hvar, mapAt); U32(hvar, mapAt); U32(hvar, mapAt);        // advance, lsb, rsb maps (one shared map, or none)
        hvar.AddRange(store);
        if (mappings) hvar.AddRange(map);
        return [.. hvar];
    }

    /// <summary>The number of points of a simple glyph in glyf.</summary>
    private static int Ot27PointCount(OpenTypeFont font, int glyph)
    {
        var glyf = Ot27GlyphData(font, glyph);
        var contours = Ot27I16(glyf, 0);
        Assert.True(contours > 0, "keine einfache Glyphe");
        return Ot27U16(glyf, 10 + 2 * (contours - 1)) + 1;
    }

    /// <summary>The glyf data of one glyph.</summary>
    private static byte[] Ot27GlyphData(OpenTypeFont font, int glyph)
    {
        var loca = font.TableData("loca").ToArray();
        int Offset(int g) => font.LongLoca ? (int)Ot27U32(loca, g * 4) : Ot27U16(loca, g * 2) * 2;
        return font.TableData("glyf")[Offset(glyph)..Offset(glyph + 1)].ToArray();
    }

    /// <summary>Liberation Sans with the glyf data of one glyph replaced (glyf rewritten with long loca, every glyph aligned to 4 bytes).</summary>
    private static byte[] Ot27SansWithGlyph(int glyph, byte[] data, params (string Tag, byte[]? Data)[] more)
    {
        var font = Ot27Sans.Value;
        var count = font.GlyphCount;
        var glyf = new List<byte>();
        var loca = new byte[(count + 1) * 4];
        for (var g = 0; g < count; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(g * 4), (uint)glyf.Count);
            glyf.AddRange(g == glyph ? data : Ot27GlyphData(font, g));
            while (glyf.Count % 4 != 0) glyf.Add(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(count * 4), (uint)glyf.Count);
        var head = Ot27Table(Ot27SansFile.Value, "head");
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1);
        return Ot27SansWith([("glyf", [.. glyf]), ("loca", loca), ("head", head), .. more]);
    }

    /// <summary>
    /// A composite glyph: component <paramref name="first"/> at the offset (100, 0) (ARGS_ARE_XY_VALUES, with <paramref name="firstFlags"/> added),
    /// and, if <paramref name="second"/> ≥ 0, component <paramref name="second"/> placed by matching point 0 of the parent with point 0 of the
    /// component (ARGS_ARE_XY_VALUES clear).
    /// </summary>
    private static byte[] Ot27Composite(int first, int second = -1, int firstFlags = 0)
    {
        var g = new List<byte>();
        void U16(int v) => g.AddRange([(byte)(v >> 8), (byte)v]);
        U16(0xFFFF);                                                 // numberOfContours −1
        U16(0); U16(0); U16(2000); U16(1500);                        // bounding box
        U16(0x0001 | 0x0002 | (second >= 0 ? 0x0020 : 0) | firstFlags);   // words, xy values, more components
        U16(first);
        U16(100); U16(0);
        if (second >= 0)
        {
            U16(0x0001);                                             // words, point numbers
            U16(second);
            U16(0); U16(0);
        }
        return [.. g];
    }

    /// <summary>A collection (ttcf 1.0) of one font: the font's tables, their offsets moved by the 16 bytes of the collection header.</summary>
    private static byte[] Ot27CollectionOf(byte[] font)
    {
        var result = new byte[16 + font.Length];
        "ttcf"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), 16);
        font.CopyTo(result, 16);
        for (var i = 0; i < Ot27U16(font, 4); i++)
        {
            var at = 16 + 12 + 16 * i + 8;
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(at), Ot27U32(result, at) + 16);
        }
        return result;
    }

    /// <summary>Sum of all uint32 of a file (a short last word padded with zeros).</summary>
    private static uint Ot27Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (var k = 0; k < 4; k++) word = (word << 8) | (i + k < data.Length ? data[i + k] : (byte)0);
            sum += word;
        }
        return sum;
    }

    /// <summary>/FontBBox of the font descriptor of a saved Type 0 font.</summary>
    private static long[] Ot27FontBBox(PdfDictionary type0)
    {
        var cid = (PdfDictionary)type0.GetArray(PdfName.Get("DescendantFonts"))!.Get(0);
        var box = cid.GetDictionary(PdfName.Get("FontDescriptor"))!.GetArray(PdfName.Get("FontBBox"))!;
        return [.. Enumerable.Range(0, box.Count).Select(i => ((PdfInteger)box.Get(i)).Value)];
    }

    /// <summary>The head box (xMin, yMin, xMax, yMax) of a font in thousandths of an em, rounded.</summary>
    private static long[] Ot27HeadBox1000(byte[] font)
    {
        var head = Ot27Table(font, "head");
        var units = 1000.0 / Ot27U16(head, 18);
        return [.. Enumerable.Range(0, 4).Select(i => (long)Math.Round(Ot27I16(head, 36 + 2 * i) * units))];
    }

    private static byte[] Ot27GlyphlessTable(string tag) => Ot27Table(OcrTextLayer.Glyphless, tag);

    // ------------------------------------------------------------ OT 22 gvar

    /// <summary>
    /// OT 22 sentences 1–4, OT 22.2 sentence 13, OT 22.6 sentences 1–8 and OT 22.8 sentences 17–19 (reader, today's behaviour; question 2): gvar is not
    /// read — Liberation Sans with fvar (wght) and a gvar with deltas for „A“ (point 0), for three points of the first contour of „O“ and data of size
    /// 0 for „B“: all three outlines and the advances are those of glyf and hmtx (the default instance); a subset has no gvar.
    /// </summary>
    [Fact]
    public void Ot27_GvarWithDeltas_OutlinesAndAdvancesOfTheDefaultInstance()
    {
        var plain = Ot27Sans.Value;
        var (a, b, o) = (Ot27Glyph('A'), Ot27Glyph('B'), Ot27Glyph('O'));
        var gvar = Ot27Gvar(plain.GlyphCount, new Dictionary<int, byte[]>
        {
            [a] = Ot27GlyphDeltas(1, [0], 100),
            [o] = Ot27GlyphDeltas(1, [0, 1, 2], -60),
        });
        Assert.Equal(Ot27U32(gvar, 20 + b * 4), Ot27U32(gvar, 20 + (b + 1) * 4));   // „B“: no variation data
        var variableFile = Ot27SansWith(("fvar", Ot27Fvar()), ("gvar", gvar));
        var variable = OpenTypeFont.Parse(variableFile);
        Assert.True(variable.HasTable("fvar") && variable.HasTable("gvar"));
        foreach (var g in (int[])[a, b, o])
        {
            Assert.True(Ot27SameOutline(plain.Outline(g), variable.Outline(g)), $"Glyphe {g}");
            Assert.Equal(plain.AdvanceWidth(g), variable.AdvanceWidth(g));
        }
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(variableFile, [a])).HasTable("gvar"));
    }

    /// <summary>
    /// OT 22.1 sentence 18 (writer): PDF-Knead builds no variable fonts — a subset of Liberation Sans with fvar, gvar and HVAR, a subset keeping
    /// all other tables and a scrubbed font have none of the three (FG 1159; before the last two kept all three).
    /// </summary>
    [Fact]
    public void Ot27_SubsetAllTablesAndScrub_DropFvarGvarHvar()
    {
        var count = Ot27Sans.Value.GlyphCount;
        var font = Ot27SansWith(("fvar", Ot27Fvar()), ("gvar", Ot27Gvar(count, new Dictionary<int, byte[]>())), ("HVAR", Ot27Hvar(count, false)));
        var glyphs = new[] { Ot27Glyph('A') };
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(font, glyphs));
        var all = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(font, glyphs, keepAllTables: true));
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, glyphs));
        foreach (var tag in (string[])["fvar", "gvar", "HVAR"])
        {
            Assert.False(subset.HasTable(tag), tag);
            Assert.False(all.HasTable(tag), tag);
            Assert.False(scrubbed.HasTable(tag), tag);
        }
    }

    /// <summary>
    /// OT 22.3 sentences 1–7 (reader, today's behaviour): gvar is not checked against fvar — a gvar with two axes in a font whose fvar has one: the
    /// font parses without an exception and draws the outlines of glyf.
    /// </summary>
    [Fact]
    public void Ot27_GvarAxisCountUnlikeFvar_ParsesToTheDefaultInstance()
    {
        var plain = Ot27Sans.Value;
        var a = Ot27Glyph('A');
        var gvar = Ot27Gvar(plain.GlyphCount, new Dictionary<int, byte[]> { [a] = Ot27GlyphDeltas(2, [0], 100) }, axisCount: 2);
        var font = OpenTypeFont.Parse(Ot27SansWith(("fvar", Ot27Fvar()), ("gvar", gvar)));
        Assert.Equal(2, Ot27U16(font.TableData("gvar"), 4));
        Assert.Equal(1, Ot27U16(font.TableData("fvar"), 8));
        Assert.True(Ot27SameOutline(plain.Outline(a), font.Outline(a)));
    }

    /// <summary>
    /// OT 22.3 sentences 8–12 (writer): no written font carries a gvar that could stop fitting — Liberation Sans with a gvar whose glyphCount is
    /// that of maxp: a subset keeping all tables and the scrubbed font keep the glyph count of maxp and have no gvar (FG 1159; before gvar stayed
    /// byte for byte); the PDF/A rebuild with a glyph appended has none either.
    /// </summary>
    [Fact]
    public void Ot27_NoWriterKeepsGvar_RebuildWithAppendedGlyphNeither()
    {
        var count = Ot27Sans.Value.GlyphCount;
        var a = Ot27Glyph('A');
        var gvar = Ot27Gvar(count, new Dictionary<int, byte[]> { [a] = Ot27GlyphDeltas(1, [0], 100) });
        Assert.Equal(count, Ot27U16(gvar, 12));
        var font = Ot27SansWith(("fvar", Ot27Fvar()), ("gvar", gvar));
        foreach (var file in new[] { TrueTypeSubsetter.Subset(font, [a], keepAllTables: true), TrueTypeSubsetter.Scrub(font, [a]) })
        {
            Assert.Equal(count, Ot27U16(Ot27Table(file, "maxp"), 4));
            Assert.False(OpenTypeFont.Parse(file).HasTable("gvar"));
        }
        var rebuilt = OpenTypeFont.Parse(TrueTypeSubsetter.Rebuild(font, new Dictionary<int, int> { [a] = 1366 }, [(a, 1000)], null));
        Assert.Equal(count + 1, rebuilt.GlyphCount);
        Assert.False(rebuilt.HasTable("gvar"));
    }

    /// <summary>
    /// OT 22.7 sentences 16–19 and 20–23 (reader, today's behaviour): composite glyphs get no deltas — a composite glyph of „A“ at the offset
    /// (100, 0) and „B“ placed by point numbers, with gvar deltas for both components: the outline is the same as without gvar.
    /// </summary>
    [Fact]
    public void Ot27_CompositeGlyphWithGvarDeltas_ComponentsWhereGlyfPutsThem()
    {
        var (a, b, umlaut) = (Ot27Glyph('A'), Ot27Glyph('B'), Ot27Glyph('Ä'));
        var composite = Ot27Composite(a, b);
        var without = OpenTypeFont.Parse(Ot27SansWithGlyph(umlaut, composite));
        var gvar = Ot27Gvar(Ot27Sans.Value.GlyphCount, new Dictionary<int, byte[]> { [umlaut] = Ot27GlyphDeltas(1, [0, 1], 80) });
        var with = OpenTypeFont.Parse(Ot27SansWithGlyph(umlaut, composite, ("fvar", Ot27Fvar()), ("gvar", gvar)));
        Assert.False(without.Outline(umlaut).IsEmpty);
        Assert.True(Ot27SameOutline(without.Outline(umlaut), with.Outline(umlaut)));
    }

    /// <summary>
    /// OT 22.7 sentences 27–32 with OT 19.4 and OT 25 sentences 5 and 15 (reader): the phantom points of a composite glyph come from the component
    /// with USE_MY_METRICS, its advance for layout from hmtx — a composite glyph at the place of „Ä“ whose only component „W“ carries the flag has
    /// the hmtx advance of „Ä“, not the one of „W“ (since docs/FEHLERGEDAECHTNIS.md, No. 1179; No. 1163 had taken that of „W“: the shaper set
    /// wider than the reference shaper, and the reference readers take hmtx); in the default instance, without variations.
    /// </summary>
    [Fact]
    public void Ot27_UseMyMetrics_AdvanceFromHmtx_NotFromTheComponent()
    {
        var (w, umlaut) = (Ot27Glyph('W'), Ot27Glyph('Ä'));
        var font = OpenTypeFont.Parse(Ot27SansWithGlyph(umlaut, Ot27Composite(w, firstFlags: 0x0200)));
        Assert.NotEqual(Ot27Sans.Value.AdvanceWidth(w), Ot27Sans.Value.AdvanceWidth(umlaut));
        Assert.False(font.Outline(umlaut).IsEmpty);
        Assert.Equal(Ot27Sans.Value.AdvanceWidth(umlaut), font.AdvanceWidth(umlaut));
    }

    // ------------------------------------------------------------ OT 23 head

    /// <summary>
    /// OT 23 sentences 2–3 and 43–45 (writer, FG 1166): the font of the text recognition has no contours, so its head box is 0 for all four
    /// values; its units per em are a power of two (1024).
    /// </summary>
    [Fact]
    public void Ot27_GlyphlessFont_HeadBoxZero_UnitsPerEm1024()
    {
        var head = Ot27GlyphlessTable("head");
        Assert.True(OpenTypeFont.Parse(OcrTextLayer.Glyphless).Outline(1).IsEmpty);
        Assert.Equal(new[] { 0, 0, 0, 0 }, Enumerable.Range(0, 4).Select(i => Ot27I16(head, 36 + 2 * i)).ToArray());
        Assert.Equal(1024, Ot27U16(head, 18));
    }

    /// <summary>OT 23 sentences 4–7 (writer): head of the text recognition font has majorVersion 1 and minorVersion 0.</summary>
    [Fact]
    public void Ot27_GlyphlessFont_HeadVersion1Dot0()
    {
        var head = Ot27GlyphlessTable("head");
        Assert.Equal((1, 0), (Ot27U16(head, 0), Ot27U16(head, 2)));
    }

    /// <summary>
    /// OT 23 sentence 9 (reader and writer): checksumAdjustment is not read — a collection whose (only) font has a wrong one parses intact with its
    /// outlines; the font taken out of it sums to 0xB1B0AFBA.
    /// </summary>
    [Fact]
    public void Ot27_CollectionWithWrongCheckSumAdjustment_ReadAndRecomputedWhenTakenOut()
    {
        var collection = Ot27CollectionOf(Ot27SansFile.Value);
        var head = (int)Ot27U32(collection, 16 + 12 + 16 * Enumerable.Range(0, Ot27U16(Ot27SansFile.Value, 4))
            .First(i => Encoding.ASCII.GetString(Ot27SansFile.Value, 12 + 16 * i, 4) == "head") + 8);
        BinaryPrimitives.WriteUInt32BigEndian(collection.AsSpan(head + 8), 0x12345678);
        var font = OpenTypeFont.Parse(collection);
        var a = Ot27Glyph('A');
        Assert.True(font.Intact);
        Assert.True(Ot27SameOutline(Ot27Sans.Value.Outline(a), font.Outline(a)));
        Assert.Equal(0xB1B0AFBAu, Ot27Checksum(TrueTypeSubsetter.FirstOfCollection(collection)));
    }

    /// <summary>OT 23 sentence 10 (writer): head of the text recognition font has magicNumber 0x5F0F3CF5.</summary>
    [Fact]
    public void Ot27_GlyphlessFont_HeadMagicNumber()
    {
        Assert.Equal(0x5F0F3CF5u, Ot27U32(Ot27GlyphlessTable("head"), 12));
    }

    /// <summary>OT 23 sentences 11–19 (writer): head flags of the text recognition font are 0x000B (bits 0, 1, 3; bits 5–10 and 15 clear).</summary>
    [Fact]
    public void Ot27_GlyphlessFont_HeadFlags0x000B()
    {
        var flags = Ot27U16(Ot27GlyphlessTable("head"), 16);
        Assert.Equal(0x000B, flags);
        Assert.Equal(0, flags & 0x87E0);
    }

    /// <summary>
    /// OT 23 sentences 25–28 (writer): /FontBBox of a new font is the head box in thousandths of an em — Liberation Sans (2048 units per em) typed
    /// and saved: xMin … yMax × 1000 / 2048, rounded.
    /// </summary>
    [Fact]
    public void Ot27_NewFont_FontBBoxIsTheHeadBoxPerThousand()
    {
        var face = ElementFace.For(ElementFont.Sans);
        var saved = face.SavedFont(new Dictionary<int, string> { [Ot27Glyph('A')] = "A" });
        Assert.Equal(2048, face.Font.UnitsPerEm);
        Assert.Equal(Ot27HeadBox1000(Ot27SansFile.Value), Ot27FontBBox(saved));
    }

    /// <summary>
    /// OT 23 sentences 29 and 46–48 (writer): macStyle and fsSelection agree — the text recognition font has macStyle 0 and fsSelection without
    /// bit 0 (italic) and bit 5 (bold).
    /// </summary>
    [Fact]
    public void Ot27_GlyphlessFont_MacStyleZero_FsSelectionNeitherItalicNorBold()
    {
        Assert.Equal(0, Ot27U16(Ot27GlyphlessTable("head"), 44));
        Assert.Equal(0, Ot27U16(Ot27GlyphlessTable("OS/2"), 62) & 0x21);
    }

    /// <summary>OT 23 sentences 31 and 34–40 (writer): fontDirectionHint of the text recognition font is 2.</summary>
    [Fact]
    public void Ot27_GlyphlessFont_FontDirectionHint2()
    {
        Assert.Equal(2, Ot27I16(Ot27GlyphlessTable("head"), 48));
    }

    /// <summary>
    /// OT 23 sentences 51–55 and OT 77.3.3 sentences 14–19 (reader): the origin is the first phantom point pp1 = xMin − lsb, not x = 0 of the
    /// glyf coordinates, whatever bit 1 of the head flags says — as the three reference readers draw it (measured, probes p25a–f:
    /// docs/FEHLERGEDAECHTNIS.md, No. 1163; before the outline stayed at xMin). „A“ of Liberation Sans with its hmtx lsb set 50 below its xMin:
    /// the outline is the same moved 50 to the left (pp1 = 50), so that it begins at the lsb — with bit 1 clear and set; with lsb = xMin (as in
    /// the font itself) it stays where it is.
    /// </summary>
    [Fact]
    public void Ot27_LsbUnlikeXMin_OriginAtPp1WhateverFlagBit1()
    {
        var a = Ot27Glyph('A');
        var xMin = Ot27I16(Ot27GlyphData(Ot27Sans.Value, a), 2);
        Assert.Equal(xMin, Ot27Sans.Value.Outline(a).Bounds.Left, 3);
        var hmtx = Ot27Table(Ot27SansFile.Value, "hmtx");
        Assert.True(a < Ot27U16(Ot27Table(Ot27SansFile.Value, "hhea"), 34));
        Assert.Equal(xMin, Ot27I16(hmtx, a * 4 + 2));
        BinaryPrimitives.WriteInt16BigEndian(hmtx.AsSpan(a * 4 + 2), (short)(xMin - 50));
        foreach (var bit1 in new[] { false, true })
        {
            var head = Ot27Table(Ot27SansFile.Value, "head");
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(16), (ushort)(bit1 ? Ot27U16(head, 16) | 0x0002 : Ot27U16(head, 16) & ~0x0002));
            var font = OpenTypeFont.Parse(Ot27SansWith(("hmtx", hmtx), ("head", head)));
            var original = Ot27Sans.Value.Outline(a);
            Assert.Equal(original.Verbs, font.Outline(a).Verbs);
            for (var i = 0; i < original.Points.Count; i++) Assert.Equal(original.Points[i] - (i % 2 == 0 ? 50 : 0), font.Outline(a).Points[i], 0.001f);
            Assert.Equal(xMin - 50, font.Outline(a).Bounds.Left, 3);
        }
    }

    /// <summary>
    /// OT 23 sentences 51–55 with OT 19.4 (reader): a composite glyph has its origin at xMin of its own glyf header minus its own lsb (not at
    /// the box of its components), and its components are not moved by their own side bearings — as the three reference readers draw it
    /// (probes p25j, p25k, p25l). Glyph 3 places the box (0–100) at 0 and glyph 2 (a box 300–400 with lsb 0) at 1000: with header xMin 0 and lsb
    /// −300 everything moves 300 to the left; with header xMin 100 and lsb 0 by 100; the second component stays at 1300–1400 relative to the first.
    /// </summary>
    [Fact]
    public void Ot27_CompositeOrigin_OwnHeaderAndLsb_ComponentsNotMovedByTheirOwn()
    {
        Ot20Part[] parts = [new Ot20Part(Ot20Xy, 1, 0, 0), new Ot20Part(Ot20Xy, 2, 1000, 0)];
        var far = Ot20Simple([Ot20Rect(300, 0, 400, 50)]);
        var withHeader = Ot20Composite(parts);
        BinaryPrimitives.WriteInt16BigEndian(withHeader.AsSpan(2), 100);
        var plain = OpenTypeFont.Parse(Ot20Font([Ot20Box, Ot20Box, far, Ot20Composite(parts)], bearings: [0, 0, 0, 0]));
        var lsb = OpenTypeFont.Parse(Ot20Font([Ot20Box, Ot20Box, far, Ot20Composite(parts)], bearings: [0, 0, 0, -300]));
        var header = OpenTypeFont.Parse(Ot20Font([Ot20Box, Ot20Box, far, withHeader], bearings: [0, 0, 0, 0]));
        Assert.Equal(new RectD(0, 0, 1400, 50), plain.Outline(3).Bounds);
        Assert.Equal(new RectD(-300, 0, 1100, 50), lsb.Outline(3).Bounds);
        Assert.Equal(new RectD(-100, 0, 1300, 50), header.Outline(3).Bounds);
    }

    /// <summary>
    /// OT 23 sentences 57–60 (writer): a new font from a variable font — /FontBBox is the head box, and the glyphs are those of the default
    /// instance.
    /// </summary>
    [Fact]
    public void Ot27_NewFontFromVariableFont_HeadBoxAndDefaultInstance()
    {
        var a = Ot27Glyph('A');
        var variable = Ot27SansWith(("fvar", Ot27Fvar()),
            ("gvar", Ot27Gvar(Ot27Sans.Value.GlyphCount, new Dictionary<int, byte[]> { [a] = Ot27GlyphDeltas(1, [0], 100) })));
        var face = ElementFace.For("m7-ot27-variable-liberation-sans", () => OpenTypeFont.Parse(variable), false, false, false);
        var saved = face.SavedFont(new Dictionary<int, string> { [a] = "A" });
        Assert.Equal(Ot27HeadBox1000(variable), Ot27FontBBox(saved));
        Assert.True(Ot27SameOutline(Ot27Sans.Value.Outline(a), face.Font.Outline(a)));
    }

    // ------------------------------------------------------------ OT 24 hhea

    /// <summary>
    /// The limits of hhea (or vhea) as the tables of <paramref name="font"/> give them (OT 24): largest advance of hmtx; over the glyphs with
    /// contours (from the glyf header) the smallest side bearing, the smallest advance − (bearing + extent) and the largest bearing + extent.
    /// </summary>
    private static (int AdvanceMax, int MinBearing, int MinOther, int MaxExtent) Ot27Limits(byte[] font)
    {
        var parsed = OpenTypeFont.Parse(font);
        byte[] hhea = parsed.TableData("hhea").ToArray(), hmtx = parsed.TableData("hmtx").ToArray(), glyf = parsed.TableData("glyf").ToArray(),
            loca = parsed.TableData("loca").ToArray();
        var full = Ot27U16(hhea, 34);
        var advanceMax = Enumerable.Range(0, full).Max(g => Ot27U16(hmtx, g * 4));
        int minBearing = int.MaxValue, minOther = int.MaxValue, maxExtent = int.MinValue;
        for (var g = 0; g < parsed.GlyphCount; g++)
        {
            int start = (int)Ot27U32(loca, g * 4), end = (int)Ot27U32(loca, g * 4 + 4);
            if (end - start < 10 || Ot27I16(glyf, start) == 0) continue;
            var advance = Ot27U16(hmtx, Math.Min(g, full - 1) * 4);
            var bearing = g < full ? Ot27I16(hmtx, g * 4 + 2) : Ot27I16(hmtx, full * 4 + (g - full) * 2);
            var extent = Ot27I16(glyf, start + 6) - Ot27I16(glyf, start + 2);
            minBearing = Math.Min(minBearing, bearing);
            minOther = Math.Min(minOther, advance - (bearing + extent));
            maxExtent = Math.Max(maxExtent, bearing + extent);
        }
        return minBearing == int.MaxValue ? (advanceMax, 0, 0, 0) : (advanceMax, minBearing, minOther, maxExtent);
    }

    /// <summary>
    /// OT 24 sentences 2–3 and 11–14 (writer): advanceWidthMax, minLeftSideBearing, minRightSideBearing and xMaxExtent are computed anew from the
    /// glyphs the written font keeps — Liberation Sans as a subset, a subset keeping all tables, a scrubbed copy (each with „Te“) and rebuilt
    /// with only „A“ at the advance 3000: hhea gives the values of its own hmtx and glyf (FG 1159; before the source's values stayed, which
    /// told of glyphs no longer in the font).
    /// </summary>
    [Fact]
    public void Ot27_SubsetScrubRebuild_RecomputeTheSideBearingExtremes()
    {
        var source = Ot27SansFile.Value;
        int[] te = [Ot27Glyph('T'), Ot27Glyph('e')];
        foreach (var written in (byte[][])[TrueTypeSubsetter.Subset(source, te), TrueTypeSubsetter.Subset(source, te, keepAllTables: true),
                     TrueTypeSubsetter.Scrub(source, te), TrueTypeSubsetter.Rebuild(source, new Dictionary<int, int> { [Ot27Glyph('A')] = 3000 }, [], null)])
        {
            var hhea = Ot27Table(written, "hhea");
            var (advanceMax, minBearing, minOther, maxExtent) = Ot27Limits(written);
            Assert.Equal((advanceMax, minBearing, minOther, maxExtent), (Ot27U16(hhea, 10), Ot27I16(hhea, 12), Ot27I16(hhea, 14), Ot27I16(hhea, 16)));
            Assert.NotEqual(Ot27Table(source, "hhea")[12..18], hhea[12..18]);
        }
    }

    /// <summary>OT 24 sentences 4 and 19–22 (writer): the reserved fields and metricDataFormat of hhea are 0 in the text recognition font.</summary>
    [Fact]
    public void Ot27_GlyphlessFont_HheaReservedFieldsZero()
    {
        Assert.All(Ot27GlyphlessTable("hhea")[24..34], b => Assert.Equal(0, b));
    }

    /// <summary>OT 24 sentences 5–7 (writer): hhea of the text recognition font has version 1.0.</summary>
    [Fact]
    public void Ot27_GlyphlessFont_HheaVersion1Dot0()
    {
        Assert.Equal(0x00010000u, Ot27U32(Ot27GlyphlessTable("hhea"), 0));
    }

    /// <summary>
    /// OT 24 sentences 8–10 (reader): ascender and descender come from hhea — Liberation Sans: <c>Ascent</c> and <c>Descent</c> are the hhea
    /// values, and the element font's ascent is the ascender per em.
    /// </summary>
    [Fact]
    public void Ot27_AscentAndDescent_FromHhea()
    {
        var hhea = Ot27Table(Ot27SansFile.Value, "hhea");
        Assert.Equal(Ot27I16(hhea, 4), Ot27Sans.Value.Ascent);
        Assert.Equal(Ot27I16(hhea, 6), Ot27Sans.Value.Descent);
        Assert.Equal(Ot27I16(hhea, 4) / 2048.0, ElementFace.For(ElementFont.Sans).Ascent, 9);
    }

    /// <summary>OT 24 sentence 11 (writer): the rebuilt font's advanceWidthMax is its largest advance — advances 300 and 900: 900.</summary>
    [Fact]
    public void Ot27_Rebuild_AdvanceWidthMaxIsTheLargestAdvance()
    {
        var rebuilt = TrueTypeSubsetter.Rebuild(Ot27SansFile.Value, new Dictionary<int, int> { [Ot27Glyph('A')] = 300, [Ot27Glyph('B')] = 900 }, [], null);
        Assert.Equal(900, Ot27U16(Ot27Table(rebuilt, "hhea"), 10));
    }

    /// <summary>
    /// OT 24 sentences 25–28 (reader; docs/FEHLERGEDAECHTNIS.md, No. 1162): with USE_TYPO_METRICS typed text takes sTypoAscender of OS/2, not the
    /// ascender of hhea — a font of 1000 units per em with hhea ascender 900 and sTypoAscender 800 (USE_TYPO_METRICS set): ascent 0.8.
    /// </summary>
    [Fact]
    public void Ot27_TypedText_AscentFromTypoAscenderWithUseTypoMetrics()
    {
        var head = Ot27Table(Ot27SansFile.Value, "head");
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), 1000);
        var hhea = Ot27Table(Ot27SansFile.Value, "hhea");
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(4), 900);
        var os2 = Ot27Table(Ot27SansFile.Value, "OS/2");
        BinaryPrimitives.WriteInt16BigEndian(os2.AsSpan(68), 800);
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(62), (ushort)(Ot27U16(os2, 62) | 0x80));
        var font = Ot27SansWith(("head", head), ("hhea", hhea), ("OS/2", os2));
        var face = ElementFace.For("m7-ot27-hhea-900-typo-800", () => OpenTypeFont.Parse(font), false, false, false);
        Assert.Equal(0.8, face.Ascent, 9);
    }
}
