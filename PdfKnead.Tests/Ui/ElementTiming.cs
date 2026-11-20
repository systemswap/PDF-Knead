using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Rendering;
using PdfKnead.Tests.Fonts;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Measurement (not part of the normal test run): shaping and typing signatures,
/// elements in large documents, drawing and saving with elements. Enable with
/// PDFKNEAD_MESSEN=1; show the output with --logger "console;verbosity=detailed" (lines starting with "MESS").
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class ElementTiming
{
    private static void Report(string name, Action action, int repeat = 1)
    {
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < repeat; i++) action();
        Console.WriteLine($"MESS {name}: {sw.Elapsed.TotalMilliseconds / repeat:0.00} ms je Mal, belegt {(GC.GetAllocatedBytesForCurrentThread() - alloc) / repeat >> 10} KB je Mal");
    }

    [Fact]
    public void Signatures()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") is null) return;
        var page = BlankPage.Create(2000, 2000).Page;
        SignatureElement Make(string font, string name) => new() { Rect = new PdfRectangle(100, 100, 520, 160), Name = name, FontFile = font };

        // first build per font (parse the font, shape, outlines or image)
        var start = GC.GetTotalMemory(true);
        var keep = new List<PageElement>();
        foreach (var font in EmbeddedFonts.Handwriting)
        {
            var sw = Stopwatch.StartNew();
            keep.Add(Make(font, "Max Mustermann"));
            ElementOverlay.List(page, keep[^1]);
            Console.WriteLine($"MESS erster Aufbau {font}: {sw.Elapsed.TotalMilliseconds:0.0} ms");
        }
        foreach (var font in Enum.GetValues<ElementFont>())
        {
            keep.Add(new TextElement { Rect = new PdfRectangle(100, 100, 400, 160), Text = "Max Mustermann", Font = font });
            ElementOverlay.List(page, keep[^1]);
        }
        Console.WriteLine($"MESS gehalten nach erstem Aufbau aller Handschriften und Textschriften (nach GC): {(GC.GetTotalMemory(true) - start) >> 10} KB");
        // all letters in a colour font: glyph images
        var alphabet = Make(TestColorFonts.Svg, "ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz ÄÖÜäöüß 0123456789");
        ElementOverlay.List(page, alphabet);
        keep.Add(alphabet);
        Console.WriteLine($"MESS gehalten mit ganzem Alphabet in der Farbschrift (nach GC): {(GC.GetTotalMemory(true) - start) >> 10} KB");
        GC.KeepAlive(keep);

        // shaping alone
        var colorFont = EmbeddedFonts.HandwritingOpenType(TestColorFonts.Svg);
        var patrick = EmbeddedFonts.HandwritingOpenType("PatrickHand-Regular.ttf");
        Report("Shaper Farbschrift „Max Mustermann“", () => Core.Fonts.Layout.TextShaper.For(colorFont).Shape("Max Mustermann"), 200);
        Report("Shaper PatrickHand „Max Mustermann“", () => Core.Fonts.Layout.TextShaper.For(patrick).Shape("Max Mustermann"), 200);

        // typing in the signature window: every key rebuilds all previews (appearance + display list)
        const string name = "Maximilian Mustermann-Schulze";
        var before = GC.GetTotalMemory(true);
        var times = new List<double>();
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        for (var n = 1; n <= name.Length; n++)
        {
            var sw = Stopwatch.StartNew();
            foreach (var font in EmbeddedFonts.Handwriting) ElementOverlay.List(page, Make(font, name[..n]));
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - alloc;
        Console.WriteLine($"MESS Tippen {name.Length} Tasten, {EmbeddedFonts.Handwriting.Count} Vorschauen je Taste: Ø {times.Average():0.0} ms, max {times.Max():0.0} ms, belegt {allocated >> 20} MB");
        Console.WriteLine($"MESS danach gehalten (nach GC): {(GC.GetTotalMemory(true) - before) >> 20} MB");

        // moving: one new element per mouse move (preview while dragging)
        var signature = Make(TestColorFonts.Svg, "Max Mustermann");
        Report("Ziehen Farbschrift (neues Element + Zeichenliste)", () =>
        {
            signature = signature with { Rect = signature.Rect with { Left = signature.Rect.Left + 1, Right = signature.Rect.Right + 1 } };
            ElementOverlay.List(page, signature);
        }, 50);
        var text = new TextElement { Rect = new PdfRectangle(100, 100, 400, 160), Text = "Musterstraße 12, 12345 Musterstadt" };
        Report("Ziehen Text (neues Element + Zeichenliste)", () =>
        {
            text = text with { Rect = text.Rect with { Left = text.Rect.Left + 1, Right = text.Rect.Right + 1 } };
            ElementOverlay.List(page, text);
        }, 50);

        // dragging with drawing (as the view does per mouse move): display list + image onto the canvas
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(1200, 900));
        var toCanvas = Core.Pdf.Graphics.Matrix.Identity;
        void DragDraw(string label, PageElement start, bool resize = false)
        {
            var current = start;
            ElementOverlay.Draw(surface.Canvas, page, [current], toCanvas);
            Report(label, () =>
            {
                current = resize
                    ? current with { Rect = current.Rect with { Right = current.Rect.Right + 1 } }
                    : ElementAppearance.Moved(current, current.Rect with { Left = current.Rect.Left + 1, Right = current.Rect.Right + 1 });
                ElementOverlay.Draw(surface.Canvas, page, [current], toCanvas);
            }, 20);
        }
        var pixels = new byte[1600 * 1200 * 4];
        new Random(1).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var picture = new ImageElement { Rect = new PdfRectangle(100, 100, 500, 400), Image = new Core.Imaging.RgbaImage(1600, 1200, pixels) };
        DragDraw("Ziehen mit Zeichnen: Bild 1600×1200", picture);
        DragDraw("Größe ändern mit Zeichnen: Bild 1600×1200", picture, resize: true);
        DragDraw("Ziehen mit Zeichnen: Farbschrift", Make(TestColorFonts.Svg, "Max Mustermann"));
        DragDraw("Ziehen mit Zeichnen: Text", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SignatureWindowTyping(bool textured)
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") is null) return;
        UiSession.Run(() =>
        {
            long before = 0;
            var window = new SignatureWindow(new SignatureElement { Rect = default, Name = "", FontFile = TestColorFonts.Svg, Textured = textured });
            window.Show();
            try
            {
                UiSession.Capture(window);
                var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "NameBox");
                const string name = "Maximilian Mustermann-Schulze";
                var parts = new List<(double Typing, double Later)>();
                var times = new List<double>();
                before = GC.GetTotalMemory(true);
                var alloc = GC.GetAllocatedBytesForCurrentThread();
                // at typing speed (one key per 80 ms): only the work is measured, not the pause
                for (var n = 1; n <= name.Length; n++)
                {
                    var sw = Stopwatch.StartNew();
                    box.Text = name[..n];
                    UiSession.Capture(window);   // like one frame on screen: list and visible previews
                    var work = sw.Elapsed.TotalMilliseconds;
                    Thread.Sleep(80);   // pause between keys (not counted)
                    sw.Restart();
                    UiSession.Capture(window);
                    times.Add(work + sw.Elapsed.TotalMilliseconds);
                    parts.Add((work, sw.Elapsed.TotalMilliseconds));
                }
                var list = window.GetVisualDescendants().OfType<ListBox>().Single();
                Assert.All(list.Items.Cast<SignatureChoice>(), c => Assert.Equal(name, c.Preview.Name));
                Console.WriteLine($"MESS Unterschrift-Fenster tippen{(textured ? " mit Kuli-Struktur" : "")} ({name.Length} Tasten): Ø {times.Average():0.0} ms, max {times.Max():0.0} ms, belegt {(GC.GetAllocatedBytesForCurrentThread() - alloc) >> 20} MB");
                Console.WriteLine($"MESS   davon gleich nach der Taste Ø {parts.Average(p => p.Typing):0.0} ms, nach der Pause Ø {parts.Average(p => p.Later):0.0} ms");
                Console.WriteLine($"MESS danach gehalten bei offenem Fenster (nach GC): {(GC.GetTotalMemory(true) - before) >> 20} MB");
            }
            finally
            {
                window.Close();
                Console.WriteLine($"MESS nach dem Schließen gehalten (nach GC): {(GC.GetTotalMemory(true) - before) >> 20} MB");
            }
        });
    }

    /// <summary>Measures small operations: building a sealed signature, a text paragraph, and flattening many fields.</summary>
    [Fact]
    public void SmallThings()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") is null) return;
        using var device = Core.Platform.DeviceIdentity.LoadOrCreate(Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"m-{Guid.NewGuid():N}.key"));
        var signature = new SignatureElement { Rect = new PdfRectangle(100, 100, 400, 170), Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" };
        signature = signature with { Seal = SignatureSeal.Create(signature, $"Max Mustermann · 16.10.2026 12:00 · Kennung {device.Code}", device) };
        Report("Aufbau versiegelte Unterschrift", () => ElementAppearance.For(signature with { }), 200);
        var paragraph = new TextElement
        {
            Rect = new PdfRectangle(60, 400, 400, 700),
            Text = string.Join("\n", Enumerable.Repeat("Max Mustermann, Musterstraße 12, 12345 Musterstadt. Hiermit bestätige ich den Empfang der Unterlagen.", 6)),
        };
        Report("Aufbau Textabsatz (6 Absätze)", () => ElementAppearance.For(paragraph with { }), 200);
        Report("FitHeight Textabsatz", () => ElementAppearance.FitHeight(paragraph with { }), 200);

        // flatten 300 pages with 20 fields each (with appearances) in a file with 500 layers (like a CAD drawing)
        var layered = TestPdf.Simple(pages: 1, title: null);
        var groups = new Core.Pdf.Objects.PdfArray();
        for (var i = 0; i < 500; i++)
            groups.Add(TestPdf.Ref(layered.Add(TestPdf.Dict(("Type", TestPdf.N("OCG")), ("Name", new Core.Pdf.Objects.PdfString(System.Text.Encoding.ASCII.GetBytes($"Ebene {i}")))))));
        ((Core.Pdf.Objects.PdfDictionary)layered.Get(1)).Set(Core.Pdf.Objects.PdfName.Get("OCProperties"),
            TestPdf.Dict(("OCGs", groups), ("D", TestPdf.Dict(("OFF", groups)))));
        using var source = PdfDocument.Open(layered.Build());
        var fields = Enumerable.Range(0, 20).Select(i => (PageElement)new TextElement
            { Rect = new PdfRectangle(60, 780 - i * 35, 300, 810 - i * 35), Text = $"Feld {i}", IsField = true }).ToList();
        using var withFields = new MemoryStream();
        Core.Pdf.Writing.PdfWriter.Write(source, Enumerable.Repeat(PageItem.From(source)[0], 300).Select(p => p.WithElements(fields)).ToList(), withFields);
        using var formDoc = PdfDocument.Open(withFields.ToArray());
        Report("300 Seiten × 20 Felder flachlegen", () =>
        {
            using var output = new MemoryStream();
            Core.Pdf.Writing.PdfWriter.Write(formDoc, PageItem.From(formDoc), output, new Core.Pdf.Writing.PdfSaveOptions { Flatten = true });
        }, 3);
    }

    /// <summary>Measures loading a photo as a signature: a 6000 × 4000 JPEG (24 MP, like a mobile phone photo).</summary>
    [Fact]
    public void LoadPhoto()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") is null) return;
        var file = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", "foto-24mp.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using (var bitmap = new SkiaSharp.SKBitmap(6000, 4000))
        {
            using (var canvas = new SkiaSharp.SKCanvas(bitmap))
            {
                canvas.Clear(SkiaSharp.SKColors.White);
                using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.DarkBlue, StrokeWidth = 30, IsAntialias = true, Style = SkiaSharp.SKPaintStyle.Stroke };
                for (var i = 0; i < 20; i++) canvas.DrawLine(300 + i * 250, 1500, 500 + i * 250, 2500 + (i % 3) * 100, paint);
            }
            using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
            File.WriteAllBytes(file, data.ToArray());
        }
        Services.ImageLoader.Load(file);   // warm-up
        var alloc = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var image = Services.ImageLoader.Load(file)!;
        Console.WriteLine($"MESS Foto 24 MP laden: {sw.ElapsedMilliseconds} ms, belegt {(GC.GetAllocatedBytesForCurrentThread() - alloc) >> 20} MB, Ergebnis {image.Width} × {image.Height}");
        Report("Weißen Hintergrund entfernen (1600 px)", () => Core.Imaging.ImageTools.Trim(Core.Imaging.ImageTools.RemoveWhiteBackground(image)), 5);
        var colorFont = EmbeddedFonts.HandwritingOpenType(TestColorFonts.Svg);
        var shaped = Core.Fonts.Layout.TextShaper.For(colorFont).Shape("Maximilian Mustermann-Schulze");
        var color = Core.Fonts.ColorGlyphs.For(colorFont)!;
        color.Compose(shaped, colorFont.UnitsPerEm);
        Report("Kuli-Bild zusammensetzen (29 Zeichen)", () => color.Compose(shaped, colorFont.UnitsPerEm), 10);
    }

    /// <summary>Measures the saved file size: text on every page, a field on every page, and the fields flattened.</summary>
    [Fact]
    public void WriterSizes()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") is null) return;
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1000, title: null).Build(XRefStyle.Stream));
        const string paragraph = "Max Mustermann, Musterstraße 12, 12345 Musterstadt. Hiermit bestätige ich den Empfang der Unterlagen und die Richtigkeit der Angaben.";
        var text = PageItem.From(doc).Select(p => p.WithElements([new TextElement { Rect = new PdfRectangle(60, 600, 400, 700), Text = paragraph }])).ToList();
        var fields = PageItem.From(doc).Select(p => p.WithElements([new TextElement { Rect = new PdfRectangle(60, 600, 400, 630), Text = "Max Mustermann", IsField = true }])).ToList();
        long Size(IReadOnlyList<PageItem> pages, bool flatten)
        {
            using var output = new MemoryStream();
            var sw = Stopwatch.StartNew();
            Core.Pdf.Writing.PdfWriter.Write(doc, pages, output, new Core.Pdf.Writing.PdfSaveOptions { Flatten = flatten });
            Console.WriteLine($"MESS   ({sw.ElapsedMilliseconds} ms)");
            return output.Length;
        }
        Console.WriteLine($"MESS 1000 Seiten ohne Elemente: {Size(PageItem.From(doc), false) >> 10} KB");
        Console.WriteLine($"MESS 1000 Seiten je ein Absatz Text: {Size(text, false) >> 10} KB");
        Console.WriteLine($"MESS 1000 Seiten je ein Textfeld: {Size(fields, false) >> 10} KB");
        Console.WriteLine($"MESS 1000 Seiten je ein Textfeld, flachgelegt: {Size(fields, true) >> 10} KB");
    }

    [Fact]
    public void LargeDocument()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") is null) return;
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 3000, title: null).Build(XRefStyle.Stream), "gross-elemente.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                UiSession.Capture(window);
                var doc = vm.SelectedDocument!;
                void Step(string name, Action action, int repeat = 1)
                {
                    var alloc = GC.GetAllocatedBytesForCurrentThread();
                    var sw = Stopwatch.StartNew();
                    for (var i = 0; i < repeat; i++)
                    {
                        action();
                        UiSession.Capture(window);
                    }
                    Console.WriteLine($"MESS {name}: {sw.Elapsed.TotalMilliseconds / repeat:0.0} ms je Mal (mit Zeichnen), belegt {(GC.GetAllocatedBytesForCurrentThread() - alloc) / repeat >> 10} KB je Mal");
                }

                var element = new TextElement { Rect = new PdfRectangle(100, 600, 400, 660), Text = "Max Mustermann" };
                Step("3000 Seiten: Text einfügen", () => doc.AddElement(0, element, "Text einfügen"));
                Step("3000 Seiten: Element verschieben", () =>
                {
                    var current = doc.ElementsOn(0)[0];
                    doc.ReplaceElement(new ElementRef(0, current), current with { Rect = current.Rect with { Left = current.Rect.Left + 1 } }, "Element verschieben");
                }, 20);
                doc.SearchText = "Seite";
                doc.OpenSearch();
                await Task.Delay(500);
                UiSession.Capture(window);
                Step("3000 Seiten: Element verschieben, Suche offen", () =>
                {
                    var current = doc.ElementsOn(0)[0];
                    doc.ReplaceElement(new ElementRef(0, current), current with { Rect = current.Rect with { Left = current.Rect.Left + 1 } }, "Element verschieben");
                }, 5);
                doc.CloseSearch();

                // drawing: a page with 10 elements compared with one without
                var view = window.GetVisualDescendants().OfType<Controls.DocumentView>().Single();
                Step("Bild zeichnen, Seite mit 1 Element", () => view.InvalidateVisual(), 20);
                for (var i = 0; i < 9; i++)
                    doc.AddElement(0, new SignatureElement { Rect = new PdfRectangle(100, 500 - i * 50, 300, 545 - i * 50), Name = "Max Mustermann", FontFile = EmbeddedFonts.Handwriting[i] }, "Unterschrift einfügen");
                UiSession.Capture(window);
                Step("Bild zeichnen, Seite mit 10 Elementen", () => view.InvalidateVisual(), 20);
                doc.ScrollToPage(1500);
                UiSession.Capture(window);
                Step("Bild zeichnen, Seite ohne Elemente", () => view.InvalidateVisual(), 20);

                // saving with elements (subset fonts, outlines)
                var target = Path.Combine(Path.GetDirectoryName(pdf)!, "gross-elemente-gespeichert.pdf");
                var sw = Stopwatch.StartNew();
                using (var output = File.Create(target))
                    Core.Pdf.Writing.PdfWriter.Write(doc.Document, doc.History.Pages, output);
                Console.WriteLine($"MESS 3000 Seiten mit 10 Elementen speichern: {sw.ElapsedMilliseconds} ms");
                sw.Restart();
                using (var output = File.Create(target))
                    Core.Pdf.Writing.PdfWriter.Write(doc.Document, Core.Pdf.Editing.PageItem.From(doc.Document), output);
                Console.WriteLine($"MESS 3000 Seiten ohne Elemente speichern: {sw.ElapsedMilliseconds} ms");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
