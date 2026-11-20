using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Bilder bearbeiten“ with the mouse (KONZEPT.md, section 5.20): the ribbon button, a click on a tile chooses the whole picture, dragging
/// moves it, a handle resizes it, the arrow keys move it in one undo step, the panel sets the width; saved, the file shows the change.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class ContentImageUiTests
{
    private static void Drag(Window window, Point from, Point to)
    {
        Thread.Sleep(600);
        window.MouseDown(from, MouseButton.Left);
        for (var k = 1; k <= 8; k++)
            window.MouseMove(new Point(from.X + (to.X - from.X) * k / 8, from.Y + (to.Y - from.Y) * k / 8));
        window.MouseUp(to, MouseButton.Left);
        UiSession.Capture(window);
    }

    private static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static Point Centre(Visual target, Window window) =>
        target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;

    [Fact]
    public void MoveAndResizeAPicture_WithTheMouse()
    {
        var pdf = TestFiles.Write(ContentImageTests.Build(), "bilder-bearbeiten.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "bilder-bearbeiten-ergebnis.pdf");
        File.Delete(target);
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                UiSession.Capture(window);
                var button = window.GetVisualDescendants().OfType<RibbonToggleButton>().Single(b => b.IsEffectivelyVisible && b.Label == Tr.T("Bearbeiten"));
                Thread.Sleep(600);
                window.MouseDown(Centre(button, window), MouseButton.Left);
                window.MouseUp(Centre(button, window), MouseButton.Left);
                UiSession.Capture(window);
                Assert.Equal(ViewTool.Edit, vm.Tool);
                Assert.True(vm.ModeBannerVisible);
                // the objects are found in the background (the view drew the page and asked for them), then kept for the page
                Assert.Equal(7, (await doc.ContentObjectsOnAsync(0)).Count);
                Assert.Same(doc.ContentObjectsOn(0), doc.ContentObjectsOn(0));
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;

                // a press on the left tile chooses the picture (both tiles) and dragging moves it 150 pt down
                Drag(window, At(120, 580), At(120, 430));
                Assert.Equal(2, doc.SelectedImages!.Keys.Count);
                var box = doc.SelectedImagesBox!.Value;
                Assert.Equal(350, box.Top, 0);            // visible part 500…600 before, the clip moved with it
                Assert.Equal(100, box.Left, 0);
                Assert.Contains("2", doc.ImagePartsText);
                Shoot(window, "bilder-bearbeiten-1-verschoben");

                // the right handle (middle of the right edge) widens it; the chain is closed, the height grows along
                Drag(window, At(box.Right, (box.Top + box.Bottom) / 2), At(box.Right + 100, (box.Top + box.Bottom) / 2));
                box = doc.SelectedImagesBox!.Value;
                Assert.Equal(300, box.Width, 0);
                Assert.Equal(150, box.Height, 0);
                Shoot(window, "bilder-bearbeiten-2-breiter");

                // three presses of the arrow key: 3 pt to the right, one undo step
                var before = box;
                view.Focus();
                for (var i = 0; i < 3; i++) window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                UiSession.Capture(window);
                Assert.Equal(before.Left + 3, doc.SelectedImagesBox!.Value.Left, 1);
                doc.Undo();
                Assert.Equal(before.Left, doc.SelectedImagesBox!.Value.Left, 1);

                // width typed in the panel: 50 mm, the upper left corner stays
                doc.ImageWidthMm = "50";
                Assert.Equal(50 * 72 / 25.4, doc.SelectedImagesBox!.Value.Width, 1);
                Assert.Equal(before.Bottom, doc.SelectedImagesBox!.Value.Bottom, 1);

                // the slider turns freely about a fixed centre: several values in a row are one undo step, the centre does not wander
                var beforeTurn = doc.SelectedImagesBox!.Value;
                foreach (var degrees in new[] { 10.0, 25, -33.3, -47 }) doc.ImageAngleDegrees = degrees;
                Assert.Equal(-47, doc.ImageAngleDegrees, 1);
                Assert.Equal("-47", doc.ImageAngle);
                var turnedBox = doc.SelectedImagesBox!.Value;
                Assert.Equal((beforeTurn.Left + beforeTurn.Right) / 2, (turnedBox.Left + turnedBox.Right) / 2, 1);
                doc.Undo();
                Assert.Equal(0, doc.ImageAngleDegrees, 1);
                Assert.Equal(beforeTurn.Left, doc.SelectedImagesBox!.Value.Left, 1);

                // „Bild kopieren“ and „Bild speichern …“ in the panel: the picture of both tiles as one image
                (int Width, int Height)? copied = null;
                vm.SetClipboardImage = (w, h, _) => { copied = (w, h); return Task.CompletedTask; };
                var picture = Path.Combine(Path.GetDirectoryName(pdf)!, "bilder-bearbeiten-bild.png");
                File.Delete(picture);
                vm.PickImageSavePath = (_, name, _) =>
                {
                    Assert.EndsWith(".png", name);
                    return Task.FromResult<string?>(picture);
                };
                var panel = window.GetVisualDescendants().OfType<ContentImagePanel>().Single();
                foreach (var name in new[] { "CopyImageButton", "SaveImageButton" })
                {
                    var b = panel.FindControl<Button>(name)!;
                    Assert.True(b.IsEffectivelyVisible, name);
                    Thread.Sleep(600);
                    window.MouseDown(Centre(b, window), MouseButton.Left);
                    window.MouseUp(Centre(b, window), MouseButton.Left);
                    for (var wait = 0; wait < 100 && (name == "CopyImageButton" ? copied is null : !File.Exists(picture)); wait++)
                    {
                        await Task.Delay(50);
                        UiSession.Capture(window);
                    }
                }
                Assert.True(copied is not null, $"Meldung: {vm.Message}");
                Assert.Equal((20, 10), copied);
                Assert.True(File.Exists(picture));

                // a redaction not yet applied over the picture: copying and saving are refused, as with „Bildinfo“
                var over = doc.SelectedImagesBox!.Value;
                doc.AddElement(0, new RedactionElement { Rect = new PdfRectangle(over.Left + 5, over.Top + 5, over.Left + 20, over.Top + 20) }, "Schwärzen");
                Assert.NotNull(doc.SelectedImagesExportBlocked());
                doc.Undo();
                Assert.Null(doc.SelectedImagesExportBlocked());

                // Esc lets go of the picture, a second Esc ends the tool
                view.Focus();
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.Null(doc.SelectedImages);
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.Equal(ViewTool.SelectText, vm.Tool);

                Assert.True(await vm.SaveDocumentAsync(doc, target, doc.SaveVersion));
            }
            finally
            {
                window.Close();
            }
        });
        using var saved = PdfDocument.Open(File.ReadAllBytes(target));
        var images = ContentObjectEditor.ObjectsOf(PageItem.From(saved)[0]);
        var tiles = images.GroupOf(0).Select(i => images[i].Visible).Aggregate((a, b) => a.Union(b));
        Assert.Equal(50 * 72 / 25.4, tiles.Width, 1);
    }
    /// <summary>„Bild ersetzen …“ in the panel: an image file takes the place of the chosen picture and is chosen afterwards.</summary>
    [Fact]
    public void ReplaceAPicture_FromAFile()
    {
        var pdf = TestFiles.Write(ContentImageTests.Build(), "bild-ersetzen.pdf");
        var pixels = new byte[20 * 10 * 4];
        for (var p = 0; p < 200; p++) (pixels[p * 4], pixels[p * 4 + 1], pixels[p * 4 + 2], pixels[p * 4 + 3]) = (150, 40, 200, 255);
        var file = Path.Combine(Path.GetDirectoryName(pdf)!, "bild-ersetzen-neu.png");
        File.WriteAllBytes(file, Core.Pdf.Graphics.ImageExport.Export(new Core.Imaging.RgbaImage(20, 10, pixels)).Data);
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.Tool = ViewTool.Edit;
                vm.PickImageFile = () => Task.FromResult<string?>(file);
                Assert.True(doc.SelectImageAt(0, 120, 590, single: false));
                UiSession.Capture(window);
                var button = window.GetVisualDescendants().OfType<ContentImagePanel>().Single().FindControl<Button>("ReplaceImageButton")!;
                button.BringIntoView();
                UiSession.Capture(window);
                Thread.Sleep(600);
                window.MouseDown(Centre(button, window), MouseButton.Left);
                window.MouseUp(Centre(button, window), MouseButton.Left);
                for (var wait = 0; wait < 100 && doc.SelectedImages is not { Keys.Count: 1 }; wait++)
                {
                    await Task.Delay(50);
                    UiSession.Capture(window);
                }
                Assert.Equal(1, doc.SelectedImages?.Keys.Count);
                Assert.Equal(new Core.Pdf.Graphics.RectD(100, 500, 300, 600), doc.SelectedImagesBox);
                Assert.Equal(20, doc.SelectedImageList[0].PixelWidth);
                doc.Undo();
                Assert.Equal(2, doc.ContentObjectGroup(0, doc.ContentObjectAt(0, 120, 590)!.Value).Count);
            }
            finally
            {
                window.Close();
            }
        });
    }
    /// <summary>
    /// Cutting with the frame: the picture stays whole while the frame is shaped (a handle, then moved inside the picture); only „Zuschneiden
    /// anwenden“ cuts. Esc instead drops the frame without a change.
    /// </summary>
    [Fact]
    public void CropWithTheFrame_OnlyApplyCuts()
    {
        var pdf = TestFiles.Write(ContentImageTests.Build(), "bild-zuschneiden.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.Tool = ViewTool.Edit;
                Assert.True(doc.SelectImageAt(0, 120, 590, single: false));
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                var before = doc.History.Pages[0];

                // Esc drops the frame without a change
                doc.IsCroppingImages = true;
                view.Focus();
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.False(doc.IsCroppingImages);
                Assert.NotNull(doc.SelectedImages);

                doc.IsCroppingImages = true;
                UiSession.Capture(window);
                Assert.Equal(Core.Pdf.Graphics.RectD.FromLtrb(100, 500, 300, 600), doc.CropFrame);
                // the right handle 100 pt inwards, then the frame 30 pt to the right (it stops at the picture's edge: 20 pt)
                Drag(window, At(300, 550), At(200, 550));
                Assert.Equal(200, doc.CropFrame!.Value.Right, 0);
                Drag(window, At(150, 550), At(180, 550));
                Assert.Equal(Core.Pdf.Graphics.RectD.FromLtrb(130, 500, 230, 600), doc.CropFrame!.Value with
                {
                    Left = Math.Round(doc.CropFrame!.Value.Left), Right = Math.Round(doc.CropFrame!.Value.Right),
                    Top = Math.Round(doc.CropFrame!.Value.Top), Bottom = Math.Round(doc.CropFrame!.Value.Bottom),
                });
                Assert.Same(before, doc.History.Pages[0]);   // nothing cut yet
                Shoot(window, "bilder-bearbeiten-3-zuschnitt");
                Assert.Equal(Core.Pdf.Graphics.RectD.FromLtrb(100, 500, 300, 600), doc.SelectedImagesBox);

                var apply = window.GetVisualDescendants().OfType<ContentImagePanel>().Single().FindControl<Button>("ApplyCropButton")!;
                apply.BringIntoView();
                UiSession.Capture(window);
                Thread.Sleep(600);
                window.MouseDown(Centre(apply, window), MouseButton.Left);
                window.MouseUp(Centre(apply, window), MouseButton.Left);
                for (var wait = 0; wait < 100 && ReferenceEquals(before, doc.History.Pages[0]); wait++)
                {
                    await Task.Delay(50);
                    UiSession.Capture(window);
                }
                Assert.False(doc.IsCroppingImages);
                var box = doc.SelectedImagesBox!.Value;
                Assert.Equal(130, box.Left, 0);
                Assert.Equal(230, box.Right, 0);
            }
            finally
            {
                window.Close();
            }
        });
    }
    /// <summary>
    /// Ctrl+click adds a picture or graphic to the chosen ones and takes it out again (B7, before it was Shift); with a drawing chosen, cutting,
    /// copying and saving (images only) are hidden. Shift + dragging a frame chooses everything inside it, with Ctrl added.
    /// </summary>
    [Fact]
    public void CtrlClick_AddsAndTakesOut_ShiftFrameChoosesInside()
    {
        var pdf = TestFiles.Write(ContentImageTests.Build(), "bild-strg.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.Tool = ViewTool.Edit;
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                void CtrlClick(double x, double y)
                {
                    Thread.Sleep(600);
                    window.MouseDown(At(x, y), MouseButton.Left, RawInputModifiers.Control);
                    window.MouseUp(At(x, y), MouseButton.Left, RawInputModifiers.Control);
                    UiSession.Capture(window);
                }
                void Frame(Point from, Point to, RawInputModifiers modifiers)
                {
                    Thread.Sleep(600);
                    window.MouseDown(from, MouseButton.Left, modifiers);
                    for (var k = 1; k <= 8; k++)
                        window.MouseMove(new Point(from.X + (to.X - from.X) * k / 8, from.Y + (to.Y - from.Y) * k / 8), modifiers);
                    window.MouseUp(to, MouseButton.Left, modifiers);
                    UiSession.Capture(window);
                }
                UiSession.Capture(window);
                Assert.True(doc.SelectImageAt(0, 165, 545, single: false));   // the photo (smallest under the point)
                Assert.Single(doc.SelectedImages!.Keys);
                CtrlClick(120, 590);                                           // + the picture of two tiles
                Assert.Equal(3, doc.SelectedImages!.Keys.Count);
                Assert.True(doc.SelectedAreImages);
                CtrlClick(25, 25);                                             // + the filled square (a drawing)
                Assert.Equal(4, doc.SelectedImages!.Keys.Count);
                Assert.False(doc.SelectedAreImages);
                var panel = window.GetVisualDescendants().OfType<ContentImagePanel>().Single();
                UiSession.Capture(window);
                Assert.False(panel.FindControl<Button>("CopyImageButton")!.IsEffectivelyVisible);
                Assert.False(panel.FindControl<ToggleButton>("CropToggle")!.IsEffectivelyVisible);
                CtrlClick(120, 590);                                           // the tiles out again
                Assert.Equal(2, doc.SelectedImages!.Keys.Count);
                // a frame around the two tiles (and the photo on them): those three, nothing beside them
                Frame(At(90, 610), At(310, 490), RawInputModifiers.Shift);
                Assert.Equal(3, doc.SelectedImages!.Keys.Count);
                var framed = doc.SelectedImagesBox!.Value;
                Assert.Equal((100.0, 500.0, 300.0, 600.0), (Math.Round(framed.Left), Math.Round(framed.Top), Math.Round(framed.Right), Math.Round(framed.Bottom)));
                // with Ctrl the blue square is added; a frame over nothing chooses nothing
                Frame(At(10, 40), At(40, 10), RawInputModifiers.Shift | RawInputModifiers.Control);
                Assert.Equal(4, doc.SelectedImages!.Keys.Count);
                Frame(At(320, 700), At(380, 650), RawInputModifiers.Shift);
                Assert.Null(doc.SelectedImages);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// B7: the chosen objects copied as one vector object and pasted — on the same page a little offset, chosen afterwards and movable; into
    /// another document at the same place; undo takes the pasted object away again.
    /// </summary>
    [Fact]
    public void CopyAndPaste_AsOneObject_IntoThisAndAnotherDocument()
    {
        var pdf = TestFiles.Write(ContentImageTests.Build(), "bild-kopieren.pdf");
        var second = TestFiles.Write(ContentImageTests.Build(), "bild-einfuegen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.Tool = ViewTool.Edit;
                var before = (await doc.ContentObjectsOnAsync(0)).Count;
                Assert.True(doc.SelectImageAt(0, 165, 545, single: false));   // the photo
                Assert.True(doc.SelectImageAt(0, 25, 25, single: false, extend: true));   // + the blue square (a drawing)
                var box = doc.SelectedImagesBox!.Value;
                await vm.CopyEditedObjectsCommand.ExecuteAsync(null);
                Assert.NotNull(vm.CopiedObjects);
                await vm.PasteObjectsCommand.ExecuteAsync(null);
                Assert.Equal(before + 2, (await doc.ContentObjectsOnAsync(0)).Count);
                Assert.Equal(2, doc.SelectedImages!.Keys.Count);   // the pasted ones, chosen
                var pasted = doc.SelectedImagesBox!.Value;
                Assert.Equal(box.Left + 10, pasted.Left, 1);
                Assert.Equal(box.Top - 10, pasted.Top, 1);
                Assert.True(doc.MoveSelectedImages(50, 0));   // moved as one
                Assert.Equal(box.Left + 60, doc.SelectedImagesBox!.Value.Left, 1);
                doc.Undo();
                doc.Undo();
                Assert.Equal(before, (await doc.ContentObjectsOnAsync(0)).Count);
                // into another document: at the same place, saved without the first one
                await vm.OpenAsync([second]);
                var other = vm.SelectedDocument!;
                Assert.NotSame(doc, other);
                vm.Tool = ViewTool.Edit;
                await vm.PasteObjectsCommand.ExecuteAsync(null);
                Assert.Equal(box.Left, other.SelectedImagesBox!.Value.Left, 1);
                var saved = Path.Combine(Path.GetDirectoryName(second)!, "bild-einfuegen-ergebnis.pdf");
                File.Delete(saved);
                Assert.True(await vm.SaveDocumentAsync(other, saved, other.SaveVersion));
                using var check = PdfDocument.Open(saved);
                Assert.Equal(before + 2, ContentObjectEditor.ObjectsOf(PageItem.From(check)[0]).Count);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// A page with very many objects (a plan) is edited in the background (docs/FEHLERGEDAECHTNIS.md, No. 255): arrow keys pressed while a step
    /// runs become one move after it, a width typed meanwhile is set from where that move left the picture; undo goes back step by step.
    /// </summary>
    [Fact]
    public void LargePage_StepsRunInTheBackground_ArrowKeysJoined()
    {
        var pdf = TestFiles.Write(ContentObjectTiming.GeneratedPlan(DocumentViewModel.BackgroundObjects + 20_000), "plan-gross.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.Tool = ViewTool.Edit;
                var found = await doc.ContentObjectsOnAsync(0);
                Assert.True(found.Count >= DocumentViewModel.BackgroundObjects, $"{found.Count} Objekte");
                var index = Enumerable.Range(0, found.Count).First(i => found.VisibleOf(i) is { Width: > 5, Height: > 5 });
                var before = found.VisibleOf(index);
                var original = doc.History.Pages[0];
                doc.SelectedImages = new ImageSelection(0, [found.KeyOf(index)]);
                Assert.True(doc.MoveSelectedImages(1, 0, continueLast: true));
                Assert.True(doc.ImageStepRunning);
                Assert.Same(original, doc.History.Pages[0]);   // not yet: the window goes on meanwhile
                doc.MoveSelectedImages(2, 0, continueLast: true);
                doc.MoveSelectedImages(3, 0, continueLast: true);
                doc.ImageWidthMm = (before.Width * 2 / (72 / 25.4)).ToString(System.Globalization.CultureInfo.CurrentCulture);
                for (var wait = 0; wait < 600 && doc.ImageStepRunning; wait++) await Task.Delay(50);
                Assert.False(doc.ImageStepRunning);
                var box = doc.SelectedImagesBox!.Value;
                Assert.Equal(before.Left + 6, box.Left, 2);   // 1, then 2 + 3 joined
                Assert.Equal(before.Width * 2, box.Width, 1);
                doc.Undo();   // the width
                Assert.Equal(before.Width, doc.SelectedImagesBox!.Value.Width, 1);
                doc.Undo();   // the arrow keys (joined with the first move: one step)
                Assert.Same(original, doc.History.Pages[0]);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
