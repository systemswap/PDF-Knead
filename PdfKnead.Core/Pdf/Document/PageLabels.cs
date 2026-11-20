using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Document;

/// <summary>The label of one page (ISO 32000-2, 12.4.2): numbering style (D, R, r, A, a or none), prefix and number.</summary>
/// <param name="Style">The numbering style; null for a label of the prefix alone.</param>
/// <param name="Prefix">The prefix („Anlage-“), empty if none.</param>
/// <param name="Number">The number of the page in its range (/St plus its distance from the range's first page).</param>
public sealed record PageLabel(string? Style, string Prefix, int Number);

/// <summary>
/// The page labels of a document (/PageLabels, a number tree from the page index to its range; ISO 32000-2, 12.4.2; docs/FEHLERGEDAECHTNIS.md,
/// No. 494): read so that they follow the pages when the page order changes.
/// </summary>
public static class PageLabels
{
    private const int MaxDepth = 32, MaxEntries = 100_000;

    /// <summary>The label of each page; null if the document has no page labels (or none that can be read).</summary>
    public static IReadOnlyList<PageLabel>? Of(PdfDocument doc)
    {
        if (doc.Catalog?.GetDictionary(PdfName.Get("PageLabels")) is not { } root) return null;
        var ranges = new SortedDictionary<int, (PdfObject Stored, PdfDictionary Range)>();
        var budget = MaxEntries;
        Collect(root, 0, ranges, ref budget, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance));
        if (ranges.Count == 0) return null;
        var starts = ranges.Keys.ToArray();
        var labels = new PageLabel[doc.PageCount];
        var at = -1;
        for (var page = 0; page < labels.Length; page++)
        {
            while (at + 1 < starts.Length && starts[at + 1] <= page) at++;
            if (at < 0)
            {
                labels[page] = new PageLabel("D", "", page + 1);   // before the first range (a damaged tree): plain numbers
                continue;
            }
            var range = ranges[starts[at]].Range;
            var first = (int)Math.Clamp(range.GetNumber(PdfName.Get("St")) ?? 1, 1, int.MaxValue / 2);
            var style = range.GetName(PdfName.Get("S"))?.Value is { } s && s is "D" or "R" or "r" or "A" or "a" ? s : null;
            labels[page] = new PageLabel(style, range.GetString(PdfName.Get("P"))?.ToText() ?? "", first + page - starts[at]);
        }
        return labels;
    }

    /// <summary>
    /// The ranges of a page-label tree as <see cref="Of"/> reads them — a key a number from 0 below <see cref="int.MaxValue"/> (its integer
    /// part) with a dictionary, the first pair of a key, nodes at most 32 deep and each once —, sorted by key, each value as stored (a reference
    /// stays one): what a writer builds the tree anew from without changing a label (docs/FEHLERGEDAECHTNIS.md, No. 779). Null if the tree holds
    /// more than the budget of <see cref="Of"/> (it is then not read whole).
    /// </summary>
    internal static IReadOnlyList<(long Key, PdfObject Value)>? Ranges(PdfDictionary root)
    {
        var ranges = new SortedDictionary<int, (PdfObject Stored, PdfDictionary Range)>();
        var budget = MaxEntries;
        Collect(root, 0, ranges, ref budget, new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance));
        return budget < 0 ? null : [.. ranges.Select(r => ((long)r.Key, r.Value.Stored))];
    }

    private static void Collect(PdfDictionary node, int depth, SortedDictionary<int, (PdfObject Stored, PdfDictionary Range)> ranges, ref int budget,
        HashSet<PdfDictionary> seen)
    {
        if (depth > MaxDepth || !seen.Add(node)) return;
        if (node.GetArray(PdfName.Get("Nums")) is { } nums)
            for (var i = 0; i + 1 < nums.Count && budget-- > 0; i += 2)
                if (nums.GetNumber(i) is { } key && key >= 0 && key < int.MaxValue && nums.Get(i + 1) is PdfDictionary range)
                    ranges.TryAdd((int)key, (nums[i + 1], range));
        if (node.GetArray(PdfName.Get("Kids")) is { } kids)
            for (var i = 0; i < kids.Count && budget-- > 0; i++)
                if (kids.Get(i) is PdfDictionary kid)
                    Collect(kid, depth + 1, ranges, ref budget, seen);
    }

    /// <summary>
    /// The number tree for pages labelled as given: neighbouring pages form a range while style and prefix stay and the number rises by
    /// one; /St only where it is not 1, /S only with a style.
    /// </summary>
    public static PdfDictionary Tree(IReadOnlyList<PageLabel> labels)
    {
        var nums = new PdfArray();
        PageLabel? previous = null;
        for (var i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            if (previous is null || label.Style != previous.Style || label.Prefix != previous.Prefix || label.Number != previous.Number + 1)
            {
                var range = new PdfDictionary();
                if (label.Style is { } style) range.Set(PdfName.Get("S"), PdfName.Get(style));
                if (label.Prefix.Length > 0) range.Set(PdfName.Get("P"), new PdfString(PdfTextEncoding.Encode(label.Prefix)));
                if (label.Number != 1) range.Set(PdfName.Get("St"), PdfInteger.Of(label.Number));
                nums.Add(PdfInteger.Of(i));
                nums.Add(range);
            }
            previous = label;
        }
        var tree = new PdfDictionary();
        tree.Set(PdfName.Get("Nums"), nums);
        return tree;
    }
}
