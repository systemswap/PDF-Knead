namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-16 (OpenType, COLR 10.3.7 to 10.3.7.8): the paint formats 8 to 32 — sweep gradients, PaintGlyph, PaintColrGlyph, the
/// transformations and PaintComposite. PDF-Knead reads neither COLR nor CPAL (question 3 is open): the tests check today's behaviour with the
/// COLR/CPAL builders of ot-14 — the font parses without an exception, no colour glyphs are offered, every glyph keeps its outline and advance.
/// </summary>
public sealed partial class M7NormTests
{
    /// <summary>
    /// OT 10.3.7 sentences 1–2, 6–16, 33–36 (today's behaviour, question 3): COLR version 1 with PaintGlyph → PaintSweepGradient (format 8,
    /// stops red and blue), a sweep around (500, 500) from −1.0 to 1.0, one with the start angle 0x8000 and the end angle 0x4000 next to a
    /// PaintRotate of 0x2000 — no colour glyphs, the outline of „A“ the one from glyf.
    /// </summary>
    [Fact]
    public void Ot16_ColrV1SweepGradients_NotRead_GlyphsKeepTheirOutlines()
    {
        var a = Ot14Gid('A');
        var b = Ot14Gid('B');
        var c = Ot14Gid('C');
        var colr = Ot14Colr(paints:
        [
            (a, Ot14Glyph(a, Ot14Sweep(Ot14RedToBlue(), 500, 500, 0, 0.5))),
            (b, Ot14Glyph(b, Ot14Sweep(Ot14RedToBlue(), 500, 500, -1, 1))),
            (c, Ot14Glyph(c, Ot14Rotate(Ot14Sweep(Ot14RedToBlue(), 500, 500, -2, 1), 0.5))),
        ]);
        Assert.True(Ot14Contains(colr, 0x80, 0x00, 0x40, 0x00));
        Ot14ColrIgnored(colr);
    }

    /// <summary>
    /// OT 10.3.7.1 sentences 1–8, 11–12; 10.3.7.2 sentences 1–6, 7–8 (today's behaviour, question 3): COLR version 1 with „A“ → PaintGlyph glyph 5 →
    /// PaintSolid red; glyph 5 with a record of its own (blue) and as a layer of „A“ (red); „Ä“ as PaintColrGlyph „A“ plus a layer for the dots;
    /// a PaintColrGlyph on a glyph without a record and a cycle glyph 3 → PaintColrGlyph 4 → PaintColrGlyph 3 — no colour glyphs, no endless
    /// loop, „A“ keeps the outline of „A“ (not that of glyph 5).
    /// </summary>
    [Fact]
    public void Ot16_ColrV1PaintGlyphAndColrGlyph_NotRead_GlyphsKeepTheirOutlines()
    {
        var a = Ot14Gid('A');
        Assert.False(Ot14Sans.Value.Outline(5).IsEmpty);
        var font = Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(5, Ot14Solid(0)))]));
        Assert.False(font.Outline(a).Points.SequenceEqual(font.Outline(5).Points));
        Ot14ColrIgnored(Ot14Colr(paints: [(5, Ot14Solid(2)), (a, Ot14Glyph(5, Ot14Solid(0)))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(Ot14Gid('Ä'), Ot14Layers(2, 0)), (a, Ot14Glyph(a, Ot14Solid(0)))],
            layers: [Ot14ColrGlyph(a), Ot14Glyph(Ot14Gid('¨'), Ot14Solid(2))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14ColrGlyph(Ot14Gid('Z'))), (3, Ot14ColrGlyph(4)), (4, Ot14ColrGlyph(3))]));
    }

    /// <summary>
    /// OT 10.3.7.3 sentences 1–3, 16–26, 45–51; 10.3.7.4 sentences 1–3, 25–28; 10.3.7.5 sentences 1–11, 16–30, 31–38, 52–63, 82–87; 10.3.7.6
    /// sentences 1–7, 12–23, 24–41, 45–53, 54–58, 59–61; 10.3.7.7 sentences 1–7, 27–34, 51–58, 59–63, 64–66 (today's behaviour, question 3):
    /// COLR version 1 with one base glyph per transformation the drafts name — PaintTransform (2 0 0 2 0 0), (0 1 −1 0 100 0), (1 0 0 1 50 −20);
    /// PaintTranslate (100, 0) and (30, 40) next to PaintTransform (1 0 0 1 30 40); PaintScaleUniformAroundCenter 0.5 around (500, 500);
    /// PaintScale (1.0, 0.5); PaintScaleAroundCenter 1.5 around (250, 250); PaintScaleUniform 0.5; PaintScale (0.5, 0.75) next to its matrix;
    /// PaintRotate 90° and −180°; PaintRotateAroundCenter 90° around (500, 500); PaintRotate 30° next to its matrix; the chain translate –
    /// rotate – translate back around (10.5, 0); PaintRotateAroundCenter inside a PaintScale 0.5; PaintSkew 22.5°; PaintSkewAroundCenter 22.5°
    /// around (500, 0); PaintSkew 20° next to its matrix; the chain around (10.5, 0); PaintSkewAroundCenter inside a PaintRotate 90° — no colour
    /// glyphs, every glyph keeps its outline.
    /// </summary>
    [Fact]
    public void Ot16_ColrV1Transformations_NotRead_GlyphsKeepTheirOutlines()
    {
        Ot14Node Fill() => Ot14Glyph(5, Ot14Solid(0));
        var cos30 = Math.Cos(Math.PI / 6);
        var sin30 = Math.Sin(Math.PI / 6);
        var tan20 = Math.Tan(Math.PI / 9);
        var paints = new Ot14Node[]
        {
            Ot14Transform(Fill(), 2, 0, 0, 2, 0, 0),
            Ot14Transform(Fill(), 0, 1, -1, 0, 100, 0),
            Ot14Transform(Fill(), 1, 0, 0, 1, 50, -20),
            Ot14Translate(Fill(), 100, 0),
            Ot14Translate(Fill(), 30, 40),
            Ot14Transform(Fill(), 1, 0, 0, 1, 30, 40),
            Ot14ScaleUniformAround(Fill(), 0.5, 500, 500),
            Ot14Scale(Fill(), 1, 0.5),
            Ot14ScaleAround(Fill(), 1.5, 1.5, 250, 250),
            Ot14ScaleUniform(Fill(), 0.5),
            Ot14Scale(Fill(), 0.5, 0.75),
            Ot14Transform(Fill(), 0.5, 0, 0, 0.75, 0, 0),
            Ot14Rotate(Fill(), 0.5),
            Ot14Rotate(Fill(), -1),
            Ot14RotateAround(Fill(), 0.5, 500, 500),
            Ot14Rotate(Fill(), 30 / 180.0),
            Ot14Transform(Fill(), cos30, sin30, -sin30, cos30, 0, 0),
            Ot14Transform(Ot14Rotate(Ot14Transform(Fill(), 1, 0, 0, 1, -10.5, 0), 0.5), 1, 0, 0, 1, 10.5, 0),
            Ot14Scale(Ot14RotateAround(Fill(), 0.5, 100, 0), 0.5, 0.5),
            Ot14Skew(Fill(), 22.5 / 180, 0),
            Ot14SkewAround(Fill(), 22.5 / 180, 0, 500, 0),
            Ot14Skew(Fill(), 20 / 180.0, 0),
            Ot14Transform(Fill(), 1, 0, -tan20, 1, 0, 0),
            Ot14Transform(Ot14Skew(Ot14Transform(Fill(), 1, 0, 0, 1, -10.5, 0), 20 / 180.0, 0), 1, 0, 0, 1, 10.5, 0),
            Ot14Rotate(Ot14SkewAround(Fill(), 20 / 180.0, 0, 100, 0), 0.5),
        };
        var glyphs = "ABCDEFGHIJKLMNOPQRSTUVWXY";
        Ot14ColrIgnored(Ot14Colr(paints: paints.Select((p, i) => (Ot14Gid(glyphs[i]), p))));
    }

    /// <summary>
    /// OT 10.3.7 sentences 6–16 (ColorLine behind the end); 10.3.7.3 sentences 6–15; 10.3.7.4 sentences 8–13; 10.3.7.5 sentences 64–81;
    /// 10.3.7.7 sentences 12–26; 10.3.7.8 sentences 5–10, 13, 51–61 (today's behaviour, question 3): COLR version 1 with a PaintSweepGradient whose
    /// ColorLine offset points behind the end of the table; a PaintTransform whose Affine2x3 lies behind the end; PaintTranslate (−32768, 32767);
    /// PaintScaleUniformAroundCenter with scale 0; PaintSkew of 90° (tangent infinite); a PaintComposite whose source offset points at itself;
    /// compositeMode 200; PaintComposite mode 2 over an unbounded PaintSolid backdrop without ClipList — each parses without an exception or
    /// endless loop and is ignored.
    /// </summary>
    [Fact]
    public void Ot16_ColrV1ExtremeValuesAndBrokenOffsets_NotRead_NoException()
    {
        var a = Ot14Gid('A');
        Ot14Node Fill() => Ot14Glyph(5, Ot14Solid(0));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Glyph(a, Ot14Sweep(Ot14Off24(0xFFFFFF), 500, 500, -1, 1)))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Paint(12, Fill(), Ot14Off24(0xFFFFFF)))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Translate(Fill(), -32768, 32767))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14ScaleUniformAround(Fill(), 0, 500, 500))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Skew(Fill(), 0.5, 0))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Paint(32, Ot14Off24(0), Ot14U8(3), Fill()))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Composite(Fill(), 200, Ot14Glyph(6, Ot14Solid(2))))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Composite(Fill(), 2, Ot14Solid(2)))]));
    }

    /// <summary>
    /// OT 10.3.7 sentence 3; 10.3.7.3 sentence 4; 10.3.7.4 sentence 4; 10.3.7.5 sentence 12; 10.3.7.6 sentence 8; 10.3.7.7 sentence 8 (today's
    /// behaviour, question 3): a font without fvar and without ItemVariationStore whose COLR version 1 uses the variable formats 9
    /// (PaintVarSweepGradient), 13 (PaintVarTransform), 15 (PaintVarTranslate), 17 (PaintVarScale), 25 (PaintVarRotate) and 29 (PaintVarSkew) —
    /// no colour glyphs, the base glyphs from their outlines, no exception (scrubbing leaves COLR out: <see cref="Ot14_SubsetsAndScrubbing_HandleColrAndCpal"/>).
    /// </summary>
    [Fact]
    public void Ot16_ColrV1VariableFormatsWithoutFvar_NotRead_GlyphsKeepTheirOutlines()
    {
        Ot14Node Fill() => Ot14Glyph(5, Ot14Solid(0));
        const long none = 0xFFFFFFFF;
        var paints = new Ot14Node[]
        {
            Ot14Paint(9, Ot14VarLine(0, (0, 0, 1, none), (1, 2, 1, none)), Ot14I16(500), Ot14I16(500), Ot14F2(0), Ot14F2(1), Ot14U32(0)),
            Ot14Paint(13, Fill(), new Ot14Node(Ot14Fixed(1), Ot14Fixed(0), Ot14Fixed(0), Ot14Fixed(1), Ot14Fixed(10), Ot14Fixed(0), Ot14U32(0))),
            Ot14Paint(15, Fill(), Ot14I16(10), Ot14I16(20), Ot14U32(0)),
            Ot14Paint(17, Fill(), Ot14F2(0.5), Ot14F2(0.5), Ot14U32(0)),
            Ot14Paint(25, Fill(), Ot14F2(0.5), Ot14U32(0)),
            Ot14Paint(29, Fill(), Ot14F2(0.1), Ot14F2(0), Ot14U32(0)),
        };
        Assert.False(Ot14Sans.Value.HasTable("fvar"));
        Ot14ColrIgnored(Ot14Colr(paints: paints.Select((p, i) => (Ot14Gid("ABCDEF"[i]), p))));
    }

    /// <summary>
    /// OT 10.3.7 sentences 17–29, 30–32; 10.3.7.3 sentences 27–41, 42–44; 10.3.7.4 sentences 14–24; 10.3.7.5 sentences 39–51; 10.3.7.6 sentences
    /// 42–44; 10.3.7.7 sentences 35–47, 48–50 (today's behaviour, question 3; PDF-Knead reads no variations): variable fonts (fvar) with COLR
    /// version 1, a DeltaSetIndexMap and an ItemVariationStore — PaintVarSweepGradient with a delta for the end angle; varIndexBase 0xFFFFFFFF;
    /// PaintVarTransform with a delta on dx; a VarAffine2x3 whose varIndexBase lies behind the end of the map; PaintVarTranslate with a delta on
    /// dy; PaintVarScaleAroundCenter with a delta on centerX; PaintVarRotate with a delta beyond 360°; PaintVarSkewAroundCenter with a delta on
    /// ySkewAngle; PaintVarSkew without a map entry for its index — no colour glyphs, the glyphs as in the default instance.
    /// </summary>
    [Fact]
    public void Ot16_ColrV1VariableTransformsAndSweeps_NotRead_DefaultInstance()
    {
        var a = Ot14Gid('A');
        Ot14Node Fill() => Ot14Glyph(5, Ot14Solid(0));
        var plainLine = Ot14VarLine(0, (0, 0, 1, 0xFFFFFFFF), (1, 2, 1, 0xFFFFFFFF));
        var map = Ot14DeltaSetIndexMap((0, 0), (0, 1), (0, 2), (0, 3), (0, 4), (0, 5));
        void Check(Ot14Node paint, params int[] deltas) =>
            Ot14ColrIgnored(Ot14Colr(paints: [(a, paint)], varIndexMap: map, varStore: Ot14VariationStore(deltas)), more: ("fvar", Ot14Fvar()));
        // PaintVarSweepGradient: delta on endAngle (index 1 of the angles)
        Check(Ot14Glyph(a, Ot14Paint(9, plainLine, Ot14I16(500), Ot14I16(500), Ot14F2(0), Ot14F2(0.5), Ot14U32(0))), 0, 0, 0, 0x1000);
        // varIndexBase 0xFFFFFFFF: no variation index
        Check(Ot14Glyph(a, Ot14Paint(9, plainLine, Ot14I16(500), Ot14I16(500), Ot14F2(0), Ot14F2(0.5), Ot14U32(0xFFFFFFFF))), 0x1000);
        // PaintVarTransform: delta on dx (index 4 of xx, yx, xy, yy, dx, dy)
        Check(Ot14Paint(13, Fill(), new Ot14Node(Ot14Fixed(1), Ot14Fixed(0), Ot14Fixed(0), Ot14Fixed(1), Ot14Fixed(0), Ot14Fixed(0), Ot14U32(0))), 0, 0, 0, 0, 100, 0);
        // VarAffine2x3 whose varIndexBase lies behind the end of the DeltaSetIndexMap
        Check(Ot14Paint(13, Fill(), new Ot14Node(Ot14Fixed(1), Ot14Fixed(0), Ot14Fixed(0), Ot14Fixed(1), Ot14Fixed(0), Ot14Fixed(0), Ot14U32(1000))), 100);
        // PaintVarTranslate: delta on dy
        Check(Ot14Paint(15, Fill(), Ot14I16(0), Ot14I16(0), Ot14U32(0)), 0, 50);
        // PaintVarScaleAroundCenter: delta on centerX (index 2)
        Check(Ot14Paint(19, Fill(), Ot14F2(0.5), Ot14F2(0.5), Ot14I16(250), Ot14I16(250), Ot14U32(0)), 0, 0, 100, 0);
        // PaintVarRotate: delta that carries the angle beyond 360°
        Check(Ot14Paint(25, Fill(), Ot14F2(1.75), Ot14U32(0)), 0x2000);
        // PaintVarSkewAroundCenter: delta on ySkewAngle (index 1)
        Check(Ot14Paint(31, Fill(), Ot14F2(0.1), Ot14F2(0), Ot14I16(500), Ot14I16(0), Ot14U32(0)), 0, 0x0800);
        // PaintVarSkew: the DeltaSetIndexMap has no entry for index 6
        Check(Ot14Paint(29, Fill(), Ot14F2(0.1), Ot14F2(0), Ot14U32(6)), 0x0800);
    }

    /// <summary>
    /// OT 10.3.7.8 sentences 1–4, 11–12, 14–29, 30–46, 47–50 (today's behaviour, question 3): COLR version 1 with PaintComposite — source over
    /// backdrop (mode 3, red over blue), screen (mode 13), one base glyph per Porter-Duff mode 0–12 (source red with alpha 0.5, backdrop blue),
    /// one per blend mode 13–27 and plus (mode 12) of two 50 % greys — no colour glyphs, the glyphs from their outlines.
    /// </summary>
    [Fact]
    public void Ot16_ColrV1PaintComposite_NotRead_GlyphsKeepTheirOutlines()
    {
        var a = Ot14Gid('A');
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Composite(Ot14Glyph(5, Ot14Solid(0)), 3, Ot14Glyph(6, Ot14Solid(2))))]));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Composite(Ot14Glyph(5, Ot14Solid(0)), 13, Ot14Glyph(6, Ot14Solid(2))))]));
        var letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        Ot14ColrIgnored(Ot14Colr(paints: Enumerable.Range(0, 13).Select(mode =>
            (Ot14Gid(letters[mode]), Ot14Composite(Ot14Glyph(5, Ot14Solid(0, 0.5)), mode, Ot14Glyph(6, Ot14Solid(2)))))));
        Ot14ColrIgnored(Ot14Colr(paints: Enumerable.Range(13, 15).Select(mode =>
            (Ot14Gid(letters[mode]), Ot14Composite(Ot14Glyph(5, Ot14Solid(0, 0.5)), mode, Ot14Glyph(6, Ot14Solid(2)))))));
        Ot14ColrIgnored(Ot14Colr(paints: [(a, Ot14Composite(Ot14Glyph(5, Ot14Solid(3)), 12, Ot14Glyph(5, Ot14Solid(3))))]));
    }
}
