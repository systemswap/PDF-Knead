using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Fonts;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>UI tests for page elements: drawing, typing, moving, resizing, formatting, fields, signatures, redaction and saving.</summary>
[Collection(nameof(UiCollection))]
public sealed class ElementUiTests
{
    private static void WaitUntil(Window window, Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
        {
            UiSession.Capture(window);
            Thread.Sleep(20);
        }
        Assert.True(condition(), "Zeitüberschreitung");
    }

    private static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
    }

    private static string PageText(PdfDocument doc, int page) =>
        string.Concat(PageInterpreter.Build(doc, doc.Pages[page]).Text.Select(t => t.Text));

    [Fact]
    public void Text_DrawTypeMoveResizeFormatAndSave()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 3).Build(), "elemente.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Insert;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var viewer = window.GetVisualDescendants().OfType<DocumentViewer>().Single();
                var view = viewer.View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;

                // 1. Text tool: draw a rectangle → input box, the tool returns to text selection
                vm.SetToolCommand.Execute(ViewTool.InsertText);
                Drag(window, At(100, 700), At(360, 650));
                Assert.NotNull(doc.EditingElement);
                Assert.True(doc.EditingIsNew);
                Assert.True(viewer.Editor.IsVisible);
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                Assert.Empty(doc.ElementsOn(0));   // only on the page once it has text
                viewer.Editor.Text = "Max Mustermann\nMusterstraße 1";
                Shoot(window, "30-element-schreiben");
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.Null(doc.EditingElement);
                var element = Assert.IsType<TextElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal("Max Mustermann\nMusterstraße 1", element.Text);
                Assert.Equal(100, element.Rect.Left, 0.5);
                Assert.Equal(700, element.Rect.Top, 0.5);
                Assert.Same(element, doc.SelectedElement?.Element);
                Assert.True(doc.IsModified);
                Assert.Equal(Tr.T("Text einfügen"), doc.History.UndoDescription);
                UiSession.Capture(window);
                Shoot(window, "31-element-ausgewaehlt");

                // 2. move: grab the element and drag it
                Drag(window, At(150, 680), At(200, 600));
                element = (TextElement)doc.SelectedElement!.Value.Element;
                Assert.Equal(150, element.Rect.Left, 1);
                Assert.Equal(620, element.Rect.Top, 1);
                Assert.Equal(Tr.T("Element verschieben"), doc.History.UndoDescription);

                // 3. size: right handle (middle of the edge) 100 pt to the right
                var middle = (element.Rect.Top + element.Rect.Bottom) / 2;
                Drag(window, At(element.Rect.Right, middle), At(element.Rect.Right + 100, middle));
                var resized = (TextElement)doc.SelectedElement!.Value.Element;
                Assert.Equal(element.Rect.Width + 100, resized.Rect.Width, 1);
                Assert.Equal(element.Rect.Left, resized.Rect.Left, 0.5);

                // 4. formatting in the ribbon applies to the selected element (one step per change)
                vm.TextSize = 20;
                vm.TextColor = MainViewModel.ColorOptions[3];
                vm.SetAlignmentCommand.Execute(ElementAlignment.Center);
                var formatted = (TextElement)doc.SelectedElement!.Value.Element;
                Assert.Equal(20, formatted.FontSize);
                Assert.Equal(0xC00000, formatted.Color);
                Assert.Equal(ElementAlignment.Center, formatted.Alignment);
                Assert.True(formatted.Rect.Height >= ElementAppearance.NeededHeight(formatted) - 0.01);   // grows with the text
                UiSession.Capture(window);
                Shoot(window, "32-element-format");

                // 5. a double-click edits, a click elsewhere commits
                var inside = At(formatted.Rect.Left + 20, formatted.Rect.Top - 10);
                window.MouseDown(inside, MouseButton.Left);
                window.MouseUp(inside, MouseButton.Left);
                window.MouseDown(inside, MouseButton.Left);
                window.MouseUp(inside, MouseButton.Left);
                Assert.NotNull(doc.EditingElement);
                Assert.False(doc.EditingIsNew);
                viewer.Editor.Text = "Musterstadt";
                view.Focus();   // focus lost → committed
                UiSession.Capture(window);
                Assert.Null(doc.EditingElement);
                Assert.Equal("Musterstadt", ((TextElement)doc.ElementsOn(0)[0]).Text);
                Assert.Equal(Tr.T("Text ändern"), doc.History.UndoDescription);

                // 6. text field tool: one click is enough (default size)
                vm.SetToolCommand.Execute(ViewTool.InsertField);
                var click = At(100, 530);   // still visible (fit width)
                window.MouseDown(click, MouseButton.Left);
                window.MouseUp(click, MouseButton.Left);
                Assert.True(doc.EditingElement is not null, $"Werkzeug {vm.Tool}/{doc.Tool}, Auswahl {doc.SelectedElement}, Elemente {doc.ElementsOn(0).Count}");
                Assert.True(doc.EditingIsNew);
                viewer.Editor.Text = "Ort";
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                var field = Assert.IsType<TextElement>(doc.ElementsOn(0)[1]);
                Assert.True(field.IsField);
                Assert.Equal(200, field.Rect.Width, 0.5);
                Assert.True(vm.TextIsField);
                Assert.True(vm.HasFieldSelected);
                // field name, custom font size (decimal comma), no line wrap
                vm.FieldName = "Wohnort";
                vm.TextSizeText = "13,5";
                vm.TextMultiline = false;
                field = (TextElement)doc.SelectedElement!.Value.Element;
                Assert.Equal("Wohnort", field.FieldName);
                Assert.Equal(13.5, field.FontSize);
                Assert.False(field.Multiline);
                Assert.Equal("13,5", vm.TextSizeText.Replace('.', ','));
                UiSession.Capture(window);
                Shoot(window, "33-element-feld");

                // 7. undo reverts the three changes and the field, redo restores everything; Del deletes
                Assert.Equal(Tr.T("Zeilenumbruch ändern"), doc.History.UndoDescription);
                for (var i = 0; i < 4; i++) vm.UndoCommand.Execute(null);
                Assert.Single(doc.ElementsOn(0));
                for (var i = 0; i < 4; i++) vm.RedoCommand.Execute(null);
                Assert.Equal("Wohnort", ((TextElement)doc.ElementsOn(0)[1]).FieldName);
                Assert.Equal(2, doc.ElementsOn(0).Count);
                doc.SelectedElement = new ElementRef(0, doc.ElementsOn(0)[1]);
                view.Focus();
                // in normal mode fields stay where they are: Del deletes nothing, arrow keys do not move …
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                Assert.Equal(2, doc.ElementsOn(0).Count);
                Assert.Equal(100, doc.ElementsOn(0)[1].Rect.Left, 0.01);
                // … deleting works only in form design mode
                vm.ToggleFormDesignCommand.Execute(null);
                doc.SelectFields([new ElementRef(0, doc.ElementsOn(0)[1])]);
                view.Focus();
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                Assert.Single(doc.ElementsOn(0));
                vm.UndoCommand.Execute(null);
                Assert.Equal(2, doc.ElementsOn(0).Count);
                vm.ToggleFormDesignCommand.Execute(null);

                // 8. save as: text as a stamp, field in the form; both remain editable elements afterwards
                var target = Path.Combine(Path.GetDirectoryName(pdf)!, "mit elementen.pdf");
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.SaveAsCommand.ExecuteAsync(null);
                Assert.False(doc.IsModified);
                Assert.Null(doc.SelectedElement);
                Assert.Equal(2, doc.ElementsOn(0).Count);
                Assert.Contains(doc.ElementsOn(0), e => e is TextElement { Text: "Musterstadt", IsField: false });
                Assert.Contains(doc.ElementsOn(0), e => e is TextElement { Text: "Ort", IsField: true, FieldName: "Wohnort" });
                using (var saved = PdfDocument.Open(target))
                {
                    var text = PageText(saved, 0);   // other programs see both
                    Assert.Contains("Musterstadt", text);
                    Assert.Contains("Ort", text);
                    var fields = saved.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!;
                    Assert.Equal("Ort", fields.Get(0).AsDictionary()!.GetString(PdfName.Get("V"))!.ToText());
                    Assert.Equal("Wohnort", fields.Get(0).AsDictionary()!.GetString(PdfName.Get("T"))!.ToText());
                }
                WaitUntil(window, () => doc.Renderer.IsIdle);
                Shoot(window, "34-element-gespeichert");

                // 9. move after saving and save again: no element duplicated, the field name stays
                var stamp = new ElementRef(0, doc.ElementsOn(0).First(e => e is TextElement { IsField: false }));
                doc.ReplaceElement(stamp, ElementAppearance.Moved(stamp.Element, stamp.Element.Rect with { Left = stamp.Element.Rect.Left + 50, Right = stamp.Element.Rect.Right + 50 }), Tr.T("Element verschieben"));
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Equal(2, doc.ElementsOn(0).Count);
                using (var saved = PdfDocument.Open(target))
                {
                    Assert.Equal(2, saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots"))!.Count);
                    var fields = saved.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!;
                    Assert.Single(fields);
                    Assert.Equal("Wohnort", fields.Get(0).AsDictionary()!.GetString(PdfName.Get("T"))!.ToText());
                    var moved = PageItem.From(saved)[0].Elements.OfType<TextElement>().Single(e => !e.IsField);
                    Assert.Equal(stamp.Element.Rect.Left + 50, moved.Rect.Left, 3);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SignatureWindow_ShowsEveryHandwriting()
    {
        UiSession.Run(() =>
        {
            // the last font of the list — free or from a folder, whichever fonts this computer has
            var last = EmbeddedFonts.Handwriting[^1];
            var window = new SignatureWindow(new SignatureElement { Rect = default, Name = "Max Mustermann", FontFile = last });
            window.Show();
            try
            {
                UiSession.Capture(window);
                UiSession.Capture(window);
                var list = window.GetVisualDescendants().OfType<ListBox>().Single();
                Assert.Equal(EmbeddedFonts.Handwriting.Count, list.ItemCount);
                Assert.Equal(last, ((SignatureChoice)list.SelectedItem!).FontFile);
                // the one used last (here the last one) is visible without scrolling
                var selected = list.ContainerFromItem(list.SelectedItem!)!;
                var box = selected.TranslatePoint(new Point(0, 0), list)!.Value;
                Assert.InRange(box.Y, 0, list.Bounds.Height - selected.Bounds.Height + 1);
                var origin = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "OriginPreview").Text!;
                Assert.DoesNotContain(Environment.MachineName, origin, StringComparison.OrdinalIgnoreCase);   // no computer name
                Assert.EndsWith($"{Tr.T("Kennung")} {Core.Platform.DeviceCode.Current}", origin);
                Assert.StartsWith("Max Mustermann · ", origin);
                Shoot(window, "35-unterschrift-fenster");

                // screen reader: entries are named after their handwriting font, inputs have names
                Assert.Equal(((SignatureChoice)list.SelectedItem!).Family, Avalonia.Automation.AutomationProperties.GetName(selected));
                Assert.Equal(Tr.T("Name"), Avalonia.Automation.AutomationProperties.GetName(window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "NameBox")));
                Assert.Equal(Tr.T("Handschrift"), Avalonia.Automation.AutomationProperties.GetName(list));

                // typing: same entries, the selection stays, only the previews change
                var chosen = list.SelectedItem;
                var first = list.Items.Cast<object>().First();
                window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "NameBox").Text = "Max";
                UiSession.Capture(window);
                Assert.Same(chosen, list.SelectedItem);
                Assert.Same(first, list.Items.Cast<object>().First());
                Assert.All(list.Items.Cast<SignatureChoice>(), c => Assert.Equal("Max", c.Preview.Name));

                // ballpoint texture: the previews change, the texture follows from the background (smooth at first)
                window.GetVisualDescendants().OfType<CheckBox>().Single(b => b.Name == "TextureBox").IsChecked = true;
                Assert.All(list.Items.Cast<SignatureChoice>(), c => Assert.True(c.Preview.Textured));
                var until = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < until && list.Items.Cast<SignatureChoice>().Any(c => ElementAppearance.For(c.Preview).Texture is { Coarse: true }))
                {
                    UiSession.Capture(window);
                    Thread.Sleep(20);
                }
                Assert.All(list.Items.Cast<SignatureChoice>(), c => Assert.False(ElementAppearance.For(c.Preview).Texture is { Coarse: true }));
                window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "NameBox").Text = "Max Mustermann";
                UiSession.Capture(window);   // TextChanged arrives only through the dispatcher
                for (var i = 0; i < 400 && list.Items.Cast<SignatureChoice>().Any(c => ElementAppearance.For(c.Preview).Texture is { Coarse: true }); i++)
                {
                    UiSession.Capture(window);
                    Thread.Sleep(25);
                }
                // ink strength: only the palette changes, visible at once
                window.GetVisualDescendants().OfType<Slider>().Single(s => s.Name == "StrengthSlider").Value = 1.6;
                UiSession.Capture(window);
                Assert.All(list.Items.Cast<SignatureChoice>(), c => Assert.Equal(1.6, c.Preview.InkStrength, 3));
                // the background thread requests the redraw shortly after finishing
                Thread.Sleep(150);
                UiSession.Capture(window);
                Shoot(window, "35b-unterschrift-kuli-struktur");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Signature_PickPlaceChangeAndSave()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 2).Build(), "unterschrift.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
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

                // choose (a fixed answer replaces the window here), then click
                SignatureElement? offered = null;
                vm.PickSignature = current =>
                {
                    offered = (SignatureElement)current;
                    return Task.FromResult<PageElement?>(offered with { Name = "Max Mustermann", FontFile = "GochiHand-Regular.ttf" });
                };
                await vm.InsertSignatureCommand.ExecuteAsync(null);
                Assert.Equal(ViewTool.InsertSignature, vm.Tool);
                window.MouseDown(At(300, 650), MouseButton.Left);
                window.MouseUp(At(300, 650), MouseButton.Left);
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                var signature = Assert.IsType<SignatureElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal("GochiHand-Regular.ttf", signature.FontFile);
                Assert.Equal(300, (signature.Rect.Left + signature.Rect.Right) / 2, 1);
                Assert.InRange(signature.Rect.Width, 150, 190);
                Assert.Same(signature, doc.SelectedElement?.Element);
                Assert.Equal(Tr.T("Unterschrift einfügen"), doc.History.UndoDescription);
                // used last: preset the next time
                Assert.Equal("Max Mustermann", Services.UserSettings.Current.SignatureName);
                UiSession.Capture(window);
                Shoot(window, "36-unterschrift-gesetzt");

                // a click that completes a double-click with the placing click does not open the window
                var asked = 0;
                vm.PickSignature = current =>
                {
                    asked++;
                    return Task.FromResult<PageElement?>(null);
                };
                var center = At(300, (signature.Rect.Top + signature.Rect.Bottom) / 2);
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                UiSession.Capture(window);
                Assert.Equal(0, asked);
                Assert.Same(signature, doc.SelectedElement?.Element);

                // a later double-click: choose again, the position stays
                Thread.Sleep(800);   // longer than the double-click time: a new double-click, not a continuation
                vm.PickSignature = current =>
                {
                    offered = (SignatureElement)current;
                    return Task.FromResult<PageElement?>(offered with { FontFile = TestColorFonts.Sbix });
                };
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                WaitUntil(window, () => doc.ElementsOn(0) is [SignatureElement { FontFile: TestColorFonts.Sbix }]);
                Assert.Same(signature, offered);
                var changed = (SignatureElement)doc.ElementsOn(0)[0];
                Assert.Equal(signature.Rect.Height, changed.Rect.Height, 0.01);   // height and centre stay, the width follows the font
                Assert.Equal((signature.Rect.Left + signature.Rect.Right) / 2, (changed.Rect.Left + changed.Rect.Right) / 2, 0.01);
                UiSession.Capture(window);
                Shoot(window, "37-unterschrift-kuli");

                // save: first the notice that the signature becomes fixed; cancelling does not save
                var target = Path.Combine(Path.GetDirectoryName(pdf)!, "unterschrieben.pdf");
                File.Delete(target);
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                var notices = new List<string>();
                vm.ConfirmFixing = (_, notice, _) =>
                {
                    notices.Add(notice);
                    return Task.FromResult(FixingReply.Cancel);
                };
                await vm.SaveAsCommand.ExecuteAsync(null);
                Assert.Equal([Tr.T("Die Unterschrift wird beim Speichern fest in die Seite geschrieben. Danach lässt sie sich nicht mehr verschieben, ändern oder löschen.")], notices);
                Assert.False(File.Exists(target));
                Assert.True(doc.IsModified);

                // confirmed: no added text, but an image (ballpoint) in the content; afterwards no element any more (fixed)
                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                await vm.SaveAsCommand.ExecuteAsync(null);
                Assert.Empty(doc.ElementsOn(0));
                using var saved = PdfDocument.Open(target);
                Assert.Equal("Seite 1", PageText(saved, 0).Trim());
                Assert.Contains(PageInterpreter.Build(saved, saved.Pages[0]).Items.ToArray(), i => i.Kind == Core.Pdf.Graphics.DisplayItemKind.Image);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Writes a photographed signature as PNG: off-white paper, a dark blue loop.</summary>
    private static string ScannedSignature()
    {
        using var bitmap = new SkiaSharp.SKBitmap(400, 160);
        using (var canvas = new SkiaSharp.SKCanvas(bitmap))
        {
            canvas.Clear(new SkiaSharp.SKColor(250, 249, 245));
            using var paint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(25, 40, 120), StrokeWidth = 6, IsAntialias = true, Style = SkiaSharp.SKPaintStyle.Stroke };
            canvas.DrawOval(new SkiaSharp.SKRect(60, 40, 200, 120), paint);
            canvas.DrawLine(180, 110, 360, 60, paint);
        }
        var path = Path.Combine(Path.GetTempPath(), "pdf-knead-tests", "unterschrift-foto.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void SignatureWindow_DrawAndImageTabs()
    {
        var photo = ScannedSignature();
        UiSession.Run(() =>
        {
            // drawing: two strokes with the mouse
            var window = new SignatureWindow(new SignatureElement { Rect = default, Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" });
            PageElement? result = null;
            window.Closed += (_, _) => result = window.Tag as PageElement;
            window.Show();
            try
            {
                var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
                tabs.SelectedIndex = 1;
                UiSession.Capture(window);
                var ink = window.GetVisualDescendants().OfType<InkCanvas>().Single();
                Point P(double x, double y) => ink.TranslatePoint(new Point(x, y), window)!.Value;
                window.MouseDown(P(40, 80), MouseButton.Left);
                for (var i = 1; i <= 20; i++) window.MouseMove(P(40 + i * 12, 80 + 30 * Math.Sin(i / 3.0)));
                window.MouseUp(P(280, 90), MouseButton.Left);
                window.MouseDown(P(300, 50), MouseButton.Left);
                window.MouseMove(P(310, 120));
                window.MouseUp(P(310, 120), MouseButton.Left);
                Assert.Equal(2, ink.Strokes.Count);
                Assert.True(ink.Strokes[0].Length >= 30);
                UiSession.Capture(window);
                Shoot(window, "38-unterschrift-zeichnen");

                // image: load a photo, the paper becomes transparent, the margin is trimmed
                tabs.SelectedIndex = 2;
                window.LoadImage(photo);
                UiSession.Capture(window);
                Shoot(window, "39-unterschrift-bild");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Signature_DrawnAndPhoto_AreSavedWithoutBackground()
    {
        var photo = ScannedSignature();
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "bild-unterschrift.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
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

                // drawn signature (from the window, fixed here)
                vm.PickSignature = _ => Task.FromResult<PageElement?>(new InkElement
                {
                    Rect = default, Strokes = [[0, 40, 20, 0, 40, 40, 60, 0, 80, 40]], StrokeWidth = 3, Color = SignatureElement.InkBlue,
                });
                await vm.InsertSignatureCommand.ExecuteAsync(null);
                window.MouseDown(At(150, 650), MouseButton.Left);
                window.MouseUp(At(150, 650), MouseButton.Left);
                Assert.IsType<InkElement>(Assert.Single(doc.ElementsOn(0)));

                // photo through the Image command: the background is not removed (only the signature does that)
                vm.PickImageFile = () => Task.FromResult<string?>(photo);
                await vm.InsertImageCommand.ExecuteAsync(null);
                Assert.Equal(ViewTool.InsertImage, vm.Tool);
                window.MouseDown(At(400, 650), MouseButton.Left);
                window.MouseUp(At(400, 650), MouseButton.Left);
                var picture = Assert.IsType<ImageElement>(doc.ElementsOn(0)[1]);
                Assert.Equal(400.0 / 160, ElementAppearance.Aspect(picture), 2);
                Assert.Equal(Tr.T("Bild einfügen"), doc.History.UndoDescription);
                UiSession.Capture(window);
                Shoot(window, "40-bild-und-zeichnung");

                var target = Path.Combine(Path.GetDirectoryName(pdf)!, "bild-unterschrift-gespeichert.pdf");
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                await vm.SaveAsCommand.ExecuteAsync(null);
                // the drawn signature is fixed, the image remains an editable element
                Assert.IsType<ImageElement>(Assert.Single(doc.ElementsOn(0)));
                using var saved = PdfDocument.Open(target);
                var items = PageInterpreter.Build(saved, saved.Pages[0]).Items.ToArray();
                Assert.Contains(items, i => i.Kind == Core.Pdf.Graphics.DisplayItemKind.Image);
                Assert.Contains(items, i => i.Kind == Core.Pdf.Graphics.DisplayItemKind.Stroke);
                Assert.DoesNotContain(PageInterpreter.Build(saved, saved.Pages[0], withoutOwnElements: true).Items.ToArray(),
                    i => i.Kind == Core.Pdf.Graphics.DisplayItemKind.Image);   // the image is the annotation, not page content
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Signing_DocumentWithFields_OffersFlattening()
    {
        // a file with one fillable field
        using var source = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        var field = new TextElement { Rect = new PdfRectangle(100, 500, 300, 530), Text = "Musterstadt", IsField = true };
        var bytes = new MemoryStream();
        Core.Pdf.Writing.PdfWriter.Write(source, [PageItem.From(source)[0].WithElements([field])], bytes);
        var pdf = TestFiles.Write(bytes.ToArray(), "mit-feld.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
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
                Assert.True(doc.HasFlattenableContent);
                Assert.False(vm.FlattenPromptVisible);

                vm.PickSignature = current => Task.FromResult<PageElement?>(new SignatureElement
                {
                    Rect = default, Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf",
                });
                await vm.InsertSignatureCommand.ExecuteAsync(null);
                window.MouseDown(At(300, 650), MouseButton.Left);
                window.MouseUp(At(300, 650), MouseButton.Left);
                Assert.True(vm.FlattenPromptVisible);
                UiSession.Capture(window);
                Shoot(window, "41-flachlegen-abfrage");

                vm.FlattenNowCommand.Execute(null);
                Assert.False(vm.FlattenPromptVisible);
                Assert.True(doc.FlattenOnSave);
                Assert.True(vm.FlattenOnSave);   // the button on the Secure tab shows it
                vm.UndoCommand.Execute(null);    // signature undone: flattening stays selected, the document is modified
                Assert.True(doc.IsModified);

                vm.ConfirmFixing = (_, _, _) => Task.FromResult(FixingReply.Save);
                var target = Path.Combine(Path.GetDirectoryName(pdf)!, "flach.pdf");
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.SaveAsCommand.ExecuteAsync(null);
                Assert.False(doc.FlattenOnSave);
                Assert.False(doc.IsModified);
                using var saved = PdfDocument.Open(target);
                Assert.Null(saved.Catalog!.GetDictionary(PdfName.Get("AcroForm")));
                Assert.Null(saved.Pages[0].Dictionary.GetArray(PdfName.Get("Annots")));
                Assert.Contains("Musterstadt", PageText(saved, 0));
                Assert.False(doc.HasFlattenableContent);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// A digitally signed file (its signature field the only annotation) gets no offer to flatten after a further signature: there is nothing
    /// to fill in, and flattening would rewrite the file and break the signature (found with the mouse, docs/FEHLERGEDAECHTNIS.md, No. 336).
    /// </summary>
    [Fact]
    public void DigitallySigned_NoFlattenOffer()
    {
        byte[] signed;
        using (var source = PdfDocument.Open(TestPdf.Simple(pages: 1).Build()))
            signed = Core.Pdf.Signatures.PdfSigner.Sign(source, 0, new InkElement
            {
                Rect = new PdfRectangle(50, 100, 250, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3,
                Digital = new DigitalSigning("Max Mustermann"),
            }, Core.Platform.SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"flach-{Guid.NewGuid():N}.p12")),
                DateTimeOffset.Now);
        var pdf = TestFiles.Write(signed, "signiert-flach.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                Assert.False(doc.HasFlattenableContent);
                doc.AddElement(0, new SignatureElement { Rect = new PdfRectangle(300, 600, 500, 650), Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" },
                    Tr.T("Unterschrift einfügen"));
                Assert.False(vm.FlattenPromptVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SignatureField_IsPlaced_ThenSignedIntoWithOneClick()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "unterschriftsfeld.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
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

                // draw the field
                vm.SetToolCommand.Execute(ViewTool.InsertSignatureField);
                Drag(window, At(300, 620), At(500, 570));
                var field = Assert.IsType<SignatureFieldElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(200, field.Rect.Width, 1);
                Assert.Equal(Tr.T("Unterschriftsfeld einfügen"), doc.History.UndoDescription);

                // saved as a /Sig field (in between, without switching the document)
                using (var output = new MemoryStream())
                {
                    Core.Pdf.Writing.PdfWriter.Write(doc.Document, doc.History.Pages, output);
                    using var saved = PdfDocument.Open(output.ToArray());
                    var widget = saved.Catalog!.GetDictionary(PdfName.Get("AcroForm"))!.GetArray(PdfName.Get("Fields"))!.Get(0).AsDictionary()!;
                    Assert.Equal("Sig", widget.GetName(PdfName.Get("FT"))!.Value);
                    Assert.Equal(Tr.T("Unterschrift") + "1", widget.GetString(PdfName.Get("T"))!.ToText());
                }
                UiSession.Capture(window);
                Shoot(window, "42-unterschriftsfeld");

                // click into it with the Signature tool: exactly into the field, which becomes the signature
                vm.PickSignature = _ => Task.FromResult<PageElement?>(new SignatureElement { Rect = default, Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" });
                await vm.InsertSignatureCommand.ExecuteAsync(null);
                window.MouseDown(At(400, 600), MouseButton.Left);
                window.MouseUp(At(400, 600), MouseButton.Left);
                var signed = Assert.IsType<SignatureElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(field.Rect, signed.Rect);
                UiSession.Capture(window);
                Shoot(window, "43-in-feld-unterschrieben");
                vm.UndoCommand.Execute(null);
                Assert.IsType<SignatureFieldElement>(Assert.Single(doc.ElementsOn(0)));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Changing elements leaves the sidebar alone (same entries, the selection stays), and several arrow key presses in a row form
    /// one undo step.
    /// </summary>
    [Fact]
    public void ElementChange_KeepsPageEntries_ArrowKeysAreOneStep()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 3).Build(), "elemente-leiste.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                UiSession.Capture(window);
                var entry = doc.Pages[0];
                doc.Pages[2].IsSelected = true;
                var layouts = 0;
                doc.PagesChanged += () => layouts++;

                doc.AddElement(0, new TextElement { Rect = new PdfRectangle(100, 600, 300, 640), Text = "Max Mustermann" }, Tr.T("Text einfügen"));
                Assert.Same(entry, doc.Pages[0]);
                Assert.Same(doc.History.Pages[0], entry.Item);
                Assert.True(doc.Pages[2].IsSelected);
                Assert.Equal(0, layouts);
                var start = doc.SelectedElement!.Value.Element.Rect;

                // five times arrow right: one step, undo returns to the start
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                view.Focus();
                for (var i = 0; i < 5; i++) window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                var moved = doc.SelectedElement!.Value.Element.Rect;
                Assert.True(moved.Left > start.Left + 4, $"{start.Left} → {moved.Left}");
                Assert.Equal(start.Width, moved.Width, 9);
                vm.UndoCommand.Execute(null);
                Assert.Equal(start, doc.ElementsOn(0)[0].Rect);
                Assert.Equal(Tr.T("Text einfügen"), doc.History.UndoDescription);

                // rearranging pages still rebuilds the sidebar
                doc.History.Rotate([1], 90);
                Assert.Equal(1, layouts);
                Assert.Same(entry, doc.Pages[0]);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// A stamp from another program: click it, drag it, delete it with Del, undo; after saving it is at the new position. Elements
    /// from other programs have no resize handles.
    /// </summary>
    [Fact]
    public void ForeignStamp_SelectMoveDeleteUndoSave()
    {
        var pdf = TestFiles.Write(ForeignAnnotationTests.AnnotatedPage(), "fremde anmerkungen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var viewer = window.GetVisualDescendants().OfType<DocumentViewer>().Single();
                var view = viewer.View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                Assert.Equal(8, doc.ElementsOn(0).Count);

                // a click selects, dragging at the edge moves (no resize handle)
                window.MouseDown(At(410, 735), MouseButton.Left);
                window.MouseUp(At(410, 735), MouseButton.Left);
                var stamp = Assert.IsType<ForeignAnnotation>(doc.SelectedElement?.Element);
                Assert.Equal("Stamp", stamp.Kind);
                Drag(window, At(498, 702), At(398, 652));
                var moved = Assert.IsType<ForeignAnnotation>(doc.SelectedElement?.Element);
                Assert.Equal(new PdfRectangle(300, 650, 400, 690), moved.Rect with
                {
                    Left = Math.Round(moved.Rect.Left), Bottom = Math.Round(moved.Rect.Bottom),
                    Right = Math.Round(moved.Rect.Right), Top = Math.Round(moved.Rect.Top),
                });
                Assert.Equal(Tr.T("Element verschieben"), doc.History.UndoDescription);
                WaitUntil(window, () => doc.Renderer.IsIdle);
                Shoot(window, "39-fremder-stempel-verschoben");

                // Del deletes, undo brings it back at the new position
                view.Focus();
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
                Assert.Equal(7, doc.ElementsOn(0).Count);
                vm.UndoCommand.Execute(null);
                Assert.Contains(doc.ElementsOn(0), e => e is ForeignAnnotation { Kind: "Stamp" } f && f.IsMoved);

                var target = Path.Combine(Path.GetDirectoryName(pdf)!, "fremde anmerkungen verschoben.pdf");
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.SaveAsCommand.ExecuteAsync(null);
                Assert.False(doc.IsModified);
                Assert.Equal(8, doc.ElementsOn(0).Count);   // still present as elements from another program, now at the new position
                Assert.DoesNotContain(doc.ElementsOn(0), e => e is ForeignAnnotation { IsMoved: true });
                using var saved = PdfDocument.Open(target);
                var rect = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().Single(f => f.Kind == "Stamp").Rect;
                Assert.Equal(300, rect.Left, 0.5);
                Assert.Equal(690, rect.Top, 0.5);

                // form fields from other programs get the same subtle background as PDF Knead's own
                vm.FitPageCommand.Execute(null);
                WaitUntil(window, () => doc.Renderer.IsIdle);
                Shoot(window, "38-fremde-felder");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Filling in a form from another program: click into a field and type, Tab to the next one (comb field, multi-line), check box
    /// and radio button by click, choice, undo; after saving and reopening all values are present.
    /// </summary>
    [Fact]
    public void Form_FillTabToggleUndoSaveReopen()
    {
        var pdf = TestFiles.Write(FormFillTests.AllKinds(), "formular.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var viewer = window.GetVisualDescendants().OfType<DocumentViewer>().Single();
                var view = viewer.View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                void Click(double x, double y)
                {
                    window.MouseDown(At(x, y), MouseButton.Left);
                    window.MouseUp(At(x, y), MouseButton.Left);
                }
                string? Value(string name) => doc.ElementsOn(0).OfType<ForeignAnnotation>().First(f => FormField.Of(f)?.Name == name).Value;

                // name: click, type; Tab commits and opens the postcode (comb field, at most 5 characters)
                Click(150, 710);
                Assert.NotNull(doc.FillingField);
                Assert.True(viewer.Editor.IsVisible);
                viewer.Editor.Text = "Max Mustermann";
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                Assert.Equal("Max Mustermann", Value("Name"));
                Assert.Equal("PLZ", FormField.Of((ForeignAnnotation)doc.FillingField!.Value.Element)!.Name);
                Assert.Equal(5, viewer.Editor.MaxLength);
                viewer.Editor.Text = "12345";
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                Assert.Equal("12345", Value("PLZ"));
                viewer.Editor.Text = "Erste Zeile\nZweite Zeile";
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.Equal("Erste Zeile\nZweite Zeile", Value("Bemerkung"));
                Assert.Null(doc.FillingField);

                // check box and radio group by click; a click on the other option switches the first one off
                Click(80, 528);
                Assert.Equal("Ja", Value("Zustimmung"));
                Click(80, 488);
                Click(180, 488);
                var payment = doc.ElementsOn(0).OfType<ForeignAnnotation>().Where(f => FormField.Of(f)?.Name == "Zahlung").Select(f => f.Value).ToList();
                Assert.Equal(["Off", "Karte"], payment);
                Assert.Equal(Tr.T("Feld ausfüllen"), doc.History.UndoDescription);

                // choice (the view opens the menu; the value is chosen the same way)
                var land = new ElementRef(0, doc.ElementsOn(0).First(e => e is ForeignAnnotation f && FormField.Of(f)?.Name == "Land"));
                Assert.True(doc.SetFieldValue(land, "at"));
                WaitUntil(window, () => doc.Renderer.IsIdle);
                Shoot(window, "42-formular-ausgefuellt");

                // undo reverts the choice, redo restores it
                vm.UndoCommand.Execute(null);
                Assert.Null(Value("Land"));
                vm.RedoCommand.Execute(null);
                Assert.Equal("at", Value("Land"));

                var target = Path.Combine(Path.GetDirectoryName(pdf)!, "formular ausgefuellt.pdf");
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.SaveAsCommand.ExecuteAsync(null);
                Assert.False(doc.IsModified);
                using var saved = PdfDocument.Open(target);
                var again = PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().ToDictionary(f => FormField.Of(f)!.Name + "#" + FormField.Of(f)!.OnState);
                Assert.Equal("Max Mustermann", FormField.Of(again["Name#"])!.StoredValue);
                Assert.Equal("12345", FormField.Of(again["PLZ#"])!.StoredValue);
                Assert.Equal("Erste Zeile\nZweite Zeile", FormField.Of(again["Bemerkung#"])!.StoredValue);
                Assert.Equal("Ja", FormField.Of(again["Zustimmung#Ja"])!.State);
                Assert.Equal("Karte", FormField.Of(again["Zahlung#Karte"])!.StoredValue);
                Assert.Equal("Off", FormField.Of(again["Zahlung#Bar"])!.State);
                Assert.Equal("at", FormField.Of(again["Land#"])!.StoredValue);
                // after saving, the program shows the stored values (no pending changes left)
                Assert.All(doc.ElementsOn(0).OfType<ForeignAnnotation>(), f => Assert.Null(f.Value));
                WaitUntil(window, () => doc.Renderer.IsIdle);
                Shoot(window, "43-formular-gespeichert");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Form fields can be moved only in form design mode (Edit fields); it stays on until it is switched off or Esc is pressed, and in
    /// it a click does not fill in. With the text selection tool and the pan tool, dragging does not move a field.
    /// </summary>
    [Fact]
    public void Form_EditFieldsButton_OnUntilDeselectedOrEsc()
    {
        var pdf = TestFiles.Write(FormFillTests.AllKinds(), "felder verschieben.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var viewer = window.GetVisualDescendants().OfType<DocumentViewer>().Single();
                var view = viewer.View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                PdfRectangle NameRect() => doc.ElementsOn(0).OfType<ForeignAnnotation>().First(f => FormField.Of(f)!.Name == "Name").Rect;

                // text selection: dragging over the field does not move it (releasing the mouse fills in)
                Drag(window, At(150, 710), At(150, 650));
                Assert.Equal(new PdfRectangle(72, 700, 272, 720), NameRect());
                if (viewer.Editor.IsVisible) window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

                // the pan tool moves the view, never a field
                vm.SetToolCommand.Execute(ViewTool.Pan);
                Drag(window, At(150, 710), At(150, 650));
                Assert.Equal(new PdfRectangle(72, 700, 272, 720), NameRect());
                Assert.False(vm.ModeBannerVisible);
                vm.SetToolCommand.Execute(ViewTool.Pan);
                doc.ScrollToPage(0);
                UiSession.Capture(window);

                // form design: dragging moves, a click does not fill in; several fields in a row, the mode stays on
                vm.ToggleFormDesignCommand.Execute(null);
                UiSession.Capture(window);   // the mode banner pushes the page down
                Drag(window, At(150, 710), At(150, 650));
                Assert.Equal(660, NameRect().Top, 1);
                Assert.Null(doc.FillingField);
                window.MouseDown(At(100, 670), MouseButton.Left);
                window.MouseUp(At(100, 670), MouseButton.Left);
                Assert.Null(doc.FillingField);
                Assert.True(vm.IsFormDesign);
                Shoot(window, "44-felder-bearbeiten");

                // Esc (without a selection) switches back; so does a second click on the button
                doc.SelectFields([]);
                view.Focus();
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                vm.ToggleFormDesignCommand.Execute(null);
                vm.ToggleFormDesignCommand.Execute(null);
                Assert.Equal(ViewTool.SelectText, vm.Tool);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Protected form that permits only printing and filling in (bit 9): filling in works (click without moving), moving does not;
    /// after saving the protection stays with the same permissions, and the values are present.
    /// </summary>
    [Fact]
    public void Form_ProtectedFillOnly_CanBeFilledAndStaysProtected()
    {
        using var source = PdfDocument.Open(FormFillTests.AllKinds());
        using var protectedForm = new MemoryStream();
        Core.Pdf.Writing.PdfWriter.Write(source, PageItem.From(source), protectedForm, new Core.Pdf.Writing.PdfSaveOptions
        {
            Protection = new Core.Pdf.Writing.PdfProtection("", "Besitzer-1", Core.Pdf.Security.PdfPermissions.Print | Core.Pdf.Security.PdfPermissions.FillForms),
        });
        var pdf = TestFiles.Write(protectedForm.ToArray(), "geschuetztes formular.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                Assert.False(doc.CanEditElements);
                Assert.True(doc.CanFillForms);
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var viewer = window.GetVisualDescendants().OfType<DocumentViewer>().Single();
                var view = viewer.View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;

                Drag(window, At(150, 710), At(150, 610));   // dragging does not move …
                Assert.Equal(new PdfRectangle(72, 700, 272, 720), doc.ElementsOn(0).OfType<ForeignAnnotation>().First(f => FormField.Of(f)!.Name == "Name").Rect);
                if (viewer.Editor.IsVisible) window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.MouseDown(At(150, 710), MouseButton.Left);   // … a click fills in
                window.MouseUp(At(150, 710), MouseButton.Left);
                Assert.NotNull(doc.FillingField);
                viewer.Editor.Text = "Max Mustermann";
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.True(doc.IsModified);

                await vm.SaveCommand.ExecuteAsync(null);
                Assert.False(doc.IsModified);
                using var saved = PdfDocument.Open(pdf);
                Assert.NotNull(saved.Security);
                Assert.Equal(Core.Pdf.Security.PdfPermissions.Print | Core.Pdf.Security.PdfPermissions.FillForms | Core.Pdf.Security.PdfPermissions.Accessibility,
                    saved.Security!.Permissions & Core.Pdf.Security.PdfPermissions.All);
                Assert.Equal("Max Mustermann", FormField.Of(PageItem.From(saved)[0].Elements.OfType<ForeignAnnotation>().First(f => FormField.Of(f)!.Name == "Name"))!.StoredValue);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Page with many annotations from another program: the tiles draw them, only the moved one is drawn on top; the sidebar shows
    /// it at the new position.
    /// </summary>
    [Fact]
    public void ManyForeignAnnotations_MovedOneAboveTheTiles()
    {
        var pdf = TestFiles.Write(ForeignAnnotationTests.ManyAnnotations(), "viele anmerkungen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitPageCommand.Execute(null);   // full page: stamps and squares visible
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                Assert.Empty(doc.Pages[0].Item.OverlayElements);   // all 251 in the tiles

                window.MouseDown(At(305, 445), MouseButton.Left);
                window.MouseUp(At(305, 445), MouseButton.Left);
                Assert.IsType<ForeignAnnotation>(doc.SelectedElement?.Element);
                Drag(window, At(305, 445), At(155, 645));
                var moved = Assert.IsType<ForeignAnnotation>(doc.SelectedElement?.Element);
                Assert.Equal(150, moved.Rect.Left, 1);
                Assert.Same(moved, Assert.Single(doc.Pages[0].Item.OverlayElements));
                WaitUntil(window, () => doc.Renderer.IsIdle);
                Shoot(window, "40-viele-anmerkungen-verschoben");

                vm.UndoCommand.Execute(null);
                Assert.Empty(doc.Pages[0].Item.OverlayElements);
                WaitUntil(window, () => doc.Renderer.IsIdle);
                Shoot(window, "41-viele-anmerkungen-zurueck");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Text in annotations from other programs (free text, field values) remains available for search and selection although the
    /// view draws these annotations above the tiles, and it moves along when the annotation is moved.
    /// </summary>
    [Fact]
    public void ForeignFreeText_StaysSearchable_AndMovesWithTheAnnotation()
    {
        var pdf = TestPdf.Simple(1);
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica"))));
        var appearance = pdf.Add(TestPdf.FlateStream("BT /F1 12 Tf 4 6 Td (Musterstadt) Tj ET"u8.ToArray(), TestPdf.Dict(("Type", TestPdf.N("XObject")),
            ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 120, 24)), ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(font)))))))));
        var note = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("FreeText")), ("Rect", TestPdf.Box(300, 500, 420, 524)),
            ("AP", TestPdf.Dict(("N", TestPdf.Ref(appearance))))));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Get("Annots"), new PdfArray([TestPdf.Ref(note)]));
        var path = TestFiles.Write(pdf.Build(), "freitext.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([path]);
                var doc = vm.SelectedDocument!;
                UiSession.Capture(window);
                var text = await doc.GetTextAsync(0);
                Assert.Single(text.Find("Musterstadt"));
                Assert.True(text.IsOverText(330, 512));

                var free = new ElementRef(0, Assert.Single(doc.ElementsOn(0)));
                doc.ReplaceElement(free, ElementAppearance.Moved(free.Element, new PdfRectangle(100, 300, 220, 324)), Tr.T("Element verschieben"));
                text = await doc.GetTextAsync(0);
                Assert.Single(text.Find("Musterstadt"));
                Assert.False(text.IsOverText(330, 512));
                Assert.True(text.IsOverText(130, 312));

                vm.UndoCommand.Execute(null);
                Assert.True((await doc.GetTextAsync(0)).IsOverText(330, 512));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The pan tool does not grab elements; without a selection the ribbon shows the defaults for new texts; paging on in
    /// single-page layout commits the text of the input box.
    /// </summary>
    [Fact]
    public void PanTool_RibbonDefaults_EditorCommitsWhenPageLeaves()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 3).Build(), "werkzeuge.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitWidthCommand.Execute(null);
                UiSession.Capture(window);
                var viewer = window.GetVisualDescendants().OfType<DocumentViewer>().Single();
                var view = viewer.View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;

                // ribbon: selected → its formatting, deselected → the defaults
                ElementDefaults.Text = ElementDefaults.Text with { FontSize = 11 };
                var element = new TextElement { Rect = new PdfRectangle(100, 700, 300, 740), Text = "Max Mustermann", FontSize = 20 };
                doc.AddElement(0, element, Tr.T("Text einfügen"));
                Assert.Equal(20, vm.TextSize);
                doc.SelectedElement = null;
                Assert.Equal(11, vm.TextSize);
                Assert.Same(element, doc.ElementsOn(0)[0]);   // showing the defaults changes no element

                // pan tool: dragging over the element moves the view, not the element
                vm.SetToolCommand.Execute(ViewTool.Pan);
                var before = view.Offset;
                Drag(window, At(150, 720), At(150, 520));
                Assert.Same(element, doc.ElementsOn(0)[0]);
                Assert.NotEqual(before, view.Offset);
                vm.SetToolCommand.Execute(ViewTool.SelectText);

                // single page: edit, then page on → committed, input box gone
                doc.PageLayout = PageLayout.Single;
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                doc.BeginEdit(new ElementRef(0, element), isNew: false);
                UiSession.Capture(window);
                Assert.True(viewer.Editor.IsVisible);
                viewer.Editor.Text = "Musterstadt";
                doc.ScrollToPage(1);
                UiSession.Capture(window);
                UiSession.Capture(window);
                Assert.False(viewer.Editor.IsVisible);
                Assert.Null(doc.EditingElement);
                Assert.Equal("Musterstadt", ((TextElement)doc.ElementsOn(0)[0]).Text);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>Setting a password and permissions (applied when saving), continuing to work encrypted, removing the protection.</summary>
    [Fact]
    public void Protection_SetSaveReopenRemove()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 2).Build(), "schutz.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                Assert.False(doc.IsProtected);
                var rights = Core.Pdf.Security.PdfPermissions.Print | Core.Pdf.Security.PdfPermissions.Accessibility;
                vm.PickProtection = (_, _, _) => Task.FromResult<ProtectionChange?>(new ProtectionChange(new Core.Pdf.Writing.PdfProtection("geheim", "Besitzer-1", rights)));
                await vm.EditProtectionCommand.ExecuteAsync(null);
                Assert.True(doc.IsModified);
                Assert.True(doc.IsProtected);

                await vm.SaveCommand.ExecuteAsync(null);
                Assert.False(doc.IsModified);
                Assert.True(doc.Document.Security!.IsOwner);   // reopened with the owner password: all permissions
                Assert.True(doc.CanEditPages);
                Assert.Throws<Core.Pdf.PdfPasswordException>(() => PdfDocument.Open(pdf));
                using (var user = PdfDocument.Open(pdf, "geheim"))
                    Assert.Equal(rights, user.Security!.Permissions & Core.Pdf.Security.PdfPermissions.All);

                // keep editing and save: the protection stays
                doc.History.Rotate([0], 90);
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Throws<Core.Pdf.PdfPasswordException>(() => PdfDocument.Open(pdf));

                // remove
                vm.PickProtection = (_, _, _) => Task.FromResult<ProtectionChange?>(new ProtectionChange(null));
                await vm.EditProtectionCommand.ExecuteAsync(null);
                Assert.False(doc.IsProtected);
                await vm.SaveCommand.ExecuteAsync(null);
                using var plain = PdfDocument.Open(pdf);
                Assert.Null(plain.Security);
                Assert.Equal(90, plain.Pages[0].Rotate);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>Redaction: drawing an area, redacting selected text, search and redact (words and patterns), one undo step per action.</summary>
    [Fact]
    public void Redaction_AreaTextAndSearch()
    {
        var pdf = TestFiles.Write(RedactionTests.TextPage(
            "Max Mustermann, Musterstrasse 12, 12345 Musterstadt",
            "E-Mail max.mustermann@example.org, Telefon +49 30 1234567",
            "IBAN DE89 3704 0044 0532 0130 00 - Max Mustermann zahlt"), "schwaerzen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Secure;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;

                // 1. area: the tool stays active, a click without dragging redacts nothing
                vm.SetToolCommand.Execute(ViewTool.InsertRedaction);
                Drag(window, At(72, 500), At(272, 400));
                var area = Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(72, area.Rect.Left, 1);
                Assert.Equal(ViewTool.InsertRedaction, vm.Tool);
                window.MouseDown(At(400, 300), MouseButton.Left);
                window.MouseUp(At(400, 300), MouseButton.Left);
                Assert.Single(doc.ElementsOn(0));
                vm.SetToolCommand.Execute(ViewTool.SelectText);

                // 2. selected text: the word „Musterstadt“ in the first line
                var text = await doc.GetTextAsync(0);
                var range = Assert.Single(text.Find("Musterstadt"));
                doc.Select(0, range);
                vm.RedactTextCommand.Execute(null);
                Assert.Equal(2, doc.ElementsOn(0).Count);
                Assert.False(doc.HasTextSelection);
                Assert.Equal(Tr.T("Text schwärzen"), doc.History.UndoDescription);

                // 3. search and redact: name (twice), e-mail, phone, IBAN, all in one step
                vm.PickRedactionHits = async find =>
                {
                    var dialog = new SearchRedactWindow(find);
                    dialog.Show();
                    dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "WordsBox").Text = "Max Mustermann";
                    foreach (var name in (ReadOnlySpan<string>)["EmailBox", "PhoneBox", "IbanBox"])
                        dialog.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == name).IsChecked = true;
                    await dialog.SearchAsync();
                    UiSession.Capture(dialog);
                    Shoot(dialog, "48-suchen-und-schwaerzen");
                    var found = dialog.Choices.Select(c => c.Hit.Found).ToList();
                    // a reason for all chosen hits (wish of 4.11.2026); an own one naming a searched word is warned about; a code of the
                    // German set replaces it once the own reason is emptied
                    var picker = dialog.GetVisualDescendants().OfType<RedactionReasonPicker>().Single();
                    var own = picker.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "OwnBox");
                    var warning = picker.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RevealWarning");
                    own.Text = "wegen Mustermann";
                    Assert.True(warning.IsVisible);
                    own.Text = "";
                    Assert.False(warning.IsVisible);
                    picker.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "SetBox").SelectedItem = picker.Sets.Single(s => s.Id == "de");
                    var codes = picker.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "CodeBox");
                    codes.SelectedItem = codes.Items.OfType<RedactionReasonCode>().Single(c => c.Code == "§ 5 IFG");
                    Assert.Equal("§ 5 IFG", picker.Choice?.Code);
                    dialog.Close();
                    Assert.Contains("Max Mustermann", found);
                    Assert.Contains("max.mustermann@example.org", found);
                    Assert.Contains(found, f => f.StartsWith("+49 30 1234567"));
                    Assert.Contains(found, f => f.StartsWith("DE89 3704 0044 0532 0130 00"));
                    return dialog.Chosen();
                };
                await vm.SearchRedactCommand.ExecuteAsync(null);
                var redactions = doc.ElementsOn(0).OfType<RedactionElement>().ToList();
                Assert.True(redactions.Count >= 2 + 5, $"{redactions.Count} Schwärzungen");
                Assert.Equal(redactions.Count - 2, redactions.Count(r => r is { ReasonCode: "§ 5 IFG", ReasonBasis: "Informationsfreiheitsgesetz des Bundes (IFG), § 5" }));
                Assert.Equal(Tr.T("Suchen und schwärzen"), doc.History.UndoDescription);
                UiSession.Capture(window);
                Shoot(window, "49-schwaerzungen-markiert");
                vm.UndoCommand.Execute(null);   // one undo removes all hits
                Assert.Equal(2, doc.ElementsOn(0).Count);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Redact first: Ctrl+S with only marked redactions does not ask (work in progress, the text is still there); "Apply
    /// redactions" redacts in the open document (the file stays untouched, Ctrl+Z brings it back); Ctrl+S then asks whether to
    /// overwrite the file. Saving as a new file leaves the original alone; overwriting (here with the pages also as images) removes
    /// the text there.
    /// </summary>
    [Fact]
    public void Redaction_InDocument_UndoAndSaveQuestion()
    {
        var pdf = TestFiles.Write(RedactionTests.TextPage("Max Mustermann, Musterstrasse 12, 12345 Musterstadt", "Zweite Zeile bleibt"), "schwaerzen-anwenden.pdf");
        string PageText(string path, bool withoutMarks = true)
        {
            using var file = PdfDocument.Open(path);
            return string.Concat(Core.Pdf.Content.PageInterpreter.Build(file, file.Pages[0], withoutOwnElements: withoutMarks).Text.Select(t => t.Text));
        }
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Secure;
                var text = await doc.GetTextAsync(0);
                doc.Select(0, Assert.Single(text.Find("Musterstrasse")));
                vm.RedactTextCommand.Execute(null);

                // 1. Ctrl+S with only marked redactions: no question, the text is still in the file, the mark comes back
                var questions = new List<RedactionQuestion>();
                vm.ConfirmRedaction = question =>
                {
                    questions.Add(question);
                    return Task.FromResult(new RedactionReply(RedactionAnswer.KeepMarking));
                };
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Empty(questions);
                Assert.Contains("Musterstrasse", PageText(pdf));
                Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0)));

                // 2. "Apply redactions": first the question; "Keep marking" leaves everything as it is
                vm.ConfirmRedaction = question =>
                {
                    questions.Add(question);
                    var dialog = new RedactionWindow(question);
                    dialog.SizeToContent = SizeToContent.Manual;   // otherwise the capture covers only part of the window
                    dialog.Height = 360;
                    dialog.Show();
                    UiSession.Capture(dialog);
                    Shoot(dialog, "50-schwaerzen-jetzt");
                    dialog.Close();
                    return Task.FromResult(new RedactionReply(RedactionAnswer.KeepMarking));
                };
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                var asked = Assert.Single(questions);
                Assert.Equal((1, 1, (string?)null, true), (asked.Marks, asked.Pages, asked.Action, asked.CanChooseImages));
                Assert.Empty(asked.Unredacted);   // „Musterstrasse“ occurs only once
                Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.False(doc.HasAppliedRedactions);

                // 3. redact now: in the document (text gone), the file stays as it is
                vm.ConfirmRedaction = _ => Task.FromResult(new RedactionReply(RedactionAnswer.Apply));
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                Assert.Empty(doc.ElementsOn(0));
                Assert.DoesNotContain("Musterstrasse", (await doc.GetTextAsync(0)).Text);
                Assert.Contains("Zweite Zeile bleibt", (await doc.GetTextAsync(0)).Text);
                Assert.True(doc.HasAppliedRedactions);
                Assert.True(doc.IsModified);
                Assert.Contains("Musterstrasse", PageText(pdf));

                // 4. undo brings back the text and the mark, redo redacts again
                vm.UndoCommand.Execute(null);
                Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Contains("Musterstrasse", (await doc.GetTextAsync(0)).Text);
                Assert.False(doc.HasAppliedRedactions);
                vm.RedoCommand.Execute(null);
                Assert.Empty(doc.ElementsOn(0));
                Assert.True(doc.HasAppliedRedactions);

                // 5. Ctrl+S: question, save as a new file (suggested name with „(geschwärzt)“); the original stays
                var saveQuestions = new List<RedactedSaveQuestion>();
                vm.ConfirmRedactedSave = question =>
                {
                    saveQuestions.Add(question);
                    var dialog = new RedactedSaveWindow(question);
                    dialog.SizeToContent = SizeToContent.Manual;
                    dialog.Height = 300;
                    dialog.Show();
                    UiSession.Capture(dialog);
                    Shoot(dialog, "51-geschwaerztes-ueberschreiben");
                    dialog.Close();
                    return Task.FromResult(RedactedSaveAnswer.SaveAsNew);
                };
                var target = Path.Combine(Path.GetDirectoryName(pdf)!, "geschwaerzt.pdf");
                string? suggested = null;
                vm.PickSavePath = (_, name, _) =>
                {
                    suggested = name;
                    return Task.FromResult<string?>(target);
                };
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Equal("schwaerzen-anwenden.pdf", Assert.Single(saveQuestions).FileName);
                Assert.Equal($"schwaerzen-anwenden ({Tr.T("geschwärzt")}).pdf", suggested);
                Assert.DoesNotContain("Musterstrasse", PageText(target, withoutMarks: false));
                Assert.Contains("Max Mustermann,", PageText(target));
                Assert.Contains("Zweite Zeile bleibt", PageText(target));
                Assert.Contains("Musterstrasse", PageText(pdf));   // original untouched
                Assert.Equal(Path.GetFullPath(target), doc.Path);
                Assert.False(doc.IsModified);
                Assert.False(doc.HasAppliedRedactions);

                // 6. the original again: redact with the page also as an image, then overwrite; no text left in it
                await vm.OpenAsync([pdf]);
                var original = vm.SelectedDocument!;
                Assert.IsType<RedactionElement>(Assert.Single(original.ElementsOn(0)));
                vm.ConfirmRedaction = _ => Task.FromResult(new RedactionReply(RedactionAnswer.ApplyAsImages));
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                Assert.Equal("", (await original.GetTextAsync(0)).Text.Trim());
                vm.ConfirmRedactedSave = _ => Task.FromResult(RedactedSaveAnswer.Overwrite);
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Equal("", PageText(pdf, withoutMarks: false));
                using var saved = PdfDocument.Open(pdf);
                Assert.Equal(1, saved.PageCount);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Redacted text also occurs elsewhere: the question lists the occurrences (not „Maximal“, nor anything already redacted), and
    /// with the check box they are redacted as well; afterwards the name appears nowhere in the file.
    /// </summary>
    [Fact]
    public void Redaction_OtherOccurrences_ShownAndRedactedToo()
    {
        // the name is also in the title, where no bar helps, so the dialog offers the cleanup
        var pdf = TestFiles.Write(RedactionTests.TitledTextPage("Vertrag Max Mustermann", "Max Mustermann, Musterstrasse 12",
            "Maximal zwei Termine mit Max Mustermann", "Kontakt: MAX MUSTERMANN"), "weitere-stellen.pdf");
        string PageText(string path)
        {
            using var file = PdfDocument.Open(path);
            return string.Concat(Core.Pdf.Content.PageInterpreter.Build(file, file.Pages[0]).Text.Select(t => t.Text));
        }
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var text = await doc.GetTextAsync(0);
                doc.Select(0, text.Find("Max Mustermann")[0]);
                vm.RedactTextCommand.Execute(null);

                RedactionQuestion? asked = null;
                vm.ConfirmRedaction = question =>
                {
                    asked = question;
                    var dialog = new RedactionWindow(question);
                    dialog.Show();
                    UiSession.Capture(dialog);
                    Shoot(dialog, "55-schwaerzen-weitere-stellen");
                    var reply = dialog.Reply(RedactionAnswer.Apply);
                    dialog.Close();
                    return Task.FromResult(reply);
                };
                var target = Path.Combine(Path.GetDirectoryName(pdf)!, "alle stellen.pdf");
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                await vm.SaveAsCommand.ExecuteAsync(null);   // nothing marked any more: no second question

                // the same name twice (also in capitals); „Maximal“ does not count
                Assert.NotNull(asked);
                Assert.Equal(2, asked!.Unredacted.Count);
                Assert.All(asked.Unredacted, h => Assert.Equal("max mustermann", h.Found.ToLowerInvariant()));
                var after = PageText(target);
                Assert.DoesNotContain("mustermann", after.ToLowerInvariant());
                Assert.Contains("Maximal zwei Termine mit", after);
                Assert.Contains("Musterstrasse 12", after);
                // title: reported as a location and cleaned up through the (preselected) check box
                Assert.Contains(asked.Elsewhere, e => e.Cleanup == Core.Pdf.Writing.CleanupItems.Metadata);
                using (var saved = PdfDocument.Open(target))
                    Assert.DoesNotContain("Mustermann", saved.Info.Title ?? "");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The area redaction tool stays on: dragging over text marks the text, a double-click marks a word, grabbing a mark moves it,
    /// a handle reshapes it, and on empty space an area is created, all without switching tools.
    /// </summary>
    [Fact]
    public void Redaction_Tool_MarksTextMovesAndReshapes()
    {
        var pdf = TestFiles.Write(RedactionTests.TextPage("Max Mustermann, Musterstrasse 12, 12345 Musterstadt", "Zweite Zeile bleibt"), "werkzeug.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Secure;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                var text = await doc.GetTextAsync(0);
                vm.SetToolCommand.Execute(ViewTool.InsertRedaction);

                // 1. drag over a word: the text is marked, not the dragged rectangle
                var street = Assert.Single(text.Quads(Assert.Single(text.Find("Musterstrasse")))).Bounds;
                var middle = (street.Top + street.Bottom) / 2;
                Drag(window, At(street.Left + 0.5, middle), At(street.Right - 0.5, middle));
                var first = Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.InRange(first.Rect.Height, street.Bottom - street.Top, street.Bottom - street.Top + 2);   // line height, not 0
                Assert.Equal(ViewTool.InsertRedaction, vm.Tool);
                Assert.False(doc.HasTextSelection);

                // 2. double-click on a word of the second line
                Thread.Sleep(800);
                var word = Assert.Single(text.Quads(Assert.Single(text.Find("Zeile")))).Bounds;
                var at = At((word.Left + word.Right) / 2, (word.Top + word.Bottom) / 2);
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(at, MouseButton.Left);
                Assert.Equal(2, doc.ElementsOn(0).Count);

                // 3. grab the first mark and drag it 30 pt to the right
                Thread.Sleep(800);
                Drag(window, At(first.Rect.Left + 5, middle), At(first.Rect.Left + 35, middle));
                var moved = doc.ElementsOn(0).OfType<RedactionElement>().Single(r => Math.Abs(r.Rect.Bottom - first.Rect.Bottom) < 0.5);
                Assert.Equal(first.Rect.Left + 30, moved.Rect.Left, 1);
                Assert.Equal(Tr.T("Element verschieben"), doc.History.UndoDescription);

                // 4. right handle (selected after moving) 20 pt further: reshaped
                Thread.Sleep(800);
                var mid = (moved.Rect.Top + moved.Rect.Bottom) / 2;
                Drag(window, At(moved.Rect.Right, mid), At(moved.Rect.Right + 20, mid));
                var reshaped = doc.ElementsOn(0).OfType<RedactionElement>().Single(r => Math.Abs(r.Rect.Bottom - first.Rect.Bottom) < 0.5);
                Assert.Equal(moved.Rect.Width + 20, reshaped.Rect.Width, 1);

                // 5. empty space (the blue rectangle is graphics, not text): an area
                Thread.Sleep(800);
                Drag(window, At(80, 490), At(200, 420));
                Assert.Equal(3, doc.ElementsOn(0).Count);
                Assert.Equal(ViewTool.InsertRedaction, vm.Tool);
                UiSession.Capture(window);
                Shoot(window, "54-schwaerzen-werkzeug");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The text redaction tool stays on (already selected text at once, then every selection); marks can be grabbed in it and with
    /// the pan tool; Esc and a second click on the button end the redaction tools, and the button shows the state correctly.
    /// </summary>
    [Fact]
    public void Redaction_TextToolStaysOn_EscAndSecondClickEnd_HandMovesMarks()
    {
        var pdf = TestFiles.Write(RedactionTests.TextPage("Max Mustermann, Musterstrasse 12, 12345 Musterstadt", "Zweite Zeile bleibt"), "textwerkzeug.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Secure;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                RibbonToggleButton Button(string label) =>
                    window.GetVisualDescendants().OfType<RibbonToggleButton>().Single(b => b.Label == Tr.T(label) && b.IsEffectivelyVisible);
                void Click(Control control)
                {
                    Thread.Sleep(800);
                    var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                    window.MouseDown(at, MouseButton.Left);
                    window.MouseUp(at, MouseButton.Left);
                    UiSession.Capture(window);
                }
                void Escape()
                {
                    window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                    UiSession.Capture(window);
                }
                var text = await doc.GetTextAsync(0);
                var textButton = Button("Text schwärzen");
                var areaButton = Button("Bereich schwärzen");

                // 1. text already selected becomes a mark as soon as the tool is switched on; the tool stays on
                doc.Select(0, Assert.Single(text.Find("Musterstrasse")));
                Click(textButton);
                var first = Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(ViewTool.RedactText, vm.Tool);
                Assert.True(textButton.IsChecked);

                // 2. drag over more text: marked on release, without another click on the button
                Thread.Sleep(800);
                var word = Assert.Single(text.Quads(Assert.Single(text.Find("Zeile")))).Bounds;
                var wordMiddle = (word.Top + word.Bottom) / 2;
                Drag(window, At(word.Left + 0.5, wordMiddle), At(word.Right - 0.5, wordMiddle));
                Assert.Equal(2, doc.ElementsOn(0).Count);
                Assert.Equal(ViewTool.RedactText, vm.Tool);

                // 3. grab a mark within the tool and move it
                Thread.Sleep(800);
                var middle = (first.Rect.Top + first.Rect.Bottom) / 2;
                Drag(window, At(first.Rect.Left + 5, middle), At(first.Rect.Left + 25, middle));
                RedactionElement FirstMark() => doc.ElementsOn(0).OfType<RedactionElement>().Single(r => Math.Abs(r.Rect.Bottom - first.Rect.Bottom) < 0.5);
                Assert.Equal(first.Rect.Left + 20, FirstMark().Rect.Left, 1);
                Assert.Equal(ViewTool.RedactText, vm.Tool);

                // 4. Esc ends the tool, the button turns off
                Escape();
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                Assert.False(textButton.IsChecked);

                // 5. area redaction: on, a second click off (the button shows it correctly), on again, Esc off
                Click(areaButton);
                Assert.Equal(ViewTool.InsertRedaction, vm.Tool);
                Assert.True(areaButton.IsChecked);
                Click(areaButton);
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                Assert.False(areaButton.IsChecked);
                Click(areaButton);
                Assert.Equal(ViewTool.InsertRedaction, vm.Tool);
                Escape();
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                Assert.False(areaButton.IsChecked);
                // a second click on the text redaction button also switches it off
                Click(textButton);
                Assert.Equal(ViewTool.RedactText, vm.Tool);
                Click(textButton);
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                Assert.False(textButton.IsChecked);
                Assert.Equal(2, doc.ElementsOn(0).Count);

                // 6. pan tool: on a mark it moves the mark, elsewhere the view
                vm.SetToolCommand.Execute(ViewTool.Pan);
                var before = FirstMark().Rect;
                Thread.Sleep(800);
                Drag(window, At(before.Left + 5, middle), At(before.Left + 35, middle));
                Assert.Equal(before.Left + 30, FirstMark().Rect.Left, 1);
                Assert.Equal(ViewTool.Pan, vm.Tool);
                Thread.Sleep(800);
                var moved = FirstMark().Rect;
                Drag(window, At(400, 300), At(400, 200));
                Assert.Equal(moved, FirstMark().Rect);
                Assert.Equal(2, doc.ElementsOn(0).Count);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// At 800 % zoom: draw a small bar tightly around a word of a 6 pt line, shrink it with the handle (below the 8 pt of text
    /// fields), move it by one screen pixel with an arrow key, apply; only this word is gone.
    /// </summary>
    [Fact]
    public void Redaction_AtHighZoom_SmallPreciseBar()
    {
        var pdf = TestFiles.Write(SmallTextPage(), "klein.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Secure;
                UiSession.Capture(window);
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 8;
                UiSession.Capture(window);
                Assert.Equal(800, doc.ZoomPercent, 0);
                var viewer = window.GetVisualDescendants().OfType<DocumentViewer>().Single();
                var view = viewer.View;
                var text = await doc.GetTextAsync(0);
                var word = Assert.Single(text.Quads(Assert.Single(text.Find("Geheim")))).Bounds;
                // scroll the spot to the middle of the window
                var target = view.PagePointToView(0, word.Left, word.Bottom);
                view.Offset += new Vector(target.X - view.Bounds.Width / 2, target.Y - view.Bounds.Height / 2);
                UiSession.Capture(window);
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;

                // draw tightly around the word (the line is only 6 pt high, so a minimum size of 4 or 8 pt would not fit)
                vm.SetToolCommand.Execute(ViewTool.InsertRedaction);
                Drag(window, At(word.Left - 0.2, word.Bottom + 0.2), At(word.Right + 0.2, word.Top - 0.2));
                var bar = Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.True(bar.Rect.Height < 8, $"Höhe {bar.Rect.Height}");
                vm.SetToolCommand.Execute(ViewTool.SelectText);
                UiSession.Capture(window);
                Shoot(window, "51-schwaerzen-starker-zoom");

                // drag the bottom middle handle (5) upwards: shrinking below 8 pt works
                doc.SelectedElement = new ElementRef(0, bar);
                UiSession.Capture(window);
                var before = bar.Rect.Height;
                Thread.Sleep(800);   // longer than the double-click time: no double-click with the drawing
                Drag(window, At((bar.Rect.Left + bar.Rect.Right) / 2, bar.Rect.Bottom), At((bar.Rect.Left + bar.Rect.Right) / 2, bar.Rect.Bottom + 0.6));
                bar = Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.True(bar.Rect.Height < before - 0.3, $"{before} → {bar.Rect.Height}");
                Assert.True(bar.Rect.Height > 4, $"Höhe {bar.Rect.Height}");

                // arrow key: one screen pixel, not a whole point
                doc.SelectedElement = new ElementRef(0, bar);
                view.Focus();
                var left = bar.Rect.Left;
                window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                bar = Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.InRange(bar.Rect.Left - left, 0.05, 0.3);
                window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);

                var target2 = Path.Combine(Path.GetDirectoryName(pdf)!, "klein geschwaerzt.pdf");
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target2);
                vm.ConfirmRedaction = _ => Task.FromResult(new RedactionReply(RedactionAnswer.Apply));
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                await vm.SaveAsCommand.ExecuteAsync(null);
                using var saved = PdfDocument.Open(target2);
                var after = PageText(saved, 0);
                Assert.DoesNotContain("Geheim", after);
                Assert.Contains("Zeile oben", after);
                Assert.Contains("Zeile unten", after);
                Assert.Contains("vorher", after);
                Assert.Contains("nachher", after);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// File → Export with a marked redaction: first the question ("Keep marking" exports nothing), then redacted in the document;
    /// PDF as image (a new file without text, the tab keeps showing the open file) and pages as images (one PNG file per page, the
    /// redaction black, not marked in red).
    /// </summary>
    [Fact]
    public void Export_ImagePdfAndPageImages_DocumentStays()
    {
        var pdf = TestFiles.Write(RedactionTests.TextPage("Max Mustermann, Musterstrasse 12", "Zweite Zeile"), "exportieren.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var text = await doc.GetTextAsync(0);
                doc.Select(0, Assert.Single(text.Find("Musterstrasse")));
                vm.RedactTextCommand.Execute(null);
                vm.BackstageOpen = true;
                vm.ShowBackstageCommand.Execute(BackstagePage.Export);
                UiSession.Capture(window);
                Shoot(window, "52-exportieren");

                var purposes = new List<RasterPurpose>();
                vm.PickExportRaster = purpose =>
                {
                    purposes.Add(purpose);
                    var dialog = new RasterWindow(purpose);
                    dialog.Show();
                    UiSession.Capture(dialog);
                    var settings = dialog.Settings();
                    dialog.Close();
                    return Task.FromResult<PdfKnead.Rendering.RasterSettings?>(settings with { Dpi = 72 });
                };
                vm.PickPageImages = (start, all, selected) =>
                {
                    var dialog = new PageImagesWindow(start, all, selected, document: true);
                    dialog.Show();
                    UiSession.Capture(dialog);
                    Shoot(dialog, "53-seiten-als-bilder");
                    var settings = dialog.Settings();
                    dialog.Close();
                    return Task.FromResult<PdfKnead.Core.Imaging.PageImageSettings?>(settings with { Format = PdfKnead.Core.Imaging.PageImageFormat.Png, Dpi = 72 });
                };
                var target = Path.Combine(Path.GetDirectoryName(pdf)!, "als bild.pdf");
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                var area = Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0))).Rect;
                var actions = new List<string?>();
                vm.ConfirmRedaction = question =>
                {
                    actions.Add(question.Action);
                    return Task.FromResult(new RedactionReply(RedactionAnswer.KeepMarking));
                };
                await vm.ExportImagePdfCommand.ExecuteAsync(null);
                Assert.False(File.Exists(target));
                Assert.Empty(purposes);
                vm.ConfirmRedaction = question =>
                {
                    actions.Add(question.Action);
                    return Task.FromResult(new RedactionReply(RedactionAnswer.Apply));
                };
                await vm.ExportImagePdfCommand.ExecuteAsync(null);
                Assert.Equal([Tr.T("Exportieren"), Tr.T("Exportieren")], actions);
                using (var exported = PdfDocument.Open(target))
                {
                    Assert.Empty(PageInterpreter.Build(exported, exported.Pages[0]).Text);
                    Assert.Equal(1, exported.PageCount);
                }
                // the tab keeps showing the open file, now redacted (not saved yet)
                Assert.Equal(Path.GetFullPath(pdf), doc.Path);
                Assert.Empty(doc.ElementsOn(0));
                Assert.True(doc.HasAppliedRedactions);

                var folder = Directory.CreateTempSubdirectory("pdf-knead-bilder-").FullName;
                vm.PickFolder = _ => Task.FromResult<string?>(folder);
                await vm.ExportImagesCommand.ExecuteAsync(null);
                Assert.Equal(2, actions.Count);   // nothing marked any more: no question
                Assert.Equal([RasterPurpose.ExportPdf], purposes);
                var file = Assert.Single(Directory.GetFiles(folder));
                Assert.Equal("exportieren_001.png", Path.GetFileName(file));
                // black under the redaction, not marked in red
                using var bitmap = SkiaSharp.SKBitmap.Decode(file);
                var pixel = bitmap.GetPixel((int)((area.Left + area.Right) / 2), (int)(bitmap.Height - (area.Bottom + area.Top) / 2));
                Assert.True(pixel.Red < 30 && pixel.Green < 30 && pixel.Blue < 30, $"{pixel}");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The redact-first gate in front of everything that leaves the document: with a marked redaction, printing, compressing,
    /// splitting, "Save as", extracting and exporting ask first, and "Keep marking" opens none of them; without the gate the outputs
    /// refuse the mark instead of redacting along the way; copying omits the text under the mark; after redacting now, the action
    /// continues.
    /// </summary>
    [Fact]
    public void RedactFirst_GatesOutputs_CopyWithoutMarkedText()
    {
        const string line = "Max Mustermann, Musterstrasse 12, 12345 Musterstadt";
        var pdf = TestFiles.Write(RedactionTests.TextPage(line, "Zweite Zeile"), "schleuse.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var text = await doc.GetTextAsync(0);
                doc.Select(0, Assert.Single(text.Find("Musterstrasse")));
                vm.RedactTextCommand.Execute(null);
                vm.SetToolCommand.Execute(ViewTool.SelectText);

                var opened = new List<string>();
                vm.ShowPrint = _ =>
                {
                    opened.Add("Drucken");
                    return Task.FromResult<string?>(null);
                };
                vm.ShowCompress = _ =>
                {
                    opened.Add("Komprimieren");
                    return Task.FromResult<string?>(null);
                };
                vm.ShowSplit = _ =>
                {
                    opened.Add("Teilen");
                    return Task.FromResult(false);
                };
                vm.PickSavePath = (_, _, _) =>
                {
                    opened.Add("Speichern unter");
                    return Task.FromResult<string?>(null);
                };
                vm.PickExportRaster = _ =>
                {
                    opened.Add("Exportieren");
                    return Task.FromResult<PdfKnead.Rendering.RasterSettings?>(null);
                };
                vm.PickPageImages = (_, _, _) =>
                {
                    opened.Add("Bilder");
                    return Task.FromResult<PdfKnead.Core.Imaging.PageImageSettings?>(null);
                };
                vm.PickFolder = _ =>
                {
                    opened.Add("Ordner");
                    return Task.FromResult<string?>(null);
                };
                foreach (var p in doc.Pages) p.IsSelected = true;   // for extracting
                var actions = new List<string?>();
                vm.ConfirmRedaction = question =>
                {
                    actions.Add(question.Action);
                    return Task.FromResult(new RedactionReply(RedactionAnswer.KeepMarking));
                };
                await vm.PrintCommand.ExecuteAsync(null);
                await vm.CompressCommand.ExecuteAsync(null);
                await vm.SplitCommand.ExecuteAsync(null);
                await vm.SaveAsCommand.ExecuteAsync(null);
                await vm.ExtractPagesCommand.ExecuteAsync(null);
                await vm.ExportImagePdfCommand.ExecuteAsync(null);
                await vm.ExportImagesCommand.ExecuteAsync(null);
                Assert.Empty(opened);
                Assert.Equal([Tr.T("Drucken"), Tr.T("Komprimieren"), Tr.T("Teilen"), Tr.T("Speichern unter"), Tr.T("Seiten extrahieren"),
                    Tr.T("Exportieren"), Tr.T("Exportieren")], actions);
                Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0)));

                // A way that skipped the gate: the outputs refuse the mark instead of redacting along the way (redacting happens in
                // one place only, „Schwärzungen anwenden“)
                var refused = Tr.T("Die markierten Schwärzungen sind noch nicht angewendet — zuerst „Schwärzungen anwenden“.");
                var folder = Path.GetDirectoryName(pdf)!;
                var raster = new PdfKnead.Rendering.RasterSettings(72);
                Assert.Equal(refused, (await Assert.ThrowsAsync<PdfKnead.Core.Pdf.PdfException>(() => doc.ExportImagesAsync(folder, new PdfKnead.Core.Imaging.PageImageSettings(Dpi: 72)))).Message);
                var target = Path.Combine(folder, "ohne-schleuse.pdf");
                Assert.Equal(refused, (await Assert.ThrowsAsync<PdfKnead.Core.Pdf.PdfException>(() => doc.ExportImagePdfAsync(target, raster))).Message);
                Assert.False(File.Exists(target));
                var split = new SplitViewModel(doc);
                split.SplitEvery();
                Assert.False(await split.SaveToAsync(folder));
                Assert.Contains(refused, split.Error);
                var compress = new CompressViewModel(doc);
                WaitUntil(window, () => compress.HasProblem);
                Assert.Contains(refused, compress.Problem);
                compress.Dispose();
                await compress.Released;
                Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0)));

                // copy: without the marked text, with a hint
                string? copied = null;
                vm.SetClipboardText = value =>
                {
                    copied = value;
                    return Task.CompletedTask;
                };
                doc.Select(0, Assert.Single(text.Find(line)));
                await vm.CopyTextCommand.ExecuteAsync(null);
                Assert.NotNull(copied);
                Assert.DoesNotContain("Musterstrasse", copied);
                Assert.Contains("Max Mustermann,", copied);
                Assert.Contains("12345 Musterstadt", copied);

                // redact now: afterwards printing opens without asking
                vm.ConfirmRedaction = question =>
                {
                    actions.Add(question.Action);
                    return Task.FromResult(new RedactionReply(RedactionAnswer.Apply));
                };
                await vm.PrintCommand.ExecuteAsync(null);
                Assert.Equal(["Drucken"], opened);
                Assert.Empty(doc.ElementsOn(0));
                await vm.CompressCommand.ExecuteAsync(null);
                Assert.Equal(8, actions.Count);
                Assert.Equal(["Drucken", "Komprimieren"], opened);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>Builds a page with three lines in 6 pt at 6.5 pt line spacing; the middle one contains the word to redact.</summary>
    private static byte[] SmallTextPage()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 6 Tf 72 700 Td 6.5 TL (Zeile oben) Tj T* (vorher Geheim nachher) Tj T* (Zeile unten) Tj ET"u8.ToArray()));
        return pdf.Build();
    }

    /// <summary>Secure as image: mark it, confirm when saving, and afterwards no text is left (not even in the session).</summary>
    [Fact]
    public void Raster_MarkConfirmSave_NoTextAfterwards()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 2).Build(), "als-bild.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.PickRaster = () =>
                {
                    var dialog = new RasterWindow();
                    dialog.Show();
                    UiSession.Capture(dialog);
                    Shoot(dialog, "47-als-bild-sichern");
                    var settings = dialog.Settings();
                    dialog.Close();
                    Assert.Equal(300, settings.Dpi);   // default
                    return Task.FromResult<PdfKnead.Rendering.RasterSettings?>(settings with { Dpi = 72 });
                };
                await vm.ToggleRasterCommand.ExecuteAsync(null);
                Assert.True(vm.RasterOnSave);
                Assert.True(doc.IsModified);
                await vm.ToggleRasterCommand.ExecuteAsync(null);   // take it back
                Assert.False(vm.RasterOnSave);
                Assert.False(doc.IsModified);
                await vm.ToggleRasterCommand.ExecuteAsync(null);

                var titles = new List<string>();
                vm.ConfirmFixing = (title, _, _) =>
                {
                    titles.Add(title);
                    return Task.FromResult(FixingReply.Save);
                };
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Equal([Tr.T("Flachlegen als Bild?")], titles);
                Assert.False(vm.RasterOnSave);
                Assert.False(doc.IsModified);
                // the session shows the saved image file: no text left, not even from old display lists
                Assert.Empty(Core.Pdf.Content.PageInterpreter.Build(doc.Document, doc.Document.Pages[0]).Text);
                Assert.True((await doc.GetTextAsync(0)).IsEmpty);
                using var saved = PdfDocument.Open(pdf);
                Assert.Equal(2, saved.PageCount);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>"Sanitize document" (UI label): the dialog shows the findings, and the selection is removed from the document at once (Ctrl+Z brings it back).</summary>
    [Fact]
    public void Cleanup_ShowFindings_RemoveInDocument()
    {
        var pdf = TestFiles.Write(CleanupTests.Laden(), "bereinigen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                await vm.CleanDocumentCommand.ExecuteAsync(null);
                Assert.True(doc.IsCleanupOpen);
                var panel = window.GetVisualDescendants().OfType<CleanupPanel>().Single();
                UiSession.Capture(window);
                Shoot(window, "46-dokument-bereinigen");
                // only the items found, each enabled
                var boxes = panel.GetVisualDescendants().OfType<CheckBox>().Where(b => b.Name != "KeepTitleBox").ToList();
                Assert.Equal(doc.CleanupRows.Count, boxes.Count);
                Assert.All(boxes, b => Assert.True(b.IsEnabled));
                await vm.ApplyCleanupCommand.ExecuteAsync(null);
                Assert.False(doc.IsCleanupOpen);
                // preselected: the recommendation (not comments, bookmarks, structure); removed from the document at once, not yet from the file
                List<Core.Pdf.Writing.CleanupItems> Left(PdfDocument d) => [.. Core.Pdf.Writing.DocumentCleanup.Inspect(d, PageItem.From(d)).Select(f => f.Item)];
                Assert.DoesNotContain(Core.Pdf.Writing.CleanupItems.Metadata, Left(doc.Document));
                Assert.DoesNotContain(Core.Pdf.Writing.CleanupItems.Scripts, Left(doc.Document));
                Assert.Contains(Core.Pdf.Writing.CleanupItems.Comments, Left(doc.Document));
                Assert.True(doc.IsModified);
                using (var file = PdfDocument.Open(File.ReadAllBytes(pdf))) Assert.Contains(Core.Pdf.Writing.CleanupItems.Metadata, Left(file));
                vm.UndoCommand.Execute(null);
                Assert.Contains(Core.Pdf.Writing.CleanupItems.Metadata, Left(doc.Document));
                vm.RedoCommand.Execute(null);

                await vm.SaveCommand.ExecuteAsync(null);
                Assert.False(doc.IsModified);
                using var saved = PdfDocument.Open(pdf);
                var left = Core.Pdf.Writing.DocumentCleanup.Inspect(saved, PageItem.From(saved)).Select(f => f.Item).ToList();
                Assert.DoesNotContain(Core.Pdf.Writing.CleanupItems.Metadata, left);
                Assert.Contains(Core.Pdf.Writing.CleanupItems.Comments, left);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void ProtectionWindow_ValidatesAndBuildsResult()
    {
        UiSession.Run(() =>
        {
            var window = new ProtectionWindow(false, Core.Pdf.Security.PdfPermissions.All, "1.7");
            window.Show();
            try
            {
                UiSession.Capture(window);
                TextBox Box(string name) => window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == name);
                CheckBox Check(string name) => window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == name);
                Assert.Null(window.Result());   // nothing chosen, nothing encrypted: no change

                Check("OpenBox").IsChecked = true;
                Box("OpenPassword").Text = "geheim";
                Box("OpenRepeat").Text = "geheimx";
                Assert.Equal(Tr.T("Die beiden Passwörter zum Öffnen stimmen nicht überein."), window.Validate());
                Box("OpenRepeat").Text = "geheim";
                Check("RightsBox").IsChecked = true;
                Box("OwnerPassword").Text = "geheim";
                Box("OwnerRepeat").Text = "geheim";
                Assert.NotNull(window.Validate());   // the same password for both: rejected
                Box("OwnerPassword").Text = Box("OwnerRepeat").Text = "Besitzer-1";
                Check("CopyBox").IsChecked = false;
                Check("ModifyBox").IsChecked = false;
                Assert.Null(window.Validate());
                // AES-256 (1.7): a control character in the password is refused with its reason (No. 558)
                Box("OpenPassword").Text = Box("OpenRepeat").Text = "geh\u0007eim";
                Assert.Equal(Tr.T("Das Passwort enthält ein Steuerzeichen."), window.Validate());
                Box("OpenPassword").Text = Box("OpenRepeat").Text = "geheim";
                Assert.Null(window.Validate());
                var protection = window.Result()!.Protection!;
                Assert.Equal(("geheim", "Besitzer-1"), (protection.UserPassword, protection.OwnerPassword));
                Assert.False(protection.Permissions.HasFlag(Core.Pdf.Security.PdfPermissions.Copy));
                Assert.True(protection.Permissions.HasFlag(Core.Pdf.Security.PdfPermissions.PrintHighQuality));
                UiSession.Capture(window);
                Shoot(window, "45-passwort-und-rechte");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// RC4 and AES-128 cut a password to 32 characters (ISO 32000-2, 7.6.4.3.2, algorithm 2, step a; docs/FEHLERGEDAECHTNIS.md, No. 1112): the
    /// window says so as soon as a password in use is longer — not for one of exactly 32 characters, not for a box that is not ticked, not with
    /// AES-256, and not for a password outside PDFDocEncoding (that copy is written with AES-256 anyway).
    /// </summary>
    [Theory]
    [InlineData("1.4")]
    [InlineData("1.6")]
    public void ProtectionWindow_PasswordOver32Characters_HintWithRc4AndAes128(string version)
    {
        UiSession.Run(() =>
        {
            var window = new ProtectionWindow(false, Core.Pdf.Security.PdfPermissions.All, version);
            window.Show();
            try
            {
                UiSession.Capture(window);
                TextBox Box(string name) => window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == name);
                CheckBox Check(string name) => window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == name);
                var hint = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "LengthHint");
                bool Shown()
                {
                    UiSession.Capture(window);
                    return hint.IsEffectivelyVisible;
                }
                Assert.False(Shown());

                var exactly32 = new string('a', 32);
                Check("OpenBox").IsChecked = true;
                Box("OpenPassword").Text = exactly32;
                Assert.False(Shown());
                Box("OpenPassword").Text = exactly32 + "b";
                Assert.True(Shown());
                Shoot(window, "45b-passwort-ueber-32-zeichen-" + version);
                Check("OpenBox").IsChecked = false;   // the password is not used
                Assert.False(Shown());

                Check("RightsBox").IsChecked = true;
                Box("OwnerPassword").Text = "Besitzer-" + exactly32;
                Assert.True(Shown());
                // a character outside PDFDocEncoding: the writer takes AES-256, nothing is cut
                Box("OwnerPassword").Text = "Пароль-" + exactly32;
                Assert.False(Shown());
                Box("OwnerPassword").Text = "kurz";
                Assert.False(Shown());
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>With AES-256 (PDF 1.7 and 2.0) the whole password counts: no hint, however long it is (No. 1112).</summary>
    [Theory]
    [InlineData("1.7")]
    [InlineData("2.0")]
    public void ProtectionWindow_PasswordOver32Characters_NoHintWithAes256(string version)
    {
        UiSession.Run(() =>
        {
            var window = new ProtectionWindow(false, Core.Pdf.Security.PdfPermissions.All, version);
            window.Show();
            try
            {
                UiSession.Capture(window);
                window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "OpenBox").IsChecked = true;
                window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "OpenPassword").Text = new string('a', 60);
                UiSession.Capture(window);
                Assert.False(window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "LengthHint").IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FixSignaturesWindow_ShowsNotice()
    {
        UiSession.Run(() =>
        {
            var notice = Tr.T("Die Unterschrift wird beim Speichern fest in die Seite geschrieben. Danach lässt sie sich nicht mehr verschieben, ändern oder löschen.");
            var window = new FixSignaturesWindow(notice, offerImage: true);
            window.Show();
            try
            {
                UiSession.Capture(window);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == notice);
                Assert.True(window.GetVisualDescendants().OfType<CheckBox>().Single().IsEffectivelyVisible);
                Shoot(window, "44-unterschrift-festschreiben");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// When a signature is fixed, the pages can optionally be secured as images at the same time; the file then contains no text,
    /// and the choice applies only to this save.
    /// </summary>
    [Fact]
    public void FixingSignature_AlsoAsImage_SavesPagesAsImages()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "als bild festschreiben.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                doc.AddElement(0, new SignatureElement { Rect = new PdfRectangle(100, 600, 300, 660), Name = "Max Mustermann", FontFile = "PatrickHand-Regular.ttf" },
                    "Unterschrift");
                var target = Path.Combine(Path.GetDirectoryName(pdf)!, "als bild festgeschrieben.pdf");
                File.Delete(target);
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                var offered = new List<bool>();
                vm.ConfirmFixing = (_, _, offerImage) =>
                {
                    offered.Add(offerImage);
                    return Task.FromResult(FixingReply.SaveAsImage);
                };
                await vm.SaveAsCommand.ExecuteAsync(null);
                Assert.Equal([true], offered);
                Assert.Null(doc.RasterOnSave);
                using var saved = PdfDocument.Open(target);
                Assert.Equal("", PageText(saved, 0).Trim());   // no text left, not even „Seite 1“
                Assert.Contains(PageInterpreter.Build(saved, saved.Pages[0]).Items.ToArray(), i => i.Kind == Core.Pdf.Graphics.DisplayItemKind.Image);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>Ctrl+S or closing while text input is open: the typed text must not be lost.</summary>
    [Fact]
    public void OpenTextInput_IsKept_OnSaveAndClose()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "eingabe-offen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitWidthCommand.Execute(null);
                UiSession.Capture(window);
                var viewer = window.GetVisualDescendants().OfType<DocumentViewer>().Single();
                var view = viewer.View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;

                // save with the input box open (focus stays in it)
                vm.SetToolCommand.Execute(ViewTool.InsertText);
                Drag(window, At(100, 700), At(360, 650));
                viewer.Editor.Focus();
                viewer.Editor.Text = "Max Mustermann";
                var target = Path.Combine(Path.GetDirectoryName(pdf)!, "eingabe-offen gespeichert.pdf");
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.SaveAsCommand.ExecuteAsync(null);
                using (var saved = PdfDocument.Open(target))
                    Assert.Contains("Max Mustermann", PageText(saved, 0));

                // close with the input box open: asks instead of discarding the text
                vm.SetToolCommand.Execute(ViewTool.InsertText);
                Drag(window, At(100, 500), At(360, 450));
                viewer.Editor.Focus();
                viewer.Editor.Text = "Musterstadt";
                var asked = false;
                vm.AskSaveChanges = _ =>
                {
                    asked = true;
                    return Task.FromResult(SaveChoice.Cancel);
                };
                Assert.False(await vm.ConfirmDiscardAsync(doc));
                Assert.True(asked);
                Assert.Contains(doc.ElementsOn(0), e => e is TextElement { Text: "Musterstadt" });
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The format of inserted text lives in the „Text“ panel on the right (not in the ribbon): visible while the „Text“ tool is on
    /// (for the next text) and while a text is selected, not in form design; changes in it apply to the selected text. The
    /// ribbon has a „Signature“ tab with signing and signature fields, and the „Form“ tab has „Löschen“.
    /// </summary>
    [Fact]
    public void TextPanel_ForToolAndSelection_RibbonTabs()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 1).Build(), "textkarte.pdf");
        var defaults = ElementDefaults.Text;   // a format change also changes the default for new text: restore it for other tests
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Insert;
                vm.FitWidthCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var host = window.FindControl<Border>("TextPropertiesHost")!;
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;

                Assert.False(vm.ShowTextProperties);
                Assert.False(host.IsVisible);
                // the ribbon no longer carries the font settings
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<ComboBox>(),
                    c => c.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(c) == Tr.T("Schriftart") && c.FindAncestorOfType<TextPropertiesPanel>() is null);

                // Text tool on: the panel shows the defaults for the next text
                vm.SetToolCommand.Execute(ViewTool.InsertText);
                UiSession.Capture(window);
                Assert.True(vm.ShowTextProperties);
                Assert.True(host.IsVisible);
                Assert.False(vm.HasTextElementSelected);

                // a placed text stays selected and keeps the panel; a change in it applies to the text
                Drag(window, At(100, 700), At(360, 650));
                window.GetVisualDescendants().OfType<DocumentViewer>().Single().Editor.Text = "Max Mustermann";
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                UiSession.Capture(window);
                Assert.True(vm.HasTextElementSelected);
                Assert.True(vm.ShowTextProperties);
                vm.TextMultiline = false;
                Assert.False(((TextElement)doc.SelectedElement!.Value.Element).Multiline);
                Shoot(window, "35-text-karte");

                // form design has its own panel
                vm.ToggleFormDesignCommand.Execute(null);
                UiSession.Capture(window);
                Assert.False(vm.ShowTextProperties);
                Assert.False(host.IsVisible);
                vm.ToggleFormDesignCommand.Execute(null);

                // Signature tab: signing and signature fields; Form tab: delete
                bool Shown(string label) => window.GetVisualDescendants().OfType<Control>()
                    .Any(c => c.IsEffectivelyVisible && c is RibbonButton or RibbonToggleButton && Label(c) == Tr.T(label));
                vm.RibbonTab = RibbonTab.Signature;
                UiSession.Capture(window);
                Assert.True(Shown("Unterschreiben"));
                Assert.True(Shown("Unterschriftsfeld"));
                Assert.True(Shown("Schriften verwalten"));
                Shoot(window, "36-reiter-unterschrift");
                vm.RibbonTab = RibbonTab.Form;
                UiSession.Capture(window);
                Assert.True(Shown("Löschen"));
                Assert.False(Shown("Unterschriftsfeld"));
                Shoot(window, "37-reiter-formular");
            }
            finally
            {
                window.Close();
                ElementDefaults.Text = defaults;
            }
        });
    }

    private static string? Label(Control control) => control switch
    {
        RibbonButton b => b.Label,
        RibbonToggleButton t => t.Label,
        _ => null,
    };

    /// <summary>
    /// A stamp its author locked (/F 132: print and locked) and a field whose contents are locked (/F 516): moving or deleting the stamp asks
    /// „Diese Anmerkung ist vom Ersteller gesperrt. Trotzdem ändern?“ — with „Abbrechen“ nothing changes, with „Trotzdem ändern“ the change
    /// follows; the field may be moved, its value asks (ISO 32000-2, 12.5.3, Table 167; docs/ABLAUFPLAN.md, 4.4 a;
    /// docs/FEHLERGEDAECHTNIS.md, No. 541). Before both changed without a word.
    /// </summary>
    [Fact]
    public void ForeignLockedAnnotation_MoveAndDeleteAsk_ContentLockedFieldValueAsks()
    {
        var pdf = TestFiles.Write(ForeignAnnotationTests.AnnotatedPage(stampFlags: 132, placeFlags: 516), "gesperrte anmerkungen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            await vm.OpenAsync([pdf]);
            var doc = vm.SelectedDocument!;
            var questions = new List<string>();
            var answer = false;
            doc.ConfirmLockedChange = q =>
            {
                questions.Add(q);
                return Task.FromResult(answer);
            };
            ForeignAnnotation Stamp() => doc.ElementsOn(0).OfType<ForeignAnnotation>().Single(f => f.Kind == "Stamp" && f.IsLocked);
            ForeignAnnotation Place() => doc.ElementsOn(0).OfType<ForeignAnnotation>().Single(f => f.IsContentLocked);
            async Task Settled() { for (var i = 0; i < 20; i++) await Task.Yield(); }

            // moving the locked stamp: asked, „Abbrechen“ — nothing changes
            var stamp = Stamp();
            var moved = stamp with { Rect = stamp.Rect with { Left = stamp.Rect.Left - 100, Right = stamp.Rect.Right - 100 } };
            Assert.False(doc.ReplaceElement(new ElementRef(0, stamp), moved, Tr.T("Element verschieben")));
            await Settled();
            Assert.Equal([Tr.T("Diese Anmerkung ist vom Ersteller gesperrt. Trotzdem ändern?")], questions);
            Assert.False(Stamp().IsMoved);
            Assert.False(doc.IsModified);
            // deleting it: asked as well, „Abbrechen“ — still there
            doc.SelectedElement = new ElementRef(0, Stamp());
            Assert.False(doc.DeleteSelectedElement());
            await Settled();
            Assert.Equal(2, questions.Count);
            Assert.Single(doc.ElementsOn(0).OfType<ForeignAnnotation>(), f => f.Kind == "Stamp" && f.IsLocked);
            // „Trotzdem ändern“: the move follows, and further changes of this annotation are not asked again
            answer = true;
            stamp = Stamp();
            doc.ReplaceElement(new ElementRef(0, stamp), stamp with { Rect = moved.Rect }, Tr.T("Element verschieben"));
            await Settled();
            Assert.True(Stamp().IsMoved);
            Assert.Equal(3, questions.Count);
            doc.SelectedElement = new ElementRef(0, Stamp());
            Assert.True(doc.DeleteSelectedElement());
            Assert.Equal(3, questions.Count);

            // contents locked: moving is free, a new value asks
            answer = false;
            var place = Place();
            Assert.True(doc.ReplaceElement(new ElementRef(0, place), place with { Rect = place.Rect with { Bottom = place.Rect.Bottom + 5, Top = place.Rect.Top + 5 } },
                Tr.T("Element verschieben")));
            Assert.Equal(3, questions.Count);
            place = Place();
            Assert.False(doc.ReplaceElement(new ElementRef(0, place), place with { Value = "Neustadt" }, Tr.T("Ausfüllen")));
            await Settled();
            Assert.Equal(4, questions.Count);
            Assert.Null(Place().Value);
            vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
        });
    }
}
