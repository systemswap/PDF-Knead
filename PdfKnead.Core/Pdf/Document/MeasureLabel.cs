namespace PdfKnead.Core.Pdf.Document;

/// <summary>
/// Where the label of a measurement — its value: length, area, angle — may be moved to (decided on 10.11.2026; docs/FEHLERGEDAECHTNIS.md,
/// No. 703). The same rules for the view and for the saved dimension, in any space with the same units on both axes (page space, view):
/// <list type="bullet">
/// <item>an area: the middle of the label inside the area or on its edge — at most half of the label lies outside, the line is the border;</item>
/// <item>a length, a polyline, an arc: the line runs through the label with at least a tenth of it on either side, and along the line the
/// label overlaps it by at least a tenth — of a polyline the nearest piece counts;</item>
/// <item>an angle: the middle of the label between its two legs.</item>
/// </list>
/// </summary>
public static class MeasureLabel
{
    /// <summary>The share of the label that must stay on the far side of the line, and over the line along it.</summary>
    public const double OnLine = 0.1;

    /// <summary>
    /// The nearest place to <paramref name="wanted"/> (the label's middle) that keeps the rules.
    /// </summary>
    /// <param name="kind">What was measured.</param>
    /// <param name="course">The course of the measurement: of an area its outline (closed without repeating the first point), of a length its
    /// two ends, of a polyline or arc its points along the way (arcs finely divided), of an angle its three points (legs and vertex).</param>
    /// <param name="wanted">Where the label's middle is to go.</param>
    /// <param name="width">The label's width.</param>
    /// <param name="height">The label's height.</param>
    /// <param name="angle">Its turn (radians, counter-clockwise in a space with y up; mirrored for y down it is the same rule).</param>
    public static (double X, double Y) Clamp(MeasureKind kind, IReadOnlyList<(double X, double Y)> course, (double X, double Y) wanted, double width,
        double height, double angle = 0)
    {
        if (course.Count == 0 || !double.IsFinite(wanted.X) || !double.IsFinite(wanted.Y)) return wanted;
        return kind switch
        {
            MeasureKind.Area when course.Count >= 3 => Inside(course, wanted) ? wanted : NearestOnOutline(course, wanted, closed: true),
            MeasureKind.Angle when course.Count >= 3 => InAngle(course[0], course[1], course[2], wanted),
            _ when course.Count >= 2 => OnTheLine(course, wanted, width, height, angle),
            _ => wanted,
        };
    }

    /// <summary>Whether a point lies inside a polygon (even-odd) or on its edge.</summary>
    public static bool Inside(IReadOnlyList<(double X, double Y)> polygon, (double X, double Y) p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if (Distance(Nearest(a, b, p), p) < 1e-9) return true;   // on the edge
            if (a.Y > p.Y != b.Y > p.Y && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    private static (double X, double Y) NearestOnOutline(IReadOnlyList<(double X, double Y)> points, (double X, double Y) p, bool closed)
    {
        var best = points[0];
        var bestDistance = double.MaxValue;
        var count = closed ? points.Count : points.Count - 1;
        for (var i = 0; i < count; i++)
        {
            var q = Nearest(points[i], points[(i + 1) % points.Count], p);
            var d = Distance(q, p);
            if (d < bestDistance) (best, bestDistance) = (q, d);
        }
        return best;
    }

    /// <summary>
    /// A line: of each piece the nearest place where the piece runs through the label with a tenth of it on either side and overlaps it by a
    /// tenth along; of those the nearest.
    /// </summary>
    private static (double X, double Y) OnTheLine(IReadOnlyList<(double X, double Y)> course, (double X, double Y) p, double width, double height, double angle)
    {
        var (ux, uy) = (Math.Cos(angle), Math.Sin(angle));
        // how far the label reaches from its middle in a direction (half its extent across that direction)
        double Reach(double dx, double dy) => Math.Abs(dx * ux + dy * uy) * width / 2 + Math.Abs(-dx * uy + dy * ux) * height / 2;
        var best = p;
        var bestDistance = double.MaxValue;
        for (var i = 0; i + 1 < course.Count; i++)
        {
            var (a, b) = (course[i], course[i + 1]);
            var length = Distance(a, b);
            if (length < 1e-12) continue;
            var (dx, dy) = ((b.X - a.X) / length, (b.Y - a.Y) / length);
            var (nx, ny) = (-dy, dx);
            var along = (p.X - a.X) * dx + (p.Y - a.Y) * dy;
            var across = (p.X - a.X) * nx + (p.Y - a.Y) * ny;
            var reachAlong = Reach(dx, dy) * (1 - 2 * OnLine);
            var reachAcross = Reach(nx, ny) * (1 - 2 * OnLine);
            along = Math.Clamp(along, -reachAlong, length + reachAlong);
            across = Math.Clamp(across, -reachAcross, reachAcross);
            var q = (a.X + dx * along + nx * across, a.Y + dy * along + ny * across);
            var d = Distance(q, p);
            if (d < bestDistance) (best, bestDistance) = (q, d);
        }
        return best;
    }

    /// <summary>An angle: inside the sector between the legs from the vertex; outside it the nearest point of a leg.</summary>
    private static (double X, double Y) InAngle((double X, double Y) leg1, (double X, double Y) vertex, (double X, double Y) leg2, (double X, double Y) p)
    {
        (double X, double Y) u = (leg1.X - vertex.X, leg1.Y - vertex.Y), w = (leg2.X - vertex.X, leg2.Y - vertex.Y), x = (p.X - vertex.X, p.Y - vertex.Y);
        static double Cross((double X, double Y) a, (double X, double Y) b) => a.X * b.Y - a.Y * b.X;
        var turn = Cross(u, w);
        if (Math.Abs(turn) > 1e-12 && Cross(u, x) * turn >= 0 && Cross(x, w) * turn >= 0) return p;
        // the nearest point on either leg (as a ray from the vertex)
        (double X, double Y) OnRay((double X, double Y) d)
        {
            var len2 = d.X * d.X + d.Y * d.Y;
            var t = len2 < 1e-24 ? 0 : Math.Max(0, (x.X * d.X + x.Y * d.Y) / len2);
            return (vertex.X + d.X * t, vertex.Y + d.Y * t);
        }
        var (r1, r2) = (OnRay(u), OnRay(w));
        return Distance(r1, p) <= Distance(r2, p) ? r1 : r2;
    }

    private static (double X, double Y) Nearest((double X, double Y) a, (double X, double Y) b, (double X, double Y) p)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var len2 = dx * dx + dy * dy;
        var t = len2 < 1e-24 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        return (a.X + dx * t, a.Y + dy * t);
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
