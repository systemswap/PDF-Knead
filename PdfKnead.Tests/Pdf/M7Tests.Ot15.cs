namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-15 (OpenType, COLR 10.3.5 and 10.3.6 to 10.3.6.4): colour references, ColorStop and ColorLine, the paint formats 1 to 7.
/// PDF-Knead reads neither COLR nor CPAL (question 3 is open): the tests check today's behaviour with the COLR/CPAL builders of ot-14 —
/// the font parses without an exception, no colour glyphs are offered, every glyph keeps its outline and advance.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// OT 10.3.5 sentences 1–3, 4, 5–9; 10.3.6.2 sentences 1–2 and 4–5, 6–10 (today's behaviour, question 3): COLR version 1 with PaintGlyph →
    /// PaintSolid (palette 0, alpha 0.5); a layer in the foreground colour (0xFFFF) next to a red one; alpha 1.5 (F2DOT14 0x6000) and −0.25 with
    /// a CPAL alpha of 128; PaintSolid palette 1 (green); the bytes 02 0005 2000 (palette 5, alpha 0.5) with a CPAL of only three entries —
    /// no colour glyphs, the glyphs from their outlines (the drawing in the fill colour: <see cref="Ot14_ColrFontInPdf_DrawnFromOutlineInFillColour"/>).
    /// </summary>
    [Fact]
    public void Ot15_ColrV1Solids_NotRead_GlyphsKeepTheirOutlines()
    {
        var a = Ot14Gid('A');
        var o = Ot14Gid('O');
        var i = Ot14Gid('I');
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Solid(0, 0.5)))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Layers(2, 0))], layers: [Ot14Glyph(o, Ot14Solid(0xFFFF)), Ot14Glyph(i, Ot14Solid(0))]));
        var alpha = Ot14Colr(paints: [(a, Ot14Layers(2, 0))], layers: [Ot14Glyph(o, Ot14Solid(0, 1.5)), Ot14Glyph(i, Ot14Solid(0, -0.25))]);
        Assert.True(Ot14Contains(alpha, 0x02, 0x00, 0x00, 0x60, 0x00));
        Assert.True(Ot14Contains(alpha, 0x02, 0x00, 0x00, 0xF0, 0x00));
        Ot14ColrIgnored(alpha, Ot14Cpal(1, [0], [new Ot14Bgra(0, 0, 255, 128)]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Solid(1)))]));
        var outside = Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Solid(5, 0.5)))]);
        Assert.True(Ot14Contains(outside, 0x02, 0x00, 0x05, 0x20, 0x00));
        Ot14ColrIgnored(outside, Ot14Cpal(3, [0], [Ot14RedColour, Ot14GreenColour, Ot14BlueColour]));
    }

    /// <summary>
    /// OT 10.3.5 sentences 12–15, 16–20, 45–47, 48–57, 58; 10.3.6.3 sentences 1–2 and 4–8; 10.3.6.4 sentences 1–2 and 4–8 (today's behaviour,
    /// question 3): COLR version 1 with PaintGlyph → PaintLinearGradient (stops 0 red, 1 blue); a ColorStop of the bytes 2000 0003 4000; stops in
    /// descending order; three fonts with the extend modes 0, 1 and 2; extend 7; PaintRadialGradient around one centre — no colour glyphs,
    /// no gradient, the glyphs from their outlines.
    /// </summary>
    [Fact]
    public void Ot15_ColrV1ColorLinesAndGradients_NotRead_GlyphsKeepTheirOutlines()
    {
        var a = Ot14Gid('A');
        var b = Ot14Gid('B');
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Linear(Ot14RedToBlue(), 0, 0, 1000, 0, 0, 1000)))]));
        var stop = Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Linear(Ot14Line(0, (0, 0, 1), (0.5, 3, 1)), 0, 0, 1000, 0, 0, 1000)))]);
        Assert.True(Ot14Contains(stop, 0x20, 0x00, 0x00, 0x03, 0x40, 0x00));
        Ot14ColrIgnored(stop);
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Linear(Ot14Line(0, (1, 2, 1), (0, 0, 1)), 0, 0, 1000, 0, 0, 1000)))]));
        foreach (var extend in (int[])[0, 1, 2])
            Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Linear(Ot14RedToBlue(extend), 0, 0, 500, 0, 0, 500)))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Linear(Ot14RedToBlue(7), 0, 0, 500, 0, 0, 500)))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(b, Ot14Glyph(b, Ot14Radial(Ot14RedToBlue(), 500, 500, 0, 500, 500, 500)))]));
    }

    /// <summary>
    /// OT 10.3.6.1 sentences 1–5 (reading part; the subset part: <see cref="Ot14_CompressedSubset_KeepsColrLayerGlyphsOrDropsColr"/>) (today's
    /// behaviour, question 3): COLR version 1 with PaintColrLayers over two layers, each a PaintGlyph on its own layer glyph — no colour glyphs,
    /// only the base glyph is drawn, from its outline.
    /// </summary>
    [Fact]
    public void Ot15_ColrV1PaintColrLayers_NotRead_BaseGlyphOnly()
    {
        Ot14ColrIgnored(Ot14Colr(paints: [(Ot14Gid('A'), Ot14Layers(2, 0))],
            layers: [Ot14Glyph(Ot14Gid('O'), Ot14Solid(2)), Ot14Glyph(Ot14Gid('I'), Ot14Solid(0))]));
    }

    /// <summary>
    /// OT 10.3.5 sentences 32–39; 10.3.6 sentences 1–6; 10.3.6.1 sentences 6–10, 11–12; 10.3.6.3 sentences 9–18; 10.3.6.4 sentences 9–18
    /// (today's behaviour, question 3): COLR version 1 with a ColorLine claiming 0xFFFF stops at the end of the table with only two present;
    /// the unassigned formats 0 and 33 in the paint tree; a PaintColrLayers with firstLayerIndex 0xFFFFFFF0 and 255 layers; a PaintColrLayers
    /// that names itself through the LayerList and a chain of 10 000 nested PaintColrLayers; a PaintLinearGradient with its ColorLine offset
    /// 0xFFFFFF and p₀ = p₁ = p₂; a PaintRadialGradient with both radii 0 around the same centre and its ColorLine behind the end — each parses
    /// without an exception, stack overflow or endless loop, and is ignored.
    /// </summary>
    [Fact]
    public void Ot15_ColrV1MalformedPaints_NotRead_NoException()
    {
        var a = Ot14Gid('A');
        var b = Ot14Gid('B');
        var shortLine = new Ot14Node(Ot14U8(0), Ot14U16(0xFFFF), Ot14F2(0), Ot14U16(0), Ot14F2(1), Ot14F2(1), Ot14U16(2), Ot14F2(1));
        var colr = Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Linear(shortLine, 0, 0, 1000, 0, 0, 1000)))]);
        Assert.True(colr.AsSpan().EndsWith(shortLine.Fields.Cast<Ot14Num>().SelectMany(n => Enumerable.Range(0, n.Size).Select(k => (byte)(n.Value >> (8 * (n.Size - 1 - k))))).ToArray()));
        Ot14ColrIgnored(colr);
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Paint(0))), (b, Ot14Paint(33, Ot14U16(0)))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Layers(255, 0xFFFFFFF0))], layers: [Ot14Glyph(a, Ot14Solid(0)), Ot14Glyph(b, Ot14Solid(1))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Layers(1, 0))], layers: [Ot14Layers(1, 0)]));
        var chain = Enumerable.Range(0, 10_000).Select(i => Ot14Layers(1, i + 1)).Append(Ot14Glyph(a, Ot14Solid(0))).ToList();
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Layers(1, 0))], layers: chain));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Linear(Ot14Off24(0xFFFFFF), 300, 300, 300, 300, 300, 300)))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Radial(Ot14Off24(0xFFFFFF), 500, 500, 0, 500, 500, 0)))]));
    }

    /// <summary>
    /// OT 10.3.5 sentences 21–31, 40–44; 10.3.6.2 sentences 11–20; 10.3.6.3 sentences 19–37; 10.3.6.4 sentences 19–37 (today's behaviour,
    /// question 3; PDF-Knead reads no variations): variable fonts (fvar) with COLR version 1, a DeltaSetIndexMap and an ItemVariationStore —
    /// a VarColorStop with a delta for stopOffset, PaintVarLinearGradient with a VarColorLine of two VarColorStops, PaintVarSolid with an alpha
    /// delta, PaintVarLinearGradient with a delta on x1, PaintVarRadialGradient with a delta on radius1 — no colour glyphs, the glyphs as in
    /// the default instance.
    /// </summary>
    [Fact]
    public void Ot15_ColrV1VariableSolidsAndGradients_NotRead_DefaultInstance()
    {
        var a = Ot14Gid('A');
        var map = Ot14DeltaSetIndexMap((0, 0), (0, 1), (0, 2), (0, 3), (0, 4), (0, 5), (0, 6), (0, 7));
        void Check(Ot14Node paint, params int[] deltas) =>
            Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, paint))], varIndexMap: map, varStore: Ot14VariationStore(deltas)), more: ("fvar", Ot14Fvar()));
        var plainLine = Ot14VarLine(0, (0, 0, 1, 0xFFFFFFFF), (1, 2, 1, 0xFFFFFFFF));
        // VarColorStop: delta on the stopOffset of the second stop (index 0)
        Check(Ot14Paint(5, Ot14VarLine(0, (0, 0, 1, 0xFFFFFFFF), (0.5, 2, 1, 0)), Ot14I16(0), Ot14I16(0), Ot14I16(1000), Ot14I16(0), Ot14I16(0), Ot14I16(1000),
            Ot14U32(0xFFFFFFFF)), 0x1000, 0);
        // PaintVarLinearGradient with a VarColorLine of two VarColorStops (indices 0–2 and 3–5)
        Check(Ot14Paint(5, Ot14VarLine(0, (0, 0, 1, 0), (1, 2, 1, 3)), Ot14I16(0), Ot14I16(0), Ot14I16(1000), Ot14I16(0), Ot14I16(0), Ot14I16(1000),
            Ot14U32(0xFFFFFFFF)), 0, 0, 0, 0, 0, 0);
        // PaintVarSolid: delta on alpha
        Check(Ot14VarSolid(0, 0.5, 1), 0, 0x1000);
        // PaintVarLinearGradient: delta on x1 (index 2 of the six coordinates)
        Check(Ot14Paint(5, plainLine, Ot14I16(0), Ot14I16(0), Ot14I16(1000), Ot14I16(0), Ot14I16(0), Ot14I16(1000), Ot14U32(0)), 0, 0, 200, 0, 0, 0);
        // PaintVarRadialGradient: delta on radius1 (index 5)
        Check(Ot14Paint(7, plainLine, Ot14I16(500), Ot14I16(500), Ot14U16(0), Ot14I16(500), Ot14I16(500), Ot14U16(300), Ot14U32(0)), 0, 0, 0, 0, 0, 200);
    }
}
