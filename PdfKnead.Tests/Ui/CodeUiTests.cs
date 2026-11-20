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
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Code einfügen“ (KONZEPT.md, section 5.19): the button in the „Einfügen“ tab, a click on the page places a code in its default size, the
/// panel on the right takes the content (typed: one undo step), shows why a content does not fit, warns of small modules and reads the
/// code back; saved, it is an element again and readable on the page.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class CodeUiTests
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

    [Fact]
    public void InsertCode_ClickTypeCheckUndoAndSave()
    {
        var pdf = TestFiles.Write(BlankPageBytes(), "code-einfuegen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Insert;
                vm.FitPageCommand.Execute(null);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;

                // the „Code“ button, then a click on the page: a QR code in its default size, selected, the content box has the cursor
                var button = window.GetVisualDescendants().OfType<RibbonToggleButton>().Single(b => b.Label == Tr.T("Code") && b.IsEffectivelyVisible);
                var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                UiSession.Capture(window);
                Assert.Equal(ViewTool.InsertCode, vm.Tool);
                Assert.True(button.IsChecked);
                window.MouseDown(At(200, 600), MouseButton.Left);
                window.MouseUp(At(200, 600), MouseButton.Left);
                var placed = Assert.IsType<CodeElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(BarcodeFormat.Qr, placed.Format);
                Assert.Equal(25 * 72 / 25.4, placed.Rect.Width, 1);   // without content: 25 mm
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                Assert.True(vm.CodeProperties.IsVisible);
                Assert.False(vm.ShowTextProperties);
                Assert.True(vm.CodeProperties.HasError);   // no content yet
                var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ContentBox");
                await Until(window, () => box.IsFocused);
                Assert.True(box.IsFocused);

                // typed: the code changes with each key, all keys are one undo step
                window.KeyTextInput("https://");
                window.KeyTextInput("example.org");
                UiSession.Capture(window);
                var typed = Assert.IsType<CodeElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal("https://example.org", typed.Text);
                Assert.False(vm.CodeProperties.HasError);
                await Until(window, () => vm.CodeProperties.CheckText == Tr.T("Probe gelesen: der Code ist lesbar."));
                Assert.Equal(Tr.T("Probe gelesen: der Code ist lesbar."), vm.CodeProperties.CheckText);
                Assert.Null(vm.CodeProperties.ModuleWarning);
                Shoot(window, "97-code-einfuegen");
                vm.UndoCommand.Execute(null);
                Assert.Equal("", Assert.IsType<CodeElement>(Assert.Single(doc.ElementsOn(0))).Text);
                vm.RedoCommand.Execute(null);
                Assert.Equal("https://example.org", Assert.IsType<CodeElement>(Assert.Single(doc.ElementsOn(0))).Text);

                // another kind: the content no longer fits — a message; digits fit, the check digit is added
                doc.SelectedElement = new ElementRef(0, doc.ElementsOn(0)[0]);
                vm.CodeProperties.Format = CodePropertiesViewModel.Formats.Single(f => f.Value == BarcodeFormat.Ean13);
                Assert.True(vm.CodeProperties.HasError);
                Assert.Contains(Tr.T("Nur Ziffern erlaubt."), vm.CodeProperties.Error);
                vm.CodeProperties.Text = "400638133393";
                var ean = Assert.IsType<CodeElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(BarcodeFormat.Ean13, ean.Format);
                Assert.True(ean.Rect.Width > ean.Rect.Height);   // the default size of an EAN-13
                Assert.Equal(BarcodeFormat.Ean13, ElementDefaults.Code.Format);   // the next code is an EAN-13 too
                // made very small: modules below the size scanners read
                doc.ReplaceElement(new ElementRef(0, ean), ean with { Rect = new PdfRectangle(ean.Rect.Left, ean.Rect.Bottom, ean.Rect.Left + 50, ean.Rect.Bottom + 30) }, "Test");
                Assert.NotNull(vm.CodeProperties.ModuleWarning);
                vm.UndoCommand.Execute(null);
                Assert.Null(vm.CodeProperties.ModuleWarning);
                Shoot(window, "98-code-ean");

                // saved: an element again, and on the page the reader finds it
                await vm.SaveCommand.ExecuteAsync(null);
                using var saved = PdfDocument.Open(File.ReadAllBytes(pdf));
                var back = Assert.IsType<CodeElement>(Assert.Single(PageItem.From(saved)[0].Elements));
                Assert.Equal((BarcodeFormat.Ean13, "400638133393"), (back.Format, back.Text));
                var found = PageBarcodes.FindAll(saved, saved.Pages[0], PageInterpreter.Build(saved, saved.Pages[0]), [], new ImageCache());
                Assert.Equal("4006381333931", Assert.Single(found).Code.Text);
            }
            finally
            {
                window.Close();
                ElementDefaults.Code = new CodeElement { Rect = default };
            }
        });
    }

    /// <summary>
    /// „Code ändern …“ on a GiroCode of the page with the IBAN as text beside it: the code becomes an element in its place with the content
    /// read; a new IBAN typed in — the panel points to the old IBAN beside it; saved, only the new code is on the page, the old content is
    /// nowhere in the file.
    /// </summary>
    [Fact]
    public void ChangeCode_OfThePage_HintOnTextBeside_UndoAndSave()
    {
        const string old = "BCD\n002\n1\nSCT\n\nMax Mustermann\nDE89370400440532013000\nEUR12.34";
        var giro = new CodeElement { Text = old, Rect = new PdfRectangle(100, 600, 180, 680) };
        using var blank = BlankPage.CreateDocument(595, 842, 1);
        byte[] bytes;
        using (var output = new MemoryStream())
        {
            Core.Pdf.Writing.PdfWriter.Write(blank, [PageItem.From(blank)[0].WithElements([giro,
                new TextElement { Rect = new PdfRectangle(200, 620, 520, 650), Text = "IBAN DE89 3704 0044 0532 0130 00" }])], output,
                new Core.Pdf.Writing.PdfSaveOptions { Flatten = true });
            bytes = output.ToArray();
        }
        var pdf = TestFiles.Write(bytes, "code-aendern.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitPageCommand.Execute(null);
                vm.Tool = ViewTool.Edit;
                await doc.InspectBarcodeAsync(0, 140, 640);
                Assert.NotNull(doc.InspectedBarcode);
                UiSession.Capture(window);   // the panel of the code is laid out with the tool's panel
                var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ReplaceCodeButton");
                UiSession.Capture(window);
                Assert.True(button.IsEffectivelyVisible);
                await vm.ReplaceBarcodeCommand.ExecuteAsync(null);

                var code = Assert.IsType<CodeElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(old, code.Text);
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                Assert.True(vm.CodeProperties.IsVisible);
                Assert.Null(vm.CodeProperties.BesideHint);   // nothing changed yet
                // the old code is no longer part of the page: only the element is read there
                var page = doc.Pages[0].Item;
                var list = Core.Pdf.Content.PageInterpreter.Build(page.Source, page.Page);
                Assert.Empty(PageBarcodes.FindAll(page.Source, page.Page, list, [], new ImageCache()));

                // a new IBAN: the panel points to the old one beside the code
                vm.CodeProperties.Text = old.Replace("DE89370400440532013000", "DE02120300000000202051");
                await Until(window, () => vm.CodeProperties.BesideHint is not null);
                Assert.Contains("DE89", vm.CodeProperties.BesideHint);
                Shoot(window, "99-code-aendern");
                // undo: the content, then the whole change (the old code is back in the page)
                vm.UndoCommand.Execute(null);
                vm.UndoCommand.Execute(null);
                Assert.Empty(doc.ElementsOn(0));
                vm.RedoCommand.Execute(null);
                vm.RedoCommand.Execute(null);

                await vm.SaveCommand.ExecuteAsync(null);
                var saved = File.ReadAllBytes(pdf);
                using var reopened = PdfDocument.Open(saved);
                var found = PageBarcodes.FindAll(reopened, reopened.Pages[0], Core.Pdf.Content.PageInterpreter.Build(reopened, reopened.Pages[0]), [], new ImageCache());
                Assert.Contains("DE02120300000000202051", Assert.Single(found).Code.Text);
                Assert.Empty(PdfKnead.Cli.LeakSearch.Find(saved, ["DE89370400440532013000"], null, out _));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Redacting over inserted codes (elements): a bar over half of one — the question reports it and the whole code goes; a bar over all
    /// of another — it goes with its bar; a third stays. Their contents are nowhere in the saved file (an element keeps its content in its
    /// own data), the untouched one is read as before.
    /// </summary>
    [Theory]
    [InlineData(BarcodeFormat.Qr)]
    [InlineData(BarcodeFormat.Ean13)]
    public void Redacted_InsertedCodes_GoWithTheirData(BarcodeFormat format)
    {
        var pdf = TestFiles.Write(BlankPageBytes(), $"code-schwaerzen-{format}.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, $"code-schwaerzen-{format}-ergebnis.pdf");
        var (half, whole, kept) = format == BarcodeFormat.Qr
            ? ("https://example.org/geheim1", "https://example.org/geheim2", "https://example.org/bleibt")
            : ("4006381333931", "4012345678901", "5901234123457");
        CodeElement At(string text, double x, double y)
        {
            var code = new CodeElement { Format = format, Text = text, Rect = default };
            var (w, h) = ElementAppearance.DefaultCodeSize(code);
            return code with { Rect = new PdfRectangle(x, y, x + w, y + h) };
        }
        CodeElement a = At(half, 60, 600), b = At(whole, 300, 600), c = At(kept, 60, 300);
        RedactionQuestion? asked = null;
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                foreach (var code in new[] { a, b, c }) doc.AddElement(0, code, "Test");
                // the left half of the first code, all of the second
                doc.AddElement(0, new RedactionElement { Rect = new PdfRectangle(a.Rect.Left - 2, a.Rect.Bottom - 2, (a.Rect.Left + a.Rect.Right) / 2, a.Rect.Top + 2) }, "Test");
                doc.AddElement(0, new RedactionElement { Rect = new PdfRectangle(b.Rect.Left - 2, b.Rect.Bottom - 2, b.Rect.Right + 2, b.Rect.Top + 2) }, "Test");
                vm.ConfirmRedaction = question =>
                {
                    asked = question;
                    return Task.FromResult(new RedactionReply(RedactionAnswer.Apply, AlsoUnredacted: true, Codes: question.Codes));
                };
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                await vm.SaveAsCommand.ExecuteAsync(null);
            }
            finally
            {
                window.Close();
            }
        });
        Assert.NotNull(asked);
        Assert.Contains(asked.Codes, r => r.Partial && r.Code.Code.Text == ElementAppearance.CodeSymbolOf(a).Symbol!.Encoded);
        var bytes = File.ReadAllBytes(target);
        using var saved = PdfDocument.Open(bytes);
        var found = PageBarcodes.FindAll(saved, saved.Pages[0], Core.Pdf.Content.PageInterpreter.Build(saved, saved.Pages[0]), [], new ImageCache())
            .Select(f => f.Code.Text).ToList();
        var keptRead = ElementAppearance.CodeSymbolOf(c).Symbol!.Encoded;
        Assert.Equal([keptRead], found);
        Assert.Empty(PdfKnead.Cli.LeakSearch.Find(bytes, [half, whole], null, out _));
    }

    private static byte[] BlankPageBytes()
    {
        using var doc = BlankPage.CreateDocument(595, 842, 1);
        using var output = new MemoryStream();
        Core.Pdf.Writing.PdfWriter.Write(doc, output, new Core.Pdf.Writing.PdfSaveOptions());
        return output.ToArray();
    }
}
