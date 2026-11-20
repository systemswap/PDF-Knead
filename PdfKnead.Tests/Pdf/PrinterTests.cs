using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Printing;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Querying printers and printing. Real printing happens only with "Microsoft Print to PDF" into a file, never to a
/// physical printer. Without this driver (Linux, driver removed) the Windows tests pass without doing anything.
/// </summary>
public sealed class PrinterTests
{
    private const string PdfPrinter = "Microsoft Print to PDF";

    private static bool HasPdfPrinter => OperatingSystem.IsWindows() && Printers.List().Any(p => p.Name == PdfPrinter);

    [Fact]
    public void Windows_PdfPrinter_ReportsPapersAndMargin()
    {
        if (!HasPdfPrinter) return;
        var caps = Printers.Capabilities(PdfPrinter);
        var a4 = Assert.Single(caps.Papers, p => Math.Abs(p.Width - 595.3) < 2 && Math.Abs(p.Height - 841.9) < 2);
        Assert.Contains(caps.Papers, p => Math.Abs(p.Width - 612) < 2 && Math.Abs(p.Height - 792) < 2);   // Letter
        Assert.NotEmpty(a4.Label);
        Assert.True(caps.MaxCopies >= 1);
        var margin = Printers.Margin(PdfPrinter, new PrinterSettings { Paper = a4.Value });
        Assert.NotNull(margin);
        Assert.InRange(margin!.Value, 0, 72);
    }

    /// <summary>Three sheets (one in landscape) printed into a file: page count, orientation and appearance match.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Windows_PrintToPdfPrinter_SheetsArriveAsDrawn(bool gray)
    {
        if (!HasPdfPrinter) return;
        using var original = PdfDocument.Open(TestPdf.Simple(3).Build());
        using var pages = PrintPages.Prepare(original, PageItem.From(original));
        var sizes = Enumerable.Range(0, 3).Select(pages.Size).ToList();
        sizes[1] = (842, 595);   // a landscape sheet in between
        var layout = PrintLayout.Arrange(sizes, new PrintLayoutOptions { Margin = 0 });
        using var sheets = new PrintJobSheets(pages, layout, [0, 1, 2]);
        var caps = Printers.Capabilities(PdfPrinter);
        var a4 = caps.Papers.First(p => Math.Abs(p.Width - 595.3) < 2 && Math.Abs(p.Height - 841.9) < 2);
        var target = Path.Combine(Directory.CreateTempSubdirectory("pdf-knead-test-").FullName, "gedruckt.pdf");
        var done = 0;
        Printers.Print(new PrintJob(PdfPrinter, "PDF-Knead Test", sheets, [0, 1, 2], new PrinterSettings { Paper = a4.Value, Grayscale = gray }, Dpi: 150,
            OutputFile: target), new Progress<(int Done, int Total)>(n => done = n.Done), CancellationToken.None);

        // The driver writes the file once the job has passed through the queue
        var until = DateTime.UtcNow.AddSeconds(30);
        while ((!File.Exists(target) || new FileInfo(target).Length == 0 || !CanOpen(target)) && DateTime.UtcNow < until) Thread.Sleep(100);
        using var printed = PdfDocument.Open(File.ReadAllBytes(target));
        Assert.Equal(3, printed.PageCount);
        Assert.True(printed.Pages[0].Height > printed.Pages[0].Width);
        Assert.True(printed.Pages[1].Width > printed.Pages[1].Height);   // orientation per sheet
        // Appearance: sheet 1 of the printout against the preview (the driver sets it as an image, not pixel-exact)
        var images = new ImageCache();
        using var expected = sheets.Render(0, 0.25);
        if (gray) PdfKnead.ViewModels.PrintViewModel.ToGray(expected);
        using var actual = RasterExport.Render(PageItem.From(printed)[0], 0.25, images);
        long diff = 0;
        var (w, h) = (Math.Min(expected.Width, actual.Width), Math.Min(expected.Height, actual.Height));
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                diff += Math.Abs(expected.GetPixel(x, y).Red - actual.GetPixel(x, y).Red);
        Assert.True(diff / (double)(w * h) < 12, $"mittlere Abweichung {diff / (double)(w * h):0.0}");
    }

    private static bool CanOpen(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    [Fact]
    public void Cups_ParsesPpdAndIppEverywhereOptions()
    {
        const string ppd = """
            PageSize/Media Size: Letter *A4 A3 A5 Legal Custom.WIDTHxHEIGHT
            InputSlot/Media Source: *Auto Tray1 Tray2 Manual
            Duplex/2-Sided Printing: *None DuplexNoTumble DuplexTumble
            ColorModel/Color Mode: *Gray RGB
            Resolution/Resolution: 300dpi *600dpi 1200dpi
            StapleLocation/Staple: *None SinglePortrait
            PageRegion/Page Region: Letter A4
            """;
        var caps = CupsPrinting.Parse(ppd);
        Assert.Equal(["PageSize=Letter", "PageSize=A4", "PageSize=A3", "PageSize=A5", "PageSize=Legal"], caps.Papers.Select(p => p.Value));
        Assert.Equal("PageSize=A4", caps.DefaultPaper);
        Assert.Equal(595.28, caps.Papers[1].Width, 1);
        Assert.Equal(4, caps.Trays.Count);
        Assert.Equal("InputSlot=Auto", caps.DefaultTray);
        Assert.True(caps.Duplex);
        Assert.True(caps.Color);
        Assert.Equal("Resolution=600dpi", caps.DefaultQuality);
        var staple = Assert.Single(caps.Extra);   // PDF Knead handles PageRegion itself
        Assert.Equal(("StapleLocation", "Staple", "None"), (staple.Key, staple.Label, staple.Default));

        const string ipp = """
            media/Media: *iso_a4_210x297mm na_letter_8.5x11in iso_a5_148x210mm
            sides/2-Sided Printing: *one-sided
            print-color-mode/Print Color Mode: *monochrome
            """;
        var everywhere = CupsPrinting.Parse(ipp);
        Assert.Equal(["A4", "Letter", "A5"], everywhere.Papers.Select(p => p.Label));
        Assert.Equal(612, everywhere.Papers[1].Width, 1);
        Assert.False(everywhere.Duplex);
        Assert.False(everywhere.Color);
    }

    [Fact]
    public void Cups_LpArguments_NoScaling_OptionsAsGiven()
    {
        var job = new PrintJob("Buero Drucker", "Max Mustermann; rm -rf", new NoSheets(), [0],
            new PrinterSettings
            {
                Paper = "PageSize=A4", Tray = "InputSlot=Tray2", Duplex = DuplexMode.ShortEdge, Grayscale = true, Copies = 2,
                Extra = new Dictionary<string, string> { ["StapleLocation"] = "SinglePortrait", ["bad key"] = "x" },
            });
        var args = CupsPrinting.Arguments(job, 2);
        Assert.Equal(["-d", "Buero Drucker", "-t", "Max Mustermann; rm -rf", "-n", "2"], args.Take(6));
        Assert.Contains("PageSize=A4", args);
        Assert.Contains("InputSlot=Tray2", args);
        Assert.Contains("sides=two-sided-short-edge", args);
        Assert.Contains("print-color-mode=monochrome", args);
        Assert.Contains("print-scaling=none", args);
        Assert.Contains("collate=true", args);
        Assert.Contains("StapleLocation=SinglePortrait", args);
        Assert.DoesNotContain(args, a => a.StartsWith("bad key"));
    }

    private sealed class NoSheets : ISheetSource
    {
        public int SheetCount => 0;
        public (double Width, double Height) SheetSize(int sheet) => (595, 842);
        public void RenderBand(int sheet, double pixelsPerPoint, int top, int width, int rows, Span<byte> pixels, int stride, bool gray, CancellationToken cancel) { }
        public void WritePdf(IReadOnlyList<int> sheets, Stream output, CancellationToken cancel) { }
        public void WriteRasterPdf(IReadOnlyList<int> sheets, int dpi, Stream output, CancellationToken cancel) { }
    }
}
