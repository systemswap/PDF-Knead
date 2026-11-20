using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Pdf.Text;

/// <summary>
/// Settings of block detection. For measurements, individual steps and rules can be switched off (names in
/// <see cref="TextBlocks.Steps"/> and <see cref="TextBlocks.Rules"/>), so that the test set shows what each one contributes.
/// </summary>
public sealed record BlockOptions
{
    /// <summary>All steps and rules enabled.</summary>
    public static readonly BlockOptions Default = new();

    /// <summary>Names of the steps and rules that do not run (see <see cref="TextBlocks.Switches"/>).</summary>
    public IReadOnlySet<string> Without { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Whether a step or rule runs.</summary>
    /// <param name="name">The name of the step or rule.</param>
    /// <returns>True unless it is listed in <see cref="Without"/>.</returns>
    public bool Uses(string name) => !Without.Contains(name);
}

/// <summary>
/// Structure of block detection: one frame per writing direction, processed by a fixed sequence of steps — rows, columns from
/// white space, segments, page statistics, list markers, paragraphs (with named rules), blocks. Each step reads and extends the frame;
/// a better method (including a learned model) replaces one step without touching the others. Measured against a public
/// layout test collection (local measuring tool, docs/PRUEFUNGEN.md). See KONZEPT.md, section 5.14.
/// </summary>
public static partial class TextBlocks
{
    /// <summary>One step of detection; if <see cref="Optional"/>, it can be switched off for measurements.</summary>
    internal interface ILayoutStep
    {
        string Name { get; }

        bool Optional { get; }

        void Run(LayoutFrame frame);
    }

    /// <summary>The steps in their order.</summary>
    internal static readonly IReadOnlyList<ILayoutStep> Steps =
        [new RowStep(), new GutterStep(), new SegmentStep(), new StatisticsStep(), new MarkerStep(), new ParagraphStep()];

    /// <summary>Names of all steps and rules that can be switched off (for <see cref="BlockOptions.Without"/>).</summary>
    public static IEnumerable<string> Switches => Steps.Where(s => s.Optional).Select(s => s.Name).Concat(Rules.Select(r => r.Name));

    /// <summary>Everything the steps know about one writing direction (measures in the frame: x along the text, y across it, upwards).</summary>
    internal sealed class LayoutFrame(double angle, List<Placed> glyphs, BlockOptions options, CancellationToken cancel)
    {
        public double Angle { get; } = angle;

        public BlockOptions Options { get; } = options;

        public CancellationToken Cancel { get; } = cancel;

        /// <summary>All characters of this direction.</summary>
        public List<Placed> Glyphs { get; } = glyphs;

        /// <summary>Rows across the whole width (characters on one baseline, sorted by start), from top to bottom.</summary>
        public List<Row> Rows { get; } = [];

        /// <summary>Contiguous segments of the rows (split at columns and large gaps).</summary>
        public List<Segment> Segments { get; } = [];

        public LayoutStatistics Statistics { get; set; } = LayoutStatistics.Empty;

        /// <summary>Paragraphs: segments from top to bottom.</summary>
        public List<Paragraph> Paragraphs { get; } = [];
    }

    /// <summary>Glyph in the frame of a writing direction: along (start, end) and across (baseline, ascent/descent).</summary>
    internal sealed record Placed(Glyph Glyph, double Start, double End, double Baseline, double Top, double Bottom)
    {
        public double Size => Glyph.Format.Size;
    }

    /// <summary>A row across the whole width; <see cref="Cuts"/>: positions where a column splits it.</summary>
    internal sealed class Row(List<Placed> glyphs, double baseline, double size)
    {
        public readonly List<Placed> Glyphs = glyphs;
        public readonly double Baseline = baseline;
        public readonly double Size = size;
        public readonly List<double> Cuts = [];
    }

    /// <summary>A line of a paragraph being built (characters in reading order).</summary>
    internal sealed class Segment
    {
        public Segment(List<Placed> glyphs)
        {
            Glyphs = glyphs;
            Baseline = glyphs.Average(g => g.Baseline);
            Size = glyphs.Max(g => g.Size);
            Top = glyphs.Max(g => g.Top);
            Bottom = glyphs.Min(g => g.Bottom);
            (Look, UniformLook) = DominantLook(glyphs);
        }

        public readonly List<Placed> Glyphs;
        public double Start => Glyphs[0].Start;
        public double End => Glyphs[^1].End;
        public readonly double Baseline, Size, Top, Bottom;

        /// <summary>Look of most characters (heading in bold, a different font or colour).</summary>
        public readonly (bool Bold, bool Italic, string Family, int Color) Look;

        /// <summary>All characters look like this (a heading is set uniformly, "Name, 49" under a business card is not).</summary>
        public readonly bool UniformLook;

        /// <summary>List item: starts with a marker such as "•", "1.", "(a)" or "[12]" followed by a gap; then the start of the text.</summary>
        public double? BulletIndent;

        private static ((bool, bool, string, int), bool) DominantLook(List<Placed> glyphs)
        {
            var counts = new Dictionary<(bool, bool, string, int), int>();
            foreach (var g in glyphs)
                if (!string.IsNullOrWhiteSpace(g.Glyph.Text))
                {
                    var f = g.Glyph.Format;
                    var key = (f.Bold, f.Italic, f.Family, f.Color);
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            return counts.Count == 0 ? (default, true) : (counts.MaxBy(c => c.Value).Key, counts.Count == 1);
        }
    }

    /// <summary>A paragraph being built: its lines from top to bottom, plus left and right margin (kept up to date, not searched for each check).</summary>
    internal sealed class Paragraph : List<Segment>
    {
        public Paragraph(Segment first)
        {
            Add(first);
            Left = first.Start;
            Right = first.End;
        }

        public double Left { get; private set; }

        public double Right { get; private set; }

        public void Append(Segment line)
        {
            Add(line);
            Left = Math.Min(Left, line.Start);
            Right = Math.Max(Right, line.End);
        }
    }

    /// <summary>Builds blocks from the characters (<see cref="Collect"/>): per writing direction the <see cref="Steps"/>, then the blocks.</summary>
    /// <param name="glyphs">The characters of the page.</param>
    /// <param name="paths">Flat lines of the page that may be underlines or strikethroughs.</param>
    /// <param name="options">Steps and rules to leave out; null = all.</param>
    /// <param name="cancel">Cancels the detection.</param>
    /// <returns>The blocks, per direction in page order (top first); blocks consisting only of whitespace are left out.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was triggered.</exception>
    public static IReadOnlyList<PageTextBlock> Build(IReadOnlyList<Glyph> glyphs, IReadOnlyList<PathMark> paths, BlockOptions? options = null,
        CancellationToken cancel = default)
    {
        options ??= BlockOptions.Default;
        var blocks = new List<PageTextBlock>();
        // one frame per writing direction (rounded to one degree); the direction follows from the up vector (also for characters without an advance)
        foreach (var direction in glyphs.GroupBy(Direction))
        {
            cancel.ThrowIfCancellationRequested();
            var angle = (double)direction.Key;
            var toFrame = Matrix.Rotate(angle).Invert();
            var placed = new List<Placed>();
            foreach (var g in direction)
            {
                var (sx, sy) = toFrame.Transform(g.X, g.Y);
                var (ex, _) = toFrame.Transform(g.EndX, g.EndY);
                placed.Add(new Placed(g, Math.Min(sx, ex), Math.Max(sx, ex), sy, sy + g.Ascent, sy + g.Descent));
            }
            var frame = new LayoutFrame(angle, placed, options, cancel);
            foreach (var step in Steps)
                if (!step.Optional || options.Uses(step.Name))
                {
                    cancel.ThrowIfCancellationRequested();
                    step.Run(frame);
                }
            var underlines = new LineMarks(paths, angle, toFrame);
            // in page order (top first)
            frame.Paragraphs.Sort((a, b) => b[0].Baseline.CompareTo(a[0].Baseline) is var c && c != 0 ? c : a[0].Start.CompareTo(b[0].Start));
            foreach (var paragraph in frame.Paragraphs)
                if (Make(paragraph, angle, underlines) is var made && !string.IsNullOrWhiteSpace(made.Text)) blocks.Add(made);   // only spaces: nothing to see
        }
        return blocks;
    }

    /// <summary>Writing direction in whole degrees (−179 … 180).</summary>
    private static int Direction(Glyph g)
    {
        var degrees = (int)Math.Round(Math.Atan2(-g.UpX, g.UpY) * 180 / Math.PI);
        return degrees == -180 ? 180 : degrees;
    }

    // ------------------------------------------------------------ Rows

    /// <summary>Rows: characters on the same baseline (superscript and subscript included), across the whole width.</summary>
    private sealed class RowStep : ILayoutStep
    {
        public string Name => "Zeilen";

        public bool Optional => false;

        /// <summary>At most this many rows open at the same time (crafted pages with thousands of closely staggered baselines).</summary>
        private const int MaxOpenRows = 64;

        private sealed class Building(Placed first)
        {
            public readonly List<Placed> Glyphs = [first];
            public double Baseline = first.Baseline, Size = first.Size;
        }

        /// <summary>
        /// Rows in two steps: first only characters whose baselines match within the measure of the smaller font; with the measure of
        /// the larger font, two small address lines next to a large company name in a letterhead would merge into one row with their
        /// characters interleaved. Then superscripts are added: short, smaller groups close to larger text (footnote marks, ®).
        /// </summary>
        public void Run(LayoutFrame frame)
        {
            var placed = frame.Glyphs;
            // from top to bottom
            placed.Sort((a, b) => b.Baseline.CompareTo(a.Baseline));
            var open = new List<Building>();
            var rows = new List<Building>();
            foreach (var p in placed)
            {
                // rows far enough above take nothing more (the measure is at most 0.45 of their size)
                for (var i = open.Count - 1; i >= 0; i--)
                    if (open[i].Baseline - p.Baseline > open[i].Size || open.Count > MaxOpenRows && i == 0)
                    {
                        rows.Add(open[i]);
                        open.RemoveAt(i);
                    }
                Building? best = null;
                var bestDistance = double.MaxValue;
                foreach (var row in open)
                {
                    var distance = Math.Abs(row.Baseline - p.Baseline);
                    if (distance <= 0.45 * Math.Min(row.Size, p.Size) && distance < bestDistance)
                    {
                        best = row;
                        bestDistance = distance;
                    }
                }
                if (best is null)
                {
                    open.Add(new Building(p));
                    continue;
                }
                best.Glyphs.Add(p);
                if (p.Size > best.Size)
                {
                    // baseline of the largest font (a small superscript does not move it)
                    best.Baseline = p.Baseline;
                    best.Size = p.Size;
                }
            }
            rows.AddRange(open);
            foreach (var row in rows) row.Glyphs.Sort((a, b) => a.Start.CompareTo(b.Start));
            AttachRaised(rows);
            rows.Sort((a, b) => b.Baseline.CompareTo(a.Baseline));
            foreach (var row in rows)
                if (row.Glyphs.Count > 0) frame.Rows.Add(new Row(row.Glyphs, row.Baseline, row.Size));
        }

        /// <summary>
        /// Attaches superscript and subscript groups to their row: at most eight characters, clearly smaller, baseline within 0.45 of the
        /// larger font and at most one font size beside one of its characters (numbers before entries of a bibliography). Longer groups
        /// (a row of small text of its own) stay.
        /// </summary>
        private static void AttachRaised(List<Building> rows)
        {
            if (rows.Count < 2) return;
            var byBaseline = rows.OrderByDescending(r => r.Baseline).ToList();
            var baselines = byBaseline.Select(r => -r.Baseline).ToArray();
            // search range: 0.45 of the largest row; budget across the page (thousands of small rows under a large font)
            var reach = 0.45 * Math.Min(MaxSize, rows.Max(r => r.Size));
            long budget = 2_000_000;
            foreach (var small in byBaseline)
            {
                if (small.Glyphs.Count == 0) continue;
                var groups = Groups(small.Glyphs, small.Size);
                if (groups.All(g => g.Count > 8)) continue;
                var kept = new List<Placed>(small.Glyphs.Count);
                foreach (var group in groups)
                {
                    var target = group.Count <= 8 && budget > 0 ? Host(byBaseline, baselines, small, group, reach, ref budget) : null;
                    if (target is null)
                    {
                        kept.AddRange(group);
                        continue;
                    }
                    foreach (var p in group)
                    {
                        var at = target.Glyphs.BinarySearch(p, StartOrder.Instance);
                        target.Glyphs.Insert(at < 0 ? ~at : at, p);
                    }
                }
                if (kept.Count != small.Glyphs.Count)
                {
                    small.Glyphs.Clear();
                    small.Glyphs.AddRange(kept);
                }
            }
        }

        /// <summary>Characters of a row in groups, split at gaps larger than one font size.</summary>
        private static List<List<Placed>> Groups(List<Placed> glyphs, double size)
        {
            var groups = new List<List<Placed>> { new() { glyphs[0] } };
            var end = glyphs[0].End;
            for (var i = 1; i < glyphs.Count; i++)
            {
                if (glyphs[i].Start - end > size) groups.Add([]);
                groups[^1].Add(glyphs[i]);
                end = Math.Max(end, glyphs[i].End);
            }
            return groups;
        }

        /// <summary>The larger row at whose characters <paramref name="group"/> stands as a superscript or subscript; null if none.</summary>
        private static Building? Host(List<Building> byBaseline, double[] baselines, Building small, List<Placed> group, double reach, ref long budget)
        {
            double start = group[0].Start, end = group.Max(p => p.End), baseline = group[0].Baseline, size = group.Max(p => p.Size);
            // candidates: baselines within the search range — search the list sorted by height from that position
            var from = Array.BinarySearch(baselines, -baseline);
            if (from < 0) from = ~from;
            Building? best = null;
            var bestDistance = double.MaxValue;
            foreach (var step in (ReadOnlySpan<int>)[-1, 1])
                for (var i = step < 0 ? from - 1 : from; i >= 0 && i < byBaseline.Count; i += step)
                {
                    var row = byBaseline[i];
                    var distance = Math.Abs(row.Baseline - baseline);
                    if (distance > reach || --budget < 0) break;
                    if (row == small || row.Glyphs.Count == 0 || size > 0.85 * row.Size || distance > 0.45 * row.Size || distance >= bestDistance) continue;
                    if (!Beside(row.Glyphs, start, end, row.Size)) continue;
                    best = row;
                    bestDistance = distance;
                }
            return best;
        }

        /// <summary>Largest font size whose row still takes superscripts (in points; limits the search).</summary>
        private const double MaxSize = 200;

        /// <summary>Whether a character of <paramref name="glyphs"/> (sorted by start) lies at most <paramref name="reach"/> beside [start, end].</summary>
        private static bool Beside(List<Placed> glyphs, double start, double end, double reach)
        {
            int lo = 0, hi = glyphs.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (glyphs[mid].Start < start - reach - MaxSize) lo = mid + 1;
                else hi = mid;
            }
            for (var i = lo; i < glyphs.Count && glyphs[i].Start <= end + reach; i++)
                if (glyphs[i].End >= start - reach) return true;
            return false;
        }

        private sealed class StartOrder : IComparer<Placed>
        {
            public static readonly StartOrder Instance = new();

            public int Compare(Placed? a, Placed? b) => a!.Start.CompareTo(b!.Start);
        }
    }

    /// <summary>Segments: the rows, split at columns (<see cref="Row.Cuts"/>) and at gaps larger than <see cref="SegmentGap"/>.</summary>
    private sealed class SegmentStep : ILayoutStep
    {
        public string Name => "Stücke";

        public bool Optional => false;

        public void Run(LayoutFrame frame)
        {
            foreach (var row in frame.Rows)
            {
                var glyphs = row.Glyphs;
                var current = new List<Placed> { glyphs[0] };
                var end = glyphs[0].End;
                // walk through the sorted column cuts along the row (the end only grows) instead of checking all of them for each gap
                var cuts = row.Cuts.Count == 0 ? null : row.Cuts.Order().ToArray();
                var next = 0;
                for (var i = 1; i < glyphs.Count; i++)
                {
                    var gap = glyphs[i].Start - end;
                    var size = Math.Max(glyphs[i].Size, current[^1].Size);
                    var cut = false;
                    if (cuts is not null && gap > 0)
                    {
                        while (next < cuts.Length && cuts[next] <= end) next++;
                        cut = next < cuts.Length && cuts[next] < glyphs[i].Start;
                    }
                    // a bar code font against other text: a block of its own — the code goes to the code tool, a label beside it stays
                    // editable text (No. 611). The kind of a segment is that of its first character that is not a space
                    var codeSwitch = glyphs[i].Glyph.Text is not " " && KindOf(current) is { } kind && kind != glyphs[i].Glyph.CodeFont;
                    if ((gap > SegmentGap * size || cut || codeSwitch) && !MarkerOnly(current, gap, size))
                    {
                        frame.Segments.Add(new Segment(current));
                        current = [];
                    }
                    current.Add(glyphs[i]);
                    end = Math.Max(end, glyphs[i].End);
                }
                frame.Segments.Add(new Segment(current));
            }
        }

        /// <summary>Whether a segment is set in a bar code font, by its first character that is not a space; null if it has none yet.</summary>
        private static bool? KindOf(List<Placed> segment)
        {
            foreach (var p in segment)
                if (p.Glyph.Text is not " ") return p.Glyph.CodeFont;
            return null;
        }

        /// <summary>Only a list marker ("•", "6.") before a gap (tab): it stays with its text.</summary>
        private static bool MarkerOnly(List<Placed> current, double gap, double size)
        {
            if (current.Count > 8 || gap > 8 * size) return false;
            var text = string.Concat(current.Select(p => p.Glyph.Text)).Trim();
            return IsMarker(text);
        }
    }
}
