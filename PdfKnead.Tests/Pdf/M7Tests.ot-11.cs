using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Konformitätsabgleich M7, package ot-11 (OpenType, CFF2 table: variations 8.11, dependencies on other tables 8.12, the example table 8.13;
/// cmap 9 and 9.1): the tests the sentence-by-sentence drafts named as missing. PDF-Knead reads CFF2 in the default instance (question 1,
/// docs/FEHLERGEDAECHTNIS.md, No. 1158) and no variation table (question 2). The CFF2 builder and the assertions are in the file of package ot-08.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package ot-11)

    private static readonly byte[] LiberationOt11 = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");

    private static string RepoOt11([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    /// <summary>
    /// A CFF2 table shaped like the example of 8.13 (built here, not copied): one axis with two regions and one ItemVariationData naming both
    /// (k = 2), two charstrings that each call local subroutine 0 („-107 callsubr“), a Private DICT with blended and plain hint values, and the
    /// subroutine drawing a rectangle 50 … 550 × 0 … 500 whose x values blend.
    /// </summary>
    internal static byte[] ExampleCff2Ot11(Cff2VariationsOt08? variations = null, string? glyph = null)
    {
        var privateDict = DictOt08("-15 15 500 15 -5 0 0 0 5 0 0 0 4 blend BlueValues -250 10 5 0 0 0 2 blend OtherBlues -15 15 FamilyBlues " +
                                   "-250 10 FamilyOtherBlues 0.0375 BlueScale 0 BlueFuzz 50 5 0 1 blend StdHW 80 10 5 1 blend StdVW " +
                                   "50 10 5 0 0 0 2 blend StemSnapH 80 12 10 5 0 0 2 blend StemSnapV");
        var subr = CsOt08("50 50 100 1 blend 0 rmoveto 500 -100 -200 1 blend hlineto 500 vlineto -500 100 200 1 blend hlineto");
        var call = CsOt08(glyph ?? "-107 callsubr");
        return Cff2Ot08(new([call, call], Privates: [new(privateDict, [subr])], Variations: variations ?? TwoRegionsOt08));
    }

    /// <summary>MVAR with no value records but an ItemVariationStore with the regions of the example.</summary>
    private static byte[] MvarOt11()
    {
        var store = VariationStoreOt08(TwoRegionsOt08);
        byte[] header = [0, 1, 0, 0, 0, 0, 0, 8, 0, 0, 0, 12];   // version 1.0, reserved, record size 8, no records, store at 12
        return [.. header, .. store];
    }

    /// <summary>gvar with one axis and no variation data for <paramref name="glyphs"/> glyphs.</summary>
    private static byte[] EmptyGvarOt11(int glyphs)
    {
        var g = new byte[20 + (glyphs + 1) * 2];
        BinaryPrimitives.WriteUInt16BigEndian(g, 1);
        BinaryPrimitives.WriteUInt16BigEndian(g.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(g.AsSpan(12), (ushort)glyphs);
        BinaryPrimitives.WriteUInt32BigEndian(g.AsSpan(16), (uint)g.Length);
        BinaryPrimitives.WriteUInt32BigEndian(g.AsSpan(8), (uint)g.Length);
        return g;
    }

    /// <summary>HVAR without mappings (the glyph index is the item) whose ItemVariationStore gives glyph <paramref name="glyph"/> a delta of 100.</summary>
    private static byte[] HvarOt11(int glyphs, int glyph)
    {
        var store = VariationStoreOt08(new([(0, 1, 1)], [[0]], glyphs));
        // the deltas follow the store header (12 bytes), the region list (10) and the ItemVariationData header with its one index (8)
        store[30 + glyph] = 100;
        byte[] header = [0, 1, 0, 0, 0, 0, 0, 20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        return [.. header, .. store];
    }

    /// <summary>Points of a simple glyph in glyf (the last end point plus one).</summary>
    private static int PointCountOt11(OpenTypeFont font, int glyph)
    {
        var loca = font.TableData("loca");
        var start = font.LongLoca ? (int)BinaryPrimitives.ReadUInt32BigEndian(loca[(glyph * 4)..]) : BinaryPrimitives.ReadUInt16BigEndian(loca[(glyph * 2)..]) * 2;
        var glyf = font.TableData("glyf")[start..];
        var contours = BinaryPrimitives.ReadInt16BigEndian(glyf);
        Assert.True(contours > 0);
        return BinaryPrimitives.ReadUInt16BigEndian(glyf[(10 + 2 * (contours - 1))..]) + 1;
    }

    /// <summary>
    /// gvar with one axis whose only data is for <paramref name="glyph"/>: one tuple (peak 1.0) moving its point 0 and its advance phantom point
    /// (number of points + 1) by 100 in x — the other points get no delta of their own (inferred in a variable font).
    /// </summary>
    private static byte[] GvarOt11(int glyphs, int glyph, int points)
    {
        byte[] data = [0, 1, 0, 10, 0, 8, 0xA0, 0, 0x40, 0,        // one tuple, data at 10; size 8, embedded peak + private points, peak 1.0
            2, 1, 0, (byte)(points + 1),                           // two points as bytes: 0 and points + 1 (delta coded)
            1, 100, 100,                                           // x deltas
            0x81];                                                 // y deltas: two zeros
        var header = 20 + (glyphs + 1) * 4;
        var g = new byte[header + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(g, 1);
        BinaryPrimitives.WriteUInt16BigEndian(g.AsSpan(4), 1);              // one axis
        BinaryPrimitives.WriteUInt32BigEndian(g.AsSpan(8), (uint)header);   // shared tuples (none)
        BinaryPrimitives.WriteUInt16BigEndian(g.AsSpan(12), (ushort)glyphs);
        BinaryPrimitives.WriteUInt16BigEndian(g.AsSpan(14), 1);             // long offsets
        BinaryPrimitives.WriteUInt32BigEndian(g.AsSpan(16), (uint)header);
        for (var i = 0; i <= glyphs; i++)
            BinaryPrimitives.WriteUInt32BigEndian(g.AsSpan(20 + i * 4), (uint)(i <= glyph ? 0 : data.Length));
        data.CopyTo(g, header);
        return g;
    }

    /// <summary>A cmap of the given subtables (records in the order given, which the caller keeps sorted).</summary>
    private static byte[] CmapOt11(params (int Platform, int Encoding, byte[] Subtable)[] subtables)
    {
        var result = new List<byte>();
        void U16(int v) => result.AddRange([(byte)(v >> 8), (byte)v]);
        U16(0);
        U16(subtables.Length);
        var at = 4 + 8 * subtables.Length;
        foreach (var (platform, encoding, subtable) in subtables)
        {
            U16(platform);
            U16(encoding);
            result.AddRange([(byte)(at >> 24), (byte)(at >> 16), (byte)(at >> 8), (byte)at]);
            at += subtable.Length;
        }
        foreach (var s in subtables) result.AddRange(s.Subtable);
        return [.. result];
    }

    private static byte[] BeOt11(params (int Value, int Size)[] fields)
    {
        var result = new List<byte>();
        foreach (var (value, size) in fields)
            for (var k = size - 1; k >= 0; k--) result.Add((byte)(value >> (8 * k)));
        return [.. result];
    }

    /// <summary>cmap format 12 or 13 with one group.</summary>
    private static byte[] GroupCmapOt11(int format, int first, int last, int glyph) =>
        BeOt11((format, 2), (0, 2), (28, 4), (0, 4), (1, 4), (first, 4), (last, 4), (glyph, 4));

    /// <summary>cmap format 10 (trimmed array of 32-bit codes) with consecutive glyphs from <paramref name="glyph"/>.</summary>
    private static byte[] Format10Ot11(int first, int count, int glyph) =>
        [.. BeOt11((10, 2), (0, 2), (20 + 2 * count, 4), (0, 4), (first, 4), (count, 4)), .. Enumerable.Range(glyph, count).SelectMany(g => BeOt11((g, 2)))];

    /// <summary>cmap format 2 (high-byte mapping): two-byte code 0x81 0x40 → <paramref name="glyph"/>, nothing else.</summary>
    private static byte[] Format2Ot11(int glyph)
    {
        var keys = new int[256];
        keys[0x81] = 8;                                             // sub-header 1
        return [.. BeOt11((2, 2), (536, 2), (0, 2)), .. keys.SelectMany(k => BeOt11((k, 2))),
            .. BeOt11((0, 2), (0, 2), (0, 2), (0, 2)),               // sub-header 0: single bytes, none mapped
            .. BeOt11((0x40, 2), (1, 2), (0, 2), (2, 2)),            // sub-header 1: second byte 0x40, one entry, its array right after
            .. BeOt11((glyph, 2))];
    }

    /// <summary>cmap format 8 (mixed 16/32-bit) with one group of three supplementary codes.</summary>
    private static byte[] Format8Ot11(int first, int glyph)
    {
        var is32 = new byte[8192];
        var high = 0xD800 + ((first - 0x10000) >> 10);
        is32[high / 8] |= (byte)(0x80 >> (high % 8));
        return [.. BeOt11((8, 2), (0, 2), (12 + 8192 + 4 + 12, 4), (0, 4)), .. is32, .. BeOt11((1, 4), (first, 4), (first + 2, 4), (glyph, 4))];
    }

    /// <summary>cmap format 14 with one selector (U+FE0F) and one non-default mapping U+2764 → <paramref name="glyph"/>.</summary>
    private static byte[] Format14Ot11(int glyph) =>
        BeOt11((14, 2), (30, 4), (1, 4), (0xFE0F, 3), (0, 4), (21, 4), (1, 4), (0x2764, 3), (glyph, 2));

    /// <summary>The operators (one or two bytes, 12 x as 1200 + x) of a Type 2 charstring, numbers skipped; masks after hint operators skipped.</summary>
    private static List<int> OperatorsOt11(ReadOnlySpan<byte> code)
    {
        var ops = new List<int>();
        int operands = 0, stems = 0;
        for (var i = 0; i < code.Length;)
        {
            int b = code[i++];
            if (b is >= 32 and <= 246) operands++;
            else if (b is >= 247 and <= 254) { i++; operands++; }
            else if (b == 255) { i += 4; operands++; }
            else if (b == 28) { i += 2; operands++; }
            else if (b == 12) { ops.Add(1200 + code[i++]); operands = 0; }
            else
            {
                ops.Add(b);
                if (b is 1 or 3 or 18 or 23 or 19 or 20) stems += operands / 2;
                if (b is 19 or 20) i += (stems + 7) / 8;
                operands = 0;
            }
        }
        return ops;
    }

    /// <summary>A CFF (version 1) with one glyph after an empty .notdef, its Type 2 charstring given.</summary>
    private static byte[] OneGlyphCffOt11(byte[] glyph)
    {
        static byte[] Index(params byte[][] items)
        {
            if (items.Length == 0) return [0, 0];
            var result = new List<byte> { (byte)(items.Length >> 8), (byte)items.Length, 4 };
            var offset = 1;
            void Offset(int v) => result.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
            Offset(offset);
            foreach (var item in items) Offset(offset += item.Length);
            foreach (var item in items) result.AddRange(item);
            return [.. result];
        }
        var name = Index("Probe"u8.ToArray());
        var topSize = Index(new byte[6]).Length;
        var charStringsAt = 4 + name.Length + topSize + 2 + 2;
        byte[] top = [29, (byte)(charStringsAt >> 24), (byte)(charStringsAt >> 16), (byte)(charStringsAt >> 8), (byte)charStringsAt, 17];
        return [1, 0, 4, 4, .. name, .. Index(top), .. Index(), .. Index(), .. Index([14], glyph)];
    }

    private static (float X, float Y) FirstPointOt11(GlyphOutline outline) => (outline.Points[0], outline.Points[1]);

    /// <summary>The rectangle 50 … 550 × 0 … 500 the example table draws in its default instance (both charstrings call the subroutine).</summary>
    private const string ExampleRectOt11 = "50 0 rmoveto 500 hlineto 500 vlineto -500 hlineto";

    /// <summary>
    /// The probes of ot-11 for the CFF2 theory: fonts „OTTO“ around CFF2 tables with variation data, and what each glyph draws in the default
    /// instance (each blend leaves its default values; vsindex chooses k).
    /// </summary>
    private static Cff2ProbeOt08 ProbeOt11(string name)
    {
        var fvar = new Dictionary<string, byte[]> { ["fvar"] = FvarOt08() };
        byte[] Font(byte[] cff2, Dictionary<string, byte[]>? extra = null, int glyphs = 2) => Cff2FontOt08(cff2, glyphs, extra: extra ?? fvar);
        Cff2ProbeOt08 Glyph(Cff2VariationsOt08? v, string glyph, string? drawn, string? privateDict = null) =>
            new(Font(Cff2Ot08(new([[], CsOt08(glyph)], Privates: [new(privateDict is null ? [] : DictOt08(privateDict))], Variations: v))), [drawn]);
        Cff2ProbeOt08 Example(byte[] font) => new(font, [ExampleRectOt11], ExampleRectOt11);
        const string line = "0 0 rmoveto 10 0 rlineto 0 10 rlineto";
        return name switch
        {
            "Example" => Example(Font(ExampleCff2Ot11())),
            // the default instance needs no fvar, and gvar or MVAR beside the table change nothing
            "ExampleWithoutFvar" => Example(Font(ExampleCff2Ot11(), new())),
            "ExampleWithGvar" => Example(Font(ExampleCff2Ot11(), new() { ["fvar"] = FvarOt08(), ["gvar"] = EmptyGvarOt11(2) })),
            "ExampleWithMvar" => Example(Font(ExampleCff2Ot11(), new() { ["fvar"] = FvarOt08(), ["MVAR"] = MvarOt11() })),
            // blend without VariationStore: invalid, the glyph is empty (as in common font engines)
            "NoVariationStoreButBlend" => new(Font(Cff2Ot08(new([[], CsOt08("0 0 rmoveto 10 5 1 blend 0 rlineto 0 10 rlineto")]))), [null]),
            "TwoDataSets" => Glyph(TwoDataSetsOt08, "1 vsindex 0 0 rmoveto 10 5 6 1 blend 0 rlineto 0 10 rlineto", line),
            // deltas in the ItemVariationData (itemCount 1) are not those of CFF2: ignored
            "ItemCountOne" => Example(Font(ExampleCff2Ot11(TwoRegionsOt08 with { ItemCount = 1 }))),
            // vsindex 0 in the charstring before vsindex 1 of the Private DICT: k = 1 for glyph 1, k = 2 for glyph 2
            "PrivateAndGlyphVsindex" => new(Font(Cff2Ot08(new([[], CsOt08("0 vsindex 0 0 rmoveto 10 5 1 blend 0 rlineto 0 10 rlineto"),
                CsOt08("0 0 rmoveto 10 5 6 1 blend 0 rlineto 0 10 rlineto")], Privates: [new(DictOt08("1 vsindex"))], Variations: TwoDataSetsOt08)), glyphs: 3),
                [line, line]),
            "BlendHlineto" => new(Font(Cff2Ot08(new([[], CsOt08("0 0 rmoveto 120 52 1 blend hlineto"), CsOt08("1 vsindex 0 0 rmoveto 120 52 36 1 blend hlineto")],
                Variations: TwoDataSetsOt08)), glyphs: 3), ["0 0 rmoveto 120 hlineto", "0 0 rmoveto 120 hlineto"]),
            "SplitBlends" => new(Font(Cff2Ot08(new([[], CsOt08("0 0 rmoveto 10 5 1 blend 20 7 1 blend rlineto"), CsOt08("0 0 rmoveto 10 20 5 7 2 blend rlineto")],
                Variations: OneRegionOt08)), glyphs: 3), ["0 0 rmoveto 10 20 rlineto", "0 0 rmoveto 10 20 rlineto"]),
            "NestedBlends" => Glyph(OneRegionOt08, "0 0 rmoveto 100 10 1 blend 4 1 blend hlineto", "0 0 rmoveto 100 hlineto"),
            "BlendFirstMoveto" => Glyph(OneRegionOt08, "10 20 5 0 2 blend rmoveto 100 0 rlineto 0 0 rmoveto 50 0 rlineto",
                "10 20 rmoveto 100 0 rlineto 0 0 rmoveto 50 0 rlineto"),
            _ => throw new ArgumentException(name),
        };
    }

    // ------------------------------------------------------------ 8.11 to 8.13

    /// <summary>
    /// OT 8.11.1 sentences 2–17, 8.11.2 sentences 1–36, 8.11.3 sentences 1–31, 8.11.5 sentences 1–13, 8.11.6 sentences 14–19, 8.12.3 sentence 1,
    /// 8.12.6, 8.12.7 and 8.13 sentences 11–114 (reader and writer, question 1): PDF-Knead reads CFF2 with its VariationStore (k of each
    /// ItemVariationData), vsindex and blend in the default instance — the table shaped like the example of 8.13 (two regions, k = 2, blended
    /// Private DICT values and a blended rectangle in a local subroutine) draws the rectangle 50 … 550 × 0 … 500 in both glyphs, with and without
    /// fvar, with gvar or MVAR beside it and with itemCount 1; two ItemVariationData with vsindex in the Private DICT and the charstring, and the
    /// blend patterns of 8.11.3, 8.11.5 and 8.11.6 (split, nested, on the first moveto) draw their default values; blend without VariationStore
    /// makes the glyph empty. The redaction writes a static CFF2 table without VariationStore, blend, vsindex and the tables of the variations.
    /// </summary>
    [Theory]
    [InlineData("Example")]                  // 8.11.1, 8.11.2 1–6/10–19, 8.11.3 1–4, 8.12.3 1, 8.12.6, 8.13 11–114
    [InlineData("ExampleWithoutFvar")]       // 8.12.6 1–5
    [InlineData("ExampleWithGvar")]          // 8.12.7 1–3
    [InlineData("ExampleWithMvar")]          // 8.11.2 35–36
    [InlineData("NoVariationStoreButBlend")] // 8.11.2 7
    [InlineData("TwoDataSets")]              // 8.11.2 20–23
    [InlineData("ItemCountOne")]             // 8.11.2 29–34
    [InlineData("PrivateAndGlyphVsindex")]   // 8.11.3 5–14
    [InlineData("BlendHlineto")]             // 8.11.3 15–31
    [InlineData("SplitBlends")]              // 8.11.5 1–6
    [InlineData("NestedBlends")]             // 8.11.5 11–13
    [InlineData("BlendFirstMoveto")]         // 8.11.6 14–19
    public void Cff2Variations_DefaultInstanceDrawn_ScrubbedStatic(string probe) => AssertCff2ReadOt08(ProbeOt11(probe));

    /// <summary>
    /// OT 8.13 sentences 1–10 (reader): the bytes of a CFF2 table (header 02 00 05 …) given as a bare FontFile3 are no font program —
    /// FontProgram.FromBytes recognises bare CFF only with major version 1 and returns null (the font is substituted by name; ISO 32000-2, 9.9
    /// knows CFF2 in no FontFile3 subtype); as the table „CFF2“ of a font they draw the example rectangle (not 'CFF ', not intact: a strict PDF
    /// reader knows no CFF2).
    /// </summary>
    [Fact]
    public void BareCff2Data_IsNoFontProgram()
    {
        var cff2 = ExampleCff2Ot11();
        Assert.Equal(new byte[] { 2, 0, 5 }, cff2[..3]);
        Assert.Null(FontProgram.FromBytes(cff2));
        var font = OpenTypeFont.Parse(Cff2FontOt08(cff2, 2));
        Assert.Null(font.Cff);
        Assert.False(font.Intact);
        SameOutlineOt08(ReferenceOt08(ExampleRectOt11), font.Outline(1), "Glyphe 1");
    }

    /// <summary>
    /// A CFF2 table with a rectangle for every glyph of a font (glyph 0 empty), the rectangle of glyph g 100 + g units wide — for replacing glyf in
    /// a real font.
    /// </summary>
    private static byte[] RectanglesCff2Ot11(int glyphs) =>
        Cff2Ot08(new([[], .. Enumerable.Range(1, glyphs - 1).Select(g => CsOt08($"50 0 rmoveto {100 + g} 0 rlineto 0 600 rlineto"))]));

    /// <summary>
    /// OT 8.11.1 sentences 9–15 (reader, today, for comparison): in a charstring of the table „CFF “ (Type 2) operator 16, blend in CFF2, is
    /// reserved — it clears the stack: „50 0 16 rmoveto 10 0 rlineto“ moves to (0, 0), not to (50, 0), and the line ends at (10, 0).
    /// </summary>
    [Fact]
    public void CffCharString_Operator16_ClearsTheStack()
    {
        var outline = CffFont.Parse(OneGlyphCffOt11(CsOt08("50 0 0x10 rmoveto 10 0 rlineto endchar"))).Outline(1);
        Assert.False(outline.IsEmpty);
        Assert.Equal((0f, 0f), FirstPointOt11(outline));
        Assert.Equal(0, outline.Bounds.Left, 3);
        Assert.Equal(10, outline.Bounds.Right, 3);
    }

    /// <summary>
    /// OT 8.11.4 sentences 1–3 (reader): stem hints only serve a hinting rasterizer — a Type 2 charstring with „10 20 15 20 hstem“ before its path
    /// gives the same points and verbs as without the hints.
    /// </summary>
    [Fact]
    public void CffCharString_StemHints_LeaveTheOutlineAlone()
    {
        var with = CffFont.Parse(OneGlyphCffOt11(CsOt08("10 20 15 20 hstem 0 0 rmoveto 100 0 rlineto endchar"))).Outline(1);
        var without = CffFont.Parse(OneGlyphCffOt11(CsOt08("0 0 rmoveto 100 0 rlineto endchar"))).Outline(1);
        Assert.False(with.IsEmpty);
        Assert.Equal(without.Points.ToArray(), with.Points.ToArray());
        Assert.Equal(without.Verbs.ToArray(), with.Verbs.ToArray());
    }

    /// <summary>
    /// OT 8.11.4 sentences 10–15 (reader): the bits of hintmask count the stems in their encoded order, whether ascending or not — after two stems
    /// (encoded „50 10 10 10“ and descending „50 10 -40 10“) hintmask takes one byte, and the path that follows starts at (10, 20).
    /// </summary>
    [Fact]
    public void CffCharString_HintmaskLengthCountsTheStemsInEncodedOrder()
    {
        foreach (var stems in (string[])["50 10 10 10", "50 10 -40 10"])
        {
            var outline = CffFont.Parse(OneGlyphCffOt11(CsOt08(stems + " hstemhm hintmask 0xC0 10 20 rmoveto 5 0 rlineto endchar"))).Outline(1);
            Assert.False(outline.IsEmpty);
            Assert.Equal((10f, 20f), FirstPointOt11(outline));
        }
    }

    /// <summary>
    /// OT 8.11.4 sentences 4–9 (writer): the rules for blended and overlapping stem hints concern fonts that carry hints — the CFF PDF-Knead builds
    /// carries none: the charstrings of CffBuilder.Build hold no hstem, vstem, hstemhm, vstemhm, hintmask or cntrmask. (The static CFF2 table of a
    /// redaction keeps the hints of the font as they were, without blend: the CFF2 theories.)
    /// </summary>
    [Fact]
    public void CffBuilder_WritesNoHintOperators()
    {
        var outline = new GlyphOutline();
        outline.MoveTo(50, 0);
        outline.LineTo(550, 0);
        outline.CubicTo(600, 200, 600, 500, 550, 700);
        outline.LineTo(50, 700);
        outline.Close();
        var cff = CffBuilder.Build("Probe", new Matrix(0.001, 0, 0, 0.001, 0, 0), [("A", outline.Finish(), 600.0)]);
        var data = new ArraySegment<byte>(cff);
        var r = new FontReader(data, cff[2]);
        CffFont.CffIndex.Read(data, ref r);                                   // Name INDEX
        var top = CffFont.CffIndex.Read(data, ref r);
        var charStringsAt = CffFont.Dict.Parse(top.Item(data, 0)).Int(17)!.Value;
        r = new FontReader(data, charStringsAt);
        var charStrings = CffFont.CffIndex.Read(data, ref r);
        Assert.Equal(2, charStrings.Count);
        var operators = new List<int>();
        for (var g = 0; g < charStrings.Count; g++) operators.AddRange(OperatorsOt11(charStrings.Item(data, g)));
        Assert.Contains(8, operators);                                        // the curve is there (rrcurveto) …
        foreach (var hint in (int[])[1, 3, 18, 19, 20, 23]) Assert.DoesNotContain(hint, operators);   // … but no hint
    }

    /// <summary>
    /// OT 8.11.6 sentences 1–13 (reader, today): a variable TrueType font (Liberation Sans with fvar, gvar and HVAR) whose gvar moves only point 0
    /// and the advance phantom point of „A“, and whose HVAR widens „A“ — PDF-Knead reads neither: the outline of „A“ is that of glyf (the default
    /// instance, no inferred deltas) and its advance that of hmtx (no phantom points).
    /// </summary>
    [Fact]
    public void VariableTrueType_GvarAndHvarAreNotRead_DefaultInstance()
    {
        var plain = OpenTypeFont.Parse(LiberationOt11);
        var a = plain.GlyphForUnicode('A');
        var font = OpenTypeFont.Parse(ColorFontBuilder.WithTables(LiberationOt11, new Dictionary<string, byte[]?>
        {
            ["fvar"] = FvarOt08(), ["gvar"] = GvarOt11(plain.GlyphCount, a, PointCountOt11(plain, a)), ["HVAR"] = HvarOt11(plain.GlyphCount, a),
        }));
        Assert.True(font.HasTable("gvar") && font.HasTable("HVAR"));
        Assert.Equal(plain.Outline(a).Points.ToArray(), font.Outline(a).Points.ToArray());
        Assert.Equal(plain.Outline(a).Verbs.ToArray(), font.Outline(a).Verbs.ToArray());
        Assert.Equal(plain.AdvanceWidth(a), font.AdvanceWidth(a));
    }

    /// <summary>
    /// OT 8.11.6 sentences 1–5 and 8.12.4 sentence 2 (reader, today): a CFF2 font with HVAR — the advance stays the hmtx value of the default
    /// instance (600), HVAR is not read.
    /// </summary>
    [Fact]
    public void Cff2FontWithHvar_AdvanceStaysTheHmtxValue()
    {
        var font = OpenTypeFont.Parse(Cff2FontOt08(ExampleCff2Ot11(), 2, extra: new Dictionary<string, byte[]> { ["fvar"] = FvarOt08(), ["HVAR"] = HvarOt11(2, 1) }));
        Assert.Equal(600, font.AdvanceWidth(1));
    }

    /// <summary>
    /// OT 8.12 sentences 3–8 and 8.12.4 sentence 1 (reader): the tables without reference to a glyph format are read as in any font, also when
    /// the outlines are in CFF2 — a handwriting font with glyf and loca replaced by a CFF2 table keeps its family and PostScript name, weight,
    /// ascent, cmap and hmtx widths, and „M“ draws its rectangle from the CFF2 table.
    /// </summary>
    [Fact]
    public void Cff2Font_TablesBesideTheOutlinesAreRead()
    {
        var source = EmbeddedFonts.LoadHandwriting("PatrickHand-Regular.ttf");
        var plain = OpenTypeFont.Parse(source);
        var font = OpenTypeFont.Parse(ColorFontBuilder.WithTables(source,
            new Dictionary<string, byte[]?> { ["CFF2"] = RectanglesCff2Ot11(plain.GlyphCount), ["glyf"] = null, ["loca"] = null }));
        var m = plain.GlyphForUnicode('M');
        Assert.True(m > 0);
        Assert.Equal(plain.FamilyName, font.FamilyName);
        Assert.Equal(plain.PostScriptName, font.PostScriptName);
        Assert.Equal(plain.WeightClass, font.WeightClass);
        Assert.Equal(plain.Ascent, font.Ascent);
        Assert.Equal(m, font.GlyphForUnicode('M'));
        Assert.Equal(plain.AdvanceWidth(m), font.AdvanceWidth(m));
        SameOutlineOt08(ReferenceOt08($"50 0 rmoveto {100 + m} 0 rlineto 0 600 rlineto"), font.Outline(m), "M");
    }

    /// <summary>
    /// OT 8.12 sentence 9 (new text, question 1): a CFF2 font used for new text draws its glyphs („M“ from the CFF2 table), and saving that text
    /// writes a CID-keyed CFF of the default instance as FontFile3 /CIDFontType0C under a CIDFontType0 (ISO 32000-2, 9.9, Table 124 knows no
    /// CFF2) whose CID „M“ draws the same outline — before the TrueType subset refused it and saving broke off (finding B74); importing it as a
    /// signature font works.
    /// </summary>
    [Fact]
    public void Cff2Font_ForNewText_DrawnAndSavedAsCff()
    {
        var source = EmbeddedFonts.LoadHandwriting("PatrickHand-Regular.ttf");
        var plain = OpenTypeFont.Parse(source);
        var data = ColorFontBuilder.WithTables(source, new Dictionary<string, byte[]?> { ["CFF2"] = RectanglesCff2Ot11(plain.GlyphCount), ["glyf"] = null, ["loca"] = null });
        var face = ElementFace.For("M7-ot-11-CFF2-" + Guid.NewGuid().ToString("N"), () => OpenTypeFont.Parse(data), false, false, false);
        var m = face.Font.GlyphForUnicode('M');
        Assert.False(face.Font.Outline(m).IsEmpty);
        var saved = face.SavedFont(new Dictionary<int, string> { [m] = "M" });
        var cidFont = (PdfKnead.Core.Pdf.Objects.PdfDictionary)saved.GetArray(PdfKnead.Core.Pdf.Objects.PdfName.Get("DescendantFonts"))!.Get(0)!;
        Assert.Equal("CIDFontType0", cidFont.GetName(PdfKnead.Core.Pdf.Objects.PdfName.Subtype)!.Value);
        var fontFile = cidFont.GetDictionary(PdfKnead.Core.Pdf.Objects.PdfName.Get("FontDescriptor"))!.GetStream(PdfKnead.Core.Pdf.Objects.PdfName.Get("FontFile3"))!;
        Assert.Equal("CIDFontType0C", fontFile.Dictionary.GetName(PdfKnead.Core.Pdf.Objects.PdfName.Subtype)!.Value);
        var cff = CffFont.Parse(fontFile.RawData!);
        Assert.True(cff.IsCidKeyed);
        SameOutlineOt08(face.Font.Outline(m), cff.Outline(cff.GlyphByCid(m)), "M gespeichert");

        var folder = Path.Combine(Path.GetTempPath(), "pdfknead-m7-ot11-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "Probe.otf");
            File.WriteAllBytes(file, data);
            var target = Path.Combine(folder, "Ziel");
            var result = SignatureFonts.Import(file, [target]);
            Assert.Null(result.Error);
            Assert.True(Directory.EnumerateFiles(target).Any());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>
    /// OT 8.12 sentences 10–11 (reader): a CFF2 font with COLR/CPAL (glyph 1 one red layer) and fvar — colour layers are not read for it
    /// (ColorGlyphs.For gives null), glyph 1 draws its outline from the CFF2 table.
    /// </summary>
    [Fact]
    public void Cff2FontWithColr_NoColourGlyphs()
    {
        byte[] colr = [.. BeOt11((0, 2), (1, 2), (14, 4), (20, 4), (1, 2)), .. BeOt11((1, 2), (0, 2), (1, 2)), .. BeOt11((1, 2), (0, 2))];
        byte[] cpal = [.. BeOt11((0, 2), (1, 2), (1, 2), (1, 2), (14, 4), (0, 2)), 0, 0, 0xFF, 0xFF];
        var font = OpenTypeFont.Parse(Cff2FontOt08(ExampleCff2Ot11(), 2,
            extra: new Dictionary<string, byte[]> { ["COLR"] = colr, ["CPAL"] = cpal, ["fvar"] = FvarOt08() }));
        Assert.True(font.HasTable("COLR"));
        Assert.Null(ColorGlyphs.For(font));
        Assert.False(font.Outline(1).IsEmpty);
    }

    /// <summary>
    /// OT 8.12.1 sentence 1 (writer): a font written with a CFF2 table and no glyf gets the sfnt version „OTTO“ (TrueTypeSubsetter.Assemble; the
    /// redaction of a CFF2 font writes through it).
    /// </summary>
    [Fact]
    public void Assembled_WithCff2_IsOtto()
    {
        var font = TrueTypeSubsetter.Assemble(new Dictionary<string, byte[]> { ["CFF2"] = ExampleCff2Ot11(), ["head"] = new byte[54] });
        Assert.Equal("OTTO"u8.ToArray(), font[..4]);
    }

    /// <summary>The glyph count of a font „OTTO“ with „CFF “ (two glyphs: .notdef and one line), head and the given maxp.</summary>
    private static int GlyphCountWithMaxpOt11(byte[] maxp)
    {
        var cff = OneGlyphCffOt11(CsOt08("0 0 rmoveto 100 0 rlineto endchar"));
        var head = new byte[54];
        head[18] = 0x03;
        head[19] = 0xE8;                                                    // 1000 units per em
        return OpenTypeFont.Parse(TrueTypeSubsetter.Assemble(new Dictionary<string, byte[]> { ["CFF "] = cff, ["head"] = head, ["maxp"] = maxp })).GlyphCount;
    }

    /// <summary>
    /// OT 8.12.2 sentences 1–3 (reader): maxp version 0.5 is 6 bytes long, without the TrueType values — a font „OTTO“ with „CFF “ (two glyphs)
    /// and the maxp 00005000 0003 counts 3 glyphs.
    /// </summary>
    [Fact]
    public void ShortMaxp_Version05_GivesTheGlyphCount() => Assert.Equal(3, GlyphCountWithMaxpOt11([0, 0, 0x50, 0, 0, 3]));

    /// <summary>
    /// OT 8.12.2 sentences 1–3 (reader): a maxp cut to 5 bytes has no whole numGlyphs — the count must come from the CFF (2), not from the byte
    /// after the table (here the padding, read as 256).
    /// </summary>
    [Fact]
    public void TruncatedMaxp_GlyphCountComesFromTheCff() => Assert.Equal(2, GlyphCountWithMaxpOt11([0, 0, 0x50, 0, 1]));

    /// <summary>
    /// OT 8.12.3 sentences 2–6 (reader, tolerant): the FontMatrix of a CFF2 font must match unitsPerEm and may be left out only at 1000 — a CFF2
    /// table without FontMatrix in a font of 2000 units per em is scaled by head (0.0005), as common font engines do; the redaction keeps it so.
    /// </summary>
    [Fact]
    public void Cff2Font_FontMatrixComesFromHead()
    {
        var file = Cff2FontOt08(ExampleCff2Ot11(), 2, unitsPerEm: 2000);
        Assert.Equal(Matrix.Scale(0.0005, 0.0005), OpenTypeFont.Parse(file).FontMatrix);
        Assert.Equal(Matrix.Scale(0.0005, 0.0005), OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(file, [1])).FontMatrix);
    }

    /// <summary>
    /// OT 8.12.3 sentences 7–10 (reader): indexToLocFormat 1 in a CFF2 font without loca — the glyph count comes from maxp, both glyphs draw the
    /// example rectangle from the CFF2 table, no loca is read.
    /// </summary>
    [Fact]
    public void Cff2Font_LongLocaFlagWithoutLoca_IsHarmless()
    {
        var font = OpenTypeFont.Parse(Cff2FontOt08(ExampleCff2Ot11(), 2, indexToLocFormat: 1));
        Assert.True(font.LongLoca);
        Assert.False(font.HasTable("loca"));
        Assert.Equal(2, font.GlyphCount);
        SameOutlineOt08(ReferenceOt08(ExampleRectOt11), font.Outline(0), "Glyphe 0");
        SameOutlineOt08(ReferenceOt08(ExampleRectOt11), font.Outline(1), "Glyphe 1");
    }

    /// <summary>vhea (all glyphs with a full record) and vmtx (advance 1000, top side bearing 100 each) for <paramref name="glyphs"/> glyphs.</summary>
    private static Dictionary<string, byte[]?> VerticalOt11(int glyphs)
    {
        var vhea = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(vhea, 0x00011000);
        BinaryPrimitives.WriteUInt16BigEndian(vhea.AsSpan(34), (ushort)glyphs);
        var vmtx = new byte[glyphs * 4];
        for (var g = 0; g < glyphs; g++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(vmtx.AsSpan(g * 4), 1000);
            BinaryPrimitives.WriteInt16BigEndian(vmtx.AsSpan(g * 4 + 2), 100);
        }
        return new() { ["vhea"] = vhea, ["vmtx"] = vmtx };
    }

    /// <summary>
    /// OT 8.12.5 sentence 1 (writer): scrubbing a TrueType font with vhea/vmtx clears the vertical metrics of the glyphs not kept (advance and top
    /// side bearing 0) and leaves those of the kept glyphs as they were.
    /// </summary>
    [Fact]
    public void Scrub_ClearsTheVerticalMetricsOfGlyphsNotKept()
    {
        var plain = OpenTypeFont.Parse(LiberationOt11);
        int a = plain.GlyphForUnicode('A'), b = plain.GlyphForUnicode('B');
        var font = ColorFontBuilder.WithTables(LiberationOt11, VerticalOt11(plain.GlyphCount));
        var vmtx = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, [0, a])).TableData("vmtx").ToArray();
        (int, int) Metrics(int g) => (BinaryPrimitives.ReadUInt16BigEndian(vmtx[(g * 4)..]), BinaryPrimitives.ReadInt16BigEndian(vmtx[(g * 4 + 2)..]));
        Assert.Equal((1000, 100), Metrics(a));
        Assert.Equal((0, 0), Metrics(b));
    }

    /// <summary>
    /// OT 8.12.5 sentence 2 (writer, today): VVAR is not read, and scrubbing a TrueType font leaves it byte for byte (it is not among the tables
    /// left out — finding S9 d, question 2).
    /// </summary>
    [Fact]
    public void Scrub_LeavesVvarAsItWas()
    {
        var plain = OpenTypeFont.Parse(LiberationOt11);
        var a = plain.GlyphForUnicode('A');
        var vvar = HvarOt11(plain.GlyphCount, a);
        var tables = VerticalOt11(plain.GlyphCount);
        tables["fvar"] = FvarOt08();
        tables["VVAR"] = vvar;
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(ColorFontBuilder.WithTables(LiberationOt11, tables), [0, a]));
        Assert.Equal(vvar, scrubbed.TableData("VVAR").ToArray());
    }

    // ------------------------------------------------------------ cmap 9.1

    /// <summary>
    /// OT cmap 9.1 sentences 7–8 (reader): formats 0, 2 and 6 are read — (1,0) format 0 maps „A“ to glyph 3, (0,3) format 6 from 0x20 maps 0x21
    /// to glyph 5; format 2 (high-byte mapping) under (3,2) maps the two-byte code 0x8140 to glyph 9 (FG 1160; before 0).
    /// </summary>
    [Fact]
    public void Cmap_Formats0And2And6AreRead()
    {
        var format0 = new byte[262];
        BeOt11((0, 2), (262, 2), (0, 2)).CopyTo(format0, 0);                // format 0, length, language
        format0[6 + 'A'] = 3;
        var font = OpenTypeFont.Parse(ColorFontBuilder.WithTables(LiberationOt11, new Dictionary<string, byte[]?>
        {
            ["cmap"] = CmapOt11((0, 3, BeOt11((6, 2), (16, 2), (0, 2), (0x20, 2), (3, 2), (4, 2), (5, 2), (6, 2))), (1, 0, format0), (3, 2, Format2Ot11(9))),
        }));
        Assert.Equal(3, font.Lookup(1, 0, 'A'));
        Assert.Equal(5, font.Lookup(0, 3, 0x21));
        Assert.Equal(6, font.Lookup(0, 3, 0x22));
        Assert.Equal(9, font.Lookup(3, 2, 0x8140));
        Assert.Equal(0, font.Lookup(3, 2, 0x8141));
        Assert.Equal(0, font.Lookup(3, 2, 0x81));
    }

    /// <summary>
    /// OT cmap 9.1 sentences 9–10 (reader): the 32-bit formats 8, 10 and 12 are read — (3,10) with the group U+1F600 … U+1F602 from glyph 5 gives
    /// glyph 6 for U+1F601 in format 12, in format 10 (trimmed array) and in format 8 (the code as the two halves of its surrogate pair) (FG 1160;
    /// before 8 and 10 gave 0).
    /// </summary>
    [Fact]
    public void Cmap_Formats8And10And12AreRead()
    {
        OpenTypeFont With(byte[] subtable) =>
            OpenTypeFont.Parse(ColorFontBuilder.WithTables(LiberationOt11, new Dictionary<string, byte[]?> { ["cmap"] = CmapOt11((3, 10, subtable)) }));
        Assert.Equal(6, With(GroupCmapOt11(12, 0x1F600, 0x1F602, 5)).GlyphForUnicode(0x1F601));
        var format10 = With(Format10Ot11(0x1F600, 3, 5));
        Assert.Equal(6, format10.GlyphForUnicode(0x1F601));
        Assert.Equal(6, format10.Lookup(3, 10, 0x1F601));
        Assert.Equal(0, format10.Lookup(3, 10, 0x1F603));
        var pair = (0xD83D << 16) | 0xDE00;
        var is32 = new byte[8192];
        is32[0xD83D / 8] |= (byte)(0x80 >> (0xD83D % 8));
        var format8 = With([.. BeOt11((8, 2), (0, 2), (12 + 8192 + 4 + 12, 4), (0, 4)), .. is32, .. BeOt11((1, 4), (pair, 4), (pair + 2, 4), (5, 4))]);
        Assert.Equal(6, format8.GlyphForUnicode(0x1F601));
        Assert.Equal(0, format8.GlyphForUnicode(0x1F603));
    }

    /// <summary>OT cmap 9.1 sentences 11–12 (reader): format 13 maps a whole group to one glyph — U+0400 … U+04FF all to glyph 7.</summary>
    [Fact]
    public void Cmap_Format13_MapsTheGroupToOneGlyph()
    {
        var font = OpenTypeFont.Parse(ColorFontBuilder.WithTables(LiberationOt11,
            new Dictionary<string, byte[]?> { ["cmap"] = CmapOt11((3, 10, GroupCmapOt11(13, 0x0400, 0x04FF, 7))) }));
        Assert.Equal(7, font.GlyphForUnicode(0x0410));
        Assert.Equal(7, font.GlyphForUnicode(0x04FF));
        Assert.Equal(7, font.GlyphForUnicode(0x0400));
        Assert.Equal(0, font.GlyphForUnicode(0x0500));
    }

    /// <summary>
    /// OT cmap 9.1 sentences 14–18 (writer): scrubbing writes only formats 4 and 12 — every subtable of Liberation Sans ((0,3), (1,0) format 6,
    /// (3,1)) comes out in format 4; the (3,10) and (0,4) subtables of the Noto CJK font in format 12 (its (0,5) stays format 14, filtered on
    /// the glyphs kept, FG 1160); the PDF/A rebuild with a code map writes one (3,0) subtable in format 4.
    /// </summary>
    [Fact]
    public void WrittenCmaps_UseFormats4And12()
    {
        int FormatOf(OpenTypeFont f, int platform, int encoding)
        {
            var at = f.CmapOffset(platform, encoding);
            return f.FileData[at] << 8 | f.FileData[at + 1];
        }
        var plain = OpenTypeFont.Parse(LiberationOt11);
        var a = plain.GlyphForUnicode('A');
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(LiberationOt11, [0, a]));
        Assert.Equal(new (int, int)[] { (0, 3), (1, 0), (3, 1) }, scrubbed.CmapSubtables);
        foreach (var (p, e) in scrubbed.CmapSubtables) Assert.Equal(4, FormatOf(scrubbed, p, e));

        var noto = OpenTypeFont.Parse(File.ReadAllBytes(Path.Combine(RepoOt11(), "Schriften", "CJK", "NotoSansCJK-Regular.ttc")));
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var tag in (string[])["CFF ", "cmap", "head", "hhea", "hmtx", "maxp"])
            if (noto.Table(tag) is { } t) tables[tag] = noto.FileData.AsSpan(t.Offset, t.Length).ToArray();
        var otf = TrueTypeSubsetter.Finish(tables);
        var cjk = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(otf, [0, noto.GlyphForUnicode('漢')]));
        Assert.True(cjk.HasCmap(3, 10));
        Assert.Equal(12, FormatOf(cjk, 3, 10));
        Assert.Equal(12, FormatOf(cjk, 0, 4));
        Assert.Equal(4, FormatOf(cjk, 3, 1));
        Assert.Equal(14, FormatOf(cjk, 0, 5));                              // the variation sequences of 漢 kept (FG 1160)

        var rebuilt = OpenTypeFont.Parse(TrueTypeSubsetter.Rebuild(LiberationOt11, new Dictionary<int, int> { [0] = 0, [a] = 600 }, [], [('A', a)]));
        Assert.Equal(new (int, int)[] { (3, 0) }, rebuilt.CmapSubtables);
        Assert.Equal(4, FormatOf(rebuilt, 3, 0));
    }

    /// <summary>
    /// OT cmap 9.1 sentence 19 (reader): every format the specification defines is read — 2, 8, 10 and 14 beside the others: (3,2) format 2 gives
    /// glyph 9 for 0x8140, (3,7) format 8 and (3,10) format 10 glyph 6 for U+1F601, (0,5) format 14 the glyph 5 for the sequence U+2764 U+FE0F
    /// (a single code in it gives 0); an unknown format 99 gives 0 without an error (FG 1160).
    /// </summary>
    [Fact]
    public void Cmap_EveryDefinedFormatIsRead_UnknownGivesGlyphZero()
    {
        var font = OpenTypeFont.Parse(ColorFontBuilder.WithTables(LiberationOt11, new Dictionary<string, byte[]?>
        {
            ["cmap"] = CmapOt11((0, 5, Format14Ot11(5)), (3, 2, Format2Ot11(9)), (3, 7, Format8Ot11(0x1F600, 5)), (3, 10, Format10Ot11(0x1F600, 3, 5)),
                (3, 11, BeOt11((99, 2), (6, 2), (0, 2)))),
        }));
        Assert.Equal(new (int, int)[] { (0, 5), (3, 2), (3, 7), (3, 10), (3, 11) }, font.CmapSubtables);
        Assert.Equal(0, font.Lookup(0, 5, 0x2764));
        Assert.Equal(5, PdfKnead.Core.Fonts.Layout.TextShaper.For(font).Shape("❤️").Single().Glyph);
        Assert.Equal(9, font.Lookup(3, 2, 0x8140));
        Assert.Equal(6, font.Lookup(3, 7, 0x1F601));
        Assert.Equal(6, font.Lookup(3, 10, 0x1F601));
        Assert.Equal(6, font.GlyphForUnicode(0x1F601));
        Assert.Equal(0, font.Lookup(3, 11, 0x41));
    }

    /// <summary>
    /// OT cmap 9.1 sentence 20 (writer and reader): the cmap version stays 0 — a scrubbed Liberation Sans starts its cmap with 00 00; a cmap whose
    /// version is 1 is read as before („A“ the same glyph).
    /// </summary>
    [Fact]
    public void Cmap_VersionZeroWritten_OtherVersionReadAlike()
    {
        var plain = OpenTypeFont.Parse(LiberationOt11);
        var a = plain.GlyphForUnicode('A');
        var cmap = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(LiberationOt11, [0, a])).TableData("cmap");
        Assert.Equal(0, cmap[0] << 8 | cmap[1]);
        var version1 = plain.TableData("cmap").ToArray();
        version1[1] = 1;
        var font = OpenTypeFont.Parse(ColorFontBuilder.WithTables(LiberationOt11, new Dictionary<string, byte[]?> { ["cmap"] = version1 }));
        Assert.Equal(a, font.GlyphForUnicode('A'));
    }
}
