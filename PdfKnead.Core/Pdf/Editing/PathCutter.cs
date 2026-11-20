using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Cuts what lies under redaction areas out of the geometry of a path, so that its coordinates there leave the file — painting only
/// outside the areas (a clip) left them in it, and a signature drawn as a path could be recovered under the bar
/// (docs/FEHLERGEDAECHTNIS.md, No. 168). Lines and Bézier curves are split exactly at the edges of the areas (de Casteljau).
/// <list type="bullet">
/// <item>Filled: where the outline runs under the areas, that part is replaced by a chain of straight lines, one for each stretch within
/// the same area, from where it enters to where it leaves that area. Each lies inside its (convex) area, so the filled region changes
/// only under the areas, for either fill rule; there only points on the edges of the areas remain.</item>
/// <item>Stroked: the outline is cut into open pieces, without what lies in the area shrunk by the reach of the stroke's ends (half its
/// width, square caps by √2) — the ends of the pieces lie so far inside that nothing of their stroke shows beside the area.</item>
/// </list>
/// The pieces of a dashed stroke are written one by one with the length along the path to where each begins (<see cref="CutDashed"/>).
/// Works in page space; the result is written back through the inverse of the CTM.
/// </summary>
internal static class PathCutter
{
    private readonly record struct Point(double X, double Y);

    /// <summary>A line (B and C unused) or a cubic Bézier curve from A to D.</summary>
    private readonly record struct Segment(bool Curve, Point A, Point B, Point C, Point D)
    {
        public Point At(double t)
        {
            if (!Curve) return new Point(A.X + (D.X - A.X) * t, A.Y + (D.Y - A.Y) * t);
            var u = 1 - t;
            return new Point(u * u * u * A.X + 3 * u * u * t * B.X + 3 * u * t * t * C.X + t * t * t * D.X,
                u * u * u * A.Y + 3 * u * u * t * B.Y + 3 * u * t * t * C.Y + t * t * t * D.Y);
        }

        /// <summary>The part from <paramref name="t0"/> to <paramref name="t1"/> (the segment itself, unchanged, from 0 to 1).</summary>
        public Segment Part(double t0, double t1)
        {
            if (t0 <= 0 && t1 >= 1) return this;
            if (!Curve)
            {
                Point p = At(t0), q = At(t1);
                return new Segment(false, p, p, q, q);
            }
            var right = Split(this, t0).Right;
            var local = t1 >= 1 ? 1 : (t1 - t0) / (1 - t0);
            return Split(right, local).Left;
        }

        private static (Segment Left, Segment Right) Split(Segment s, double t)
        {
            static Point Mix(Point p, Point q, double t) => new(p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t);
            Point ab = Mix(s.A, s.B, t), bc = Mix(s.B, s.C, t), cd = Mix(s.C, s.D, t);
            Point abc = Mix(ab, bc, t), bcd = Mix(bc, cd, t), m = Mix(abc, bcd, t);
            return (new Segment(true, s.A, ab, abc, m), new Segment(true, m, bcd, cd, s.D));
        }
    }

    /// <summary>A subpath: its segments in order, and whether it is closed.</summary>
    private sealed record Subpath(List<Segment> Segments, bool Closed, Point Start);

    /// <summary>
    /// The geometry of one subpath (its operators as written in the content) with what lies under the areas cut out — for filling
    /// (<paramref name="stroke"/> false) or for stroking; null if nothing of it lies under an area (then it stays as written), an empty
    /// string if nothing of it remains.
    /// </summary>
    /// <param name="operators">The path construction of the subpath (m, l, c, v, y, h, re).</param>
    /// <param name="ctm">User space → page space.</param>
    /// <param name="areas">The areas in page space.</param>
    /// <param name="stroke">For stroking: cut into open pieces; otherwise as an area with chords.</param>
    /// <param name="reach">How far the stroke reaches beyond the path in page space (only for stroking).</param>
    public static string? Cut(ReadOnlySpan<byte> operators, Matrix ctm, IReadOnlyList<RectD> areas, bool stroke, double reach)
    {
        if (!ctm.IsInvertible) return null;
        var parsed = Parse(operators, ctm);
        if (parsed.Count == 0) return null;
        var cutters = stroke
            ? areas.Select(a => a.Inflate(-(reach + 0.5))).Where(a => a.Width > 0 && a.Height > 0).ToList()
            : [.. areas];
        if (cutters.Count == 0) return null;
        // all areas at once: cut one after the other, a cut at one left points on its edge inside another, which cut them again
        var changed = false;
        var next = new List<Subpath>();
        foreach (var sub in parsed)
        {
            var result = stroke ? CutStroke(sub, cutters) : CutFill(sub, cutters);
            if (result is null)
            {
                next.Add(sub);
                continue;
            }
            changed = true;
            next.AddRange(result);
        }
        parsed = next;
        return changed ? Write(parsed, ctm.Invert()) : null;
    }

    /// <summary>
    /// Whether a point the subpaths pass through — the start and the end of each segment and 16 steps along each curve, as the check after
    /// redacting counts them (<see cref="Redactor.PathPointUnder"/>) — lies under an area beyond a seam at its edge.
    /// </summary>
    /// <param name="operators">The path construction (m, l, c, v, y, h, re).</param>
    /// <param name="ctm">User space → page space.</param>
    /// <param name="areas">The areas in page space.</param>
    /// <param name="seam">How far inside an area a point may lie (page space).</param>
    public static bool PointUnder(ReadOnlySpan<byte> operators, Matrix ctm, IReadOnlyList<RectD> areas, double seam)
    {
        if (!ctm.IsInvertible) return true;
        foreach (var sub in Parse(operators, ctm))
        {
            if (Under(sub.Start)) return true;
            foreach (var segment in sub.Segments)
            {
                if (Under(segment.D)) return true;
                if (segment.Curve)
                    for (var k = 1; k < 16; k++)
                        if (Under(segment.At(k / 16.0))) return true;
            }
        }
        return false;

        bool Under(Point p)
        {
            foreach (var a in areas)
                if (p.X > a.Left + seam && p.X < a.Right - seam && p.Y > a.Top + seam && p.Y < a.Bottom - seam) return true;
            return false;
        }
    }

    /// <summary>
    /// As <see cref="Cut"/> for a dashed stroke: each piece by itself, with the length along its subpath (in user space, the space of the
    /// dash lengths) from where the subpath begins to where the piece begins — stroked with the pattern started that far in, its dashes
    /// lie where they were. A piece of a closed subpath that would run across its start is split there, where the pattern begins anew.
    /// Null if nothing lies under an area.
    /// </summary>
    public static List<(string Operators, double Offset)>? CutDashed(ReadOnlySpan<byte> operators, Matrix ctm, IReadOnlyList<RectD> areas, double reach)
    {
        if (!ctm.IsInvertible) return null;
        var parsed = Parse(operators, ctm);
        var cutters = areas.Select(a => a.Inflate(-(reach + 0.5))).Where(a => a.Width > 0 && a.Height > 0).ToList();
        if (parsed.Count == 0 || cutters.Count == 0) return null;
        var inverse = ctm.Invert();
        var result = new List<(string, double)>();
        var changed = false;
        foreach (var sub in parsed)
        {
            var segments = new List<Segment>(sub.Segments);
            if (sub.Closed && segments.Count > 0 && segments[^1].D != sub.Start) segments.Add(Line(segments[^1].D, sub.Start));
            var pieces = Pieces(segments, cutters);
            if (pieces.All(p => p.Area < 0))
            {
                result.Add((Write([sub], inverse), 0));
                continue;
            }
            changed = true;
            var along = 0.0;
            List<Segment>? current = null;
            var start = 0.0;
            foreach (var (piece, area) in pieces)
            {
                if (area >= 0)
                {
                    if (current is { Count: > 0 }) result.Add((Write([new Subpath(current, false, current[0].A)], inverse), start));
                    current = null;
                }
                else
                {
                    if (current is null) (current, start) = ([], along);
                    current.Add(piece);
                }
                along += Length(piece, inverse);
            }
            if (current is { Count: > 0 }) result.Add((Write([new Subpath(current, false, current[0].A)], inverse), start));
        }
        return changed ? result : null;
    }

    /// <summary>Length of a segment in user space (a curve as 32 chords).</summary>
    private static double Length(Segment s, Matrix pageToUser)
    {
        double Distance(Point p, Point q)
        {
            var (px, py) = pageToUser.Transform(p.X, p.Y);
            var (qx, qy) = pageToUser.Transform(q.X, q.Y);
            return Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
        }
        if (!s.Curve) return Distance(s.A, s.D);
        var total = 0.0;
        var previous = s.A;
        for (var i = 1; i <= 32; i++)
        {
            var p = s.At(i / 32.0);
            total += Distance(previous, p);
            previous = p;
        }
        return total;
    }

    // -------------------------------------------------- Cutting

    /// <summary>
    /// The pieces of the segments between the crossings of the edges of all areas — so each lies in an area entirely or not at all — and
    /// the first area each lies in (-1: in none).
    /// </summary>
    private static List<(Segment Piece, int Area)> Pieces(IEnumerable<Segment> segments, List<RectD> areas)
    {
        var pieces = new List<(Segment, int)>();
        var ts = new List<double>();
        foreach (var s in segments)
        {
            ts.Clear();
            ts.Add(0);
            ts.Add(1);
            var bounds = Bounds(s);
            foreach (var area in areas)
            {
                if (bounds.Right < area.Left || bounds.Left > area.Right || bounds.Bottom < area.Top || bounds.Top > area.Bottom) continue;
                foreach (var (vertical, value) in (ReadOnlySpan<(bool, double)>)[(true, area.Left), (true, area.Right), (false, area.Top), (false, area.Bottom)])
                    Crossings(s, vertical, value, ts);
            }
            ts.Sort();
            // Neighbouring pieces outside every area stay one: a curve is split only where it really enters or leaves an area, not
            // where it merely crosses the line of an edge beside it — every split makes a reader approximate the curve differently,
            // and a thin slur beside a bar came out a fraction of a pixel thinner (docs/FEHLERGEDAECHTNIS.md, No. 179)
            var from = 0.0;
            var current = int.MinValue;
            for (var i = 0; i + 1 < ts.Count; i++)
            {
                if (ts[i + 1] - ts[i] < 1e-9) continue;
                var mid = s.At((ts[i] + ts[i + 1]) / 2);
                var inside = areas.FindIndex(a => mid.X > a.Left && mid.X < a.Right && mid.Y > a.Top && mid.Y < a.Bottom);
                if (current != int.MinValue && (inside >= 0 || current >= 0))
                {
                    pieces.Add((s.Part(from, ts[i]), current));
                    from = ts[i];
                }
                current = inside;
            }
            if (current != int.MinValue) pieces.Add((s.Part(from, 1), current));
        }
        return pieces;
    }

    /// <summary>Bounds of a segment (of its control points, which enclose the curve).</summary>
    private static RectD Bounds(Segment s) => s.Curve
        ? RectD.FromLtrb(Math.Min(Math.Min(s.A.X, s.B.X), Math.Min(s.C.X, s.D.X)), Math.Min(Math.Min(s.A.Y, s.B.Y), Math.Min(s.C.Y, s.D.Y)),
            Math.Max(Math.Max(s.A.X, s.B.X), Math.Max(s.C.X, s.D.X)), Math.Max(Math.Max(s.A.Y, s.B.Y), Math.Max(s.C.Y, s.D.Y)))
        : RectD.FromLtrb(Math.Min(s.A.X, s.D.X), Math.Min(s.A.Y, s.D.Y), Math.Max(s.A.X, s.D.X), Math.Max(s.A.Y, s.D.Y));

    /// <summary>Adds the parameters (0 … 1) where the segment crosses the line x = value (or y = value) to <paramref name="ts"/>.</summary>
    private static void Crossings(Segment s, bool vertical, double value, List<double> ts)
    {
        double C(Point p) => vertical ? p.X : p.Y;
        if (!s.Curve)
        {
            double a = C(s.A) - value, d = C(s.D) - value;
            if (a * d < 0) ts.Add(a / (a - d));
            return;
        }
        // the coordinate as a cubic in t; roots by sign changes over 32 steps, then bisection (a curve that only touches the line
        // does not cross it and needs no cut)
        double p0 = C(s.A) - value, p1 = C(s.B) - value, p2 = C(s.C) - value, p3 = C(s.D) - value;
        double F(double t)
        {
            var u = 1 - t;
            return u * u * u * p0 + 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t * p3;
        }
        const int Steps = 32;
        var previous = F(0);
        for (var i = 1; i <= Steps; i++)
        {
            double lo = (i - 1) / (double)Steps, hi = i / (double)Steps, fhi = F(hi);
            if (previous * fhi < 0)
            {
                double flo = previous;
                for (var k = 0; k < 50; k++)
                {
                    var mid = (lo + hi) / 2;
                    var fm = F(mid);
                    if (flo * fm <= 0) hi = mid;
                    else (lo, flo) = (mid, fm);
                }
                ts.Add((lo + hi) / 2);
            }
            previous = fhi;
        }
    }

    /// <summary>
    /// As an area: where the outline runs under the areas, a chain of chords instead — one for each stretch within the same area, from
    /// where it enters to where it leaves it (both on its edge, the chord inside it: the region outside the areas stays exactly as it was).
    /// Null if nothing of it lies under an area; empty if all of it does.
    /// </summary>
    private static List<Subpath>? CutFill(Subpath sub, List<RectD> areas)
    {
        var segments = new List<Segment>(sub.Segments);
        // filling closes every subpath
        if (segments.Count > 0 && segments[^1].D != sub.Start) segments.Add(Line(segments[^1].D, sub.Start));
        var pieces = Pieces(segments, areas);
        if (pieces.All(p => p.Area < 0)) return null;
        if (pieces.All(p => p.Area >= 0)) return [];
        // begin at a piece outside that follows one inside, so that every stretch inside lies between two outside ones
        var n = pieces.Count;
        var first = Enumerable.Range(0, n).First(i => pieces[i].Area < 0 && pieces[(i + n - 1) % n].Area >= 0);
        var result = new List<Segment>();
        for (var k = 0; k < n;)
        {
            var (piece, area) = pieces[(first + k) % n];
            if (area < 0)
            {
                result.Add(piece);
                k++;
                continue;
            }
            // as far as the pieces lie in this same area (each lies in an area entirely or not at all)
            var box = areas[area];
            var enter = piece.A;
            var leave = piece.D;
            k++;
            while (k < n && pieces[(first + k) % n].Area >= 0 && Inside(pieces[(first + k) % n].Piece, box)) leave = pieces[(first + k++) % n].Piece.D;
            result.Add(Line(enter, leave));
        }
        return [new Subpath(result, true, result[0].A)];
    }

    /// <summary>The middle of the piece lies inside the area (a piece lies in an area entirely or not at all).</summary>
    private static bool Inside(Segment piece, RectD area)
    {
        var mid = piece.At(0.5);
        return mid.X > area.Left && mid.X < area.Right && mid.Y > area.Top && mid.Y < area.Bottom;
    }

    /// <summary>For stroking: open pieces without what lies in the areas. Null if nothing of it lies inside.</summary>
    private static List<Subpath>? CutStroke(Subpath sub, List<RectD> areas)
    {
        var segments = new List<Segment>(sub.Segments);
        if (sub.Closed && segments.Count > 0 && segments[^1].D != sub.Start) segments.Add(Line(segments[^1].D, sub.Start));
        var pieces = Pieces(segments, areas);
        if (pieces.All(p => p.Area < 0)) return null;
        var n = pieces.Count;
        var start = 0;
        // a closed one is opened where it leaves an area, so that its corner at the old start keeps its join
        if (sub.Closed && pieces.Any(p => p.Area < 0))
            start = Enumerable.Range(0, n).First(i => pieces[i].Area < 0 && pieces[(i + n - 1) % n].Area >= 0);
        var result = new List<Subpath>();
        List<Segment>? current = null;
        for (var k = 0; k < n; k++)
        {
            var (piece, area) = pieces[(start + k) % n];
            if (area >= 0)
            {
                if (current is { Count: > 0 }) result.Add(new Subpath(current, false, current[0].A));
                current = null;
                continue;
            }
            (current ??= []).Add(piece);
        }
        if (current is { Count: > 0 }) result.Add(new Subpath(current, false, current[0].A));
        return result;
    }

    private static Segment Line(Point a, Point b) => new(false, a, a, b, b);

    // -------------------------------------------------- Reading and writing

    /// <summary>The subpaths of the operators, in page space.</summary>
    private static List<Subpath> Parse(ReadOnlySpan<byte> operators, Matrix ctm)
    {
        var result = new List<Subpath>();
        List<Segment>? segments = null;
        Point start = default, current = default;
        var parser = new ContentParser(operators.ToArray());
        void Finish(bool closed)
        {
            if (segments is { Count: > 0 }) result.Add(new Subpath(segments, closed, start));
            segments = null;
        }
        Point P(double x, double y)
        {
            var (px, py) = ctm.Transform(x, y);
            return new Point(px, py);
        }
        while (parser.Next() is { } op)
        {
            var o = parser.Operands;
            switch (op)
            {
                case "m" when o.Count >= 2:
                    Finish(false);
                    start = current = P(o[0].Number, o[1].Number);
                    segments = [];
                    break;
                case "l" when o.Count >= 2:
                {
                    var p = P(o[0].Number, o[1].Number);
                    (segments ??= []).Add(Line(current, p));
                    current = p;
                    break;
                }
                case "c" when o.Count >= 6:
                {
                    Point b = P(o[0].Number, o[1].Number), c = P(o[2].Number, o[3].Number), d = P(o[4].Number, o[5].Number);
                    (segments ??= []).Add(new Segment(true, current, b, c, d));
                    current = d;
                    break;
                }
                case "v" when o.Count >= 4:
                {
                    Point c = P(o[0].Number, o[1].Number), d = P(o[2].Number, o[3].Number);
                    (segments ??= []).Add(new Segment(true, current, current, c, d));
                    current = d;
                    break;
                }
                case "y" when o.Count >= 4:
                {
                    Point b = P(o[0].Number, o[1].Number), d = P(o[2].Number, o[3].Number);
                    (segments ??= []).Add(new Segment(true, current, b, d, d));
                    current = d;
                    break;
                }
                case "h":
                    if (segments is { Count: > 0 } && current != start) segments.Add(Line(current, start));
                    Finish(true);
                    current = start;
                    break;
                case "re" when o.Count >= 4:
                {
                    Finish(false);
                    double x = o[0].Number, y = o[1].Number, w = o[2].Number, h = o[3].Number;
                    Point a = P(x, y), b = P(x + w, y), c = P(x + w, y + h), d = P(x, y + h);
                    result.Add(new Subpath([Line(a, b), Line(b, c), Line(c, d), Line(d, a)], true, a));
                    start = current = a;
                    break;
                }
            }
        }
        Finish(false);
        return result;
    }

    /// <summary>
    /// A coordinate in user space at the precision the display keeps it (single precision, PageInterpreter): the same point for it, in the
    /// shortest form. Rounded to five decimals, the pieces moved by whole points under a matrix that scales by 10⁵ or more
    /// (docs/FEHLERGEDAECHTNIS.md, No. 428; operands are never rounded, No. 132).
    /// </summary>
    private static string Coordinate(double value)
    {
        var single = (float)value;
        return float.IsFinite(single)
            ? PdfSerializer.FormatReal(double.Parse(single.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture))
            : PdfSerializer.FormatReal(value);
    }

    private static string Write(List<Subpath> subpaths, Matrix pageToUser)
    {
        var sb = new StringBuilder();
        void Add(Point p)
        {
            var (x, y) = pageToUser.Transform(p.X, p.Y);
            sb.Append(Coordinate(x)).Append(' ').Append(Coordinate(y)).Append(' ');
        }
        foreach (var sub in subpaths)
        {
            if (sub.Segments.Count == 0) continue;
            Add(sub.Segments[0].A);
            sb.Append("m\n");
            foreach (var s in sub.Segments)
            {
                if (s.Curve)
                {
                    Add(s.B);
                    Add(s.C);
                    Add(s.D);
                    sb.Append("c\n");
                }
                else
                {
                    Add(s.D);
                    sb.Append("l\n");
                }
            }
            if (sub.Closed) sb.Append("h\n");
        }
        return sb.ToString();
    }
}
