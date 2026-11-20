using System.Buffers;
using PdfKnead.Core.Ocr;

namespace PdfKnead.Ai;

/// <summary>
/// The boxes of text from the probability map of the detection network (DB, "differentiable binarization"; PaddleOCR's DBPostProcess, built
/// without OpenCV): pixels above the threshold, joined to regions (8-neighbourhood); each region's smallest turned rectangle; its mean
/// probability inside that rectangle at least the box threshold; widened by its area times the unclip ratio divided by its perimeter, as the
/// network was trained to give text a little smaller than it is.
/// </summary>
internal static class TextBoxes
{
    /// <summary>Boxes with a shorter side than this (in map pixels) are dropped.</summary>
    private const double MinSize = 3;

    /// <summary>Finds the boxes.</summary>
    /// <param name="map">The probability per pixel, row by row.</param>
    /// <param name="width">Width of the map.</param>
    /// <param name="height">Height of the map.</param>
    /// <param name="threshold">A pixel counts as text from this probability on.</param>
    /// <param name="boxThreshold">A box counts from this mean probability on.</param>
    /// <param name="unclipRatio">How far a box is widened.</param>
    /// <param name="maxBoxes">At most this many regions are looked at (the largest first).</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The boxes as quadrilaterals (top left, top right, bottom right, bottom left) in map pixels.</returns>
    public static List<OcrPoint[]> Find(ReadOnlySpan<float> map, int width, int height, double threshold, double boxThreshold, double unclipRatio, int maxBoxes,
        CancellationToken cancel = default)
    {
        var regions = new List<List<int>>();
        var stack = new Stack<int>();
        // pixels already in a region; pooled: a page map has about two million of them
        var size = width * height;
        var seen = ArrayPool<bool>.Shared.Rent(size);
        try
        {
            Array.Clear(seen, 0, size);
            Regions(map, width, height, threshold, seen, regions, stack, cancel);
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(seen);
        }
        var boxes = new List<OcrPoint[]>();

        foreach (var pixels in regions.OrderByDescending(r => r.Count).Take(maxBoxes))
        {
            cancel.ThrowIfCancellationRequested();
            if (MinRect(Outline(pixels, width)) is not { } rect || Math.Min(rect.W, rect.H) < MinSize) continue;
            if (MeanInside(map, width, height, Corners(rect)) < boxThreshold) continue;
            var d = rect.W * rect.H * unclipRatio / (2 * (rect.W + rect.H));
            var grown = rect with { W = rect.W + 2 * d, H = rect.H + 2 * d };
            if (Math.Min(grown.W, grown.H) < MinSize + 2) continue;
            boxes.Add(Ordered(Corners(grown)));
        }
        return boxes;
    }

    /// <summary>The regions above the threshold: pixels joined with their eight neighbours.</summary>
    private static void Regions(ReadOnlySpan<float> map, int width, int height, double threshold, bool[] seen, List<List<int>> regions, Stack<int> stack,
        CancellationToken cancel)
    {
        for (var start = 0; start < width * height; start++)
        {
            if (seen[start] || map[start] <= threshold) continue;
            if ((start & 0xFFFF) == 0) cancel.ThrowIfCancellationRequested();
            var pixels = new List<int>();
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                pixels.Add(p);
                int px = p % width, py = p / width;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx, ny = py + dy;
                        if ((uint)nx >= (uint)width || (uint)ny >= (uint)height) continue;
                        var n = ny * width + nx;
                        if (seen[n] || map[n] <= threshold) continue;
                        seen[n] = true;
                        stack.Push(n);
                    }
            }
            regions.Add(pixels);
        }
    }

    /// <summary>A turned rectangle: centre, side lengths and the direction of its first side (unit vector).</summary>
    internal readonly record struct TurnedRect(double Cx, double Cy, double W, double H, double Ux, double Uy);

    /// <summary>
    /// The outline points of a region: for each row its leftmost and rightmost pixel — enough for the convex hull (whatever lies between them
    /// is inside it). A pixel stands at its index, as the outlines the thresholds were tuned with (pixel centres, not their outer edges).
    /// </summary>
    private static List<(double X, double Y)> Outline(List<int> pixels, int width)
    {
        var rows = new Dictionary<int, (int Min, int Max)>();
        foreach (var p in pixels)
        {
            int x = p % width, y = p / width;
            rows[y] = rows.TryGetValue(y, out var r) ? (Math.Min(r.Min, x), Math.Max(r.Max, x)) : (x, x);
        }
        var points = new List<(double, double)>(rows.Count * 2);
        foreach (var (y, (min, max)) in rows)
        {
            points.Add((min, y));
            points.Add((max, y));
        }
        return points;
    }

    /// <summary>The smallest turned rectangle around points: on the convex hull, one side always lies on an edge of it (rotating calipers).</summary>
    internal static TurnedRect? MinRect(List<(double X, double Y)> points)
    {
        var hull = Hull(points);
        if (hull.Count < 3) return null;
        TurnedRect? best = null;
        var bestArea = double.MaxValue;
        for (var i = 0; i < hull.Count; i++)
        {
            var (ax, ay) = hull[i];
            var (bx, by) = hull[(i + 1) % hull.Count];
            double ex = bx - ax, ey = by - ay, len = Math.Sqrt(ex * ex + ey * ey);
            if (len < 1e-9) continue;
            double ux = ex / len, uy = ey / len;
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (var (x, y) in hull)
            {
                double u = x * ux + y * uy, v = -x * uy + y * ux;
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v);
                maxV = Math.Max(maxV, v);
            }
            var area = (maxU - minU) * (maxV - minV);
            if (area >= bestArea) continue;
            bestArea = area;
            double cu = (minU + maxU) / 2, cv = (minV + maxV) / 2;
            best = new TurnedRect(cu * ux - cv * uy, cu * uy + cv * ux, maxU - minU, maxV - minV, ux, uy);
        }
        return best;
    }

    /// <summary>Convex hull (monotone chain), counter-clockwise without repeated points.</summary>
    private static List<(double X, double Y)> Hull(List<(double X, double Y)> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (sorted.Count < 3) return sorted;
        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<(double, double)>(sorted.Count * 2);
        foreach (var p in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        var lower = hull.Count + 1;
        for (var i = sorted.Count - 2; i >= 0; i--)
        {
            var p = sorted[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    /// <summary>The corners of a turned rectangle.</summary>
    internal static OcrPoint[] Corners(TurnedRect r)
    {
        double hx = r.Ux * r.W / 2, hy = r.Uy * r.W / 2, vx = -r.Uy * r.H / 2, vy = r.Ux * r.H / 2;
        return
        [
            new(r.Cx - hx - vx, r.Cy - hy - vy), new(r.Cx + hx - vx, r.Cy + hy - vy),
            new(r.Cx + hx + vx, r.Cy + hy + vy), new(r.Cx - hx + vx, r.Cy - hy + vy),
        ];
    }

    /// <summary>
    /// The corners in reading order as PaddleOCR orders them: the two with the smaller x are the left ones (the upper of them first), the
    /// other two the right ones.
    /// </summary>
    internal static OcrPoint[] Ordered(OcrPoint[] corners)
    {
        var byX = corners.OrderBy(p => p.X).ToArray();
        var (tl, bl) = byX[0].Y <= byX[1].Y ? (byX[0], byX[1]) : (byX[1], byX[0]);
        var (tr, br) = byX[2].Y <= byX[3].Y ? (byX[2], byX[3]) : (byX[3], byX[2]);
        return [tl, tr, br, bl];
    }

    /// <summary>The mean probability of the pixels inside a quadrilateral (its edges included).</summary>
    private static double MeanInside(ReadOnlySpan<float> map, int width, int height, OcrPoint[] quad)
    {
        int x0 = Math.Clamp((int)Math.Floor(quad.Min(p => p.X)), 0, width - 1), x1 = Math.Clamp((int)Math.Ceiling(quad.Max(p => p.X)), 0, width - 1);
        int y0 = Math.Clamp((int)Math.Floor(quad.Min(p => p.Y)), 0, height - 1), y1 = Math.Clamp((int)Math.Ceiling(quad.Max(p => p.Y)), 0, height - 1);
        double sum = 0;
        long count = 0;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
                if (Inside(quad, x, y))
                {
                    sum += map[y * width + x];
                    count++;
                }
        return count == 0 ? 0 : sum / count;
    }

    /// <summary>Whether a point lies inside a convex quadrilateral (either orientation).</summary>
    internal static bool Inside(OcrPoint[] quad, double x, double y)
    {
        int sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var (a, b) = (quad[i], quad[(i + 1) % 4]);
            var cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
            var s = Math.Sign(cross);
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }
}
