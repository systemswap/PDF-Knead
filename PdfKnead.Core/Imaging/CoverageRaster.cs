namespace PdfKnead.Core.Imaging;

/// <summary>
/// Area coverage per pixel (0 to 1) for filled outlines, exact and without supersampling: every edge adds
/// its share to an accumulation buffer, and a running sum per row yields the coverage. Overlapping areas of the same
/// direction count once (absolute value, clamped to 1); holes of the opposite direction stay empty.
/// Coordinates in pixels, y pointing down.
/// </summary>
/// <remarks>Not thread-safe. Memory: about 4 bytes per pixel for the buffer plus 4 bytes per pixel for the result of <see cref="Coverage"/>.</remarks>
public sealed class CoverageRaster
{
    private readonly float[] _acc;
    private readonly int _stride;
    private double _startX, _startY, _x, _y;
    private bool _open;

    /// <summary>Creates an empty raster.</summary>
    /// <param name="width">Width in px.</param>
    /// <param name="height">Height in px.</param>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive or the raster would exceed 64 megapixels.</exception>
    public CoverageRaster(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 64L << 20) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        _stride = width + 3;
        _acc = new float[_stride * height];
    }

    /// <summary>Width in px.</summary>
    public int Width { get; }

    /// <summary>Height in px.</summary>
    public int Height { get; }

    /// <summary>Starts a new subpath; closes the open one first.</summary>
    /// <param name="x">X in px.</param>
    /// <param name="y">Y in px (pointing down).</param>
    public void MoveTo(double x, double y)
    {
        Close();
        _startX = _x = x;
        _startY = _y = y;
        _open = true;
    }

    /// <summary>Adds a straight edge from the current point.</summary>
    /// <param name="x">X of the end point in px.</param>
    /// <param name="y">Y of the end point in px.</param>
    public void LineTo(double x, double y)
    {
        Line(_x, _y, x, y);
        _x = x;
        _y = y;
    }

    /// <summary>Adds a cubic Bézier curve, flattened into line segments (deviation below a tenth of a pixel, at most 256 segments).</summary>
    /// <param name="x1">X of the first control point.</param>
    /// <param name="y1">Y of the first control point.</param>
    /// <param name="x2">X of the second control point.</param>
    /// <param name="y2">Y of the second control point.</param>
    /// <param name="x3">X of the end point.</param>
    /// <param name="y3">Y of the end point.</param>
    public void CubicTo(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        double x0 = _x, y0 = _y;
        // Number of segments from the second derivative (Wang's formula): error ≤ 0.1 pixel
        var ddx = Math.Max(Math.Abs(x0 - 2 * x1 + x2), Math.Abs(x1 - 2 * x2 + x3));
        var ddy = Math.Max(Math.Abs(y0 - 2 * y1 + y2), Math.Abs(y1 - 2 * y2 + y3));
        var steps = (int)Math.Clamp(Math.Ceiling(Math.Sqrt(Math.Sqrt(ddx * ddx + ddy * ddy) * 0.75 / 0.1)), 1, 256);
        for (var k = 1; k <= steps; k++)
        {
            double t = (double)k / steps, u = 1 - t;
            double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
            LineTo(a * x0 + b * x1 + c * x2 + d * x3, a * y0 + b * y1 + c * y2 + d * y3);
        }
    }

    /// <summary>Closes the subpath (even without this call every subpath is led back to its start point).</summary>
    public void Close()
    {
        if (!_open) return;
        Line(_x, _y, _startX, _startY);
        _x = _startX;
        _y = _startY;
        _open = false;
    }

    /// <summary>
    /// Adds a circle as a counter-clockwise polygon (for round stroke ends), fine enough for a tenth of a pixel and with the same
    /// area as the circle (an inscribed polygon would make strokes slightly thinner).
    /// </summary>
    /// <param name="cx">X of the centre in px.</param>
    /// <param name="cy">Y of the centre in px.</param>
    /// <param name="r">Radius in px; nothing is added if it is not positive.</param>
    public void Circle(double cx, double cy, double r)
    {
        if (r <= 0) return;
        var n = Math.Clamp((int)Math.Ceiling(Math.PI / Math.Acos(Math.Max(-1, 1 - 0.1 / Math.Max(r, 0.1)))), 8, 256);
        r *= Math.Sqrt(2 * Math.PI / (n * Math.Sin(2 * Math.PI / n)));
        MoveTo(cx + r, cy);
        for (var k = 1; k < n; k++) LineTo(cx + r * Math.Cos(-2 * Math.PI * k / n), cy + r * Math.Sin(-2 * Math.PI * k / n));
        Close();
    }

    /// <summary>Adds a polygon with the same winding as <see cref="Circle"/> (positive area), so that overlaps add up.</summary>
    /// <param name="points">Corners in px in either order; fewer than three points or a zero area add nothing.</param>
    public void Polygon(ReadOnlySpan<(double X, double Y)> points)
    {
        if (points.Length < 3) return;
        double area = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % points.Length]);
            area += a.X * b.Y - b.X * a.Y;
        }
        if (area == 0) return;
        MoveTo(points[0].X, points[0].Y);
        if (area < 0)
            for (var i = 1; i < points.Length; i++) LineTo(points[i].X, points[i].Y);
        else
            for (var i = points.Length - 1; i >= 1; i--) LineTo(points[i].X, points[i].Y);
        Close();
    }

    /// <summary>Closes the open subpath and computes the coverage per pixel.</summary>
    /// <returns>A new array of <see cref="Width"/> × <see cref="Height"/> values from 0 to 1, row by row (y pointing down).</returns>
    public float[] Coverage()
    {
        Close();
        var coverage = new float[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            float sum = 0;
            var row = y * _stride;
            for (var x = 0; x < Width; x++)
            {
                sum += _acc[row + x];
                coverage[y * Width + x] = Math.Min(1, Math.Abs(sum));
            }
        }
        return coverage;
    }

    /// <summary>
    /// Adds the edge from (<paramref name="x0"/>, <paramref name="y0"/>) to (<paramref name="x1"/>, <paramref name="y1"/>). In every pixel row it
    /// crosses, the edge spans a strip of height h (signed by its direction); F(c), the part of that strip in column c lying right of the
    /// edge, rises from 0 left of the edge to h right of it, and the buffer takes its steps F(c) − F(c − 1), so that the running sum of a
    /// row (<see cref="Coverage"/>) gives each pixel the area the outline covers. Own derivation (16.11.2026; the procedure taken over before
    /// was replaced, docs/LIZENZPRUEFUNG.md): along the edge x runs linearly over the x range [a, b] it has in the row, and F(c) is h times
    /// the mean of clamp(c + 1 − x, 0, 1) — with the antiderivative <see cref="Ramp"/> of clamp(z, 0, 1),
    /// F(c) = h · (R(c + 1 − a) − R(c + 1 − b)) / (b − a). Within one column that is h · (c + 1 − (a + b) / 2).
    /// </summary>
    private void Line(double x0, double y0, double x1, double y1)
    {
        if (y0 == y1 || double.IsNaN(x0 + y0 + x1 + y1)) return;
        double direction = 1;
        if (y0 > y1)
        {
            (x0, x1) = (x1, x0);
            (y0, y1) = (y1, y0);
            direction = -1;
        }
        if (y1 <= 0 || y0 >= Height) return;
        var slope = (x1 - x0) / (y1 - y0);
        double top = Math.Max(y0, 0), bottom = Math.Min(y1, Height);
        for (var row = (int)Math.Floor(top); row < bottom; row++)
        {
            double ya = Math.Max(row, top), yb = Math.Min(row + 1, bottom);
            if (yb <= ya) continue;
            double xa = x0 + (ya - y0) * slope, xb = x0 + (yb - y0) * slope;
            // beyond the left border the area counts at the border, beyond the right one not at all
            Strip(row * _stride, Math.Clamp(Math.Min(xa, xb), 0, Width), Math.Clamp(Math.Max(xa, xb), 0, Width), (yb - ya) * direction);
        }
    }

    /// <summary>The steps of F in one row (see <see cref="Line"/>): the edge spans [<paramref name="a"/>, <paramref name="b"/>], the strip is <paramref name="h"/> high.</summary>
    private void Strip(int rowStart, double a, double b, double h)
    {
        // the columns from `first` to before `last` meet the edge; from `last` on the whole strip lies right of it
        int first = (int)Math.Floor(a), last = (int)Math.Ceiling(b);
        if (last <= first + 1)
        {
            var inside = h * (first + 1 - 0.5 * (a + b));
            _acc[rowStart + first] += (float)inside;
            _acc[rowStart + first + 1] += (float)(h - inside);
            return;
        }
        double before = 0, span = b - a, step = h / span;
        // where c − 1 + 1 − a ≥ 1 and c + 1 − b ≤ 0, both arguments of R lie on its straight parts: every step there is h / (b − a)
        int even = (int)Math.Ceiling(a + 1), uneven = (int)Math.Floor(b - 1);
        for (var c = first; c < last; c++)
        {
            var f = c >= even && c <= uneven ? before + step : h * (Ramp(c + 1 - a) - Ramp(c + 1 - b)) / span;
            _acc[rowStart + c] += (float)(f - before);
            before = f;
        }
        _acc[rowStart + last] += (float)(h - before);
    }

    /// <summary>R(z), the integral of clamp(t, 0, 1) from −∞ to z: 0 up to 0, z²/2 up to 1, then z − 1/2.</summary>
    private static double Ramp(double z) => z <= 0 ? 0 : z < 1 ? 0.5 * z * z : z - 0.5;
}
