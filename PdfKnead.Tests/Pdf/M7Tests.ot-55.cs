using System.Buffers.Binary;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-55 (OpenType specification, TrueType fundamentals 77–77.6.2): the tests the sentence-by-sentence drafts named as missing —
/// contours of points on and off the curve, the fill by contour direction, points beyond the em square and beyond the FUnit range, and unequal
/// scales in x and y. The glyphs are written by hand with the builders of package ot-20.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package ot-55)

    /// <summary>The font with unitsPerEm in head replaced (the table found through the directory).</summary>
    private static byte[] WithUnitsPerEmOt55(byte[] font, int unitsPerEm)
    {
        var result = (byte[])font.Clone();
        int count = BinaryPrimitives.ReadUInt16BigEndian(result.AsSpan(4));
        for (var i = 0; i < count; i++)
        {
            var record = 12 + 16 * i;
            if (System.Text.Encoding.ASCII.GetString(result, record, 4) != "head") continue;
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(record + 8));
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset + 18), (ushort)unitsPerEm);
            return result;
        }
        throw new InvalidOperationException("head fehlt");
    }

    /// <summary>A page drawing the TrueType font given (Type 0, CID = glyph) with <paramref name="content"/>, rendered at 1 pixel per point.</summary>
    private static SKBitmap RenderOt55(byte[] font, string content)
    {
        var (doc, list) = InterpreterTests.Page(content, pdf => TestPdf.Dict(("Font", TestPdf.Dict(("F", Ot20Type0(pdf, font))))));
        using (doc) return SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
    }

    /// <summary>Whether the pixel at (x, y) in page coordinates (y up) is dark.</summary>
    private static bool DarkOt55(SKBitmap bitmap, double x, double y) => bitmap.GetPixel((int)x, bitmap.Height - 1 - (int)y).Red < 100;

    /// <summary>The box of the dark pixels of a bitmap in page coordinates: left, bottom, width, height (in pixels).</summary>
    private static (int Left, int Bottom, int Width, int Height) InkOt55(SKBitmap bitmap)
    {
        int left = int.MaxValue, right = -1, top = int.MaxValue, bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Red >= 128) continue;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        Assert.True(right >= 0, "nichts gezeichnet");
        return (left, bitmap.Height - 1 - bottom, right - left + 1, bottom - top + 1);
    }

    // ------------------------------------------------------------ 77.3.1 outlines

    /// <summary>
    /// OT 77.3.1 sentence 10 (reader): any mix of points on and off the curve is allowed. A contour of only four points off the curve (the
    /// corners of the square 0 … 100) starts at the midpoint (0, 50) between its last and its first point and closes in four quadratic pieces
    /// through the implied midpoints (50, 0), (100, 50), (50, 100) back to (0, 50); a contour whose first point (100, 100) is off the curve and
    /// whose last (0, 0) is on it starts at that last point, draws a curve to (200, 0) and a line back.
    /// </summary>
    [Fact]
    public void Ot55_ContoursOfPointsOffTheCurve_StartAtTheImpliedOrLastPoint()
    {
        var offOnly = Ot20Simple([[(0, 0, false), (100, 0, false), (100, 100, false), (0, 100, false)]]);
        var offFirst = Ot20Simple([[(100, 100, false), (200, 0, true), (0, 0, true)]]);
        var font = OpenTypeFont.Parse(Ot20Font([Ot20Box, offOnly, offFirst]));

        var square = font.Outline(1);
        Assert.Equal([PathVerb.Move, PathVerb.Cubic, PathVerb.Cubic, PathVerb.Cubic, PathVerb.Cubic, PathVerb.Close], square.Verbs);
        var p = square.Points;
        Assert.Equal((0f, 50f), (p[0], p[1]));
        Assert.Equal((50f, 0f), (p[6], p[7]));
        Assert.Equal((100f, 50f), (p[12], p[13]));
        Assert.Equal((50f, 100f), (p[18], p[19]));
        Assert.Equal((0f, 50f), (p[24], p[25]));

        var curve = font.Outline(2);
        Assert.Equal([PathVerb.Move, PathVerb.Cubic, PathVerb.Line, PathVerb.Close], curve.Verbs);
        var q = curve.Points;
        Assert.Equal((0f, 0f), (q[0], q[1]));
        Assert.Equal((200f, 0f), (q[6], q[7]));
        Assert.Equal((0f, 0f), (q[8], q[9]));
    }

    /// <summary>
    /// OT 77.3.1 sentences 13–14 (reader): the direction of the contours decides the fill, the filled area lies to the right in the order of
    /// rising point numbers. A glyph of an outer square drawn clockwise (0 … 600) and an inner one counter-clockwise (200 … 400) drawn on a page
    /// leaves the inner square empty (nonzero winding: the opposite contours cancel); with the inner square also clockwise it is filled.
    /// </summary>
    [Fact]
    public void Ot55_ContourDirection_OppositeInnerContourLeavesAHole()
    {
        (int, int, bool)[] outer = [(0, 0, true), (0, 600, true), (600, 600, true), (600, 0, true)];
        var hole = Ot20Simple([outer, [(200, 200, true), (400, 200, true), (400, 400, true), (200, 400, true)]]);
        var filled = Ot20Simple([outer, [(200, 200, true), (200, 400, true), (400, 400, true), (400, 200, true)]]);
        using var bitmap = RenderOt55(Ot20Font([Ot20Box, hole, filled]), "BT /F 100 Tf 20 20 Td <0001> Tj 0 90 Td <0002> Tj ET");
        // the glyphs cover 20 … 80 (first) and 110 … 170 (second) in y, 20 … 80 in x; the inner squares 40 … 60 and 130 … 150
        Assert.True(DarkOt55(bitmap, 25, 25), "äußeres Quadrat nicht gefüllt");
        Assert.False(DarkOt55(bitmap, 50, 50), "gegenläufiges Innenquadrat gefüllt");
        Assert.True(DarkOt55(bitmap, 25, 115), "äußeres Quadrat (zweite Glyphe) nicht gefüllt");
        Assert.True(DarkOt55(bitmap, 50, 140), "gleichsinniges Innenquadrat nicht gefüllt");
    }

    // ------------------------------------------------------------ 77.3.2 the em square

    /// <summary>
    /// OT 77.3.2 sentences 4–7 (reader): glyphs may reach beyond the em square. In a font of 2048 units per em, a glyph whose rectangle reaches
    /// up to y = 3000 keeps that point (the outline is not clipped at the em), and drawn at 20 pt it reaches about 29 pt above the baseline,
    /// beyond the height of the font size.
    /// </summary>
    [Fact]
    public void Ot55_GlyphBeyondTheEmSquare_NotClipped()
    {
        var tall = Ot20Simple([Ot20Rect(0, 0, 1000, 3000)]);
        var data = WithUnitsPerEmOt55(Ot20Font([Ot20Box, tall]), 2048);
        var font = OpenTypeFont.Parse(data);
        Assert.Equal(2048, font.UnitsPerEm);
        Assert.Equal(3000, font.Outline(1).Bounds.Bottom, 3);                               // the largest y of the outline
        using var bitmap = RenderOt55(data, "BT /F 20 Tf 20 20 Td <0001> Tj ET");
        Assert.True(DarkOt55(bitmap, 24, 45), "über der Schriftgröße nicht gezeichnet");    // 25 pt above the baseline (20 pt font size)
        Assert.InRange(InkOt55(bitmap).Height, 28, 31);                                      // 3000 / 2048 · 20 ≈ 29.3
    }

    // ------------------------------------------------------------ 77.3.3 FUnits

    /// <summary>
    /// OT 77.3.3 sentences 6–7 (reader): points of a font lie between −16384 and +16383 FUnits — a rule for font makers; the reader draws points
    /// beyond it as well. A simple glyph whose x differences 16000 and 16000 put a point at x = 32000 (summed without overflow) parses without an
    /// exception, and its outline reaches to 32000.
    /// </summary>
    [Fact]
    public void Ot55_PointBeyond16383FUnits_Drawn()
    {
        var wide = Ot20Simple([[(0, 0, true), (16000, 0, true), (32000, 0, true), (32000, 100, true), (0, 100, true)]]);
        var font = OpenTypeFont.Parse(Ot20Font([Ot20Box, wide]));
        Assert.False(font.Broken);
        var outline = font.Outline(1);
        Assert.Equal(32000, outline.Bounds.Right, 3);
        Assert.Equal((16000f, 0f), (outline.Points[2], outline.Points[3]));
        Assert.Equal((32000f, 0f), (outline.Points[4], outline.Points[5]));
    }

    // ------------------------------------------------------------ 77.4.3 resolution

    /// <summary>
    /// OT 77.4.3 sentences 1–4 (reader): a device may have different resolutions across and down. PDF-Knead renders on square pixels; unequal
    /// scales come from the matrices of the page, and the glyphs follow them as paths: a TrueType square of 500 units drawn at 20 pt (10 × 10
    /// pixels) under „4 0 0 1 cm“ is four times as wide (40 pixels) and as high as without.
    /// </summary>
    [Fact]
    public void Ot55_UnequalScales_GlyphFollowsTheMatrix()
    {
        var data = Ot20Font([Ot20Box, Ot20Simple([Ot20Rect(0, 0, 500, 500)])]);
        using var plain = RenderOt55(data, "BT /F 20 Tf 20 20 Td <0001> Tj ET");
        using var wide = RenderOt55(data, "q 4 0 0 1 0 0 cm BT /F 20 Tf 5 20 Td <0001> Tj ET Q");
        var (before, after) = (InkOt55(plain), InkOt55(wide));
        Assert.InRange(before.Width, 9, 11);
        Assert.InRange(before.Height, 9, 11);
        Assert.InRange(after.Width, 39, 41);
        Assert.Equal(before.Height, after.Height);
        Assert.Equal(before.Bottom, after.Bottom);
    }
}
