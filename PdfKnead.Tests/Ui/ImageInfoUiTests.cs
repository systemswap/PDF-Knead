using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>UI tests for the image information tool: button on the Start tab, click on an image, next image below on a second click, Esc.</summary>
[Collection(nameof(UiCollection))]
public sealed class ImageInfoUiTests
{
    private static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    [Fact]
    public void ClickOnImage_ShowsDetails_AltClickNextImage_EscEnds()
    {
        // A photo with EXIF data, a rotated second image partly on top of it, and a blue rectangle above both (which must not interfere)
        var photo = ImageInfoTests.WithSegments(ImageInfoTests.Jpeg(300, 150, 85), ImageInfoTests.Exif(true));
        var bytes = ImageInfoTests.OnePage(
            "q 216 0 0 108 72 600 cm /Unten Do Q q 0 72 -72 0 200 620 cm /Oben Do Q 0 0 1 rg 60 590 300 20 re f",
            ("Unten", ImageInfoTests.JpegImage(photo, 300, 150)), ("Oben", ImageInfoTests.JpegImage(ImageInfoTests.Jpeg(100, 100, 60), 100, 100)));
        var pdf = TestFiles.Write(bytes, "bildinfo.pdf");
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
                void Click(Point p)
                {
                    window.MouseDown(p, MouseButton.Left);
                    window.MouseUp(p, MouseButton.Left);
                }
                async Task<InspectedImage> Wait(Func<InspectedImage?, bool> done)
                {
                    var until = DateTime.UtcNow.AddSeconds(20);
                    while (!done(doc.InspectedImage) && DateTime.UtcNow < until)
                    {
                        UiSession.Capture(window);
                        await Task.Delay(20);
                    }
                    return doc.InspectedImage!;
                }

                // The Image info button on the Start tab: tool on, right-hand panel with a hint
                var button = window.GetVisualDescendants().OfType<RibbonToggleButton>().Single(b => b.Label == Tr.T("Bearbeiten") && b.IsEffectivelyVisible);
                Click(button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value);
                Assert.Equal(ViewTool.Edit, vm.Tool);
                var host = window.FindControl<Border>("EditHost")!;
                UiSession.Capture(window);
                Assert.True(host.IsEffectivelyVisible);
                Assert.NotEmpty(doc.EditHint);

                // Click beside the images (top left, visible and not under the right-hand panel): nothing is chosen
                Click(At(100, 780));
                await Task.Delay(300);
                UiSession.Capture(window);
                Assert.Null(doc.InspectedImage);
                Assert.Equal(EditTarget.None, doc.EditTarget);

                // Click into the photo, below the rectangle: the photo with its EXIF data
                Click(At(100, 605));
                var photoInfo = await Wait(i => i is { Info.Width: 300 });
                Assert.Equal((1, 1), (photoInfo.Number, photoInfo.Of));
                Assert.Contains(photoInfo.Sections.SelectMany(s => s.Rows), r => r.Value == "Mustermarke Scan 100");
                Shoot(window, "90-bildinfo");

                // Where both overlap: first the upper (rotated) image, then Alt+click at the same spot gives the one below
                Click(At(170, 640));
                var top = await Wait(i => i is { Info.Width: 100 });
                Assert.Equal((1, 2), (top.Number, top.Of));
                Assert.Equal(90, top.Info.Rotation);
                Assert.Equal(EditTarget.Image, doc.EditTarget);
                Shoot(window, "91-bildinfo-oben");
                window.MouseDown(At(170, 640), MouseButton.Left, RawInputModifiers.Alt);
                window.MouseUp(At(170, 640), MouseButton.Left, RawInputModifiers.Alt);
                var below = await Wait(i => i is { Number: 2 });
                Assert.Equal(300, below.Info.Width);
                Assert.Contains("2", doc.ImageInfoPosition);

                // Esc: first the choice goes, the second Esc ends the tool and closes the panel
                view.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                UiSession.Capture(window);
                Assert.Equal(ViewTool.Edit, vm.Tool);
                Assert.Null(doc.InspectedImage);
                Assert.Null(doc.SelectedImages);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                UiSession.Capture(window);
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                Assert.False(host.IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SaveImage_OriginalBytes_CopyImage_BothRefusedUnderABar()
    {
        var photo = ImageInfoTests.Jpeg(300, 150, 85);
        var bytes = ImageInfoTests.OnePage("q 216 0 0 108 72 600 cm /Foto Do Q", ("Foto", ImageInfoTests.JpegImage(photo, 300, 150)));
        var pdf = TestFiles.Write(bytes, "bild.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "gespeichert.jpg");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                string? suggested = null;
                (int Width, int Height)? copied = null;
                vm.PickImageSavePath = (_, name, _) =>
                {
                    suggested = name;
                    return Task.FromResult<string?>(target);
                };
                vm.SetClipboardImage = (w, h, _) =>
                {
                    copied = (w, h);
                    return Task.CompletedTask;
                };
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                void Click(Point p)
                {
                    window.MouseDown(p, MouseButton.Left);
                    window.MouseUp(p, MouseButton.Left);
                }
                void Press(string name)
                {
                    var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);
                    Assert.True(button.IsEffectivelyVisible);
                    Click(button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value);
                }
                async Task Until(Func<bool> done)
                {
                    var until = DateTime.UtcNow.AddSeconds(20);
                    while (!done() && DateTime.UtcNow < until)
                    {
                        UiSession.Capture(window);
                        await Task.Delay(20);
                    }
                    Assert.True(done());
                }

                vm.Tool = ViewTool.Edit;
                Click(view.TranslatePoint(view.PagePointToView(0, 150, 650), window)!.Value);
                await Until(() => doc.InspectedImage is not null);
                UiSession.Capture(window);

                // Save: the JPEG exactly as it is in the PDF, the name from the file, page and image
                Press("SaveImageButton");
                await Until(() => File.Exists(target));
                Assert.Equal(photo, File.ReadAllBytes(target));
                Assert.Equal(Tr.T("{0} Seite {1} Bild", "bild", 1) + ".jpg", suggested);

                // Copy: as drawn, at full size
                Press("CopyImageButton");
                await Until(() => copied is not null);
                Assert.Equal((300, 150), copied);

                // A bar that has not been applied over part of the image: neither leaves the document
                File.Delete(target);
                copied = null;
                doc.AddElement(0, new RedactionElement { Rect = new PdfRectangle(100, 620, 140, 640) }, "Test");
                doc.SelectedElement = null;   // the new mark would show its own panel at the same place
                UiSession.Capture(window);
                Press("SaveImageButton");
                Press("CopyImageButton");
                await Task.Delay(200);
                UiSession.Capture(window);
                Assert.False(File.Exists(target));
                Assert.Null(copied);
                Assert.Contains(Tr.T("Schwärzungen anwenden"), vm.Message);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CopiedImage_StillReadableLater()
    {
        // The clipboard hands the image over only when another program asks for it: the bitmap must still be there then
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 800, Height = 600 };
            window.Show();
            try
            {
                byte[] rgba = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 10, 20, 30, 255];
                await vm.SetClipboardImage!(2, 2, rgba);
                UiSession.Capture(window);
                using var read = await window.Clipboard!.TryGetBitmapAsync();
                Assert.NotNull(read);
                Assert.Equal(new PixelSize(2, 2), read!.PixelSize);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
