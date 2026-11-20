using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Localization;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Elements at the edge of the page (docs/FEHLERGEDAECHTNIS.md, No. 622; decided on 8.11.2026). A red image dragged over the right edge:
/// while dragging, what it has beyond the edge shows pale; let go, the question „Bild ragt über den Blattrand“ — „Behalten“ leaves it, and
/// beyond the edge nothing of it is drawn any more (only the frame of the selection); „Abschneiden“ cuts it off for real. A code beyond the
/// edge gets a note; „Nicht mehr fragen“ is saved. With PDFKNEAD_SCREENSHOTS the dragging is saved (85-blattrand-ziehen.png).
/// Before: the whole image was drawn beyond the edge, and the part there stayed in the file without a word.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class PageEdgeUiTests
{
    private static RgbaImage Red(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 3]) = (230, 255);
        return new RgbaImage(width, height, pixels);
    }

    /// <summary>The colour of a pixel of a rendered frame.</summary>
    private static (int R, int G, int B) Pixel(WriteableBitmap frame, Point at)
    {
        using var buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        var i = (int)at.Y * buffer.RowBytes + (int)at.X * 4;
        var bgra = buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888;
        return (bgra ? bytes[i + 2] : bytes[i], bytes[i + 1], bgra ? bytes[i] : bytes[i + 2]);
    }

    private static bool IsRed((int R, int G, int B) c) => c.R > 200 && c.G < 40 && c.B < 40;

    [Fact]
    public void ImageOverTheEdge_PaleWhileDragging_CutOrKept_CodeNoted()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(1).Build(), "blattrand.pdf");
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
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                var box = doc.Pages[0].Page.CropBox;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                var asked = new List<ChoiceRequest>();
                var answer = new ChoiceAnswer(0, false);
                vm.AskChoices = request =>
                {
                    asked.Add(request);
                    return Task.FromResult(answer);
                };

                // a red image of 100 × 50 pt, inside the page: no question
                var image = new ImageElement { Rect = new PdfRectangle(box.Right - 160, 380, box.Right - 60, 430), Image = Red(100, 50) };
                doc.AddElement(0, image, "Bild");
                Assert.Empty(asked);
                UiSession.Capture(window);   // the area of the image opens on the right: the view narrows first
                var target = view.PagePointToView(0, box.Right, 400);
                view.Offset += new Vector(target.X - view.Bounds.Width / 2, target.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);

                // dragged 100 pt to the right: half of it beyond the edge — pale there while dragging, full inside
                window.MouseMove(At(box.Right - 110, 405));
                window.MouseDown(At(box.Right - 110, 405), MouseButton.Left);
                window.MouseMove(At(box.Right - 60, 405));
                window.MouseMove(At(box.Right - 10, 405));
                var dragging = UiSession.Capture(window);
                if (UiSession.ScreenshotFolder() is { } folder) dragging.Save(Path.Combine(folder, "85-blattrand-ziehen.png"), PngBitmapEncoderOptions.Default);
                Assert.True(IsRed(Pixel(dragging, At(box.Right - 20, 405))));
                var pale = Pixel(dragging, At(box.Right + 20, 405));
                Assert.False(IsRed(pale));
                Assert.True(pale.R > pale.G + 20, $"blass rot erwartet, war {pale}");
                window.MouseUp(At(box.Right - 10, 405), MouseButton.Left);
                UiSession.Capture(window);

                // let go: the question; „Behalten“ — beyond the edge nothing of it any more
                var question = Assert.Single(asked);
                Assert.Equal(Tr.T("Bild ragt über den Blattrand"), question.Heading);
                Assert.Equal([Tr.T("Abschneiden")], question.Answers);
                Assert.Equal(Tr.T("Behalten"), question.Cancel);
                var kept = Assert.IsType<ImageElement>(doc.SelectedElement!.Value.Element);
                Assert.Equal(box.Right + 40, kept.Rect.Right, 1);   // 100 pt to the right: 40 pt beyond the edge
                doc.SelectedElement = null;
                var after = UiSession.Capture(window);
                Assert.True(IsRed(Pixel(after, At(box.Right - 20, 405))));
                Assert.False(IsRed(Pixel(after, At(box.Right + 20, 405))));
                Assert.True(Math.Abs(Pixel(after, At(box.Right + 20, 405)).R - Pixel(after, At(box.Right + 20, 300)).R) < 6);

                // the area of the image: „Am Blattrand abschneiden“ cuts it off for real (one undo step)
                doc.SelectedElement = new ElementRef(0, kept);
                Assert.True(vm.ElementProperties.ImageOverhangsPage);
                vm.ElementProperties.CropAtPageEdgeCommand.Execute(null);
                var cut = Assert.IsType<ImageElement>(doc.SelectedElement!.Value.Element);
                Assert.Equal(box.Right, cut.Rect.Right, 3);
                Assert.True(cut.Image.Width < 100);
                vm.UndoCommand.Execute(null);
                Assert.Equal(100, Assert.Single(doc.History.Pages[0].Elements.OfType<ImageElement>()).Image.Width);

                // „Abschneiden“ in the question, „Nicht mehr fragen“ ticked: cut, and no question after that
                answer = new ChoiceAnswer(1, true);
                var whole = doc.History.Pages[0].Elements.OfType<ImageElement>().Single();
                doc.ReplaceElement(new ElementRef(0, whole), whole with { Rect = new PdfRectangle(box.Right - 50, 380, box.Right + 50, 430) }, "verschieben");
                Assert.Equal(2, asked.Count);
                Assert.Equal(box.Right, doc.History.Pages[0].Elements.OfType<ImageElement>().Single().Rect.Right, 3);
                Assert.False(doc.AskCropAtPageEdge);
                var now = doc.History.Pages[0].Elements.OfType<ImageElement>().Single();
                doc.ReplaceElement(new ElementRef(0, now), now with { Rect = new PdfRectangle(box.Right - 20, 380, box.Right + 30, 430) }, "verschieben");
                Assert.Equal(2, asked.Count);

                // a code beyond the edge: a note
                doc.AddElement(0, new CodeElement { Rect = new PdfRectangle(box.Right - 30, 200, box.Right + 30, 260), Text = "Erika Mustermann" }, "Code");
                Assert.Equal(Tr.T("Der Code ragt über den Blattrand — so lässt er sich nicht vollständig lesen."), vm.Message);
            }
            finally
            {
                PdfKnead.Services.UserSettings.Update(s => s with { AskCropOverhang = true });
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
