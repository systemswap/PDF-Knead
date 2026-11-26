using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Fonts;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-14 (OpenType, COLR 10.2.6 to 10.3.4): transformations, composition, reuse, bounds and the graph rules of colour glyphs,
/// the COLR header, the version 0 records, BaseGlyphList, LayerList and ClipList. PDF-Knead reads neither COLR nor CPAL (question 3 is open):
/// the tests build fonts with hand-made COLR and CPAL tables over Liberation Sans and check today's behaviour — the font parses, no colour
/// glyphs are offered and every glyph keeps the outline and advance it has without the tables. The COLR/CPAL builders here are shared by
/// the packages ot-15 to ot-17.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (ot-14, shared with ot-15 to ot-17)

    /// <summary>A field of a COLR subtable: an unsigned big-endian number of 1 to 4 bytes.</summary>
    private sealed record Ot14Num(int Size, long Value);

    /// <summary>A COLR version 1 subtable (Paint, ColorLine, Affine2x3, ClipBox): its fields in order; a field that is a node is an Offset24 to it.</summary>
    private sealed class Ot14Node(params object[] fields)
    {
        public object[] Fields { get; } = fields;

        public int Size => Fields.Sum(f => f is Ot14Num n ? n.Size : 3);
    }

    private static Ot14Num Ot14U8(int v) => new(1, v & 0xFF);

    private static Ot14Num Ot14U16(int v) => new(2, v & 0xFFFF);

    private static Ot14Num Ot14I16(int v) => new(2, unchecked((ushort)(short)v));

    private static Ot14Num Ot14U32(long v) => new(4, v & 0xFFFFFFFF);

    /// <summary>A raw Offset24 (0 points at the subtable itself, 0xFFFFFF far behind the end of the table).</summary>
    private static Ot14Num Ot14Off24(int v) => new(3, v & 0xFFFFFF);

    /// <summary>F2DOT14 (angles in half turns: 0.5 is 90°).</summary>
    private static Ot14Num Ot14F2(double v) => new(2, unchecked((ushort)(short)(int)Math.Round(v * 16384)));

    /// <summary>Fixed 16.16.</summary>
    private static Ot14Num Ot14Fixed(double v) => new(4, unchecked((uint)(int)Math.Round(v * 65536)));

    /// <summary>Any paint: format byte and fields.</summary>
    private static Ot14Node Ot14Paint(int format, params object[] fields) => new([Ot14U8(format), .. fields]);

    private static Ot14Node Ot14Layers(int count, long first) => Ot14Paint(1, Ot14U8(count), Ot14U32(first));

    private static Ot14Node Ot14Solid(int palette, double alpha = 1) => Ot14Paint(2, Ot14U16(palette), Ot14F2(alpha));

    private static Ot14Node Ot14VarSolid(int palette, double alpha, long varIndexBase) => Ot14Paint(3, Ot14U16(palette), Ot14F2(alpha), Ot14U32(varIndexBase));

    /// <summary>ColorLine: extend mode and stops (offset, palette index, alpha).</summary>
    private static Ot14Node Ot14Line(int extend, params (double Stop, int Palette, double Alpha)[] stops) =>
        new([Ot14U8(extend), Ot14U16(stops.Length), .. stops.SelectMany(s => new object[] { Ot14F2(s.Stop), Ot14U16(s.Palette), Ot14F2(s.Alpha) })]);

    /// <summary>VarColorLine: extend mode and stops with their varIndexBase.</summary>
    private static Ot14Node Ot14VarLine(int extend, params (double Stop, int Palette, double Alpha, long VarIndexBase)[] stops) =>
        new([Ot14U8(extend), Ot14U16(stops.Length), .. stops.SelectMany(s => new object[] { Ot14F2(s.Stop), Ot14U16(s.Palette), Ot14F2(s.Alpha), Ot14U32(s.VarIndexBase) })]);

    /// <summary>Two stops: 0 red (palette 0), 1 blue (palette 2).</summary>
    private static Ot14Node Ot14RedToBlue(int extend = 0) => Ot14Line(extend, (0, 0, 1), (1, 2, 1));

    private static Ot14Node Ot14Linear(object line, int x0, int y0, int x1, int y1, int x2, int y2) =>
        Ot14Paint(4, line, Ot14I16(x0), Ot14I16(y0), Ot14I16(x1), Ot14I16(y1), Ot14I16(x2), Ot14I16(y2));

    private static Ot14Node Ot14Radial(object line, int x0, int y0, int r0, int x1, int y1, int r1) =>
        Ot14Paint(6, line, Ot14I16(x0), Ot14I16(y0), Ot14U16(r0), Ot14I16(x1), Ot14I16(y1), Ot14U16(r1));

    private static Ot14Node Ot14Sweep(object line, int cx, int cy, double start, double end) =>
        Ot14Paint(8, line, Ot14I16(cx), Ot14I16(cy), Ot14F2(start), Ot14F2(end));

    private static Ot14Node Ot14Glyph(int glyph, Ot14Node paint) => Ot14Paint(10, paint, Ot14U16(glyph));

    private static Ot14Node Ot14ColrGlyph(int glyph) => Ot14Paint(11, Ot14U16(glyph));

    /// <summary>PaintTransform with an Affine2x3 (x' = xx·x + xy·y + dx, y' = yx·x + yy·y + dy).</summary>
    private static Ot14Node Ot14Transform(Ot14Node paint, double xx, double yx, double xy, double yy, double dx, double dy) =>
        Ot14Paint(12, paint, new Ot14Node(Ot14Fixed(xx), Ot14Fixed(yx), Ot14Fixed(xy), Ot14Fixed(yy), Ot14Fixed(dx), Ot14Fixed(dy)));

    private static Ot14Node Ot14Translate(Ot14Node paint, int dx, int dy) => Ot14Paint(14, paint, Ot14I16(dx), Ot14I16(dy));

    private static Ot14Node Ot14Scale(Ot14Node paint, double sx, double sy) => Ot14Paint(16, paint, Ot14F2(sx), Ot14F2(sy));

    private static Ot14Node Ot14ScaleAround(Ot14Node paint, double sx, double sy, int cx, int cy) =>
        Ot14Paint(18, paint, Ot14F2(sx), Ot14F2(sy), Ot14I16(cx), Ot14I16(cy));

    private static Ot14Node Ot14ScaleUniform(Ot14Node paint, double s) => Ot14Paint(20, paint, Ot14F2(s));

    private static Ot14Node Ot14ScaleUniformAround(Ot14Node paint, double s, int cx, int cy) => Ot14Paint(22, paint, Ot14F2(s), Ot14I16(cx), Ot14I16(cy));

    private static Ot14Node Ot14Rotate(Ot14Node paint, double halfTurns) => Ot14Paint(24, paint, Ot14F2(halfTurns));

    private static Ot14Node Ot14RotateAround(Ot14Node paint, double halfTurns, int cx, int cy) => Ot14Paint(26, paint, Ot14F2(halfTurns), Ot14I16(cx), Ot14I16(cy));

    private static Ot14Node Ot14Skew(Ot14Node paint, double x, double y) => Ot14Paint(28, paint, Ot14F2(x), Ot14F2(y));

    private static Ot14Node Ot14SkewAround(Ot14Node paint, double x, double y, int cx, int cy) =>
        Ot14Paint(30, paint, Ot14F2(x), Ot14F2(y), Ot14I16(cx), Ot14I16(cy));

    private static Ot14Node Ot14Composite(Ot14Node source, int mode, Ot14Node backdrop) => Ot14Paint(32, source, Ot14U8(mode), backdrop);

    private static Ot14Node Ot14ClipBox(int xMin, int yMin, int xMax, int yMax) => new(Ot14U8(1), Ot14I16(xMin), Ot14I16(yMin), Ot14I16(xMax), Ot14I16(yMax));

    private static void Ot14Put(List<byte> bytes, int size, long value)
    {
        for (var i = size - 1; i >= 0; i--) bytes.Add((byte)(value >> (8 * i)));
    }

    /// <summary>
    /// A COLR table. Version 0: header of 14 bytes, then the BaseGlyph and Layer records. Version 1: header of 34 bytes, the version 0 records,
    /// BaseGlyphList (<paramref name="paints"/>, sorted by glyph unless <paramref name="sort"/> is false), LayerList, ClipList, DeltaSetIndexMap,
    /// ItemVariationStore and behind them all subtables, parents before their children (offsets are relative to the subtable or list that holds them).
    /// </summary>
    private static byte[] Ot14Colr(
        IEnumerable<(int Glyph, Ot14Node Paint)>? paints = null,
        IReadOnlyList<Ot14Node>? layers = null,
        IEnumerable<(int First, int Last, Ot14Node Box)>? clips = null,
        IEnumerable<(int Glyph, int FirstLayer, int Count)>? baseGlyphs = null,
        IEnumerable<(int Glyph, int Palette)>? layerRecords = null,
        byte[]? varIndexMap = null,
        byte[]? varStore = null,
        int version = 1,
        long? paintRecordCount = null,
        bool sort = true)
    {
        var bases = baseGlyphs?.ToList() ?? [];
        var layerRecs = layerRecords?.ToList() ?? [];
        var paintList = paints is null ? null : sort ? paints.OrderBy(p => p.Glyph).ToList() : paints.ToList();
        var clipList = clips?.ToList();

        var at = version == 0 ? 14 : 34;
        var baseAt = bases.Count > 0 ? at : 0;
        at += 6 * bases.Count;
        var layerAt = layerRecs.Count > 0 ? at : 0;
        at += 4 * layerRecs.Count;
        var listAt = paintList is null ? 0 : at;
        at += paintList is null ? 0 : 4 + 6 * paintList.Count;
        var layerListAt = layers is null ? 0 : at;
        at += layers is null ? 0 : 4 + 4 * layers.Count;
        var clipAt = clipList is null ? 0 : at;
        at += clipList is null ? 0 : 5 + 7 * clipList.Count;
        var mapAt = varIndexMap is null ? 0 : at;
        at += varIndexMap?.Length ?? 0;
        var storeAt = varStore is null ? 0 : at;
        at += varStore?.Length ?? 0;

        // All subtables in topological order (reverse post-order of a depth-first walk): every offset points forward
        var order = new List<Ot14Node>();
        var seen = new HashSet<Ot14Node>();
        void Visit(Ot14Node n)
        {
            if (!seen.Add(n)) return;
            foreach (var child in n.Fields.OfType<Ot14Node>()) Visit(child);
            order.Add(n);
        }
        foreach (var (_, p) in paintList ?? []) Visit(p);
        foreach (var l in layers ?? []) Visit(l);
        foreach (var (_, _, box) in clipList ?? []) Visit(box);
        order.Reverse();
        var position = new Dictionary<Ot14Node, int>();
        foreach (var n in order)
        {
            position[n] = at;
            at += n.Size;
        }

        var b = new List<byte>();
        Ot14Put(b, 2, version);
        Ot14Put(b, 2, bases.Count);
        Ot14Put(b, 4, baseAt);
        Ot14Put(b, 4, layerAt);
        Ot14Put(b, 2, layerRecs.Count);
        if (version != 0)
        {
            Ot14Put(b, 4, listAt);
            Ot14Put(b, 4, layerListAt);
            Ot14Put(b, 4, clipAt);
            Ot14Put(b, 4, mapAt);
            Ot14Put(b, 4, storeAt);
        }
        foreach (var (glyph, first, count) in bases)
        {
            Ot14Put(b, 2, glyph);
            Ot14Put(b, 2, first);
            Ot14Put(b, 2, count);
        }
        foreach (var (glyph, palette) in layerRecs)
        {
            Ot14Put(b, 2, glyph);
            Ot14Put(b, 2, palette);
        }
        if (paintList is not null)
        {
            Ot14Put(b, 4, paintRecordCount ?? paintList.Count);
            foreach (var (glyph, paint) in paintList)
            {
                Ot14Put(b, 2, glyph);
                Ot14Put(b, 4, position[paint] - listAt);
            }
        }
        if (layers is not null)
        {
            Ot14Put(b, 4, layers.Count);
            foreach (var l in layers) Ot14Put(b, 4, position[l] - layerListAt);
        }
        if (clipList is not null)
        {
            Ot14Put(b, 1, 1);
            Ot14Put(b, 4, clipList.Count);
            foreach (var (first, last, box) in clipList)
            {
                Ot14Put(b, 2, first);
                Ot14Put(b, 2, last);
                Ot14Put(b, 3, position[box] - clipAt);
            }
        }
        if (varIndexMap is not null) b.AddRange(varIndexMap);
        if (varStore is not null) b.AddRange(varStore);
        foreach (var n in order)
            foreach (var f in n.Fields)
                if (f is Ot14Num num) Ot14Put(b, num.Size, num.Value);
                else Ot14Put(b, 3, position[(Ot14Node)f] - position[n]);
        return [.. b];
    }

    /// <summary>A CPAL colour record (blue, green, red, alpha — the byte order of the table).</summary>
    private readonly record struct Ot14Bgra(int B, int G, int R, int A);

    private static readonly Ot14Bgra Ot14RedColour = new(0, 0, 255, 255);
    private static readonly Ot14Bgra Ot14GreenColour = new(0, 255, 0, 255);
    private static readonly Ot14Bgra Ot14BlueColour = new(255, 0, 0, 255);
    private static readonly Ot14Bgra Ot14GreyColour = new(128, 128, 128, 255);

    /// <summary>
    /// A CPAL table: <paramref name="entries"/> per palette, the first colour record of each palette, the colour records; version 1 with the
    /// optional arrays of palette types, palette labels and entry labels (an array left null has offset 0).
    /// </summary>
    private static byte[] Ot14Cpal(int entries, int[] indices, Ot14Bgra[] colours, int version = 0, int? colourCount = null,
        long[]? types = null, int[]? labels = null, int[]? entryLabels = null)
    {
        var head = 12 + 2 * indices.Length + (version >= 1 ? 12 : 0);
        var b = new List<byte>();
        Ot14Put(b, 2, version);
        Ot14Put(b, 2, entries);
        Ot14Put(b, 2, indices.Length);
        Ot14Put(b, 2, colourCount ?? colours.Length);
        Ot14Put(b, 4, head);
        foreach (var i in indices) Ot14Put(b, 2, i);
        if (version >= 1)
        {
            var after = head + 4 * colours.Length;
            Ot14Put(b, 4, types is null ? 0 : after);
            after += 4 * (types?.Length ?? 0);
            Ot14Put(b, 4, labels is null ? 0 : after);
            after += 2 * (labels?.Length ?? 0);
            Ot14Put(b, 4, entryLabels is null ? 0 : after);
        }
        foreach (var c in colours) b.AddRange([(byte)c.B, (byte)c.G, (byte)c.R, (byte)c.A]);
        foreach (var t in types ?? []) Ot14Put(b, 4, t);
        foreach (var l in labels ?? []) Ot14Put(b, 2, l);
        foreach (var l in entryLabels ?? []) Ot14Put(b, 2, l);
        return [.. b];
    }

    /// <summary>One palette: 0 red, 1 green, 2 blue, 3 grey.</summary>
    private static byte[] Ot14Palette() => Ot14Cpal(4, [0], [Ot14RedColour, Ot14GreenColour, Ot14BlueColour, Ot14GreyColour]);

    /// <summary>DeltaSetIndexMap format 0 with entries of 4 bytes (outer index, inner index of 16 bits each).</summary>
    private static byte[] Ot14DeltaSetIndexMap(params (int Outer, int Inner)[] map)
    {
        var b = new List<byte>();
        Ot14Put(b, 1, 0);
        Ot14Put(b, 1, 0x3F);
        Ot14Put(b, 2, map.Length);
        foreach (var (outer, inner) in map)
        {
            Ot14Put(b, 2, outer);
            Ot14Put(b, 2, inner);
        }
        return [.. b];
    }

    /// <summary>ItemVariationStore: one axis, one region (peak at 1), one ItemVariationData with one int16 delta per item.</summary>
    private static byte[] Ot14VariationStore(params int[] deltas)
    {
        var b = new List<byte>();
        Ot14Put(b, 2, 1);
        Ot14Put(b, 4, 12);              // region list behind the header
        Ot14Put(b, 2, 1);
        Ot14Put(b, 4, 12 + 10);         // the data behind the region list
        Ot14Put(b, 2, 1);               // region list: one axis, one region (start 0, peak 1, end 1)
        Ot14Put(b, 2, 1);
        Ot14Put(b, 2, 0);
        Ot14Put(b, 2, 0x4000);
        Ot14Put(b, 2, 0x4000);
        Ot14Put(b, 2, deltas.Length);   // ItemVariationData
        Ot14Put(b, 2, 1);
        Ot14Put(b, 2, 1);
        Ot14Put(b, 2, 0);
        foreach (var d in deltas) Ot14Put(b, 2, unchecked((ushort)(short)d));
        return [.. b];
    }

    /// <summary>fvar with one axis 'wght' (100 to 900, default 400) and no named instances.</summary>
    private static byte[] Ot14Fvar()
    {
        var b = new List<byte>();
        Ot14Put(b, 2, 1);
        Ot14Put(b, 2, 0);
        Ot14Put(b, 2, 16);
        Ot14Put(b, 2, 2);
        Ot14Put(b, 2, 1);
        Ot14Put(b, 2, 20);
        Ot14Put(b, 2, 0);
        Ot14Put(b, 2, 8);
        b.AddRange("wght"u8.ToArray());
        Ot14Put(b, 4, 100 << 16);
        Ot14Put(b, 4, 400 << 16);
        Ot14Put(b, 4, 900 << 16);
        Ot14Put(b, 2, 0);
        Ot14Put(b, 2, 256);
        return [.. b];
    }

    private static readonly Lazy<byte[]> Ot14SansFile = new(() => EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"));
    private static readonly Lazy<OpenTypeFont> Ot14Sans = new(() => OpenTypeFont.Parse(Ot14SansFile.Value));

    /// <summary>The glyph of a character in Liberation Sans.</summary>
    private static int Ot14Gid(char c) => Ot14Sans.Value.GlyphForUnicode(c);

    /// <summary>
    /// Today's behaviour with colour tables PDF-Knead does not read: <paramref name="source"/> with <paramref name="tables"/> added parses without
    /// an exception, keeps the tables byte for byte, offers no colour glyphs, and every glyph has the outline and advance it has without them.
    /// </summary>
    private static OpenTypeFont Ot14TablesIgnored(byte[] source, IReadOnlyDictionary<string, byte[]?> tables)
    {
        var plain = ReferenceEquals(source, Ot14SansFile.Value) ? Ot14Sans.Value : OpenTypeFont.Parse(source);
        var font = OpenTypeFont.Parse(ColorFontBuilder.WithTables(source, tables));
        foreach (var (tag, data) in tables)
            if (data is not null) Assert.Equal(data, font.TableData(tag).ToArray());
        Assert.Null(ColorGlyphs.For(font));
        Assert.Equal(plain.GlyphCount, font.GlyphCount);
        for (var g = 0; g < plain.GlyphCount; g++)
        {
            var expected = plain.Outline(g);
            var actual = font.Outline(g);
            Assert.True(expected.Verbs.SequenceEqual(actual.Verbs) && expected.Points.SequenceEqual(actual.Points), $"Umriss der Glyphe {g}");
            Assert.Equal(plain.AdvanceWidth(g), font.AdvanceWidth(g));
        }
        return font;
    }

    /// <summary><see cref="Ot14TablesIgnored"/> with a COLR table, a CPAL table (default <see cref="Ot14Palette"/>) and further tables.</summary>
    private static OpenTypeFont Ot14ColrIgnored(byte[] colr, byte[]? cpal = null, byte[]? source = null, params (string Tag, byte[] Data)[] more)
    {
        var tables = new Dictionary<string, byte[]?> { ["COLR"] = colr, ["CPAL"] = cpal ?? Ot14Palette() };
        foreach (var (tag, data) in more) tables[tag] = data;
        return Ot14TablesIgnored(source ?? Ot14SansFile.Value, tables);
    }

    /// <summary>Whether <paramref name="pattern"/> occurs in <paramref name="data"/>.</summary>
    private static bool Ot14Contains(byte[] data, params byte[] pattern) => data.AsSpan().IndexOf(pattern) >= 0;

    /// <summary>
    /// A page that shows „A“ in a simple TrueType font with <paramref name="program"/> embedded (FontFile2), filled in green (0 0.6 0 rg)
    /// with the constant alpha <paramref name="alpha"/> (/ca), rendered.
    /// </summary>
    private static SKBitmap Ot14RenderA(byte[] program, double alpha)
    {
        var pdf = new TestPdf();
        var catalog = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog"))));
        var pages = pdf.Add(new PdfDictionary());
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Length1", PdfInteger.Of(program.Length))), program));
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("KneadColr")),
            ("FirstChar", PdfInteger.Of(65)), ("LastChar", PdfInteger.Of(65)), ("Widths", new PdfArray([PdfInteger.Of(667)])),
            ("Encoding", TestPdf.N("WinAnsiEncoding")),
            ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("KneadColr")), ("Flags", PdfInteger.Of(32)),
                ("FontBBox", TestPdf.Box(-203, -303, 1050, 910)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(905)),
                ("Descent", PdfInteger.Of(-212)), ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file))))));
        var content = pdf.Add(new PdfStream(new PdfDictionary(), "/G gs 0 0.6 0 rg BT /F1 150 Tf 20 40 Td (A) Tj ET"u8.ToArray()));
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("MediaBox", TestPdf.Box(0, 0, 160, 160)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(font)))),
                ("ExtGState", TestPdf.Dict(("G", TestPdf.Dict(("ca", new PdfReal(alpha)))))))), ("Contents", TestPdf.Ref(content))));
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        ((PdfDictionary)pdf.Get(catalog)).Set(PdfName.Pages, TestPdf.Ref(pages));
        pdf.RootNumber = catalog;
        using var doc = PdfDocument.Open(pdf.Build());
        return ContentImageTests.Render(doc, doc.Pages[0]);
    }

    /// <summary>
    /// The colour fonts of the render test: COLR (and CPAL) variants over Liberation Sans whose glyph „A“ would be red, partly transparent or
    /// in the foreground colour if the tables were read.
    /// </summary>
    private static (byte[] Colr, byte[] Cpal) Ot14RenderCase(string name)
    {
        var a = Ot14Gid('A');
        var o = Ot14Gid('O');
        var i = Ot14Gid('I');
        return name switch
        {
            // OT 10.3.3: layer 0 in palette entry 0 (red), layer 1 in the foreground colour (0xFFFF)
            "v0-red-and-foreground" => (Ot14Colr(version: 0, baseGlyphs: [(a, 0, 2)], layerRecords: [(o, 0), (a, 0xFFFF)]), Ot14Palette()),
            // OT 11.6.1: a single layer in the foreground colour
            "v0-foreground" => (Ot14Colr(version: 0, baseGlyphs: [(a, 0, 1)], layerRecords: [(a, 0xFFFF)]), Ot14Palette()),
            // OT 10.3.5 sentences 1–3: PaintGlyph → PaintSolid palette 0 (red) with alpha 0.5
            "v1-red-half" => (Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Solid(0, 0.5)))]), Ot14Palette()),
            // OT 10.3.5 sentence 4: one layer in the foreground colour, one red
            "v1-foreground-and-red" => (Ot14Colr(paints: [(a, Ot14Layers(2, 0))], layers: [Ot14Glyph(a, Ot14Solid(0xFFFF)), Ot14Glyph(i, Ot14Solid(0))]), Ot14Palette()),
            // OT 10.3.5 sentences 5–9 and 11.2 sentences 15–17: alpha out of range and a CPAL entry red with alpha 128
            "v1-alpha" => (Ot14Colr(paints: [(a, Ot14Layers(2, 0))], layers: [Ot14Glyph(a, Ot14Solid(0, 1.5)), Ot14Glyph(o, Ot14Solid(0, -0.25))]),
                Ot14Cpal(1, [0], [new Ot14Bgra(0, 0, 255, 0x80)])),
            _ => throw new ArgumentException(name),
        };
    }

    // ------------------------------------------------------------ rendering in a PDF (ot-14, ot-15, ot-17)

    /// <summary>
    /// OT 10.3.3 sentences 16–20, 23, 25; OT 10.3.5 sentences 1–3, 4, 5–9; OT 11.2 sentences 15–17; OT 11.6.1 sentences 1–4 (today's behaviour,
    /// question 3): an embedded TrueType font with COLR and CPAL is drawn from its outlines in the fill colour and constant alpha of the
    /// graphics state — the page looks pixel for pixel as with the same font without the colour tables: green, nowhere red.
    /// </summary>
    [Theory]
    [InlineData("v0-red-and-foreground", 1.0)]
    [InlineData("v0-foreground", 1.0)]
    [InlineData("v1-red-half", 1.0)]
    [InlineData("v1-foreground-and-red", 1.0)]
    [InlineData("v1-alpha", 1.0)]
    [InlineData("v1-alpha", 0.5)]
    public void Ot14_ColrFontInPdf_DrawnFromOutlineInFillColour(string name, double alpha)
    {
        var (colr, cpal) = Ot14RenderCase(name);
        var coloured = ColorFontBuilder.WithTables(Ot14SansFile.Value, new Dictionary<string, byte[]?> { ["COLR"] = colr, ["CPAL"] = cpal });
        using var withColr = Ot14RenderA(coloured, alpha);
        using var without = Ot14RenderA(Ot14SansFile.Value, alpha);
        Assert.Equal(without.Width, withColr.Width);
        Assert.Equal(without.Height, withColr.Height);
        int green = 0, red = 0;
        for (var y = 0; y < withColr.Height; y++)
        for (var x = 0; x < withColr.Width; x++)
        {
            var c = withColr.GetPixel(x, y);
            Assert.Equal(without.GetPixel(x, y), c);
            if (c.Green > c.Red + 40 && c.Green > c.Blue + 40) green++;
            if (c.Red > c.Green + 40) red++;
        }
        Assert.True(green > 1000, $"grün: {green} Pixel");
        Assert.Equal(0, red);
    }

    // ------------------------------------------------------------ 10.2.6 transformations

    /// <summary>
    /// OT 10.2.6 sentences 1–5, 7–8, 11–13, 14–17, 18–23, 24–26, 27–31, 32–33 (today's behaviour, question 3): COLR version 1 glyphs with
    /// PaintTransform (rotation by 90°), PaintTranslate (100, 0) over PaintScale (2, 2), PaintTranslate (50, −20), PaintScaleAroundCenter
    /// (2 and 0.5 around 500/500), PaintRotateAroundCenter (90° around 500/500), PaintSkew (45°), PaintScale (−1, 1), PaintRotate (30°) and a
    /// PaintRotateAroundCenter (45° around 200/300) next to the equivalent PaintTransform are not read: no colour glyphs, every glyph keeps its outline.
    /// </summary>
    [Fact]
    public void Ot14_ColrV1Transforms_NotRead_GlyphsKeepTheirOutlines()
    {
        var layer = Ot14Gid('L');   // not symmetric
        Ot14Node Fill() => Ot14Glyph(layer, Ot14Solid(0));
        var c = Math.Cos(Math.PI / 4);
        var s = Math.Sin(Math.PI / 4);
        Ot14ColrIgnored(Ot14Colr(paints:
        [
            (Ot14Gid('A'), Ot14Transform(Fill(), 0, 1, -1, 0, 0, 0)),
            (Ot14Gid('B'), Ot14Translate(Ot14Scale(Fill(), 2, 2), 100, 0)),
            (Ot14Gid('C'), Ot14Translate(Fill(), 50, -20)),
            (Ot14Gid('D'), Ot14ScaleAround(Fill(), 2 - 1.0 / 16384, 0.5, 500, 500)),   // 2.0 itself is beyond F2DOT14
            (Ot14Gid('E'), Ot14RotateAround(Fill(), 0.5, 500, 500)),
            (Ot14Gid('F'), Ot14Skew(Fill(), 0.25, 0)),
            (Ot14Gid('G'), Ot14Scale(Fill(), -1, 1)),
            (Ot14Gid('H'), Ot14Rotate(Fill(), 30 / 180.0)),
            (Ot14Gid('I'), Ot14RotateAround(Fill(), 0.25, 200, 300)),
            (Ot14Gid('J'), Ot14Transform(Fill(), c, s, -s, c, 200 - c * 200 + s * 300, 300 - s * 200 - c * 300)),
        ]));
    }

    // ------------------------------------------------------------ 10.2.7 compositing

    /// <summary>
    /// OT 10.2.7 sentences 1–4, 5–12 (today's behaviour, question 3): COLR version 0 with two overlapping layers (blue below, red with alpha
    /// 128 above) and COLR version 1 with PaintComposite XOR (mode 11) of a red and a blue rectangle are not read: no colour glyphs, outlines unchanged.
    /// </summary>
    [Fact]
    public void Ot14_ColrComposition_NotRead_GlyphsKeepTheirOutlines()
    {
        var a = Ot14Gid('A');
        Ot14ColrIgnored(Ot14Colr(version: 0, baseGlyphs: [(a, 0, 2)], layerRecords: [(Ot14Gid('O'), 0), (Ot14Gid('I'), 1)]),
            Ot14Cpal(2, [0], [Ot14BlueColour, new Ot14Bgra(0, 0, 255, 128)]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Composite(Ot14Glyph(Ot14Gid('-'), Ot14Solid(0)), 11, Ot14Glyph(Ot14Gid('I'), Ot14Solid(2))))]));
    }

    // ------------------------------------------------------------ 10.2.8 reuse

    /// <summary>
    /// OT 10.2.8.2 sentences 1–11, 10.2.8.3 sentences 1–3 and 9–11, 10.2.8.4 sentences 1–9, 10.2.10 sentences 1–10 (today's behaviour,
    /// question 3): COLR version 1 with two PaintGlyph sharing one PaintSolid (the same offset), a PaintColrLayers over three layers whose
    /// first is a PaintColrLayers over the layers 3 and 4, a PaintColrGlyph on a glyph without a record next to one on a glyph with a record,
    /// and a PaintColrLayers whose second layer is a PaintColrGlyph — none of it is read: no colour glyphs, outlines unchanged.
    /// </summary>
    [Fact]
    public void Ot14_ColrV1Reuse_NotRead_GlyphsKeepTheirOutlines()
    {
        var shared = Ot14Solid(1);
        Ot14ColrIgnored(Ot14Colr(paints: [(Ot14Gid('A'), Ot14Layers(2, 0))], layers: [Ot14Glyph(Ot14Gid('O'), shared), Ot14Glyph(Ot14Gid('I'), shared)]));
        Ot14ColrIgnored(Ot14Colr(paints: [(Ot14Gid('A'), Ot14Layers(3, 0))],
            layers: [Ot14Layers(2, 3), Ot14Glyph(Ot14Gid('O'), Ot14Solid(0)), Ot14Glyph(Ot14Gid('I'), Ot14Solid(1)), Ot14Glyph(Ot14Gid('L'), Ot14Solid(2)), Ot14Glyph(Ot14Gid('T'), Ot14Solid(3))]));
        Ot14ColrIgnored(Ot14Colr(paints:
        [
            (Ot14Gid('A'), Ot14ColrGlyph(Ot14Gid('Z'))),
            (Ot14Gid('B'), Ot14ColrGlyph(Ot14Gid('C'))),
            (Ot14Gid('C'), Ot14Glyph(Ot14Gid('O'), Ot14Solid(2))),
        ]));
        Ot14ColrIgnored(Ot14Colr(paints: [(Ot14Gid('A'), Ot14Layers(2, 0)), (Ot14Gid('B'), Ot14Glyph(Ot14Gid('B'), Ot14Solid(2)))],
            layers: [Ot14Glyph(Ot14Gid('O'), Ot14Solid(0)), Ot14ColrGlyph(Ot14Gid('B'))]));
    }

    // ------------------------------------------------------------ 10.2.8.1, 10.2.10 cycles and limits

    /// <summary>
    /// OT 10.2.8.1 sentence 11, 10.2.10 sentences 11–17 and 21, 28–32, 33–36 (today's behaviour, question 3): COLR version 1 with a cycle of
    /// two PaintColrGlyph („A“ → „B“ → „A“), a LayerList entry that is a PaintColrLayers on itself, a glyph whose second layer is a
    /// PaintColrGlyph on the glyph itself, a simple PaintGlyph → PaintSolid next to 64 nested PaintTranslate — parsed without an exception or
    /// endless loop, no colour glyphs, outlines unchanged.
    /// </summary>
    [Fact]
    public void Ot14_ColrV1CyclesAndDepth_NotRead_NoLoop()
    {
        var a = Ot14Gid('A');
        var b = Ot14Gid('B');
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14ColrGlyph(b)), (b, Ot14ColrGlyph(a))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Layers(1, 0))], layers: [Ot14Layers(1, 0)]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Layers(2, 0))], layers: [Ot14Glyph(Ot14Gid('O'), Ot14Solid(0)), Ot14ColrGlyph(a)]));
        var deep = Ot14Glyph(Ot14Gid('O'), Ot14Solid(1));
        for (var i = 0; i < 64; i++) deep = Ot14Translate(deep, 1, 0);
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Solid(0))), (b, deep)]));
    }

    // ------------------------------------------------------------ 10.2.10, 10.3.1, 10.3.4 malformed tables

    /// <summary>
    /// OT 10.2.10 sentences 22–27, 37–38; 10.3.1 sentences 7–9; 10.3.4 sentences 1–16, 28–43 (today's behaviour, question 3): COLR version 1
    /// with an offset behind the end of the table, a PaintColrLayers without LayerList, a PaintColrLayers whose slice runs past the end of the
    /// LayerList, a layer of the unknown format 99, a root of format 40, a BaseGlyphList with the glyphs 5 and 9 next to one claiming
    /// 0xFFFFFFFF records, and a ClipList with the ranges 5–9 and 10–12 next to one with overlapping ranges 5–9 and 8–12 — each parses
    /// without an exception and is ignored.
    /// </summary>
    [Fact]
    public void Ot14_ColrV1Malformed_NotRead_NoException()
    {
        var a = Ot14Gid('A');
        var b = Ot14Gid('B');
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Paint(10, Ot14Off24(0xFFFFFF), Ot14U16(a)))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Layers(2, 0))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Layers(5, 1))], layers: [Ot14Glyph(a, Ot14Solid(0)), Ot14Glyph(b, Ot14Solid(1))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Layers(2, 0))], layers: [Ot14Glyph(a, Ot14Solid(0)), Ot14Paint(99, Ot14U16(0))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Paint(40, Ot14U32(0))), (b, Ot14Glyph(b, Ot14Solid(0)))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(5, Ot14Glyph(5, Ot14Solid(0))), (9, Ot14Glyph(9, Ot14Solid(1)))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(5, Ot14Glyph(5, Ot14Solid(0))), (9, Ot14Glyph(9, Ot14Solid(1)))], paintRecordCount: 0xFFFFFFFF));
        var fill = Ot14Glyph(a, Ot14Solid(0));
        Ot14ColrIgnored(Ot14Colr(paints: [(11, fill)], clips: [(5, 9, Ot14ClipBox(0, 0, 500, 500)), (10, 12, Ot14ClipBox(0, 0, 1000, 1000))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(11, fill)], clips: [(5, 9, Ot14ClipBox(0, 0, 500, 500)), (8, 12, Ot14ClipBox(0, 0, 1000, 1000))]));
    }

    // ------------------------------------------------------------ 10.2.9 metrics and boundedness

    /// <summary>
    /// OT 10.2.9.1 sentences 1–2, 10.2.9.2 sentences 1–3 (today's behaviour, question 3): the advance of a colour glyph is the one of its base
    /// glyph — with COLR version 0 (layer glyph „i“) and version 1 (PaintGlyph of „i“) the shaped „A“ advances by the hmtx value of „A“,
    /// not by the narrower one of „i“.
    /// </summary>
    [Fact]
    public void Ot14_ColrAdvance_IsTheBaseGlyphAdvance()
    {
        var a = Ot14Gid('A');
        var i = Ot14Gid('i');
        Assert.NotEqual(Ot14Sans.Value.AdvanceWidth(a), Ot14Sans.Value.AdvanceWidth(i));
        foreach (var colr in (byte[][])[Ot14Colr(version: 0, baseGlyphs: [(a, 0, 1)], layerRecords: [(i, 0)]), Ot14Colr(paints: [(a, Ot14Glyph(i, Ot14Solid(0)))])])
        {
            var font = Ot14ColrIgnored(colr);
            var shaped = Assert.Single(TextShaper.For(font).Shape("A"));
            Assert.Equal(a, shaped.Glyph);
            Assert.Equal(Ot14Sans.Value.AdvanceWidth(a), shaped.XAdvance);
        }
    }

    /// <summary>
    /// OT 10.2.9.2 sentences 4–14, 15–17, 18–21; 10.3.4 sentences 44–51, 64–68 (today's behaviour, question 3): COLR version 1 without
    /// ClipList whose „A“ is an unbounded PaintSolid and „B“ a PaintGlyph; with a ClipBox (0, 0, 1000, 1000) for a PaintSolid root; without
    /// ClipList with a PaintLinearGradient root; with ClipBoxFormat1 (−100, −200, 1100, 900); with a ClipBox (0, 0, 500, 500) around a PaintGlyph
    /// — no colour glyphs, nothing drawn beyond the outlines.
    /// </summary>
    [Fact]
    public void Ot14_ColrV1BoundsAndClipBoxes_NotRead_GlyphsKeepTheirOutlines()
    {
        var a = Ot14Gid('A');
        var b = Ot14Gid('B');
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Solid(0)), (b, Ot14Glyph(b, Ot14Solid(0)))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Solid(0))], clips: [(a, a, Ot14ClipBox(0, 0, 1000, 1000))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Linear(Ot14RedToBlue(), 0, 0, 1000, 0, 0, 1000))]));
        var colr = Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Solid(0)))], clips: [(a, a, Ot14ClipBox(-100, -200, 1100, 900))]);
        Assert.True(Ot14Contains(colr, 0x01, 0xFF, 0x9C, 0xFF, 0x38, 0x04, 0x4C, 0x03, 0x84));
        Ot14ColrIgnored(colr);
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(Ot14Gid('O'), Ot14Solid(0)))], clips: [(a, a, Ot14ClipBox(0, 0, 500, 500))]));
    }

    // ------------------------------------------------------------ variations

    /// <summary>
    /// OT 10.2.9.2 sentences 22–24; 10.3.1 sentences 2–6; 10.3.2 sentences 30–34; 10.3.4 sentences 52–63, 69–70 (today's behaviour, question 3;
    /// PDF-Knead reads no variations): COLR version 1 with ClipBoxFormat2 and an ItemVariationStore; PaintVarSolid with a DeltaSetIndexMap, an
    /// ItemVariationStore and fvar; an ItemVariationStore without DeltaSetIndexMap; ClipBoxFormat2 (0, 0, 1000, 1000, varIndexBase 4); PaintVarSolid
    /// (varIndexBase 2) with a map entry 2 → (0, 1) — no colour glyphs, the glyphs as in the default instance.
    /// </summary>
    [Fact]
    public void Ot14_ColrV1Variations_NotRead_DefaultInstance()
    {
        var a = Ot14Gid('A');
        var box2 = new Ot14Node(Ot14U8(2), Ot14I16(0), Ot14I16(0), Ot14I16(1000), Ot14I16(1000), Ot14U32(4));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Solid(0)))], clips: [(a, a, box2)], varStore: Ot14VariationStore(10, 20, 30, 40, 50)));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14VarSolid(0, 1, 0)))], varIndexMap: Ot14DeltaSetIndexMap((0, 0)), varStore: Ot14VariationStore(-0x1000)),
            more: ("fvar", Ot14Fvar()));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14VarSolid(0, 1, 0)))], varStore: Ot14VariationStore(-0x1000)));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14VarSolid(0, 1, 2)))], varIndexMap: Ot14DeltaSetIndexMap((0, 0), (0, 0), (0, 1)),
            varStore: Ot14VariationStore(0, -0x1000)), more: ("fvar", Ot14Fvar()));
    }

    // ------------------------------------------------------------ 10.3, 10.3.1 to 10.3.4 header and records

    /// <summary>
    /// OT 10.2.10 sentences 39–40; 10.3.2 sentences 1–9; 10.3.3 sentences 1–4 (today's behaviour, question 3): Liberation Sans with COLR version 0
    /// (header 0000 0001, BaseGlyph record at 14, two Layer records at 20) and CPAL — the table is there (HasTable), but no colour glyphs and
    /// the outline of „A“ is the one without COLR.
    /// </summary>
    [Fact]
    public void Ot14_ColrV0_NotRead_BaseGlyphKeepsItsOutline()
    {
        var a = Ot14Gid('A');
        var colr = Ot14Colr(version: 0, baseGlyphs: [(a, 0, 2)], layerRecords: [(Ot14Gid('O'), 0), (Ot14Gid('I'), 2)]);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0, 0, 0, 14, 0, 0, 0, 20, 0, 2 }, colr[..14]);
        Assert.Equal(28, colr.Length);
        var font = Ot14ColrIgnored(colr);
        Assert.True(font.HasTable("COLR"));
        Assert.True(font.HasTable("CPAL"));
    }

    /// <summary>
    /// OT 10.3.2 sentences 11–22, 23–29 (today's behaviour, question 3): COLR version 1 with the header 0001, no version 0 records (NULL offsets),
    /// baseGlyphListOffset 34 and NULL for LayerList, ClipList, DeltaSetIndexMap and ItemVariationStore, „A“ as PaintGlyph → PaintSolid — no
    /// colour glyphs, outlines unchanged.
    /// </summary>
    [Fact]
    public void Ot14_ColrV1Header_NotRead_GlyphsKeepTheirOutlines()
    {
        var a = Ot14Gid('A');
        var colr = Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Solid(0)))]);
        Assert.Equal(new byte[] { 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 34, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, colr[..34]);
        Ot14ColrIgnored(colr);
    }

    /// <summary>
    /// OT 10.3.2.1 sentences 1–6, 7–9; 10.3.3 sentences 5–12, 13–15, 16–20, 23, 25; 10.3.4 sentences 17–27 (today's behaviour, question 3):
    /// COLR version 1 with a BaseGlyphList for „A“ and a BaseGlyph record for „B“; „A“ both in the BaseGlyphList (green) and in the BaseGlyph
    /// records (red); version 0 records for the glyphs 5, 9, 12 and one beyond the glyph count; „A“ with layers 0–1 and „B“ with layers 1–2
    /// sharing layer 1; a layer with palette index 0xFFFF; a LayerList whose second entry is a PaintColrLayers with two layers of its own —
    /// no colour glyphs, outlines unchanged.
    /// </summary>
    [Fact]
    public void Ot14_ColrRecordsAndLists_NotRead_GlyphsKeepTheirOutlines()
    {
        var a = Ot14Gid('A');
        var b = Ot14Gid('B');
        var o = Ot14Gid('O');
        var i = Ot14Gid('I');
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(o, Ot14Solid(1)))], baseGlyphs: [(b, 0, 1)], layerRecords: [(i, 0)]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Solid(1)))], baseGlyphs: [(a, 0, 1)], layerRecords: [(a, 0)]));
        Ot14ColrIgnored(Ot14Colr(version: 0, baseGlyphs: [(5, 0, 1), (9, 1, 1), (12, 2, 1), (Ot14Sans.Value.GlyphCount + 5, 0, 1)],
            layerRecords: [(o, 0), (i, 1), (a, 2)]));
        Ot14ColrIgnored(Ot14Colr(version: 0, baseGlyphs: [(a, 0, 2), (b, 1, 2)], layerRecords: [(o, 2), (i, 0), (Ot14Gid('L'), 1)]));
        Ot14ColrIgnored(Ot14Colr(version: 0, baseGlyphs: [(a, 0, 2)], layerRecords: [(o, 0), (a, 0xFFFF)]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Layers(3, 0))],
            layers: [Ot14Glyph(o, Ot14Solid(0)), Ot14Layers(2, 3), Ot14Glyph(i, Ot14Solid(1)), Ot14Glyph(Ot14Gid('L'), Ot14Solid(2)), Ot14Glyph(Ot14Gid('T'), Ot14Solid(3))]));
    }

    // ------------------------------------------------------------ writing: subsets and scrubbing

    /// <summary>
    /// OT 10.3.2 sentence 10; 10.3.6.2 sentence 3; 10.3.6.3 sentence 3; 10.3.6.4 sentence 3; 10.3.7 sentence 3; 10.3.7.3 sentence 4; 10.3.7.1
    /// sentences 9–10 (scrubbing part): a font with COLR (version 0 records and the paint formats 2, 5, 7, 9 and 13) and CPAL — a subset for new
    /// text (<see cref="TrueTypeSubsetter.Subset(byte[], IEnumerable{int}, bool)"/>) and a scrubbed font (<see cref="TrueTypeSubsetter.Scrub"/>)
    /// have neither COLR nor CPAL, a compressed one (keepAllTables) has both byte for byte.
    /// </summary>
    [Fact]
    public void Ot14_SubsetsAndScrubbing_HandleColrAndCpal()
    {
        var a = Ot14Gid('A');
        var o = Ot14Gid('O');
        var varLine = Ot14VarLine(0, (0, 0, 1, 0xFFFFFFFF), (1, 2, 1, 0xFFFFFFFF));
        var colr = Ot14Colr(
            paints:
            [
                (a, Ot14Glyph(o, Ot14Solid(0))),
                (Ot14Gid('B'), Ot14Glyph(o, Ot14Paint(5, varLine, Ot14I16(0), Ot14I16(0), Ot14I16(1000), Ot14I16(0), Ot14I16(0), Ot14I16(1000), Ot14U32(0xFFFFFFFF)))),
                (Ot14Gid('C'), Ot14Glyph(o, Ot14Paint(7, varLine, Ot14I16(500), Ot14I16(500), Ot14U16(0), Ot14I16(500), Ot14I16(500), Ot14U16(500), Ot14U32(0xFFFFFFFF)))),
                (Ot14Gid('D'), Ot14Glyph(o, Ot14Paint(9, varLine, Ot14I16(500), Ot14I16(500), Ot14F2(0), Ot14F2(1), Ot14U32(0xFFFFFFFF)))),
                (Ot14Gid('E'), Ot14Paint(13, Ot14Glyph(o, Ot14Solid(1)),
                    new Ot14Node(Ot14Fixed(1), Ot14Fixed(0), Ot14Fixed(0), Ot14Fixed(1), Ot14Fixed(0), Ot14Fixed(0), Ot14U32(0xFFFFFFFF)))),
            ],
            baseGlyphs: [(Ot14Gid('F'), 0, 1)], layerRecords: [(o, 2)]);
        var cpal = Ot14Palette();
        var file = ColorFontBuilder.WithTables(Ot14SansFile.Value, new Dictionary<string, byte[]?> { ["COLR"] = colr, ["CPAL"] = cpal });
        Assert.True(OpenTypeFont.Parse(file).HasTable("COLR"));

        var forText = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(file, [a]));
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(file, [a]));
        foreach (var font in (OpenTypeFont[])[forText, scrubbed])
        {
            Assert.False(font.HasTable("COLR"));
            Assert.False(font.HasTable("CPAL"));
        }
        var compressed = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(file, [a], keepAllTables: true));
        Assert.Equal(colr, compressed.TableData("COLR").ToArray());
        Assert.Equal(cpal, compressed.TableData("CPAL").ToArray());
    }

    /// <summary>
    /// OT 10.3.3 sentences 21–22, 24; 10.3.6.1 sentences 1–5 (subset part); 10.3.7.1 sentences 9–10 (writer): a layer glyph of COLR must name a
    /// glyph with an outline — compressing a font (keepAllTables) whose base glyph is used must either leave COLR out or keep the layer
    /// glyphs: COLR version 0 („A“ with the layer glyphs 300 and 301) and version 1 (glyph 3 → PaintGlyph glyph 5).
    /// </summary>
    [Fact(Skip = "M7-Befund: Verkleinern (Subset mit keepAllTables) behält COLR und CPAL, leert aber die Ebenen-Glyphen, auf die COLR zeigt")]
    public void Ot14_CompressedSubset_KeepsColrLayerGlyphsOrDropsColr()
    {
        var a = Ot14Gid('A');
        foreach (var (colr, used, layerGlyphs) in new (byte[], int, int[])[]
        {
            (Ot14Colr(version: 0, baseGlyphs: [(a, 0, 2)], layerRecords: [(300, 0), (301, 2)]), a, [300, 301]),
            (Ot14Colr(paints: [(3, Ot14Glyph(5, Ot14Solid(0)))]), 3, [5]),
        })
        {
            foreach (var g in layerGlyphs) Assert.False(Ot14Sans.Value.Outline(g).IsEmpty);
            var file = ColorFontBuilder.WithTables(Ot14SansFile.Value, new Dictionary<string, byte[]?> { ["COLR"] = colr, ["CPAL"] = Ot14Palette() });
            var compressed = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(file, [used], keepAllTables: true));
            Assert.True(!compressed.HasTable("COLR") || layerGlyphs.All(g => !compressed.Outline(g).IsEmpty),
                "COLR geblieben, Ebenen-Glyphen leer: " + string.Join(", ", layerGlyphs.Where(g => compressed.Outline(g).IsEmpty)));
        }
    }
}
