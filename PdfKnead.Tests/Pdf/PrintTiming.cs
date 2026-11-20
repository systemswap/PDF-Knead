using System.Diagnostics;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Printing;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Measurement (not part of the normal test run): printing with large files: 3000 pages, an A0 plan (if present in
/// TestPDFs), a scanned document. Enable with PDFKNEAD_MESSEN=1 (Release build); output with --logger "console;verbosity=detailed".
/// </summary>
public sealed class PrintTiming
{
    private static void Step(string name, Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalAllocatedBytes(true);
        var gen2 = GC.CollectionCount(2);
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"MESS {name,-34} {sw.ElapsedMilliseconds,7} ms  belegt {(GC.GetTotalAllocatedBytes(true) - before) / 1e6,8:0.0} MB  " +
                          $"gen2 {GC.CollectionCount(2) - gen2,3}  verwaltet {GC.GetTotalMemory(false) / 1e6,7:0} MB  privat {process.PrivateMemorySize64 / 1e6,6:0} MB");
    }

    // the largest of the user's own files (TestPDFs/privat/), usually a plan
    private static string? Plan() => TestPdfs.Private().OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();

    [Fact]
    public void Measure()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") is null) return;
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") == "plan")
        {
            if (Plan() is { } only) Run("A0-Plan", only);
            return;
        }
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") == "poster")
        {
            if (Plan() is { } plan0) RunPoster("A0-Plan als Poster", plan0);
            return;
        }
        var big = TestFiles.Write(TestPdf.Simple(pages: 3000, title: null).Build(XRefStyle.Stream), "gross.pdf");
        Run("3000 Seiten", big);
        if (Plan() is { } plan) Run("A0-Plan", plan);
        // Scan: 120 pages as JPEG at 300 dpi
        using (var simple = PdfDocument.Open(TestPdf.Simple(120, null).Build()))
        using (var raster = new MemoryStream())
        {
            RasterExport.Write(PageItem.From(simple), raster, new RasterSettings(Dpi: 300, JpegQuality: 92));
            Run("Scan 120 S. 300 dpi", TestFiles.Write(raster.ToArray(), "scan.pdf"));
        }
    }

    private static void Run(string label, string path)
    {
        Console.WriteLine($"MESS ===== {label} ({new FileInfo(path).Length / 1e6:0.0} MB)");
        using var doc = PdfDocument.Open(path);
        var items = PageItem.From(doc);
        PrintPages? prepared = null;
        Step("Vorbereiten (Fenster öffnen)", () => prepared = PrintPages.Prepare(doc, items));
        using var pages = prepared!;
        Console.WriteLine($"MESS   flachgelegt: {pages.Flattened} von {pages.Count} Seiten");
        var indices = Enumerable.Range(0, pages.Count).ToList();
        var sizes = indices.Select(pages.Size).ToList();
        foreach (var (name, perSheet, booklet) in new[] { ("1 pro Blatt", 1, false), ("4 pro Blatt", 4, false), ("Broschüre", 1, true) })
        {
            PrintJobSheets? arranged = null;
            List<PrintSheet> layout = [];
            Step($"Anordnen {name}", () =>
            {
                var options = new PrintLayoutOptions { PagesPerSheet = perSheet };
                layout = booklet ? Booklet.Arrange(sizes, options, new BookletOptions()).Sheets : PrintLayout.Arrange(sizes, options);
                arranged = new PrintJobSheets(pages, layout, indices);
            });
            using var sheets = arranged!;
            Console.WriteLine($"MESS   {layout.Count} Blätter");
            Step($"Vorschau Blatt 1 ({name})", () => sheets.Render(0, 1600 / Math.Max(sheets.SheetSize(0).Width, sheets.SheetSize(0).Height)).Dispose());
            if (perSheet == 1 && !booklet)
            {
                Step("Windows-Streifen Blatt 1, 600 dpi", () => Bands(sheets, 0, 600, gray: false));
                var count = Math.Min(20, sheets.SheetCount);
                Step($"Windows-Streifen {count} Blätter, 300 dpi", () =>
                {
                    for (var s = 0; s < count; s++) Bands(sheets, s, 300, gray: false);
                });
                Step($"dasselbe in Graustufen", () =>
                {
                    for (var s = 0; s < count; s++) Bands(sheets, s, 300, gray: true);
                });
                Step("PDF für CUPS (alle Blätter)", () =>
                {
                    using var output = new CountingStream();
                    sheets.WritePdf(Enumerable.Range(0, sheets.SheetCount).ToList(), output, CancellationToken.None);
                    Console.WriteLine($"MESS   PDF {output.Length / 1e6:0.0} MB");
                });
            }
        }
    }

    /// <summary>Poster: the first page at original size on A4, measuring arrangement, overview, all sheets as on Windows, and PDF. See KONZEPT.md, section 5.11.</summary>
    private static void RunPoster(string label, string path)
    {
        Console.WriteLine($"MESS ===== {label} ({new FileInfo(path).Length / 1e6:0.0} MB)");
        using var doc = PdfDocument.Open(path);
        using var pages = PrintPages.Prepare(doc, [PageItem.From(doc)[0]]);
        var size = pages.Size(0);
        Console.WriteLine($"MESS   Seite {size.Width / PageFormat.PointsPerMillimeter:0} × {size.Height / PageFormat.PointsPerMillimeter:0} mm");
        Poster? poster = null;
        PrintJobSheets? arranged = null;
        Step("Anordnen 100 % auf A4", () =>
        {
            poster = Poster.Arrange([size], new PrintLayoutOptions { Margin = 12 }, new PosterOptions(Percent: 100));
            arranged = new PrintJobSheets(pages, poster.Sheets, [0]);
        });
        using var sheets = arranged!;
        var grid = poster!.Grids[0];
        Console.WriteLine($"MESS   {grid.Columns} × {grid.Rows} = {sheets.SheetCount} Blätter");
        Step("Übersicht (ganze Seite, 1600 px)", () =>
            pages.Render(new PrintSheet(size.Width, size.Height, [new SheetPlacement(0, 0, 0, 1, size.Width, size.Height)]),
                1600 / Math.Max(size.Width, size.Height)).Dispose());
        Step("Vorschau Blatt 1", () => sheets.Render(0, 1600 / Math.Max(sheets.SheetSize(0).Width, sheets.SheetSize(0).Height)).Dispose());
        Step($"Windows-Streifen {sheets.SheetCount} Blätter, 300 dpi", () =>
        {
            for (var s = 0; s < sheets.SheetCount; s++) Bands(sheets, s, 300, gray: false);
        });
        Step("PDF für CUPS (alle Blätter)", () =>
        {
            using var output = new CountingStream();
            sheets.WritePdf(Enumerable.Range(0, sheets.SheetCount).ToList(), output, CancellationToken.None);
            Console.WriteLine($"MESS   PDF {output.Length / 1e6:0.0} MB");
        });
    }

    private static byte[]? _buffer;

    /// <summary>Works like WindowsPrinting.DrawSheet, only without a printer (one buffer for all sheets).</summary>
    private static void Bands(PrintJobSheets sheets, int sheet, int dpi, bool gray)
    {
        var (w, h) = sheets.SheetSize(sheet);
        var ppp = dpi / 72.0;
        var width = (int)Math.Ceiling(w * ppp);
        var height = (int)Math.Ceiling(h * ppp);
        var stride = (width * (gray ? 1 : 3) + 3) & ~3;
        var rows = Math.Clamp((16 << 20) / stride, 16, height);
        if (_buffer is null || _buffer.Length < stride * rows) _buffer = new byte[stride * rows];
        for (var top = 0; top < height; top += rows)
        {
            var n = Math.Min(rows, height - top);
            sheets.RenderBand(sheet, ppp, top, width, n, _buffer.AsSpan(0, stride * n), stride, gray, CancellationToken.None);
        }
    }

    /// <summary>Only counts the bytes (like the pipe to lp: nothing stays in memory).</summary>
    private sealed class CountingStream : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
    }
}
