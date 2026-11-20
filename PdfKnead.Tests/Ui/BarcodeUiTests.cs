using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Barcodes.Linear;
using PdfKnead.Core.Barcodes.Qr;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Ui;

/// <summary>The „QR-/Barcode“ tool: button on the Start tab, click on a code drawn as vector graphics or as a turned image, the panel, and that an address opens only after asking.</summary>
[Collection(nameof(UiCollection))]
public sealed class BarcodeUiTests
{
    /// <summary>Content stream that draws modules as filled rectangles: <paramref name="module"/> points each, lower left corner at (x, y).</summary>
    internal static string Vector(BitMatrix m, double x, double y, double module)
    {
        var s = new StringBuilder("q 0 g\n");
        for (var r = 0; r < m.Height; r++)
            for (var c = 0; c < m.Width; c++)
                if (m[c, r])
                    s.Append(FormattableString.Invariant($"{x + c * module:0.###} {y + (m.Height - 1 - r) * module:0.###} {module:0.###} {module:0.###} re\n"));
        return s.Append("f Q\n").ToString();
    }

    /// <summary>A bar code as a 1-bit image, one pixel per module and a few rows high.</summary>
    internal static PdfStream BarImage(bool[] modules, int rows)
    {
        var rowBytes = (modules.Length + 7) / 8;
        var data = new byte[rowBytes * rows];
        for (var r = 0; r < rows; r++)
            for (var i = 0; i < modules.Length; i++)
                if (!modules[i]) data[r * rowBytes + i / 8] |= (byte)(0x80 >> (i % 8));   // 1 = white in DeviceGray
        return new PdfStream(Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(modules.Length)), ("Height", PdfInteger.Of(rows)),
            ("BitsPerComponent", PdfInteger.Of(1)), ("ColorSpace", N("DeviceGray"))), data);
    }

    [Fact]
    public void ClickOnCodes_PanelShowsMeaning_AddressOpensOnlyAfterAsking()
    {
        const string address = "https://example.org/rechnung?nr=4711";
        var qr = QrEncoder.Encode(address, QrErrorCorrection.M);
        var ean = LinearCodes.Encode(LinearSymbology.Ean13, "4006381333931");
        // QR at (100, 550), 3 pt per module; EAN as an image turned by 90°, 1 pt per module, 40 pt high, at (420, 450)
        var content = Vector(qr, 100, 550, 3) + "q 0 95 -40 0 460 450 cm /Ean Do Q";
        var bytes = ImageInfoTests.OnePage(content, ("Ean", BarImage(ean, 4)));
        var pdf = TestFiles.Write(bytes, "codes.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                var asked = new List<(string Target, int Warnings)>();
                var opened = new List<string>();
                var answer = false;
                vm.ConfirmOpenAddress = (target, warnings) =>
                {
                    asked.Add((target, warnings.Count));
                    return Task.FromResult(answer);
                };
                vm.OpenAddress = target =>
                {
                    opened.Add(target);
                    return Task.CompletedTask;
                };
                string? copied = null;
                vm.SetClipboardText = text =>
                {
                    copied = text;
                    return Task.CompletedTask;
                };
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitPageCommand.Execute(null);   // the whole page is visible: every click lands on it
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                void Click(Point p)
                {
                    window.MouseDown(p, MouseButton.Left);
                    window.MouseUp(p, MouseButton.Left);
                }
                void Press(Control control)
                {
                    // the panel has just changed: lay it out first, so the button is where it is drawn
                    UiSession.Capture(window);
                    Assert.True(control.IsEffectivelyVisible);
                    Click(control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value);
                }
                async Task Until(Func<bool> done)
                {
                    var until = DateTime.UtcNow.AddSeconds(30);
                    while (!done() && DateTime.UtcNow < until)
                    {
                        UiSession.Capture(window);
                        await Task.Delay(20);
                    }
                    Assert.True(done());
                }

                // the button on the Start tab switches the tool on, the panel appears
                var button = window.GetVisualDescendants().OfType<RibbonToggleButton>().Single(b => b.Label == Tr.T("Bearbeiten") && b.IsEffectivelyVisible);
                Press(button);
                Assert.Equal(ViewTool.Edit, vm.Tool);
                var host = window.FindControl<Border>("EditHost")!;
                UiSession.Capture(window);
                Assert.True(host.IsEffectivelyVisible);

                // beside the codes: nothing is chosen
                Click(At(300, 700));
                await Task.Delay(300);
                UiSession.Capture(window);
                Assert.Null(doc.InspectedBarcode);
                Assert.Equal(EditTarget.None, doc.EditTarget);

                // the QR code drawn as rectangles
                var size = qr.Width * 3;
                Click(At(100 + size * 0.3, 550 + size * 0.6));
                await Until(() => doc.InspectedBarcode is not null);
                var code = doc.InspectedBarcode!;
                Assert.Equal(BarcodeFormat.Qr, code.Code.Code.Format);
                Assert.Equal(address, code.Code.Code.Text);
                Assert.Equal(Tr.T("Web-Adresse"), code.Meaning.Kind);
                Assert.Equal(1.06, code.Code.ModuleMillimetres, 1);   // 3 pt
                if (UiSession.ScreenshotFolder() is { } folder)
                    UiSession.Capture(window).Save(Path.Combine(folder, "95-qr-code.png"), PngBitmapEncoderOptions.Default);

                // open: asked first; "Abbrechen" opens nothing, "Öffnen" exactly the address shown
                var open = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "OpenAddressButton");
                Press(open);
                await Until(() => asked.Count == 1);
                Assert.Equal(address, asked[0].Target);
                Assert.Empty(opened);
                answer = true;
                Press(open);
                await Until(() => opened.Count == 1);
                Assert.Equal(address, opened[0]);

                // copy the content
                Press(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CopyCodeButton"));
                await Until(() => copied is not null);
                Assert.Equal(address, copied);

                // the EAN-13 image, turned
                Click(At(440, 497));
                await Until(() => doc.InspectedBarcode?.Code.Code.Format == BarcodeFormat.Ean13);
                Assert.Equal("4006381333931", doc.InspectedBarcode!.Code.Code.Text);
                Assert.Contains(doc.InspectedBarcode.Meaning.Rows, r => r.Value is "GS1 Deutschland" or "GS1 Germany");

                // under a bar that is not applied yet the content stays in the document
                doc.AddElement(0, new RedactionElement { Rect = new PdfRectangle(415, 445, 465, 550) }, "Test");
                doc.SelectedElement = null;   // the new mark would show its own panel at the same place
                UiSession.Capture(window);
                copied = null;
                Press(window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CopyCodeButton"));
                await Until(() => vm.Message?.Contains(Tr.T("Schwärzung"), StringComparison.Ordinal) == true);
                Assert.Null(copied);

                // Esc lets go of the code, a second Esc ends the tool
                view.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                UiSession.Capture(window);
                Assert.Null(doc.InspectedBarcode);
                Assert.Equal(ViewTool.Edit, vm.Tool);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                UiSession.Capture(window);
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                Assert.Null(doc.InspectedBarcode);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Redaction_CodesPartlyCoveredOrHoldingRedactedText_ReportedAndRedactedWhole()
    {
        // the IBAN in the text and in a GiroCode; a second code is covered by a bar in part only
        const string iban = "DE89 3704 0044 0532 0130 00";
        var giro = QrEncoder.Encode("BCD\n002\n1\nSCT\n\nMax Mustermann\nDE89370400440532013000\nEUR10\n\n\nTest\n", QrErrorCorrection.M);
        var other = QrEncoder.Encode("Lager 17, Fach 4", QrErrorCorrection.H);
        var pdf = TestPdf.Simple(1, "Codes");
        var content = "BT /F1 12 Tf 72 760 Td (IBAN " + iban + ") Tj ET\n" + Vector(giro, 330, 420, 3) + Vector(other, 100, 150, 4);
        pdf.Replace(4, TestPdf.FlateStream(Encoding.Latin1.GetBytes(content)));
        var file = TestFiles.Write(pdf.Build(), "codes-schwaerzen.pdf");
        var target = Path.Combine(Path.GetDirectoryName(file)!, "geschwaerzt.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([file]);
                var doc = vm.SelectedDocument!;
                var text = await doc.GetTextAsync(0);
                doc.Select(0, text.Find(iban)[0]);
                vm.RedactTextCommand.Execute(null);
                // the left half of the second code
                var size = other.Width * 4;
                doc.AddElement(0, new RedactionElement { Rect = new PdfRectangle(95, 145, 100 + size / 2.0, 155 + size) }, "Test");

                RedactionQuestion? asked = null;
                vm.ConfirmRedaction = question =>
                {
                    asked = question;
                    var dialog = new RedactionWindow(question);
                    dialog.Show();
                    UiSession.Capture(dialog);
                    if (UiSession.ScreenshotFolder() is { } folder)
                        UiSession.Capture(dialog).Save(Path.Combine(folder, "96-schwaerzen-codes.png"), PngBitmapEncoderOptions.Default);
                    var reply = dialog.Reply(RedactionAnswer.Apply);
                    dialog.Close();
                    return Task.FromResult(reply);
                };
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                await vm.SaveAsCommand.ExecuteAsync(null);

                Assert.NotNull(asked);
                Assert.Equal(2, asked!.Codes.Count);
                Assert.Contains(asked.Codes, c => c.Phrase is not null && c.Code.Code.Text.Contains("DE89370400440532013000") && !c.Partial);
                Assert.Contains(asked.Codes, c => c.Partial && c.Code.Code.Text == "Lager 17, Fach 4");
            }
            finally
            {
                window.Close();
            }
        });
        // afterwards no code of the page can be read any more, and the IBAN is gone from the text
        using var saved = PdfDocument.Open(File.ReadAllBytes(target));
        var page = saved.Pages[0];
        var list = Core.Pdf.Content.PageInterpreter.Build(saved, page);
        Assert.Empty(PdfKnead.Rendering.PageBarcodes.FindAll(saved, page, list, [], new PdfKnead.Rendering.ImageCache()));
        Assert.DoesNotContain("DE89", string.Concat(list.Text.Select(t => t.Text)));
    }

    [Fact]
    public void SearchOutOfTime_PageNamedAsUnchecked_NotTakenAsWithoutCodes()
    {
        // a page whose search takes longer than allowed (here: no time at all) — a crafted page must not hold up the redaction for ever,
        // but it must not pass as "no codes" either
        var pdf = TestPdf.Simple(2, "Zeit");
        var qr = QrEncoder.Encode("Lager 9", QrErrorCorrection.M);
        pdf.Replace(4, TestPdf.FlateStream(Encoding.Latin1.GetBytes("BT /F1 12 Tf 72 760 Td (Seite eins) Tj ET\n" + Vector(qr, 100, 300, 4))));
        var file = TestFiles.Write(pdf.Build(), "zeit.pdf");
        var before = DocumentViewModel.CodePageTime;
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([file]);
                var doc = vm.SelectedDocument!;
                DocumentViewModel.CodePageTime = TimeSpan.FromTicks(1);
                doc.AddElement(0, new RedactionElement { Rect = new PdfRectangle(90, 290, 120, 320) }, "Test");
                var skipped = new List<int>();
                Assert.Empty(await doc.FindCodesToRedactAsync(uncheckedPages: skipped));
                Assert.Equal([0], skipped);
                // the question says so
                var dialog = new RedactionWindow(new RedactionQuestion(1, 1, null, true) { UncheckedCodePages = skipped });
                dialog.Show();
                UiSession.Capture(dialog);
                var note = dialog.FindControl<TextBlock>("CodesUnchecked")!;
                Assert.True(note.IsEffectivelyVisible);
                Assert.Contains("Seite 1", note.Text);
                dialog.Close();
                // with time, it is checked (and not cached as empty before)
                DocumentViewModel.CodePageTime = before;
                skipped.Clear();
                Assert.Equal("Lager 9", Assert.Single(await doc.FindCodesToRedactAsync(uncheckedPages: skipped)).Code.Code.Text);
                Assert.Empty(skipped);
            }
            finally
            {
                DocumentViewModel.CodePageTime = before;
                window.Close();
            }
        });
    }

    [Fact]
    public void Bars_StartTheSearchAhead_QuestionNeedsNoSearchAfterwards()
    {
        var pdf = TestPdf.Simple(3, "Vorab");
        var qr = QrEncoder.Encode("Lager 9", QrErrorCorrection.M);
        pdf.Replace(4, TestPdf.FlateStream(Encoding.Latin1.GetBytes("BT /F1 12 Tf 72 760 Td (Seite eins) Tj ET\n" + Vector(qr, 100, 300, 4))));
        var file = TestFiles.Write(pdf.Build(), "vorab.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([file]);
                var doc = vm.SelectedDocument!;
                // without bars nothing is searched
                await Task.Delay(300);
                Assert.Equal(0, doc.CodePagesKnown);
                // a bar over part of the code: all pages are searched in the background
                doc.AddElement(0, new RedactionElement { Rect = new PdfRectangle(90, 290, 120, 320) }, "Test");
                var until = DateTime.UtcNow.AddSeconds(30);
                while ((doc.CodePagesKnown < 3 || doc.SearchingCodesAhead) && DateTime.UtcNow < until)
                {
                    UiSession.Capture(window);
                    await Task.Delay(20);
                }
                Assert.Equal(3, doc.CodePagesKnown);
                var codes = await doc.FindCodesToRedactAsync();
                Assert.Equal("Lager 9", Assert.Single(codes).Code.Code.Text);
                Assert.True(codes[0].Partial);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(0.25)]   // the smallest QR module printers recommend
    [InlineData(0.33)]
    public void FindAll_SmallestCommonSizes(double qrModuleMm)
    {
        // a QR code at the given module size, an EAN-13 at nominal size (0.33 mm) and at 80 % (0.26 mm), beside text
        var mm = 72 / 25.4;
        var qr = QrEncoder.Encode("https://example.org/klein", QrErrorCorrection.M);
        var ean = LinearCodes.Encode(LinearSymbology.Ean13, "4006381333931");
        string Bars(bool[] modules, double x, double y, double module, double height)
        {
            var b = new StringBuilder("q 0 g\n");
            for (var i = 0; i < modules.Length; i++)
                if (modules[i]) b.Append(FormattableString.Invariant($"{x + i * module:0.###} {y:0.###} {module:0.###} {height:0.###} re\n"));
            return b.Append("f Q\n").ToString();
        }
        var pdf = TestPdf.Simple(1, "Klein");
        var content = "BT /F1 9 Tf 72 780 Td (Rechnung 4711 vom 27.10.2026, bitte bis zum 10.11. bezahlen) Tj ET\n"
                      + Vector(qr, 72, 650, qrModuleMm * mm) + Bars(ean, 200, 650, 0.33 * mm, 60) + Bars(ean, 200, 520, 0.264 * mm, 48);
        pdf.Replace(4, TestPdf.FlateStream(Encoding.Latin1.GetBytes(content)));
        using var doc = PdfDocument.Open(pdf.Build());
        var page = doc.Pages[0];
        var list = Core.Pdf.Content.PageInterpreter.Build(doc, page);
        var found = PdfKnead.Rendering.PageBarcodes.FindAll(doc, page, list, [], new PdfKnead.Rendering.ImageCache());
        Assert.Contains(found, f => f.Code.Text == "https://example.org/klein");
        Assert.Equal(2, found.Count(f => f.Code.Text == "4006381333931"));
        Assert.Equal(3, found.Count);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(-55)]
    public void FindAll_TurnedBarCode(double angle)
    {
        // a bar code turned by an odd angle: a bar over its upper half leaves it readable along the bars, so it must be found to be reported
        var ean = LinearCodes.Encode(LinearSymbology.Ean13, "4006381333931");
        double rad = angle * Math.PI / 180, w = ean.Length, h = 30;
        var b = new StringBuilder(FormattableString.Invariant($"q {Math.Cos(rad):0.####} {Math.Sin(rad):0.####} {-Math.Sin(rad):0.####} {Math.Cos(rad):0.####} 250 400 cm 0 g\n"));
        for (var i = 0; i < ean.Length; i++)
            if (ean[i]) b.Append(FormattableString.Invariant($"{i - w / 2} {-h / 2} 1 {h} re\n"));
        b.Append("f Q\n");
        var pdf = TestPdf.Simple(1, "Schräg");
        pdf.Replace(4, TestPdf.FlateStream(Encoding.Latin1.GetBytes(b.ToString())));
        using var doc = PdfDocument.Open(pdf.Build());
        var list = Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]);
        var found = PdfKnead.Rendering.PageBarcodes.FindAll(doc, doc.Pages[0], list, [], new PdfKnead.Rendering.ImageCache());
        Assert.Equal("4006381333931", Assert.Single(found).Code.Text);
    }

    [Theory]
    [InlineData(400, 3)]
    [InlineData(3, 400)]
    [InlineData(2000, 2.9)]
    public void FindAll_FlatPage_NoError(double width, double height)
    {
        // a page only a few points high or wide: at 3 pixels per point fewer rows than one stripe of the rendering (fuzz find)
        var pdf = new TestPdf();
        var pages = pdf.Add(new PdfDictionary());
        var stream = pdf.Add(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("0 g 0 0 1 1 re f")));
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("Contents", TestPdf.Ref(stream)),
            ("MediaBox", TestPdf.Box(0, 0, width, height))));
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pages))));
        using var doc = PdfDocument.Open(pdf.Build());
        var list = Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]);
        Assert.Empty(PdfKnead.Rendering.PageBarcodes.FindAll(doc, doc.Pages[0], list, [], new PdfKnead.Rendering.ImageCache()));
    }

    [Theory]
    [InlineData(25)]
    [InlineData(30)]
    [InlineData(37.4)]
    public void FindAll_ShortBarCodeWithDigitsBelow(double height)
    {
        // a flat EAN-8 with its digits: they belong to the area the page search finds, so the bars cover only three quarters of its
        // height — the bars must still count as bars
        var code = new CodeElement { Format = BarcodeFormat.Ean8, Text = "96385074", Rect = new PdfRectangle(31, 400, 124, 400 + height) };
        using var blank = BlankPage.CreateDocument(595, 842, 1);
        using var output = new MemoryStream();
        Core.Pdf.Writing.PdfWriter.Write(blank, [PageItem.From(blank)[0].WithElements([code])], output, new Core.Pdf.Writing.PdfSaveOptions());
        using var doc = PdfDocument.Open(output.ToArray());
        var list = Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]);
        var found = PdfKnead.Rendering.PageBarcodes.FindAll(doc, doc.Pages[0], list, [], new PdfKnead.Rendering.ImageCache());
        Assert.Equal("96385074", Assert.Single(found).Code.Text);
    }
}
