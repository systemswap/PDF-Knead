namespace PdfKnead.Core.Scanning.Processing;

/// <summary>
/// „Buchseite entzerren“ without AI (KONZEPT §5.25, „Verarbeitung je Seite“ 3.): on a flatbed the page rises from the glass towards the
/// spine — the lines bend (with optics: more the farther from the optical axis), the print is compressed towards the spine and gets darker.
/// The lines of print give the model; pages without enough lines stay as they are (or are only turned when their skew is sure).
/// </summary>
/// <remarks>
/// <para>Lines: in about 20 vertical strips the centres of the lines of print are found (peaks of the projection of dark samples, the line
/// spacing from the autocorrelation of the whole page), then joined from strip to strip into lines. The flat side of the page is the one
/// whose straight part explains the middle of the lines better; the spine lies on the other side.</para>
/// <para>Model: per strip the vertical shift of each line against its height on the flat side, fitted as a quadratic in the height
/// (so lines far from the axis may bend more), smoothed across the strips. Its slope is the scale of the lifted part as seen through the
/// lens; with an assumed lens distance of 350 mm it gives the height over the glass, and the arc length of that height gives the stretch
/// across the spine — vertical and horizontal correction come from the same model. The darkening towards the spine is taken out with the
/// paper of each column.</para>
/// <para>Checked before use: at least six long lines, lines really bent, and the model must explain them (the remaining deviation at most a
/// third of the bending) — otherwise the page stays.</para>
/// <para>A double page (dark spine in the middle third) is corrected as two halves.</para>
/// </remarks>
public sealed class BookStraightening : IStraightener
{
    /// <inheritdoc/>
    public string Key => Straighteners.Book;

    /// <inheritdoc/>
    public string DisplayName => Tr.T("Buchseite entzerren");

    // the assumed distance of the lens from the glass (flatbed scanners with optics: about 300 to 400 mm)
    private const double LensMm = 350;
    private const int Strips = 20;
    private const int MinLines = 6;

    /// <inheritdoc/>
    public StraightenPlan? Find(ScannedImage page, CancellationToken cancel = default) => Find(page, cancel, null);

    /// <summary>Where the spine of a double page is, as a share of the width (a narrow dark valley without print); null for a single page.</summary>
    /// <param name="page">The page (a reduced copy is enough).</param>
    /// <returns>The share, or null.</returns>
    public static double? FindSpine(ScannedImage page)
    {
        var work = Work.From(page);
        return work.MiddleSpine() is { } at ? at / (double)work.Width : null;
    }

    /// <summary>As <see cref="Find(ScannedImage, CancellationToken)"/>, telling why a page stays.</summary>
    internal StraightenPlan? Find(ScannedImage page, CancellationToken cancel, Action<string>? log)
    {
        // first turned by a sure skew (a real turn, not a shear), then only the bending that remains is corrected
        var skew = new SkewStraightening().Find(page, cancel);
        var turned = skew?.Apply(page, cancel) ?? page;
        var book = FindBook(turned, cancel, log);
        return book is null ? skew : skew is null ? book : new CombinedPlan(skew, book);
    }

    private static StraightenPlan? FindBook(ScannedImage page, CancellationToken cancel, Action<string>? log)
    {
        var work = Work.From(page);
        cancel.ThrowIfCancellationRequested();
        // a double page: a clearly darker band in the middle third of the paper
        var spine = work.MiddleSpine();
        log?.Invoke(spine is { } at ? $"Doppelseite, Bund bei {at} von {work.Width}" : "keine Doppelseite");
        if (spine is { } middle)
        {
            var left = FindHalf(work.Part(0, middle), spineRight: true, cancel, log);
            var right = FindHalf(work.Part(middle, work.Width), spineRight: false, cancel, log);
            return left is null && right is null ? null : new DoublePagePlan(middle / (double)work.Width, left, right);
        }
        return FindHalf(work, spineRight: null, cancel, log) ?? Brighten(work, log);
    }

    /// <summary>
    /// A page without bent lines but with a dark spine at one edge (a contact sensor sees no perspective, only the darkening): only the
    /// spine is lightened. Null when no edge is clearly darker.
    /// </summary>
    private static BookPlan? Brighten(Work work, Action<string>? log)
    {
        var sorted = work.PaperColumns.Order().ToList();
        var paper = sorted[(int)(sorted.Count * 0.9)];
        var edge = Math.Max(1, work.Width / 20);
        var (left, right) = (work.PaperColumns.Take(edge).Average(), work.PaperColumns.TakeLast(edge).Average());
        var darker = Math.Min(left, right);
        if (darker > paper * 0.9f) return null;
        log?.Invoke($"nur Bund aufhellen ({(right < left ? "rechts" : "links")}, {darker / paper:P0} des Papiers)");
        return BookPlan.LightenOnly(work);
    }

    /// <summary>The model of one page; null when the page shows no sure bending.</summary>
    /// <param name="work">The page.</param>
    /// <param name="spineRight">Where the spine is (a half of a double page); null: found from the lines.</param>
    /// <param name="log">Receives why a page stays (diagnosis).</param>
    internal static BookPlan? FindHalf(Work work, bool? spineRight, CancellationToken cancel, Action<string>? log = null)
    {
        var lines = work.Lines(cancel, null, log);
        if (lines.Count < MinLines)
        {
            log?.Invoke($"zu wenige Zeilen: {lines.Count}");
            return null;
        }
        var (x0, x1) = (work.TextLeft, work.TextRight);
        var stripX = Enumerable.Range(0, Strips).Select(s => x0 + (s + 0.5) * (x1 - x0) / Strips).ToArray();

        // the flat side: a straight line through the outer 35 % of a side explains the middle third better there
        double Deviation(bool fromLeft)
        {
            double sum = 0;
            var n = 0;
            foreach (var line in lines)
            {
                var outer = line.Where(p => fromLeft ? p.Key < Strips * 0.35 : p.Key >= Strips * 0.65).ToList();
                var middle = line.Where(p => p.Key >= Strips * 0.35 && p.Key < Strips * 0.65).ToList();
                if (outer.Count < 3 || middle.Count < 2) continue;
                var (k, d) = LineFit(outer.Select(p => (stripX[p.Key], p.Value)));
                foreach (var p in middle)
                {
                    sum += Math.Abs(p.Value - (k * stripX[p.Key] + d));
                    n++;
                }
            }
            return n == 0 ? double.MaxValue : sum / n;
        }
        var (fromLeft, fromRight) = (Deviation(true), Deviation(false));
        var spineOnRight = spineRight ?? fromLeft <= fromRight;

        // second pass: the three strips at the spine split into three each — the bending is steepest on the last millimetres
        var edges = new List<double>();
        for (var e = 0; e <= Strips; e++)
        {
            var x = x0 + e * (x1 - x0) / (double)Strips;
            var nearSpine = spineOnRight ? e > Strips - 3 : e < 3;
            if (nearSpine && e > 0 && edges.Count > 0)
            {
                var previous = edges[^1];
                edges.Add(previous + (x - previous) / 3);
                edges.Add(previous + (x - previous) * 2 / 3);
            }
            edges.Add(x);
        }
        lines = work.Lines(cancel, [.. edges]);
        var count = edges.Count - 1;
        stripX = [.. Enumerable.Range(0, count).Select(i => (edges[i] + edges[i + 1]) / 2)];
        if (lines.Count < MinLines)
        {
            log?.Invoke($"zu wenige Zeilen im zweiten Durchgang: {lines.Count}");
            return null;
        }
        // the reference strip on the flat side, 15 % into the print
        var referenceX = spineOnRight ? x0 + 0.15 * (x1 - x0) : x1 - 0.15 * (x1 - x0);
        var reference = Enumerable.Range(0, count).MinBy(i => Math.Abs(stripX[i] - referenceX));

        // per line: height at the reference, shift in every strip (lines without a point at the reference are left out)
        var samples = new List<(double YRef, double[] Shift)>();
        foreach (var line in lines)
        {
            var points = Fill(line, count);
            if (points is null || double.IsNaN(points[reference])) continue;
            samples.Add((points[reference], [.. points.Select(y => double.IsNaN(y) ? double.NaN : y - points[reference])]));
        }
        if (samples.Count < MinLines)
        {
            log?.Invoke($"zu wenige Zeilen an der Bezugsstelle: {samples.Count}");
            return null;
        }
        cancel.ThrowIfCancellationRequested();

        // the bending: how far the lines leave their height at the reference, on the half towards the spine (in work pixels)
        var spineHalf = Enumerable.Range(0, count).Where(i => spineOnRight ? stripX[i] > (x0 + x1) / 2.0 : stripX[i] < (x0 + x1) / 2.0).ToHashSet();
        var bending = Math.Sqrt(samples.SelectMany(s => s.Shift.Where((d, i) => spineHalf.Contains(i) && !double.IsNaN(d))).DefaultIfEmpty(0).Average(d => d * d));
        if (bending < 2)
        {
            log?.Invoke($"kaum gebogen: {bending:0.00} px");
            return null;
        }

        // per strip a field of the shift over the height: the shifts of the lines sorted by their height, each smoothed with its neighbours
        // (a median of three: a line joined wrongly does not pull the field), linear between the lines; above the first and below the last
        // line it keeps the outermost value. Then smoothed across the strips.
        var h = (double)work.Height;
        var field = new double[count][];
        double rawResidual = 0;
        var rawCount = 0;
        for (var s = 0; s < count; s++)
        {
            var points = samples.Where(p => !double.IsNaN(p.Shift[s])).Select(p => (Y: p.YRef, D: p.Shift[s])).OrderBy(p => p.Y).ToList();
            var column = new double[BookPlan.Rows];
            if (points.Count >= 2)
            {
                var smoothed = points.Select((p, i) =>
                {
                    var near = points.Skip(Math.Max(0, i - 1)).Take(i == 0 ? 2 : 3).Select(q => q.D).Order().ToList();
                    return (p.Y, D: near[near.Count / 2]);
                }).ToList();
                for (var i = 0; i < points.Count; i++)
                {
                    rawResidual += (points[i].D - smoothed[i].D) * (points[i].D - smoothed[i].D);
                    rawCount++;
                }
                for (var r = 0; r < BookPlan.Rows; r++)
                {
                    var y = r / (BookPlan.Rows - 1.0) * (h - 1);
                    var k = smoothed.FindLastIndex(p => p.Y <= y);
                    column[r] = (k < 0 ? smoothed[0].D : k >= smoothed.Count - 1 ? smoothed[^1].D
                        : smoothed[k].D + (smoothed[k + 1].D - smoothed[k].D) * (y - smoothed[k].Y) / Math.Max(1e-9, smoothed[k + 1].Y - smoothed[k].Y)) / h;
                }
            }
            field[s] = column;
        }
        for (var pass = 0; pass < 2; pass++)
        {
            var smooth = new double[count][];
            for (var s = 0; s < count; s++)
            {
                smooth[s] = new double[BookPlan.Rows];
                for (var r = 0; r < BookPlan.Rows; r++)
                {
                    var (sum, n) = (0.0, 0);
                    for (var t = Math.Max(0, s - 1); t <= Math.Min(count - 1, s + 1); t++)
                        for (var q = Math.Max(0, r - 1); q <= Math.Min(BookPlan.Rows - 1, r + 1); q++)
                        {
                            sum += field[t][q];
                            n++;
                        }
                    smooth[s][r] = sum / n;
                }
            }
            field = smooth;
        }
        // the flat side is the reference: no shift there
        var atReference = field[reference];
        for (var s = 0; s < count; s++)
            for (var r = 0; r < BookPlan.Rows; r++) field[s][r] -= atReference[r];

        // checks: the lines must agree with their neighbours (a wrongly joined line shows as noise), and the field must not fold
        rawResidual = Math.Sqrt(rawResidual / Math.Max(1, rawCount));
        var steepest = 0.0;
        for (var s = 0; s < count; s++)
            for (var r = 1; r < BookPlan.Rows; r++)
                steepest = Math.Max(steepest, Math.Abs(field[s][r] - field[s][r - 1]) * (BookPlan.Rows - 1));
        log?.Invoke($"Seite {(spineOnRight ? "Bund rechts" : "Bund links")}, Zeilen {samples.Count}, Biegung {bending:0.00} px, Unruhe {rawResidual:0.00} px, steilste Stelle {steepest:0.000}");
        if (rawResidual > bending / 2 || steepest > 0.3) return null;

        return BookPlan.Build(work, stripX, field, spineOnRight, LensMm / 25.4 * work.Dpi);
    }

    /// <summary>The heights of a line in all strips (NaN where it has no point), with single gaps filled; null if it is too short.</summary>
    private static double[]? Fill(Dictionary<int, double> line, int count)
    {
        var y = Enumerable.Repeat(double.NaN, count).ToArray();
        foreach (var (s, v) in line) y[s] = v;
        for (var s = 1; s < count - 1; s++)
            if (double.IsNaN(y[s]) && !double.IsNaN(y[s - 1]) && !double.IsNaN(y[s + 1])) y[s] = (y[s - 1] + y[s + 1]) / 2;
        return y.Count(v => !double.IsNaN(v)) >= count * 0.6 ? y : null;
    }

    private static double[] Smooth(double[] v)
    {
        var r = new double[v.Length];
        for (var i = 0; i < v.Length; i++)
        {
            var (sum, n) = (0.0, 0);
            for (var k = Math.Max(0, i - 1); k <= Math.Min(v.Length - 1, i + 1); k++)
            {
                sum += v[k];
                n++;
            }
            r[i] = sum / n;
        }
        return r;
    }

    /// <summary>Least squares y = k·x + d.</summary>
    internal static (double K, double D) LineFit(IEnumerable<(double X, double Y)> points)
    {
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        var n = 0;
        foreach (var (x, y) in points)
        {
            (sx, sy, sxx, sxy) = (sx + x, sy + y, sxx + x * x, sxy + x * y);
            n++;
        }
        var den = n * sxx - sx * sx;
        if (n == 0) return (0, 0);
        if (Math.Abs(den) < 1e-12) return (0, sy / n);
        var k = (n * sxy - sx * sy) / den;
        return (k, (sy - k * sx) / n);
    }

    /// <summary>Least squares y = a + b·x + c·x².</summary>
    internal static (double A, double B, double C) QuadraticFit(IReadOnlyList<(double X, double Y)> points)
    {
        double s0 = points.Count, s1 = 0, s2 = 0, s3 = 0, s4 = 0, t0 = 0, t1 = 0, t2 = 0;
        foreach (var (x, y) in points)
        {
            var x2 = x * x;
            (s1, s2, s3, s4) = (s1 + x, s2 + x2, s3 + x2 * x, s4 + x2 * x2);
            (t0, t1, t2) = (t0 + y, t1 + x * y, t2 + x2 * y);
        }
        // solve the normal equations (Cramer's rule)
        double Det(double a1, double a2, double a3, double b1, double b2, double b3, double c1, double c2, double c3) =>
            a1 * (b2 * c3 - b3 * c2) - a2 * (b1 * c3 - b3 * c1) + a3 * (b1 * c2 - b2 * c1);
        var d = Det(s0, s1, s2, s1, s2, s3, s2, s3, s4);
        if (Math.Abs(d) < 1e-18) return (LineFit(points).D, LineFit(points).K, 0);
        return (Det(t0, s1, s2, t1, s2, s3, t2, s3, s4) / d, Det(s0, t0, s2, s1, t1, s3, s2, t2, s4) / d, Det(s0, s1, t0, s1, s2, t1, s2, s3, t2) / d);
    }

    /// <summary>The page in grey at about 150 dpi, with its paper and dark samples — where the lines and the spine are found.</summary>
    internal sealed class Work
    {
        public required byte[] Grey { get; init; }
        public required bool[] Dark { get; init; }
        public required float[] PaperColumns { get; init; }   // the paper of each column (median over the rows)
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int Dpi { get; init; }

        /// <summary>The left and right edge of the print (5 % margin inside the first and last columns with print).</summary>
        public int TextLeft { get; private set; }

        public int TextRight { get; private set; }

        public static Work From(ScannedImage page)
        {
            var factor = Math.Max(1, (int)Math.Round(page.Dpi / 150.0));
            var (w, h, c) = (Math.Max(1, page.Width / factor), Math.Max(1, page.Height / factor), page.Channels);
            var grey = new byte[w * h];
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    var sum = 0;
                    for (var dy = 0; dy < factor; dy++)
                        for (var dx = 0; dx < factor; dx++)
                        {
                            var at = ((long)(y * factor + dy) * page.Width + x * factor + dx) * c;
                            sum += c == 1 ? page.Pixels[at] : (page.Pixels[at] * 77 + page.Pixels[at + 1] * 150 + page.Pixels[at + 2] * 29) >> 8;
                        }
                    grey[y * w + x] = (byte)(sum / (factor * factor));
                }
            });
            return Make(grey, w, h, Math.Max(1, page.Dpi / factor));
        }

        private static Work Make(byte[] grey, int w, int h, int dpi)
        {
            var paper = PaperBackground.Estimate(new ScannedImage(w, h, 1, dpi, grey));
            var dark = new bool[w * h];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++) dark[y * w + x] = grey[y * w + x] < paper.At(x, y, 0) * 0.6f;
            // the paper of each column measured directly (the brightness 90 % of its samples reach or stay below), smoothed over a few
            // columns: the paper map counts very dark areas as „no paper“ (photos) and would not show the shadow of the spine
            var raw = new float[w];
            var histogram = new int[256];
            for (var x = 0; x < w; x++)
            {
                Array.Clear(histogram);
                var n = 0;
                for (var y = 0; y < h; y += 2)
                {
                    histogram[grey[y * w + x]]++;
                    n++;
                }
                var level = 255;
                for (var seen = 0; level > 0; level--)
                {
                    seen += histogram[level];
                    if (seen >= n / 10) break;
                }
                raw[x] = level;
            }
            var columns = new float[w];
            for (var x = 0; x < w; x++)
            {
                var (sum, count) = (0f, 0);
                for (var k = Math.Max(0, x - 3); k <= Math.Min(w - 1, x + 3); k++)
                {
                    sum += raw[k];
                    count++;
                }
                columns[x] = sum / count;
            }
            var work = new Work { Grey = grey, Dark = dark, PaperColumns = columns, Width = w, Height = h, Dpi = dpi };
            work.FindTextEdges();
            return work;
        }

        /// <summary>A vertical part of the page (a half of a double page).</summary>
        public Work Part(int from, int to)
        {
            var w = to - from;
            var grey = new byte[w * Height];
            for (var y = 0; y < Height; y++) Array.Copy(Grey, y * Width + from, grey, y * w, w);
            return Make(grey, w, Height, Dpi);
        }

        private void FindTextEdges()
        {
            var counts = new int[Width];
            for (var y = (int)(Height * 0.05); y < Height * 0.95; y++)
                for (var x = 0; x < Width; x++)
                    if (Dark[y * Width + x]) counts[x]++;
            var threshold = Math.Max(2, counts.Max() / 20);
            var first = Array.FindIndex(counts, v => v >= threshold);
            var last = Array.FindLastIndex(counts, v => v >= threshold);
            if (first < 0 || last <= first) (first, last) = (0, Width - 1);
            TextLeft = first;
            TextRight = last;
        }

        /// <summary>
        /// The column of a dark spine in the middle third (a double page), or null: a narrow valley of the paper (clearly darker than the
        /// paper a tenth of the width to both sides) with hardly any print in it — a single page has its print running through the middle.
        /// </summary>
        public int? MiddleSpine()
        {
            var (from, to) = ((int)(Width * 0.35), (int)(Width * 0.65));
            var min = from;
            for (var x = from; x < to; x++) if (PaperColumns[x] < PaperColumns[min]) min = x;
            var reach = Width / 10;
            var (left, right) = (PaperColumns[Math.Max(0, min - reach)], PaperColumns[Math.Min(Width - 1, min + reach)]);
            if (PaperColumns[min] > Math.Min(left, right) * 0.85f) return null;
            // print in a band of 2 % of the width around the valley, against the print in the columns of text
            var counts = new int[Width];
            for (var y = 0; y < Height; y += 2)
                for (var x = 0; x < Width; x++)
                    if (Dark[y * Width + x]) counts[x]++;
            var text = counts.Where(c => c > 0).Order().ToList();
            if (text.Count == 0) return min;
            var typical = text[text.Count / 2];
            var band = Width / 50;
            var inBand = counts.Skip(Math.Max(0, min - band)).Take(2 * band + 1).Average();
            return inBand < typical * 0.15 ? min : null;
        }

        /// <summary>The lines of print: per line the height of its centre in the strips it crosses (strip → y).</summary>
        /// <param name="cancel">Stops the search.</param>
        /// <param name="edges">The edges of the strips; null: <see cref="Strips"/> strips of equal width over the print.</param>
        public List<Dictionary<int, double>> Lines(CancellationToken cancel, double[]? edges, Action<string>? log = null)
        {
            var (x0, x1) = (TextLeft, TextRight);
            if (x1 - x0 < Strips * 4) return [];
            edges ??= [.. Enumerable.Range(0, Strips + 1).Select(e => x0 + e * (x1 - x0) / (double)Strips)];
            var count = edges.Length - 1;
            var centres = Enumerable.Range(0, count).Select(i => (edges[i] + edges[i + 1]) / 2).ToArray();
            var profiles = new double[count][];
            var total = new double[Height];
            for (var s = 0; s < count; s++)
            {
                var p = new double[Height];
                var (a, b) = ((int)edges[s], Math.Max((int)edges[s] + 1, (int)edges[s + 1]));
                for (var y = 0; y < Height; y++)
                {
                    var n = 0;
                    for (var x = a; x < b; x++) if (Dark[y * Width + x]) n++;
                    p[y] = n;
                    total[y] += n;
                }
                profiles[s] = p;
            }
            cancel.ThrowIfCancellationRequested();
            // a rule or a black bar fills a row completely: capped at the level of ordinary lines, it no longer hides them
            Cap(total);
            foreach (var p in profiles) Cap(p);
            var spacing = LineSpacing(profiles, Height, log);
            if (spacing < 6) return [];
            var radius = Math.Max(1, (int)(spacing / 6));
            var peaks = profiles.Select(p => Peaks(Box(p, radius), spacing)).ToArray();
            log?.Invoke($"Zeilenabstand {spacing}, Spitzen je Streifen {string.Join(",", peaks.Select(p => p.Count))}");

            // join into pieces of lines: from every peak not yet used (strips with many peaks first), outwards strip by strip, the nearest
            // unused peak where the piece is expected; then pieces on both sides of a gap (dotted leaders, a picture, short lines) are
            // joined when the one, continued straight, meets the other
            var used = Enumerable.Range(0, count).Select(_ => new HashSet<int>()).ToArray();
            var pieces = new List<Dictionary<int, double>>();
            foreach (var s0 in Enumerable.Range(0, count).OrderByDescending(i => peaks[i].Count))
                for (var i0 = 0; i0 < peaks[s0].Count; i0++)
                {
                    if (used[s0].Contains(i0)) continue;
                    used[s0].Add(i0);
                    var piece = new Dictionary<int, double> { [s0] = peaks[s0][i0] };
                    foreach (var direction in new[] { 1, -1 })
                        for (var s = s0 + direction; s >= 0 && s < count; s += direction)
                        {
                            var known = piece.Keys.OrderBy(k => Math.Abs(k - s)).Take(4).ToList();
                            if (Math.Abs(known[0] - s) > 2) break;   // a gap: the piece ends here, joining comes later
                            var predicted = piece[known[0]];
                            if (known.Count >= 2)
                                predicted += Math.Clamp(LineFit(known.Select(k => (centres[k], piece[k]))).K, -0.4, 0.4) * (centres[s] - centres[known[0]]);
                            var best = -1;
                            for (var i = 0; i < peaks[s].Count; i++)
                                if (!used[s].Contains(i) && Math.Abs(peaks[s][i] - predicted) < spacing / 3
                                    && (best < 0 || Math.Abs(peaks[s][i] - predicted) < Math.Abs(peaks[s][best] - predicted)))
                                    best = i;
                            if (best < 0) continue;
                            used[s].Add(best);
                            piece[s] = peaks[s][best];
                        }
                    if (piece.Count >= 3) pieces.Add(piece);
                }
            cancel.ThrowIfCancellationRequested();
            // join pieces: the right end of one continued straight to the left end of another, without overlap, closest first
            var joined = true;
            while (joined)
            {
                joined = false;
                var best = (A: -1, B: -1, Diff: double.MaxValue);
                for (var i = 0; i < pieces.Count; i++)
                    for (var j = 0; j < pieces.Count; j++)
                    {
                        if (i == j) continue;
                        var (left, right) = (pieces[i], pieces[j]);
                        var (end, begin) = (left.Keys.Max(), right.Keys.Min());
                        if (begin <= end) continue;
                        var tail = left.Keys.OrderByDescending(k => k).Take(4).ToList();
                        var head = right.Keys.Order().Take(4).ToList();
                        var (k1, d1) = LineFit(tail.Select(k => (centres[k], left[k])));
                        var (k2, d2) = LineFit(head.Select(k => (centres[k], right[k])));
                        // both continued to the middle of the gap
                        var middle = (centres[end] + centres[begin]) / 2;
                        var diff = Math.Abs(k1 * middle + d1 - (k2 * middle + d2));
                        if (diff < spacing / 4 && diff < best.Diff) best = (i, j, diff);
                    }
                if (best.A < 0) break;
                foreach (var (k, v) in pieces[best.B]) pieces[best.A][k] = v;
                pieces.RemoveAt(best.B);
                joined = true;
            }
            var lines = pieces;
            log?.Invoke($"Zeilen vor der Auswahl: {lines.Count}, Punkte {string.Join(",", lines.Select(l => l.Count).Order().TakeLast(12))}");
            return [.. lines.Where(l => l.Count >= count * 0.6)];
        }

        /// <summary>Caps a profile at its 90th percentile of the rows with print (rules and bars above it count as ordinary lines).</summary>
        private static void Cap(double[] profile)
        {
            var filled = profile.Where(v => v > 0).Order().ToList();
            if (filled.Count < 10) return;
            var cap = filled[(int)(filled.Count * 0.9)];
            for (var i = 0; i < profile.Length; i++) profile[i] = Math.Min(profile[i], cap);
        }

        /// <summary>
        /// The line spacing: the smallest lag at which the autocorrelation has a peak of at least half the strongest peak (the strongest may
        /// lie at a multiple of the spacing). The autocorrelation is summed over the strips — within a strip the lines are nearly straight,
        /// the projection of the whole page is smeared by bent lines and loses its first peak.
        /// </summary>
        private static double LineSpacing(double[][] profiles, int height, Action<string>? log = null)
        {
            var maxLag = Math.Min(160, height / 3);
            if (maxLag <= 7) return 0;
            var correlation = new double[maxLag + 2];
            foreach (var profile in profiles)
            {
                var mean = profile.Average();
                for (var lag = 0; lag < correlation.Length; lag++)
                {
                    double sum = 0;
                    for (var i = 0; i + lag < profile.Length; i++) sum += (profile[i] - mean) * (profile[i + lag] - mean);
                    correlation[lag] += sum;
                }
            }
            if (correlation[0] <= 0) return 0;
            var peaks = new List<(int Lag, double Value)>();
            for (var lag = 6; lag <= maxLag; lag++)
                if (correlation[lag] > correlation[lag - 1] && correlation[lag] >= correlation[lag + 1] && correlation[lag] > correlation[0] * 0.1)
                    peaks.Add((lag, correlation[lag]));
            log?.Invoke("Autokorrelation: " + string.Join(" ", peaks.Take(8).Select(p => $"{p.Lag}:{p.Value / correlation[0]:0.00}")));
            if (peaks.Count == 0) return 0;
            var strongest = peaks.Max(p => p.Value);
            return peaks.First(p => p.Value >= strongest * 0.5).Lag;
        }

        private static double[] Box(double[] p, int r)
        {
            var result = new double[p.Length];
            for (var i = 0; i < p.Length; i++)
            {
                var (sum, n) = (0.0, 0);
                for (var k = Math.Max(0, i - r); k <= Math.Min(p.Length - 1, i + r); k++)
                {
                    sum += p[k];
                    n++;
                }
                result[i] = sum / n;
            }
            return result;
        }

        /// <summary>The peaks of a strip profile: at least a quarter of its largest value, at least 60 % of the spacing apart.</summary>
        private static List<double> Peaks(double[] p, double spacing)
        {
            var max = p.Max();
            if (max <= 0) return [];
            var candidates = new List<(int Y, double V)>();
            for (var y = 1; y < p.Length - 1; y++)
                if (p[y] >= max * 0.25 && p[y] >= p[y - 1] && p[y] > p[y + 1]) candidates.Add((y, p[y]));
            var chosen = new List<int>();
            foreach (var (y, _) in candidates.OrderByDescending(c => c.V))
                if (chosen.All(c => Math.Abs(c - y) >= spacing * 0.6)) chosen.Add(y);
            chosen.Sort();
            // a sub-pixel centre: the parabola through the peak and its neighbours
            return [.. chosen.Select(y =>
            {
                var (l, m, r) = (p[y - 1], p[y], p[y + 1]);
                var den = l - 2 * m + r;
                return den < 0 ? y + 0.5 * (l - r) / den : y;
            })];
        }
    }
}

/// <summary>
/// The correction of one book page, scale-free (found on a reduced copy, applied to any resolution): per output column the source column
/// (the stretch across the spine), the vertical shift as a quadratic in the height, and the gain against the darkening.
/// </summary>
public sealed class BookPlan : StraightenPlan
{
    /// <summary>The rows of the shift field over the height.</summary>
    internal const int Rows = 48;

    private readonly double[] _sourceX;     // per output column (share of the output width): source column as share of the input width
    private readonly double[][] _shift;     // per output column: the vertical shift over the height (shares of the height, Rows values)
    private readonly double[] _gain;        // per output column: brightness gain
    private readonly double _widthRatio;    // output width / input width

    private BookPlan(double[] sourceX, double[][] shift, double[] gain, double widthRatio)
    {
        (_sourceX, _shift, _gain, _widthRatio) = (sourceX, shift, gain, widthRatio);
    }

    /// <inheritdoc/>
    public override double Degrees => 0;

    /// <summary>The output width as a share of the input width (above 1: stretched across the spine).</summary>
    public double WidthRatio => _widthRatio;

    /// <summary>Builds the plan from the shift field of the strips.</summary>
    /// <param name="work">The page.</param>
    /// <param name="stripX">The middle column of each strip.</param>
    /// <param name="field">Per strip the shift over the height (shares of the height).</param>
    /// <param name="spineRight">Whether the spine is on the right.</param>
    /// <param name="lensPx">The assumed distance of the lens in work pixels.</param>
    internal static BookPlan Build(BookStraightening.Work work, double[] stripX, double[][] field, bool spineRight, double lensPx)
    {
        const int samples = 256;
        var w = work.Width;
        var strips = stripX.Length;
        // the field per input column: linear between the strips; beyond the print constant on the flat side, continued on the spine side
        double[] At(double x)
        {
            var result = new double[Rows];
            int i;
            double t;
            if (x <= stripX[0]) (i, t) = (0, spineRight ? 0 : (x - stripX[0]) / (stripX[1] - stripX[0]));
            else if (x >= stripX[^1]) (i, t) = (strips - 2, !spineRight ? 1 : 1 + (x - stripX[^1]) / (stripX[^1] - stripX[^2]));
            else
            {
                i = Array.FindLastIndex(stripX, s => s <= x);
                t = (x - stripX[i]) / (stripX[i + 1] - stripX[i]);
            }
            for (var r = 0; r < Rows; r++) result[r] = field[i][r] + (field[i + 1][r] - field[i][r]) * t;
            return result;
        }
        // the scale of the lifted part through the lens (the mean slope of the shift over the height) gives the height over the glass:
        // z = f · (1/S − 1); its arc length is the paper position of each column
        var z = new double[w];
        for (var x = 0; x < w; x++)
        {
            var column = At(x);
            var scale = 1 + Math.Clamp(column[^1] - column[0], -0.25, 0.25);
            z[x] = Math.Max(0, lensPx * (1 / scale - 1));
        }
        var arc = new double[w + 1];
        for (var i = 0; i < w; i++)
        {
            var x = spineRight ? i : w - 1 - i;
            var previous = spineRight ? Math.Max(0, x - 1) : Math.Min(w - 1, x + 1);
            var slope = z[x] - z[previous];
            arc[i + 1] = arc[i] + Math.Sqrt(1 + slope * slope);
        }
        var length = arc[w];
        var sorted = work.PaperColumns.Order().ToList();
        var paper = sorted[(int)(sorted.Count * 0.9)];
        var (sx, shift, gain) = (new double[samples], new double[samples][], new double[samples]);
        for (var k = 0; k < samples; k++)
        {
            var target = k / (samples - 1.0) * length;   // along the paper from the flat side
            var i = Math.Clamp(Array.BinarySearch(arc, target) is var found && found >= 0 ? found : ~found, 1, w);
            var t = (target - arc[i - 1]) / Math.Max(1e-9, arc[i] - arc[i - 1]);
            var fromFlat = i - 1 + t;
            var x = spineRight ? fromFlat : w - 1 - fromFlat;
            var column = spineRight ? k : samples - 1 - k;
            sx[column] = Math.Clamp(x / Math.Max(1, w - 1), 0, 1);
            shift[column] = At(x);
            var colPaper = work.PaperColumns[Math.Clamp((int)Math.Round(x), 0, w - 1)];
            gain[column] = colPaper < paper * 0.97f ? Math.Min(2, paper / Math.Max(1, colPaper)) : 1;
        }
        return new BookPlan(sx, shift, gain, length / w);
    }

    /// <summary>A plan that keeps the geometry and only lightens the darker columns towards the paper (the spine of a contact sensor scan).</summary>
    internal static BookPlan LightenOnly(BookStraightening.Work work)
    {
        const int samples = 256;
        var sorted = work.PaperColumns.Order().ToList();
        var paper = sorted[(int)(sorted.Count * 0.9)];
        var (sx, shift, gain) = (new double[samples], new double[samples][], new double[samples]);
        for (var k = 0; k < samples; k++)
        {
            sx[k] = k / (samples - 1.0);
            shift[k] = new double[Rows];
            var colPaper = work.PaperColumns[Math.Clamp((int)Math.Round(sx[k] * (work.Width - 1)), 0, work.Width - 1)];
            gain[k] = colPaper < paper * 0.97f ? Math.Min(2, paper / Math.Max(1, colPaper)) : 1;
        }
        return new BookPlan(sx, shift, gain, 1);
    }

    /// <inheritdoc/>
    public override ScannedImage Apply(ScannedImage page, CancellationToken cancel = default)
    {
        var (w, h, ch) = (page.Width, page.Height, page.Channels);
        var outW = Math.Max(1, (int)Math.Round(w * _widthRatio));
        var result = new byte[(long)outW * h * ch];
        var source = page.Pixels;
        var n = _sourceX.Length;
        Parallel.For(0, outW, new ParallelOptions { CancellationToken = cancel }, ox =>
        {
            var f = ox / Math.Max(1.0, outW - 1) * (n - 1);
            var k = Math.Min((int)f, n - 2);
            var t = f - k;
            var x = (_sourceX[k] + (_sourceX[k + 1] - _sourceX[k]) * t) * (w - 1);
            var gain = _gain[k] + (_gain[k + 1] - _gain[k]) * t;
            var (s0, s1) = (_shift[k], _shift[k + 1]);
            var (x0, fx) = ((int)Math.Floor(x), x - Math.Floor(x));
            var x1 = Math.Min(x0 + 1, w - 1);
            for (var oy = 0; oy < h; oy++)
            {
                var rf = oy / Math.Max(1.0, h - 1) * (Rows - 1);
                var r = Math.Min((int)rf, Rows - 2);
                var rt = rf - r;
                var shift = (s0[r] + (s0[r + 1] - s0[r]) * rt) * (1 - t) + (s1[r] + (s1[r + 1] - s1[r]) * rt) * t;
                var y = oy + shift * h;
                var at = ((long)oy * outW + ox) * ch;
                if (y < 0 || y > h - 1)
                {
                    for (var i = 0; i < ch; i++) result[at + i] = 255;
                    continue;
                }
                var (y0, fy) = ((int)y, y - (int)y);
                var y1 = Math.Min(y0 + 1, h - 1);
                for (var i = 0; i < ch; i++)
                {
                    double P(int px, int py) => source[((long)py * w + px) * ch + i];
                    var v = (P(x0, y0) * (1 - fx) + P(x1, y0) * fx) * (1 - fy) + (P(x0, y1) * (1 - fx) + P(x1, y1) * fx) * fy;
                    result[at + i] = (byte)Math.Clamp(v * gain + 0.5, 0, 255);
                }
            }
        });
        return new ScannedImage(outW, h, ch, page.Dpi, result);
    }
}

/// <summary>Two plans one after the other (turned by the skew, then the bending corrected).</summary>
/// <param name="first">Applied first.</param>
/// <param name="second">Applied to the result of the first.</param>
public sealed class CombinedPlan(StraightenPlan first, StraightenPlan second) : StraightenPlan
{
    /// <inheritdoc/>
    public override double Degrees => first.Degrees;

    /// <summary>The second plan (the bending).</summary>
    public StraightenPlan Second => second;

    /// <inheritdoc/>
    public override ScannedImage Apply(ScannedImage page, CancellationToken cancel = default) => second.Apply(first.Apply(page, cancel), cancel);
}

/// <summary>A double page: both halves corrected on their own (the spine between them), side by side again.</summary>
/// <param name="split">Where the spine is, as a share of the width.</param>
/// <param name="left">The plan of the left half; null: stays.</param>
/// <param name="right">The plan of the right half; null: stays.</param>
public sealed class DoublePagePlan(double split, BookPlan? left, BookPlan? right) : StraightenPlan
{
    /// <summary>Where the spine is, as a share of the width.</summary>
    public double Split { get; } = split;

    /// <inheritdoc/>
    public override double Degrees => 0;

    /// <inheritdoc/>
    public override ScannedImage Apply(ScannedImage page, CancellationToken cancel = default)
    {
        var at = Math.Clamp((int)Math.Round(page.Width * Split), 1, page.Width - 1);
        var a = ImageGeometry.Crop(page, 0, 0, page.Width - at, 0);
        var b = ImageGeometry.Crop(page, at, 0, 0, 0);
        a = left?.Apply(a, cancel) ?? a;
        b = right?.Apply(b, cancel) ?? b;
        var (h, c) = (page.Height, page.Channels);
        var w = a.Width + b.Width;
        var pixels = new byte[(long)w * h * c];
        for (var y = 0; y < h; y++)
        {
            Array.Copy(a.Pixels, (long)y * a.Width * c, pixels, (long)y * w * c, (long)a.Width * c);
            Array.Copy(b.Pixels, (long)y * b.Width * c, pixels, ((long)y * w + a.Width) * c, (long)b.Width * c);
        }
        return new ScannedImage(w, h, c, page.Dpi, pixels);
    }
}
