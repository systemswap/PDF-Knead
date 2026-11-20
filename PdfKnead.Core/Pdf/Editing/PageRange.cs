using System.Globalization;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>Page ranges such as "1-3, 7, 10-" (page numbers from 1; "10-" up to the end, "-3" from the start).</summary>
public static class PageRange
{
    /// <summary>Parses page ranges into page indices.</summary>
    /// <param name="text">Ranges separated by commas or semicolons; an en dash counts as a hyphen, and "5-3" counts backwards.</param>
    /// <param name="pageCount">Number of pages in the document.</param>
    /// <returns>
    /// Page indices (from 0) in the given order, without duplicates; empty for empty text; <see langword="null"/> if the text
    /// is not a valid range.
    /// </returns>
    public static List<int>? Parse(string text, int pageCount)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (var raw in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.Replace('–', '-').Replace(" ", "");
            int from, to;
            var dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (!TryPage(part, out from)) return null;
                to = from;
            }
            else
            {
                var left = part[..dash];
                var right = part[(dash + 1)..];
                from = left.Length == 0 ? 1 : TryPage(left, out var f) ? f : -1;
                to = right.Length == 0 ? pageCount : TryPage(right, out var t) ? t : -1;
                if (from < 0 || to < 0) return null;
            }
            if (from < 1 || to < 1 || from > pageCount || to > pageCount) return null;
            var step = from <= to ? 1 : -1;   // "5-3" backwards
            for (var p = from; ; p += step)
            {
                if (seen.Add(p - 1)) result.Add(p - 1);
                if (p == to) break;
            }
        }
        return result;
    }

    private static bool TryPage(string text, out int page) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out page) && page >= 1;

    /// <summary>Formats indices as short text: 0, 1, 2, 6 → "1-3, 7".</summary>
    /// <param name="indices">Page indices from 0, in any order.</param>
    /// <returns>Sorted ranges of page numbers from 1.</returns>
    public static string Format(IEnumerable<int> indices)
    {
        var sorted = indices.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j == i ? $"{sorted[i] + 1}" : $"{sorted[i] + 1}-{sorted[j] + 1}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
}
