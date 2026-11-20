using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Measurement for performance reviews (not part of the normal test run): page editing and splitting with 3000
/// pages. Enable with PDFKNEAD_MESSEN=1; show the output with --logger "console;verbosity=detailed" (lines starting with "MESS").
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class LargeDocumentTiming
{
    [Fact]
    public void Measure()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") is null) return;
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 3000, title: null).Build(XRefStyle.Stream), "gross.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                var w = Stopwatch.StartNew();
                await vm.OpenAsync([pdf]);
                UiSession.Capture(window);
                Console.WriteLine($"MESS öffnen+anzeigen {w.ElapsedMilliseconds} ms");
                var doc = vm.SelectedDocument!;
                long before = GC.GetTotalMemory(true);
                void Step(string name, Action action)
                {
                    var alloc = GC.GetAllocatedBytesForCurrentThread();
                    var sw = Stopwatch.StartNew();
                    action();
                    var ms = sw.Elapsed.TotalMilliseconds;
                    UiSession.Capture(window);
                    Console.WriteLine($"MESS {name}: {ms:0.0} ms (mit Zeichnen {sw.Elapsed.TotalMilliseconds:0.0} ms), belegt {(GC.GetAllocatedBytesForCurrentThread() - alloc) >> 10} KB");
                }
                Step("Seite 1 ans Ende", () => doc.Move([0], doc.PageCount));
                Step("Seiten 1-100 ans Ende", () => doc.Move([.. Enumerable.Range(0, 100)], doc.PageCount));
                Step("100 Seiten löschen", () => doc.History.Delete(Enumerable.Range(500, 100)));
                Step("Umkehren", () => doc.ReverseSelected());
                Step("Rückgängig", () => doc.Undo());
                Step("Drehen (aktuelle)", () => doc.RotateSelected(90));
                for (var i = 0; i < 50; i++) doc.Move([0], doc.PageCount);
                Step("50 weitere Schritte, dann Rückgängig", () => doc.Undo());
                Console.WriteLine($"MESS Verlauf+Seiten halten {(GC.GetTotalMemory(true) - before) >> 10} KB nach {doc.History.Count} Seiten");

                var split = new SplitViewModel(doc);
                var dialog = new SplitWindow(split) { Width = 1180, Height = 780 };
                var sw2 = Stopwatch.StartNew();
                dialog.Show();
                UiSession.Capture(dialog);
                Console.WriteLine($"MESS Teilen-Fenster öffnen {sw2.ElapsedMilliseconds} ms");
                var grid = dialog.GetVisualDescendants().OfType<Controls.PageGrid>().Single();
                sw2.Restart();
                for (var i = 0; i < 20; i++)
                {
                    grid.Offset += 400;
                    UiSession.Capture(dialog);
                }
                Console.WriteLine($"MESS Teilen-Raster 20× rollen {sw2.ElapsedMilliseconds} ms");
                sw2.Restart();
                for (var i = 0; i < 20; i++) { grid.InvalidateVisual(); UiSession.Capture(dialog); }
                Console.WriteLine($"MESS Teilen-Raster 20× nur neu zeichnen {sw2.ElapsedMilliseconds} ms");
                sw2.Restart();
                for (var i = 0; i < 20; i++) UiSession.Capture(dialog);
                Console.WriteLine($"MESS Fenster 20× ohne Änderung {sw2.ElapsedMilliseconds} ms");
                sw2.Restart();
                split.EveryPages = 1;
                split.SplitEvery();
                UiSession.Capture(dialog);
                Console.WriteLine($"MESS Teilen je 1 Seite (3000 Teile) {sw2.ElapsedMilliseconds} ms");
                dialog.Close();
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
