using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Rendering;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Code fields (KONZEPT.md, section 5.19, step 4): „QR-Feld“ and „Barcode-Feld“ in the „Formular“ tab place a field in the size of a code;
/// on the right the kind, look and content of the code with module size and self-check; filled in through a box below the field, which never
/// takes over content that gives no code; saved, other readers find the code and PDF Knead the field again.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class CodeFieldUiTests
{
    private static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static async Task Until(Window window, Func<bool> condition, int seconds = 10)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < end)
        {
            UiSession.Capture(window);
            await Task.Delay(20);
        }
    }

    private static void Click(Window window, Point at)
    {
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        UiSession.Capture(window);
    }

    /// <summary>Controls of the window and of its open flyouts.</summary>
    private static IEnumerable<Visual> Everything(Window window) =>
        window.GetVisualDescendants().Concat(Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(window)?.GetVisualDescendants() ?? []);

    private static byte[] BlankPageBytes()
    {
        using var doc = BlankPage.CreateDocument(595, 842, 1);
        using var output = new MemoryStream();
        Core.Pdf.Writing.PdfWriter.Write(doc, output, new Core.Pdf.Writing.PdfSaveOptions());
        return output.ToArray();
    }

    [Fact]
    public void QrField_PlaceSetLookFillInAndSave()
    {
        var pdf = TestFiles.Write(BlankPageBytes(), "qr-feld.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1500, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Form;
                vm.FitPageCommand.Execute(null);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;

                // „QR-Feld“, a click on the page: a code field 25 mm square, selected, the panel shows the code card
                var button = window.GetVisualDescendants().OfType<RibbonToggleButton>().Single(b => b.Label == Tr.T("QR-Feld") && b.IsEffectivelyVisible);
                Click(window, button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value);
                Assert.Equal(ViewTool.InsertFormField, vm.Tool);
                Click(window, At(200, 600));
                var field = Assert.IsType<FieldElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(BarcodeFormat.Qr, field.Spec.Code!.Format);
                Assert.Equal(25 * 72 / 25.4, field.Rect.Width, 1);
                Assert.Equal(25 * 72 / 25.4, field.Rect.Height, 1);
                var panel = vm.FieldProperties;
                Assert.True(panel.IsCode);
                Assert.False(panel.IsPlainText);
                Assert.False(panel.ShowAppearance);
                Assert.Equal(Tr.T("{0}-Feld", Tr.T("QR-Code")), panel.Title);
                Assert.Null(panel.CodeError);   // empty: no code yet, no error either

                // content in the panel: the code appears and reads back
                panel.CodeContent = "https://example.org/lager?platz=17";
                Assert.Equal("https://example.org/lager?platz=17", DocumentViewModel.ValueOf(Assert.Single(doc.ElementsOn(0))));
                await Until(window, () => panel.CodeCheckText == Tr.T("Probe gelesen: der Code ist lesbar."));
                Assert.Equal(Tr.T("Probe gelesen: der Code ist lesbar."), panel.CodeCheckText);
                Assert.Null(panel.CodeModuleWarning);
                Shoot(window, "99-qr-feld");

                // the look: colour and error correction, one step each, the content stays
                panel.CodeLevel = CodePropertiesViewModel.Levels.Single(l => l.Value == Core.Barcodes.Qr.QrErrorCorrection.H);
                Assert.Equal(Core.Barcodes.Qr.QrErrorCorrection.H, Assert.IsType<FieldElement>(Assert.Single(doc.ElementsOn(0))).Spec.Code!.Level);
                vm.UndoCommand.Execute(null);
                Assert.Equal(Core.Barcodes.Qr.QrErrorCorrection.M, Assert.IsType<FieldElement>(Assert.Single(doc.ElementsOn(0))).Spec.Code!.Level);
                Assert.Equal("https://example.org/lager?platz=17", DocumentViewModel.ValueOf(Assert.Single(doc.ElementsOn(0))));

                // fill in: a click on the field opens the box below it; content that gives no code stays out
                vm.TestFormCommand.Execute(null);
                UiSession.Capture(window);
                var placed = Assert.Single(doc.ElementsOn(0)).Rect;
                Click(window, At((placed.Left + placed.Right) / 2, (placed.Bottom + placed.Top) / 2));
                await Until(window, () => Everything(window).OfType<TextBox>().Any(t => t.IsFocused));
                var box = Everything(window).OfType<TextBox>().Single(t => t.IsFocused);
                Assert.Equal("https://example.org/lager?platz=17", box.Text);
                Assert.True(box.AcceptsReturn);
                box.Text = "Lager 17\nRegal 4";
                Shoot(window, "99-qr-feld-eingabe");
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Control);
                UiSession.Capture(window);
                Assert.Equal("Lager 17\nRegal 4", DocumentViewModel.ValueOf(Assert.Single(doc.ElementsOn(0))));
                Shoot(window, "99-qr-feld-ausgefuellt");

                // saved: the code on the page, read-only for other readers, a code field again in PDF Knead
                await vm.SaveCommand.ExecuteAsync(null);
                using var saved = PdfDocument.Open(File.ReadAllBytes(pdf));
                var widget = Assert.IsType<ForeignAnnotation>(Assert.Single(PageItem.From(saved)[0].Elements));
                Assert.Equal(BarcodeFormat.Qr, FieldSpec.Of(widget)!.Code!.Format);
                Assert.Equal(1, FormField.Of(widget)!.Flags & 1);
                var found = PageBarcodes.FindAll(saved, saved.Pages[0], PageInterpreter.Build(saved, saved.Pages[0]), [], new ImageCache());
                Assert.Equal("Lager 17\nRegal 4", Assert.Single(found).Code.Text);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
                ElementDefaults.Code = new CodeElement { Rect = default };
            }
        });
    }

    /// <summary>
    /// „Barcode-Feld“: Code 128 in a wide field; another kind of the same family keeps the size, content that does not fit the kind is
    /// refused with the reason; filled in with Enter (one line), Esc leaves the value as it was.
    /// </summary>
    [Fact]
    public void BarcodeField_KindContentRefusedEnterAndEsc()
    {
        var pdf = TestFiles.Write(BlankPageBytes(), "barcode-feld.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1500, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Form;
                vm.FitPageCommand.Execute(null);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                vm.InsertFormFieldCommand.Execute(FieldPreset.Barcode);
                Click(window, At(150, 500));
                var field = Assert.IsType<FieldElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(BarcodeFormat.Code128, field.Spec.Code!.Format);
                Assert.True(field.Rect.Width > 2 * field.Rect.Height);
                var panel = vm.FieldProperties;
                panel.CodeContent = "RE-2026-0815";
                var size = Assert.Single(doc.ElementsOn(0)).Rect;

                // EAN-13: same family, same size; the content does not fit — shown, and new content that does not fit is refused
                panel.CodeFormat = CodePropertiesViewModel.Formats.Single(f => f.Value == BarcodeFormat.Ean13);
                Assert.Equal(size, Assert.Single(doc.ElementsOn(0)).Rect);
                Assert.True(panel.HasCodeError);
                panel.CodeContent = "ABC";
                Assert.Equal("RE-2026-0815", DocumentViewModel.ValueOf(Assert.Single(doc.ElementsOn(0))));
                panel.CodeContent = "400638133393";
                Assert.Equal("400638133393", DocumentViewModel.ValueOf(Assert.Single(doc.ElementsOn(0))));
                Assert.False(panel.HasCodeError);

                // fill in: Enter takes over one line, Esc leaves it
                vm.TestFormCommand.Execute(null);
                UiSession.Capture(window);
                var placed = Assert.Single(doc.ElementsOn(0)).Rect;
                var middle = At((placed.Left + placed.Right) / 2, (placed.Bottom + placed.Top) / 2);
                Click(window, middle);
                await Until(window, () => Everything(window).OfType<TextBox>().Any(t => t.IsFocused));
                var box = Everything(window).OfType<TextBox>().Single(t => t.IsFocused);
                Assert.False(box.AcceptsReturn);
                box.Text = "12345";   // too short for EAN-13: never taken over
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                UiSession.Capture(window);
                Assert.Equal("400638133393", DocumentViewModel.ValueOf(Assert.Single(doc.ElementsOn(0))));
                box.Text = "590123412345";
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                UiSession.Capture(window);
                Assert.Equal("590123412345", DocumentViewModel.ValueOf(Assert.Single(doc.ElementsOn(0))));
                Click(window, middle);
                await Until(window, () => Everything(window).OfType<TextBox>().Any(t => t.IsFocused));
                box = Everything(window).OfType<TextBox>().Single(t => t.IsFocused);
                box.Text = "4006381333931";
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                UiSession.Capture(window);
                Assert.Equal("590123412345", DocumentViewModel.ValueOf(Assert.Single(doc.ElementsOn(0))));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
                ElementDefaults.Code = new CodeElement { Rect = default };
            }
        });
    }
}
