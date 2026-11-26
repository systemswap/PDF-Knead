using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Outline of a glyph in font units (TrueType: units per em, CFF/Type 1: usually
/// 1/1000 em). Quadratic curves (TrueType) are stored as cubic ones, with the same verbs
/// as in the display list.
/// </summary>
public sealed class GlyphOutline
{
    /// <summary>The empty outline (shared; must not be modified).</summary>
    public static readonly GlyphOutline Empty = new();

    private readonly List<PathVerb> _verbs = [];
    private readonly List<float> _points = [];
    private float _cx, _cy, _sx, _sy;
    private bool _open;

    /// <summary>Path verbs in order.</summary>
    public IReadOnlyList<PathVerb> Verbs => _verbs;

    /// <summary>Coordinates of the verbs, alternating x and y.</summary>
    public IReadOnlyList<float> Points => _points;

    /// <summary>Whether the outline has no verbs.</summary>
    public bool IsEmpty => _verbs.Count == 0;

    /// <summary>Approximate memory in bytes (lists including spare capacity, object), for the budget of the glyph caches.</summary>
    internal long EstimatedBytes => 4L * _points.Capacity + _verbs.Capacity + 96;

    internal ReadOnlySpan<PathVerb> VerbSpan => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_verbs);

    internal ReadOnlySpan<float> PointSpan => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_points);

    /// <summary>Starts a new subpath; closes an open one first. Consecutive moves replace each other.</summary>
    /// <param name="x">X in font units.</param>
    /// <param name="y">Y in font units.</param>
    public void MoveTo(float x, float y)
    {
        _finished = false;
        if (_open) Close();
        if (_verbs.Count > 0 && _verbs[^1] == PathVerb.Move)
        {
            _points[^2] = x;
            _points[^1] = y;
        }
        else
        {
            _verbs.Add(PathVerb.Move);
            _points.Add(x);
            _points.Add(y);
        }
        _cx = _sx = x;
        _cy = _sy = y;
    }

    /// <summary>Adds a straight line from the current point.</summary>
    /// <param name="x">X of the end point in font units.</param>
    /// <param name="y">Y of the end point in font units.</param>
    public void LineTo(float x, float y)
    {
        _finished = false;
        EnsureStarted();
        _verbs.Add(PathVerb.Line);
        _points.Add(x);
        _points.Add(y);
        _cx = x;
        _cy = y;
        _open = true;
    }

    /// <summary>Adds a cubic Bézier curve from the current point.</summary>
    /// <param name="x1">X of the first control point.</param>
    /// <param name="y1">Y of the first control point.</param>
    /// <param name="x2">X of the second control point.</param>
    /// <param name="y2">Y of the second control point.</param>
    /// <param name="x3">X of the end point.</param>
    /// <param name="y3">Y of the end point.</param>
    public void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3)
    {
        _finished = false;
        EnsureStarted();
        _verbs.Add(PathVerb.Cubic);
        _points.Add(x1); _points.Add(y1);
        _points.Add(x2); _points.Add(y2);
        _points.Add(x3); _points.Add(y3);
        _cx = x3;
        _cy = y3;
        _open = true;
    }

    /// <summary>Adds a quadratic curve from the current point via (qx, qy) to (x, y), stored as a cubic curve.</summary>
    /// <param name="qx">X of the control point.</param>
    /// <param name="qy">Y of the control point.</param>
    /// <param name="x">X of the end point.</param>
    /// <param name="y">Y of the end point.</param>
    public void QuadTo(float qx, float qy, float x, float y) =>
        CubicTo(_cx + 2f / 3 * (qx - _cx), _cy + 2f / 3 * (qy - _cy), x + 2f / 3 * (qx - x), y + 2f / 3 * (qy - y), x, y);

    /// <summary>Closes the current subpath if it is open.</summary>
    public void Close()
    {
        if (!_open) return;
        _verbs.Add(PathVerb.Close);
        _cx = _sx;
        _cy = _sy;
        _open = false;
    }

    /// <summary>Closes the subpaths, removes a dangling move at the end, trims the lists and computes the bounds.</summary>
    /// <returns>This outline.</returns>
    public GlyphOutline Finish()
    {
        _finished = false;
        Close();
        if (_verbs.Count > 0 && _verbs[^1] == PathVerb.Move)
        {
            _verbs.RemoveAt(_verbs.Count - 1);
            _points.RemoveRange(_points.Count - 2, 2);
        }
        // Finished outlines stay in the glyph cache, so they are trimmed to the exact size (the lists double
        // while growing; otherwise up to half of them would stay empty)
        if (_verbs.Capacity > _verbs.Count) _verbs.Capacity = _verbs.Count;
        if (_points.Capacity > _points.Count) _points.Capacity = _points.Count;
        _bounds = ComputeBounds();
        _finished = true;
        return this;
    }

    /// <summary>Appends the outline of another glyph, transformed by <paramref name="m"/> (composite glyphs, seac).</summary>
    /// <param name="other">The outline to append.</param>
    /// <param name="m">Transformation applied to its points.</param>
    public void Append(GlyphOutline other, Matrix m)
    {
        if (_open) Close();
        var p = 0;
        foreach (var verb in other._verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    var (mx, my) = m.Transform(other._points[p], other._points[p + 1]);
                    MoveTo((float)mx, (float)my);
                    p += 2;
                    break;
                case PathVerb.Line:
                    var (lx, ly) = m.Transform(other._points[p], other._points[p + 1]);
                    LineTo((float)lx, (float)ly);
                    p += 2;
                    break;
                case PathVerb.Cubic:
                    var (x1, y1) = m.Transform(other._points[p], other._points[p + 1]);
                    var (x2, y2) = m.Transform(other._points[p + 2], other._points[p + 3]);
                    var (x3, y3) = m.Transform(other._points[p + 4], other._points[p + 5]);
                    CubicTo((float)x1, (float)y1, (float)x2, (float)y2, (float)x3, (float)y3);
                    p += 6;
                    break;
                case PathVerb.Close:
                    Close();
                    break;
            }
        }
        Close();
    }

    // Bounds computed once in Finish: they are needed for every glyph drawn, and one glyph
    // occurs hundreds of times on a page (computing them each time would walk all points again)
    private RectD _bounds;
    private bool _finished;

    /// <summary>Bounds of the control points (they enclose the curves), in font units.</summary>
    /// <remarks>Cached after <see cref="Finish"/>; computed on each call before that.</remarks>
    public RectD Bounds => _finished ? _bounds : ComputeBounds();

    /// <summary>
    /// The smallest rectangle around the outline (ISO 32000-2, 9.2.4): the end points and the extremes of each curve — before the control
    /// points, which a curve need not reach (docs/FEHLERGEDAECHTNIS.md, No. 1153).
    /// </summary>
    private RectD ComputeBounds()
    {
        if (_points.Count == 0) return RectD.Empty;
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        void Add(double x, double y)
        {
            l = Math.Min(l, x); r = Math.Max(r, x);
            t = Math.Min(t, y); b = Math.Max(b, y);
        }
        var p = 0;
        double cx = 0, cy = 0;
        foreach (var verb in _verbs)
        {
            switch (verb)
            {
                case PathVerb.Move or PathVerb.Line when p + 1 < _points.Count:
                    (cx, cy) = (_points[p], _points[p + 1]);
                    Add(cx, cy);
                    p += 2;
                    break;
                case PathVerb.Cubic when p + 5 < _points.Count:
                {
                    double x1 = _points[p], y1 = _points[p + 1], x2 = _points[p + 2], y2 = _points[p + 3], x3 = _points[p + 4], y3 = _points[p + 5];
                    Add(x3, y3);
                    foreach (var s in Extremes(cx, x1, x2, x3)) Add(Cubic(cx, x1, x2, x3, s), Cubic(cy, y1, y2, y3, s));
                    foreach (var s in Extremes(cy, y1, y2, y3)) Add(Cubic(cx, x1, x2, x3, s), Cubic(cy, y1, y2, y3, s));
                    (cx, cy) = (x3, y3);
                    p += 6;
                    break;
                }
            }
        }
        return l > r ? RectD.Empty : RectD.FromLtrb((float)l, (float)t, (float)r, (float)b);
    }

    private static double Cubic(double a, double b, double c, double d, double s)
    {
        var u = 1 - s;
        return u * u * u * a + 3 * u * u * s * b + 3 * u * s * s * c + s * s * s * d;
    }

    /// <summary>The parameters in (0, 1) where one coordinate of a cubic curve has a zero derivative.</summary>
    private static IEnumerable<double> Extremes(double a, double b, double c, double d)
    {
        // derivative / 3: (b − a)(1 − s)² + 2(c − b)(1 − s)s + (d − c)s² = qa s² + qb s + qc
        double qa = -a + 3 * b - 3 * c + d, qb = 2 * (a - 2 * b + c), qc = b - a;
        if (Math.Abs(qa) < 1e-12)
        {
            if (Math.Abs(qb) > 1e-12 && -qc / qb is > 0 and < 1 and var s) yield return s;
            yield break;
        }
        var disc = qb * qb - 4 * qa * qc;
        if (disc < 0) yield break;
        var root = Math.Sqrt(disc);
        foreach (var s in new[] { (-qb + root) / (2 * qa), (-qb - root) / (2 * qa) })
            if (s is > 0 and < 1) yield return s;
    }

    private void EnsureStarted()
    {
        if (_verbs.Count == 0 || (!_open && _verbs[^1] != PathVerb.Move)) MoveTo(_cx, _cy);
    }
}
