using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Bearbeiten“ (KONZEPT.md, section 5.22): one tool for text, images and codes. The order of a click, Alt+click for what lies underneath,
/// the double-click for a page-filling scan, and Esc that first lets go of the choice.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class EditToolUiTests
{
    /// <summary>
    /// An A4 page: a JPEG that fills the page (a scan), a smaller JPEG at x 400–500, y 600–660, and a text line over that smaller image.
    /// </summary>
    private static byte[] Page()
    {
        var pdf = new TestPdf();
        var scan = pdf.Add(ImageInfoTests.JpegImage(ImageInfoTests.Jpeg(120, 160, 70), 120, 160));
        var small = pdf.Add(ImageInfoTests.JpegImage(ImageInfoTests.Jpeg(100, 60, 70), 100, 60));
        var font = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica")),
            ("Encoding", TestPdf.N("WinAnsiEncoding")));
        var content = "q 595.28 0 0 841.89 0 0 cm /Scan Do Q q 100 0 0 60 400 600 cm /Klein Do Q " +
                      "BT /F1 12 Tf 405 625 Td (Rechnung Nr. 4711) Tj ET";
        var stream = pdf.Add(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content)));
        var pages = pdf.Add(new PdfDictionary());
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("Contents", TestPdf.Ref(stream)),
            ("MediaBox", TestPdf.Box(0, 0, 595.28, 841.89)),
            ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", font))),
                ("XObject", TestPdf.Dict(("Scan", TestPdf.Ref(scan)), ("Klein", TestPdf.Ref(small))))))));
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pages))));
        return pdf.Build();
    }

    [Fact]
    public void ClickOrder_TextBeforeImage_AltClickUnderneath_ScanByDoubleClick_EscLetsGoFirst()
    {
        var pdf = TestFiles.Write(Page(), "bearbeiten.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                void Click(Point p, RawInputModifiers modifiers = RawInputModifiers.None)
                {
                    window.MouseDown(p, MouseButton.Left, modifiers);
                    window.MouseUp(p, MouseButton.Left, modifiers);
                }
                async Task Until(Func<bool> done, string what)
                {
                    var until = DateTime.UtcNow.AddSeconds(20);
                    while (!done() && DateTime.UtcNow < until)
                    {
                        UiSession.Capture(window);
                        await Task.Delay(20);
                    }
                    Assert.True(done(), what);
                }
                var messages = new List<string>();
                doc.MessageRequested += messages.Add;

                // the button on the Start tab: tool on, mode bar, the tool's panel with its hint
                var button = window.GetVisualDescendants().OfType<RibbonToggleButton>().Single(b => b.Label == Tr.T("Bearbeiten") && b.IsEffectivelyVisible);
                Click(button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value);
                Assert.Equal(ViewTool.Edit, vm.Tool);
                Assert.True(vm.ModeBannerVisible);
                Assert.True(window.FindControl<Border>("EditHost")!.IsEffectivelyVisible);
                Assert.Equal(EditTarget.None, doc.EditTarget);
                await Until(() => doc.BlocksOn(0) is { Count: 1 }, "Textblock erkannt");

                // on the text over the small image: the text is edited, not the image
                Click(At(430, 629));
                await Until(() => doc.RichEditor is not null, "Text wird bearbeitet");
                Assert.Equal(EditTarget.Text, doc.EditTarget);
                Assert.Null(doc.SelectedImages);
                Assert.False(doc.IsEditPanelVisible);   // the format panel of the text is there instead

                // Alt+click at the same spot: what lies underneath — the small image; again: the scan; again: round to the text
                Click(At(430, 629), RawInputModifiers.Alt);
                await Until(() => doc.SelectedImages is not null, "Bild gewählt");
                Assert.Equal(EditTarget.Image, doc.EditTarget);
                Assert.Null(doc.RichEditor);
                var box = doc.SelectedImagesBox!.Value;
                Assert.Equal(400, box.Left, 0);
                Assert.Equal(100, box.Width, 0);
                await Until(() => doc.InspectedImage is { Info.Width: 100 }, "Angaben zum kleinen Bild");
                Click(At(430, 629), RawInputModifiers.Alt);
                await Until(() => doc.SelectedImagesBox is { Width: > 500 }, "der Scan gewählt");
                Click(At(430, 629), RawInputModifiers.Alt);
                await Until(() => doc.RichEditor is not null, "wieder der Text");

                // beside everything on the scan (within the window): a single click passes the scan by (said once), a double-click chooses it
                view.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                UiSession.Capture(window);
                Assert.Null(doc.RichEditor);
                Assert.Equal(ViewTool.Edit, vm.Tool);
                Click(At(200, 700));
                // waits for the notice instead of a fixed time: under load (full test run beside a measurement) 300 ms were too short
                await Until(() => messages.Any(m => m.Contains("Doppelklick", StringComparison.Ordinal)), "Hinweis auf den Doppelklick");
                Assert.Equal(EditTarget.None, doc.EditTarget);
                var said = messages.Count;
                window.MouseDown(At(200, 700), MouseButton.Left);
                window.MouseUp(At(200, 700), MouseButton.Left);
                window.MouseDown(At(200, 700), MouseButton.Left);
                window.MouseUp(At(200, 700), MouseButton.Left);
                await Until(() => doc.SelectedImagesBox is { Width: > 500 }, "Doppelklick wählt den Scan");
                Assert.Equal(said, messages.Count);   // the notice came once

                // Esc lets go of the choice, the tool stays on; a second Esc ends it
                view.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                UiSession.Capture(window);
                Assert.Null(doc.SelectedImages);
                Assert.Equal(EditTarget.None, doc.EditTarget);
                Assert.Equal(ViewTool.Edit, vm.Tool);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                UiSession.Capture(window);
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                Assert.False(vm.ModeBannerVisible);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>Alt+click walks through every object under a point, the smallest first; a point in a quadrilateral is recognised whichever way round its corners go.</summary>
    [Fact]
    public void AllObjectsAtAPoint_SmallestFirst_InsideQuadrilateral()
    {
        using var doc = Core.Pdf.Document.PdfDocument.Open(Page());
        var objects = ContentObjects.Find(doc, doc.Pages[0]);
        var at = objects.AllAt(430, 629, 0.5);
        Assert.Equal(2, at.Count);
        Assert.True(objects.VisibleOf(at[0]).Width < objects.VisibleOf(at[1]).Width);
        Assert.Equal(at[0], objects.At(430, 629, 0.5));
        Assert.Empty(objects.AllAt(-10, -10, 0.5));

        (double, double)[] quad = [(10, 10), (30, 12), (28, 32), (8, 30)];
        Assert.True(DocumentViewModel.Inside(quad, 20, 20));
        Assert.True(DocumentViewModel.Inside([.. quad.Reverse()], 20, 20));
        Assert.False(DocumentViewModel.Inside(quad, 40, 20));
        Assert.False(DocumentViewModel.Inside(quad, 5, 20));
    }

    /// <summary>
    /// While reading (not in „Bearbeiten“), a click on an image beside the text shows its details on the right with a frame, as the former
    /// „Bildinfo“ button did; a click on text does not, Esc closes them (KONZEPT.md, sections 5.15 and 5.22).
    /// </summary>
    [Fact]
    public void Reading_ClickOnImage_ShowsItsDetails_EscCloses()
    {
        var pdf = TestFiles.Write(Page(), "lesen-bildinfo.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                async Task Until(Func<bool> done, string what)
                {
                    var until = DateTime.UtcNow.AddSeconds(20);
                    while (!done() && DateTime.UtcNow < until)
                    {
                        UiSession.Capture(window);
                        await Task.Delay(20);
                    }
                    Assert.True(done(), what);
                }
                var host = window.FindControl<Border>("ImageInfoHost")!;

                // on the small image beside its text line: its details, the panel with its head
                window.MouseDown(At(480, 610), MouseButton.Left);
                window.MouseUp(At(480, 610), MouseButton.Left);
                await Until(() => doc.InspectedImage is { Info.Width: 100 }, "Angaben zum kleinen Bild");
                Assert.True(doc.IsReadingImageInfo);
                Assert.True(host.IsEffectivelyVisible);
                Assert.Equal(ViewTool.SelectText, vm.Tool);

                // Esc closes them, the tool stays
                view.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                UiSession.Capture(window);
                Assert.Null(doc.InspectedImage);
                Assert.False(host.IsEffectivelyVisible);
                Assert.Equal(ViewTool.SelectText, vm.Tool);

                // switching „Bearbeiten“ on closes them as well
                window.MouseDown(At(480, 610), MouseButton.Left);
                window.MouseUp(At(480, 610), MouseButton.Left);
                await Until(() => doc.InspectedImage is not null, "wieder gezeigt");
                vm.ToggleEditCommand.Execute(null);
                UiSession.Capture(window);
                Assert.Null(doc.InspectedImage);
                Assert.False(host.IsEffectivelyVisible);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Layers in „Bearbeiten“ (KONZEPT.md, section 5.22 „Ebenen“): a square of the page content under another one shows „Ebene 1 von 2“;
    /// „Nach vorn“ on the Start tab puts it in front (it stays chosen, „Ebene 2 von 2“), Ctrl+Page Down puts it back; Ctrl+Z undoes.
    /// </summary>
    [Fact]
    public void Layers_OfContentObjects_FromTheStartTab()
    {
        var pdf = new TestPdf();
        var stream = pdf.Add(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("q 1 0 0 rg 100 500 100 100 re f Q q 0 1 0 rg 150 450 100 100 re f Q")));
        var pages = pdf.Add(new PdfDictionary());
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("Contents", TestPdf.Ref(stream)),
            ("MediaBox", TestPdf.Box(0, 0, 595.28, 841.89))));
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pages))));
        var file = TestFiles.Write(pdf.Build(), "ebenen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([file]);
                var doc = vm.SelectedDocument!;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                vm.ToggleEditCommand.Execute(null);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                async Task Until(Func<bool> done, string what)
                {
                    var until = DateTime.UtcNow.AddSeconds(20);
                    while (!done() && DateTime.UtcNow < until)
                    {
                        UiSession.Capture(window);
                        await Task.Delay(20);
                    }
                    Assert.True(done(), what);
                }
                bool RedAt(double x, double y)
                {
                    using var bitmap = ContentImageTests.Render(doc.History.Pages[0].Source, doc.History.Pages[0].Page);
                    return ContentImageTests.Red(ContentImageTests.At(bitmap, doc.History.Pages[0].Page, x, y));
                }
                await Until(() => doc.BlocksOn(0) is not null && doc.ContentObjectsOn(0) is not null, "Seite gelesen");

                // the red square (at its free corner): a click chooses both squares as one graphic, Alt+click the red one alone — behind the green one
                window.MouseDown(At(120, 580), MouseButton.Left);
                window.MouseUp(At(120, 580), MouseButton.Left);
                await Until(() => doc.SelectedImages is { Keys.Count: 2 }, "die Grafik gewählt");
                Assert.Null(doc.LayerLabel());   // nothing else lies there
                window.MouseDown(At(120, 580), MouseButton.Left, RawInputModifiers.Alt);
                window.MouseUp(At(120, 580), MouseButton.Left, RawInputModifiers.Alt);
                await Until(() => doc.SelectedImages is { Keys.Count: 1 }, "rotes Quadrat allein gewählt");
                Assert.Equal(Tr.T("Ebene {0} von {1}", 1, 2), doc.LayerLabel());
                Assert.False(RedAt(175, 525));

                // „Nach vorn“ on the Start tab
                var forward = window.GetVisualDescendants().OfType<RibbonButton>().First(b => b.Label == Tr.T("Nach vorn") && b.IsEffectivelyVisible);
                Assert.True(forward.Command!.CanExecute(forward.CommandParameter));
                var centre = forward.TranslatePoint(new Point(forward.Bounds.Width / 2, forward.Bounds.Height / 2), window)!.Value;
                window.MouseDown(centre, MouseButton.Left);
                window.MouseUp(centre, MouseButton.Left);
                await Until(() => RedAt(175, 525), "rot liegt vorn");
                await Until(() => doc.LayerLabel() == Tr.T("Ebene {0} von {1}", 2, 2), "Anzeige Ebene 2 von 2");
                Assert.NotNull(doc.SelectedImages);
                UiSession.Capture(window);

                // Ctrl+Page Down: one back again; Ctrl+Z, Ctrl+Z: as at the start
                view.Focus();
                window.KeyPress(Key.PageDown, RawInputModifiers.Control, PhysicalKey.PageDown, null);
                await Until(() => !RedAt(175, 525), "wieder hinten");
                vm.UndoCommand.Execute(null);
                Assert.True(RedAt(175, 525));
                vm.UndoCommand.Execute(null);
                Assert.False(RedAt(175, 525));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
