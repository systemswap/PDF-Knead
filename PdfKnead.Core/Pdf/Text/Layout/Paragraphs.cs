namespace PdfKnead.Core.Pdf.Text;

public static partial class TextBlocks
{
    /// <summary>Gap (in font sizes) from which a space is assumed, as in PageText.</summary>
    private const double SpaceGap = 0.15;

    /// <summary>Gap (in font sizes) from which a row always splits into two segments (columns, table cells, form fields).</summary>
    private const double SegmentGap = 2.0;

    /// <summary>Baseline spacing in font sizes within which lines can belong to one paragraph (without page statistics).</summary>
    private const double MinSpacing = 0.85, MaxSpacing = 1.8;

    /// <summary>This much more than the usual line spacing of the page (in font sizes) still counts as the same paragraph.</summary>
    private const double SpacingSlack = 0.2;

    /// <summary>
    /// A rule that separates two lines: does <c>line</c> not belong under the paragraph <c>block</c>? Rules are individually named,
    /// testable and can be switched off (<see cref="BlockOptions.Without"/>); new findings are added as new rules.
    /// </summary>
    internal interface IParagraphRule
    {
        string Name { get; }

        bool Separates(LayoutFrame frame, Paragraph block, Segment line, double distance, double size);
    }

    /// <summary>The rules by which lines form a paragraph (all of them must agree).</summary>
    internal static readonly IReadOnlyList<IParagraphRule> Rules =
        [new SpacingRule(), new SizeRule(), new EvenSpacingRule(), new BulletRule(), new EdgesRule(), new IndentRule(), new LookRule()];

    /// <summary>
    /// Paragraphs: from top to bottom, each line goes under the nearest open paragraph that no rule objects to; otherwise it starts a
    /// new one. Paragraphs whose last line lies far above are finished.
    /// </summary>
    private sealed class ParagraphStep : ILayoutStep
    {
        public string Name => "Absätze";

        public bool Optional => false;

        public void Run(LayoutFrame frame)
        {
            var lines = frame.Segments;
            lines.Sort((a, b) => b.Baseline.CompareTo(a.Baseline) is var c && c != 0 ? c : a.Start.CompareTo(b.Start));
            var rules = Rules.Where(r => frame.Options.Uses(r.Name)).ToList();
            var open = new List<Paragraph>();
            for (var n = 0; n < lines.Count; n++)
            {
                if ((n & 1023) == 0) frame.Cancel.ThrowIfCancellationRequested();
                var line = lines[n];
                Paragraph? best = null;
                var bestDistance = double.MaxValue;
                foreach (var block in open)
                {
                    var last = block[^1];
                    var distance = last.Baseline - line.Baseline;
                    // same row or above: never under it (checked before all rules, otherwise many segments of one row would be quadratic)
                    if (distance <= 0 || distance >= bestDistance) continue;
                    var size = Math.Max(last.Size, line.Size);
                    if (Separated(rules, frame, block, line, distance, size)) continue;
                    bestDistance = distance;
                    best = block;
                }
                if (best is not null) best.Append(line);
                else open.Add(new Paragraph(line));
                // at most this many open paragraphs (plans with thousands of labels in one band): the oldest is then finished
                if (open.Count > MaxOpen)
                {
                    frame.Paragraphs.Add(open[0]);
                    open.RemoveAt(0);
                }
                for (var i = open.Count - 1; i >= 0; i--)
                    if (open[i][^1].Baseline - line.Baseline > Reach(frame, open[i][^1]))
                    {
                        frame.Paragraphs.Add(open[i]);
                        open.RemoveAt(i);
                    }
            }
            frame.Paragraphs.AddRange(open);
        }
    }

    private const int MaxOpen = 4096;

    private static bool Separated(List<IParagraphRule> rules, LayoutFrame frame, Paragraph block, Segment line, double distance, double size)
    {
        foreach (var rule in rules)
            if (rule.Separates(frame, block, line, distance, size)) return true;
        return false;
    }

    /// <summary>How far below its last line a paragraph can still continue (beyond that it is finished).</summary>
    private static double Reach(LayoutFrame frame, Segment last) =>
        1.5 * last.Size * Math.Max(MaxSpacing, (frame.Statistics.LineSpacing(last.Size) ?? 0) + SpacingSlack);

    /// <summary>
    /// Line spacing: at most slightly more than the usual spacing of the page for this size (statistics — also double spacing); without
    /// statistics 0.85 … 1.8 font sizes.
    /// </summary>
    private sealed class SpacingRule : IParagraphRule
    {
        public string Name => "Zeilenabstand";

        public bool Separates(LayoutFrame frame, Paragraph block, Segment line, double distance, double size)
        {
            var max = frame.Statistics.LineSpacing(size) is { } typical ? typical + SpacingSlack : MaxSpacing;
            return distance < MinSpacing * size || distance > max * size;
        }
    }

    /// <summary>Different font size: heading, footnote or similar.</summary>
    private sealed class SizeRule : IParagraphRule
    {
        public string Name => "Schriftgröße";

        public bool Separates(LayoutFrame frame, Paragraph block, Segment line, double distance, double size) =>
            Math.Abs(block[^1].Size - line.Size) > 0.12 * size;
    }

    /// <summary>Uneven spacing: deviates from the spacing so far in the paragraph.</summary>
    private sealed class EvenSpacingRule : IParagraphRule
    {
        public string Name => "Gleichmäßig";

        public bool Separates(LayoutFrame frame, Paragraph block, Segment line, double distance, double size) =>
            block.Count >= 2 && Math.Abs(distance - (block[^2].Baseline - block[^1].Baseline)) > 0.25 * size;
    }

    /// <summary>New list item: a block of its own.</summary>
    private sealed class BulletRule : IParagraphRule
    {
        public string Name => "Aufzählungspunkt";

        public bool Separates(LayoutFrame frame, Paragraph block, Segment line, double distance, double size) => line.BulletIndent is not null;
    }

    /// <summary>
    /// Margins: same left or right margin or same centre, clear overlap; the first line may be indented. Under a list marker the
    /// paragraph continues at the text after the marker (hanging indent) or, if the line wraps fully, at the marker itself
    /// ("[0012] Text", "(5) Text" with the following lines at the margin).
    /// </summary>
    private sealed class EdgesRule : IParagraphRule
    {
        public string Name => "Ränder";

        public bool Separates(LayoutFrame frame, Paragraph block, Segment line, double distance, double size)
        {
            var first = block[0];
            var last = block[^1];
            double left = block.Left, right = block.Right;
            var overlap = Math.Min(right, line.End) - Math.Max(left, line.Start);
            if (overlap < 0.3 * Math.Min(right - left, line.End - line.Start)) return true;
            var tolerance = 0.8 * size;
            if (first.BulletIndent is { } indent)
            {
                if (block.Count >= 2) return Math.Abs(line.Start - last.Start) >= tolerance;
                if (Math.Abs(line.Start - indent) < tolerance) return false;
                var atMarker = Math.Abs(line.Start - first.Start) < tolerance || line.Start < first.Start && first.Start - line.Start < 4 * size;
                return !atMarker || EndedEarly(first, line, Math.Max(right, line.End));
            }
            var leftAligned = Math.Abs(line.Start - last.Start) < tolerance || block.Count == 1 && line.Start < last.Start && last.Start - line.Start < 4 * size;
            var rightAligned = Math.Abs(line.End - last.End) < tolerance;
            var centered = Math.Abs((line.Start + line.End) / 2 - (last.Start + last.End) / 2) < tolerance;
            return !(leftAligned || rightAligned || centered);
        }
    }

    /// <summary>
    /// Different look of the whole line — bold, italic, another font or colour: heading above the paragraph, caption or similar. A
    /// heading is set uniformly: for bold against regular, the bold line must be entirely bold ("Name, 49" above the job title stays one
    /// block); otherwise both lines must be uniform.
    /// </summary>
    private sealed class LookRule : IParagraphRule
    {
        public string Name => "Aussehen";

        public bool Separates(LayoutFrame frame, Paragraph block, Segment line, double distance, double size)
        {
            var last = block[^1];
            if (last.Look == line.Look) return false;
            if (last.Look.Bold != line.Look.Bold) return (last.Look.Bold ? last : line).UniformLook;
            return last.UniformLook && line.UniformLook;
        }
    }

    /// <summary>
    /// Indent: if the lines of the paragraph are flush, an indented line starts the next one (first-line indent, also in justified text where
    /// the right margin would otherwise fit); if the first line stands left of the others (hanging indent, bibliography), a line there starts the next one.
    /// </summary>
    private sealed class IndentRule : IParagraphRule
    {
        public string Name => "Einzug";

        public bool Separates(LayoutFrame frame, Paragraph block, Segment line, double distance, double size)
        {
            if (block.Count < 2) return false;
            Segment first = block[0], second = block[1], last = block[^1];
            var tolerance = 0.5 * size;
            // centred lines become shorter by the same amount on both sides; their start moves without being an indent
            if (Math.Abs(second.Start - first.Start) > tolerance && Math.Abs(second.Start - first.Start - (first.End - second.End)) < tolerance) return false;
            // flush: the lines after the first (with two lines, both) start at the same margin
            var flush = block.Count >= 3 ? Math.Abs(last.Start - second.Start) < tolerance : Math.Abs(first.Start - second.Start) < tolerance;
            if (flush && first.BulletIndent is null && line.Start > last.Start + tolerance && line.Start < last.Start + 6 * size) return true;
            return first.Start < second.Start - tolerance && Math.Abs(last.Start - second.Start) < tolerance && Math.Abs(line.Start - first.Start) < tolerance;
        }
    }

    /// <summary>Whether <paramref name="line"/> ends early, that is, whether the first word of <paramref name="next"/> would still have fitted before <paramref name="right"/>.</summary>
    private static bool EndedEarly(Segment line, Segment next, double right) =>
        line.End + 0.3 * line.Size + FirstWordWidth(next) < right - 0.3 * line.Size;
}
