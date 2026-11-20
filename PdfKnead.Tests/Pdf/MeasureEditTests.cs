using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Points put into and taken out of a finished polyline or area (Ablaufplan 7.6 k; docs/FEHLERGEDAECHTNIS.md, No. 621; design decision,
/// 8.11.2026): Ctrl+click on the line puts a point there — on a straight piece on the line, on an arc on its circle, the arc split in two on
/// the same circle — and the measurement stays as it was until a point is dragged; a chosen point and Del take it out.
/// </summary>
public sealed class MeasureEditTests
{
    private static PdfString S(string text) => new(PdfTextEncoding.Encode(text));

    private static PdfDictionary Unit(string label, double factor) => TestPdf.Dict(("U", S(label)), ("C", new PdfReal(factor)));

    private static readonly IReadOnlyList<Viewport> Metres = PageScales.Of(TestPdf.Dict(("VP", new PdfArray([TestPdf.Dict(("BBox", TestPdf.Box(-1000, -1000, 1000, 1000)),
        ("Measure", TestPdf.Dict(("Subtype", TestPdf.N("RL")), ("R", S("1:100")), ("X", new PdfArray([Unit("m", 0.01)])),
            ("D", new PdfArray([Unit("m", 1)])), ("A", new PdfArray([Unit("m²", 1)])))))]))));

    private static readonly RectilinearMeasure Scale = Metres[0].Measure!;

    private static Measurement Measure(MeasureKind kind, IReadOnlyList<(double X, double Y)> points, IReadOnlyList<int> arcs) =>
        Measuring.Measure(kind, points, Metres, arcPoints: arcs);

    /// <summary>A straight piece: the new point on the line where it was clicked (projected onto it); the length the same.</summary>
    [Fact]
    public void Insert_OnAStraightPiece_OnTheLine_LengthTheSame()
    {
        (double X, double Y)[] points = [(0, 0), (300, 0), (300, 200)];
        var (after, arcs, index) = MeasureGeometry.Insert(points, [], false, Scale, 1, (310, 80))!.Value;
        Assert.Equal(2, index);
        Assert.Equal([(0.0, 0.0), (300.0, 0.0), (300.0, 80.0), (300.0, 200.0)], after);
        Assert.Empty(arcs);
        Assert.Equal(5, Measure(MeasureKind.Polyline, after, arcs).Value!.Value, 9);
        // on (or beside) an end: nothing to put in
        Assert.Null(MeasureGeometry.Insert(points, [], false, Scale, 0, (0, 5)));
        Assert.Null(MeasureGeometry.Insert(points, [], false, Scale, 0, (-50, 0)));
        Assert.Null(MeasureGeometry.Insert(points, [], false, Scale, 7, (100, 0)));
    }

    /// <summary>
    /// A half circle (arc point at its top): clicked on its first quarter, the new point goes on the circle there and the first half gets an
    /// arc point of its own; clicked on the second quarter, the other way round — the old arc point stays either way, the length stays π.
    /// </summary>
    [Fact]
    public void Insert_OnAnArc_SplitOnTheSameCircle_LengthTheSame()
    {
        (double X, double Y)[] points = [(0, 0), (100, 100), (200, 0)];
        // before the arc point: at 45° from the start (the click a little outside the circle)
        var before = MeasureGeometry.Insert(points, [1], false, Scale, 0, (100 - 80, 80))!.Value;
        Assert.Equal(5, before.Points.Count);
        Assert.Equal(2, before.Index);
        Assert.Equal([1, 3], before.ArcPoints);
        Assert.Equal((100.0, 100.0), before.Points[3]);   // the old arc point stays
        Assert.All(before.Points, p => Assert.Equal(100, Math.Sqrt((p.X - 100) * (p.X - 100) + p.Y * p.Y), 6));
        Assert.Equal(Math.PI, Measure(MeasureKind.Polyline, before.Points, before.ArcPoints).Value!.Value, 9);
        // after it
        var after = MeasureGeometry.Insert(points, [1], false, Scale, 0, (170, 70))!.Value;
        Assert.Equal(2, after.Index);
        Assert.Equal([1, 3], after.ArcPoints);
        Assert.Equal((100.0, 100.0), after.Points[1]);
        Assert.Equal(Math.PI, Measure(MeasureKind.Polyline, after.Points, after.ArcPoints).Value!.Value, 9);
        // on the arc point itself: nothing to put in
        Assert.Null(MeasureGeometry.Insert(points, [1], false, Scale, 0, (100, 101)));
    }

    /// <summary>An area whose last piece is an arc back to the first point: the new points go at the end, the area stays the same.</summary>
    [Fact]
    public void Insert_OnTheArcBackToTheStart_AreaTheSame()
    {
        (double X, double Y)[] points = [(0, 0), (200, 0), (100, 100)];   // a half disc above its diameter
        var area = Measure(MeasureKind.Area, points, [2]);
        Assert.Equal(Math.PI / 2, area.Value!.Value, 9);
        var split = MeasureGeometry.Insert(points, [2], true, Scale, 1, (30, 60))!.Value;
        Assert.Equal(5, split.Points.Count);
        Assert.Equal(3, split.Index);
        Assert.Equal([2, 4], split.ArcPoints);
        var again = Measure(MeasureKind.Area, split.Points, split.ArcPoints);
        Assert.Equal(Math.PI / 2, again.Value!.Value, 9);
        Assert.Equal(area.Outline!.Length, again.Outline!.Length, 9);
    }

    /// <summary>
    /// Taking points out: an arc point makes its piece straight; a corner joins its neighbours straight and an arc ending there becomes straight
    /// (its arc point stays as a plain point); a polyline keeps two points, an area three.
    /// </summary>
    [Fact]
    public void Remove_ArcPointCornerAndTheLeastThereMustBe()
    {
        (double X, double Y)[] points = [(0, 0), (100, 100), (200, 0), (400, 0)];
        var straight = MeasureGeometry.Remove(points, [1], false, 1)!.Value;
        Assert.Equal([(0.0, 0.0), (200.0, 0.0), (400.0, 0.0)], straight.Points);
        Assert.Empty(straight.ArcPoints);
        var corner = MeasureGeometry.Remove(points, [1], false, 2)!.Value;
        Assert.Equal([(0.0, 0.0), (100.0, 100.0), (400.0, 0.0)], corner.Points);
        Assert.Empty(corner.ArcPoints);
        // the other arc of an S keeps its arc point
        (double X, double Y)[] s = [(0, 0), (50, 50), (100, 0), (150, -50), (200, 0), (300, 0)];
        var end = MeasureGeometry.Remove(s, [1, 3], false, 5)!.Value;
        Assert.Equal([1, 3], end.ArcPoints);
        var middle = MeasureGeometry.Remove(s, [1, 3], false, 2)!.Value;
        Assert.Empty(middle.ArcPoints);
        // the least there must be
        Assert.Null(MeasureGeometry.Remove([(0, 0), (100, 0)], [], false, 0));
        Assert.Null(MeasureGeometry.Remove([(0, 0), (100, 0), (0, 100)], [], true, 1));
        // of an area the first point goes: the arc between two other corners keeps its arc point
        var area = MeasureGeometry.Remove([(0, 0), (200, 0), (200, 200), (100, 300), (0, 200)], [3], true, 0)!.Value;
        Assert.Equal(4, area.Points.Count);
        Assert.Equal([2], area.ArcPoints);   // (100, 300) is now point 2, between (200, 200) and (0, 200)
    }

    /// <summary>
    /// Tangent arcs (Ctrl+Alt+click): after 1 m straight to the right, an end at (200, 100) gives the quarter circle round (100, 100) that goes
    /// on to the right without a bend; another end at (300, 200) the quarter bending back — an S, smooth at both joins (180°), 1 + π m long.
    /// Straight ahead, or without a direction yet, there is no arc.
    /// </summary>
    [Fact]
    public void TangentArc_GoesOnWithoutABend()
    {
        var points = new List<(double X, double Y)> { (0, 0), (100, 0) };
        var arcs = new List<int>();
        void Add((double X, double Y) end)
        {
            if (MeasureGeometry.TangentArcPoint(points, arcs, Scale, end) is { } through)
            {
                points.Add(through);
                arcs.Add(points.Count - 1);
            }
            points.Add(end);
        }
        Add((200, 100));
        Assert.Equal(100 + 100 * Math.Sqrt(0.5), points[2].X, 9);
        Assert.Equal(100 - 100 * Math.Sqrt(0.5), points[2].Y, 9);
        Add((300, 200));
        var s = Measure(MeasureKind.Polyline, points, arcs);
        Assert.Equal([2, 4], s.ArcPoints);
        Assert.Equal(1 + Math.PI, s.Value!.Value, 9);
        Assert.Equal("180°", s.Corners[1]);
        Assert.Equal("180°", s.Corners[3]);
        Assert.True(s.Outline!.Pieces[1].Sweep > 0 && s.Outline.Pieces[2].Sweep < 0);
        // straight ahead: none; without a direction yet: none; the last point an arc point waiting for its end: none
        Assert.Null(MeasureGeometry.TangentArcPoint([(0, 0), (100, 0)], [], Scale, (300, 0)));
        Assert.Null(MeasureGeometry.TangentArcPoint([(0, 0)], [], Scale, (100, 100)));
        Assert.Null(MeasureGeometry.TangentArcPoint([(0, 0), (100, 0), (150, 50)], [2], Scale, (200, 0)));
    }
}
