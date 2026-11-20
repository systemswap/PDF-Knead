using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Forms.Recognition;

/// <summary>A straight axis-parallel line of the page: horizontal at y = <see cref="Pos"/> from x <see cref="From"/> to <see cref="To"/>, or vertical
/// at x = <see cref="Pos"/> from y <see cref="From"/> to <see cref="To"/>.</summary>
internal readonly record struct Segment(double Pos, double From, double To)
{
    public double Length => To - From;
}

/// <summary>
/// The lines, rectangles and small circles a page draws (KONZEPT.md, section 5.9a, step 3), in page space (y upwards), from the display list
/// without the fields and annotations: stroked lines, thin filled rectangles (many programs draw rules that way), stroked rectangles and light
/// filled rectangles (field backgrounds), and table cells where horizontal and vertical lines enclose them. White is left out: it draws nothing
/// on paper.
/// </summary>
internal sealed class PageShapes
{
    /// <summary>At most this many lines per direction: a page of hatching or a drawing has no form to find.</summary>
    public const int MaxLines = 3000;

    /// <summary>At most this many rectangles (with the cells of tables) per page.</summary>
    public const int MaxBoxes = 4000;

    public List<Segment> Horizontal { get; } = [];
    public List<Segment> Vertical { get; } = [];

    /// <summary>Closed rectangles: stroked, light filled, and the cells of tables.</summary>
    public List<RectD> Boxes { get; } = [];

    /// <summary>Small round shapes (option buttons on paper).</summary>
    public List<RectD> Circles { get; } = [];

    /// <summary>At most this many cell marks are kept (letters in a scan give many short strokes).</summary>
    public const int MaxTicks = 50_000;

    /// <summary>
    /// Short vertical strokes (1.2 … 8 points): the marks between the cells of a comb line (a line to write on with a small stroke per
    /// character, KONZEPT.md, section 5.9a) — and in scans also the stems of letters; only those standing on a line count.
    /// </summary>
    public List<Segment> Ticks { get; } = [];

    /// <summary>The page draws too many lines to look for a form in.</summary>
    public bool TooMany { get; private set; }

    /// <param name="list">The display list of the page.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <param name="scan">The image of a scanned page: its lines are found in the pixels (the content shows only the image).</param>
    public static PageShapes From(DisplayList list, CancellationToken cancel = default, ScanImage? scan = null)
    {
        var shapes = new PageShapes();
        if (scan is not null) shapes.FromImage(scan, cancel);
        var items = list.Items.Span;
        var points = new List<(double X, double Y)>(16);
        for (var i = 0; i < items.Length; i++)
        {
            if ((i & 1023) == 0) cancel.ThrowIfCancellationRequested();
            ref readonly var item = ref items[i];
            if (item.Glyph || item.Path < 0 || item.Kind == DisplayItemKind.Image || list.IsHidden(i)) continue;
            var paint = list.Paints[item.Paint];
            var stroked = item.Kind is DisplayItemKind.Stroke or DisplayItemKind.FillStroke && !IsWhite(paint.Stroke);
            var filled = item.Kind is DisplayItemKind.Fill or DisplayItemKind.FillStroke && !IsWhite(paint.Fill);
            if (!stroked && !filled) continue;
            var (v0, vc, p) = list.Paths[item.Path];
            var m = list.Matrices[item.Matrix];
            var curved = false;
            points.Clear();
            for (var v = v0; v < v0 + vc; v++)
            {
                switch (list.Verbs[v])
                {
                    case PathVerb.Move:
                        if (points.Count > 0) shapes.Subpath(points, curved, stroked, filled, closed: false);
                        points.Clear();
                        curved = false;
                        points.Add(m.Transform(list.Points[p], list.Points[p + 1]));
                        p += 2;
                        break;
                    case PathVerb.Line:
                        points.Add(m.Transform(list.Points[p], list.Points[p + 1]));
                        p += 2;
                        break;
                    case PathVerb.Cubic:
                        curved = true;
                        points.Add(m.Transform(list.Points[p + 4], list.Points[p + 5]));
                        p += 6;
                        break;
                    case PathVerb.Close:
                        shapes.Subpath(points, curved, stroked, filled, closed: true);
                        var start = points.Count > 0 ? points[0] : default;
                        points.Clear();
                        if (start != default) points.Add(start);
                        curved = false;
                        break;
                }
            }
            if (points.Count > 1) shapes.Subpath(points, curved, stroked, filled, closed: false);
            if (shapes.Horizontal.Count > MaxLines * 4 || shapes.Vertical.Count > MaxLines * 4)
            {
                shapes.TooMany = true;
                return shapes;
            }
        }
        shapes.Merge();
        if (shapes.Horizontal.Count > MaxLines || shapes.Vertical.Count > MaxLines)
        {
            shapes.TooMany = true;
            return shapes;
        }
        shapes.Cells(cancel);
        shapes.Dedupe();
        return shapes;
    }

    /// <summary>Darker than this is ink (0 black … 255 white).</summary>
    private const int Ink = 150;

    /// <summary>
    /// Lines in a scan (KONZEPT.md, section 5.9a, step 3): runs of ink along the rows (and the columns) at least 8 points long, with gaps of
    /// up to two pixels (a weak scan breaks a line) but nearly all ink (the feet of the letters of a word are no line); runs in neighbouring rows that lie on each other are one stroke. A stroke at most 3 points
    /// thick is a line, a thicker one a filled area (a bar, a logo), and a line has paper beside it (the edges of white letters on a dark area
    /// are none). Boxes and table cells then come from the lines, as for drawn ones.
    /// </summary>
    private void FromImage(ScanImage scan, CancellationToken cancel)
    {
        var m = scan.ImageToPage;
        var (x0, y0) = m.Transform(0, 0);
        var (x1, y1) = m.Transform(1, 0);
        var pointsPerPixel = Math.Max(0.01, Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)));
        var minRun = Math.Max(4, (int)Math.Ceiling(8 / pointsPerPixel));
        var maxThick = Math.Max(2, (int)Math.Ceiling(3 / pointsPerPixel));
        int w = scan.Width, h = scan.Height;
        if (w <= 0 || h <= 0 || scan.Gray.Length < (long)w * h) return;
        var minTick = Math.Max(3, (int)Math.Ceiling(1.2 / pointsPerPixel));
        // right beside the stroke (text set close under a line is no reason to drop it)
        const int aside = 2;
        Strokes(h, w, minRun, (row, i) => scan.Gray[row * w + i] < Ink, (row0, row1, from, to) =>
        {
            if (OnPaper(from, to, k => (row0 - aside, k), k => (row1 + aside, k))) Add(from, (row0 + row1 + 1) / 2.0, to, (row0 + row1 + 1) / 2.0);
        });
        // across the columns also the short strokes: the marks between the cells of comb lines
        Strokes(w, h, minTick, (col, i) => scan.Gray[i * w + col] < Ink, (col0, col1, from, to) =>
        {
            if (to - from < minRun || OnPaper(from, to, k => (k, col0 - aside), k => (k, col1 + aside))) Add((col0 + col1 + 1) / 2.0, from, (col0 + col1 + 1) / 2.0, to);
        });

        // a line lies on paper: beside it on both sides little ink (at most 30 %) — the edges of white letters on a dark area are no lines
        bool OnPaper(int from, int to, Func<int, (int Row, int Col)> before, Func<int, (int Row, int Col)> after)
        {
            int inkBefore = 0, inkAfter = 0, count = 0;
            for (var k = from; k < to; k += 2)
            {
                count++;
                var (r0, c0) = before(k);
                var (r1, c1) = after(k);
                if (r0 < 0 || c0 < 0 || r0 >= h || c0 >= w || scan.Gray[r0 * w + c0] < Ink) inkBefore++;
                if (r1 < 0 || c1 < 0 || r1 >= h || c1 >= w || scan.Gray[r1 * w + c1] < Ink) inkAfter++;
            }
            // beside a line on paper there is next to no ink (the marks of a comb line are about a tenth)
            return count == 0 || inkBefore <= 0.3 * count && inkAfter <= 0.3 * count;
        }

        void Add(double ax, double ay, double bx, double by)
        {
            var (px, py) = m.Transform(ax, ay);
            var (qx, qy) = m.Transform(bx, by);
            var horizontal = Math.Abs(qx - px) >= Math.Abs(qy - py);
            var segment = horizontal ? new Segment((py + qy) / 2, Math.Min(px, qx), Math.Max(px, qx)) : new Segment((px + qx) / 2, Math.Min(py, qy), Math.Max(py, qy));
            if (segment.Length < 8 - 0.01)
            {
                if (!horizontal && segment.Length <= 8 && Ticks.Count < MaxTicks) Ticks.Add(segment);
                return;
            }
            (horizontal ? Horizontal : Vertical).Add(segment);
        }

        // runs along each line of pixels (rows or columns), joined across neighbouring lines into strokes
        void Strokes(int lines, int length, int minRun, Func<int, int, bool> ink, Action<int, int, int, int> found)
        {
            var open = new List<(int First, int Last, int From, int To)>();
            var next = new List<(int First, int Last, int From, int To)>();
            var runs = new List<(int From, int To)>();
            for (var line = 0; line <= lines; line++)
            {
                if ((line & 255) == 0) cancel.ThrowIfCancellationRequested();
                runs.Clear();
                if (line < lines)
                    for (var i = 0; i < length; i++)
                    {
                        if (!ink(line, i)) continue;
                        var start = i;
                        var last = i;
                        var count = 1;
                        while (++i < length)
                        {
                            if (ink(line, i))
                            {
                                last = i;
                                count++;
                            }
                            else if (i - last > 2) break;
                        }
                        // a line is nearly all ink; the feet of letters in a row have gaps between them
                        if (last - start + 1 >= minRun && count >= 0.85 * (last - start + 1)) runs.Add((start, last + 1));
                        i = last;
                    }
                next.Clear();
                var used = new bool[runs.Count];
                foreach (var stroke in open)
                {
                    var joined = false;
                    for (var r = 0; r < runs.Count; r++)
                    {
                        var (from, to) = runs[r];
                        var overlap = Math.Min(to, stroke.To) - Math.Max(from, stroke.From);
                        if (used[r] || overlap < 0.5 * Math.Min(to - from, stroke.To - stroke.From)) continue;
                        used[r] = true;
                        next.Add((stroke.First, line, Math.Min(from, stroke.From), Math.Max(to, stroke.To)));
                        joined = true;
                        break;
                    }
                    if (!joined && stroke.Last - stroke.First + 1 <= maxThick) found(stroke.First, stroke.Last, stroke.From, stroke.To);
                }
                for (var r = 0; r < runs.Count; r++)
                    if (!used[r]) next.Add((line, line, runs[r].From, runs[r].To));
                (open, next) = (next, open);
                if (Horizontal.Count > MaxLines * 4 || Vertical.Count > MaxLines * 4) return;
            }
        }
    }

    /// <summary>Near white (or fully transparent): nothing on paper.</summary>
    private static bool IsWhite(uint argb) =>
        (argb >> 24) < 0x20 || ((argb >> 16) & 0xFF) > 0xF4 && ((argb >> 8) & 0xFF) > 0xF4 && (argb & 0xFF) > 0xF4;

    private void Subpath(List<(double X, double Y)> pts, bool curved, bool stroked, bool filled, bool closed)
    {
        if (pts.Count < 2) return;
        double l = double.MaxValue, b = double.MaxValue, r = double.MinValue, t = double.MinValue;
        foreach (var (x, y) in pts)
        {
            l = Math.Min(l, x);
            r = Math.Max(r, x);
            b = Math.Min(b, y);
            t = Math.Max(t, y);
        }
        double w = r - l, h = t - b;
        if (curved)
        {
            // a small round shape: an option button
            if (w is >= 5 and <= 24 && h is >= 5 and <= 24 && Math.Abs(w - h) <= 0.2 * Math.Max(w, h)) Circles.Add(new RectD(l, b, r, t));
            return;
        }
        var axisRect = pts.Count is 4 or 5 && pts.All(q => (Near(q.X, l) || Near(q.X, r)) && (Near(q.Y, b) || Near(q.Y, t)));
        if (filled && !stroked && axisRect)
        {
            // a filled rectangle: a thin one is a rule; a light one of a field's size is a field background
            if (h <= 2.5 && w >= 8) Horizontal.Add(new Segment((b + t) / 2, l, r));
            else if (w <= 2.5 && h >= 8) Vertical.Add(new Segment((l + r) / 2, b, t));
            else if (w >= 5 && h >= 5) Boxes.Add(new RectD(l, b, r, t));
            return;
        }
        if (!stroked) return;
        if (axisRect && w >= 5 && h >= 5) Boxes.Add(new RectD(l, b, r, t));
        var count = closed ? pts.Count : pts.Count - 1;
        for (var k = 0; k < count; k++)
        {
            var (x0, y0) = pts[k];
            var (x1, y1) = pts[(k + 1) % pts.Count];
            if (Math.Abs(y1 - y0) <= 0.5 && Math.Abs(x1 - x0) >= 3) Horizontal.Add(new Segment((y0 + y1) / 2, Math.Min(x0, x1), Math.Max(x0, x1)));
            else if (Math.Abs(x1 - x0) <= 0.5 && Math.Abs(y1 - y0) >= 3) Vertical.Add(new Segment((x0 + x1) / 2, Math.Min(y0, y1), Math.Max(y0, y1)));
            if (Math.Abs(x1 - x0) <= 0.5 && Math.Abs(y1 - y0) is >= 1.2 and <= 8 && Ticks.Count < MaxTicks)
                Ticks.Add(new Segment((x0 + x1) / 2, Math.Min(y0, y1), Math.Max(y0, y1)));
        }
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) <= 0.5;

    /// <summary>Collinear pieces that touch or overlap become one line (a rule drawn in parts, the edges of adjacent cells).</summary>
    private void Merge()
    {
        static List<Segment> Join(List<Segment> lines)
        {
            lines.Sort((a, b) => a.Pos != b.Pos ? a.Pos.CompareTo(b.Pos) : a.From.CompareTo(b.From));
            var merged = new List<Segment>(lines.Count);
            var used = new bool[lines.Count];
            for (var i = 0; i < lines.Count; i++)
            {
                if (used[i]) continue;
                var cur = lines[i];
                for (var j = i + 1; j < lines.Count && lines[j].Pos - cur.Pos <= 0.8; j++)
                {
                    if (used[j] || lines[j].From > cur.To + 1.5) continue;
                    cur = cur with { To = Math.Max(cur.To, lines[j].To), From = Math.Min(cur.From, lines[j].From) };
                    used[j] = true;
                }
                merged.Add(cur);
            }
            return merged;
        }
        var h = Join(Horizontal);
        Horizontal.Clear();
        Horizontal.AddRange(h);
        var v = Join(Vertical);
        Vertical.Clear();
        Vertical.AddRange(v);
    }

    /// <summary>
    /// Table cells: between two horizontal lines, the stretches between consecutive vertical lines that run across both — the smallest
    /// enclosed rectangles (a line in between splits a cell).
    /// </summary>
    private void Cells(CancellationToken cancel)
    {
        var rows = Horizontal.Where(s => s.Length >= 6).OrderByDescending(s => s.Pos).ToList();
        var columns = Vertical.Where(s => s.Length >= 6).OrderBy(s => s.Pos).ToList();
        if (rows.Count < 2 || columns.Count < 2) return;
        // a budget of steps: a grid of thousands of lines (hatching, a drawing) is no table to fill in
        var budget = 20_000_000L;
        for (var i = 0; i < rows.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var top = rows[i];
            for (var j = i + 1; j < rows.Count; j++)
            {
                if ((budget -= columns.Count) < 0 || Boxes.Count > MaxBoxes)
                {
                    TooMany = true;
                    return;
                }
                var bottom = rows[j];
                var height = top.Pos - bottom.Pos;
                if (height < 5) continue;
                if (height > 200) break;
                double from = Math.Max(top.From, bottom.From), to = Math.Min(top.To, bottom.To);
                if (to - from < 5) continue;
                double? left = null;
                foreach (var c in columns)
                {
                    if (c.Pos < from - 1 || c.Pos > to + 1 || c.From > bottom.Pos + 1.5 || c.To < top.Pos - 1.5) continue;
                    if (left is { } x && c.Pos - x >= 5 && !SplitBetween(x, c.Pos, bottom.Pos, top.Pos))
                        Boxes.Add(new RectD(x, bottom.Pos, c.Pos, top.Pos));
                    left = c.Pos;
                }
            }
        }
    }

    /// <summary>A horizontal line strictly between the two rows across most of the stretch: the cell is smaller.</summary>
    private bool SplitBetween(double x0, double x1, double y0, double y1)
    {
        foreach (var s in Horizontal)
            if (s.Pos > y0 + 1 && s.Pos < y1 - 1 && Math.Min(s.To, x1) - Math.Max(s.From, x0) > 0.6 * (x1 - x0))
                return true;
        return false;
    }

    /// <summary>The same rectangle found twice (drawn and as a cell) only once.</summary>
    private void Dedupe()
    {
        var seen = new HashSet<(long, long, long, long)>();
        var unique = new List<RectD>(Boxes.Count);
        foreach (var r in Boxes)
            if (seen.Add(((long)Math.Round(r.Left), (long)Math.Round(r.Top), (long)Math.Round(r.Right), (long)Math.Round(r.Bottom)))) unique.Add(r);
        Boxes.Clear();
        Boxes.AddRange(unique);
    }
}
