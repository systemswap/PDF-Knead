using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// The shape of a painted path for hit testing (see KONZEPT.md, section 5.20): where its construction lies in the content stream and how it is
/// painted. Read only when asked (a click or the mouse over its bounds), so a page of many paths keeps no geometry in memory.
/// </summary>
/// <param name="Content">The content stream (decoded).</param>
/// <param name="Start">Byte where its construction begins (the operands of its first operator).</param>
/// <param name="End">Byte after its painting operator.</param>
/// <param name="Fills">It is filled.</param>
/// <param name="Strokes">It is stroked.</param>
/// <param name="EvenOdd">Filled by the even-odd rule (f*, B*, b*), otherwise non-zero winding.</param>
internal sealed record PathShape(byte[] Content, int Start, int End, bool Fills, bool Strokes, bool EvenOdd)
{
    /// <summary>At most this many points are made of a path (curves split into lines); a larger one counts by its bounds.</summary>
    private const int MaxPoints = 200_000;

    /// <summary>Lines a curve is split into.</summary>
    private const int CurveSteps = 16;

    /// <summary>
    /// Whether the point lies on the painted path: inside the fill (by its rule), or within <paramref name="tolerance"/> (plus
    /// <paramref name="reach"/> for a stroke) of its outline.
    /// </summary>
    /// <param name="ctm">The CTM of the path (user space to page space).</param>
    /// <param name="x">x in page space.</param>
    /// <param name="y">y in page space.</param>
    /// <param name="tolerance">Allowance in page units.</param>
    /// <param name="reach">Half the line width in page space.</param>
    /// <returns>Whether it is hit; true as well if the path is too large to examine (it was hit by its bounds).</returns>
    public bool Hits(Matrix ctm, double x, double y, double tolerance, double reach)
    {
        if (Flatten(ctm) is not { } subpaths) return true;
        if (Fills && Inside(subpaths, x, y)) return true;
        var limit = (Strokes ? reach : 0) + tolerance;
        foreach (var (points, closed) in subpaths)
        {
            var count = points.Count;
            for (var i = 0; i + 1 < count; i++)
                if (Distance(points[i], points[i + 1], x, y) <= limit) return true;
            if ((closed || Fills) && count > 1 && Distance(points[^1], points[0], x, y) <= limit) return true;
            if (count == 1 && Distance(points[0], points[0], x, y) <= limit) return true;   // a dot (round caps)
        }
        return false;
    }

    /// <summary>The subpaths as points in page space; null if the path has more than <see cref="MaxPoints"/>.</summary>
    private List<(List<(double X, double Y)> Points, bool Closed)>? Flatten(Matrix ctm)
    {
        var result = new List<(List<(double, double)>, bool)>();
        List<(double X, double Y)>? current = null;
        (double X, double Y) last = (0, 0), first = (0, 0);   // user space
        var total = 0;
        (double, double) P(double ux, double uy) => ctm.Transform(ux, uy);
        bool Add(double ux, double uy)
        {
            if (current is null)
            {
                current = [];
                result.Add((current, false));
                first = (ux, uy);
            }
            current.Add(P(ux, uy));
            last = (ux, uy);
            return ++total <= MaxPoints;
        }
        // a segment without a subpath of its own (after h, or without m) begins at the current point, as the display draws it (8.5.2.1;
        // before, at its own end point — docs/FEHLERGEDAECHTNIS.md, No. 1125)
        bool Begin() => current is not null || Add(last.X, last.Y);
        bool Curve((double X, double Y) c1, (double X, double Y) c2, (double X, double Y) to)
        {
            var from = last;
            for (var k = 1; k <= CurveSteps; k++)
            {
                var t = (double)k / CurveSteps;
                var u = 1 - t;
                var px = u * u * u * from.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * to.X;
                var py = u * u * u * from.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * to.Y;
                if (!Add(px, py)) return false;
            }
            return true;
        }
        var parser = new ContentParser(Content.AsSpan(Start, Math.Max(0, End - Start)).ToArray());
        while (parser.Next() is { } op)
        {
            var o = parser.Operands;
            double N(int i) => o[i].Number;
            var ok = true;
            switch (op)
            {
                case "m" when o.Count >= 2:
                    current = null;
                    ok = Add(N(0), N(1));
                    break;
                case "l" when o.Count >= 2:
                    ok = Begin() && Add(N(0), N(1));
                    break;
                case "c" when o.Count >= 6:
                    ok = Begin() && Curve((N(0), N(1)), (N(2), N(3)), (N(4), N(5)));
                    break;
                case "v" when o.Count >= 4:
                    ok = Begin() && Curve(last, (N(0), N(1)), (N(2), N(3)));
                    break;
                case "y" when o.Count >= 4:
                    ok = Begin() && Curve((N(0), N(1)), (N(2), N(3)), (N(2), N(3)));
                    break;
                case "h" when current is not null:
                    result[^1] = (current, true);
                    current = null;
                    last = first;
                    break;
                case "re" when o.Count >= 4:
                    current = null;
                    double rx = N(0), ry = N(1), rw = N(2), rh = N(3);
                    ok = Add(rx, ry) && Add(rx + rw, ry) && Add(rx + rw, ry + rh) && Add(rx, ry + rh);
                    result[^1] = (current!, true);
                    current = null;
                    last = first = (rx, ry);
                    break;
            }
            if (!ok) return null;
        }
        return result;
    }

    /// <summary>Whether the point lies inside the fill: every subpath counts as closed, by the even-odd or the non-zero winding rule.</summary>
    private bool Inside(List<(List<(double X, double Y)> Points, bool Closed)> subpaths, double x, double y)
    {
        var winding = 0;
        var crossings = 0;
        foreach (var (points, _) in subpaths)
        {
            var count = points.Count;
            if (count < 3) continue;
            for (var i = 0; i < count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % count];
                if (a.Y <= y)
                {
                    if (b.Y > y && Side(a, b, x, y) > 0)
                    {
                        winding++;
                        crossings++;
                    }
                }
                else if (b.Y <= y && Side(a, b, x, y) < 0)
                {
                    winding--;
                    crossings++;
                }
            }
        }
        return EvenOdd ? crossings % 2 == 1 : winding != 0;
    }

    /// <summary>Which side of the line a → b the point lies on (positive: left).</summary>
    private static double Side((double X, double Y) a, (double X, double Y) b, double x, double y) => (b.X - a.X) * (y - a.Y) - (x - a.X) * (b.Y - a.Y);

    /// <summary>Distance of the point from the segment a – b.</summary>
    private static double Distance((double X, double Y) a, (double X, double Y) b, double x, double y)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var length = dx * dx + dy * dy;
        var t = length > 0 ? Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / length, 0, 1) : 0;
        double px = a.X + t * dx - x, py = a.Y + t * dy - y;
        return Math.Sqrt(px * px + py * py);
    }
}
