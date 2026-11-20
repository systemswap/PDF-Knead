using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.Core.Printing;

/// <summary>Which pages are printed.</summary>
public enum PrintRange
{
    /// <summary>All pages of the document.</summary>
    All,

    /// <summary>The current page only.</summary>
    Current,

    /// <summary>A range such as "1-3, 7".</summary>
    Custom,

    /// <summary>The pages selected in the page sidebar.</summary>
    Selected,
}

/// <summary>Restricts printing to odd or even page numbers.</summary>
public enum PrintSubset
{
    /// <summary>All pages of the range.</summary>
    All,

    /// <summary>Odd page numbers only (1, 3, 5 …).</summary>
    Odd,

    /// <summary>Even page numbers only (2, 4, 6 …).</summary>
    Even,
}

/// <summary>Converts the page selection of the print dialog into page indices (zero-based) in print order.</summary>
public static class PrintSelection
{
    /// <summary>Computes the zero-based page indices to print, in print order.</summary>
    /// <param name="pageCount">Number of pages in the document.</param>
    /// <param name="range">Which pages to print.</param>
    /// <param name="current">Zero-based index of the current page (used for <see cref="PrintRange.Current"/>).</param>
    /// <param name="custom">Range text such as "1-3, 7" (used for <see cref="PrintRange.Custom"/>).</param>
    /// <param name="selected">Zero-based indices selected in the sidebar (used for <see cref="PrintRange.Selected"/>); out-of-range
    /// indices and duplicates are dropped, the rest is sorted.</param>
    /// <param name="subset">Restriction to odd or even page numbers (counted from 1).</param>
    /// <param name="reverse">Print in reverse order.</param>
    /// <returns>Null for an invalid custom range; an empty list if nothing remains after filtering.</returns>
    public static List<int>? Pages(int pageCount, PrintRange range, int current, string? custom, IEnumerable<int>? selected,
        PrintSubset subset = PrintSubset.All, bool reverse = false)
    {
        List<int>? pages = range switch
        {
            PrintRange.Current => current >= 0 && current < pageCount ? [current] : [],
            PrintRange.Custom => PageRange.Parse(custom ?? "", pageCount) is { Count: > 0 } parsed ? parsed : null,
            PrintRange.Selected => [.. (selected ?? []).Where(i => i >= 0 && i < pageCount).Distinct().Order()],
            _ => [.. Enumerable.Range(0, Math.Max(0, pageCount))],
        };
        if (pages is null) return null;
        // Page numbers count from 1: odd number = even index
        if (subset == PrintSubset.Odd) pages.RemoveAll(i => i % 2 != 0);
        else if (subset == PrintSubset.Even) pages.RemoveAll(i => i % 2 == 0);
        if (reverse) pages.Reverse();
        return pages;
    }
}
