using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Fonts;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-13 (OpenType, COLR 10 to 10.2.5): PDF-Knead reads neither COLR nor CPAL (question 3 is open). The tests hold today's
/// behaviour for colour fonts built here from Liberation Sans — COLR version 0 (layers) and version 1 (a paint graph with gradients,
/// transformations, composition, cycles and variations): no colour glyphs, every glyph drawn with its own outline in the colour of the text
/// state; writing new subsets and scrubbing drop the tables, compressing copies them byte for byte.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (ot-13)

    private static readonly Lazy<byte[]> Ot13SansFile = new(() => EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf"));
    private static readonly Lazy<OpenTypeFont> Ot13Sans = new(() => OpenTypeFont.Parse(Ot13SansFile.Value));

    private static int Ot13Gid(char c) => Ot13Sans.Value.GlyphForUnicode(c);

    private static int Ot13F2Dot14(double v) => (int)Math.Round(v * 16384) & 0xFFFF;

    /// <summary>CPAL version 0: palettes of equal length, colours as (red, green, blue, alpha).</summary>
    private static byte[] Ot13Cpal(params (int R, int G, int B, int A)[][] palettes)
    {
        var entries = palettes[0].Length;
        var w = new Ot12Bytes().U16(0).U16(entries).U16(palettes.Length).U16(entries * palettes.Length).U32(12 + 2 * palettes.Length);
        for (var i = 0; i < palettes.Length; i++) w.U16(i * entries);
        foreach (var palette in palettes)
            foreach (var (r, g, b, a) in palette) w.U8(b).U8(g).U8(r).U8(a);
        return w.ToArray();
    }

    /// <summary>COLR version 0: base glyph records (sorted by glyph) naming slices of the layer records.</summary>
    private static byte[] Ot13ColrV0((int Glyph, int First, int Count)[] bases, (int Glyph, int Palette)[] layers)
    {
        var w = new Ot12Bytes().U16(0).U16(bases.Length).U32(14).U32(14 + 6 * bases.Length).U16(layers.Length);
        foreach (var (glyph, first, count) in bases) w.U16(glyph).U16(first).U16(count);
        foreach (var (glyph, palette) in layers) w.U16(glyph).U16(palette);
        return w.ToArray();
    }

    // Paint tables of COLR version 1, each followed by its children (offsets from the start of the paint table).

    private static byte[] Ot13Solid(int palette, double alpha) => new Ot12Bytes().U8(2).U16(palette).U16(Ot13F2Dot14(alpha)).ToArray();

    private static byte[] Ot13VarSolid(int palette, double alpha, int varIndexBase) =>
        new Ot12Bytes().U8(3).U16(palette).U16(Ot13F2Dot14(alpha)).U32(varIndexBase).ToArray();

    /// <summary>A ColorLine (extend 0 pad, 1 repeat, 2 reflect) or, with <paramref name="varIndexBase"/>, a VarColorLine.</summary>
    private static byte[] Ot13ColorLine(int extend, (double Offset, int Palette, double Alpha)[] stops, int? varIndexBase = null)
    {
        var w = new Ot12Bytes().U8(extend).U16(stops.Length);
        foreach (var (offset, palette, alpha) in stops)
        {
            w.U16(Ot13F2Dot14(offset)).U16(palette).U16(Ot13F2Dot14(alpha));
            if (varIndexBase is { } v) w.U32(v);
        }
        return w.ToArray();
    }

    private static byte[] Ot13Linear(byte[] line, int x0, int y0, int x1, int y1, int x2, int y2, int? varIndexBase = null)
    {
        var w = new Ot12Bytes().U8(varIndexBase is null ? 4 : 5).U24(varIndexBase is null ? 16 : 20).U16(x0).U16(y0).U16(x1).U16(y1).U16(x2).U16(y2);
        if (varIndexBase is { } v) w.U32(v);
        return w.Bytes(line).ToArray();
    }

    private static byte[] Ot13Radial(byte[] line, int x0, int y0, int r0, int x1, int y1, int r1) =>
        new Ot12Bytes().U8(6).U24(16).U16(x0).U16(y0).U16(r0).U16(x1).U16(y1).U16(r1).Bytes(line).ToArray();

    private static byte[] Ot13Sweep(byte[] line, int cx, int cy, double startHalfTurns, double endHalfTurns) =>
        new Ot12Bytes().U8(8).U24(12).U16(cx).U16(cy).U16(Ot13F2Dot14(startHalfTurns)).U16(Ot13F2Dot14(endHalfTurns)).Bytes(line).ToArray();

    /// <summary>PaintGlyph; without a child its paint offset is 0.</summary>
    private static byte[] Ot13Glyph(int glyph, byte[]? child) =>
        new Ot12Bytes().U8(10).U24(child is null ? 0 : 6).U16(glyph).Bytes(child ?? Array.Empty<byte>()).ToArray();

    private static byte[] Ot13ColrGlyph(int glyph) => new Ot12Bytes().U8(11).U16(glyph).ToArray();

    private static byte[] Ot13ColrLayers(int count, int first) => new Ot12Bytes().U8(1).U8(count).U32(first).ToArray();

    /// <summary>PaintTransform with an Affine2x3 (16.16 values) after the child.</summary>
    private static byte[] Ot13Transform(byte[] child, double xx, double yx, double xy, double yy, double dx, double dy)
    {
        var w = new Ot12Bytes().U8(12).U24(7).U24(7 + child.Length).Bytes(child);
        foreach (var v in (double[])[xx, yx, xy, yy, dx, dy]) w.U32((long)Math.Round(v * 65536) & 0xFFFFFFFF);
        return w.ToArray();
    }

    private static byte[] Ot13Scale(byte[] child, double sx, double sy) =>
        new Ot12Bytes().U8(16).U24(8).U16(Ot13F2Dot14(sx)).U16(Ot13F2Dot14(sy)).Bytes(child).ToArray();

    private static byte[] Ot13Rotate(byte[] child, double halfTurns) => new Ot12Bytes().U8(24).U24(6).U16(Ot13F2Dot14(halfTurns)).Bytes(child).ToArray();

    private static byte[] Ot13Composite(byte[] source, int mode, byte[] backdrop) =>
        new Ot12Bytes().U8(32).U24(8).U8(mode).U24(8 + source.Length).Bytes(source).Bytes(backdrop).ToArray();

    /// <summary>
    /// COLR version 1: the base glyph paint records (sorted by glyph), the layer list and an item variation store with one region on one axis
    /// and one delta (index 0) — the paints with a variation index refer to it.
    /// </summary>
    private static byte[] Ot13ColrV1((int Glyph, byte[] Paint)[] bases, byte[][] layers)
    {
        var baseList = new Ot12Bytes().U32(bases.Length);
        var at = 4 + 6 * bases.Length;
        foreach (var (glyph, paint) in bases)
        {
            baseList.U16(glyph).U32(at);
            at += paint.Length;
        }
        foreach (var (_, paint) in bases) baseList.Bytes(paint.AsSpan());
        var layerList = new Ot12Bytes().U32(layers.Length);
        at = 4 + 4 * layers.Length;
        foreach (var paint in layers)
        {
            layerList.U32(at);
            at += paint.Length;
        }
        foreach (var paint in layers) layerList.Bytes(paint.AsSpan());
        // item variation store: header 12, region list 10 (one axis, region 0 … 1 … 1), one variation data with one 8-bit delta of +0.25
        var store = new Ot12Bytes().U16(1).U32(12).U16(1).U32(22)
            .U16(1).U16(1).U16(0).U16(0x4000).U16(0x4000)
            .U16(1).U16(0).U16(1).U16(0).U8(16);
        const int header = 34;
        var colr = new Ot12Bytes().U16(1).U16(0).U32(0).U32(0).U16(0)
            .U32(header).U32(header + baseList.Length).U32(0).U32(0).U32(header + baseList.Length + layerList.Length);
        return colr.Bytes(baseList.ToArray()).Bytes(layerList.ToArray()).Bytes(store.ToArray()).ToArray();
    }

    /// <summary>An fvar table with one axis (wght 100 … 400 … 900) and no named instance.</summary>
    private static byte[] Ot13Fvar() =>
        new Ot12Bytes().U16(1).U16(0).U16(16).U16(2).U16(1).U16(20).U16(0).U16(8)
            .Bytes("wght"u8).U32(100L << 16).U32(400L << 16).U32(900L << 16).U16(0).U16(256).ToArray();

    /// <summary>Two palettes of four colours: half transparent red, opaque blue, green, yellow — and their counterparts.</summary>
    private static byte[] Ot13Palettes() => Ot13Cpal(
        [(255, 0, 0, 128), (0, 0, 255, 255), (0, 200, 0, 255), (255, 255, 0, 255)],
        [(0, 255, 255, 255), (255, 0, 255, 255), (0, 0, 0, 255), (0, 0, 128, 255)]);

    /// <summary>
    /// Liberation Sans with COLR version 0: „A“ has the layers „B“ (palette entry 0), „C“ (1) and „D“ (0xFFFF, the text colour); „B“ has an
    /// overlapping slice (layers 1–3, the last „E“ with entry 1). With or without CPAL.
    /// </summary>
    private static byte[] Ot13ColrV0Font(bool withCpal)
    {
        var (a, b, c, d, e) = (Ot13Gid('A'), Ot13Gid('B'), Ot13Gid('C'), Ot13Gid('D'), Ot13Gid('E'));
        var colr = Ot13ColrV0([(a, 0, 3), (b, 1, 3)], [(b, 0), (c, 1), (d, 0xFFFF), (e, 1)]);
        return ColorFontBuilder.WithTables(Ot13SansFile.Value, new Dictionary<string, byte[]?> { ["COLR"] = colr, ["CPAL"] = withCpal ? Ot13Palettes() : null });
    }

    /// <summary>
    /// Liberation Sans with COLR version 1, CPAL, fvar and an item variation store; the base glyphs „A“ to „H“:
    /// A — PaintColrLayers over layers 1 and 2 of three (PaintGlyph „B“ with a linear gradient over a VarColorLine with unsorted stops; PaintTransform
    ///     rotating PaintGlyph „C“ with PaintVarSolid; layer 0, PaintGlyph „B“ with PaintSolid, is not named);
    /// B — PaintGlyph „B“ with a radial gradient whose stops share one offset, and repeat;
    /// C — PaintColrGlyph „D“, and D — PaintColrGlyph „C“ (a cycle);
    /// E — PaintComposite (source over): PaintGlyph „B“ with a sweep gradient from 45° to 45° and reflect over PaintScale (1, 0) around a radial
    ///     gradient with both centres on the x axis;
    /// F — PaintGlyph without a child (paint offset 0);
    /// G — PaintColrLayers whose only layer (index 3) is that PaintColrLayers itself (a cycle);
    /// H — PaintRotate around PaintGlyph „H“ with a linear gradient whose p1 equals p0, over a ColorLine without stops.
    /// </summary>
    private static byte[] Ot13ColrV1Font()
    {
        var g = "ABCDEFGH".ToDictionary(c => c, Ot13Gid);
        byte[] twoStops = Ot13ColorLine(0, [(1.0, 1, 1.0), (0.0, 0, 1.0)], varIndexBase: 0);
        byte[] sameOffset = Ot13ColorLine(1, [(0.5, 0, 1.0), (0.5, 2, 1.0), (0.5, 1, 1.0)]);
        byte[] reflect = Ot13ColorLine(2, [(0.0, 0, 1.0), (1.0, 1, 0.5)]);
        byte[] none = Ot13ColorLine(0, []);
        byte[][] layers =
        [
            Ot13Glyph(g['B'], Ot13Solid(0, 1)),
            Ot13Glyph(g['B'], Ot13Linear(twoStops, 0, 0, 100, 0, 0, 100, varIndexBase: 0)),
            Ot13Transform(Ot13Glyph(g['C'], Ot13VarSolid(2, 0.5, 0)), 0, 1, -1, 0, 0, 0),
            Ot13ColrLayers(1, 3),
        ];
        (int, byte[])[] bases =
        [
            (g['A'], Ot13ColrLayers(2, 1)),
            (g['B'], Ot13Glyph(g['B'], Ot13Radial(sameOffset, 50, 50, 0, 50, 50, 50))),
            (g['C'], Ot13ColrGlyph(g['D'])),
            (g['D'], Ot13ColrGlyph(g['C'])),
            (g['E'], Ot13Composite(Ot13Glyph(g['B'], Ot13Sweep(reflect, 50, 50, 0.25, 0.25)), 3,
                Ot13Scale(Ot13Radial(reflect, 30, 0, 10, 70, 0, 30), 1, 0))),
            (g['F'], Ot13Glyph(g['F'], null)),
            (g['G'], Ot13ColrLayers(1, 3)),
            (g['H'], Ot13Rotate(Ot13Glyph(g['H'], Ot13Linear(none, 10, 10, 10, 10, 0, 100)), 0.5)),
        ];
        return ColorFontBuilder.WithTables(Ot13SansFile.Value, new Dictionary<string, byte[]?>
        {
            ["COLR"] = Ot13ColrV1(bases, layers),
            ["CPAL"] = Ot13Palettes(),
            ["fvar"] = Ot13Fvar(),
        });
    }

    /// <summary>A page that shows <paramref name="text"/> in red with the given TrueType program embedded (WinAnsiEncoding); the document and its font dictionary.</summary>
    private static (PdfDocument Doc, PdfDictionary Font) Ot13Page(byte[] program, string text)
    {
        var (doc, _) = InterpreterTests.Page($"1 0 0 rg BT /F1 24 Tf 5 80 Td ({text}) Tj ET", pdf =>
        {
            var file = pdf.Add(TestPdf.FlateStream(program, TestPdf.Dict(("Length1", PdfInteger.Of(program.Length)))));
            var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ProbeColour")), ("Flags", PdfInteger.Of(32)),
                ("FontBBox", TestPdf.Box(-203, -303, 1050, 910)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(905)),
                ("Descent", PdfInteger.Of(-212)), ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file)));
            var font = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("ProbeColour")),
                ("Encoding", TestPdf.N("WinAnsiEncoding")), ("FontDescriptor", descriptor));
            return TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(pdf.Add(font))))));
        });
        return (doc, doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!);
    }

    /// <summary>The page of <see cref="Ot13Page"/> rendered.</summary>
    private static SKBitmap Ot13Render(byte[] program, string text)
    {
        var (doc, _) = Ot13Page(program, text);
        using (doc) return ContentImageTests.Render(doc, doc.Pages[0]);
    }

    /// <summary>
    /// The colour font draws as plain Liberation Sans: the same outlines, no colour glyphs, the same pixels — and those are white or red only
    /// (the text colour; nothing from the palettes).
    /// </summary>
    private static void Ot13DrawnLikeThePlainFont(byte[] colour, string text)
    {
        var font = OpenTypeFont.Parse(colour);
        Assert.True(font.HasTable("COLR"));
        Assert.Null(ColorGlyphs.For(font));
        foreach (var c in text) Assert.True(Iso03SameOutline(Ot13Sans.Value.Outline(Ot13Gid(c)), font.Outline(Ot13Gid(c))), $"Umriss von {c}");
        using var bitmap = Ot13Render(colour, text);
        using var plain = Ot13Render(Ot13SansFile.Value, text);
        Assert.True(bitmap.Bytes.AsSpan().SequenceEqual(plain.Bytes), "die Seite sieht anders aus als mit der einfarbigen Schrift");
        var red = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var p = bitmap.GetPixel(x, y);
                Assert.True(p.Green == p.Blue && p.Red >= p.Green, $"Farbe {p} bei {x},{y}");
                if (ContentImageTests.Red(p)) red++;
            }
        Assert.True(red > 100, $"{red} rote Pixel");
    }

    // ------------------------------------------------------------ 10.1, 10.2.1, 10.2.2, 10.2.5 (version 0)

    /// <summary>
    /// 10.1 sentences 1–4, 11–13, 22–23, 10.2.1 sentences 9–13, 10.2.2 sentences 1–3 and 10.2.5 sentences 1–8 (reader, today's behaviour;
    /// question 3): COLR version 0 is not read — a font whose base glyph „A“ has three layers (palette entries 0, 1 and 0xFFFF, the last being
    /// the text colour) and whose „B“ uses an overlapping slice of the layer records, with two palettes in CPAL or without CPAL: no colour glyphs,
    /// „A“ and „B“ keep their own outlines, and the page draws them exactly as plain Liberation Sans in the fill colour of the text state (red),
    /// with no colour of the palettes; nothing reads a colour record for 0xFFFF.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ColrV0Font_IsNotRead_TheBaseGlyphsDrawInTheTextColour(bool withCpal) => Ot13DrawnLikeThePlainFont(Ot13ColrV0Font(withCpal), "AB");

    // ------------------------------------------------------------ 10.1, 10.2.1 to 10.2.5 (version 1)

    /// <summary>
    /// 10.1 sentences 15–16, 18–20, 10.2.1 sentences 15–31, 10.2.2 sentences 4–10, 10.2.3 sentences 1–3, 10.2.3.1 sentences 1–20, 24–37, 41–42, 47–59,
    /// 10.2.3.2 sentences 1–15, 19–33, 10.2.3.3 sentences 1–22, 29–55, 57–83, 10.2.3.4 sentences 1–19, 24–29, 32–42, 10.2.4 sentences 1–6, 9–11, 13–14
    /// and 10.2.5 sentences 10–38 (reader, today's behaviour; question 3): the paint graph of COLR version 1 is not read — a variable font (fvar,
    /// item variation store) whose base glyphs use PaintColrLayers with a layer list, PaintSolid and PaintVarSolid, linear, radial and sweep gradients
    /// over ColorLine and VarColorLine (unsorted stops, stops sharing an offset, no stops, pad, repeat and reflect), degenerate gradients (p1 = p0,
    /// equal start and end angle with reflect, a scale that flattens the circles to a line), PaintTransform, PaintScale, PaintRotate, PaintComposite,
    /// PaintColrGlyph and PaintColrLayers in cycles and a PaintGlyph without a child: no colour glyphs, every glyph keeps its own outline (default
    /// instance), and the page draws „ABCDEFGH“ exactly as plain Liberation Sans in the text colour — without hanging on the cycles.
    /// </summary>
    [Fact]
    public void ColrV1Font_PaintGraphIsNotRead_TheBaseGlyphsDrawInTheTextColour() => Ot13DrawnLikeThePlainFont(Ot13ColrV1Font(), "ABCDEFGH");

    /// <summary>
    /// 10.1 sentences 5–6 (reader, today's behaviour; question 3): colour glyphs would apply after shaping — a COLR version 0 font whose base glyph
    /// is the ligature the shaper makes of „fi“ (a GSUB „liga“ lookup f + i → ﬁ written here): shaping gives that ligature glyph as for the plain
    /// font, and it keeps its own outline (no layers, no colour glyphs).
    /// </summary>
    [Fact]
    public void ColrLigature_ShapesToTheBaseGlyph_DrawnWithoutLayers()
    {
        var ligature = Ot13Gid('ﬁ') is > 0 and var fi ? fi : Ot13Gid('Z');
        var gsub = ShaperLimitTests.Gsub(1, [0], t =>
        {
            t.U16(1).U16(4);                                  // LookupList: one lookup at +4
            t.U16(4).U16(0).U16(1).U16(8);                    // ligature substitution, one subtable at +8
            t.U16(1).U16(8).U16(1).U16(14);                   // format 1: coverage at +8, one ligature set at +14
            t.U16(1).U16(1).U16(Ot13Gid('f'));                // coverage: „f“
            t.U16(1).U16(4);                                  // ligature set: one ligature at +4
            t.U16(ligature).U16(2).U16(Ot13Gid('i'));         // ﬁ from „f“ and „i“
        });
        var plain = OpenTypeFont.Parse(ColorFontBuilder.WithTables(Ot13SansFile.Value, new Dictionary<string, byte[]?> { ["GSUB"] = gsub, ["GPOS"] = null }));
        Assert.Equal([ligature], TextShaper.For(plain).Shape("fi").Select(s => s.Glyph));
        var colr = Ot13ColrV0([(ligature, 0, 2)], [(Ot13Gid('B'), 0), (Ot13Gid('C'), 1)]);
        var font = OpenTypeFont.Parse(ColorFontBuilder.WithTables(Ot13SansFile.Value,
            new Dictionary<string, byte[]?> { ["GSUB"] = gsub, ["GPOS"] = null, ["COLR"] = colr, ["CPAL"] = Ot13Palettes() }));
        Assert.Equal([ligature], TextShaper.For(font).Shape("fi").Select(s => s.Glyph));
        Assert.Null(ColorGlyphs.For(font));
        Assert.True(Iso03SameOutline(Ot13Sans.Value.Outline(ligature), font.Outline(ligature)));
    }

    /// <summary>
    /// 10.1 sentences 24–26 (today's behaviour): applications that fake bold should not do so on colour glyphs — PDF-Knead shows no colour glyphs
    /// from COLR, so edited text in a COLR font of the file, made bold, is faked bold (stroked, 2 Tr) on the plain outlines of the base glyphs.
    /// </summary>
    [Fact]
    public void ColrFontOfTheFile_FakeBold_StrokesThePlainOutlines()
    {
        var (doc, font) = Ot13Page(Ot13ColrV0Font(withCpal: true), "AB");
        using (doc)
        {
            var style = new RichStyle { Bold = true, Size = 20, Original = new OriginalFont(doc, font, FakeBold: true, FakeItalic: false) };
            var element = new RichTextElement { Rect = new PdfRectangle(10, 10, 190, 60), Content = new RichText("AB", style) };
            Assert.All(RichLayout.Of(element).Lines.SelectMany(l => l.Glyphs), g => Assert.True(g.Original is not null && g.FakeBold));
            var content = System.Text.Encoding.ASCII.GetString(ElementAppearance.For(element).Form.RawData!);
            Assert.Contains(" 2 Tr ", content);
        }
    }

    // ------------------------------------------------------------ 10.2.1, 10.2.3.1, 10.2.3.3, 10.2.5 (writing)

    /// <summary>
    /// 10.2.1 sentences 7–8, 10.2.3.1 sentences 43–46, 10.2.3.3 sentence 56 and 10.2.5 sentences 4–8 (writer, today's behaviour): the shapes of the
    /// layers are glyphs of the font — compressing a COLR font that shows only „A“ (subset keeping all tables) copies COLR and CPAL byte for byte,
    /// while the layer glyphs „B“, „C“, „D“ lose their outlines (only the glyphs of the content stay); a new subset and scrubbing leave COLR and
    /// CPAL out.
    /// </summary>
    [Fact]
    public void ColrFont_CompressingCopiesTheTables_NewSubsetsAndScrubbingDropThem()
    {
        var colour = Ot13ColrV0Font(withCpal: true);
        var original = OpenTypeFont.Parse(colour);
        var a = Ot13Gid('A');
        var compressed = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(colour, [0, a], keepAllTables: true));
        foreach (var tag in (string[])["COLR", "CPAL"]) Assert.True(original.TableData(tag).SequenceEqual(compressed.TableData(tag)), tag);
        Assert.False(compressed.Outline(a).IsEmpty);
        foreach (var c in "BCD") Assert.True(compressed.Outline(Ot13Gid(c)).IsEmpty, $"Umriss von {c}");
        foreach (var rewritten in new[] { TrueTypeSubsetter.Subset(colour, [0, a]), TrueTypeSubsetter.Scrub(colour, [0, a]) })
        {
            var font = OpenTypeFont.Parse(rewritten);
            Assert.False(font.HasTable("COLR") || font.HasTable("CPAL"));
        }
    }
}
