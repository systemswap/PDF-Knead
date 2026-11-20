using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Printing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>Printing: direct drawing path, flattening only where needed, layers when printing, copies, lifetime, CUPS path.</summary>
public sealed class PrintRoundTests
{
    /// <summary>Guard: preview/Windows (drawn directly) and CUPS/Save as PDF (print PDF) show the same sheets.</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    public void DirectDrawing_EqualsTheSheetPdf(int perSheet, bool booklet)
    {
        using var original = PdfDocument.Open(TestPdf.Simple(5).Build());
        var items = PageItem.From(original);
        items[2] = items[2].WithElements([new TextElement { Rect = new PdfRectangle(100, 400, 400, 450), Text = "Max Mustermann", FontSize = 24 }]);
        items[3] = items[3].Rotated(90);
        using var pages = PrintPages.Prepare(original, items);
        Assert.Equal(1, pages.Flattened);
        var indices = Enumerable.Range(0, pages.Count).ToList();
        var sizes = indices.Select(pages.Size).ToList();
        var options = new PrintLayoutOptions { PagesPerSheet = perSheet, Borders = perSheet > 1 };
        var layout = booklet ? Booklet.Arrange(sizes, options, new BookletOptions()).Sheets : PrintLayout.Arrange(sizes, options);
        using var sheets = new PrintJobSheets(pages, layout, indices);
        using var pdf = new MemoryStream();
        sheets.WritePdf([.. Enumerable.Range(0, sheets.SheetCount)], pdf, CancellationToken.None);
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
        Assert.Contains("Mustermann", PageText.Create(PageInterpreter.Build(written, written.Pages[booklet ? 2 : perSheet == 4 ? 0 : 2]).Text).Text);
    }

    [Fact]
    public void Prepare_TakesPagesWithoutAnythingStraightFromTheFile()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(3).Build());
        var items = PageItem.From(original);
        using (var plain = PrintPages.Prepare(original, items))
        {
            Assert.Equal(0, plain.Flattened);
            Assert.All(plain.Pages, p => Assert.Same(original, p.Document));
        }
        items[1] = items[1].WithElements([new TextElement { Rect = new PdfRectangle(60, 300, 260, 330), Text = "Notiz", FontSize = 12 }]);
        using var marked = PrintPages.Prepare(original, items, CancellationToken.None);
        Assert.Equal(1, marked.Flattened);
        Assert.Same(original, marked.Pages[0].Document);
        Assert.NotSame(original, marked.Pages[1].Document);
        Assert.Contains("Notiz", PageText.Create(PageInterpreter.Build(marked.Pages[1].Document, marked.Pages[1].Page).Text).Text);
    }

    /// <summary>Layers "print only" (watermarks of common PDF editors) and "screen only".</summary>
    [Fact]
    public void Layers_PrintStateDecidesWhatIsPrinted()
    {
        var pdf = TestPdf.Simple(1);
        PdfDictionary Group(string name, string view, string print) => TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new PdfString(Encoding.ASCII.GetBytes(name))),
            ("Usage", TestPdf.Dict(("View", TestPdf.Dict(("ViewState", TestPdf.N(view)))), ("Print", TestPdf.Dict(("PrintState", TestPdf.N(print)))))));
        var watermark = pdf.Add(Group("Wasserzeichen", "OFF", "ON"));
        var screen = pdf.Add(Group("Bildschirm", "ON", "OFF"));
        PdfDictionary Auto(string evt, string category) => TestPdf.Dict(("Event", TestPdf.N(evt)), ("Category", new PdfArray([TestPdf.N(category)])),
            ("OCGs", new PdfArray([TestPdf.Ref(watermark), TestPdf.Ref(screen)])));
        ((PdfDictionary)pdf.Get(1)).Set(PdfName.Get("OCProperties"), TestPdf.Dict(
            ("OCGs", new PdfArray([TestPdf.Ref(watermark), TestPdf.Ref(screen)])),
            ("D", TestPdf.Dict(("OFF", new PdfArray([TestPdf.Ref(watermark)])), ("AS", new PdfArray([Auto("View", "View"), Auto("Print", "Print")]))))));
        // Page: red area in the watermark layer, green one in the screen layer
        var content = pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes(
            "/OC /W BDC 1 0 0 rg 50 50 100 100 re f EMC /OC /S BDC 0 1 0 rg 200 50 100 100 re f EMC")));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Contents, TestPdf.Ref(content));
        page.Set(PdfName.Resources, TestPdf.Dict(("Properties", TestPdf.Dict(("W", TestPdf.Ref(watermark)), ("S", TestPdf.Ref(screen))))));
        using var doc = PdfDocument.Open(pdf.Build());

        int Fills(DisplayListPeek list) => list.Fills;
        Assert.Equal(1, Fills(new(PageInterpreter.Build(doc, doc.Pages[0]))));                    // screen: green only
        Assert.Equal(1, Fills(new(PageInterpreter.Build(doc, doc.Pages[0], forPrint: true))));    // print: red only
        using var pages = PrintPages.Prepare(doc, PageItem.From(doc));
        var layout = PrintLayout.Arrange([pages.Size(0)], new PrintLayoutOptions { Scaling = PrintScaling.ActualSize, Margin = 0 });
        using var sheets = new PrintJobSheets(pages, layout, [0]);
        using (var direct = sheets.Render(0, 1))
        {
            var h = direct.Height;
            Assert.Equal(new SKColor(255, 0, 0), direct.GetPixel(100, h - 100));   // watermark printed
            Assert.Equal(SKColors.White, direct.GetPixel(250, h - 100));          // screen layer not printed
        }
        // The print PDF carries the print state as its default, so every reader and every printer shows it that way
        using var output = new MemoryStream();
        sheets.WritePdf([0], output, CancellationToken.None);
        using var written = PdfDocument.Open(output.ToArray());
        using var shown = RasterExport.Render(PageItem.From(written)[0], 1, new ImageCache());
        Assert.Equal(new SKColor(255, 0, 0), shown.GetPixel(100, shown.Height - 100));
        Assert.Equal(SKColors.White, shown.GetPixel(250, shown.Height - 100));
    }

    /// <summary>Counts the filled areas of a display list.</summary>
    private readonly struct DisplayListPeek(Core.Pdf.Graphics.DisplayList list)
    {
        public int Fills { get; } = Fill(list);

        private static int Fill(Core.Pdf.Graphics.DisplayList list)
        {
            var n = 0;
            foreach (var item in list.Items.Span) if (item.Kind == Core.Pdf.Graphics.DisplayItemKind.Fill) n++;
            return n;
        }
    }

    [Fact]
    public void Copies_DuplexKeepsFrontsOnFronts()
    {
        Assert.Equal([0, 1, 2], Printers.CopyOrder([0, 1, 2], 1, collate: true, duplex: true));
        Assert.Equal([0, 1, 2, -1, 0, 1, 2, -1], Printers.CopyOrder([0, 1, 2], 2, collate: true, duplex: true));
        Assert.Equal([0, 1, 0, 1, 2, -1, 2, -1], Printers.CopyOrder([0, 1, 2], 2, collate: false, duplex: true));
        Assert.Equal([0, 0, 1, 1, 2, 2], Printers.CopyOrder([0, 1, 2], 2, collate: false, duplex: false));
        Assert.Equal([0, 1, 2, 0, 1, 2], Printers.CopyOrder([0, 1, 2], 2, collate: true, duplex: false));
    }

    /// <summary>Releasing waits until nobody reads any more; otherwise reading from the already unmapped file crashes.</summary>
    [Fact]
    public void PrintPages_FreedOnlyWhenNobodyReads()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(2).Build());
        var items = PageItem.From(original);
        items[0] = items[0].WithElements([new TextElement { Rect = new PdfRectangle(100, 400, 400, 450), Text = "Max", FontSize = 20 }]);
        var pages = PrintPages.Prepare(original, items);
        Assert.True(pages.TryAcquire());
        pages.Dispose();
        Assert.False(pages.Closed.IsCompleted);
        Assert.False(pages.TryAcquire());
        // The reader can still draw
        var layout = PrintLayout.Arrange([pages.Size(0)], new PrintLayoutOptions());
        using (var sheets = new PrintJobSheets(pages, layout, [0]))
            Assert.Throws<OperationCanceledException>(() => sheets.Render(0, 0.2));   // new readers are refused
        Assert.NotNull(pages.Pages[0].Document.Pages[0].Dictionary);
        pages.Release();
        Assert.True(pages.Closed.IsCompleted);
    }

    /// <summary>"Low resolution only" under CUPS: images only, no text.</summary>
    [Fact]
    public void RasterPdf_ForLowResolution_HasNoText()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(3).Build());
        using var pages = PrintPages.Prepare(original, PageItem.From(original));
        var layout = PrintLayout.Arrange([.. Enumerable.Range(0, 3).Select(pages.Size)], new PrintLayoutOptions());
        using var sheets = new PrintJobSheets(pages, layout, [0, 1, 2]);
        using var output = new MemoryStream();
        sheets.WriteRasterPdf([0, 1, 2, -1], 72, output, CancellationToken.None);
        using var raster = PdfDocument.Open(output.ToArray());
        Assert.Equal(4, raster.PageCount);
        Assert.All(raster.Pages, p => Assert.Empty(PageInterpreter.Build(raster, p).Text));
    }

    /// <summary>CUPS path: if the command exits early, its result is returned (not "pipe closed"); cancelling terminates it.</summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void RunWriting_EarlyExitAndCancel()
    {
        var (shell, quit, wait) = OperatingSystem.IsWindows()
            ? ("cmd.exe", (string[])["/c", "exit 3"], (string[])["/c", "ping -n 30 127.0.0.1 >nul"])
            : ("/bin/sh", ["-c", "exit 3"], ["-c", "sleep 30"]);
        var data = new byte[1 << 20];
        var (code, _) = Printers.RunWriting(shell, quit, s =>
        {
            for (var i = 0; i < 64; i++) s.Write(data);
        }, CancellationToken.None);
        Assert.Equal(3, code);

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var started = DateTime.UtcNow;
        Assert.ThrowsAny<OperationCanceledException>(() => Printers.RunWriting(shell, wait, s =>
        {
            for (var i = 0; i < 1024; i++) s.Write(data);   // fills up because nobody reads
        }, cancel.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20 * TestKind.Factor));
    }
}
