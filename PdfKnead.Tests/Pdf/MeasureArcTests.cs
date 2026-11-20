using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Arcs in polylines and areas (Ablaufplan 7.6 j; docs/FEHLERGEDAECHTNIS.md, No. 620; decided on 8.11.2026): an arc point makes the piece
/// from the point before to the point after a circular arc through it. Known values — a half circle is π·r long, a rectangle with a half
/// circle on top has its area and perimeter, an S of two half circles is smooth at its middle — and the arcs are circles in the measuring
/// units (another unit along y: an ellipse on the paper).
/// </summary>
public sealed class MeasureArcTests
{
    private static PdfString S(string text) => new(PdfTextEncoding.Encode(text));

    private static PdfDictionary Unit(string label, double factor) => TestPdf.Dict(("U", S(label)), ("C", new PdfReal(factor)));

    /// <summary>A scale of 0.01 m per point all over the page (1:100 at 1 pt = 1 mm of paper, roughly).</summary>
    private static IReadOnlyList<Viewport> Metres => PageScales.Of(TestPdf.Dict(("VP", new PdfArray([TestPdf.Dict(("BBox", TestPdf.Box(-1000, -1000, 1000, 1000)),
        ("Measure", TestPdf.Dict(("Subtype", TestPdf.N("RL")), ("R", S("1:100")), ("X", new PdfArray([Unit("m", 0.01)])),
            ("D", new PdfArray([Unit("m", 1)])), ("A", new PdfArray([Unit("m²", 1)])))))]))));

    /// <summary>The same along x, along y 0.02 m per point (/Y with /CYX 1): a circle of the drawing is an ellipse on the paper.</summary>
    private static IReadOnlyList<Viewport> Stretched => PageScales.Of(TestPdf.Dict(("VP", new PdfArray([TestPdf.Dict(("BBox", TestPdf.Box(-1000, -1000, 1000, 1000)),
        ("Measure", TestPdf.Dict(("Subtype", TestPdf.N("RL")), ("R", S("1:100")), ("X", new PdfArray([Unit("m", 0.01)])), ("Y", new PdfArray([Unit("m", 0.02)])),
            ("CYX", new PdfReal(1)), ("D", new PdfArray([Unit("m", 1)])), ("A", new PdfArray([Unit("m²", 1)])))))]))));

    /// <summary>A half circle of radius 100 pt = 1 m: π m long, its piece „3.14 m · r 1 m“; without the arc point the two chords (before: always).</summary>
    [Fact]
    public void Polyline_HalfCircle_IsPiTimesTheRadius()
    {
        var arc = Measuring.Measure(MeasureKind.Polyline, [(0, 0), (100, 100), (200, 0)], Metres, arcPoints: [1]);
        Assert.Equal(Math.PI, arc.Value!.Value, 9);
        Assert.Equal([1], arc.ArcPoints);
        var piece = Assert.Single(arc.Outline!.Pieces);
        Assert.True(piece.IsArc);
        Assert.Equal(1, piece.Radius!.Value, 9);
        Assert.Equal(Measuring.Measure(MeasureKind.Length, [(0, 0), (Math.PI * 100, 0)], Metres).Text + " · r 1 m", Assert.Single(arc.Segments));
        Assert.Equal(["", "", ""], arc.Corners);
        // the course runs over the top, its middle at the point it goes through
        Assert.Equal(100, piece.Middle.X, 6);
        Assert.Equal(100, piece.Middle.Y, 6);
        Assert.All(piece.Path, p => Assert.Equal(100, Math.Sqrt((p.X - 100) * (p.X - 100) + p.Y * p.Y), 6));
        Assert.Equal(2 * Math.Sqrt(2), Measuring.Measure(MeasureKind.Polyline, [(0, 0), (100, 100), (200, 0)], Metres).Value!.Value, 9);
    }

    /// <summary>A straight piece and an arc after it: the lengths add up; the corner between them is measured against the tangent of the arc.</summary>
    [Fact]
    public void Polyline_LineThenArc_TangentAtTheCorner()
    {
        // 1 m straight to the right, then a half circle of 0.5 m up and round to (200, 0): at (100, 0) the arc starts straight up — 90°
        var m = Measuring.Measure(MeasureKind.Polyline, [(0, 0), (100, 0), (150, 50), (200, 0)], Metres, arcPoints: [2]);
        Assert.Equal(1 + Math.PI * 0.5, m.Value!.Value, 9);
        Assert.Equal(2, m.Outline!.Pieces.Count);
        Assert.False(m.Outline.Pieces[0].IsArc);
        Assert.True(m.Outline.Pieces[1].IsArc);
        Assert.Equal("90°", m.Corners[1]);
        Assert.Equal("", m.Corners[2]);   // on the arc
        Assert.Equal((0, 1), Round(m.Outline.Pieces[1].StartDirection));
        Assert.Equal((0, -1), Round(m.Outline.Pieces[1].EndDirection));
    }

    /// <summary>
    /// An S of two half circles bending the other way: π m long, smooth where they meet (the corner there is a straight 180°), the two arcs
    /// turning in opposite senses.
    /// </summary>
    [Fact]
    public void Polyline_SCurve_SmoothWhereTheArcsMeet()
    {
        var s = Measuring.Measure(MeasureKind.Polyline, [(0, 0), (50, 50), (100, 0), (150, -50), (200, 0)], Metres, arcPoints: [1, 3]);
        Assert.Equal(Math.PI, s.Value!.Value, 9);
        var (first, second) = (s.Outline!.Pieces[0], s.Outline.Pieces[1]);
        Assert.Equal(Round(first.EndDirection), Round(second.StartDirection));
        Assert.Equal("180°", s.Corners[2]);
        Assert.True(first.Middle.Y > 0 && second.Middle.Y < 0);
    }

    /// <summary>
    /// A rectangle of 2 m × 1 m with a half circle of 1 m on top: 2 + π/2 m², perimeter 4 + π m — drawn either way round, with the arc in the
    /// middle or as the piece back to the first point.
    /// </summary>
    [Fact]
    public void Area_WithAHalfCircle_SegmentCounted_EitherWayRound()
    {
        var expected = 2 + Math.PI / 2;
        var counter = Measuring.Measure(MeasureKind.Area, [(0, 0), (200, 0), (200, 100), (100, 200), (0, 100)], Metres, arcPoints: [3]);
        Assert.Equal(expected, counter.Value!.Value, 9);
        Assert.Equal(4 + Math.PI, counter.Outline!.Length, 9);
        var clockwise = Measuring.Measure(MeasureKind.Area, [(0, 0), (0, 100), (100, 200), (200, 100), (200, 0)], Metres, arcPoints: [2]);
        Assert.Equal(expected, clockwise.Value!.Value, 9);
        // where the half circle meets the sides it goes on in their direction: 180°
        Assert.Equal(["90°", "180°", "", "180°", "90°"], clockwise.Corners);
        // the arc as the way back: a half disc on its diameter
        var back = Measuring.Measure(MeasureKind.Area, [(0, 0), (200, 0), (100, 100)], Metres, arcPoints: [2]);
        Assert.Equal(Math.PI / 2, back.Value!.Value, 9);
        Assert.Equal(2 + Math.PI, back.Outline!.Length, 9);
        // a whole circle from two half circles
        var circle = Measuring.Measure(MeasureKind.Area, [(0, 0), (100, 100), (200, 0), (100, -100)], Metres, arcPoints: [1, 3]);
        Assert.Equal(Math.PI, circle.Value!.Value, 9);
    }

    /// <summary>Another unit along y: the arc is a circle in the units (π m, half disc π/2 m²), on the paper half as high as wide.</summary>
    [Fact]
    public void OtherUnitAlongY_CircleInUnits_EllipseOnThePaper()
    {
        var m = Measuring.Measure(MeasureKind.Polyline, [(0, 0), (100, 50), (200, 0)], Stretched, arcPoints: [1]);
        Assert.Equal(Math.PI, m.Value!.Value, 9);
        var piece = Assert.Single(m.Outline!.Pieces);
        Assert.Equal(50, piece.Middle.Y, 6);
        Assert.Equal(50, piece.Path.Max(p => p.Y), 1);
        Assert.All(piece.Path, p => Assert.Equal(1, (p.X - 100) * (p.X - 100) / 10000 + p.Y * p.Y / 2500, 6));
        var area = Measuring.Measure(MeasureKind.Area, [(0, 0), (100, 50), (200, 0)], Stretched, arcPoints: [1]);
        Assert.Equal(Math.PI / 2, area.Value!.Value, 9);
    }

    /// <summary>Three points of an arc on one line: two straight pieces (that is what was drawn).</summary>
    [Fact]
    public void ArcOnALine_TwoStraightPieces()
    {
        var m = Measuring.Measure(MeasureKind.Polyline, [(0, 0), (100, 0), (300, 0)], Metres, arcPoints: [1]);
        Assert.Equal(3, m.Value!.Value, 9);
        Assert.Equal(2, m.Outline!.Pieces.Count);
        Assert.All(m.Outline.Pieces, p => Assert.False(p.IsArc));
    }

    /// <summary>Which arc points can be: not the first, of a polyline not the last, not right after another; of an area the last one can.</summary>
    [Fact]
    public void ValidArcPoints_Rules()
    {
        Assert.Equal([1, 3], MeasureGeometry.ValidArcPoints(5, [0, 1, 2, 3, 4], closed: false));
        Assert.Equal([1, 3], MeasureGeometry.ValidArcPoints(5, [3, 1, 1], closed: false));
        Assert.Equal([2], MeasureGeometry.ValidArcPoints(3, [2], closed: true));
        Assert.Empty(MeasureGeometry.ValidArcPoints(3, [2], closed: false));
        Assert.Empty(MeasureGeometry.ValidArcPoints(2, [1], closed: false));
        Assert.Empty(MeasureGeometry.ValidArcPoints(5, null, closed: true));
    }

    /// <summary>
    /// The Bézier pieces of an arc (for a dimension's path with curves): at most 90° each, one ends exactly at the point the arc goes through,
    /// the last at its end; their middles lie on the circle (a quarter circle is off by at most 0.03 % of the radius).
    /// </summary>
    [Fact]
    public void Curves_AtTheMiddlePointAndOnTheCircle()
    {
        // a half circle round (100, 100), radius 100, from (0, 100) through (100, 0) to (200, 100): two quarter curves
        AssertOnCircle(Measuring.Measure(MeasureKind.Polyline, [(0, 100), (100, 0), (200, 100)], Metres, arcPoints: [1]), 2, (100, 0), (200, 100));
        // three quarters, the point between after the first quarter: one curve to it, two after it
        AssertOnCircle(Measuring.Measure(MeasureKind.Polyline, [(0, 100), (100, 0), (100, 200)], Metres, arcPoints: [1]), 3, (100, 0), (100, 200));

        static void AssertOnCircle(Measurement m, int count, (double X, double Y) through, (double X, double Y) end)
        {
            var piece = Assert.Single(m.Outline!.Pieces);
            Assert.Equal(count, piece.Curves.Count);
            Assert.Contains(piece.Curves, c => c.End == through);
            Assert.Equal(end, piece.Curves[^1].End);
            var from = piece.Path[0];
            foreach (var curve in piece.Curves)
            {
                (double X, double Y) mid = (0.125 * from.X + 0.375 * curve.Control1.X + 0.375 * curve.Control2.X + 0.125 * curve.End.X,
                    0.125 * from.Y + 0.375 * curve.Control1.Y + 0.375 * curve.Control2.Y + 0.125 * curve.End.Y);
                Assert.InRange(Math.Sqrt((mid.X - 100) * (mid.X - 100) + (mid.Y - 100) * (mid.Y - 100)), 100 - 0.03, 100 + 0.03);
                from = curve.End;
            }
        }
    }

    private static (double X, double Y) Round((double X, double Y) v) => (Math.Round(v.X, 9) + 0.0, Math.Round(v.Y, 9) + 0.0);
}
