namespace PdfKnead.Core.Pdf.Text;

public static partial class TextBlocks
{
    /// <summary>Gap (in font sizes) from which white space can be a column gutter if it continues over several rows.</summary>
    private const double GutterWidth = 1.0;

    /// <summary>A narrow gutter needs this many rows with text on both sides.</summary>
    private const int GutterRows = 3;

    /// <summary>At most this many column cuts per row (crafted files).</summary>
    private const int MaxCuts = 512;

    /// <summary>
    /// Columns from white space (after Breuel 2002, "whitespace cover", simplified): a gap of at least <see cref="GutterWidth"/> font sizes
    /// that continues vertically over at least <see cref="GutterRows"/> rows with text on both sides splits the rows, even if it is
    /// narrower than <see cref="SegmentGap"/> (narrow columns, margin numbers in the gutter, table columns). Single wide word spaces in
    /// justified text do not continue over several rows.
    /// </summary>
    private sealed class GutterStep : ILayoutStep
    {
        public string Name => "Spalten";

        public bool Optional => true;

        public void Run(LayoutFrame frame)
        {
            var rows = frame.Rows;
            var ends = rows.Select(r => r.Glyphs.Max(g => g.End)).ToArray();
            var widest = rows.Select(r => r.Glyphs.Max(g => g.End - g.Start)).ToArray();
            // total budget for following the gaps (otherwise a staircase of rows or thousands of identical rows would follow every gap
            // over all rows — quadratic); once it is used up, no further columns
            long budget = 20_000_000;
            for (var r = 0; r < rows.Count; r++)
            {
                if ((r & 255) == 0) frame.Cancel.ThrowIfCancellationRequested();
                var glyphs = rows[r].Glyphs;
                var end = glyphs[0].End;
                for (var i = 1; i < glyphs.Count; i++)
                {
                    double a = end, b = glyphs[i].Start;
                    end = Math.Max(end, glyphs[i].End);
                    var size = Math.Max(glyphs[i].Size, glyphs[i - 1].Size);
                    // word space: too narrow; above SegmentGap: splits anyway
                    if (b - a < GutterWidth * size || b - a > SegmentGap * size || rows[r].Cuts.Count >= MaxCuts) continue;
                    if (rows[r].Cuts.Any(c => c > a && c < b)) continue;
                    if (budget <= 0) return;
                    var members = new List<(int Row, double Cut)> { (r, (a + b) / 2) };
                    foreach (var step in (ReadOnlySpan<int>)[-1, 1])
                    {
                        double ca = a, cb = b;
                        var last = r;
                        for (var k = r + step; k >= 0 && k < rows.Count && members.Count < 10_000; k += step)
                        {
                            // only closely following rows (the same text area)
                            if (Math.Abs(rows[k].Baseline - rows[last].Baseline) > 2.5 * Math.Max(size, rows[k].Size)) break;
                            budget -= 16;
                            var (fa, fb) = Free(rows[k].Glyphs, widest[k], ca, cb);
                            if (fb - fa < GutterWidth * size) break;
                            ca = fa;
                            cb = fb;
                            last = k;
                            // counts only with text on both sides (a short last line on the left keeps the gutter open)
                            if (rows[k].Glyphs[0].Start < fa && ends[k] > fb) members.Add((k, (fa + fb) / 2));
                        }
                    }
                    if (members.Count < GutterRows) continue;
                    foreach (var (k, cut) in members) rows[k].Cuts.Add(cut);
                }
            }
        }

        /// <summary>The widest free stretch of the row within [a, b] (characters sorted by start; <paramref name="widest"/>: widest character).</summary>
        private static (double A, double B) Free(List<Placed> glyphs, double widest, double a, double b)
        {
            // first character that can reach into [a, b]
            int lo = 0, hi = glyphs.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (glyphs[mid].Start < a - widest) lo = mid + 1;
                else hi = mid;
            }
            double bestA = a, bestB = a, cursor = a;
            for (var i = lo; i < glyphs.Count && glyphs[i].Start < b; i++)
            {
                var g = glyphs[i];
                if (g.End <= cursor) continue;
                if (g.Start > cursor && g.Start - cursor > bestB - bestA)
                {
                    bestA = cursor;
                    bestB = g.Start;
                }
                cursor = Math.Max(cursor, g.End);
            }
            if (b - cursor > bestB - bestA)
            {
                bestA = cursor;
                bestB = b;
            }
            return (bestA, bestB);
        }
    }
}
