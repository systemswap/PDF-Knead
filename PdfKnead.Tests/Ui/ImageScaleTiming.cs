using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Measurement (not part of the normal test run; PDFKNEAD_MESSEN=1, lines „MESS“): scaling a large picture put on a blank page by dragging a
/// corner, with and without a polyline measured on the page (reported on 9.11.2026: scaling became sluggish).
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class ImageScaleTiming
{
    private static byte[] Blank() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<<>>>>",
        [6] = ("", ""),
    });

    /// <summary>A photo-sized picture (4000 × 3000) with structure, so that it does not compress to nothing.</summary>
    private static RgbaImage Photo()
    {
        const int w = 4000, h = 3000;
        var pixels = new byte[w * h * 4];
        var random = new Random(7);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                pixels[i] = (byte)(x * 255 / w);
                pixels[i + 1] = (byte)(y * 255 / h);
                pixels[i + 2] = (byte)random.Next(256);
                pixels[i + 3] = 255;
            }
        return new RgbaImage(w, h, pixels);
    }

    [Fact]
    public void ScalePicture_WithAndWithoutPolyline()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") is null) return;
        var pdf = TestFiles.Write(Blank(), "bild-skalieren-messung.pdf");
        var photo = Photo();
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentView>().Single();
                double Idle()
                {
                    var t = new List<double>();
                    for (var k = 0; k < 20; k++)
                    {
                        var sw0 = Stopwatch.StartNew();
                        view.InvalidateVisual();
                        UiSession.Capture(window);
                        t.Add(sw0.Elapsed.TotalMilliseconds);
                    }
                    t.Sort();
                    return t[10];
                }
                Console.WriteLine($"MESS leere Seite, nur Neuzeichnen: {Idle():0.0} ms");
                var image = new ImageElement { Rect = new PdfRectangle(60, 300, 460, 600), Image = photo };
                doc.AddElement(0, image, "Bild einfügen");
                UiSession.Capture(window);

                async Task Drag(string name)
                {
                    if (vm.Tool != ViewTool.SelectText) vm.SetToolCommand.Execute(ViewTool.SelectText);
                    var current = doc.Pages[0].Item.Elements.OfType<ImageElement>().Single();
                    var middle = view.TranslatePoint(view.PagePointToView(0, (current.Rect.Left + current.Rect.Right) / 2, (current.Rect.Top + current.Rect.Bottom) / 2), window)!.Value;
                    window.MouseDown(middle, MouseButton.Left);
                    window.MouseUp(middle, MouseButton.Left);
                    // as in use: the picture has been on the page a while, its smooth version is there
                    for (var w = 0; w < 30; w++)
                    {
                        Thread.Sleep(100);
                        UiSession.Capture(window);
                    }
                    Console.WriteLine($"MESS Werkzeug {vm.Tool}, gewählt: {doc.SelectedElement?.Element.GetType().Name}");
                    // the upper right corner (the larger y of the page) — on the screen above the lower one
                    var y = Math.Max(current.Rect.Top, current.Rect.Bottom);
                    var idle = new List<double>();
                    for (var k = 0; k < 20; k++)
                    {
                        var sw0 = Stopwatch.StartNew();
                        view.InvalidateVisual();
                        UiSession.Capture(window);
                        idle.Add(sw0.Elapsed.TotalMilliseconds);
                    }
                    idle.Sort();
                    Console.WriteLine($"MESS {name}: Bild ohne Änderung (nur Neuzeichnen) Median {idle[10]:0.0} ms");
                    var corner = view.TranslatePoint(view.PagePointToView(0, current.Rect.Right, y), window)!.Value;
                    Console.WriteLine($"MESS Ecke {corner}, Fenster {window.Bounds.Size}");
                    window.MouseMove(corner);
                    window.MouseDown(corner, MouseButton.Left);
                    var times = new List<double>();
                    var allocated = GC.GetTotalAllocatedBytes(true);
                    var gen2 = GC.CollectionCount(2);
                    var gen0 = GC.CollectionCount(0);
                    for (var k = 1; k <= 30; k++)
                    {
                        var sw = Stopwatch.StartNew();
                        window.MouseMove(corner + new Point(k * 3, -k * 2));
                        UiSession.Capture(window);
                        times.Add(sw.Elapsed.TotalMilliseconds);
                    }
                    var dragAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
                    var dragGen2 = GC.CollectionCount(2) - gen2;
                    var dragGen0 = GC.CollectionCount(0) - gen0;
                    window.MouseUp(corner + new Point(90, -60), MouseButton.Left);
                    UiSession.Capture(window);
                    Console.WriteLine($"MESS {name}: beim Ziehen belegt {dragAllocated >> 20} MB, Speicherbereinigungen Stufe 0: {dragGen0}, Stufe 2: {dragGen2}");
                    times.Sort();
                    var after = doc.Pages[0].Item.Elements.OfType<ImageElement>().Single().Rect;
                    Console.WriteLine($"MESS {name}: Median {times[times.Count / 2]:0.0} ms, max {times[^1]:0.0} ms je Bild (30 Bilder); Breite {current.Rect.Right - current.Rect.Left:0} → {after.Right - after.Left:0}");
                    await doc.WhenIdle();
                }

                // building the preview of one frame alone: a new rectangle means a new appearance and a new display list
                var page = doc.Pages[0].Page;
                var listTimes = new List<double>();
                for (var k = 1; k <= 10; k++)
                {
                    var preview = image with { Rect = new PdfRectangle(60, 300, 460 + k * 3, 600 + k * 2) };
                    var sw = Stopwatch.StartNew();
                    PdfKnead.Rendering.ElementOverlay.List(page, preview);
                    listTimes.Add(sw.Elapsed.TotalMilliseconds);
                }
                listTimes.Sort();
                Console.WriteLine($"MESS Vorschau-Liste je Bild: Median {listTimes[5]:0.0} ms, max {listTimes[^1]:0.0} ms");

                await Drag("Bild skalieren, ohne Messung");

                // a polyline with arcs, as the user drew it
                vm.MeasureCommand.Execute(null);
                doc.MeasureKind = MeasureKind.Polyline;
                for (var k = 0; k < 40; k++)
                    doc.MeasureAt(0, 80 + k * 10, 320 + (k % 2 == 0 ? 0 : 60), k % 3 == 1);
                Assert.True(doc.FinishMeasure());
                UiSession.Capture(window);
                await Drag("Bild skalieren, mit Linienzug, Messen aus");
                vm.MeasureCommand.Execute(null);
                UiSession.Capture(window);
                Console.WriteLine($"MESS Messen an: {doc.IsMeasuring}");
            }
            finally
            {
                vm.AskChoices = _ => Task.FromResult(new ChoiceAnswer(2, false));
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
