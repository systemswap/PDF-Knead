using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Printing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>Distributing pages onto sheets and imposing them as a vector PDF.</summary>
public sealed class PrintTests
{
    private const double A4W = 595.28, A4H = 841.89;

    [Fact]
    public void OnePerSheet_FollowsThePage_ScalesAsChosen()
    {
        (double, double)[] pages = [(A4W, A4H), (792, 612), (300, 400), (2384, 3370)];   // A4, Letter landscape, small, A0
        var fit = PrintLayout.Arrange(pages, new PrintLayoutOptions { Margin = 12 });
        Assert.Equal(4, fit.Count);
        Assert.False(fit[0].Landscape);
        Assert.True(fit[1].Landscape);
        Assert.Equal((A4H, A4W), (fit[1].Width, fit[1].Height));
        Assert.All(fit, s => Assert.False(s.Clips(12)));
        Assert.True(fit[2].Placements[0].Scale > 1);   // fitting also enlarges
        Assert.Equal(Math.Min((A4W - 24) / 2384, (A4H - 24) / 3370), fit[3].Placements[0].Scale, 6);

        var shrink = PrintLayout.Arrange(pages, new PrintLayoutOptions { Scaling = PrintScaling.ShrinkLarge });
        Assert.Equal(1, shrink[2].Placements[0].Scale);
        Assert.True(shrink[3].Placements[0].Scale < 0.3);

        var actual = PrintLayout.Arrange(pages, new PrintLayoutOptions { Scaling = PrintScaling.ActualSize });
        Assert.All(actual, s => Assert.Equal(1, s.Placements[0].Scale));
        Assert.True(actual[3].Clips(12));   // A0 on A4: clipped, centred
        Assert.Equal((A4W - 2384) / 2, actual[3].Placements[0].X, 6);

        var custom = PrintLayout.Arrange(pages, new PrintLayoutOptions { Scaling = PrintScaling.Custom, Percent = 50 });
        Assert.All(custom, s => Assert.Equal(0.5, s.Placements[0].Scale));

        var portrait = PrintLayout.Arrange(pages, new PrintLayoutOptions { Orientation = PrintOrientation.Portrait });
        Assert.All(portrait, s => Assert.False(s.Landscape));
    }

    [Theory]
    [InlineData(2, true, 1, 2)]    // two portrait pages side by side on a landscape sheet
    [InlineData(4, false, 2, 2)]
    [InlineData(6, true, 2, 3)]
    [InlineData(9, false, 3, 3)]
    [InlineData(16, false, 4, 4)]
    public void SeveralPerSheet_GridAndOrientation(int perSheet, bool landscape, int rows, int cols)
    {
        var pages = Enumerable.Repeat((A4W, A4H), perSheet + 1).ToList();
        var sheets = PrintLayout.Arrange(pages, new PrintLayoutOptions { PagesPerSheet = perSheet, Margin = 10 });
        Assert.Equal(2, sheets.Count);
        Assert.Equal(landscape, sheets[0].Landscape);
        Assert.Equal(perSheet, sheets[0].Placements.Count);
        Assert.Single(sheets[1].Placements);
        Assert.Equal(cols, sheets[0].Placements.Select(p => Math.Round(p.X)).Distinct().Count());
        Assert.Equal(rows, sheets[0].Placements.Select(p => Math.Round(p.Y)).Distinct().Count());
        Assert.False(sheets[0].Clips(10));
        // Row by row: the second page to the right of the first (below it with a single column)
        var (first, second) = (sheets[0].Placements[0], sheets[0].Placements[1]);
        Assert.True(cols > 1 ? second.X > first.X && Math.Abs(second.Y - first.Y) < 0.01 : second.Y < first.Y);
        // No overlap
        foreach (var a in sheets[0].Placements)
            foreach (var b in sheets[0].Placements.Where(b => b != a))
                Assert.True(a.Right <= b.X + 0.01 || b.Right <= a.X + 0.01 || a.Top <= b.Y + 0.01 || b.Top <= a.Y + 0.01);

        if (perSheet >= 4)
        {
            var vertical = PrintLayout.Arrange(pages, new PrintLayoutOptions { PagesPerSheet = perSheet, Order = SheetOrder.Vertical });
            Assert.True(vertical[0].Placements[1].Y < vertical[0].Placements[0].Y);   // column by column: below
            Assert.Equal(vertical[0].Placements[0].X, vertical[0].Placements[1].X, 6);
        }
    }

    [Fact]
    public void Selection_RangeSubsetReverse()
    {
        Assert.Equal([0, 1, 2, 3, 4], PrintSelection.Pages(5, PrintRange.All, 0, null, null));
        Assert.Equal([2], PrintSelection.Pages(5, PrintRange.Current, 2, null, null));
        Assert.Equal([0, 1, 2, 6], PrintSelection.Pages(9, PrintRange.Custom, 0, "1-3, 7", null));
        Assert.Null(PrintSelection.Pages(9, PrintRange.Custom, 0, "1-30", null));
        Assert.Null(PrintSelection.Pages(9, PrintRange.Custom, 0, "", null));
        Assert.Equal([1, 4], PrintSelection.Pages(9, PrintRange.Selected, 0, null, [4, 1, 4, 99]));
        Assert.Equal([0, 2, 4], PrintSelection.Pages(5, PrintRange.All, 0, null, null, PrintSubset.Odd));
        Assert.Equal([3, 1], PrintSelection.Pages(5, PrintRange.All, 0, null, null, PrintSubset.Even, reverse: true));
    }

    /// <summary>Sheets look like the pages: actual size, with the rotated page 2 upright as well.</summary>
    [Fact]
    public void Sheets_LookLikeThePages_RotatedPageUpright()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(3).Build());
        var items = PageItem.From(original);
        using var flat = SheetPdf.Flatten(original, items);
        int[] indices = [0, 1, 2];
        var sheets = PrintLayout.Arrange([.. indices.Select(i => (flat.Pages[i].Width, flat.Pages[i].Height))],
            new PrintLayoutOptions { Scaling = PrintScaling.ActualSize, Margin = 0, PaperWidth = 612, PaperHeight = 792 });
        using var sheetDoc = SheetPdf.Build(flat, indices, sheets);
        Assert.Equal(3, sheetDoc.PageCount);
        Assert.False(sheetDoc.WasRepaired);
        Assert.True(sheetDoc.Pages[1].Width < sheetDoc.Pages[1].Height);   // Letter landscape rotated by 90°: stands upright

        var images = new ImageCache();
        for (var i = 0; i < 3; i++)
        {
            using var page = RasterExport.Render(items[i], 0.5, images);
            using var sheet = RasterExport.Render(PageItem.From(sheetDoc)[i], 0.5, images);
            var p = sheets[i].Placements[0];
            var dx = (int)Math.Round(p.X * 0.5);
            var dy = (int)Math.Round((sheets[i].Height - p.Top) * 0.5);
            Assert.True(Difference(page, sheet, dx, dy) < 6, $"Seite {i + 1}: {Difference(page, sheet, dx, dy):0.0}");
        }
    }

    /// <summary>Only what is needed: page tree, catalog and annotations of the source do not come along; fonts do.</summary>
    [Fact]
    public void SheetPdf_IsSelfContained_TextStaysVector()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(4).Build());
        using var flat = SheetPdf.Flatten(original, PageItem.From(original));
        int[] indices = [0, 1, 2, 3];
        var sheets = PrintLayout.Arrange([.. indices.Select(i => (flat.Pages[i].Width, flat.Pages[i].Height))],
            new PrintLayoutOptions { PagesPerSheet = 4, Borders = true });
        using var output = new MemoryStream();
        SheetPdf.Write(flat, indices, sheets, output);
        var bytes = output.ToArray();
        using var doc = PdfDocument.Open(bytes);
        Assert.False(doc.WasRepaired);
        Assert.Equal(1, doc.PageCount);
        Assert.Equal(1, Count(Encoding.Latin1.GetString(bytes), "/Type/Pages"));
        Assert.Equal(1, Count(Encoding.Latin1.GetString(bytes), "/Type/Font"));   // the shared font once
        var text = PageText.Create(PageInterpreter.Build(doc, doc.Pages[0]).Text).Text;
        for (var n = 1; n <= 4; n++) Assert.Contains($"Seite {n}", text);
    }

    /// <summary>Annotations: only those with the Print flag are printed, "print only" (NoView) as well, "do not print" not.</summary>
    [Fact]
    public void Annotations_OnlyThoseMarkedForPrinting()
    {
        var pdf = TestPdf.Simple(1);
        var page = 5;   // catalog, page tree, font, content, page
        PdfDictionary Stamp(double left, string color, int flags)
        {
            var form = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 60, 60))),
                Encoding.ASCII.GetBytes($"{color} rg 0 0 60 60 re f")));
            return TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Stamp")), ("Rect", TestPdf.Box(left, 20, left + 60, 80)),
                ("F", PdfInteger.Of(flags)), ("AP", TestPdf.Dict(("N", TestPdf.Ref(form)))));
        }
        ((PdfDictionary)pdf.Get(page)).Set(PdfName.Get("Annots"), new PdfArray(
            [Stamp(20, "1 0 0", 4), Stamp(120, "0 1 0", 0), Stamp(220, "0 0 1", 4 | 32), Stamp(320, "1 0 1", 4 | 2)]));
        using var original = PdfDocument.Open(pdf.Build());
        using var flat = SheetPdf.Flatten(original, PageItem.From(original));
        var sheets = PrintLayout.Arrange([(flat.Pages[0].Width, flat.Pages[0].Height)],
            new PrintLayoutOptions { Scaling = PrintScaling.ActualSize, Margin = 0 });
        using var sheetDoc = SheetPdf.Build(flat, [0], sheets);
        using var bitmap = RasterExport.Render(PageItem.From(sheetDoc)[0], 1, new ImageCache());
        SKColor At(double x) => bitmap.GetPixel((int)x, (int)(sheets[0].Height - 50));
        Assert.Equal(new SKColor(255, 0, 0), At(50));          // printed
        Assert.Equal(SKColors.White, At(150));                // do not print
        Assert.Equal(new SKColor(0, 0, 255), At(250));        // print only
        Assert.Equal(SKColors.White, At(350));                // hidden
    }

    /// <summary>
    /// Redactions are applied in one place only, in the document („Schwärzen zuerst“): printing refuses a mark left over instead of
    /// redacting along the way. After applying, the page prints black and the text underneath is missing from the print data.
    /// </summary>
    [Fact]
    public void Redactions_OnlyAppliedInTheDocument_ThenPrintBlack_TextGone()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(1).Build());
        var items = PageItem.From(original);
        items[0] = items[0].WithElements([new RedactionElement { Rect = new PdfRectangle(60, 740, 300, 800) }]);
        Assert.Throws<PdfException>(() => SheetPdf.Flatten(original, items, SkiaImageCodec.Instance));
        Assert.Throws<PdfException>(() => PrintPages.Prepare(original, items));

        var (applied, pages) = InDocumentRedaction.Apply(original, items, new PdfSaveOptions { ImageCodec = SkiaImageCodec.Instance }, null);
        using var redacted = applied;
        using var print = PrintPages.Prepare(redacted, pages);
        var page = print.Pages[0];
        Assert.DoesNotContain("Seite 1", PageText.Create(PageInterpreter.Build(page.Document, page.Page).Text).Text);
        var sheets = PrintLayout.Arrange([(page.Page.Width, page.Page.Height)], new PrintLayoutOptions { Scaling = PrintScaling.ActualSize, Margin = 0 });
        using var sheetDoc = SheetPdf.Build(page.Document, [0], sheets);
        using var bitmap = RasterExport.Render(PageItem.From(sheetDoc)[0], 1, new ImageCache());
        Assert.Equal(SKColors.Black, bitmap.GetPixel(100, (int)(sheets[0].Height - 770)));
    }

    private static int Count(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>Mean difference (red, 0 … 255) of the page against its place on the sheet.</summary>
    private static double Difference(SKBitmap page, SKBitmap sheet, int dx, int dy)
    {
        long sum = 0, n = 0;
        for (var y = 1; y < page.Height - 1; y++)
            for (var x = 1; x < page.Width - 1; x++)
            {
                if (x + dx >= sheet.Width || y + dy >= sheet.Height || x + dx < 0 || y + dy < 0) continue;
                sum += Math.Abs(page.GetPixel(x, y).Red - sheet.GetPixel(x + dx, y + dy).Red);
                n++;
            }
        return sum / (double)Math.Max(1, n);
    }
}

/// <summary>Booklet imposition.</summary>
public sealed class BookletTests
{
    private static readonly PrintLayoutOptions A4 = new() { Margin = 0 };

    private static List<(int Left, int Right)> Sides(Booklet booklet) => [.. booklet.Sheets.Select(s =>
    {
        var left = s.Placements.FirstOrDefault(p => p.X < s.Width / 2)?.Page ?? -1;
        var right = s.Placements.FirstOrDefault(p => p.X >= s.Width / 2)?.Page ?? -1;
        return (left, right);
    })];

    private static (double, double)[] Pages(int n) => [.. Enumerable.Repeat((419.53, 595.28), n)];   // A5

    [Fact]
    public void EightPages_ClassicOrder_A4Landscape()
    {
        var booklet = Booklet.Arrange(Pages(8), A4, new BookletOptions());
        // 8|1, 2|7, 6|3, 4|5 (page numbers from 1)
        Assert.Equal([(7, 0), (1, 6), (5, 2), (3, 4)], Sides(booklet));
        Assert.All(booklet.Sheets, s => Assert.Equal((841.89, 595.28), (s.Width, s.Height)));
        // A5 fits exactly into one half of A4 landscape
        Assert.All(booklet.Sheets.SelectMany(s => s.Placements), p => Assert.Equal(1, p.Scale, 2));
        // Reading order: page 1 on the right of sheet side 0, page 2 on the left of sheet side 1
        Assert.Equal(new BookletPage(0, 0, true), booklet.Pages[0]);
        Assert.Equal(new BookletPage(1, 1, false), booklet.Pages[1]);
        Assert.Equal(new BookletPage(7, 0, false), booklet.Pages[7]);
    }

    [Fact]
    public void FivePages_BlanksBeforeTheBackCover_OrAtTheEnd()
    {
        var before = Booklet.Arrange(Pages(5), A4, new BookletOptions());
        Assert.Equal([(4, 0), (1, -1), (-1, 2), (3, -1)], Sides(before));
        Assert.Equal([0, 1, 2, 3, -1, -1, -1, 4], before.Pages.Select(p => p.Page));
        var after = Booklet.Arrange(Pages(5), A4, new BookletOptions(BlanksBeforeBack: false));
        Assert.Equal([0, 1, 2, 3, 4, -1, -1, -1], after.Pages.Select(p => p.Page));
        Assert.Equal((-1, 0), Sides(after)[0]);
    }

    [Fact]
    public void RightBinding_Mirrors()
    {
        var booklet = Booklet.Arrange(Pages(4), A4, new BookletOptions(RightBinding: true));
        Assert.Equal([(0, 3), (2, 1)], Sides(booklet));
        Assert.False(booklet.Pages[0].RightHalf);
    }

    [Fact]
    public void Signatures_EachFoldedOnItsOwn()
    {
        var booklet = Booklet.Arrange(Pages(12), A4, new BookletOptions(SheetsPerBooklet: 1));
        Assert.Equal([(3, 0), (1, 2), (7, 4), (5, 6), (11, 8), (9, 10)], Sides(booklet));
    }

    [Fact]
    public void GutterAndCreep_MoveThePages()
    {
        var plain = Booklet.Arrange(Pages(8), A4 with { Margin = 10 }, new BookletOptions());
        var gutter = Booklet.Arrange(Pages(8), A4 with { Margin = 10 }, new BookletOptions(Gutter: 20));
        var right = gutter.Sheets[0].Placements.First(p => p.X > 400);
        var left = gutter.Sheets[0].Placements.First(p => p.X < 400);
        Assert.True(right.X >= 841.89 / 2 + 10 - 0.01);
        Assert.True(left.Right <= 841.89 / 2 - 10 + 0.01);
        Assert.True(right.Scale < plain.Sheets[0].Placements[0].Scale);
        Assert.False(gutter.Sheets[0].Clips(10));

        // Creep: the inner sheet moves towards the fold, the outer one does not
        var creep = Booklet.Arrange(Pages(8), A4, new BookletOptions(Gutter: 10, Creep: 1));
        var flat = Booklet.Arrange(Pages(8), A4, new BookletOptions(Gutter: 10));
        Assert.False(creep.CreepLimited);
        // More than there is room up to the fold: limited, the pages stay on their side of the fold
        var much = Booklet.Arrange(Pages(8), A4, new BookletOptions(Gutter: 10, Creep: 20));
        Assert.True(much.CreepLimited);
        Assert.All(much.Sheets.SelectMany(s => s.Placements), p => Assert.True(p.X >= 841.89 / 2 - 0.01 || p.Right <= 841.89 / 2 + 0.01));
        Assert.Equal(flat.Sheets[0].Placements[0].X, creep.Sheets[0].Placements[0].X, 6);
        Assert.Equal(flat.Sheets[2].Placements[0].X + 1, creep.Sheets[2].Placements[0].X, 6);   // left → to the right
        Assert.Equal(flat.Sheets[2].Placements[1].X - 1, creep.Sheets[2].Placements[1].X, 6);   // right → to the left
    }

    /// <summary>A4 pages as an A5 booklet on A4: scaled down, front and back match, the file is valid.</summary>
    [Fact]
    public void A4Document_AsBooklet_SheetPdf()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(6).Build());
        using var flat = SheetPdf.Flatten(original, PageItem.From(original));
        int[] indices = [0, 1, 2, 3, 4, 5];
        var booklet = Booklet.Arrange([.. indices.Select(i => (flat.Pages[i].Width, flat.Pages[i].Height))], new PrintLayoutOptions(), new BookletOptions());
        Assert.Equal(4, booklet.Sheets.Count);
        Assert.InRange(booklet.Sheets[0].Placements.First(p => p.Page == 0).Scale, 0.66, 0.71);
        using var doc = SheetPdf.Build(flat, indices, booklet.Sheets);
        Assert.Equal(4, doc.PageCount);
        Assert.True(doc.Pages[0].Width > doc.Pages[0].Height);
        var text = PageText.Create(PageInterpreter.Build(doc, doc.Pages[0]).Text).Text;
        Assert.Contains("Seite 1", text);
        Assert.Contains("Seite 6", text);
    }
}
