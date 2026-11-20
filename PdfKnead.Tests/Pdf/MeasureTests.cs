using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>The measuring core of „Messen“ (Ablaufplan 7.6 b, ISO 32000-2, 12.9; docs/FEHLERGEDAECHTNIS.md, No. 580).</summary>
public sealed class MeasureTests
{
    private static PdfString S(string text) => new(PdfTextEncoding.Encode(text));

    private static PdfDictionary Unit(string label, double factor) => TestPdf.Dict(("U", S(label)), ("C", new PdfReal(factor)));

    /// <summary>A viewport with a scale in metres (<paramref name="metresPerPoint"/> per point of user space).</summary>
    private static PdfDictionary Viewport(double left, double bottom, double right, double top, double metresPerPoint, string ratio) => TestPdf.Dict(
        ("BBox", TestPdf.Box(left, bottom, right, top)), ("Measure", TestPdf.Dict(("Subtype", TestPdf.N("RL")), ("R", S(ratio)),
            ("X", new PdfArray([Unit("m", metresPerPoint)])), ("D", new PdfArray([Unit("m", 1)])), ("A", new PdfArray([Unit("m²", 1)])))));

    private static IReadOnlyList<Viewport> Scales(params PdfDictionary[] viewports) => PageScales.Of(TestPdf.Dict(("VP", new PdfArray([.. viewports]))));

    [Fact]
    public void Length_WithTheScaleOfThePage_OrOnPaper()
    {
        var scales = Scales(Viewport(0, 0, 595, 842, 0.01, "1:100"));
        var measured = Measuring.Measure(MeasureKind.Length, [(0, 0), (300, 400)], scales);
        Assert.Equal(5, measured.Value!.Value, 9);
        Assert.Equal("5 m", measured.Text);
        Assert.Equal("1:100", measured.Scale);
        var paper = Measuring.Measure(MeasureKind.Length, [(0, 0), (300, 400)], []);
        Assert.Equal("176.4 mm", paper.Text);
        Assert.Null(paper.Scale);
    }

    [Fact]
    public void ViewportOfTheFirstPoint_Decides()
    {
        var scales = Scales(Viewport(0, 0, 300, 842, 0.01, "Grundriss 1:100"), Viewport(300, 0, 595, 842, 0.002, "Detail 1:20"));
        var polyline = Measuring.Measure(MeasureKind.Polyline, [(400, 0), (400, 100), (100, 100)], scales);   // starts in the detail
        Assert.Equal("Detail 1:20", polyline.Scale);
        Assert.Equal(0.8, polyline.Value!.Value, 9);                                                           // 400 pt × 0.002 m
        Assert.Equal("0.8 m", polyline.Text);
    }

    [Fact]
    public void AreaAngleAndRadius()
    {
        var scales = Scales(Viewport(0, 0, 595, 842, 0.01, "1:100"));
        var area = Measuring.Measure(MeasureKind.Area, [(0, 0), (100, 0), (100, 100), (0, 100)], scales);
        Assert.Equal(1, area.Value!.Value, 9);
        Assert.Equal("1 m²; 4 m", area.Text);
        Assert.Equal("45°", Measuring.Measure(MeasureKind.Angle, [(100, 0), (0, 0), (100, 100)], scales).Text);
        Assert.Equal("90°", Measuring.Measure(MeasureKind.Angle, [(100, 0), (0, 0), (0, 50)], scales).Text);
        var radius = Measuring.Measure(MeasureKind.Radius, [(200, 100), (100, 200), (0, 100)], scales);   // circle round (100, 100), 100 pt
        Assert.Equal(1, radius.Value!.Value, 9);
        Assert.Equal("r 1 m; ⌀ 2 m", radius.Text);
        Assert.Null(Measuring.Measure(MeasureKind.Radius, [(0, 0), (50, 50), (100, 100)], scales).Value);   // on a line: no circle
        Assert.Null(Measuring.Measure(MeasureKind.Length, [(0, 0)], scales).Value);
    }

    /// <summary>Snapping: a corner before a point on a line, both only within the radius; text does not count.</summary>
    [Fact]
    public void Snap_CornersBeforeLines_NotText()
    {
        var (doc, list) = InterpreterTests.Page("2 w 0 0 m 100 0 l 100 100 l S 20 120 60 30 re f BT /F1 24 Tf 200 200 Td (X) Tj ET",
            _ => TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica"))))))));
        using (doc)
        {
            Assert.Equal(((100.0, 0.0), SnapKind.Corner), MeasureSnap.Find(list, 98, 3, 5));
            Assert.Equal(((50.0, 0.0), SnapKind.OnLine), MeasureSnap.Find(list, 50, 2, 5));
            Assert.Equal(((80.0, 150.0), SnapKind.Corner), MeasureSnap.Find(list, 79, 148, 5));   // corner of a filled rectangle
            Assert.Equal(((50.0, 40.0), SnapKind.None), MeasureSnap.Find(list, 50, 40, 5));
            Assert.Equal(SnapKind.None, MeasureSnap.Find(list, 205, 205, 5).Kind);               // the letter X
        }
    }

    /// <summary>
    /// Snapping onto curves (Ablaufplan 7.6 e; docs/FEHLERGEDAECHTNIS.md, No. 584): a circle of radius 100 drawn with four Bézier arcs — a
    /// click beside its middle snaps onto it (before only its four ends counted), within 0.05 % of the radius; the ends stay corners.
    /// </summary>
    [Fact]
    public void Snap_OntoCurves_OfACircle()
    {
        const double k = 55.22847498;
        var (doc, list) = InterpreterTests.Page(FormattableString.Invariant($"1 w 200 100 m 200 {100 + k} {100 + k} 200 100 200 c {100 - k} 200 0 {100 + k} 0 100 c ")
            + FormattableString.Invariant($"0 {100 - k} {100 - k} 0 100 0 c {100 + k} 0 200 {100 - k} 200 100 c S"));
        using (doc)
        {
            var at45 = (X: 100 + 100 / Math.Sqrt(2), Y: 100 + 100 / Math.Sqrt(2));
            var (point, kind) = MeasureSnap.Find(list, at45.X + 2, at45.Y + 1, 5);
            Assert.Equal(SnapKind.OnLine, kind);
            Assert.Equal(100, Math.Sqrt((point.X - 100) * (point.X - 100) + (point.Y - 100) * (point.Y - 100)), 0.05);
            Assert.Equal(((200.0, 100.0), SnapKind.Corner), MeasureSnap.Find(list, 198, 102, 5));
        }
    }

    /// <summary>
    /// Straight lines (Ablaufplan 7.6 g; docs/FEHLERGEDAECHTNIS.md, No. 602): from a previous point, a direction within 3° of 0°, 45°, 90° …
    /// snaps onto it — exactly at the height of the point; where it meets a line of the drawing, at the crossing; a corner still comes first;
    /// further off, too short, or switched off: not; parallel beside a line it stays straight.
    /// </summary>
    [Fact]
    public void Snap_StraightFromThePreviousPoint()
    {
        var (doc, list) = InterpreterTests.Page("1 w 100 0 m 100 200 l S");
        using (doc)
        {
            Assert.Equal(((60.0, 50.0), SnapKind.Straight), MeasureSnap.Find(list, 60, 52.5, 5, (0, 50), drawing: true, straight: true));
            Assert.Equal(((100.0, 50.0), SnapKind.OnLine | SnapKind.Straight), MeasureSnap.Find(list, 98, 52, 5, (0, 50), true, true));
            Assert.Equal(((60.0, 58.0), SnapKind.None), MeasureSnap.Find(list, 60, 58, 5, (0, 50), true, true));          // about 8° off
            Assert.Equal(((100.0, 0.0), SnapKind.Corner), MeasureSnap.Find(list, 98, 2, 5, (0, 1), true, true));          // a corner first
            Assert.Equal(((5.0, 50.1), SnapKind.None), MeasureSnap.Find(list, 5, 50.1, 5, (0, 50), true, true));          // too short yet
            Assert.Equal(((60.0, 52.5), SnapKind.None), MeasureSnap.Find(list, 60, 52.5, 5, (0, 50), true, straight: false));
            Assert.Equal(((98.0, 50.0), SnapKind.Straight), MeasureSnap.Find(list, 98, 52, 5, (0, 50), drawing: false, straight: true));
            Assert.Equal(((100.0, 52.0), SnapKind.OnLine), MeasureSnap.Find(list, 98, 52, 5, null, true, true));          // no previous point
            var (diagonal, kind) = MeasureSnap.Find(list, 40, 91, 5, (0, 50), true, true);
            Assert.Equal(SnapKind.Straight, kind);
            Assert.Equal(40.5, diagonal.X, 9);
            Assert.Equal(90.5, diagonal.Y, 9);
            var (beside, parallel) = MeasureSnap.Find(list, 101.1, 150, 5, (100.3, 7.77), true, true);
            Assert.Equal(SnapKind.Straight, parallel);
            Assert.Equal(100.3, beside.X);
            Assert.Equal(150, beside.Y, 9);
        }
    }

    /// <summary>
    /// A circle or arc through more than three points (Ablaufplan 7.6 e; FEHLERGEDAECHTNIS 584): points set by hand, a little off a circle of
    /// 1 m — the circle that fits them best has about that radius, the deviation is shown; first and last point are the ends, the arc goes the
    /// way of the points between (here a half circle, clockwise); first and last point the same: the full circle; on a line: none.
    /// </summary>
    [Fact]
    public void Arc_ThroughMorePoints_BestFit_Direction_FullCircle()
    {
        var scales = Scales(Viewport(0, 0, 595, 842, 0.01, "1:100"));   // 100 pt = 1 m
        (double X, double Y)[] half = [(400, 300), (371, 371), (300, 400.6), (229.5, 370.5), (200, 300)];   // over the top, from the right to the left
        var arc = Measuring.Measure(MeasureKind.Radius, half, scales);
        Assert.Equal(1, arc.Value!.Value, 2);
        Assert.Equal(Math.PI, arc.Arc!.Length, 1);
        Assert.InRange(arc.Arc.Sweep, 179, 181);
        Assert.NotNull(arc.Arc.DeviationText);
        Assert.True(arc.Arc.Deviation is > 0 and < 0.01);
        Assert.All(arc.Arc.Path, p => Assert.True(p.Y >= 299));                                         // the arc is the upper half
        Assert.Equal(half[0], arc.Arc.Path[0]);
        Assert.Equal(half[^1], arc.Arc.Path[^1]);
        // the same ends, the points between below: the lower half
        var lower = Measuring.Measure(MeasureKind.Radius, [(400, 300), (300, 200), (200, 300)], scales);
        Assert.All(lower.Arc!.Path, p => Assert.True(p.Y <= 301));
        Assert.Null(lower.Arc.DeviationText);                                                            // three points: exact
        Assert.Equal("180°", lower.Arc.SweepText);
        Assert.Equal("3.14 m", lower.Arc.LengthText);
        var full = Measuring.Measure(MeasureKind.Radius, [(400, 300), (300, 400), (200, 300), (300, 200), (400, 300)], scales);
        Assert.Equal(2 * Math.PI, full.Arc!.Length, 9);
        Assert.Equal("360°", full.Arc.SweepText);
        Assert.Null(Measuring.Measure(MeasureKind.Radius, [(0, 0), (10, 10), (20, 20), (30, 30)], scales).Arc);
    }

    /// <summary>
    /// Pieces and angles of a polyline and an area (Ablaufplan 7.6 e; FEHLERGEDAECHTNIS 584): an L-shaped room 4 m × 3 m with a notch —
    /// its inner angles are 90° and 270° at the notch; a polyline bends 90° and 135°.
    /// </summary>
    [Fact]
    public void Pieces_AndAnglesAtThePoints()
    {
        var scales = Scales(Viewport(0, 0, 595, 842, 0.01, "1:100"));
        var room = Measuring.Measure(MeasureKind.Area, [(0, 0), (400, 0), (400, 300), (200, 300), (200, 150), (0, 150)], scales);
        Assert.Equal(["4 m", "3 m", "2 m", "1.5 m", "2 m", "1.5 m"], room.Segments);
        Assert.Equal(["90°", "90°", "90°", "90°", "270°", "90°"], room.Corners);
        // the same room the other way round: the same inner angles
        var reversed = Measuring.Measure(MeasureKind.Area, [(0, 150), (200, 150), (200, 300), (400, 300), (400, 0), (0, 0)], scales);
        Assert.Equal(["90°", "270°", "90°", "90°", "90°", "90°"], reversed.Corners);
        var line = Measuring.Measure(MeasureKind.Polyline, [(0, 0), (100, 0), (100, 100), (200, 0)], scales);
        Assert.Equal(["1 m", "1 m", "1.41 m"], line.Segments);
        Assert.Equal(["", "90°", "45°", ""], line.Corners);
        Assert.Empty(Measuring.Measure(MeasureKind.Length, [(0, 0), (100, 0)], scales).Segments);
    }
}
