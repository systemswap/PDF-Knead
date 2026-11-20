using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfKnead.EInvoice.Ui;
using PdfKnead.Services;
using PdfKnead.Tests;
using PdfKnead.Tests.Ui;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// The interface of the checker in the main window (KONZEPT.md, section 5.24, „Oberfläche“), headless: the tab „E-Rechnung“ after „Sicher“,
/// its group „Info“ — green with an e-invoice, warning colour without one, searched only when the tab is shown — the area of the checker,
/// and hiding the tab in the settings. Only invented invoices.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class EInvoiceUiTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pdfknead-erechnung-ui-" + Guid.NewGuid().ToString("N"));

    public EInvoiceUiTests()
    {
        Directory.CreateDirectory(_folder);
        EInvoiceModule.Register();
    }

    public void Dispose()
    {
        UserSettings.Update(s => s with { HiddenModules = null });
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
        }
    }

    private string Write(string name, Dictionary<int, object> objects)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, TestFiles.Build(objects));
        return path;
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Tab_AfterSecure_InfoOnlyWhenShown_PanelChecks_HiddenInTheSettings()
    {
        // the invoice number visible on the page: the comparison finds it there
        var shown = InvoiceLocatorTests.Pdf();
        shown[3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 11 0 R>>>>/Annots[8 0 R]>>";
        shown[4] = ("", "BT /F1 14 Tf 72 700 Td (Rechnung RE-2026-0001) Tj ET");
        shown[11] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>";
        var withInvoice = Write("rechnung.pdf", shown);
        var plain = InvoiceLocatorTests.Pdf(catalogExtra: "");
        plain[1] = "<</Type/Catalog/Pages 2 0 R>>";
        plain[3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>>>";
        var without = Write("brief.pdf", plain);
        UiSession.Run(async () =>
        {
            UserSettings.Update(s => s with { HiddenModules = null });
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                // the tab, right after „Sicher“
                var tab = Assert.Single(vm.ModuleTabs);
                Assert.Equal(Tr.T("E-Rechnung"), tab.Header);
                var row = window.FindControl<ItemsControl>("ModuleTabRow")!;
                var buttons = ((StackPanel)row.Parent!).Children.ToList();
                Assert.Equal(buttons.IndexOf(row) - 1, buttons.FindIndex(b => b is Button { Content: string s } && s == Tr.T("Sicher")));

                await vm.OpenAsync([withInvoice]);
                // nothing searched before the tab is shown: its content does not exist yet
                vm.SelectModuleTabCommand.Execute(tab);
                var model = (EInvoiceTabViewModel)tab.Content.DataContext!;
                WaitUntil(() => model.State is InvoiceInfoState.Present or InvoiceInfoState.Absent);
                Assert.Equal(InvoiceInfoState.Present, model.State);
                Assert.Equal(Tr.T("E-Rechnung enthalten ({0})", "EN 16931"), model.InfoText);
                // green (the colour by style: a value set on the element would win over it)
                var info = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "InfoText");
                WaitUntil(() => info.Classes.Contains("present"));
                Assert.Equal(((Avalonia.Media.ISolidColorBrush)window.FindResource("GoodBrush")!).Color,
                    ((Avalonia.Media.ISolidColorBrush)info.Foreground!).Color);

                // „Prüfen“: the area on the right, its overview
                model.CheckCommand.Execute(null);
                var doc = vm.SelectedDocument!;
                var panel = Assert.IsType<EInvoicePanel>(doc.ModulePanel);
                var check = (EInvoicePanelViewModel)panel.DataContext!;
                WaitUntil(() => !check.IsBusy);
                Assert.True(check.HasInvoice);
                Assert.Equal("EN 16931", check.ProfileText);
                Assert.False(string.IsNullOrEmpty(check.CountText));
                // the sections: the readable invoice, the rules (the weightiest first, E4: by what they mean, then by weight), the file, the comparison
                Assert.Contains(check.InvoiceRows, r => r.Text.Contains("RE-2026-0001", StringComparison.Ordinal));
                Assert.NotEmpty(check.Rules);
                string[] meanings = [Tr.T("Formatfehler"), Tr.T("Pflichtangabe"), Tr.T("Geschäftsregel"), Tr.T("technisch")];
                Assert.Equal(check.Rules.OrderBy(r => Array.IndexOf(meanings, r.Meaning))
                        .ThenBy(r => r.Weight == Tr.T("Fehler") ? 0 : r.Weight == Tr.T("Warnung") ? 1 : 2).Select(r => r.Id),
                    check.Rules.Select(r => r.Id));
                Assert.All(check.Rules, r => Assert.Contains(r.Meaning, meanings));
                Assert.Contains(check.FileRows, f => f.Label == "/AFRelationship" && f.Value == "Alternative");
                Assert.Contains(check.FileRows, f => f.Label == "PDF/A");
                // a value found on the page jumps there
                var found = Assert.Single(check.Matches, m => m.HasPlace && m.Value.Contains("RE-2026-0001", StringComparison.Ordinal));
                Assert.Equal(Tr.T("S. {0}", 1), found.Page);
                (int Page, Core.Pdf.Text.TextQuad Quad)? revealed = null;
                doc.RevealRequested += (p, q) => revealed = (p, q);
                found.RevealCommand.Execute(null);
                Assert.Equal(0, revealed?.Page);
                // and marked there, so that it is seen at once (decided on 15.11.2026)
                Assert.Equal(revealed, doc.ModulePlace);
                // the page beside the area, not under it
                var view = window.GetVisualDescendants().OfType<PdfKnead.Controls.DocumentView>().Single();
                WaitUntil(() => view.RightInset > 380);
                Assert.True(view.FitBesideInset);
                WaitUntil(() => window.FindControl<Border>("ModulePanelHost")?.IsEffectivelyVisible == true);
                foreach (var section in panel.GetVisualDescendants().OfType<Expander>()) section.IsExpanded = true;
                window.UpdateLayout();
                if (UiSession.ScreenshotFolder() is { } feld)
                    UiSession.Capture(window).Save(Path.Combine(feld, "96-erechnung-feld.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                foreach (var section in panel.GetVisualDescendants().OfType<Expander>()) section.IsExpanded = false;
                if (UiSession.ScreenshotFolder() is { } shots)
                    UiSession.Capture(window).Save(Path.Combine(shots, "95-erechnung-reiter.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                // another area on the right closes it (one at a time)
                vm.ToggleAccessibilityCommand.Execute(null);
                WaitUntil(() => doc.IsAccessibilityOpen);
                Assert.Null(doc.ModulePanel);
                Assert.Null(doc.ModulePlace);   // the mark goes with the area
                doc.CloseAccessibility();

                // a PDF without an e-invoice: warning colour, and the area says so without error colour
                await vm.OpenAsync([without]);
                WaitUntil(() => model.State is InvoiceInfoState.Absent && model.InfoText == Tr.T("Keine E-Rechnung"));
                WaitUntil(() => info.Classes.Contains("absent"));
                Assert.Equal(((Avalonia.Media.ISolidColorBrush)window.FindResource("WarnBrush")!).Color,
                    ((Avalonia.Media.ISolidColorBrush)info.Foreground!).Color);
                model.CheckCommand.Execute(null);
                var none = (EInvoicePanelViewModel)((EInvoicePanel)vm.SelectedDocument!.ModulePanel!).DataContext!;
                WaitUntil(() => !none.IsBusy);
                Assert.True(none.NoInvoice);
                Assert.False(none.HasInvoice);
                Assert.Null(none.Error);
                // back to the first: remembered, not searched again
                vm.SelectedDocument = vm.Documents[0];
                Assert.Equal(InvoiceInfoState.Present, model.State);

                // hidden in the settings: the tab is gone, „Start“ is shown, its areas closed
                Assert.Single(vm.Settings.Modules).Shown = false;
                Assert.Empty(vm.ModuleTabs);
                Assert.Equal(RibbonTab.Start, vm.RibbonTab);
                Assert.All(vm.Documents, d => Assert.Null(d.ModulePanel));
                vm.Settings.Modules[0].Shown = true;
                Assert.Single(vm.ModuleTabs);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// The advice for „Als PDF/A speichern …“ (decided on 15.11.2026): with an e-invoice the sentence and PDF/A-3b; without, none;
    /// the module hands it to the main window through its tab.
    /// </summary>
    [Fact]
    public void PdfAAdvice_WithAnInvoice_3b_WithoutNone()
    {
        var withInvoice = Write("rechnung.pdf", InvoiceLocatorTests.Pdf());
        var plain = InvoiceLocatorTests.Pdf(catalogExtra: "");
        plain[1] = "<</Type/Catalog/Pages 2 0 R>>";
        plain[3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>>>";
        var without = Write("brief.pdf", plain);
        Assert.NotNull(Modules.UiModules.Tabs.Single(t => t.Key == EInvoiceModule.Key).PdfAAdvice);
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            await vm.OpenAsync([withInvoice]);
            var advice = await EInvoicePdfAAdvice.AskAsync(vm.SelectedDocument!);
            Assert.Equal(new Modules.PdfAAdvice(EInvoicePdfAAdvice.Text, Core.Pdf.Writing.PdfAConformance.A3b), advice);
            await vm.OpenAsync([without]);
            Assert.Null(await EInvoicePdfAAdvice.AskAsync(vm.SelectedDocument!));
        });
    }

    /// <summary>
    /// „Jetzt schwärzen?“ with an e-invoice (decided on 15.11.2026): the e-invoice is named always — here the bar covers nothing the
    /// search could find elsewhere — with what stays if it is kept; „Gleich mit entfernen“ takes it out with its data in the XMP, unticked
    /// it stays whole. The XML is never changed.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Redaction_NamesTheInvoiceAlways_RemovedWithIt(bool clean)
    {
        var path = Write("rechnung.pdf", InvoiceHybridRulesTests.Pdf());
        byte[] embedded;
        using (var original = PdfKnead.Core.Pdf.Document.PdfDocument.Open(path)) embedded = InvoiceLocator.Locate(original).Data!;
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            await vm.OpenAsync([path]);
            var doc = vm.SelectedDocument!;
            RedactionQuestion? asked = null;
            vm.ConfirmRedaction = question =>
            {
                asked = question;
                return Task.FromResult(new RedactionReply(RedactionAnswer.Apply, CleanElsewhere: clean));
            };
            doc.AddElement(0, new PdfKnead.Core.Pdf.Editing.RedactionElement { Rect = new PdfKnead.Core.Pdf.Document.PdfRectangle(400, 600, 500, 650) }, "Bereich");
            await vm.ApplyRedactionsCommand.ExecuteAsync(null);
            var entry = Assert.Single(asked!.Elsewhere, e => e.Sentence);
            Assert.Equal(EInvoiceRedaction.Text, entry.Where);
            Assert.Equal(EInvoiceRedaction.KeepWarning, entry.KeepWarning);
            Assert.Equal(Core.Pdf.Writing.CleanupItems.Attachments, entry.Cleanup);
            var location = InvoiceLocator.Locate(doc.Document);
            Assert.Equal(!clean, location.HasInvoice);
            Assert.Equal(!clean, location.Xmp is { Namespace.Length: > 0 });
            if (!clean) Assert.Equal(embedded, location.Data);   // kept: as it was, byte for byte
        });
    }

    /// <summary>
    /// „Dokument bereinigen“ with an e-invoice (decision (4) of 15.11.2026): a row of its own, not ticked; „Eingebettete Dateien“
    /// counts the other attachment (the delivery note). As offered — „Metadaten“ ticked too — the delivery note goes and the e-invoice stays
    /// whole: byte for byte, with its XMP data and the PDF/A identification, also when saved afterwards (the cleaning is applied again then);
    /// its row alone takes the e-invoice with its XMP data and leaves the delivery note.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cleanup_InvoiceARowOfItsOwn_NotTicked(bool invoiceTicked)
    {
        var path = Write("rechnung.pdf", InvoiceHybridRulesTests.Pdf());
        byte[] embedded;
        using (var original = PdfKnead.Core.Pdf.Document.PdfDocument.Open(path)) embedded = InvoiceLocator.Locate(original).Data!;
        var copy = Path.Combine(_folder, "bereinigt.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
            await vm.OpenAsync([path]);
            var doc = vm.SelectedDocument!;
            await vm.CleanDocumentCommand.ExecuteAsync(null);
            var invoice = Assert.Single(doc.CleanupRows, r => r.Module is not null);
            if (!invoiceTicked && UiSession.ScreenshotFolder() is { } shots)
            {
                window.UpdateLayout();
                UiSession.Capture(window).Save(Path.Combine(shots, "98-bereinigen-erechnung.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            Assert.False(invoice.IsChecked);
            Assert.Equal(Tr.T("E-Rechnung ({0})", "factur-x.xml"), invoice.Title);
            var attachments = Assert.Single(doc.CleanupRows, r => r.Item == Core.Pdf.Writing.CleanupItems.Attachments);
            Assert.Equal(Tr.T("Eingebettete Dateien: {0}", 1), attachments.Title);
            Assert.True(attachments.IsChecked);
            Assert.Equal(doc.CleanupRows.IndexOf(attachments) + 1, doc.CleanupRows.IndexOf(invoice));   // right under it
            var metadata = Assert.Single(doc.CleanupRows, r => r.Item == Core.Pdf.Writing.CleanupItems.Metadata);
            Assert.True(metadata.IsChecked);
            foreach (var row in doc.CleanupRows.Where(r => r != invoice && r != attachments && r != metadata)) row.IsChecked = false;
            if (invoiceTicked) (invoice.IsChecked, attachments.IsChecked) = (true, false);
            await vm.ApplyCleanupCommand.ExecuteAsync(null);

            foreach (var target in new[] { doc.Document, null })
            {
                // in the document, then saved (what was cleaned stays cleaned — the e-invoice not removed after all)
                if (target is null) await doc.SaveAsync(copy, doc.SaveOptions("1.7"));
                using var reopened = target is null ? PdfKnead.Core.Pdf.Document.PdfDocument.Open(copy) : null;
                var location = InvoiceLocator.Locate(target ?? reopened!);
                Assert.Equal(!invoiceTicked, location.HasInvoice);
                Assert.Equal(!invoiceTicked, location.Xmp is { Namespace.Length: > 0 });
                Assert.Equal(invoiceTicked, location.Attachments.Any(a => a.DisplayName == "lieferschein.txt"));
                if (!invoiceTicked) Assert.Equal(embedded, location.Data);
                if (!invoiceTicked) Assert.Equal(Core.Pdf.Writing.PdfAConformance.A3b, Core.Pdf.Writing.PdfACheck.Declared(target ?? reopened!));
                Assert.DoesNotContain("PDF-Knead", System.Text.Encoding.UTF8.GetString(XmpOf(target ?? reopened!)), StringComparison.Ordinal);   // no program
            }
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    private static byte[] XmpOf(PdfKnead.Core.Pdf.Document.PdfDocument doc) =>
        doc.Catalog?.Get(PdfKnead.Core.Pdf.Objects.PdfName.Get("Metadata")) is PdfKnead.Core.Pdf.Objects.PdfStream xmp ? doc.GetStreamData(xmp) : [];

    /// <summary>
    /// Pages of another file inserted and saved: the e-invoice of the document stays as it was (the inserted file brings only its pages).
    /// „Als Bild sichern“ and „PDF als Bild exportieren“ make a new file of page images: the e-invoice goes whole — no file, and no invoice
    /// data in the XMP naming one (KONZEPT.md, section 5.24).
    /// </summary>
    [Fact]
    public void InsertedPages_InvoiceStays_AsImages_GoesWhole()
    {
        var path = Write("rechnung.pdf", InvoiceHybridRulesTests.Pdf());
        var plain = InvoiceLocatorTests.Pdf(catalogExtra: "");
        plain[1] = "<</Type/Catalog/Pages 2 0 R>>";
        plain[3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>>>";
        var other = Write("brief.pdf", plain);
        byte[] embedded;
        using (var original = PdfKnead.Core.Pdf.Document.PdfDocument.Open(path)) embedded = InvoiceLocator.Locate(original).Data!;
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            await vm.OpenAsync([path]);
            var doc = vm.SelectedDocument!;
            using var source = PdfKnead.Core.Pdf.Document.PdfDocument.Open(other);
            doc.InsertPages(1, source, [0]);
            var merged = Path.Combine(_folder, "zusammengefuehrt.pdf");
            await doc.SaveAsync(merged, doc.SaveOptions("1.7"));
            using (var saved = PdfKnead.Core.Pdf.Document.PdfDocument.Open(merged))
            {
                var location = InvoiceLocator.Locate(saved);
                Assert.Equal(2, saved.PageCount);
                Assert.Equal(embedded, location.Data);
                Assert.True(location.Xmp is { Namespace.Length: > 0 });
            }

            var exported = Path.Combine(_folder, "als-bild.pdf");
            await doc.ExportImagePdfAsync(exported, new PdfKnead.Rendering.RasterSettings(72));
            doc.RasterOnSave = new PdfKnead.Rendering.RasterSettings(72);
            var flattened = Path.Combine(_folder, "als-bild-gesichert.pdf");
            await doc.SaveAsync(flattened, doc.SaveOptions("1.7"));
            foreach (var file in new[] { exported, flattened })
            {
                using var saved = PdfKnead.Core.Pdf.Document.PdfDocument.Open(file);
                var location = InvoiceLocator.Locate(saved);
                Assert.False(location.HasInvoice, file);
                Assert.Empty(location.Attachments);
                Assert.True(location.Xmp is null || location.Xmp.Namespace.Length == 0, $"{file}: XMP nennt noch eine Rechnung");
            }
        });
    }

    /// <summary>
    /// A value found twice on the page (decided on 15.11.2026): the row says „S. 1 (2 Stellen)“, a click marks both and shows the first,
    /// the next click the second, then the first again.
    /// </summary>
    [Fact]
    public void Comparison_ValueTwice_BothMarked_EachClickToTheNext()
    {
        var objects = InvoiceLocatorTests.Pdf();
        objects[3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 11 0 R>>>>/Annots[8 0 R]>>";
        objects[4] = ("", "BT /F1 14 Tf 72 700 Td (Rechnung RE-2026-0001) Tj ET BT /F1 10 Tf 72 300 Td (Bitte RE-2026-0001 angeben) Tj ET");
        objects[11] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>";
        var path = Write("rechnung.pdf", objects);
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            await vm.OpenAsync([path]);
            var doc = vm.SelectedDocument!;
            var tab = new EInvoiceTabViewModel(vm);
            tab.Shown(doc);
            await tab.Searching;
            tab.CheckCommand.Execute(null);
            var check = (EInvoicePanelViewModel)Assert.IsType<EInvoicePanel>(doc.ModulePanel).DataContext!;
            await check.Checking;
            var row = Assert.Single(check.Matches, m => m.Value.Contains("RE-2026-0001", StringComparison.Ordinal));
            Assert.Equal(Tr.T("S. {0} ({1} Stellen)", "1", 2), row.Page);
            row.RevealCommand.Execute(null);
            Assert.Equal(2, doc.ModulePlaces.Count);
            var first = doc.ModulePlace;
            Assert.Equal(doc.ModulePlaces[0], first);
            row.RevealCommand.Execute(null);
            Assert.Equal(doc.ModulePlaces[1], doc.ModulePlace);
            Assert.NotEqual(first, doc.ModulePlace);
            row.RevealCommand.Execute(null);
            Assert.Equal(first, doc.ModulePlace);
        });
    }

    /// <summary>The counts of the overview: one and many each in its own form (decided on 15.11.2026: not „1 Hinweise“).</summary>
    [Fact]
    public void Counts_OneAndManyEachInItsForm()
    {
        Assert.Equal(Tr.T("Auffälligkeiten: {0}", $"{Tr.T("1 Fehler")}, {Tr.T("{0} Warnungen", 2)}, {Tr.T("1 Hinweis")}"), EInvoicePanelViewModel.Counts(1, 2, 1));
        Assert.Equal(Tr.T("Auffälligkeiten: {0}", $"{Tr.T("{0} Fehler", 3)}, {Tr.T("1 Warnung")}, {Tr.T("{0} Hinweise", 0)}"), EInvoicePanelViewModel.Counts(3, 1, 0));
    }

    /// <summary>The invented invoice of the tests as a loose XML file (an XRechnung mostly comes so).</summary>
    private static string Cii(string extra = "") =>
        InvoiceKindTests.Cii("urn:cen.eu:en16931:2017").Replace("<ram:TypeCode>380</ram:TypeCode>", "<ram:TypeCode>380</ram:TypeCode>" + extra, StringComparison.Ordinal);

    /// <summary>Opens the document and opens the area of the checker through the tab; returns both, the check done.</summary>
    private static async Task<(EInvoiceTabViewModel Tab, EInvoicePanelViewModel Check, DocumentViewModel Doc)> Open(MainViewModel vm, string path, bool attachments = false)
    {
        await vm.OpenAsync([path]);
        var doc = vm.SelectedDocument!;
        var tab = new EInvoiceTabViewModel(vm);
        tab.Shown(doc);
        await tab.Searching;
        (attachments ? tab.ShowAttachmentsCommand : tab.CheckCommand).Execute(null);
        var check = (EInvoicePanelViewModel)Assert.IsType<EInvoicePanel>(doc.ModulePanel).DataContext!;
        await check.Checking;
        return (tab, check, doc);
    }

    /// <summary>
    /// „XRechnung als eigene Datei prüfen …“: offered under a PDF without an e-invoice; the loose file is read and its rules checked, the
    /// sections of the PDF (comparison, file, attachments, rules of the PDF) stay away. A file that is no invoice, one too large, a large
    /// invoice (the hint of 15 MB), and the limit of time.
    /// </summary>
    [Fact]
    public void LooseFile_ChecksTheXmlAlone_TellsWhatItIs_EndsAtTheLimit()
    {
        var plain = InvoiceLocatorTests.Pdf(catalogExtra: "");
        plain[1] = "<</Type/Catalog/Pages 2 0 R>>";
        plain[3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>>>";
        var without = Write("brief.pdf", plain);
        var xml = Path.Combine(_folder, "rechnung.xml");
        File.WriteAllText(xml, Cii());
        var note = Path.Combine(_folder, "notiz.xml");
        File.WriteAllText(note, "<?xml version=\"1.0\"?><notiz>Milch kaufen</notiz>");
        // about 16 MB in notes of 900 000 characters each (a single text may hold 1 MiB)
        var large = Path.Combine(_folder, "gross.xml");
        File.WriteAllText(large, Cii(string.Concat(Enumerable.Repeat($"<ram:IncludedNote><ram:Content>{new string('x', 900_000)}</ram:Content></ram:IncludedNote>", 18))));
        // over the limit of reading (100 MB): refused before a byte is read
        var tooLarge = Path.Combine(_folder, "zu-gross.xml");
        using (var s = File.Create(tooLarge)) s.SetLength(InvoiceXmlLimits.Default.MaxBytes + 1);
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var (_, check, _) = await Open(vm, without);
            Assert.True(check.NoInvoice);
            Assert.True(check.OfferLooseFile);
            Assert.True(check.NoAttachmentsNote);
            Assert.False(check.CancelCommand.CanExecute(null));

            check.CheckLooseFile(xml);
            Assert.True(check.IsBusy);
            Assert.True(check.CancelCommand.CanExecute(null));
            await check.Checking;
            Assert.Null(check.Error);
            Assert.Equal(Tr.T("Eigene Datei: {0}", "rechnung.xml"), check.SourceText);
            Assert.True(check.HasInvoice);
            Assert.False(check.FromPdf);
            Assert.Equal("EN 16931", check.ProfileText);
            Assert.Equal(InvoiceRules.Source, check.RulesText);
            Assert.Contains(check.InvoiceRows, r => r.Text.Contains("RE-2026-0001", StringComparison.Ordinal));
            Assert.NotEmpty(check.Rules);
            // no PDF: no comparison, no file, no attachments, no rule of the PDF
            Assert.False(check.ShowPdfSections);
            Assert.False(check.ShowAttachments);
            Assert.False(check.NoAttachmentsNote);
            Assert.Empty(check.Matches);
            Assert.Empty(check.FileRows);
            Assert.DoesNotContain(check.Rules, r => r.Id.StartsWith("BR-HYBRID", StringComparison.Ordinal) || r.Id.StartsWith("PDFK-PDF", StringComparison.Ordinal));
            Assert.Equal("", check.SizeHint);
            Assert.True(check.OfferLooseFile);   // the next one

            check.CheckLooseFile(note);
            await check.Checking;
            Assert.True(check.NoInvoice);
            Assert.Null(check.Error);
            Assert.Equal(Tr.T("Diese Datei ist keine E-Rechnung, die PDF-Knead kennt (CII, UBL, ZUGFeRD 1)."), check.NoInvoiceText);
            Assert.False(check.NoAttachmentsNote);

            check.CheckLooseFile(large);
            await check.Checking;
            Assert.Null(check.Error);
            Assert.True(check.HasInvoice);
            Assert.Equal(Tr.T("Die Rechnungs-XML ist {0:0.0} MB groß — Peppol und die Rechnungseingangsplattformen nehmen höchstens 15 MB samt Anhängen an.",
                new FileInfo(large).Length / 1048576.0), check.SizeHint);

            check.CheckLooseFile(tooLarge);
            await check.Checking;
            Assert.False(check.HasInvoice);
            Assert.Equal(Tr.T("Die Prüfung ist fehlgeschlagen: {0}", Tr.T("Die Datei ist zu groß ({0} MB).", 100)), check.Error);

            // the limit of time (here none at all): ended, said so, not busy any more
            check.Limit = TimeSpan.Zero;
            check.CheckLooseFile(xml);
            await check.Checking;
            Assert.Equal(Tr.T("Die Prüfung wurde abgebrochen — von Hand oder nach {0} Sekunden.", 0), check.Error);
            Assert.False(check.IsBusy);
            Assert.False(check.HasInvoice);
            Assert.False(check.CancelCommand.CanExecute(null));
        });
    }

    /// <summary>
    /// „XML speichern …“: the embedded XML byte for byte, under its embedded name, the PDF untouched; „Anhänge“: the other attachments shown,
    /// a program named as such and in the warning colour, saved byte for byte (never opened); „Keine Anhänge.“ when there are none.
    /// </summary>
    [Fact]
    public void SaveXml_ByteForByte_Attachments_SavedNeverOpened()
    {
        var objects = InvoiceLocatorTests.Pdf();
        // „lieferschein.pdf“, but a program by its first bytes (invented bytes, nothing runnable)
        const string program = "MZ\u0090\0 kein echtes Programm";
        objects[9] = "<</Type/Filespec/F(lieferschein.pdf)/UF(lieferschein.pdf)/EF<</F 10 0 R>>>>";
        objects[10] = ("/Type/EmbeddedFile/Subtype/application#2Fpdf", program);
        var withInvoice = Write("rechnung.pdf", objects);
        var only = InvoiceLocatorTests.Pdf();
        only[3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>>>";
        var invoiceOnly = Write("nur-rechnung.pdf", only);
        var plain = InvoiceLocatorTests.Pdf(catalogExtra: "");
        plain[1] = "<</Type/Catalog/Pages 2 0 R>>";
        plain[3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>>>";
        var without = Write("brief.pdf", plain);
        var before = File.ReadAllBytes(withInvoice);
        byte[] embedded;
        using (var doc = PdfKnead.Core.Pdf.Document.PdfDocument.Open(before)) embedded = InvoiceLocator.Locate(doc).Data!;
        var output = Directory.CreateDirectory(Path.Combine(_folder, "ausgabe")).FullName;
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var (tab, check, _) = await Open(vm, withInvoice, attachments: true);
            Assert.True(tab.SaveXmlCommand.CanExecute(null));

            // „XML speichern …“: nothing chosen, nothing written
            tab.PickSavePath = (_, _) => Task.FromResult<string?>(null);
            await tab.SaveXmlCommand.ExecuteAsync(null);
            Assert.Empty(Directory.GetFiles(output));
            (string Name, string Extension)? asked = null;
            var target = Path.Combine(output, "kopie.xml");
            tab.PickSavePath = (name, extension) =>
            {
                asked = (name, extension);
                return Task.FromResult<string?>(target);
            };
            await tab.SaveXmlCommand.ExecuteAsync(null);
            Assert.Equal(("factur-x.xml", "xml"), asked);
            Assert.Equal(embedded, File.ReadAllBytes(target));
            Assert.Equal(Tr.T("Die Rechnungs-XML ist gespeichert: {0}", "kopie.xml"), vm.Message);
            Assert.Single(Directory.GetFiles(output));   // the safe way leaves nothing beside it

            // „Anhänge“: the section open, the program named and marked
            Assert.True(check.AttachmentsOpen);
            Assert.True(check.ShowAttachments);
            var row = Assert.Single(check.Attachments);
            Assert.True(row.Dangerous);
            Assert.Contains(Tr.T("ein Programm! Nicht öffnen."), row.Description, StringComparison.Ordinal);
            Assert.StartsWith("lieferschein.pdf", row.Description, StringComparison.Ordinal);
            var saved = Path.Combine(output, "anhang.bin");
            check.PickSavePath = (name, extension) =>
            {
                asked = (name, extension);
                return Task.FromResult<string?>(saved);
            };
            await row.SaveCommand.ExecuteAsync(null);
            Assert.Equal(("lieferschein.pdf", "pdf"), asked);
            Assert.Equal(System.Text.Encoding.Latin1.GetBytes(program), File.ReadAllBytes(saved));
            Assert.Equal(Tr.T("Der Anhang ist gespeichert: {0}", "anhang.bin"), check.SaveNote);
            // closed by hand, „Anhänge“ opens it again in the same area
            check.AttachmentsOpen = false;
            tab.ShowAttachmentsCommand.Execute(null);
            Assert.True(check.AttachmentsOpen);
            // strictly reading: the PDF is as it was
            Assert.Equal(before, File.ReadAllBytes(withInvoice));

            // an e-invoice and nothing else: „Anhänge“ says so
            var (_, alone, _) = await Open(vm, invoiceOnly, attachments: true);
            Assert.True(alone.HasInvoice);
            Assert.False(alone.ShowAttachments);
            Assert.True(alone.NoAttachmentsNote);

            // no e-invoice: nothing to save
            var (plainTab, _, _) = await Open(vm, without);
            Assert.False(plainTab.SaveXmlCommand.CanExecute(null));
        });
    }

    /// <summary>
    /// The hint when a PDF with an e-invoice is changed for the first time (decided on 15.11.2026: a hint only, the invoice is never
    /// changed): once per document, not without an e-invoice, not while the tab is hidden.
    /// </summary>
    [Fact]
    public void FirstChange_PdfWithInvoice_HintOnce_NotWithoutInvoice_NotWhenHidden()
    {
        var withInvoice = Write("rechnung.pdf", InvoiceLocatorTests.Pdf());
        var second = Write("rechnung-2.pdf", InvoiceLocatorTests.Pdf());
        var plain = InvoiceLocatorTests.Pdf(catalogExtra: "");
        plain[1] = "<</Type/Catalog/Pages 2 0 R>>";
        plain[3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>>>";
        var without = Write("brief.pdf", plain);
        UiSession.Run(async () =>
        {
            UserSettings.Update(s => s with { HiddenModules = null });
            var vm = new MainViewModel();
            await vm.OpenAsync([withInvoice]);
            var doc = vm.SelectedDocument!;
            vm.Message = "";
            doc.RotateSelected(90);
            Assert.True(doc.IsModified);
            await EInvoiceEditHint.Last;
            Assert.Equal(EInvoiceEditHint.Text, vm.Message);
            // once per document: the next change asks nobody
            vm.Message = "";
            var before = EInvoiceEditHint.Last;
            doc.RotateSelected(90);
            Assert.Same(before, EInvoiceEditHint.Last);
            Assert.Equal("", vm.Message);

            // without an e-invoice: searched, nothing said
            await vm.OpenAsync([without]);
            vm.Message = "";
            vm.SelectedDocument!.RotateSelected(90);
            Assert.NotSame(before, EInvoiceEditHint.Last);
            await EInvoiceEditHint.Last;
            Assert.Equal("", vm.Message);

            // the tab hidden: the module is not asked
            Assert.Single(vm.Settings.Modules).Shown = false;
            await vm.OpenAsync([second]);
            vm.Message = "";
            before = EInvoiceEditHint.Last;
            vm.SelectedDocument!.RotateSelected(90);
            Assert.True(vm.SelectedDocument.IsModified);
            Assert.Same(before, EInvoiceEditHint.Last);
            Assert.Equal("", vm.Message);
            vm.Settings.Modules[0].Shown = true;

            // the first change „Als Bild sichern“: the hint says the e-invoice goes (the file of page images keeps no attachment)
            var third = Write("rechnung-3.pdf", InvoiceLocatorTests.Pdf());
            await vm.OpenAsync([third]);
            vm.Message = "";
            vm.SelectedDocument!.RasterOnSave = new PdfKnead.Rendering.RasterSettings(72);
            await EInvoiceEditHint.Last;
            Assert.Equal(EInvoiceEditHint.AsImagesText, vm.Message);
        });
    }
}
