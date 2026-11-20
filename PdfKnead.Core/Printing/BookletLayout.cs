namespace PdfKnead.Core.Printing;

/// <summary>Booklet settings. Dimensions in points.</summary>
/// <param name="RightBinding">Bound on the right (reading direction right to left); otherwise on the left.</param>
/// <param name="Gutter">Gutter: distance between the two pages of a sheet, half on each side of the fold (clamped to 0 … a quarter of the sheet width).</param>
/// <param name="SheetsPerBooklet">Split thick booklets into signatures of this many sheets each (at most <see cref="MaxSheetsPerBooklet"/>); 0 = everything in one signature.</param>
/// <param name="BlanksBeforeBack">Insert the missing pages (up to a multiple of 4) before the last page so that it stays outside as the back cover.</param>
/// <param name="Creep">Creep: after folding, inner sheets stick out — their pages move this much per sheet towards the fold (paper thickness; clamped to 0 … 10).</param>
public sealed record BookletOptions(
    bool RightBinding = false,
    double Gutter = 0,
    int SheetsPerBooklet = 0,
    bool BlanksBeforeBack = true,
    double Creep = 0)
{
    /// <summary>Maximum number of sheets per signature that can still be folded and stapled in the middle (beyond that it is a book).</summary>
    public const int MaxSheetsPerBooklet = 100;
}

/// <summary>One page of the booklet in reading order: which page (−1 = blank) on which sheet side, in which half.</summary>
/// <param name="Page">Zero-based document page, or −1 for a blank page.</param>
/// <param name="Sheet">Index of the sheet side in <see cref="Booklet.Sheets"/>.</param>
/// <param name="RightHalf">Whether the page lies in the right half of the sheet side.</param>
public sealed record BookletPage(int Page, int Sheet, bool RightHalf);

/// <summary>Sheets of the booklet: front and back sides alternating (double-sided, flip on the short edge), plus the reading order.</summary>
/// <param name="Sheets">The sheet sides in print order, landscape.</param>
/// <param name="Pages">The booklet pages in reading order (including blanks).</param>
/// <param name="CreepLimited">The creep was larger than the free margin: the pages move only up to the fold.</param>
public sealed record Booklet(List<PrintSheet> Sheets, IReadOnlyList<BookletPage> Pages, bool CreepLimited = false)
{
    /// <summary>
    /// Imposition: pads the pages to a multiple of 4, then per signature from the outside inwards — with 8 pages sheet 1 front 8|1,
    /// back 2|7, sheet 2 front 6|3, back 4|5. The paper lies in landscape; each page is fitted into its half and centred.
    /// </summary>
    /// <param name="pages">Page sizes in points, in document order; invalid sizes are treated as A4.</param>
    /// <param name="options">Paper size and margin (an invalid paper size is treated as A4).</param>
    /// <param name="booklet">Booklet settings.</param>
    /// <returns>The sheets and the reading order.</returns>
    public static Booklet Arrange(IReadOnlyList<(double Width, double Height)> pages, PrintLayoutOptions options, BookletOptions booklet)
    {
        var paperW = Math.Min(options.PaperWidth, options.PaperHeight);
        var paperH = Math.Max(options.PaperWidth, options.PaperHeight);
        if (!(paperW > 0) || !double.IsFinite(paperH)) (paperW, paperH) = (595.28, 841.89);
        var (sheetW, sheetH) = (paperH, paperW);   // landscape
        var margin = Math.Clamp(double.IsFinite(options.Margin) ? options.Margin : 0, 0, paperW / 4);
        var gutter = Math.Clamp(double.IsFinite(booklet.Gutter) ? booklet.Gutter : 0, 0, sheetW / 4);
        var creep = Math.Clamp(double.IsFinite(booklet.Creep) ? booklet.Creep : 0, 0, 10);

        // Reading order with blank pages (−1)
        var order = Enumerable.Range(0, pages.Count).ToList();
        var missing = (4 - order.Count % 4) % 4;
        if (order.Count == 0) missing = 0;
        if (booklet.BlanksBeforeBack && order.Count >= 2) order.InsertRange(order.Count - 1, Enumerable.Repeat(-1, missing));
        else order.AddRange(Enumerable.Repeat(-1, missing));

        var sheets = new List<PrintSheet>();
        var map = new BookletPage[order.Count];
        var limited = false;
        var perBooklet = booklet.SheetsPerBooklet is > 0 and var k ? Math.Min(k, BookletOptions.MaxSheetsPerBooklet) * 4 : order.Count;
        for (var start = 0; start < order.Count; start += perBooklet)
        {
            var count = Math.Min(perBooklet, order.Count - start);
            var sheetCount = count / 4;
            for (var i = 0; i < sheetCount; i++)
            {
                // From the outside inwards; left/right for left binding (right binding: mirrored)
                var shift = i * creep;
                (int Left, int Right) front = (start + count - 1 - 2 * i, start + 2 * i);
                (int Left, int Right) back = (start + 2 * i + 1, start + count - 2 - 2 * i);
                if (booklet.RightBinding)
                {
                    front = (front.Right, front.Left);
                    back = (back.Right, back.Left);
                }
                foreach (var (left, right) in new[] { front, back })
                {
                    var placements = new List<SheetPlacement>(2);
                    Place(left, false);
                    Place(right, true);
                    sheets.Add(new PrintSheet(sheetW, sheetH, placements));

                    void Place(int position, bool rightHalf)
                    {
                        map[position] = new BookletPage(order[position], sheets.Count, rightHalf);
                        if (order[position] < 0) return;
                        var (w, h) = Valid(pages[order[position]]);
                        var areaLeft = rightHalf ? sheetW / 2 + gutter / 2 : margin;
                        var areaRight = rightHalf ? sheetW - margin : sheetW / 2 - gutter / 2;
                        var areaW = Math.Max(1, areaRight - areaLeft);
                        var areaH = Math.Max(1, sheetH - 2 * margin);
                        var scale = Math.Min(areaW / w, areaH / h);
                        // Never across the fold: at most by the free margin next to the page
                        var free = (areaW - w * scale) / 2 + gutter / 2;
                        var moved = Math.Min(shift, free);
                        limited |= shift > free + 1e-6;
                        var x = areaLeft + (areaW - w * scale) / 2 + (rightHalf ? -moved : moved);
                        placements.Add(new SheetPlacement(order[position], x, (sheetH - h * scale) / 2, scale, w, h));
                    }
                }
            }
        }
        return new Booklet(sheets, map, limited);
    }

    private static (double Width, double Height) Valid((double Width, double Height) page) =>
        page.Width is > 0.5 and < 1e6 && page.Height is > 0.5 and < 1e6 ? page : (595.28, 841.89);
}
