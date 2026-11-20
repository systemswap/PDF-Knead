using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>UI tests for shapes: button, drawing (with Shift), arrow, properties panel, undo, saving. See KONZEPT.md, section 5.13.</summary>
[Collection(nameof(UiCollection))]
public sealed class ShapeUiTests
{
    private static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static void Drag(Window window, Point from, Point to, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.MouseDown(from, MouseButton.Left, modifiers);
        window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), modifiers);
        window.MouseMove(to, modifiers);
        window.MouseUp(to, MouseButton.Left, modifiers);
    }

    /// <summary>
    /// A change from the properties panel replaces the element: the selection moves straight to the new one — without a moment with no
    /// selection, in which the panel vanished and a text box in it lost the focus (typing then stopped after the first character).
    /// </summary>
    [Fact]
    public void Replace_SelectionNeverEmptyInBetween()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "ersetzen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var shape = new ShapeElement { Rect = new PdfRectangle(100, 600, 200, 680) };
                doc.AddElement(0, shape, "Test");
                var seen = new List<PageElement?>();
                var panelHidden = false;
                doc.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(DocumentViewModel.SelectedElement)) return;
                    seen.Add(doc.SelectedElement?.Element);
                    panelHidden |= !vm.ElementProperties.IsVisible;
                };
                vm.ElementProperties.Dashed = true;
                var dashed = Assert.IsType<ShapeElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.True(dashed.Dashed);
                Assert.DoesNotContain(null, seen);
                Assert.Same(dashed, doc.SelectedElement?.Element);
                Assert.False(panelHidden);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The percentage beside the opacity slider follows it while it is dragged (report of 4.11.2026: it stayed).</summary>
    [Fact]
    public void OpacitySlider_PercentageFollows()
    {
        using var source = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        var box = new ShapeElement { Kind = ShapeKind.Rectangle, Rect = new PdfRectangle(100, 500, 220, 580), FillColor = 0xC00000 };
        using var file = new MemoryStream();
        Core.Pdf.Writing.PdfWriter.Write(source, [PageItem.From(source)[0].WithElements([box])], file, new Core.Pdf.Writing.PdfSaveOptions());
        var pdf = TestFiles.Write(file.ToArray(), "deckkraft.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.SelectedElement = new ElementRef(0, Assert.IsType<ShapeElement>(Assert.Single(doc.ElementsOn(0))));
                UiSession.Capture(window);
                var panel = window.GetVisualDescendants().OfType<ElementPropertiesPanel>().Single();
                var slider = panel.GetVisualDescendants().OfType<Slider>().Single(s => Avalonia.Automation.AutomationProperties.GetName(s) == Tr.T("Deckkraft"));
                var percent = (TextBlock)((Grid)slider.Parent!).Children[1];
                Assert.Equal("100 %", percent.Text);
                // dragged with the mouse: the thumb from the right end to the left, the percentage on the way
                var thumb = slider.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Thumb>().First();
                var start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(start - new Point(slider.Bounds.Width / 3, 0));
                UiSession.Capture(window);
                var dragged = Assert.IsType<ShapeElement>(Assert.Single(doc.ElementsOn(0))).Opacity;
                Assert.True(dragged < 1, $"{dragged}");
                Assert.Equal($"{Math.Round(dragged * 100)} %", percent.Text);
                window.MouseMove(start - new Point(slider.Bounds.Width / 2, 0));
                UiSession.Capture(window);
                var further = Assert.IsType<ShapeElement>(Assert.Single(doc.ElementsOn(0))).Opacity;
                Assert.True(further < dragged, $"{further}");
                Assert.Equal($"{Math.Round(further * 100)} %", percent.Text);
                window.MouseUp(start - new Point(slider.Bounds.Width / 2, 0), MouseButton.Left);
                foreach (var value in (double[])[85, 60, 35])
                {
                    slider.Value = value;
                    UiSession.Capture(window);
                    Assert.Equal($"{value} %", percent.Text);
                    Assert.Equal(value / 100, Assert.IsType<ShapeElement>(Assert.Single(doc.ElementsOn(0))).Opacity, 3);
                }
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Protected file without the modify and annotate permissions: the properties panel shows the shape but does not change it.
    /// </summary>
    [Fact]
    public void ProtectedFile_PropertiesPanel_ChangesNothing()
    {
        using var source = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        var box = new ShapeElement { Kind = ShapeKind.Rectangle, Rect = new PdfRectangle(100, 500, 220, 580), FillColor = 0xC00000 };
        using var protectedFile = new MemoryStream();
        Core.Pdf.Writing.PdfWriter.Write(source, [PageItem.From(source)[0].WithElements([box])], protectedFile, new Core.Pdf.Writing.PdfSaveOptions
        {
            Protection = new Core.Pdf.Writing.PdfProtection("", "Besitzer-1", Core.Pdf.Security.PdfPermissions.Print),
        });
        var pdf = TestFiles.Write(protectedFile.ToArray(), "geschuetzte form.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                Assert.False(doc.CanEditElements);
                var shape = Assert.IsType<ShapeElement>(Assert.Single(doc.ElementsOn(0)));
                doc.SelectedElement = new ElementRef(0, shape);
                UiSession.Capture(window);
                var panel = vm.ElementProperties;
                Assert.True(panel.IsVisible);
                Assert.False(panel.CanEdit);
                Assert.False(window.GetVisualDescendants().OfType<ElementPropertiesPanel>().Single().IsEffectivelyEnabled);
                panel.Opacity = 40;
                panel.StrokeWidth = ElementPropertiesViewModel.StrokeWidths[^1];
                Assert.Same(shape, Assert.Single(doc.ElementsOn(0)));
                Assert.False(doc.IsModified);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Layers: forward and backward past the next overlapping element, to the very front or back, Ctrl+Page Up/Down, right-click,
    /// numbers on the elements; saved in the same order. Fields move only in form design mode, with a hint about the tab order.
    /// See KONZEPT.md, section 5.13.
    /// </summary>
    [Fact]
    public void Layers_ForwardBackward_KeysMenuNumbers_AndSaved()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "ebenen.pdf");
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
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                ShapeElement Box(ShapeKind kind, double left, double bottom, int fill) =>
                    new() { Kind = kind, Rect = new PdfRectangle(left, bottom, left + 120, bottom + 80), FillColor = fill };
                var a = Box(ShapeKind.Rectangle, 100, 500, 0xC00000);
                var b = Box(ShapeKind.Ellipse, 160, 530, 0x1E7B34);
                var far = Box(ShapeKind.Star, 400, 200, 0xFFC000);   // overlaps none
                var c = Box(ShapeKind.Diamond, 130, 560, 0x1F4E9A);
                foreach (var shape in new[] { a, b, far, c }) doc.AddElement(0, shape, "Form");
                IReadOnlyList<PageElement> Order() => doc.ElementsOn(0);

                // a one forward: past b (overlapping), not just one position
                doc.SelectedElement = new ElementRef(0, a);
                Assert.True(vm.MoveLayerCommand.CanExecute(LayerMove.Forward));
                vm.MoveLayerCommand.Execute(LayerMove.Forward);
                Assert.Equal([b, a, far, c], Order());
                // again: past „far“ (not overlapping) to behind c, which overlaps
                vm.MoveLayerCommand.Execute(LayerMove.Forward);
                Assert.Equal([b, far, c, a], Order());
                // keys: Ctrl+Shift+Page Down to the very back, Ctrl+Page Up one forward
                view.Focus();
                window.KeyPress(Key.PageDown, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.PageDown, null);
                Assert.Equal([a, b, far, c], Order());
                window.KeyPress(Key.PageUp, RawInputModifiers.Control, PhysicalKey.PageUp, null);
                Assert.Equal([b, a, far, c], Order());
                Assert.Equal(Tr.T("Eine Ebene nach vorn"), doc.History.UndoDescription);
                vm.UndoCommand.Execute(null);
                Assert.Equal([a, b, far, c], Order());

                // right-click on c: selected, menu open; numbers on the elements
                window.MouseDown(At(190, 600), MouseButton.Right);
                window.MouseUp(At(190, 600), MouseButton.Right);
                Assert.Same(c, doc.SelectedElement?.Element);
                vm.ShowLayers = true;
                Assert.True(doc.ShowLayers);
                Assert.Equal(4, doc.LayerOf(0, c));
                UiSession.Capture(window);
                Shoot(window, "72-ebenen-nummern");
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                vm.ShowLayers = false;

                // saved in this order, and reopened in the same order
                await vm.SaveCommand.ExecuteAsync(null);
                using var saved = PdfDocument.Open(File.ReadAllBytes(pdf));
                var kinds = PageItem.From(saved)[0].Elements.Select(e => ((ShapeElement)e).Kind).ToList();
                Assert.Equal([ShapeKind.Rectangle, ShapeKind.Ellipse, ShapeKind.Star, ShapeKind.Diamond], kinds);

                // fields: in normal mode the layer stays, in form design mode it changes, with a hint about the tab order
                var first = new FieldElement { Rect = new PdfRectangle(100, 300, 250, 320), Spec = new FieldSpec { Name = "Erstes" } };
                var second = new FieldElement { Rect = new PdfRectangle(120, 310, 270, 330), Spec = new FieldSpec { Name = "Zweites" } };
                doc.AddElement(0, first, "Feld");
                doc.AddElement(0, second, "Feld");
                doc.SelectedElement = new ElementRef(0, first);
                Assert.False(vm.MoveLayerCommand.CanExecute(LayerMove.Forward));
                var message = "";
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Message)) message = vm.Message; };
                vm.ToggleFormDesignCommand.Execute(null);
                doc.SelectFields([new ElementRef(0, first)]);
                Assert.True(vm.MoveLayerCommand.CanExecute(LayerMove.Forward));
                vm.MoveLayerCommand.Execute(LayerMove.Forward);
                Assert.Same(first, Order()[^1]);
                Assert.Equal(2, doc.TabNumberOf(0, first));
                Assert.Contains(Tr.T("Tab-Reihenfolge"), message);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Images: when drawn, the rectangle adapts to the image (chain closed); dragging the handles keeps the aspect ratio (Shift: free);
    /// with the chain open, sizing is free; size in mm, sharpening; the view computes a Lanczos version in the background.
    /// See KONZEPT.md, section 5.13.
    /// </summary>
    [Fact]
    public void Picture_ChainHandlesMillimetresSharpen_AndLanczosInTheView()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "bild.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            var improved = 0;
            void OnImproved() => Interlocked.Increment(ref improved);
            PdfKnead.Rendering.ImageCache.Improved += OnImproved;
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Insert;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                var photo = Core.Imaging.RgbaImage.Blank(1600, 800);
                for (var i = 0; i < photo.Pixels.Length; i += 4)
                    (photo.Pixels[i], photo.Pixels[i + 1], photo.Pixels[i + 2], photo.Pixels[i + 3]) = ((byte)(i / 4 % 1600 / 7), (byte)(i / 4 / 1600 / 4), 120, 255);

                // drawn as 200 × 200: with the chain closed it becomes 2 : 1
                ElementDefaults.Image = new ImageElement { Rect = default, Image = photo };
                vm.Tool = ViewTool.InsertImage;
                Drag(window, At(100, 700), At(300, 500));
                var picture = Assert.IsType<ImageElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(2, picture.Rect.Width / picture.Rect.Height, 2);
                Assert.Equal(200, picture.Rect.Width, 1);
                Assert.True(vm.ElementProperties.IsVisible && vm.ElementProperties.IsImage);
                Assert.Equal(Tr.T("Bild"), vm.ElementProperties.Title);

                // bottom-right corner dragged further down: the aspect ratio stays
                var r = picture.Rect;
                Drag(window, At(r.Right, r.Bottom), At(r.Right + 10, r.Bottom - 60));
                picture = Assert.IsType<ImageElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(2, picture.Rect.Width / picture.Rect.Height, 2);
                Assert.True(picture.Rect.Height > r.Height + 50);
                // with Shift: free
                r = picture.Rect;
                Drag(window, At(r.Right, r.Bottom), At(r.Right + 40, r.Bottom), RawInputModifiers.Shift);
                picture = Assert.IsType<ImageElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(r.Height, picture.Rect.Height, 1);
                Assert.Equal(r.Width + 40, picture.Rect.Width, 1);

                // chain closed: 2 : 1 again (width stays); typing a width in mm: the height follows
                vm.ElementProperties.KeepAspect = false;
                vm.ElementProperties.KeepAspect = true;
                picture = Assert.IsType<ImageElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(2, picture.Rect.Width / picture.Rect.Height, 2);
                vm.ElementProperties.WidthMm = "100";
                picture = Assert.IsType<ImageElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(100 * 72 / 25.4, picture.Rect.Width, 1);
                Assert.Equal("50", vm.ElementProperties.HeightMm);
                // chain open: height alone
                vm.ElementProperties.KeepAspect = false;
                vm.ElementProperties.HeightMm = "80";
                picture = Assert.IsType<ImageElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal((100.0, 80.0), (Math.Round(picture.Rect.Width * 25.4 / 72, 1), Math.Round(picture.Rect.Height * 25.4 / 72, 1)));
                vm.ElementProperties.KeepAspect = true;

                // sharpening and opacity
                vm.ElementProperties.Sharpness = 40;
                vm.ElementProperties.Opacity = 80;
                picture = Assert.IsType<ImageElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal((0.4, 0.8), (picture.Sharpness, picture.Opacity));
                // the alternative text for screen readers (docs/FEHLERGEDAECHTNIS.md, No. 631): set in the panel, trimmed, one undo step
                var altBox = window.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == "ImageAltTextBox");
                Assert.True(altBox.IsEffectivelyVisible);
                vm.ElementProperties.ImageAltText = "  Lageplan des Gartens  ";
                picture = Assert.IsType<ImageElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal("Lageplan des Gartens", picture.AltText);
                vm.UndoCommand.Execute(null);
                Assert.Equal("", Assert.IsType<ImageElement>(Assert.Single(doc.ElementsOn(0))).AltText);
                vm.RedoCommand.Execute(null);

                // the view downscales the image (1600 pixels to about 400) with Lanczos in the background and then redraws
                var until = DateTime.UtcNow.AddSeconds(10);
                while (Volatile.Read(ref improved) == 0 && DateTime.UtcNow < until)
                {
                    UiSession.Capture(window);
                    Thread.Sleep(20);
                }
                Assert.True(improved > 0, "keine Lanczos-Fassung");
                UiSession.Capture(window);
                Shoot(window, "73-bild-eigenschaften");
            }
            finally
            {
                PdfKnead.Rendering.ImageCache.Improved -= OnImproved;
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>Text field with automatic font shrinking: switch in the properties panel (only with a fixed size); while typing, the font in the input box gets smaller.</summary>
    [Fact]
    public void TextField_ShrinkToFit_SwitchAndWhileTyping()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "verkleinern.pdf");
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
                var field = new FieldElement { Rect = new PdfRectangle(100, 600, 260, 622), Spec = new FieldSpec { Name = "Name", FontSize = 14, BorderColor = 0 } };
                doc.AddElement(0, field, "Feld");
                vm.ToggleFormDesignCommand.Execute(null);
                doc.SelectFields([new ElementRef(0, field)]);
                UiSession.Capture(window);
                Assert.True(vm.FieldProperties.CanShrinkToFit);
                vm.FieldProperties.ShrinkToFit = true;
                var current = Assert.IsType<FieldElement>(doc.ElementsOn(0).Single(e => e is FieldElement));
                Assert.True(current.Spec.ShrinkToFit);
                UiSession.Capture(window);
                Shoot(window, "74-feld-verkleinern");
                vm.ToggleFormDesignCommand.Execute(null);

                // filling in: short text at the initial size, long text smaller, already in the input box
                var viewer = window.GetVisualDescendants().OfType<DocumentViewer>().Single();
                doc.BeginFill(new ElementRef(0, current));
                UiSession.Capture(window);
                viewer.Editor.Text = "Max";
                UiSession.Capture(window);
                var start = viewer.Editor.FontSize;
                viewer.Editor.Text = "Max Mustermann aus Musterstadt am Musterbach";
                UiSession.Capture(window);
                Assert.True(viewer.Editor.FontSize < start * 0.75, $"{viewer.Editor.FontSize} statt kleiner als {start}");
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Eyedropper: button next to the colour; a click on the page takes the colour there (shown in the list as a custom colour);
    /// Esc and right-click cancel, and the shape stays selected. Also: the shape extends to the point where the mouse is released.
    /// </summary>
    [Fact]
    public void Shapes_PipetteAndSizeUntilRelease()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "pipette.pdf");
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
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;

                // released a little beyond the last move (fast dragging): the shape extends to that point
                vm.InsertShapeCommand.Execute(ShapeKind.Rectangle);
                window.MouseDown(At(100, 700), MouseButton.Left);
                window.MouseMove(At(200, 600));
                window.MouseUp(At(300, 500), MouseButton.Left);
                var shape = Assert.IsType<ShapeElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal((200.0, 200.0), (Math.Round(shape.Rect.Width), Math.Round(shape.Rect.Height)));

                // drawn small (20 × 5 pt): stays that small and must not turn into the default size 140 × 90
                vm.InsertShapeCommand.Execute(ShapeKind.Rectangle);
                Drag(window, At(250, 760), At(270, 755));
                var small = Assert.IsType<ShapeElement>(doc.ElementsOn(0)[^1]);
                Assert.Equal((20.0, 5.0), (Math.Round(small.Rect.Width), Math.Round(small.Rect.Height)));
                // a plain click sets the default size
                vm.InsertShapeCommand.Execute(ShapeKind.Rectangle);
                window.MouseDown(At(330, 600), MouseButton.Left);
                window.MouseUp(At(330, 600), MouseButton.Left);
                Assert.Equal((140.0, 90.0), (Math.Round(doc.ElementsOn(0)[^1].Rect.Width), Math.Round(doc.ElementsOn(0)[^1].Rect.Height)));
                vm.UndoCommand.Execute(null);
                vm.UndoCommand.Execute(null);
                doc.SelectedElement = new ElementRef(0, shape);

                // eyedropper for the fill: Esc cancels, so does right-click; the shape stays selected
                UiSession.Capture(window);
                vm.ElementProperties.IsPickingFill = true;
                Assert.True(doc.IsPickingColor);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(doc.IsPickingColor || vm.ElementProperties.IsPickingFill);
                Assert.Same(shape, doc.SelectedElement?.Element);
                vm.ElementProperties.IsPickingFill = true;
                window.MouseDown(At(390, 590), MouseButton.Right);
                window.MouseUp(At(390, 590), MouseButton.Right);
                Assert.False(doc.IsPickingColor);
                Assert.Same(shape, Assert.Single(doc.ElementsOn(0)));   // nothing changed

                // click into the orange square on the page (0.95 0.6 0.2): orange fill, listed as a custom colour
                vm.ElementProperties.IsPickingFill = true;
                window.MouseMove(At(390, 590));
                UiSession.Capture(window);
                Shoot(window, "75-pipette");
                window.MouseDown(At(390, 590), MouseButton.Left);
                window.MouseUp(At(390, 590), MouseButton.Left);
                shape = Assert.IsType<ShapeElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(0xF29933, shape.FillColor);
                Assert.False(doc.IsPickingColor);
                Assert.Same(shape, doc.SelectedElement?.Element);
                Assert.Equal(0xF29933, vm.ElementProperties.FillColor?.Rgb);
                Assert.Contains(vm.ElementProperties.FillColor, ElementPropertiesViewModel.Colors);
                Assert.True(vm.ElementProperties.FillColor!.IsCustom);
                Assert.Equal("#F29933", vm.ElementProperties.FillColor.Code);

                // stroke colour from the blue rectangle; one undo step
                vm.ElementProperties.IsPickingStroke = true;
                window.MouseDown(At(80, 730), MouseButton.Left);
                window.MouseUp(At(80, 730), MouseButton.Left);
                Assert.Equal(0x4F45E6, Assert.IsType<ShapeElement>(Assert.Single(doc.ElementsOn(0))).StrokeColor);
                vm.UndoCommand.Execute(null);
                Assert.Equal(shape.StrokeColor, Assert.IsType<ShapeElement>(Assert.Single(doc.ElementsOn(0))).StrokeColor);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void Shapes_DrawWithShift_ArrowAndProperties_UndoAndSave()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "formen.pdf");
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
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;

                // the Ellipse button in the Shapes group, drawn with Shift: a circle
                var ellipseButton = window.GetVisualDescendants().OfType<RibbonToggleButton>().Single(b => b.Label == Tr.T("Ellipse") && b.IsEffectivelyVisible);
                Thread.Sleep(800);
                Shoot(window, "69-formen-menueband");
                var center = ellipseButton.TranslatePoint(new Point(ellipseButton.Bounds.Width / 2, ellipseButton.Bounds.Height / 2), window)!.Value;
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                UiSession.Capture(window);
                Assert.Equal(ViewTool.InsertShape, vm.Tool);
                Assert.True(ellipseButton.IsChecked);
                Drag(window, At(100, 700), At(250, 640), RawInputModifiers.Shift);
                var circle = Assert.IsType<ShapeElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(ShapeKind.Ellipse, circle.Kind);
                Assert.Equal(circle.Rect.Width, circle.Rect.Height, 1);   // Shift: circle
                Assert.Equal(150, circle.Rect.Width, 2);
                Assert.Equal(ViewTool.SelectText, vm.Tool);               // back to selecting afterwards
                Assert.Same(circle, doc.SelectedElement?.Element);

                // properties panel: visible; fill, width, dashed, opacity, one undo step each, also applied to the next shape
                UiSession.Capture(window);
                Assert.True(vm.ElementProperties.IsVisible);
                Assert.True(window.FindControl<Border>("ElementPropertiesHost")!.IsEffectivelyVisible);
                Assert.Equal(Tr.T("Ellipse"), vm.ElementProperties.Title);
                vm.ElementProperties.FillColor = ElementPropertiesViewModel.Colors.First(c => c.Label == "Hellgelb");
                vm.ElementProperties.StrokeWidth = ElementPropertiesViewModel.StrokeWidths.First(w => w.Value == 4);
                vm.ElementProperties.Dashed = true;
                vm.ElementProperties.Opacity = 60;
                var styled = Assert.IsType<ShapeElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal((0xFFF6CC, 4.0, true, 0.6), (styled.FillColor, styled.StrokeWidth, styled.Dashed, styled.Opacity));
                Assert.Equal(0xFFF6CC, ElementDefaults.Shape.FillColor);
                UiSession.Capture(window);
                Shoot(window, "70-form-eigenschaften");

                // arrow with Shift: horizontal, head at the end
                vm.InsertShapeCommand.Execute(ShapeKind.Arrow);
                Drag(window, At(300, 500), At(450, 510), RawInputModifiers.Shift);
                var arrow = Assert.IsType<ShapeElement>(doc.ElementsOn(0)[^1]);
                Assert.Equal(ShapeKind.Arrow, arrow.Kind);
                var ((x1, y1), (x2, y2)) = ElementAppearance.ShapeLine(arrow);
                Assert.Equal(y1, y2, 0.5);
                Assert.True(x2 > x1 + 100);
                Assert.True(vm.ElementProperties.IsLine);
                Assert.DoesNotContain(ElementPropertiesViewModel.LineColors, c => c.Rgb is null);   // a line without colour cannot be chosen

                // hits only near the line: elsewhere inside the arrow's rectangle nothing is hit
                doc.SelectedElement = null;
                window.MouseDown(At(375, 508), MouseButton.Left);
                window.MouseUp(At(375, 508), MouseButton.Left);
                Assert.Null(doc.SelectedElement);
                window.MouseDown(At(375, 500), MouseButton.Left);
                window.MouseUp(At(375, 500), MouseButton.Left);
                Assert.Same(arrow, doc.SelectedElement?.Element);

                // undo removes the arrow; four more undo steps revert the style changes of the circle
                Shoot(window, "71-form-pfeil");
                vm.UndoCommand.Execute(null);   // arrow gone
                Assert.Single(doc.ElementsOn(0));
                for (var i = 0; i < 4; i++) vm.UndoCommand.Execute(null);
                var plain = Assert.IsType<ShapeElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal((false, 1.0), (plain.Dashed, plain.Opacity));

                // save and reopen: a Circle annotation that is read back as a shape
                vm.RedoCommand.Execute(null);
                await vm.SaveCommand.ExecuteAsync(null);
                using var saved = PdfDocument.Open(File.ReadAllBytes(pdf));
                var annot = saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Get(0).AsDictionary()!;
                Assert.Equal("Circle", annot.GetName(PdfName.Subtype)!.Value);
                Assert.IsType<ShapeElement>(Assert.Single(PageItem.From(saved)[0].Elements));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
