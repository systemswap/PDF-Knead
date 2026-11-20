using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Printing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>Poster: one page enlarged onto several sheets, with a glue margin and marks. See KONZEPT.md, section 5.11.</summary>
public sealed class PosterTests
{
    private const double A4W = 595.28, A4H = 841.89;

    /// <summary>A4 to A2 (twice the size) from A4 sheets: the grid covers the whole page, neighbours overlap exactly by the glue margin,
    /// nothing reaches into the non-printable margin, and landscape sheets are chosen when fewer are needed.</summary>
    [Fact]
    public void A4ToA2_CoversThePage_NeighboursOverlap()
    {
        var overlap = 10 * PageFormat.PointsPerMillimeter;
        var options = new PrintLayoutOptions { Margin = 12 };
        var poster = Poster.Arrange([(A4W, A4H)], options, new PosterOptions(Overlap: overlap));
        var grid = Assert.Single(poster.Grids);
        Assert.Equal(2, grid.Scale, 2);
        Assert.Equal(grid.SheetCount, poster.Sheets.Count);
        Assert.Equal(8, grid.SheetCount);   // in portrait it would be 3 × 3
        Assert.True(poster.Sheets[0].Landscape);
        Assert.Equal(grid.TileWidth - grid.StepX, overlap, 6);
        Assert.True(grid.CoverWidth >= A4W * 2 - 1e-6 && grid.CoverHeight >= A4H * 2 - 1e-6);
        Assert.True(grid.OffsetX >= 0 && grid.OffsetY >= 0);
        Assert.All(poster.Sheets, s => Assert.False(s.Clips(12)));
        Assert.All(poster.Sheets, s => Assert.NotEmpty(s.Marks!));
        // Registration crosses in the middle of the glue margin: on sheet A1 at the right and on B1 at the left, at the same place on the poster
        static IEnumerable<SheetMark> TopCrosses(PrintSheet s) =>
            s.Marks!.Where(m => m.Kind == SheetMarkKind.Cross && m.Y > s.Height - 12 - PosterOptions.MarkBand);
        var right = Assert.Single(TopCrosses(poster.Sheets[0])).X;
        var left = Assert.Single(TopCrosses(poster.Sheets[1])).X;
        Assert.Equal(right - grid.StepX, left, 6);
        Assert.Contains(poster.Sheets[^1].Marks!, m => m.Text?.StartsWith("B4 · ") == true);
    }

    [Fact]
    public void Labels_LikeASpreadsheet()
    {
        Assert.Equal("A1", PosterGrid.Label(0, 0));
        Assert.Equal("B2", PosterGrid.Label(1, 1));
        Assert.Equal("Z1", PosterGrid.Label(25, 0));
        Assert.Equal("AA3", PosterGrid.Label(26, 2));
    }

    /// <summary>Too large (2000 % on A5): no sheets; the notice comes from the window.</summary>
    [Fact]
    public void TooManySheets_GivesNone()
    {
        var poster = Poster.Arrange([(A4W, A4H)], new PrintLayoutOptions { PaperWidth = 419.53, PaperHeight = A4W }, new PosterOptions(Percent: 2000));
        Assert.True(poster.TooLarge);
        Assert.Empty(poster.Sheets);
    }

    /// <summary>
    /// The tiles put together give the enlarged page: every pixel of every tile sits at its place in the poster (dimensions chosen
    /// so that everything falls on whole pixels; without marks and margin). Identical except for the anti-aliasing of a few curve
    /// pixels near the tile edge: Skia anti-aliases curves slightly differently depending on the drawing area (measured 35 of
    /// 240 000 pixels, at most 32 levels).
    /// </summary>
    [Fact]
    public void Tiles_PutTogether_GiveThePage()
    {
        var pdf = TestPdf.Simple(1);
        ((Core.Pdf.Objects.PdfDictionary)pdf.Get(5)).Set(Core.Pdf.Objects.PdfName.MediaBox, TestPdf.Box(0, 0, 300, 400));
        using var doc = PdfDocument.Open(pdf.Build());
        using var pages = PrintPages.Prepare(doc, PageItem.From(doc));
        Assert.Equal((300.0, 400.0), pages.Size(0));
        var poster = Poster.Arrange([pages.Size(0)], new PrintLayoutOptions { PaperWidth = 200, PaperHeight = 300, Margin = 0, Orientation = PrintOrientation.Portrait },
            new PosterOptions(Percent: 100, Overlap: 20, CutMarks: false, RegistrationMarks: false, Labels: false));
        var grid = Assert.Single(poster.Grids);
        Assert.Equal((2, 2, 40.0, 90.0), (grid.Columns, grid.Rows, grid.OffsetX, grid.OffsetY));
        using var sheets = new PrintJobSheets(pages, poster.Sheets, [0]);

        var whole = new PrintSheet(grid.CoverWidth, grid.CoverHeight,
            [new SheetPlacement(0, grid.OffsetX, grid.CoverHeight - grid.OffsetY - 400, 1, 300, 400)]);
        using var reference = pages.Render(whole, 1);
        int differing = 0, worst = 0, ink = 0, total = 0;
        for (var sheet = 0; sheet < sheets.SheetCount; sheet++)
        {
            using var tile = sheets.Render(sheet, 1);
            var (column, row) = (sheet % grid.Columns, sheet / grid.Columns);
            for (var y = 0; y < tile.Height; y++)
                for (var x = 0; x < tile.Width; x++)
                {
                    var (a, b) = (tile.GetPixel(x, y), reference.GetPixel(column * (int)grid.StepX + x, row * (int)grid.StepY + y));
                    var d = Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue)));
                    if (d > 2) differing++;
                    worst = Math.Max(worst, d);
                    if (a.Red + a.Green + a.Blue < 600) ink++;
                    total++;
                }
        }
        Assert.True(differing <= total / 2000, $"{differing} von {total} Pixeln anders");
        Assert.True(worst <= 48, $"größte Abweichung {worst}");
        Assert.True(ink > 1000, "die Kacheln zeigen Inhalt");
    }

    /// <summary>The print PDF (CUPS, "Save as PDF") shows the same sheets as the preview and Windows printing, including clipping and marks.</summary>
    [Fact]
    public void SheetPdf_EqualsDirectDrawing()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        using var pages = PrintPages.Prepare(doc, PageItem.From(doc));
        var poster = Poster.Arrange([pages.Size(0)], new PrintLayoutOptions { Margin = 12 }, new PosterOptions(Percent: 150), "Seite {0}");
        using var sheets = new PrintJobSheets(pages, poster.Sheets, [0]);
        using var pdf = new MemoryStream();
        sheets.WritePdf([.. Enumerable.Range(0, sheets.SheetCount)], pdf, CancellationToken.None);
        var raw = System.Text.Encoding.Latin1.GetString(pdf.ToArray());
        Assert.Contains("/BaseFont/Helvetica", raw);
        using var written = PdfDocument.Open(pdf.ToArray());
        Assert.Equal(sheets.SheetCount, written.PageCount);
        var images = new ImageCache();
        for (var i = 0; i < sheets.SheetCount; i++)
        {
            using var direct = sheets.Render(i, 0.5);
            using var fromPdf = RasterExport.Render(PageItem.From(written)[i], 0.5, images);
            Assert.Equal((direct.Width, direct.Height), (fromPdf.Width, fromPdf.Height));
            long diff = 0;
            for (var y = 0; y < direct.Height; y++)
                for (var x = 0; x < direct.Width; x++)
                {
                    var (a, b) = (direct.GetPixel(x, y), fromPdf.GetPixel(x, y));
                    diff += Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
                }
            var mean = diff / (3.0 * direct.Width * direct.Height);
            Assert.True(mean < 1.5, $"Blatt {i + 1}: mittlere Abweichung {mean:0.00}");
        }
        Assert.Contains("A1", Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.Build(written, written.Pages[0]).Text).Text);
    }
}
