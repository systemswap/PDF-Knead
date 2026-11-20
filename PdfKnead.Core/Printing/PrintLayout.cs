namespace PdfKnead.Core.Printing;

/// <summary>Size of the pages on the sheet.</summary>
public enum PrintScaling
{
    /// <summary>Fit to the printable area (enlarge or reduce).</summary>
    Fit,

    /// <summary>Actual size (100 %); whatever is too large is cut off.</summary>
    ActualSize,

    /// <summary>Reduce only pages that do not fit; smaller ones stay at actual size.</summary>
    ShrinkLarge,

    /// <summary>Custom percentage.</summary>
    Custom,
}

/// <summary>Orientation of the sheet.</summary>
public enum PrintOrientation
{
    /// <summary>As the page lies (with several pages per sheet: the orientation that gives the largest pages).</summary>
    Auto,

    /// <summary>Portrait.</summary>
    Portrait,

    /// <summary>Landscape.</summary>
    Landscape,
}

/// <summary>Order with several pages per sheet.</summary>
public enum SheetOrder
{
    /// <summary>Row by row: left to right, then downwards.</summary>
    Horizontal,

    /// <summary>Column by column: top to bottom, then to the right.</summary>
    Vertical,
}

/// <summary>How the pages are placed on the sheets (all dimensions in points, 1/72 inch).</summary>
public sealed record PrintLayoutOptions
{
    /// <summary>Paper width in portrait orientation (width ≤ height), e.g. A4 = 595.28 × 841.89; the smaller of the two values is used as width.</summary>
    public double PaperWidth { get; init; } = 595.28;

    /// <summary>Paper height in portrait orientation.</summary>
    public double PaperHeight { get; init; } = 841.89;

    /// <summary>Margin the printer cannot print on (the largest of the four margins; applies on all sides; clamped to 0 … a quarter of the paper width).</summary>
    public double Margin { get; init; } = 12;

    /// <summary>How pages are scaled.</summary>
    public PrintScaling Scaling { get; init; } = PrintScaling.Fit;

    /// <summary>For <see cref="PrintScaling.Custom"/>: 1 to 1000 %.</summary>
    public double Percent { get; init; } = 100;

    /// <summary>Orientation of the sheet.</summary>
    public PrintOrientation Orientation { get; init; } = PrintOrientation.Auto;

    /// <summary>1, 2, 4, 6, 9 or 16; other values are treated as 1.</summary>
    public int PagesPerSheet { get; init; } = 1;

    /// <summary>Order of the pages with several pages per sheet.</summary>
    public SheetOrder Order { get; init; } = SheetOrder.Horizontal;

    /// <summary>Thin border around each page (with several pages per sheet).</summary>
    public bool Borders { get; init; }

    /// <summary>Allowed values for <see cref="PagesPerSheet"/>.</summary>
    public static readonly IReadOnlyList<int> PagesPerSheetChoices = [1, 2, 4, 6, 9, 16];

    /// <summary>Gap between the pages with several pages per sheet.</summary>
    public const double Gap = 8;
}

/// <summary>A page on the sheet: lower left corner at (<paramref name="X"/>, <paramref name="Y"/>), scaled by <paramref name="Scale"/>.</summary>
/// <param name="Page">Position in the list of pages to print.</param>
/// <param name="X">Left edge on the sheet in points.</param>
/// <param name="Y">Bottom edge on the sheet in points.</param>
/// <param name="Scale">Scale factor.</param>
/// <param name="Width">Width of the page as it appears (rotated, unscaled).</param>
/// <param name="Height">Height of the page as it appears (rotated, unscaled).</param>
/// <param name="Clip">Only this area of the sheet (poster: the tile); null = the whole page.</param>
public sealed record SheetPlacement(int Page, double X, double Y, double Scale, double Width, double Height, PdfKnead.Core.Pdf.Document.PdfRectangle? Clip = null)
{
    /// <summary>Right edge on the sheet in points.</summary>
    public double Right => X + Width * Scale;

    /// <summary>Top edge on the sheet in points.</summary>
    public double Top => Y + Height * Scale;
}

/// <summary>A sheet of paper (one side of it) as it lies: landscape means wider than high.</summary>
/// <param name="Width">Width in points.</param>
/// <param name="Height">Height in points.</param>
/// <param name="Placements">The pages on this sheet side.</param>
/// <param name="Borders">Draw a thin border around each page.</param>
/// <param name="Marks">Marks drawn over the pages (poster: crop marks, registration marks, labels).</param>
public sealed record PrintSheet(double Width, double Height, IReadOnlyList<SheetPlacement> Placements, bool Borders = false,
    IReadOnlyList<SheetMark>? Marks = null)
{
    /// <summary>Whether the sheet lies in landscape orientation.</summary>
    public bool Landscape => Width > Height;

    /// <summary>Whether a page (or its clip area) extends beyond the printable area and is therefore cut off (tolerance 0.5 pt).</summary>
    /// <param name="margin">Non-printable margin in points.</param>
    /// <returns>True if at least one placement is cut off.</returns>
    public bool Clips(double margin) => Placements.Any(p =>
    {
        var (left, bottom, right, top) = p.Clip is { } c
            ? (Math.Max(c.Left, p.X), Math.Max(c.Bottom, p.Y), Math.Min(c.Right, p.Right), Math.Min(c.Top, p.Top))
            : (p.X, p.Y, p.Right, p.Top);
        return left < margin - 0.5 || bottom < margin - 0.5 || right > Width - margin + 0.5 || top > Height - margin + 0.5;
    });
}

/// <summary>
/// Distributes pages onto sheets: size (fit, actual, shrink only, percentage), orientation and several pages per sheet. Pure
/// geometry — the preview, "save as PDF" and both printing paths use the same sheets.
/// </summary>
public static class PrintLayout
{
    /// <summary>Arranges pages onto sheets.</summary>
    /// <param name="pages">Page sizes as they appear (rotated), in points; invalid sizes from crafted files are treated as A4.</param>
    /// <param name="options">Layout options; an invalid paper size is treated as A4.</param>
    /// <returns>The sheets in print order; empty if there are no pages.</returns>
    public static List<PrintSheet> Arrange(IReadOnlyList<(double Width, double Height)> pages, PrintLayoutOptions options)
    {
        var sheets = new List<PrintSheet>();
        if (pages.Count == 0) return sheets;
        var paperW = Math.Min(options.PaperWidth, options.PaperHeight);
        var paperH = Math.Max(options.PaperWidth, options.PaperHeight);
        if (!(paperW > 0) || !double.IsFinite(paperH)) (paperW, paperH) = (595.28, 841.89);
        var margin = Math.Clamp(double.IsFinite(options.Margin) ? options.Margin : 0, 0, paperW / 4);
        var perSheet = options.PagesPerSheet;
        if (!PrintLayoutOptions.PagesPerSheetChoices.Contains(perSheet)) perSheet = 1;

        if (perSheet == 1)
        {
            for (var i = 0; i < pages.Count; i++)
            {
                var (w, h) = Valid(pages[i]);
                var landscape = options.Orientation switch
                {
                    PrintOrientation.Portrait => false,
                    PrintOrientation.Landscape => true,
                    _ => w > h,
                };
                var (sw, sh) = landscape ? (paperH, paperW) : (paperW, paperH);
                var scale = Scale(w, h, sw - 2 * margin, sh - 2 * margin, options);
                sheets.Add(new PrintSheet(sw, sh, [new SheetPlacement(i, (sw - w * scale) / 2, (sh - h * scale) / 2, scale, w, h)]));
            }
            return sheets;
        }

        // Several pages per sheet: grid and orientation chosen so that the pages become as large as possible (same for all sheets)
        var (cols, rows) = Grid(perSheet);
        var refW = pages.Max(p => Valid(p).Width);
        var refH = pages.Max(p => Valid(p).Height);
        (double Scale, bool Landscape, int Cols, int Rows) best = (-1, false, cols, rows);
        foreach (var landscape in new[] { false, true })
        {
            if (options.Orientation == PrintOrientation.Portrait && landscape) continue;
            if (options.Orientation == PrintOrientation.Landscape && !landscape) continue;
            var (sw, sh) = landscape ? (paperH, paperW) : (paperW, paperH);
            foreach (var (c, r) in new[] { (cols, rows), (rows, cols) })
            {
                var (cw, ch) = Cell(sw, sh, margin, c, r);
                var s = Math.Min(cw / refW, ch / refH);
                if (s > best.Scale + 1e-9) best = (s, landscape, c, r);
            }
        }
        var (sheetW, sheetH) = best.Landscape ? (paperH, paperW) : (paperW, paperH);
        var (cellW, cellH) = Cell(sheetW, sheetH, margin, best.Cols, best.Rows);
        for (var start = 0; start < pages.Count; start += perSheet)
        {
            var placements = new List<SheetPlacement>(perSheet);
            for (var k = 0; k < perSheet && start + k < pages.Count; k++)
            {
                var (col, row) = options.Order == SheetOrder.Vertical ? (k / best.Rows, k % best.Rows) : (k % best.Cols, k / best.Cols);
                var (w, h) = Valid(pages[start + k]);
                var scale = Math.Min(cellW / w, cellH / h);
                var cellX = margin + col * (cellW + PrintLayoutOptions.Gap);
                var cellTop = sheetH - margin - row * (cellH + PrintLayoutOptions.Gap);
                placements.Add(new SheetPlacement(start + k, cellX + (cellW - w * scale) / 2, cellTop - cellH + (cellH - h * scale) / 2, scale, w, h));
            }
            sheets.Add(new PrintSheet(sheetW, sheetH, placements, options.Borders));
        }
        return sheets;
    }

    /// <summary>Columns × rows in portrait orientation (for 2 and 6 the sheet usually ends up in landscape; the search decides).</summary>
    internal static (int Cols, int Rows) Grid(int perSheet) => perSheet switch
    {
        2 => (1, 2),
        4 => (2, 2),
        6 => (2, 3),
        9 => (3, 3),
        16 => (4, 4),
        _ => (1, 1),
    };

    private static (double Width, double Height) Cell(double sheetW, double sheetH, double margin, int cols, int rows) =>
        (Math.Max(1, (sheetW - 2 * margin - (cols - 1) * PrintLayoutOptions.Gap) / cols),
         Math.Max(1, (sheetH - 2 * margin - (rows - 1) * PrintLayoutOptions.Gap) / rows));

    private static double Scale(double w, double h, double areaW, double areaH, PrintLayoutOptions options)
    {
        var fit = Math.Min(Math.Max(1, areaW) / w, Math.Max(1, areaH) / h);
        return options.Scaling switch
        {
            PrintScaling.ActualSize => 1,
            PrintScaling.ShrinkLarge => Math.Min(1, fit),
            PrintScaling.Custom => Math.Clamp(double.IsFinite(options.Percent) ? options.Percent : 100, 1, 1000) / 100,
            _ => fit,
        };
    }

    /// <summary>Treats nonsensical page sizes (crafted files) as A4.</summary>
    private static (double Width, double Height) Valid((double Width, double Height) page) =>
        page.Width is > 0.5 and < 1e6 && page.Height is > 0.5 and < 1e6 ? page : (595.28, 841.89);
}
