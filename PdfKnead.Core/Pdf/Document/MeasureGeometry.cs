namespace PdfKnead.Core.Pdf.Document;

/// <summary>A cubic Bézier piece in page space (from the end of the one before): its two control points and its end.</summary>
public readonly record struct MeasureCurve((double X, double Y) Control1, (double X, double Y) Control2, (double X, double Y) End);

/// <summary>
/// A piece of a polyline or of the outline of an area (Ablaufplan 7.6 j): straight from point <see cref="From"/> to point <see cref="To"/>, or a
/// circular arc from the one through <see cref="Through"/> to the other — circular in the measuring units, so on the paper an ellipse where y
/// has units of its own.
/// </summary>
/// <param name="From">The index of its first point.</param>
/// <param name="To">The index of its last point.</param>
/// <param name="Through">The index of the point the arc goes through; null for a straight piece.</param>
/// <param name="Path">Its course in page space from the first to the last point (an arc divided every 0.5° at least, its ends exact).</param>
/// <param name="Length">Its length in units of x (of an arc the arc, not the chord).</param>
/// <param name="Radius">The radius of an arc in units of x; null for a straight piece.</param>
/// <param name="Middle">The point halfway along it (page space) — where its label goes.</param>
/// <param name="Direction">The direction of travel there (page space, length 1).</param>
/// <param name="StartDirection">The direction of travel at its first point (page space, length 1).</param>
/// <param name="EndDirection">The direction of travel at its last point (page space, length 1).</param>
/// <param name="Curves">An arc as cubic Bézier pieces of at most 90°, split at the point it goes through (page space, ends exact) — for a path
/// with curves (ISO 32000-2, 12.5.6.9, /Path); empty for a straight piece.</param>
public sealed record MeasurePiece(int From, int To, int? Through, IReadOnlyList<(double X, double Y)> Path, double Length, double? Radius,
    (double X, double Y) Middle, (double X, double Y) Direction, (double X, double Y) StartDirection, (double X, double Y) EndDirection,
    IReadOnlyList<MeasureCurve> Curves)
{
    /// <summary>Whether it is an arc.</summary>
    public bool IsArc => Through is not null;

    /// <summary>The direction of travel at its first point in measuring units (length 1) — the angles at bends are measured there.</summary>
    internal (double X, double Y) StartUnits { get; init; }

    /// <summary>The direction of travel at its last point in measuring units (length 1).</summary>
    internal (double X, double Y) EndUnits { get; init; }

    /// <summary>The angle an arc spans in radians, positive counter-clockwise in the measuring units; 0 for a straight piece.</summary>
    internal double Sweep { get; init; }

    /// <summary>The centre of an arc in measuring units, relative to its first point.</summary>
    internal (double X, double Y) CentreUnits { get; init; }

    /// <summary>The angle of its first point seen from the centre (radians, measuring units).</summary>
    internal double StartAngle { get; init; }
}

/// <summary>A polyline or the outline of an area as pieces (<see cref="MeasureGeometry.Build"/>).</summary>
/// <param name="Pieces">Its pieces in order (of an area all the way round, the last one back to the first point).</param>
/// <param name="Path">Its whole course in page space (an area without the first point again at the end).</param>
/// <param name="Length">Its length (of an area its perimeter) in units of x.</param>
/// <param name="SignedArea">Of an area its area in units of x squared, positive counter-clockwise (with the segments of its arcs); 0 for a polyline.</param>
public sealed record MeasureOutline(IReadOnlyList<MeasurePiece> Pieces, IReadOnlyList<(double X, double Y)> Path, double Length, double SignedArea);

/// <summary>
/// The geometry of polylines and areas with arcs (Ablaufplan 7.6 j; decided on 8.11.2026): a point marked as an arc point makes the piece
/// from the point before it to the point after it a circular arc through it (three points, as the tool „Kreis/Bogen“ takes them) — two arcs in a
/// row bending the other way make an S. Everything is worked out in the measuring units of the scale (ISO 32000-2, 12.9: x and y may have units
/// of their own), so lengths and areas are those of the drawing; the course on the page follows from them. Shared by the measuring core, the
/// view, the appearance of a dimension and the writer.
/// </summary>
public static class MeasureGeometry
{
    /// <summary>
    /// Most degrees between two points of the course of an arc: written as /Vertices of a dimension below PDF 2.0, other programs measure
    /// the arc from them — at 0.5° the chords are a hundred-thousandth shorter than the arc.
    /// </summary>
    private const double StepDegrees = 0.5;

    /// <summary>
    /// The arc points that can be: not the first point, of a polyline not the last (an arc needs a point after it), and not right after another
    /// arc point (the point after an arc point is the end of its arc) — the later one of such a pair counts as a plain point. In order, each once.
    /// </summary>
    /// <param name="count">How many points there are.</param>
    /// <param name="arcPoints">The points marked as arc points (indices).</param>
    /// <param name="closed">An area (the piece after the last point goes back to the first).</param>
    public static IReadOnlyList<int> ValidArcPoints(int count, IEnumerable<int>? arcPoints, bool closed)
    {
        if (arcPoints is null || count < 3) return [];
        var valid = new List<int>();
        foreach (var i in arcPoints.Where(i => i > 0 && i < (closed ? count : count - 1)).Distinct().Order())
            if (valid.Count == 0 || valid[^1] != i - 1) valid.Add(i);
        return valid;
    }

    /// <summary>
    /// The pieces of a polyline or area: from each point to the next straight, or an arc where the next point is an arc point (to the point after
    /// it). Three points of an arc on one line make two straight pieces (that is what was drawn). Null if the scale cannot measure them (/Y
    /// without /CYX) or there are too few points.
    /// </summary>
    /// <param name="points">The points (page space).</param>
    /// <param name="arcPoints">The arc points (see <see cref="ValidArcPoints"/>; others are left out).</param>
    /// <param name="closed">An area: the outline goes back to the first point.</param>
    /// <param name="measure">The scale whose units the arcs are circles in.</param>
    public static MeasureOutline? Build(IReadOnlyList<(double X, double Y)> points, IReadOnlyList<int>? arcPoints, bool closed, RectilinearMeasure measure)
    {
        var n = points.Count;
        if (n < 2 || closed && n < 3) return null;
        var arcs = new HashSet<int>(ValidArcPoints(n, arcPoints, closed));
        var pieces = new List<MeasurePiece>();
        for (var i = 0; closed ? i < n : i < n - 1;)
        {
            var next = (i + 1) % n;
            if (arcs.Contains(next))
            {
                var to = (i + 2) % n;
                var arc = Arc(points, i, next, to, measure);
                if (arc is null)
                {
                    // on one line: two straight pieces (or the scale cannot measure)
                    if (Straight(points, i, next, measure) is not { } first || Straight(points, next, to, measure) is not { } second) return null;
                    pieces.Add(first);
                    pieces.Add(second);
                }
                else pieces.Add(arc);
                i += 2;
            }
            else
            {
                if (Straight(points, i, next, measure) is not { } line) return null;
                pieces.Add(line);
                i += 1;
            }
        }

        var path = new List<(double X, double Y)>();
        foreach (var piece in pieces) path.AddRange(path.Count == 0 ? piece.Path : piece.Path.Skip(1));
        if (closed && path.Count > 1) path.RemoveAt(path.Count - 1);   // back at the first point
        double length = 0;
        foreach (var piece in pieces) length += piece.Length;
        double signed = 0;
        if (closed)
        {
            // the polygon of the ends of the pieces (as RectilinearMeasure.Surface: relative to the first point) and the segment of each arc
            var origin = points[0];
            for (var k = 0; k < pieces.Count; k++)
            {
                var (a, b) = (points[pieces[k].From], points[pieces[k].To]);
                if (measure.ToUnits(a.X - origin.X, a.Y - origin.Y) is not { } p || measure.ToUnits(b.X - origin.X, b.Y - origin.Y) is not { } q) return null;
                signed += (p.X * q.Y - q.X * p.Y) / 2;
            }
            foreach (var piece in pieces)
                if (piece is { Radius: { } r })
                {
                    // the segment between the arc and its chord, on the side the arc turns to
                    var angle = Math.Abs(piece.Sweep);
                    signed += Math.Sign(piece.Sweep) * r * r / 2 * (angle - Math.Sin(angle));
                }
        }
        return new MeasureOutline(pieces, path, length, signed);
    }

    /// <summary>A straight piece; null if the scale cannot measure it.</summary>
    private static MeasurePiece? Straight(IReadOnlyList<(double X, double Y)> points, int from, int to, RectilinearMeasure measure)
    {
        var (a, b) = (points[from], points[to]);
        if (measure.ToUnits(b.X - a.X, b.Y - a.Y) is not { } d) return null;
        var length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
        var page = Unit((b.X - a.X, b.Y - a.Y));
        var units = Unit(d);
        return new MeasurePiece(from, to, null, [a, b], length, null, ((a.X + b.X) / 2, (a.Y + b.Y) / 2), page, page, page, [])
        {
            StartUnits = units,
            EndUnits = units,
        };
    }

    /// <summary>The circular arc (in measuring units) from one point through another to a third; null if they lie on one line.</summary>
    private static MeasurePiece? Arc(IReadOnlyList<(double X, double Y)> points, int from, int through, int to, RectilinearMeasure measure)
    {
        var (a, m, b) = (points[from], points[through], points[to]);
        if (measure.ToUnits(m.X - a.X, m.Y - a.Y) is not { } p || measure.ToUnits(b.X - a.X, b.Y - a.Y) is not { } q) return null;
        // the centre where the perpendicular bisectors meet (relative to the first point, as Measuring.Circle)
        var d = 2 * (p.X * q.Y - p.Y * q.X);
        if (Math.Abs(d) < 1e-12 * Math.Max(1, p.X * p.X + p.Y * p.Y + q.X * q.X + q.Y * q.Y)) return null;
        var (pp, qq) = (p.X * p.X + p.Y * p.Y, q.X * q.X + q.Y * q.Y);
        (double X, double Y) c = ((q.Y * pp - p.Y * qq) / d, (p.X * qq - q.X * pp) / d);
        var r = Math.Sqrt(c.X * c.X + c.Y * c.Y);
        if (!double.IsFinite(r) || r > 1e12) return null;
        double a0 = Math.Atan2(-c.Y, -c.X), a1 = Math.Atan2(p.Y - c.Y, p.X - c.X), a2 = Math.Atan2(q.Y - c.Y, q.X - c.X);
        // from the first point to the last on the side of the point between
        var ccw = Positive(a2 - a0);
        var sweep = Positive(a1 - a0) <= ccw ? ccw : ccw - 2 * Math.PI;
        var toMiddle = sweep > 0 ? Positive(a1 - a0) : -Positive(a0 - a1);

        (double X, double Y) Page(double t)
        {
            var (dx, dy) = FromUnits(measure, c.X + r * Math.Cos(t), c.Y + r * Math.Sin(t));
            return (a.X + dx, a.Y + dy);
        }
        // the direction of travel at an angle, in units and on the page
        (double X, double Y) Along(double t) => (-Math.Sin(t) * Math.Sign(sweep), Math.Cos(t) * Math.Sign(sweep));
        (double X, double Y) PageDirection(double t)
        {
            var u = Along(t);
            return Unit(FromUnits(measure, u.X, u.Y));
        }

        var steps = Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) / (StepDegrees * Math.PI / 180)), 8, 1440);
        var path = new (double X, double Y)[steps + 1];
        for (var k = 0; k <= steps; k++) path[k] = Page(a0 + sweep * k / steps);
        path[0] = a;
        path[^1] = b;

        // Bézier pieces of at most 90°, the point between as the end of one of them (k = 4/3 tan(φ/4) of the arc they span)
        var curves = new List<MeasureCurve>();
        void Curves(double start, double span, (double X, double Y) end)
        {
            // a hair over 90° (a half circle split at its middle, its points rounded) is still one piece: the count stays the same
            var count = Math.Max(1, (int)Math.Ceiling(Math.Abs(span) / (Math.PI / 2) - 1e-3));
            for (var k = 0; k < count; k++)
            {
                double t0 = start + span * k / count, t1 = start + span * (k + 1) / count, phi = t1 - t0, f = 4.0 / 3 * Math.Tan(phi / 4) * r;
                (double X, double Y) c1 = (c.X + r * Math.Cos(t0) - f * Math.Sin(t0), c.Y + r * Math.Sin(t0) + f * Math.Cos(t0));
                (double X, double Y) c2 = (c.X + r * Math.Cos(t1) + f * Math.Sin(t1), c.Y + r * Math.Sin(t1) - f * Math.Cos(t1));
                var (x1, y1) = FromUnits(measure, c1.X, c1.Y);
                var (x2, y2) = FromUnits(measure, c2.X, c2.Y);
                curves.Add(new MeasureCurve((a.X + x1, a.Y + y1), (a.X + x2, a.Y + y2), k == count - 1 ? end : Page(t1)));
            }
        }
        Curves(a0, toMiddle, m);
        Curves(a0 + toMiddle, sweep - toMiddle, b);

        return new MeasurePiece(from, to, through, path, r * Math.Abs(sweep), r, Page(a0 + sweep / 2), PageDirection(a0 + sweep / 2), PageDirection(a0),
            PageDirection(a0 + sweep), curves)
        {
            StartUnits = Along(a0),
            EndUnits = Along(a0 + sweep),
            Sweep = sweep,
            CentreUnits = c,
            StartAngle = a0,
        };
    }

    /// <summary>
    /// A point put into a piece where it was clicked (Ctrl+click on the line of a chosen measurement; Ablaufplan 7.6 k): on a straight piece the
    /// point on it nearest to <paramref name="at"/>; on an arc the point of its circle there — the arc is split into two arcs on the same circle,
    /// the arc point set before stays with its half and the other half gets one in its middle, so the shape (length, area) stays the same until
    /// a point is dragged. Null if the piece does not exist, the click is beside its ends or on a point that is there already.
    /// </summary>
    /// <param name="points">The points (page space).</param>
    /// <param name="arcPoints">The arc points.</param>
    /// <param name="closed">An area.</param>
    /// <param name="measure">The scale whose units the arcs are circles in.</param>
    /// <param name="piece">The index of the piece (<see cref="MeasureOutline.Pieces"/>).</param>
    /// <param name="at">Where it was clicked (page space).</param>
    /// <returns>The points and arc points after it, and the index of the new point.</returns>
    public static (IReadOnlyList<(double X, double Y)> Points, IReadOnlyList<int> ArcPoints, int Index)? Insert(IReadOnlyList<(double X, double Y)> points,
        IReadOnlyList<int> arcPoints, bool closed, RectilinearMeasure measure, int piece, (double X, double Y) at)
    {
        if (Build(points, arcPoints, closed, measure) is not { } outline || (uint)piece >= (uint)outline.Pieces.Count) return null;
        var n = points.Count;
        var p = outline.Pieces[piece];
        var arcs = ValidArcPoints(n, arcPoints, closed).ToList();
        var list = points.ToList();
        (IReadOnlyList<(double X, double Y)>, IReadOnlyList<int>, int) With(int position, (double X, double Y)[] added, int[] newArcs, int index)
        {
            // the points from the position on move up; a piece back to the first point gets its new points at the end
            for (var k = 0; k < arcs.Count; k++)
                if (arcs[k] >= position) arcs[k] += added.Length;
            list.InsertRange(position, added);
            arcs.AddRange(newArcs);
            return (list, ValidArcPoints(list.Count, arcs, closed), index);
        }
        var (a, b) = (points[p.From], points[p.To]);
        if (p.Through is not { } through)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
            if (!(length > 1e-12)) return null;
            var t = ((at.X - a.X) * dx + (at.Y - a.Y) * dy) / length;
            if (t <= 1e-3 || t >= 1 - 1e-3) return null;   // on (or beside) an end: a point is there already
            var position = p.To == 0 ? n : p.To;
            return With(position, [(a.X + t * dx, a.Y + t * dy)], [], position);
        }
        // the arc: where the click lies along it, seen from its centre (in units)
        if (measure.ToUnits(at.X - a.X, at.Y - a.Y) is not { } u || measure.ToUnits(points[through].X - a.X, points[through].Y - a.Y) is not { } m) return null;
        var c = p.CentreUnits;
        var r = Math.Sqrt(c.X * c.X + c.Y * c.Y);
        double Along((double X, double Y) v)
        {
            var phi = Math.Atan2(v.Y - c.Y, v.X - c.X);
            return p.Sweep > 0 ? Positive(phi - p.StartAngle) : -Positive(p.StartAngle - phi);
        }
        var (s, sm, total) = (Along(u), Along(m), Math.Abs(p.Sweep));
        var margin = 0.005 * total;
        if (Math.Abs(s) <= margin || Math.Abs(s) >= total - margin || Math.Abs(Math.Abs(s) - Math.Abs(sm)) <= margin) return null;
        (double X, double Y) Page(double offset)
        {
            var angle = p.StartAngle + offset;
            var (px, py) = FromUnits(measure, c.X + r * Math.Cos(angle), c.Y + r * Math.Sin(angle));
            return (a.X + px, a.Y + py);
        }
        var point = Page(s);
        // before the arc point: the first half gets an arc point in its middle, the new point ends it, the old arc point stays with the rest
        if (Math.Abs(s) < Math.Abs(sm)) return With(through, [Page(s / 2), point], [through], through + 1);
        // after it: the new point ends the first half, the second half gets an arc point in its middle
        return With(through + 1, [point, Page((s + p.Sweep) / 2)], [through + 2], through + 1);
    }

    /// <summary>
    /// The arc point of a tangent arc (Ctrl+Alt+click; Ablaufplan 7.6 k, design decision): the circular arc from the last point to
    /// <paramref name="end"/> that starts in the direction the last piece ends in — without a bend, as CAD programs draw polylines with arcs.
    /// Its point halfway along it, to be set as an ordinary arc point (so the arc is one of three points like any other: measured, saved and
    /// changed the same way; dragging that point later makes it a free arc). Null where there is no direction yet (fewer than two points, the
    /// last point an arc point waiting for its end), or the end lies straight ahead (a straight piece then), or the scale cannot measure.
    /// </summary>
    /// <param name="points">The points set so far (page space).</param>
    /// <param name="arcPoints">Their arc points.</param>
    /// <param name="measure">The scale whose units the arcs are circles in.</param>
    /// <param name="end">The end of the arc (page space).</param>
    public static (double X, double Y)? TangentArcPoint(IReadOnlyList<(double X, double Y)> points, IReadOnlyList<int> arcPoints, RectilinearMeasure measure,
        (double X, double Y) end)
    {
        if (points.Count < 2 || arcPoints.Contains(points.Count - 1) || Build(points, arcPoints, false, measure) is not { Pieces.Count: > 0 } outline) return null;
        return TangentFrom(points[^1], outline.Pieces[^1].EndUnits, measure, end);
    }

    /// <summary>
    /// The point halfway along an arc from <paramref name="start"/> to <paramref name="end"/> that leaves <paramref name="start"/> in
    /// <paramref name="direction"/> (page space) — the first arc of a polyline, whose direction the user drags out (decided on 9.11.2026;
    /// docs/FEHLERGEDAECHTNIS.md, No. 664); as <see cref="TangentArcPoint(IReadOnlyList{ValueTuple{double, double}}, IReadOnlyList{int}, RectilinearMeasure, ValueTuple{double, double})"/>
    /// otherwise. Null for no direction, an end straight ahead or a scale that cannot measure.
    /// </summary>
    public static (double X, double Y)? TangentArcPoint((double X, double Y) start, (double X, double Y) direction, RectilinearMeasure measure, (double X, double Y) end)
    {
        if (measure.ToUnits(direction.X, direction.Y) is not { } d) return null;
        var length = Math.Sqrt(d.X * d.X + d.Y * d.Y);
        return length > 0 && double.IsFinite(length) ? TangentFrom(start, (d.X / length, d.Y / length), measure, end) : null;
    }

    /// <summary>The point halfway along the arc from <paramref name="start"/> leaving it in the unit direction <paramref name="d"/> (units) to <paramref name="end"/>.</summary>
    private static (double X, double Y)? TangentFrom((double X, double Y) start, (double X, double Y) d, RectilinearMeasure measure, (double X, double Y) end)
    {
        if (measure.ToUnits(end.X - start.X, end.Y - start.Y) is not { } e) return null;
        // the circle touching the direction at the start and going through the end: its centre on the normal, r = |e|² / (2 e·n)
        (double X, double Y) n = (-d.Y, d.X);
        var across = e.X * n.X + e.Y * n.Y;
        var squared = e.X * e.X + e.Y * e.Y;
        if (!(squared > 0) || Math.Abs(across) <= 1e-6 * Math.Sqrt(squared)) return null;   // straight ahead (or the start itself)
        var r = squared / (2 * across);   // negative: the centre on the right
        (double X, double Y) c = (n.X * r, n.Y * r);
        double a0 = Math.Atan2(-c.Y, -c.X), a2 = Math.Atan2(e.Y - c.Y, e.X - c.X);
        // going round in the sense the direction gives: counter-clockwise with the centre on the left
        var sweep = r > 0 ? Positive(a2 - a0) : -Positive(a0 - a2);
        var middle = a0 + sweep / 2;
        var radius = Math.Abs(r);
        var (dx, dy) = FromUnits(measure, c.X + radius * Math.Cos(middle), c.Y + radius * Math.Sin(middle));
        return (start.X + dx, start.Y + dy);
    }

    /// <summary>
    /// A point taken out (chosen with a click on its handle, then Del; Ablaufplan 7.6 k): an arc point makes its piece straight; a corner joins
    /// its neighbours straight — an arc that loses its end becomes straight too (its arc point stays as a plain point). Null if too few points
    /// would be left (a polyline needs two, an area three).
    /// </summary>
    /// <returns>The points and arc points after it.</returns>
    public static (IReadOnlyList<(double X, double Y)> Points, IReadOnlyList<int> ArcPoints)? Remove(IReadOnlyList<(double X, double Y)> points,
        IReadOnlyList<int> arcPoints, bool closed, int index)
    {
        var n = points.Count;
        if ((uint)index >= (uint)n || n - 1 < (closed ? 3 : 2)) return null;
        var arcs = new HashSet<int>(ValidArcPoints(n, arcPoints, closed));
        if (!arcs.Remove(index))
        {
            // a corner: the arcs ending or beginning there lose an end
            arcs.Remove(closed ? (index + n - 1) % n : index - 1);
            arcs.Remove(closed ? (index + 1) % n : index + 1);
        }
        var list = points.ToList();
        list.RemoveAt(index);
        return (list, ValidArcPoints(list.Count, arcs.Select(i => i > index ? i - 1 : i), closed));
    }

    /// <summary>An angle in [0, 2π).</summary>
    private static double Positive(double a)
    {
        a %= 2 * Math.PI;
        return a < 0 ? a + 2 * Math.PI : a;
    }

    /// <summary>A change in units of x and y back into default user space (the inverse of <see cref="RectilinearMeasure.ToUnits"/>).</summary>
    internal static (double X, double Y) FromUnits(RectilinearMeasure measure, double ux, double uy) =>
        measure.Y is null ? (ux / measure.X.Factor, uy / measure.X.Factor) : (ux / measure.X.Factor, uy / (measure.Y.Factor * measure.YToX!.Value));

    private static (double X, double Y) Unit((double X, double Y) v)
    {
        var l = Math.Sqrt(v.X * v.X + v.Y * v.Y);
        return l > 0 && double.IsFinite(l) ? (v.X / l, v.Y / l) : (1, 0);
    }
}
