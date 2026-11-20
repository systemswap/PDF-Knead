using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Measurement (not part of the normal test run): editing a large form (2 000 fields on one page). Covers drawing in
/// form design mode with and without the tab order, selecting and moving 1 000 fields, undo with the field list open, filling in
/// with calculations, duplicating and saving. Enable with PDFKNEAD_MESSEN=1, use a release build, and show the output with
/// --logger "console;verbosity=detailed" (lines starting with "MESS").
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class FormTiming
{
    [Fact]
    public void LargeForm()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_MESSEN") is null) return;
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1, title: null).Build(), "grosses-formular.pdf");
        UiSession.Run(async () =>
        {
            PdfKnead.Services.UserSettings.Update(s => s with { FieldLooks = null });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                void Step(string name, Action action, int repeat = 1)
                {
                    UiSession.Capture(window);
                    var alloc = GC.GetAllocatedBytesForCurrentThread();
                    var sw = Stopwatch.StartNew();
                    for (var i = 0; i < repeat; i++)
                    {
                        action();
                        UiSession.Capture(window);
                    }
                    Console.WriteLine($"MESS {name}: {sw.Elapsed.TotalMilliseconds / repeat:0.0} ms je Mal (mit Zeichnen), belegt {(GC.GetAllocatedBytesForCurrentThread() - alloc) / repeat >> 10} KB je Mal");
                }

                // 2 000 text fields in a grid, 100 of them sums over 20 fields each
                var fields = new List<PageElement>();
                for (var i = 0; i < 2000; i++)
                {
                    double x = 20 + i % 40 * 14, y = 800 - i / 40 * 15;
                    fields.Add(new FieldElement { Rect = new PdfRectangle(x, y, x + 12, y + 10), Value = "", Spec = new FieldSpec { Name = $"F{i}" } });
                }
                for (var i = 0; i < 100; i++)
                    fields[1900 + i] = (FieldElement)fields[1900 + i] with
                    {
                        Spec = new FieldSpec { Name = $"F{1900 + i}", ReadOnly = true, Calculation = new FieldCalculation(CalcOperation.Sum, [.. Enumerable.Range(i * 19, 20).Select(k => $"F{k}")]) },
                    };
                doc.History.AddElements(new Dictionary<int, IReadOnlyList<PageElement>> { [0] = fields }, "Felder");
                vm.RibbonTab = RibbonTab.Form;
                vm.ToggleFormDesignCommand.Execute(null);
                vm.FitPageCommand.Execute(null);
                var view = window.GetVisualDescendants().OfType<Controls.DocumentView>().Single();

                Step("2000 Felder: Bild zeichnen (Bearbeiten)", () => view.InvalidateVisual(), 10);
                vm.ToggleTabOrderCommand.Execute(null);
                Step("2000 Felder: Bild zeichnen mit Tab-Reihenfolge", () => view.InvalidateVisual(), 10);
                vm.ToggleTabOrderCommand.Execute(null);

                List<ElementRef> FirstHalf() => [.. doc.ElementsOn(0).Take(1000).Select(e => new ElementRef(0, e))];
                Step("1000 Felder auswählen", () => doc.SelectFields(FirstHalf()), 3);
                // a property of the field: all widgets of each field (also linked ones by their name) are looked for
                Step("1000 gewählte Felder: Pflichtfeld", () => doc.ChangeFieldSpecs(s => s with { Required = !s.Required }, true, "Pflichtfeld ändern"), 3);
                Step("1000 gewählte Felder: Pfeiltaste", () => doc.MoveSelectedFields(1, 0, "Felder verschieben"), 5);
                vm.SidebarTab = SidebarTab.Bookmarks;
                Step("1000 gewählte Felder: Pfeiltaste, Vorschaubilder verdeckt", () => doc.MoveSelectedFields(1, 0, "Felder verschieben"), 5);
                foreach (var tab in new[] { SidebarTab.Bookmarks, SidebarTab.Pages, SidebarTab.Bookmarks, SidebarTab.Pages })
                for (var k = 0; k < 3; k++)
                {
                    vm.SidebarTab = tab;
                    UiSession.Capture(window);
                    var t0 = Stopwatch.StartNew();
                    doc.MoveSelectedFields(1, 0, "Felder verschieben");
                    var vmMs = t0.Elapsed.TotalMilliseconds;
                    t0.Restart();
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    var jobs = t0.Elapsed.TotalMilliseconds;
                    t0.Restart();
                    window.UpdateLayout();
                    var layout = t0.Elapsed.TotalMilliseconds;
                    t0.Restart();
                    UiSession.Capture(window);
                    Console.WriteLine($"MESS   Pfeiltaste zerlegt ({tab}): Logik {vmMs:0} ms, Aufträge {jobs:0} ms, Layout {layout:0} ms, Zeichnen {t0.Elapsed.TotalMilliseconds:0} ms");
                }
                vm.SidebarTab = SidebarTab.Pages;
                void Part(string name, Action action, int repeat = 5)
                {
                    var alloc = GC.GetAllocatedBytesForCurrentThread();
                    var sw = Stopwatch.StartNew();
                    for (var i = 0; i < repeat; i++) action();
                    Console.WriteLine($"MESS   Teil {name}: {sw.Elapsed.TotalMilliseconds / repeat:0.0} ms, belegt {(GC.GetAllocatedBytesForCurrentThread() - alloc) / repeat >> 10} KB");
                }
                using (var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(180, 255)))
                {
                    var item = doc.History.Pages[0];
                    Part("Vorschaubild: 2000 Felder zeichnen (je Bild)", () => Rendering.ElementOverlay.Draw(surface.Canvas, item.Page, item.OverlayElements,
                        Rendering.PageGeometry.PageToDevice(item.Page, 180 / item.Page.Width)));
                }
                Part("FieldsOfSelection", () => _ = doc.FieldsOfSelection);
                Part("FieldSelection", () => _ = doc.FieldSelection);
                Part("Bereich rechts Refresh", () => vm.FieldProperties.Refresh(doc));
                Part("SelectFields (gleiche Auswahl)", () => doc.SelectFields(doc.FieldSelection));
                Part("Pfeiltaste ohne Zeichnen", () => doc.MoveSelectedFields(1, 0, "Felder verschieben"));
                Step("Nur Bereich rechts neu + Zeichnen", () => vm.FieldProperties.Refresh(doc), 5);
                Step("Nur Bild neu (InvalidateVisual)", () => view.InvalidateVisual(), 5);
                Step("Gleiche Auswahl neu setzen + Zeichnen", () => doc.SelectFields(doc.FieldSelection), 5);
                Step("1000 Felder auswählen (wiederholt)", () => doc.SelectFields(FirstHalf()), 3);
                Step("Eins auswählen", () => doc.SelectFields([.. FirstHalf().Take(1)]), 3);
                Step("1000 Felder auswählen (aus einem)", () => { doc.SelectFields([.. FirstHalf().Take(1)]); UiSession.Capture(window); doc.SelectFields(FirstHalf()); }, 3);
                Step("1000 gewählte Felder: Bild zeichnen", () => view.InvalidateVisual(), 5);
                vm.SidebarTab = SidebarTab.Fields;
                Step("Rückgängig, Feldliste offen", () => vm.UndoCommand.Execute(null), 5);
                vm.SidebarTab = SidebarTab.Pages;
                Step("Rückgängig, Feldliste zu", () => vm.RedoCommand.Execute(null), 5);

                doc.SelectFields([.. doc.ElementsOn(0).Skip(1900).Take(1).Select(e => new ElementRef(0, e))]);
                Step("Summenfeld gewählt (Bereich rechts)", () => doc.SelectFields([.. doc.ElementsOn(0).Skip(1901).Take(1).Select(e => new ElementRef(0, e))]), 3);

                doc.SelectFields([.. doc.ElementsOn(0).Take(500).Select(e => new ElementRef(0, e))]);
                Step("500 Felder verdoppeln", () => doc.DuplicateSelectedFields());
                vm.UndoCommand.Execute(null);

                vm.ToggleFormDesignCommand.Execute(null);   // fill-in mode
                var n = 0;
                Step("Ausfüllen mit 100 Summen", () => doc.SetFieldValue(new ElementRef(0, doc.ElementsOn(0)[5]), (++n).ToString()), 10);

                var sw = Stopwatch.StartNew();
                using (var output = new MemoryStream())
                    Core.Pdf.Writing.PdfWriter.Write(doc.Document, doc.History.Pages, output);
                Console.WriteLine($"MESS 2000 Felder speichern: {sw.ElapsedMilliseconds} ms");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
