using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.Core.Printing;

/// <summary>Print kind; the print window offers one choice and shows only the options of the chosen kind.</summary>
public enum PrintKind
{
    /// <summary>One or more pages per sheet.</summary>
    Normal,

    /// <summary>Two pages per sheet side, folded in the middle.</summary>
    Booklet,

    /// <summary>One page enlarged onto several sheets (see KONZEPT.md, section 5.11).</summary>
    Poster,
}

/// <summary>Poster settings (see KONZEPT.md, section 5.11). Dimensions in points.</summary>
/// <param name="TargetWidth">Target size in portrait orientation (width ≤ height); the orientation follows the page; has no effect if <paramref name="Percent"/> is set. Default A2 width.</param>
/// <param name="TargetHeight">Target height in portrait orientation (default A2 height).</param>
/// <param name="Percent">Greater than 0: enlarge by this percentage (clamped to <see cref="MinPercent"/> … <see cref="MaxPercent"/>) instead of to the target size.</param>
/// <param name="Overlap">Glue margin: how far neighbouring sheets overlap (default 10 mm, at most <see cref="MaxOverlap"/>).</param>
/// <param name="CutMarks">Crop marks at the corners of the content and lines where the neighbouring sheet adjoins.</param>
/// <param name="RegistrationMarks">Registration marks in the middle of the glue margin — they appear at the same place on both neighbouring sheets.</param>
/// <param name="Labels">Tile label (e.g. "B2 · page 1 · 3 × 2").</param>
public sealed record PosterOptions(
    double TargetWidth = 1190.55,
    double TargetHeight = 1683.78,
    double Percent = 0,
    double Overlap = 28.35,
    bool CutMarks = true,
    bool RegistrationMarks = true,
    bool Labels = true)
{
    /// <summary>Maximum number of sheets per page (1000 % on A4 would already be 100).</summary>
    public const int MaxSheetsPerPage = 400;

    /// <summary>Band around the content for marks and labels (about 5 mm), inside the printable area.</summary>
    public const double MarkBand = 14;

    /// <summary>Allowed enlargement in percent.</summary>
    public const double MinPercent = 10, MaxPercent = 2000;

    /// <summary>Largest glue margin (about 50 mm; more brings nothing, and the content per sheet would become small).</summary>
    public const double MaxOverlap = 141.73;
}

/// <summary>Kind of mark on the sheet.</summary>
public enum SheetMarkKind
{
    /// <summary>Hairline from (X, Y) to (X2, Y2).</summary>
    Line,

    /// <summary>Registration mark around (X, Y): circle and cross, <see cref="SheetMark.Size"/> wide.</summary>
    Cross,

    /// <summary>Text with its baseline at (X, Y), <see cref="SheetMark.Size"/> points high.</summary>
    Text,
}

/// <summary>Mark on the sheet (points, origin lower left), black and thin.</summary>
/// <param name="Kind">Kind of mark.</param>
/// <param name="X">X position.</param>
/// <param name="Y">Y position.</param>
/// <param name="X2">End X of a line.</param>
/// <param name="Y2">End Y of a line.</param>
/// <param name="Size">Width of a cross or font size of a text.</param>
/// <param name="Text">Text of a text mark.</param>
public sealed record SheetMark(SheetMarkKind Kind, double X, double Y, double X2 = 0, double Y2 = 0, double Size = 0, string? Text = null);

/// <summary>
/// Grid of a poster: the page (<paramref name="Page"/>, position in the list of pages to print) enlarged by <paramref name="Scale"/>,
/// in poster coordinates (origin top left, growing right and down) at (<paramref name="OffsetX"/>, <paramref name="OffsetY"/>); the
/// tile (column c, row r) shows the window starting at (c · <paramref name="StepX"/>, r · <paramref name="StepY"/>) of size
/// <paramref name="TileWidth"/> × <paramref name="TileHeight"/>. Its sheets start at <paramref name="FirstSheet"/>, row by row.
/// </summary>
/// <param name="Page">Position in the list of pages to print.</param>
/// <param name="Width">Page width as it appears, unscaled.</param>
/// <param name="Height">Page height as it appears, unscaled.</param>
/// <param name="Scale">Enlargement factor.</param>
/// <param name="OffsetX">Left edge of the enlarged page in poster coordinates.</param>
/// <param name="OffsetY">Top edge of the enlarged page in poster coordinates.</param>
/// <param name="StepX">Horizontal distance between tiles (tile width minus overlap).</param>
/// <param name="StepY">Vertical distance between tiles (tile height minus overlap).</param>
/// <param name="TileWidth">Width of the content area of a sheet.</param>
/// <param name="TileHeight">Height of the content area of a sheet.</param>
/// <param name="Columns">Number of tile columns.</param>
/// <param name="Rows">Number of tile rows.</param>
/// <param name="FirstSheet">Index of the first sheet of this page in <see cref="Poster.Sheets"/>.</param>
public sealed record PosterGrid(int Page, double Width, double Height, double Scale, double OffsetX, double OffsetY,
    double StepX, double StepY, double TileWidth, double TileHeight, int Columns, int Rows, int FirstSheet)
{
    /// <summary>Number of sheets of this page.</summary>
    public int SheetCount => Columns * Rows;

    /// <summary>Width covered by all tiles together (slightly larger than the enlarged page, which is centred in it).</summary>
    public double CoverWidth => (Columns - 1) * StepX + TileWidth;

    /// <summary>Height covered by all tiles together.</summary>
    public double CoverHeight => (Rows - 1) * StepY + TileHeight;

    /// <summary>Tile label "A1", "B1" … "AA3": column as letters, row as number (as in a spreadsheet).</summary>
    /// <param name="column">Zero-based column.</param>
    /// <param name="row">Zero-based row.</param>
    /// <returns>The label.</returns>
    public static string Label(int column, int row)
    {
        var name = "";
        for (var c = column + 1; c > 0; c = (c - 1) / 26) name = (char)('A' + (c - 1) % 26) + name;
        return name + (row + 1);
    }
}

/// <summary>Sheets of the poster (all pages one after another, row by row per page) and the grid per page.</summary>
/// <param name="Sheets">The sheets in print order.</param>
/// <param name="Grids">One grid per page.</param>
/// <param name="TooLarge">A page would need more than <see cref="PosterOptions.MaxSheetsPerPage"/> sheets: then there are no sheets at all.</param>
public sealed record Poster(List<PrintSheet> Sheets, IReadOnlyList<PosterGrid> Grids, bool TooLarge = false)
{
    /// <summary>Finds the grid a sheet belongs to, and its column and row.</summary>
    /// <param name="sheet">Index in <see cref="Sheets"/>.</param>
    /// <returns>Grid, zero-based column and row; null if the index belongs to no grid.</returns>
    public (PosterGrid Grid, int Column, int Row)? TileOf(int sheet)
    {
        foreach (var grid in Grids)
            if (sheet >= grid.FirstSheet && sheet < grid.FirstSheet + grid.SheetCount)
                return (grid, (sheet - grid.FirstSheet) % grid.Columns, (sheet - grid.FirstSheet) / grid.Columns);
        return null;
    }

    /// <summary>
    /// Enlarges each page (to the target size, in the orientation of the page, or by the percentage) and splits it into tiles: per
    /// sheet the printable area without the band for marks; neighbours overlap by the glue margin. With automatic orientation, the
    /// sheet orientation needing fewer sheets is chosen (portrait on a tie).
    /// </summary>
    /// <param name="pages">Page sizes as they appear, in points; invalid sizes are treated as A4.</param>
    /// <param name="options">Paper size, margin and orientation.</param>
    /// <param name="poster">Poster settings.</param>
    /// <param name="pageLabel">Label of the page with {0} for the page number (formatted with <see cref="Tr.Culture"/>).</param>
    /// <returns>The sheets and grids; empty with <see cref="TooLarge"/> set if a page would need too many sheets.</returns>
    public static Poster Arrange(IReadOnlyList<(double Width, double Height)> pages, PrintLayoutOptions options, PosterOptions poster, string pageLabel = "S. {0}")
    {
        var paperW = Math.Min(options.PaperWidth, options.PaperHeight);
        var paperH = Math.Max(options.PaperWidth, options.PaperHeight);
        if (!(paperW > 0) || !double.IsFinite(paperH)) (paperW, paperH) = (595.28, 841.89);
        var margin = Math.Clamp(double.IsFinite(options.Margin) ? options.Margin : 0, 0, paperW / 4);
        var marks = poster.CutMarks || poster.RegistrationMarks || poster.Labels;
        var band = marks ? PosterOptions.MarkBand : 0;

        var sheets = new List<PrintSheet>();
        var grids = new List<PosterGrid>();
        for (var i = 0; i < pages.Count; i++)
        {
            var (w, h) = Valid(pages[i]);
            double scale;
            if (poster.Percent > 0)
            {
                scale = Math.Clamp(poster.Percent, PosterOptions.MinPercent, PosterOptions.MaxPercent) / 100;
            }
            else
            {
                double tw = Math.Min(poster.TargetWidth, poster.TargetHeight), th = Math.Max(poster.TargetWidth, poster.TargetHeight);
                if (!(tw > 0) || !double.IsFinite(th)) (tw, th) = (1190.55, 1683.78);
                if (w > h) (tw, th) = (th, tw);
                scale = Math.Min(tw / w, th / h);
            }
            var (sw, sh) = (w * scale, h * scale);

            // Sheet orientation: the one needing fewer sheets (portrait on a tie)
            (int Cols, int Rows, bool Landscape, double Aw, double Ah, double Overlap)? best = null;
            foreach (var landscape in new[] { false, true })
            {
                if (options.Orientation == PrintOrientation.Portrait && landscape) continue;
                if (options.Orientation == PrintOrientation.Landscape && !landscape) continue;
                var (pw, ph) = landscape ? (paperH, paperW) : (paperW, paperH);
                var aw = Math.Max(1, pw - 2 * (margin + band));
                var ah = Math.Max(1, ph - 2 * (margin + band));
                var overlap = Math.Clamp(double.IsFinite(poster.Overlap) ? poster.Overlap : 0, 0, Math.Min(PosterOptions.MaxOverlap, Math.Min(aw, ah) / 2));
                var cols = Count(sw, aw, overlap);
                var rows = Count(sh, ah, overlap);
                if (best is null || (long)cols * rows < (long)best.Value.Cols * best.Value.Rows) best = (cols, rows, landscape, aw, ah, overlap);
            }
            var (columns, rowCount, land, tileW, tileH, ov) = best!.Value;
            if ((long)columns * rowCount > PosterOptions.MaxSheetsPerPage) return new Poster([], [], TooLarge: true);

            var (stepX, stepY) = (tileW - ov, tileH - ov);
            var grid = new PosterGrid(i, w, h, scale, 0, 0, stepX, stepY, tileW, tileH, columns, rowCount, sheets.Count);
            grid = grid with { OffsetX = (grid.CoverWidth - sw) / 2, OffsetY = (grid.CoverHeight - sh) / 2 };
            grids.Add(grid);
            var (sheetW, sheetH) = land ? (paperH, paperW) : (paperW, paperH);
            for (var r = 0; r < rowCount; r++)
                for (var c = 0; c < columns; c++)
                    sheets.Add(Tile(grid, c, r, sheetW, sheetH, margin + band, band, poster, string.Format(Tr.Culture, pageLabel, i + 1)));
        }
        return new Poster(sheets, grids);
    }

    /// <summary>Number of tiles for a length: one if it fits, otherwise enough that the steps plus glue margin cover it.</summary>
    private static int Count(double length, double tile, double overlap)
    {
        if (length <= tile + 1e-6) return 1;
        var n = (int)Math.Ceiling((length - overlap) / (tile - overlap) - 1e-9);
        return Math.Clamp(n, 1, PosterOptions.MaxSheetsPerPage + 1);
    }

    /// <summary>One sheet: the page shifted so that its window lies in the content area and clipped to it, plus the marks.</summary>
    private static PrintSheet Tile(PosterGrid g, int c, int r, double sheetW, double sheetH, double inset, double band, PosterOptions poster, string page)
    {
        double left = inset, top = sheetH - inset, right = left + g.TileWidth, bottom = top - g.TileHeight;
        // Poster coordinates → sheet: x = left + (u − c·StepX), y = top − (v − r·StepY); the page's lower left corner is at (OffsetX, OffsetY + height)
        var x = left + g.OffsetX - c * g.StepX;
        var y = top - (g.OffsetY + g.Height * g.Scale - r * g.StepY);
        var placement = new SheetPlacement(g.Page, x, y, g.Scale, g.Width, g.Height, new PdfRectangle(left, bottom, right, top));

        var marks = new List<SheetMark>();
        if (band > 0)
        {
            const double gap = 2;
            var reach = band - 1;
            if (poster.CutMarks)
            {
                // Corners of the content: this is where to cut
                foreach (var (cx, cy, dx, dy) in new[] { (left, top, -1, 1), (right, top, 1, 1), (left, bottom, -1, -1), (right, bottom, 1, -1) })
                {
                    marks.Add(new SheetMark(SheetMarkKind.Line, cx + dx * gap, cy, cx + dx * reach, cy));
                    marks.Add(new SheetMark(SheetMarkKind.Line, cx, cy + dy * gap, cx, cy + dy * reach));
                }
                // Where the neighbouring sheet adjoins (start of the glue margin)
                if (c < g.Columns - 1)
                {
                    var gx = left + g.StepX;
                    marks.Add(new SheetMark(SheetMarkKind.Line, gx, top + gap, gx, top + reach));
                    marks.Add(new SheetMark(SheetMarkKind.Line, gx, bottom - gap, gx, bottom - reach));
                }
                if (r < g.Rows - 1)
                {
                    var gy = top - g.StepY;
                    marks.Add(new SheetMark(SheetMarkKind.Line, left - gap, gy, left - reach, gy));
                    marks.Add(new SheetMark(SheetMarkKind.Line, right + gap, gy, right + reach, gy));
                }
            }
            if (poster.RegistrationMarks)
            {
                // Middle of the glue margin: at the same place of the poster on both neighbouring sheets
                var size = Math.Min(band - 3, 10);
                var overlap = g.TileWidth - g.StepX;
                if (c < g.Columns - 1) AddCrosses(left + g.StepX + overlap / 2, vertical: false);
                if (c > 0) AddCrosses(left + overlap / 2, vertical: false);
                var overlapY = g.TileHeight - g.StepY;
                if (r < g.Rows - 1) AddCrosses(top - g.StepY - overlapY / 2, vertical: true);
                if (r > 0) AddCrosses(top - overlapY / 2, vertical: true);

                void AddCrosses(double at, bool vertical)
                {
                    if (vertical)
                    {
                        marks.Add(new SheetMark(SheetMarkKind.Cross, left - band / 2, at, Size: size));
                        marks.Add(new SheetMark(SheetMarkKind.Cross, right + band / 2, at, Size: size));
                    }
                    else
                    {
                        marks.Add(new SheetMark(SheetMarkKind.Cross, at, top + band / 2, Size: size));
                        marks.Add(new SheetMark(SheetMarkKind.Cross, at, bottom - band / 2, Size: size));
                    }
                }
            }
            if (poster.Labels)
                marks.Add(new SheetMark(SheetMarkKind.Text, left + (g.TileWidth - g.StepX) + 16, bottom - band + 4, Size: 7,
                    Text: $"{PosterGrid.Label(c, r)} · {page} · {g.Columns} × {g.Rows}"));
        }
        return new PrintSheet(sheetW, sheetH, [placement], Marks: marks);
    }

    private static (double Width, double Height) Valid((double Width, double Height) page) =>
        page.Width is > 0.5 and < 1e6 && page.Height is > 0.5 and < 1e6 ? page : (595.28, 841.89);
}
