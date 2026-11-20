namespace PdfKnead.Core.Pdf.Text;

/// <summary>
/// Measures of the page instead of fixed numbers (after O'Gorman 1993, "Docstrum"): the usual baseline spacing per font size, measured
/// between each segment and the next one below that overlaps it and has the same size; the most frequent value is the line spacing within
/// a paragraph. More spacing indicates a new paragraph, even if it is only slightly larger.
/// </summary>
internal sealed class LayoutStatistics
{
    public static readonly LayoutStatistics Empty = new([]);

    // font size in half points → usual spacing in font sizes
    private readonly Dictionary<int, double> _spacing;

    private LayoutStatistics(Dictionary<int, double> spacing) => _spacing = spacing;

    /// <summary>Usual baseline spacing in font sizes for this size; null if the page has too few lines of it.</summary>
    public double? LineSpacing(double size) => _spacing.TryGetValue(Key(size), out var s) ? s : null;

    private static int Key(double size) => (int)Math.Round(size * 2);

    /// <summary>At least this many pairs, so that the most frequent spacing means something.</summary>
    private const int MinSamples = 4;

    public static LayoutStatistics Measure(List<TextBlocks.Segment> segments, CancellationToken cancel)
    {
        var samples = new Dictionary<int, List<double>>();
        var sorted = segments.OrderByDescending(s => s.Baseline).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            if ((i & 1023) == 0) cancel.ThrowIfCancellationRequested();
            var s = sorted[i];
            // the next segment below that overlaps horizontally and has the same size
            for (var k = i + 1; k < sorted.Count && k - i < 2000; k++)
            {
                var t = sorted[k];
                var distance = s.Baseline - t.Baseline;
                var size = Math.Max(s.Size, t.Size);
                if (distance > 2.5 * size) break;
                if (distance < 0.5 * size || Math.Abs(s.Size - t.Size) > 0.12 * size) continue;
                var overlap = Math.Min(s.End, t.End) - Math.Max(s.Start, t.Start);
                if (overlap < 0.3 * Math.Min(s.End - s.Start, t.End - t.Start)) continue;
                var key = Key(s.Size);
                if (!samples.TryGetValue(key, out var list)) samples[key] = list = [];
                list.Add(distance / size);
                break;
            }
        }
        var spacing = new Dictionary<int, double>();
        foreach (var (key, list) in samples)
        {
            if (list.Count < MinSamples) continue;
            list.Sort();
            // most frequent value: the most neighbours within ±0.04 font sizes (on a tie the smaller one)
            int best = 0, bestCount = 0;
            for (int i = 0, lo = 0, hi = 0; i < list.Count; i++)
            {
                while (list[i] - list[lo] > 0.04) lo++;
                while (hi + 1 < list.Count && list[hi + 1] - list[i] <= 0.04) hi++;
                if (hi - lo + 1 > bestCount)
                {
                    bestCount = hi - lo + 1;
                    best = i;
                }
            }
            if (bestCount < MinSamples) continue;
            spacing[key] = list.Where(v => Math.Abs(v - list[best]) <= 0.04).Average();
        }
        return new LayoutStatistics(spacing);
    }
}

public static partial class TextBlocks
{
    private sealed class StatisticsStep : ILayoutStep
    {
        public string Name => "Statistik";

        public bool Optional => true;

        public void Run(LayoutFrame frame) => frame.Statistics = LayoutStatistics.Measure(frame.Segments, frame.Cancel);
    }
}
