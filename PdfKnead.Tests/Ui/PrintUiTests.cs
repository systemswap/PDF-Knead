using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Printing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>UI tests for the print dialog: options, preview, "Save as PDF". These tests never print on a printer.</summary>
[Collection(nameof(UiCollection))]
public sealed class PrintUiTests
{
    private static void WaitUntil(Window window, Func<bool> condition, int seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
        {
            UiSession.Capture(window);
            Thread.Sleep(20);
        }
        Assert.True(condition(), "Zeitüberschreitung");
    }

    private static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    /// <summary>Booklet: preview as a book (front cover alone, opened spreads, back cover alone), print sheets, saving.</summary>
    [Fact]
    public void Booklet_BookPreview_Sheets_Save()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(6).Build(), "broschuere.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "broschuere (Druck).pdf");
        UiSession.Run(async () =>
        {
            Services.UserSettings.Update(s => s with { Print = new PrintPreferences(Printer: PrintPreferences.PdfTarget) });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                PrintViewModel? print = null;
                PrintWindow? dialog = null;
                Task<string?>? result = null;
                vm.ShowPrint = p =>
                {
                    print = p;
                    dialog = new PrintWindow(p);
                    result = dialog.ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                await vm.PrintCommand.ExecuteAsync(null);
                WaitUntil(window, () => print is { IsReady: true, PreviewImage: not null });
                var p = print!;
                var d = dialog!;

                p.SelectedKind = p.Kinds.Single(c => c.Value == PrintKind.Booklet);
                WaitUntil(d, () => p.IsReady && p.BookPages is not null && p.SheetCount == 4);
                Assert.True(p.IsBooklet);
                Assert.True(p.ShowBook);
                Assert.Equal(Tr.T("{0} (auf {1} quer)", "A5", "A4"), p.SelectedPaperChoice!.Label);
                var pages = p.BookPages!;
                Assert.Equal(8, pages.PageCount);   // 6 pages + 2 blank ones before the back cover
                Assert.Equal(Tr.T("Titelseite"), p.SheetInfo);
                Assert.False(p.PreviousSheetCommand.CanExecute(null));
                pages.Get(0);
                WaitUntil(d, () => pages.Get(0) is not null);
                Shoot(d, "79-broschuere-deckblatt");

                // open the book: pages 2–3
                p.NextSheetCommand.Execute(null);
                Assert.Equal(1, p.Spread);
                Assert.Equal(Tr.T("Seiten {0}–{1} von {2}", 2, 3, 8), p.SheetInfo);
                WaitUntil(d, () => pages.Get(1) is not null && pages.Get(2) is not null);
                Thread.Sleep(600);   // let the page turn finish
                Shoot(d, "80-broschuere-aufgeschlagen");

                // in the middle of a page turn (image for visual inspection)
                var book = d.FindControl<PdfKnead.Controls.BookletPreview>("Book")!;
                p.NextSheetCommand.Execute(null);
                WaitUntil(d, () => pages.Get(3) is not null && pages.Get(4) is not null);
                book.FreezeTurn(1, 0.3);
                Shoot(d, "80b-broschuere-umblaettern");
                book.FreezeTurn(1, 0.7);
                Shoot(d, "80c-broschuere-umblaettern");
                p.PreviousSheetCommand.Execute(null);

                // on to the back cover
                for (var i = 0; i < 3; i++) p.NextSheetCommand.Execute(null);
                Assert.Equal(Tr.T("Rückseite"), p.SheetInfo);
                Assert.False(p.NextSheetCommand.CanExecute(null));

                // print sheets: front of sheet 1 with booklet page 8 (the back cover, document page 6) on the left and page 1 on the right
                p.ShowSheetViewCommand.Execute(null);
                Assert.True(p.ShowSheets);
                Assert.Equal(Tr.T("Blatt {0} von {1}", 1, 4), p.SheetInfo);
                p.SheetIndex = 0;
                WaitUntil(d, () => p.PreviewImage is not null && p.SheetWidth > p.SheetHeight);
                Assert.Equal([5, 0], p.Layout[0].Placements.Select(x => x.Page));
                Shoot(d, "81-broschuere-druckbogen");

                // right-hand binding mirrors the sheets
                p.SelectedBinding = p.Bindings[1];
                WaitUntil(d, () => p.IsReady && p.Layout.Count == 4 && p.Layout[0].Placements[0].Page == 0);

                p.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await p.PrintCommand.ExecuteAsync(null);
                Assert.Equal(Tr.T("Gespeichert: {0}", target), await result!);
                using (var saved = PdfDocument.Open(File.ReadAllBytes(target)))
                {
                    Assert.Equal(4, saved.PageCount);
                    Assert.True(saved.Pages[0].Width > saved.Pages[0].Height);
                }
                var remembered = Services.UserSettings.Current.Print!;
                Assert.True(remembered.Booklet);
                Assert.True(remembered.BookletSettings!.RightBinding);
                Assert.Equal(1, remembered.PagesPerSheet);
            }
            finally
            {
                Services.UserSettings.Update(s => s with { Print = null });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Poster: print type "Poster", overview with grid, a click on a sheet shows it enlarged, percentage instead of a format, a hint
    /// when too large, saving as PDF and remembering the settings. See KONZEPT.md, section 5.11.
    /// </summary>
    [Fact]
    public void Poster_Overview_Tiles_Save()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(2).Build(), "poster.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "poster (Druck).pdf");
        UiSession.Run(async () =>
        {
            Services.UserSettings.Update(s => s with { Print = new PrintPreferences(Printer: PrintPreferences.PdfTarget) });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                PrintViewModel? print = null;
                PrintWindow? dialog = null;
                Task<string?>? result = null;
                vm.ShowPrint = p =>
                {
                    print = p;
                    dialog = new PrintWindow(p);
                    result = dialog.ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                await vm.PrintCommand.ExecuteAsync(null);
                WaitUntil(window, () => print is { IsReady: true, PreviewImage: not null });
                var p = print!;
                var d = dialog!;
                p.SelectedRange = p.Ranges.First(r => r.Value == PrintRange.Current);

                // poster on A2 (default): A4 at twice the size, as PDF without margins → 2 × 3 landscape sheets
                p.SelectedKind = p.Kinds.Single(k => k.Value == PrintKind.Poster);
                Assert.False(p.IsSingle);
                Assert.False(p.IsMultiple);
                WaitUntil(d, () => p.IsReady && p.PosterGrid is not null && p.PosterImage is not null);
                Assert.True(p.ShowPosterOverview);
                Assert.False(p.ShowSheets);
                var grid = p.PosterGrid!;
                Assert.Equal(2, grid.Scale, 2);
                Assert.Equal(grid.SheetCount, p.SheetCount);
                Assert.Contains($"{grid.Columns} × {grid.Rows}", p.PosterInfo);
                Shoot(d, "88e-poster-uebersicht");

                // click on sheet B1 in the overview: it appears enlarged
                var overview = d.FindControl<PdfKnead.Controls.PosterPreview>("Poster")!;
                var bounds = overview.Bounds;
                var scale = Math.Min((bounds.Width - 56) / grid.CoverWidth, (bounds.Height - 56) / grid.CoverHeight);
                var origin = Avalonia.VisualExtensions.TranslatePoint(overview, new Avalonia.Point((bounds.Width - grid.CoverWidth * scale) / 2, (bounds.Height - grid.CoverHeight * scale) / 2), d)!.Value;
                var click = new Avalonia.Point(origin.X + (grid.StepX * 1.5) * scale, origin.Y + grid.StepY * 0.5 * scale);
                d.MouseDown(click, MouseButton.Left);
                d.MouseUp(click, MouseButton.Left);
                Assert.Equal(1, p.SheetIndex);
                Assert.True(p.ShowSheets);
                Assert.Contains("B1", p.SheetInfo);
                WaitUntil(d, () => p.PreviewImage is not null && !p.Building);
                Shoot(d, "88f-poster-blatt");

                // custom size: 400 %, more sheets
                p.SelectedPosterSize = p.PosterSizes[^1];
                p.PosterPercent = 400;
                WaitUntil(d, () => p.IsReady && p.PosterGrid is { Scale: 4 });
                Assert.True(p.SheetCount > grid.SheetCount);
                Shoot(d, "88g-poster-prozent");

                // too large: a hint, nothing to print
                p.PosterPercent = 2000;
                WaitUntil(d, () => p.IsReady && p.HasProblem && p.SheetCount == 0);
                Assert.False(p.PrintCommand.CanExecute(null));

                p.PosterPercent = 150;
                p.Overlap = 15;
                WaitUntil(d, () => p.IsReady && p.SheetCount > 0 && !p.HasProblem);
                var sheets = p.SheetCount;
                p.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await p.PrintCommand.ExecuteAsync(null);
                Assert.Equal(Tr.T("Gespeichert: {0}", target), await result!);
                using (var saved = PdfDocument.Open(File.ReadAllBytes(target)))
                    Assert.Equal(sheets, saved.PageCount);
                var remembered = Services.UserSettings.Current.Print!;
                Assert.Equal(PrintKind.Poster, remembered.Kind);
                Assert.Equal(150, remembered.PosterSettings!.Percent);
                Assert.Equal(15 * PageFormat.PointsPerMillimeter, remembered.PosterSettings.Overlap, 3);
            }
            finally
            {
                Services.UserSettings.Update(s => s with { Print = null });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Protected file with only printing permitted: "Save as PDF" stays disabled, also after switching from a printer; otherwise the
    /// button would stay active and write an unprotected copy.
    /// </summary>
    [Fact]
    public void Protected_SaveAsPdfStaysLocked()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(2).Build(cipher: Core.Pdf.Security.PdfCipher.Aes128, permissions: Core.Pdf.Security.PdfPermissions.Print),
            "geschuetzt.pdf");
        UiSession.Run(async () =>
        {
            Services.UserSettings.Update(s => s with { Print = null });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                PrintViewModel? print = null;
                vm.ShowPrint = p =>
                {
                    print = p;
                    _ = new PrintWindow(p).ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                await vm.PrintCommand.ExecuteAsync(null);
                WaitUntil(window, () => print is { IsReady: true, SheetCount: > 0 });
                var p = print!;
                Assert.True(p.PrintAllowed);
                Assert.False(p.PdfAllowed);
                if (p.Targets.FirstOrDefault(t => !t.IsPdf) is { } printer)
                {
                    p.SelectedTarget = printer;
                    WaitUntil(window, () => p.IsReady && p.PrintCommand.CanExecute(null));
                }
                p.SelectedTarget = p.Targets.First(t => t.IsPdf);
                WaitUntil(window, () => p.IsReady);
                Assert.False(p.PrintCommand.CanExecute(null));
                var asked = false;
                p.PickSavePath = (_, _, _) =>
                {
                    asked = true;
                    return Task.FromResult<string?>(null);
                };
                await p.PrintCommand.ExecuteAsync(null);   // called directly: does nothing
                Assert.False(asked);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Closing the dialog while the pages are still being prepared aborts the work and releases the document; otherwise closing and
    /// saving would not wait and would release the document while the reader is still using it. Printing is disabled while the sheets
    /// are being built.
    /// </summary>
    [Fact]
    public void Closing_WhilePreparing_ReleasesTheDocument()
    {
        // 300 pages, 150 of them with one annotation each: all annotations are flattened (this takes a while)
        var pdf = TestPdf.Simple(300, null);
        var form = pdf.Add(new Core.Pdf.Objects.PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")),
            ("BBox", TestPdf.Box(0, 0, 60, 60))), System.Text.Encoding.ASCII.GetBytes("1 0 0 rg 0 0 60 60 re f")));
        for (var page = 5; page < 5 + 300; page += 2)
            ((Core.Pdf.Objects.PdfDictionary)pdf.Get(page)).Set(Core.Pdf.Objects.PdfName.Get("Annots"), new Core.Pdf.Objects.PdfArray(
            [
                TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Stamp")), ("Rect", TestPdf.Box(20, 20, 80, 80)),
                    ("F", Core.Pdf.Objects.PdfInteger.Of(4)), ("AP", TestPdf.Dict(("N", TestPdf.Ref(form))))),
            ]));
        var file = TestFiles.Write(pdf.Build(), "viele-anmerkungen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([file]);
                var doc = vm.SelectedDocument!;
                var print = new PrintViewModel(doc);
                Assert.False(print.PrintCommand.CanExecute(null));   // sheets are still being built
                print.Dispose();
                await print.Released.WaitAsync(TimeSpan.FromSeconds(30));
                await doc.Renderer.WaitUntilUnused([doc.Document]).WaitAsync(TimeSpan.FromSeconds(5));

                // let a second run complete: then printing or saving becomes possible
                var again = new PrintViewModel(doc);
                var until = DateTime.UtcNow.AddSeconds(60);
                while (!again.IsReady && DateTime.UtcNow < until) await Task.Delay(50);
                Assert.True(again.IsReady);
                Assert.Equal(150, (await again.Prepared).Flattened);
                again.Dispose();
                await again.Released.WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>A printer as the target: its options and the non-printable margin (only with "Microsoft Print to PDF" on Windows; nothing is printed).</summary>
    [Fact]
    public void PrintDialog_PrinterOptions()
    {
        const string printer = "Microsoft Print to PDF";
        if (!OperatingSystem.IsWindows() || Printers.List().All(p => p.Name != printer)) return;
        var pdf = TestFiles.Write(TestPdf.Simple(3).Build(), "drucker.pdf");
        UiSession.Run(async () =>
        {
            Services.UserSettings.Update(s => s with { Print = new PrintPreferences(Printer: printer) });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                PrintViewModel? print = null;
                PrintWindow? dialog = null;
                vm.ShowPrint = p =>
                {
                    print = p;
                    dialog = new PrintWindow(p);
                    _ = dialog.ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                await vm.PrintCommand.ExecuteAsync(null);
                WaitUntil(window, () => print is { IsReady: true, PreviewImage: not null });
                var p = print!;
                Assert.Equal(printer, p.SelectedTarget!.Name);
                Assert.True(p.IsPrinter);
                Assert.Equal(Tr.T("Drucken"), p.PrimaryText);
                Assert.True(p.HasDriverDialog);
                Assert.Contains(p.DuplexModes, m => m.Value == DuplexMode.Manual);
                Assert.True(p.Papers.Count > 3);
                p.SelectedKind = p.Kinds[0];
                p.SelectedPerSheet = p.PerSheetChoices.First(c => c.Value == 2);
                WaitUntil(dialog!, () => p.IsReady && p.SheetCount == 2 && p.Layout[0].Landscape);
                Shoot(dialog!, "78-drucken-drucker");
                dialog!.Close();
            }
            finally
            {
                Services.UserSettings.Update(s => s with { Print = null });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void PrintDialog_OptionsChangeTheSheets_SaveAsPdf()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(5).Build(), "drucken.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "drucken (Druck).pdf");
        UiSession.Run(async () =>
        {
            Services.UserSettings.Update(s => s with { Print = new PrintPreferences(Printer: PrintPreferences.PdfTarget) });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                PrintViewModel? print = null;
                PrintWindow? dialog = null;
                Task<string?>? result = null;
                vm.ShowPrint = p =>
                {
                    print = p;
                    dialog = new PrintWindow(p);
                    result = dialog.ShowDialog<string?>(window);
                    return Task.FromResult<string?>(null);
                };
                window.KeyPress(Key.P, RawInputModifiers.Control, PhysicalKey.P, null);
                WaitUntil(window, () => print is { IsReady: true, PreviewImage: not null });
                var p = print!;
                var d = dialog!;

                // "Save as PDF" (remembered), all 5 pages, one sheet each; page 2 is Letter landscape rotated by 90° → portrait
                Assert.True(p.IsPdf);
                Assert.Equal("A4", p.SelectedPaper!.Value);
                Assert.Equal(5, p.SheetCount);
                Assert.Equal(Tr.T("Blatt {0} von {1}", 1, 5), p.SheetInfo);
                Assert.Equal(0, p.PrintableMargin);
                Shoot(d, "76-drucken");

                // four pages per sheet, with borders → 2 sheets
                p.SelectedKind = p.Kinds[0];
                p.SelectedPerSheet = p.PerSheetChoices.First(c => c.Value == 4);
                p.Borders = true;
                WaitUntil(d, () => p.IsReady && p.SheetCount == 2 && p.Layout[0].Placements.Count == 4);
                Assert.False(p.IsCustomScaling);
                Shoot(d, "77-drucken-4-pro-blatt");

                // the "Print type" list opened (booklet and poster are chosen there, not under "Per sheet")
                Assert.Equal([PrintKind.Normal, PrintKind.Booklet, PrintKind.Poster], p.Kinds.Select(k => k.Value));
                Assert.All(p.PerSheetChoices, c => Assert.True(c.Value >= 1));
                var kinds = d.FindControl<ComboBox>("KindBox")!;
                kinds.IsDropDownOpen = true;
                Shoot(d, "77b-druckart-liste");
                kinds.IsDropDownOpen = false;

                // range "2-3", reversed → page 3, then page 2, one sheet each
                p.SelectedPerSheet = p.PerSheetChoices[0];
                p.SelectedRange = p.Ranges.First(r => r.Value == PrintRange.Custom);
                p.CustomRange = "2-3";
                p.Reverse = true;
                WaitUntil(d, () => p.IsReady && p.SheetCount == 2 && !p.HasProblem);
                p.CustomRange = "2-30";
                WaitUntil(d, () => p.IsReady && p.HasProblem && p.SheetCount == 0);
                Assert.False(p.PrintCommand.CanExecute(null));
                p.CustomRange = "2-3";
                WaitUntil(d, () => p.IsReady && p.SheetCount == 2 && !p.HasProblem);

                // actual size on A5: pages are cut off, so a hint appears
                p.SelectedPaper = p.Papers.First(x => x.Value == "A5");
                p.SelectedScaling = p.Scalings.First(s => s.Value == PrintScaling.ActualSize);
                WaitUntil(d, () => p.IsReady && p.HasProblem);
                Assert.Contains(Tr.T("Seiten ragen über das Papier hinaus und werden abgeschnitten."), p.Problem);
                p.SelectedScaling = p.Scalings[0];
                WaitUntil(d, () => p.IsReady && !p.HasProblem);

                // paging through the sheets
                p.NextSheetCommand.Execute(null);
                Assert.Equal(1, p.SheetIndex);
                Assert.False(p.NextSheetCommand.CanExecute(null));

                // save: the sheets as PDF, the dialog closes with a message
                p.PickSavePath = (_, name, _) =>
                {
                    Assert.Equal("drucken (Druck).pdf", name);
                    return Task.FromResult<string?>(target);
                };
                await p.PrintCommand.ExecuteAsync(null);
                var message = await result!;
                Assert.Equal(Tr.T("Gespeichert: {0}", target), message);
                using (var saved = PdfDocument.Open(File.ReadAllBytes(target)))
                {
                    Assert.False(saved.WasRepaired);
                    Assert.Equal(2, saved.PageCount);
                    Assert.Equal(PageFormat.Millimeters("A5")!.Value.Width * PageFormat.PointsPerMillimeter, saved.Pages[0].Width, 1);
                }

                // remembered: layout and paper for "Save as PDF"
                var remembered = Services.UserSettings.Current.Print!;
                Assert.Equal(PrintPreferences.PdfTarget, remembered.Printer);
                Assert.Equal("A5", remembered.For(PrintPreferences.PdfTarget).Paper);
                Assert.Equal(1, remembered.PagesPerSheet);
                Assert.True(remembered.Borders);
                Assert.Same(doc, vm.SelectedDocument);
                Assert.False(doc.IsModified);   // printing does not change the document
            }
            finally
            {
                Services.UserSettings.Update(s => s with { Print = null });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
