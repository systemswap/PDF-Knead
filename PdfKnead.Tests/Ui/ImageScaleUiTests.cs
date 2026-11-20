using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Sizing a large picture put on the page (reported on 9.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 672): every step of the drag started a
/// new smooth (Lanczos) version of the picture in the background and dropped it at the next — each reading a copy of the whole decoded picture
/// (48 MB for 12 megapixels) and resampling it: 150–220 MB and several full garbage collections for 30 steps, the surface stalled, more so with a
/// polyline measured on the page. Now none starts while the drag lasts; the newest version is shown scaled, the sharp one follows on release —
/// and it is made of the picture's own pixels, without a copy.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class ImageScaleUiTests
{
    [Fact]
    public void SizingAPicture_NoSmoothVersionPerStep_SharpAfterRelease()
    {
        var pdf = TestFiles.Write(Pdf.CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<<>>>>",
            [6] = ("", ""),
        }), "bild-skalieren.pdf");
        const int w = 2400, h = 1800;
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)(i / 4 % w), (byte)(i / 4 / w), 90, 255);
        var photo = new RgbaImage(w, h, pixels);
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
                var view = window.GetVisualDescendants().OfType<DocumentView>().Single();
                var image = new ImageElement { Rect = new PdfRectangle(60, 300, 460, 600), Image = photo };
                doc.AddElement(0, image, "Bild einfügen");
                // the display image knows the picture's pixels (its Lanczos versions need no copy)
                var form = ElementAppearance.For(image).Form;
                var xobjects = form.Dictionary.GetDictionary(Core.Pdf.Objects.PdfName.Get("Resources"))!.GetDictionary(Core.Pdf.Objects.PdfName.Get("XObject"))!;
                var shown = xobjects.Entries.Select(e => xobjects.Get(e.Key)).OfType<Core.Pdf.Objects.PdfStream>().First();
                Assert.Same(photo, ElementAppearance.DisplayImage(shown));
                var images = doc.Renderer.Images;
                // as in use: the picture has been on the page a while, its smooth version for this size is there
                for (var k = 0; k < 50 && images.SmoothVersionsStarted == 0; k++)
                {
                    Thread.Sleep(20);
                    UiSession.Capture(window);
                }
                Thread.Sleep(500);
                UiSession.Capture(window);

                var middle = view.TranslatePoint(view.PagePointToView(0, 260, 450), window)!.Value;
                window.MouseDown(middle, MouseButton.Left);
                window.MouseUp(middle, MouseButton.Left);
                Assert.Same(image, doc.SelectedElement?.Element);
                var corner = view.TranslatePoint(view.PagePointToView(0, 460, 600), window)!.Value;
                var before = images.SmoothVersionsStarted;
                window.MouseMove(corner);
                window.MouseDown(corner, MouseButton.Left);
                for (var k = 1; k <= 30; k++)
                {
                    window.MouseMove(corner + new Point(k * 4, -k * 3));
                    UiSession.Capture(window);
                    Assert.True(images.Holding);
                }
                // before: a new version at every eighth of an octave the size passed (several in these 30 steps)
                Assert.Equal(before, images.SmoothVersionsStarted);
                window.MouseUp(corner + new Point(120, -90), MouseButton.Left);
                UiSession.Capture(window);
                Assert.False(images.Holding);
                Assert.True(doc.Pages[0].Item.Elements.OfType<ImageElement>().Single().Rect.Right > 500);
                // on release the sharp version for the new size (the view's, perhaps one for the page in the side bar) — and then none while
                // nothing changes (the view redrawing does not start versions over and over)
                var released = images.SmoothVersionsStarted;
                Assert.InRange(released - before, 1, 2);
                for (var k = 0; k < 20; k++)
                {
                    Thread.Sleep(50);
                    view.InvalidateVisual();
                    UiSession.Capture(window);
                }
                Assert.Equal(released, images.SmoothVersionsStarted);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
