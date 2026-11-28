using System.Text;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Pdf.Text;

/// <summary>Last step of block detection: from the lines of a paragraph, the finished block (runs, line breaks, tabs, alignment).</summary>
public static partial class TextBlocks
{
    /// <summary>
    /// Flat lines in the frame that may be underlines or strikethroughs (only lines horizontal in reading direction, in an unrotated
    /// frame), sorted by height, so that each line of text searches only the band around its baseline (plans have hundreds of thousands
    /// of lines). Each line belongs to at most one block, because it is removed together with it when editing.
    /// </summary>
    private sealed class LineMarks
    {
        private readonly (int Ordinal, double Start, double End, double Position, double Thickness)[] _lines;
        private readonly HashSet<int> _claimed = [];

        public LineMarks(IReadOnlyList<PathMark> paths, double angle, Matrix toFrame)
        {
            if (Math.Abs(angle) > 0.5)
            {
                _lines = [];   // rotated blocks: lines are not assigned
                return;
            }
            _lines = new (int, double, double, double, double)[paths.Count];
            for (var i = 0; i < paths.Count; i++)
            {
                var b = toFrame.TransformBounds(paths[i].Bounds);
                _lines[i] = (paths[i].Ordinal, b.Left, b.Right, (b.Top + b.Bottom) / 2, Math.Abs(b.Height));
            }
            Array.Sort(_lines, (a, b) => a.Position.CompareTo(b.Position));
        }

        /// <summary>Lines whose centre lies between <paramref name="from"/> and <paramref name="to"/>.</summary>
        public IEnumerable<(int Ordinal, double Start, double End, double Position, double Thickness)> Between(double from, double to)
        {
            int lo = 0, hi = _lines.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (_lines[mid].Position < from) lo = mid + 1;
                else hi = mid;
            }
            for (var i = lo; i < _lines.Length && _lines[i].Position <= to; i++) yield return _lines[i];
        }

        public bool Claim(int ordinal) => _claimed.Add(ordinal);
    }

    private static PageTextBlock Make(List<Segment> segments, double angle, LineMarks underlines)
    {
        var lines = new List<TextBlockLine>();
        var glyphs = new List<int>();
        var paths = new List<int>();
        string? locked = null;
        var codeFont = false;
        double top = double.MinValue, bottom = double.MaxValue;
        foreach (var segment in segments)
        {
            top = Math.Max(top, segment.Top);
            bottom = Math.Min(bottom, segment.Bottom);
            // Lines just below the baseline (underline) or at half the x-height (strikethrough): thin, under text of the line and
            // not wider than it — a signature line under "Place, date" or a table rule under the cell is not an underline
            // and stays in place when editing
            var marks = new List<(double Start, double End, bool Under)>();
            var size = segment.Size;
            foreach (var (ordinal, start, end, position, thickness) in underlines.Between(segment.Baseline - 0.35 * size, segment.Baseline + 0.45 * size))
            {
                var offset = position - segment.Baseline;
                var under = offset < 0;
                var strike = offset > 0.15 * size;
                if (!under && !strike) continue;
                if (thickness > 0.15 * size || end < segment.Start || start > segment.End) continue;
                if (start < segment.Start - size || end > segment.End + size) continue;
                if (!underlines.Claim(ordinal)) continue;
                marks.Add((start, end, under));
                paths.Add(ordinal);
            }
            lines.Add(Line(segment, marks, glyphs, ref locked));
            codeFont |= segment.Glyphs.Any(p => p.Glyph.CodeFont);
        }
        // Soft break only for full lines of a real paragraph: the first word of the next line would not have fitted, and
        // at least two lines reach close to the right margin (or the line ends with a hyphen). With two lines such as
        // "Customer number / 12345" this cannot be told apart, so the break is hard (when editing, nothing jumps to another line).
        double right = segments.Max(s => s.End);
        bool Full(Segment s) => s.End >= right - 3 * s.Size;
        var fullLines = segments.Take(segments.Count - 1).Count(Full);
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            var next = segments[i + 1];
            var space = 0.3 * segments[i].Size;
            var fits = segments[i].End + space + FirstWordWidth(next) < right - 0.3 * segments[i].Size;
            var hyphen = lines[i].Text.TrimEnd() is { Length: > 1 } t && t[^1] is '-' or '\u00AD' && char.IsLetter(t[^2]);
            // lines with tabs are tabular (price list, label and value): their end is always hard, even if they fill the margin
            var tabular = lines[i].Tabs.Count > 0 || lines[i + 1].Tabs.Count > 0;
            var soft = !fits && !tabular && Full(segments[i]) && (fullLines >= 2 || hyphen);
            lines[i] = lines[i] with { HardBreak = !soft };
        }
        var spacing = segments.Count >= 2 ? (segments[0].Baseline - segments[^1].Baseline) / (segments.Count - 1) : 1.2 * segments[0].Size;
        // editable only in quarter turns (the new block rotates like elements: 0, 90, 180, 270 degrees)
        if (angle % 90 != 0) locked ??= Tr.T("Schräg gesetzter Text — nicht bearbeitbar.");
        // a bar code font comes before every other reason (also before Type 3, phase 8.1): its block goes to the code tool
        if (codeFont) locked = CodeFontLock;
        var alignment = Alignment(segments);
        // justification stretches the word spacing, so large gaps there are not tabs; for centred and right-aligned text a tab stop from the
        // left margin makes no sense (the line comes from the right): a space there
        if (alignment != BlockAlignment.Left)
            for (var i = 0; i < lines.Count; i++)
                if (lines[i].Tabs.Count > 0)
                    lines[i] = lines[i] with { Runs = [.. lines[i].Runs.Select(r => r with { Text = r.Text.Replace('\t', ' ') })], Tabs = [] };
        var left = segments.Min(s => s.Start);
        return new PageTextBlock(lines, angle, alignment, spacing, locked, glyphs, paths.Distinct().ToList(), top, bottom)
        {
            CodeFont = codeFont,
            Indent = Indent(segments),
            TabStops = TabStops(lines, left),
        };
    }

    /// <summary>
    /// Indent: the following lines start together to the right of the first (list item, positive) or the first to the right of the others
    /// (indented paragraph start, negative); otherwise 0.
    /// </summary>
    private static double Indent(List<Segment> segments)
    {
        if (segments.Count < 2) return segments[0].BulletIndent is { } bullet ? bullet - segments[0].Start : 0;
        var size = segments.Max(s => s.Size);
        var rest = segments.Skip(1).Select(s => s.Start).ToList();
        if (rest.Max() - rest.Min() > 0.5 * size) return 0;
        var delta = rest[0] - segments[0].Start;
        return Math.Abs(delta) > 0.5 * size ? delta : 0;
    }

    private static double FirstWordWidth(Segment segment)
    {
        var g = segment.Glyphs;
        var end = g[0].End;
        for (var i = 1; i < g.Count; i++)
        {
            if (g[i].Glyph.Text is " " || g[i].Start - g[i - 1].End > SpaceGap * g[i].Size) break;
            end = g[i].End;
        }
        return end - g[0].Start;
    }

    /// <summary>
    /// Tab stops of the block: per column (the k-th tab of the lines) left if the texts after it start together, right if they
    /// end together (amounts), centred if their centres line up. In a single line this cannot be told apart: left.
    /// </summary>
    private static List<TabStop> TabStops(List<TextBlockLine> lines, double left)
    {
        const double tolerance = 1;
        var stops = new List<TabStop>();
        var columns = lines.Max(l => Math.Min(l.Tabs.Count, l.TabEnds.Count));
        for (var k = 0; k < columns; k++)
        {
            var cells = lines.Where(l => k < l.Tabs.Count && k < l.TabEnds.Count).Select(l => (Start: l.Tabs[k], End: l.TabEnds[k])).ToList();
            static double Spread(IEnumerable<double> values) => values.Max() - values.Min();
            if (cells.Count >= 2 && Spread(cells.Select(c => c.Start)) > tolerance)
            {
                if (Spread(cells.Select(c => c.End)) <= tolerance)
                {
                    stops.Add(new TabStop(Math.Round(cells.Average(c => c.End) - left, 1), BlockAlignment.Right));
                    continue;
                }
                if (Spread(cells.Select(c => (c.Start + c.End) / 2)) <= tolerance)
                {
                    stops.Add(new TabStop(Math.Round(cells.Average(c => (c.Start + c.End) / 2) - left, 1), BlockAlignment.Center));
                    continue;
                }
            }
            foreach (var (start, _) in cells) stops.Add(new TabStop(Math.Round(start - left, 1)));
        }
        return [.. stops.Distinct().OrderBy(s => s.Position)];
    }

    /// <summary>Line from one segment: runs of equal formatting, spaces from gaps, characters drawn twice only once.</summary>
    private static TextBlockLine Line(Segment segment, List<(double Start, double End, bool Under)> marks, List<int> allGlyphs, ref string? locked)
    {
        var runs = new List<TextRun>();
        var text = new StringBuilder();
        var ordinals = new List<int>();
        TextFormat? format = null;
        Placed? previous = null;
        double trackSum = 0;
        var trackCount = 0;
        // tabs: gaps much larger than the word spacing of the line (the text jumps to a tab stop)
        var wordGaps = new List<double>();
        for (var i = 1; i < segment.Glyphs.Count; i++)
            if (segment.Glyphs[i].Start - segment.Glyphs[i - 1].End is var g && g > SpaceGap * segment.Glyphs[i].Size) wordGaps.Add(g);
        wordGaps.Sort();
        var median = wordGaps.Count > 0 ? wordGaps[wordGaps.Count / 2] : 0;
        var tabs = new List<double>();
        var tabEnds = new List<double>();
        void Flush()
        {
            if (format is not null && (text.Length > 0 || ordinals.Count > 0))
                runs.Add(new TextRun(text.ToString(), format, [.. ordinals]) { Tracking = TrackingOf(trackSum, trackCount, format.Size) });
            text.Clear();
            ordinals.Clear();
            trackSum = 0;
            trackCount = 0;
        }
        foreach (var p in segment.Glyphs)
        {
            allGlyphs.Add(p.Glyph.Ordinal);
            locked ??= p.Glyph.Locked;
            // symbol font: allowed only as a list marker (it stays in its font when editing)
            if (p.Glyph.Symbol && !(segment.BulletIndent is { } textStart && p.Start < textStart)) locked ??= SymbolLock;
            // bold by overprinting: the same character at almost the same position
            if (previous is not null && p.Glyph.Text == previous.Glyph.Text && Math.Abs(p.Start - previous.Start) < 0.15 * p.Size)
            {
                ordinals.Add(p.Glyph.Ordinal);
                continue;
            }
            var underline = marks.Any(m => m.Under && m.Start <= p.End && m.End >= p.Start);
            var strike = marks.Any(m => !m.Under && m.Start <= p.End && m.End >= p.Start);
            var glyphFormat = underline || strike ? p.Glyph.Format with { Underline = underline, Strikeout = strike } : p.Glyph.Format;
            var gap = previous is null ? 0 : p.Start - previous.End;
            var spaced = previous is not null && gap > SpaceGap * p.Size && text.Length > 0 && !char.IsWhiteSpace(text[^1]) && p.Glyph.Text is not " ";
            var tabbed = previous is not null && p.Glyph.Text is not " " && (gap > 1.2 * p.Size || gap > 0.8 * p.Size && gap > 2.5 * median);
            if (tabbed)
            {
                // tab (belongs to the previous run; a space before it is absorbed), the stop is the start of the character
                if (text.Length > 0 && text[^1] == ' ') text.Length--;
                else if (text.Length == 0 && runs.Count > 0 && runs[^1].Text.EndsWith(' ')) runs[^1] = runs[^1] with { Text = runs[^1].Text[..^1] };
                if (text.Length > 0) text.Append('\t');
                else if (runs.Count > 0) runs[^1] = runs[^1] with { Text = runs[^1].Text + "\t" };
                tabs.Add(p.Start);
                tabEnds.Add(p.End);
                spaced = false;
            }
            if (format is null || !glyphFormat.SameAs(format))
            {
                // the space belongs to the previous run
                if (spaced) text.Append(' ');
                spaced = false;
                Flush();
                format = glyphFormat;
            }
            if (spaced) text.Append(' ');
            else if (previous is not null && ordinals.Count > 0 && !tabbed && p.Glyph.Text is not " " && previous.Glyph.Text is not " ")
            {
                // spacing within the word: tracking (not the jump of a tab; not beside a space, whose advance carries the word spacing — a
                // TJ shift taking it back made a „tracking“ of −5 pt at 7 pt, and edited text ran backwards: No. 609)
                trackSum += gap;
                trackCount++;
            }
            text.Append(p.Glyph.Text ?? "");
            ordinals.Add(p.Glyph.Ordinal);
            previous = p;
            // end of the text after the last tab (spaces do not count)
            if (tabEnds.Count > 0 && p.Glyph.Text is not (null or " ")) tabEnds[^1] = Math.Max(tabEnds[^1], p.End);
        }
        Flush();
        return new TextBlockLine(runs, segment.Start, segment.End, segment.Baseline) { Tabs = tabs, TabEnds = tabEnds };
    }

    /// <summary>
    /// The mean tracking of a run; 0 if it is beyond what typesetting does (more than 30 % of the font size either way): then the measure
    /// caught something else, and taken over it would make the characters of edited text overlap or run backwards (No. 609).
    /// </summary>
    private static double TrackingOf(double sum, int count, double size)
    {
        if (count == 0) return 0;
        var mean = sum / count;
        return Math.Abs(mean) > 0.3 * size ? 0 : Math.Round(mean, 3);
    }

    /// <summary>Alignment from the margins (the last line does not count for the right margin, because it ends earlier in justified text).</summary>
    private static BlockAlignment Alignment(List<Segment> lines)
    {
        if (lines.Count < 2) return BlockAlignment.Left;
        var size = lines.Max(l => l.Size);
        var tolerance = 0.5 * size;
        // right margin flush: exactly (as set), not just lines of ragged text that happen to be of similar length
        var edge = 0.15 * size;
        static double Spread(IEnumerable<double> values)
        {
            var list = values.ToList();
            return list.Count == 0 ? 0 : list.Max() - list.Min();
        }
        var body = lines.Skip(lines[0].BulletIndent is null && lines.Count > 2 ? 1 : 0).ToList();   // the first line may be indented
        var left = Spread(body.Select(l => l.Start)) < tolerance;
        var right = Spread(lines.Take(lines.Count - 1).Select(l => l.End)) < edge;
        var lastRight = Math.Abs(lines[^1].End - lines[0].End) < edge;
        if (left && right && !lastRight && lines.Count >= 3) return BlockAlignment.Justify;
        if (left) return BlockAlignment.Left;
        if (right && lastRight) return BlockAlignment.Right;
        if (Spread(lines.Select(l => (l.Start + l.End) / 2)) < tolerance) return BlockAlignment.Center;
        return BlockAlignment.Left;
    }
}
