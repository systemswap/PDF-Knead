using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Saved dimensions as measurements again (Ablaufplan 7.6 h; docs/FEHLERGEDAECHTNIS.md, No. 605): what another program sees of a dimension —
/// /L of a line dimension, /Vertices of a polyline or polygon dimension (ISO 32000-2, 12.5.6.7, 12.5.6.9) — counts before the own data of
/// PDF Knead: has another program moved its points, they are taken and the value is measured anew with its scale (/Measure, 12.9). And the
/// dimensions of other programs, written as the standard says, are read as dimensions, so that „Messen“ can change them too.
/// </summary>
public static class Dimensions
{
    /// <summary>Points are written to a thousandth of a point; closer than this they are the same.</summary>
    private const double Tolerance = 0.01;

    /// <summary>A viewport around everything, for measuring with the scale of a dimension itself.</summary>
    private const double Everywhere = 1e7;

    private static readonly PdfName L = PdfName.Get("L"), Vertices = PdfName.Get("Vertices"), PathKey = PdfName.Get("Path"), It = PdfName.Get("IT"), MeasureKey = PdfName.Get("Measure"),
        C = PdfName.Get("C"), Ic = PdfName.Get("IC"), Bs = PdfName.Get("BS"), W = PdfName.Get("W"), Border = PdfName.Get("Border");

    /// <summary>Most points of a dimension of another program that are taken (a crafted file with millions would make it unusable).</summary>
    private const int MaxPoints = 10_000;

    /// <summary>Straight pieces a curve of a /Path is divided into (a quarter circle: its length 0.01 % short).</summary>
    private const int CurvePieces = 32;

    /// <summary>
    /// The points another program sees (page space): /L of a line, /Vertices of a polyline or polygon — or, in PDF 2.0, their /Path with its
    /// curves finely divided (12.5.6.9; Ablaufplan 7.6 j), of a polygon without the first point again at the end; null without usable ones.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)>? FilePoints(PdfDictionary annot)
    {
        if (annot.GetName(PdfName.Subtype)?.Value is "PolyLine" or "Polygon" && !annot.ContainsKey(Vertices) && annot.GetArray(PathKey) is { } path)
            return PathPoints(path, annot.GetName(PdfName.Subtype)?.Value == "Polygon");
        var array = annot.GetName(PdfName.Subtype)?.Value switch
        {
            "Line" => annot.GetArray(L),
            "PolyLine" or "Polygon" => annot.GetArray(Vertices),
            _ => null,
        };
        if (array is null || array.Count < 4 || array.Count % 2 != 0 || array.Count > 2 * MaxPoints) return null;
        var points = new List<(double X, double Y)>(array.Count / 2);
        for (var i = 0; i + 1 < array.Count; i += 2)
        {
            if (array.GetNumber(i) is not { } x || array.GetNumber(i + 1) is not { } y || !double.IsFinite(x) || !double.IsFinite(y)
                || Math.Abs(x) > 1e6 || Math.Abs(y) > 1e6)
                return null;
            points.Add((x, y));
        }
        return points;
    }

    /// <summary>
    /// The points of a /Path (operands of m, l and c, 12.5.6.9): its first point, the end of each line, each curve divided into
    /// <see cref="CurvePieces"/>; null if it is broken or too long.
    /// </summary>
    private static IReadOnlyList<(double X, double Y)>? PathPoints(PdfArray path, bool closed)
    {
        if (path.Count < 2 || path.Count > MaxPoints) return null;
        var points = new List<(double X, double Y)>();
        for (var i = 0; i < path.Count; i++)
        {
            if (path.Get(i) is not PdfArray step || step.Count is not (2 or 6) || i == 0 && step.Count != 2) return null;
            var values = new double[step.Count];
            for (var k = 0; k < step.Count; k++)
                if (step.GetNumber(k) is not { } v || !double.IsFinite(v) || Math.Abs(v) > 1e6) return null;
                else values[k] = v;
            if (step.Count == 2) points.Add((values[0], values[1]));
            else AddCurve(points, points[^1], (values[0], values[1]), (values[2], values[3]), (values[4], values[5]));
            if (points.Count > MaxPoints) return null;
        }
        if (closed && points.Count > 1 && Math.Abs(points[^1].X - points[0].X) <= Tolerance && Math.Abs(points[^1].Y - points[0].Y) <= Tolerance)
            points.RemoveAt(points.Count - 1);
        return points;
    }

    /// <summary>Adds a cubic Bézier curve from <paramref name="start"/> as <see cref="CurvePieces"/> points (its end the last).</summary>
    private static void AddCurve(List<(double X, double Y)> points, (double X, double Y) start, (double X, double Y) c1, (double X, double Y) c2, (double X, double Y) end)
    {
        for (var k = 1; k <= CurvePieces; k++)
        {
            double t = (double)k / CurvePieces, u = 1 - t;
            points.Add(k == CurvePieces ? end : (u * u * u * start.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * end.X,
                u * u * u * start.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * end.Y));
        }
    }

    /// <summary>
    /// The /Path of a polyline or area with arcs (12.5.6.9; Ablaufplan 7.6 j): its first point, then for each straight piece its end and for
    /// each arc its Bézier pieces (to a thousandth of a point, as the points of /Vertices).
    /// </summary>
    public static PdfArray CurvePath(MeasureOutline outline, (double X, double Y) first)
    {
        static PdfReal N(double v) => new(Math.Round(v, 3));
        var path = new PdfArray { new PdfArray([N(first.X), N(first.Y)]) };
        foreach (var piece in outline.Pieces)
            if (piece.Curves.Count == 0) path.Add(new PdfArray([N(piece.Path[^1].X), N(piece.Path[^1].Y)]));
            else
                foreach (var c in piece.Curves)
                    path.Add(new PdfArray([N(c.Control1.X), N(c.Control1.Y), N(c.Control2.X), N(c.Control2.Y), N(c.End.X), N(c.End.Y)]));
        return path;
    }

    /// <summary>Measures points with a scale of its own (that of a dimension); without one the paper, an angle in degrees.</summary>
    /// <param name="kind">What to measure.</param>
    /// <param name="points">The points (page space).</param>
    /// <param name="measure">The scale; null for the paper.</param>
    /// <param name="userUnit">The /UserUnit of the page (for the paper).</param>
    /// <param name="decimals">Decimals to show instead of those of the scale; null keeps them.</param>
    /// <param name="arcPoints">Of a polyline or area its arc points (Ablaufplan 7.6 j); null: none.</param>
    public static Measurement MeasureWith(MeasureKind kind, IReadOnlyList<(double X, double Y)> points, RectilinearMeasure? measure, double userUnit = 1,
        int? decimals = null, IReadOnlyList<int>? arcPoints = null) =>
        Measuring.Measure(kind, points, measure is null ? [] : [new Viewport([-Everywhere, -Everywhere, Everywhere, Everywhere], "", measure)], userUnit, decimals,
            arcPoints);

    /// <summary>
    /// A dimension of PDF Knead as read from its own data, checked against the points of the file: are they the same, it stays as it is;
    /// has another program moved them, the dimension takes the points of the file and is measured anew with its scale (before, it came back as
    /// it was saved, and the change of the other program was lost when PDF Knead saved again).
    /// </summary>
    /// <param name="annot">The annotation.</param>
    /// <param name="read">The dimension from the own data.</param>
    public static DimensionElement Reconcile(PdfDictionary annot, DimensionElement read)
    {
        if (FilePoints(annot) is not { } file || Unchanged(read, file, annot.ContainsKey(PathKey) && !annot.ContainsKey(Vertices)) ||
            PointsFor(read.Kind, file) is not { } points) return read;
        var measured = MeasureWith(read.Kind, points, read.Measure);
        // a moved label stays where it was, within the rules for the new points (No. 703)
        return measured.Value is null ? read
            : ElementAppearance.Dimension(measured, read.Color, read.StrokeWidth, read.FillOpacity, read.ShowSegments, read.ShowAngles, read.PageRotation,
                Measuring.Detail(measured), read.PageLabelAt) ?? read;
    }

    /// <summary>
    /// A dimension of another program as a dimension of PDF Knead (not yet on the page — „Messen“ shows it as a measurement, a change replaces
    /// the annotation by it): a line annotation with /Measure or /IT /LineDimension as a length, a polyline as a polyline, a polygon as an
    /// area; its points (moved with the annotation in this session), its colour, width and scale. Null for other annotations, a scale that is
    /// not rectilinear (GEO) or points that do not measure.
    /// </summary>
    /// <param name="foreign">The annotation of another program.</param>
    /// <param name="pageRotation">The rotation of the page (labels upright as shown).</param>
    public static DimensionElement? Of(ForeignAnnotation foreign, int pageRotation)
    {
        try
        {
            return Read(foreign, pageRotation);
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or IOException or ArgumentException)
        {
            return null;   // a broken annotation stays an annotation
        }
    }

    private static DimensionElement? Read(ForeignAnnotation foreign, int pageRotation)
    {
        var annot = foreign.Original;
        var (kind, intent) = annot.GetName(PdfName.Subtype)?.Value switch
        {
            "Line" => (MeasureKind.Length, "LineDimension"),
            "PolyLine" => (MeasureKind.Polyline, "PolyLineDimension"),
            "Polygon" => (MeasureKind.Area, "PolygonDimension"),
            _ => (MeasureKind.Length, (string?)null),
        };
        if (intent is null) return null;
        var scale = annot.GetDictionary(MeasureKey);
        if (scale is null && annot.GetName(It)?.Value != intent) return null;
        var measure = RectilinearMeasure.Read(scale);
        if (scale is not null && measure is null) return null;   // a geospatial or broken scale: stays an annotation
        if (FilePoints(annot) is not { } file || file.Count < (kind == MeasureKind.Area ? 3 : 2)) return null;
        var (dx, dy) = foreign.Offset;
        var points = kind == MeasureKind.Length ? new[] { file[0], file[^1] } : file.ToArray();
        for (var i = 0; i < points.Length; i++) points[i] = (points[i].X + dx, points[i].Y + dy);
        var measured = MeasureWith(kind, points, measure);
        if (measured.Value is null) return null;
        var color = ColorOf(annot.GetArray(C)) ?? 0x000000;
        var width = annot.GetDictionary(Bs)?.GetNumber(W) ?? (annot.GetArray(Border) is { Count: >= 3 } border ? border.GetNumber(2) : null) ?? 1;
        var fill = kind == MeasureKind.Area && annot.GetArray(Ic) is { Count: > 0 } ? 0.2 : 0;
        var lineWidth = double.IsFinite(width) ? width : 1;
        // a line dimension whose caption another program moved (/CO, ISO 32000-2, 12.5.6.7): the label there (No. 703)
        (double X, double Y)? label = null;
        if (kind == MeasureKind.Length && annot.GetArray(PdfName.Get("CO")) is { Count: 2 } co && co.GetNumber(0) is { } h && co.GetNumber(1) is { } v &&
            double.IsFinite(h) && double.IsFinite(v) && Math.Abs(h) < 1e5 && Math.Abs(v) < 1e5 && (h != 0 || v != 0))
        {
            var home = ElementAppearance.CaptionHome(points[0], points[^1], lineWidth, out var along, out var across);
            label = (home.X + along.X * h + across.X * v, home.Y + along.Y * h + across.Y * v);
        }
        return ElementAppearance.Dimension(measured, color, lineWidth, fill, false, false, pageRotation, Measuring.Detail(measured), label);
    }

    /// <summary>
    /// Whether the points of the file are those PDF Knead wrote for the dimension (nobody moved them). With arcs (Ablaufplan 7.6 j) the shapes
    /// are compared, not the points one by one: how finely an arc is divided may come out one step different after rounding — every point of
    /// the file must lie on the course of the own data (written as a /Path, <paramref name="curves"/>: on its curves divided as
    /// <see cref="FilePoints"/> divides them), and every point of the own data on the course of the file.
    /// </summary>
    private static bool Unchanged(DimensionElement d, IReadOnlyList<(double X, double Y)> file, bool curves)
    {
        var points = d.PagePoints;
        if (d.ArcPoints.Count > 0 && ElementAppearance.Outline(d) is { } outline && outline.Pieces.Any(p => p.IsArc))
        {
            IReadOnlyList<(double X, double Y)> course = outline.Path;
            if (curves)
            {
                var divided = new List<(double X, double Y)> { points[0] };
                foreach (var piece in outline.Pieces)
                    if (piece.Curves.Count == 0) divided.Add(piece.Path[^1]);
                    else foreach (var c in piece.Curves) AddCurve(divided, divided[^1], c.Control1, c.Control2, c.End);
                course = divided;
            }
            var closed = d.Kind == MeasureKind.Area;
            // the chords of a finely divided arc lie up to 1/100 000 of its radius inside it (0.5° steps): that much more for large arcs
            var near = 2 * Tolerance + 1e-5 * Radius(outline);
            return file.All(p => Distance(p, course, closed) <= near) && points.All(p => Distance(p, file, closed) <= near) &&
                   (closed || Same([file[0], file[^1]], [course[0], course[^1]]));
        }
        IReadOnlyList<(double X, double Y)> expected = d.Kind switch
        {
            MeasureKind.Length => [points[0], points[^1]],
            MeasureKind.Radius when d.Path.Count >= 2 => d.PagePath,
            _ => points,
        };
        return Same(file, expected);
    }

    /// <summary>A measure of the size of the arcs of an outline in page space (the largest extent of their courses, at least their radius).</summary>
    private static double Radius(MeasureOutline outline)
    {
        double largest = 0;
        foreach (var piece in outline.Pieces.Where(p => p.IsArc))
        {
            double left = piece.Path.Min(p => p.X), right = piece.Path.Max(p => p.X), bottom = piece.Path.Min(p => p.Y), top = piece.Path.Max(p => p.Y);
            largest = Math.Max(largest, Math.Max(right - left, top - bottom));
        }
        return largest;
    }

    /// <summary>The distance of a point from a course (its straight pieces; closed: also back to the first point).</summary>
    private static double Distance((double X, double Y) p, IReadOnlyList<(double X, double Y)> course, bool closed)
    {
        var best = double.PositiveInfinity;
        var count = closed ? course.Count : course.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var (a, b) = (course[i], course[(i + 1) % course.Count]);
            double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
            var t = length > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / length, 0, 1) : 0;
            double qx = a.X + t * dx - p.X, qy = a.Y + t * dy - p.Y;
            best = Math.Min(best, qx * qx + qy * qy);
        }
        return course.Count == 1 ? Math.Sqrt((p.X - course[0].X) * (p.X - course[0].X) + (p.Y - course[0].Y) * (p.Y - course[0].Y)) : Math.Sqrt(best);
    }

    private static bool Same(IReadOnlyList<(double X, double Y)> a, IReadOnlyList<(double X, double Y)> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (Math.Abs(a[i].X - b[i].X) > Tolerance || Math.Abs(a[i].Y - b[i].Y) > Tolerance) return false;
        return true;
    }

    /// <summary>
    /// The points to measure from the points of the file: a length its ends, an angle its three points, an arc (written finely divided) its
    /// ends and the point in its middle — a whole circle (ends the same) three points around it and the first again; null if too few.
    /// </summary>
    private static IReadOnlyList<(double X, double Y)>? PointsFor(MeasureKind kind, IReadOnlyList<(double X, double Y)> file) => kind switch
    {
        MeasureKind.Length when file.Count >= 2 => [file[0], file[^1]],
        MeasureKind.Polyline when file.Count >= 2 => file,
        MeasureKind.Area when file.Count >= 3 => file,
        MeasureKind.Angle when file.Count == 3 => file,
        MeasureKind.Radius when file.Count >= 4 && Same([file[0]], [file[^1]]) => [file[0], file[file.Count / 3], file[2 * file.Count / 3], file[0]],
        MeasureKind.Radius when file.Count >= 3 => [file[0], file[file.Count / 2], file[^1]],
        _ => null,
    };

    /// <summary>A colour array (gray, RGB or CMYK, 0–1) as 0xRRGGBB; null if empty or broken.</summary>
    private static int? ColorOf(PdfArray? array)
    {
        if (array is null) return null;
        var values = new double[array.Count];
        for (var i = 0; i < values.Length; i++)
        {
            if (array.GetNumber(i) is not { } v || !double.IsFinite(v)) return null;
            values[i] = Math.Clamp(v, 0, 1);
        }
        (double R, double G, double B)? rgb = values.Length switch
        {
            1 => (values[0], values[0], values[0]),
            3 => (values[0], values[1], values[2]),
            4 => ((1 - values[0]) * (1 - values[3]), (1 - values[1]) * (1 - values[3]), (1 - values[2]) * (1 - values[3])),
            _ => null,
        };
        return rgb is { } c ? ((int)Math.Round(c.R * 255) << 16) | ((int)Math.Round(c.G * 255) << 8) | (int)Math.Round(c.B * 255) : null;
    }
}
