using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Pdf.Document;

/// <summary>What the tool „Messen“ measures (Ablaufplan 7.6; decision of 6.11.2026).</summary>
public enum MeasureKind
{
    /// <summary>The distance between two points.</summary>
    Length,

    /// <summary>The length of a polyline (perimeter of an open shape).</summary>
    Polyline,

    /// <summary>The area of a polygon, with its perimeter.</summary>
    Area,

    /// <summary>The angle at the middle of three points.</summary>
    Angle,

    /// <summary>
    /// A circle or arc through three or more points: the first and the last point are its ends, those between fix it (three points: the circle
    /// through them; more: the circle that fits them best). Radius, diameter, length of the arc, its angle (Ablaufplan 7.6 e).
    /// </summary>
    Radius,
}

/// <summary>A measurement: its points in default user space, the value as text, and the scale it was taken with.</summary>
/// <param name="Kind">What was measured.</param>
/// <param name="Points">The points (page space).</param>
/// <param name="Value">The main value: length, area, angle in degrees or radius — in the units of the scale; null if it cannot be measured.</param>
/// <param name="Text">The value as text (with the perimeter for areas, the diameter for radii); empty if it cannot be measured.</param>
/// <param name="Scale">The scale: its ratio text (/R) or the name of its viewport; null for the paper (no scale on the page).</param>
public sealed record Measurement(MeasureKind Kind, IReadOnlyList<(double X, double Y)> Points, double? Value, string Text, string? Scale)
{
    /// <summary>
    /// The length of each piece as text (Ablaufplan 7.6 e): of a polyline from point to point, of an area also the closing piece back to the
    /// first point; empty for the other kinds.
    /// </summary>
    public IReadOnlyList<string> Segments { get; init; } = [];

    /// <summary>
    /// The angle at each point as text, one entry per point (Ablaufplan 7.6 e): at the bends of a polyline the angle between its two pieces
    /// (0–180°), at the corners of an area its inner angle (over 180° where it is concave); empty text where there is none (the ends of a
    /// polyline); empty for the other kinds.
    /// </summary>
    public IReadOnlyList<string> Corners { get; init; } = [];

    /// <summary>The circle or arc of <see cref="MeasureKind.Radius"/>; null for the other kinds or if the points lie on a line.</summary>
    public MeasuredArc? Arc { get; init; }

    /// <summary>The scale measured with (that of the page, or the paper; with the decimals chosen) — written as /Measure of a dimension.</summary>
    public RectilinearMeasure? Measure { get; init; }

    /// <summary>
    /// The arc points of a polyline or area (Ablaufplan 7.6 j): each makes the piece from the point before it to the point after it an arc
    /// through it; only those that can be (<see cref="MeasureGeometry.ValidArcPoints"/>); empty for the other kinds.
    /// </summary>
    public IReadOnlyList<int> ArcPoints { get; init; } = [];

    /// <summary>The pieces of a polyline or area and its course (<see cref="MeasureGeometry"/>); null for the other kinds or without a value.</summary>
    public MeasureOutline? Outline { get; init; }
}

/// <summary>A circle or arc measured through three or more points (Ablaufplan 7.6 e).</summary>
/// <param name="Centre">Its centre (page space).</param>
/// <param name="Radius">The radius in units of x.</param>
/// <param name="Length">The length of the arc from the first to the last point, through those between, in units of x.</param>
/// <param name="Sweep">The angle of the arc in degrees (360 for a full circle: first and last point the same).</param>
/// <param name="Deviation">How far the points lie from the circle at most, in units of x (0 with three points).</param>
/// <param name="Path">The arc as points (page space), finely divided — to draw it, and to save it as a polyline dimension.</param>
/// <param name="LengthText">The length of the arc as text.</param>
/// <param name="SweepText">Its angle as text.</param>
/// <param name="DeviationText">The deviation as text; null with three points.</param>
public sealed record MeasuredArc((double X, double Y) Centre, double Radius, double Length, double Sweep, double Deviation,
    IReadOnlyList<(double X, double Y)> Path, string LengthText, string SweepText, string? DeviationText);

/// <summary>
/// The measuring core of „Messen“ (ISO 32000-2, 12.9): the viewport of the first point decides the scale (12.9.1); without one, the paper is
/// measured in millimetres. Lengths, areas and angles are formatted with the number formats of the scale (12.9.2) — /D, /A, /T; an angle
/// without /T in degrees. See docs/FEHLERGEDAECHTNIS.md, No. 580.
/// </summary>
public static class Measuring
{
    /// <summary>Points of default user space (1/72 inch) as millimetres of the paper.</summary>
    public const double MillimetresPerPoint = 25.4 / 72;

    /// <summary>The paper: millimetres with one decimal, areas in square centimetres, as a scale for pages without one.</summary>
    public static readonly RectilinearMeasure Paper = PaperOf(1);

    /// <summary>
    /// The paper of a page whose user space unit is <paramref name="userUnit"/> × 1/72 inch (/UserUnit, Table 31): one unit is that many
    /// points of the paper. (A scale of the page needs no such care: /X converts user space units, 12.9.1.) See FEHLERGEDAECHTNIS 583.
    /// </summary>
    public static RectilinearMeasure PaperOf(double userUnit) => new("", new NumberFormats([new NumberFormat("mm", MillimetresPerPoint * userUnit)]), null,
        new NumberFormats([new NumberFormat("mm", 1, Precision: 10)]), new NumberFormats([new NumberFormat("cm²", 0.01, Precision: 100)]), null, null, null);

    /// <summary>The detail line of a measurement: „Bogen 3.93 m · 90° · Punkte bis 0.01 m vom Kreis“; empty for kinds without.</summary>
    public static string Detail(Measurement measurement) => measurement.Arc is not { } arc ? ""
        : arc.DeviationText is { } deviation ? Localization.Tr.T("Bogen {0} · {1} · Punkte bis {2} vom Kreis", arc.LengthText, arc.SweepText, deviation)
        : Localization.Tr.T("Bogen {0} · {1}", arc.LengthText, arc.SweepText);

    /// <summary>Degrees with one decimal, for angles without /T.</summary>
    private static readonly NumberFormats Degrees = new([new NumberFormat("°", 1, Precision: 10, Before: "", After: "")]);

    /// <summary>Measures <paramref name="points"/> with the scale of the page at the first point.</summary>
    /// <param name="kind">What to measure.</param>
    /// <param name="points">The points in default user space (two for a length, three for an angle or radius, more for polylines and areas).</param>
    /// <param name="viewports">The viewports of the page (<see cref="PageScales.Of"/>).</param>
    /// <param name="userUnit">The /UserUnit of the page, for measuring the paper (<see cref="PaperOf"/>).</param>
    /// <param name="decimals">
    /// Decimals to show instead of those of the scale (Ablaufplan 7.6 e3): the last unit of lengths, areas and angles as a decimal with that
    /// many places, trailing zeros kept; null keeps the number formats of the file.
    /// </param>
    /// <param name="arcPoints">
    /// Of a polyline or area the arc points (Ablaufplan 7.6 j): the piece through each is an arc from the point before to the point after;
    /// null or empty: all pieces straight.
    /// </param>
    /// <returns>The measurement; without a value if there are too few points or the scale cannot measure it (y without /CYX).</returns>
    public static Measurement Measure(MeasureKind kind, IReadOnlyList<(double X, double Y)> points, IReadOnlyList<Viewport> viewports, double userUnit = 1,
        int? decimals = null, IReadOnlyList<int>? arcPoints = null)
    {
        var viewport = points.Count > 0 ? PageScales.At(viewports, points[0].X, points[0].Y) : null;
        var measure = viewport?.Measure ?? (userUnit == 1 ? Paper : PaperOf(userUnit));
        if (decimals is { } places)
            measure = measure with
            {
                Distance = WithDecimals(measure.Distance, places), Area = WithDecimals(measure.Area, places),
                Angle = WithDecimals(measure.Angle ?? Degrees, places),
            };
        var scale = viewport?.Measure is { } m ? (m.Ratio.Length > 0 ? m.Ratio : viewport.Name.Length > 0 ? viewport.Name : "") : null;
        double? value = null;
        var text = "";
        switch (kind)
        {
            case MeasureKind.Length when points.Count >= 2:
                value = measure.Length([points[0], points[^1]]);
                if (value is { } length) text = measure.Distance.Format(length);
                break;
            case MeasureKind.Polyline when points.Count >= 2:
            case MeasureKind.Area when points.Count >= 3:
            {
                // straight pieces and arcs (Ablaufplan 7.6 j): the length of a polyline, the area of an area with its perimeter
                var closed = kind == MeasureKind.Area;
                var arcs = MeasureGeometry.ValidArcPoints(points.Count, arcPoints, closed);
                if (MeasureGeometry.Build(points, arcs, closed, measure) is not { } outline) break;
                value = closed ? Math.Abs(outline.SignedArea) : outline.Length;
                if (!double.IsFinite(value.Value) || !double.IsFinite(outline.Length)) break;
                text = closed ? measure.Area.Format(value.Value) + "; " + measure.Distance.Format(outline.Length) : measure.Distance.Format(value.Value);
                return new Measurement(kind, points, value, text, scale)
                {
                    Measure = measure, ArcPoints = arcs, Outline = outline, Segments = Pieces(measure, outline), Corners = Bends(measure, outline, points.Count, closed),
                };
            }
            case MeasureKind.Angle when points.Count >= 3:
                if (measure.ToUnits(points[0].X - points[1].X, points[0].Y - points[1].Y) is { } a &&
                    measure.ToUnits(points[2].X - points[1].X, points[2].Y - points[1].Y) is { } b && (a.X != 0 || a.Y != 0) && (b.X != 0 || b.Y != 0))
                {
                    var degrees = Math.Abs(Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y)) * 180 / Math.PI;
                    value = degrees;
                    text = (measure.Angle ?? Degrees).Format(degrees);
                }
                break;
            case MeasureKind.Radius when points.Count >= 3:
                if (ArcThrough(measure, points) is { } arc && double.IsFinite(arc.Radius) && double.IsFinite(arc.Length))
                {
                    value = arc.Radius;
                    text = "r " + measure.Distance.Format(arc.Radius) + "; ⌀ " + measure.Distance.Format(2 * arc.Radius);
                    return new Measurement(kind, points, value, text, scale) { Measure = measure, Arc = arc };
                }
                break;
        }
        // a value that is no number (an absurd scale) measures nothing (No. 596)
        if (value is { } v && !double.IsFinite(v)) (value, text) = (null, "");
        return new Measurement(kind, points, value, text, scale) { Measure = measure };
    }

    /// <summary>Number formats whose last unit shows <paramref name="places"/> decimals (0–6), zeros kept.</summary>
    private static NumberFormats WithDecimals(NumberFormats formats, int places)
    {
        var units = formats.Units.ToArray();
        units[^1] = units[^1] with { Fraction = 'D', Precision = (int)Math.Pow(10, Math.Clamp(places, 0, 6)), Exact = true };
        return new NumberFormats(units);
    }

    /// <summary>The length of each piece as text — of an arc with its radius („3.93 m · r 2.5 m“; Ablaufplan 7.6 j, design decision).</summary>
    private static string[] Pieces(RectilinearMeasure measure, MeasureOutline outline) =>
        [.. outline.Pieces.Select(p => p.Radius is { } r ? measure.Distance.Format(p.Length) + " · r " + measure.Distance.Format(r) : measure.Distance.Format(p.Length))];

    /// <summary>
    /// The angle at each point, between the directions of the pieces that meet there (of an arc its tangent): for a polyline 0–180°, none at
    /// the ends; for an area (<paramref name="closed"/>) the inner angle — over 180° at concave corners, so that the angles of a polygon add
    /// up to (n − 2) × 180°. None at an arc point (it lies on its arc).
    /// </summary>
    private static string[] Bends(RectilinearMeasure measure, MeasureOutline outline, int count, bool closed)
    {
        var corners = new string[count];
        Array.Fill(corners, "");
        // the turning sense of the outline decides which side is inside
        var turning = closed ? Math.Sign(outline.SignedArea) : 0;
        var format = measure.Angle ?? Degrees;
        var pieces = outline.Pieces;
        for (var k = 0; k < pieces.Count; k++)
        {
            if (!closed && k == 0) continue;
            var (incoming, outgoing) = (pieces[(k + pieces.Count - 1) % pieces.Count], pieces[k]);
            // towards the point before and towards the point after, from the corner
            (double X, double Y) a = (-incoming.EndUnits.X, -incoming.EndUnits.Y), b = outgoing.StartUnits;
            double degrees;
            if (!closed || turning == 0)
                degrees = Math.Abs(Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y)) * 180 / Math.PI;
            else
            {
                // counter-clockwise: inside is turning from the next piece to the previous one counter-clockwise; clockwise the other way
                var (from, to) = turning > 0 ? (b, a) : (a, b);
                degrees = Math.Atan2(from.X * to.Y - from.Y * to.X, from.X * to.X + from.Y * to.Y) * 180 / Math.PI;
                if (degrees < 0) degrees += 360;
            }
            corners[outgoing.From] = format.Format(degrees);
        }
        return corners;
    }

    /// <summary>
    /// The circle or arc through the points (in the measuring units, so a scale with other units along y is respected): three points give
    /// the circle through them, more the circle with the least squared distances of the points from it (an algebraic fit refined by
    /// Gauss–Newton). The arc runs from the first to the last point on the side of most of the points between; first and last the same:
    /// the full circle. Null if the points lie on a line.
    /// </summary>
    private static MeasuredArc? ArcThrough(RectilinearMeasure measure, IReadOnlyList<(double X, double Y)> points)
    {
        var origin = points[0];
        var units = new List<(double X, double Y)>(points.Count);
        foreach (var p in points)
            if (measure.ToUnits(p.X - origin.X, p.Y - origin.Y) is { } u) units.Add(u);
            else return null;
        (double X, double Y) centre;
        double radius;
        if (units.Count == 3)
        {
            if (Circle(measure, points[0], points[1], points[2]) is not { } r3) return null;
            // the centre in units, relative to the first point (as in Circle)
            var (p, q) = (units[1], units[2]);
            var d = 2 * (p.X * q.Y - p.Y * q.X);
            var (pp, qq) = (p.X * p.X + p.Y * p.Y, q.X * q.X + q.Y * q.Y);
            centre = ((q.Y * pp - p.Y * qq) / d, (p.X * qq - q.X * pp) / d);
            radius = r3;
        }
        else if (FitCircle(units) is { } fit) (centre, radius) = fit;
        else return null;

        var angles = units.Select(u => Math.Atan2(u.Y - centre.Y, u.X - centre.X)).ToList();
        var closed = Math.Abs(units[^1].X - units[0].X) + Math.Abs(units[^1].Y - units[0].Y) <= 1e-9 * Math.Max(1, radius);
        double sweep;            // radians, counter-clockwise positive
        if (closed) sweep = 2 * Math.PI * (Orientation(angles) >= 0 ? 1 : -1);
        else
        {
            var ccw = Positive(angles[^1] - angles[0]);
            // the points between: on the counter-clockwise arc from first to last, or on the other one
            var inside = 0;
            for (var i = 1; i < angles.Count - 1; i++) inside += Positive(angles[i] - angles[0]) <= ccw ? 1 : -1;
            sweep = inside >= 0 ? ccw : ccw - 2 * Math.PI;
        }
        var deviation = units.Count == 3 ? 0 : units.Max(u => Math.Abs(Math.Sqrt((u.X - centre.X) * (u.X - centre.X) + (u.Y - centre.Y) * (u.Y - centre.Y)) - radius));
        var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 90)), 8, 720);   // every 2° at least
        var path = new (double X, double Y)[steps + 1];
        for (var i = 0; i <= steps; i++)
        {
            var t = angles[0] + sweep * i / steps;
            var (dx, dy) = MeasureGeometry.FromUnits(measure, centre.X + radius * Math.Cos(t), centre.Y + radius * Math.Sin(t));
            path[i] = (origin.X + dx, origin.Y + dy);
        }
        // the ends exactly where they were set
        path[0] = points[0];
        if (!closed) path[^1] = points[^1];
        var (cx, cy) = MeasureGeometry.FromUnits(measure, centre.X, centre.Y);
        var length = radius * Math.Abs(sweep);
        var degrees = Math.Abs(sweep) * 180 / Math.PI;
        return new MeasuredArc((origin.X + cx, origin.Y + cy), radius, length, degrees, deviation, path, measure.Distance.Format(length),
            (measure.Angle ?? Degrees).Format(degrees), units.Count == 3 ? null : measure.Distance.Format(deviation));

        static double Positive(double a)
        {
            a %= 2 * Math.PI;
            return a < 0 ? a + 2 * Math.PI : a;
        }

        // the sense in which a closed run of points goes round the centre: sum of its steps
        static double Orientation(List<double> angles)
        {
            double total = 0;
            for (var i = 1; i < angles.Count; i++)
            {
                var step = angles[i] - angles[i - 1];
                while (step > Math.PI) step -= 2 * Math.PI;
                while (step < -Math.PI) step += 2 * Math.PI;
                total += step;
            }
            return total;
        }
    }

    /// <summary>
    /// The circle that fits more than three points best: first the algebraic fit (x² + y² + Dx + Ey + F = 0 by least squares, about the mean
    /// of the points), then Gauss–Newton on the distances of the points from the circle. Null if the points lie on a line.
    /// </summary>
    internal static ((double X, double Y) Centre, double Radius)? FitCircle(IReadOnlyList<(double X, double Y)> points)
    {
        double mx = 0, my = 0;
        foreach (var p in points) { mx += p.X; my += p.Y; }
        mx /= points.Count;
        my /= points.Count;
        double scale = 0;
        foreach (var p in points) scale = Math.Max(scale, Math.Abs(p.X - mx) + Math.Abs(p.Y - my));
        if (!(scale > 0)) return null;
        // algebraic: minimise Σ (x² + y² + D x + E y + F)² over D, E, F (normal equations, points about their mean, scaled)
        var m = new double[3, 4];
        foreach (var p in points)
        {
            double x = (p.X - mx) / scale, y = (p.Y - my) / scale, z = -(x * x + y * y);
            double[] row = [x, y, 1];
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++) m[i, j] += row[i] * row[j];
                m[i, 3] += row[i] * z;
            }
        }
        if (Solve(m) is not { } abc) return null;
        double cx = -abc[0] / 2, cy = -abc[1] / 2, r2 = cx * cx + cy * cy - abc[2];
        if (!(r2 > 0) || !double.IsFinite(r2)) return null;
        var r = Math.Sqrt(r2);
        // geometric: Gauss–Newton on d_i = |p_i − c| − r
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var n = new double[3, 4];
            foreach (var p in points)
            {
                double x = (p.X - mx) / scale, y = (p.Y - my) / scale, dx = x - cx, dy = y - cy, dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist == 0) return null;
                double[] j = [-dx / dist, -dy / dist, -1];
                var residual = dist - r;
                for (var a = 0; a < 3; a++)
                {
                    for (var b = 0; b < 3; b++) n[a, b] += j[a] * j[b];
                    n[a, 3] -= j[a] * residual;
                }
            }
            if (Solve(n) is not { } step) break;
            cx += step[0];
            cy += step[1];
            r += step[2];
            if (Math.Abs(step[0]) + Math.Abs(step[1]) + Math.Abs(step[2]) < 1e-15) break;
        }
        // a circle so large that the points are practically on a line: none
        if (!(r > 0) || r > 1e7) return null;
        return ((mx + cx * scale, my + cy * scale), r * scale);
    }

    /// <summary>Solves a 3 × 3 system (augmented matrix) by elimination with pivoting; null if it is singular.</summary>
    private static double[]? Solve(double[,] m)
    {
        for (var col = 0; col < 3; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < 3; row++) if (Math.Abs(m[row, col]) > Math.Abs(m[pivot, col])) pivot = row;
            if (Math.Abs(m[pivot, col]) < 1e-14) return null;
            for (var k = 0; k < 4; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
            for (var row = 0; row < 3; row++)
            {
                if (row == col) continue;
                var f = m[row, col] / m[col, col];
                for (var k = col; k < 4; k++) m[row, k] -= f * m[col, k];
            }
        }
        return [m[0, 3] / m[0, 0], m[1, 3] / m[1, 1], m[2, 3] / m[2, 2]];
    }

    /// <summary>The radius of the circle through three points, in units of x of the scale; null if they lie on a line.</summary>
    private static double? Circle(RectilinearMeasure measure, (double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
    {
        // in the measuring units, relative to a: centre where the perpendicular bisectors of ab and ac meet
        if (measure.ToUnits(b.X - a.X, b.Y - a.Y) is not { } p || measure.ToUnits(c.X - a.X, c.Y - a.Y) is not { } q) return null;
        var d = 2 * (p.X * q.Y - p.Y * q.X);
        if (Math.Abs(d) < 1e-12 * Math.Max(1, p.X * p.X + p.Y * p.Y + q.X * q.X + q.Y * q.Y)) return null;
        var pp = p.X * p.X + p.Y * p.Y;
        var qq = q.X * q.X + q.Y * q.Y;
        var ux = (q.Y * pp - p.Y * qq) / d;
        var uy = (p.X * qq - q.X * pp) / d;
        return Math.Sqrt(ux * ux + uy * uy);
    }
}

/// <summary>Where a measuring point snaps to (a point on a line can also lie straight from the previous point).</summary>
[Flags]
public enum SnapKind
{
    /// <summary>Not snapped: where it was put.</summary>
    None = 0,

    /// <summary>An end point or corner of a line of the drawing.</summary>
    Corner = 1,

    /// <summary>The nearest point on a line of the drawing.</summary>
    OnLine = 2,

    /// <summary>Straight from the previous point: at 0°, 45°, 90° … (Ablaufplan 7.6 g; with OnLine where that direction meets a line).</summary>
    Straight = 4,
}

/// <summary>
/// Snapping of measuring points to the vector drawing (Ablaufplan 7.6 b): corners (end points of lines and curves, corners of paths) before
/// points on straight lines, within a radius; text and images do not count. Only entries near the point are looked at (spatial index), with a
/// limit on the work, so that large plans stay quick.
/// </summary>
public static class MeasureSnap
{
    /// <summary>Most path points looked at per search.</summary>
    private const int MaxPoints = 200_000;

    /// <summary>Straight pieces a curve is divided into when snapping onto it (a quarter circle: at most 0.03 % of its radius off).</summary>
    private const int CurvePieces = 32;

    /// <summary>Largest deviation in degrees from a straight direction (0°, 45°, 90° …) that still snaps onto it.</summary>
    public const double StraightTolerance = 3;

    /// <summary>The unit vectors of the straight directions, every 45° (exact, so that a horizontal line keeps its height to the last bit).</summary>
    private static readonly (double X, double Y)[] Straights =
    [
        (1, 0), (Math.Sqrt(0.5), Math.Sqrt(0.5)), (0, 1), (-Math.Sqrt(0.5), Math.Sqrt(0.5)),
        (-1, 0), (-Math.Sqrt(0.5), -Math.Sqrt(0.5)), (0, -1), (Math.Sqrt(0.5), -Math.Sqrt(0.5)),
    ];

    /// <summary>The point to measure from: a corner or a point on a line within <paramref name="radius"/>, else the point itself.</summary>
    /// <param name="list">The display list of the page.</param>
    /// <param name="x">The point (page space).</param>
    /// <param name="y">The point (page space).</param>
    /// <param name="radius">The snapping distance in page space (a few screen pixels).</param>
    /// <returns>The snapped point and how it snapped.</returns>
    public static ((double X, double Y) Point, SnapKind Kind) Find(DisplayList list, double x, double y, double radius)
    {
        if (!(radius > 0) || !double.IsFinite(x) || !double.IsFinite(y)) return ((x, y), SnapKind.None);
        (double X, double Y) bestCorner = default, bestLine = default;
        double cornerDistance = radius * radius, lineDistance = radius * radius;
        bool corner = false, line = false;
        Walk(list, RectD.FromLtrb(x - radius, y - radius, x + radius, y + radius), c =>
        {
            var d = (c.X - x) * (c.X - x) + (c.Y - y) * (c.Y - y);
            if (d > cornerDistance) return;
            (cornerDistance, bestCorner, corner) = (d, c, true);
        }, (a, b) =>
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
            var t = length > 0 ? Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length, 0, 1) : 0;
            var q = (a.X + t * dx, a.Y + t * dy);
            var d = (q.Item1 - x) * (q.Item1 - x) + (q.Item2 - y) * (q.Item2 - y);
            if (d > lineDistance) return;
            (lineDistance, bestLine, line) = (d, q, true);
        });
        return corner ? (bestCorner, SnapKind.Corner) : line ? (bestLine, SnapKind.OnLine) : ((x, y), SnapKind.None);
    }

    /// <summary>
    /// The point to measure from with a previous point (Ablaufplan 7.6 g; docs/FEHLERGEDAECHTNIS.md, No. 602): a corner of the drawing first;
    /// then, if the direction from <paramref name="from"/> is within <see cref="StraightTolerance"/> of 0°, 45°, 90° …, the point on that
    /// direction — where it meets a line of the drawing near the point, else straight beside the point; otherwise a point on a line, else the
    /// point itself. A line shorter than twice the radius is not straightened (its direction is not yet clear).
    /// </summary>
    /// <param name="list">The display list of the page (null: nothing to snap to on the page).</param>
    /// <param name="x">The point (page space).</param>
    /// <param name="y">The point (page space).</param>
    /// <param name="radius">The snapping distance in page space.</param>
    /// <param name="from">The previous point of the line (null: none, nothing to straighten).</param>
    /// <param name="drawing">Whether to snap to the drawing.</param>
    /// <param name="straight">Whether to snap to straight directions.</param>
    public static ((double X, double Y) Point, SnapKind Kind) Find(DisplayList? list, double x, double y, double radius, (double X, double Y)? from,
        bool drawing, bool straight)
    {
        var near = drawing && list is not null ? Find(list, x, y, radius) : ((x, y), SnapKind.None);
        if (near.Item2 == SnapKind.Corner || !straight || from is not { } f || Straighten(f, x, y, radius) is not { } axis) return near;
        if (drawing && list is not null && Crossing(list, f, axis.Direction, axis.Point, radius) is { } met) return (met, SnapKind.OnLine | SnapKind.Straight);
        return (axis.Point, SnapKind.Straight);
    }

    /// <summary>
    /// The point straight from <paramref name="from"/> nearest to (<paramref name="x"/>, <paramref name="y"/>) and the direction, if the
    /// direction to it is within <see cref="StraightTolerance"/> of a multiple of 45° and the line is at least twice the radius long.
    /// </summary>
    public static ((double X, double Y) Point, (double X, double Y) Direction)? Straighten((double X, double Y) from, double x, double y, double radius)
    {
        double dx = x - from.X, dy = y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || !(length >= 2 * radius)) return null;
        var angle = Math.Atan2(dy, dx);
        var k = Math.Round(angle / (Math.PI / 4));
        if (Math.Abs(angle - k * Math.PI / 4) > StraightTolerance * Math.PI / 180) return null;
        var u = Straights[(((int)k % 8) + 8) % 8];
        var t = dx * u.X + dy * u.Y;
        return ((from.X + t * u.X, from.Y + t * u.Y), u);
    }

    /// <summary>Where the ray from <paramref name="from"/> in direction <paramref name="u"/> crosses a line of the drawing within
    /// <paramref name="radius"/> of <paramref name="near"/> (the crossing nearest to it); null if none.</summary>
    private static (double X, double Y)? Crossing(DisplayList list, (double X, double Y) from, (double X, double Y) u, (double X, double Y) near, double radius)
    {
        (double X, double Y)? best = null;
        var bestDistance = radius * radius;
        Walk(list, RectD.FromLtrb(near.X - radius, near.Y - radius, near.X + radius, near.Y + radius), null, (a, b) =>
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            var denominator = u.X * dy - u.Y * dx;   // u × d: zero when parallel
            if (Math.Abs(denominator) <= 1e-9 * Math.Sqrt(dx * dx + dy * dy)) return;
            double wx = a.X - from.X, wy = a.Y - from.Y;
            var t = (wx * dy - wy * dx) / denominator;   // along the ray
            var s = (wx * u.Y - wy * u.X) / denominator;   // along the piece
            if (s < 0 || s > 1 || !(t > 0)) return;
            (double X, double Y) q = (from.X + t * u.X, from.Y + t * u.Y);
            var d = (q.X - near.X) * (q.X - near.X) + (q.Y - near.Y) * (q.Y - near.Y);
            if (d > bestDistance) return;
            (bestDistance, best) = (d, q);
        });
        return best;
    }

    /// <summary>
    /// Goes through the visible paths of the drawing (not text) that touch <paramref name="area"/>: <paramref name="corner"/> for each corner
    /// (start, end of lines and curves), <paramref name="segment"/> for each straight piece (curves in <see cref="CurvePieces"/> pieces), with a
    /// limit on the work.
    /// </summary>
    private static void Walk(DisplayList list, RectD area, Action<(double X, double Y)>? corner, Action<(double X, double Y), (double X, double Y)> segment)
    {
        using var hits = list.QueryHits(area);
        var items = list.Items.Span;
        var work = 0;
        for (var n = 0; n < hits.Count && work < MaxPoints; n++)
        {
            ref readonly var item = ref items[hits[n]];
            if (item.Path < 0 || item.Glyph || item.Kind is not (DisplayItemKind.Fill or DisplayItemKind.Stroke or DisplayItemKind.FillStroke) || list.IsHidden(hits[n]))
                continue;
            var matrix = list.Matrices[item.Matrix];
            var (verbStart, verbCount, p) = list.Paths[item.Path];
            var points = list.Points;
            (double X, double Y) start = default, current = default;
            for (var v = verbStart; v < verbStart + verbCount && work < MaxPoints; v++, work++)
            {
                switch (list.Verbs[v])
                {
                    case PathVerb.Move:
                        current = start = matrix.Transform(points[p], points[p + 1]);
                        p += 2;
                        corner?.Invoke(current);
                        break;
                    case PathVerb.Line:
                        var to = matrix.Transform(points[p], points[p + 1]);
                        p += 2;
                        segment(current, to);
                        corner?.Invoke(to);
                        current = to;
                        break;
                    case PathVerb.Cubic:
                    {
                        // a curve: its end is a corner, points along it count as on a line (arcs of a drawing, Ablaufplan 7.6 e)
                        var c1 = matrix.Transform(points[p], points[p + 1]);
                        var c2 = matrix.Transform(points[p + 2], points[p + 3]);
                        var end = matrix.Transform(points[p + 4], points[p + 5]);
                        p += 6;
                        var from = current;
                        for (var k = 1; k <= CurvePieces; k++)
                        {
                            double t = (double)k / CurvePieces, u = 1 - t;
                            (double X, double Y) on = (u * u * u * current.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * end.X,
                                u * u * u * current.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * end.Y);
                            segment(from, on);
                            from = on;
                        }
                        work += CurvePieces;
                        current = end;
                        corner?.Invoke(current);
                        break;
                    }
                    case PathVerb.Close:
                        segment(current, start);
                        current = start;
                        break;
                }
            }
        }
    }
}
