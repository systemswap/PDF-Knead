using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The whole redaction as a user does it, with the mouse only: ribbon tab and buttons, marking text and areas on the page, a handle,
/// undo, hidden content with the flyout, the real questions when applying and saving — then the saved file is checked: everything
/// marked is gone from the file (text, image samples, text under an image and a filled area), everything else is still there.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class RedactionMouseUiTests
{
    // Page 1: three lines of visible text; a stack of text, a grey area over it and an image over both (only the image is seen); white
    // text on the paper; invisible text (OCR); a visible line with the name once more; a line that stays
    private const string Content =
        "BT /F1 12 Tf 72 700 Td 20 TL (Name: Max Mustermann) Tj T* (IBAN DE89 3704 0044 0532 0130 00) Tj T* (Zweite Zeile bleibt) Tj ET " +
        "BT /F1 14 Tf 320 440 Td (UNTENTEXT) Tj ET " +
        "0.6 g 300 420 200 60 re f 0 g " +
        "q 200 0 0 100 300 400 cm /Im0 Do Q " +
        "1 1 1 rg BT /F1 12 Tf 100 300 Td (WEISSGEHEIM) Tj ET 0 g " +
        "BT /F1 12 Tf 72 150 Td (Kontakt: Max Mustermann) Tj ET " +
        "BT /F1 12 Tf 300 150 Td (Fusszeile bleibt) Tj ET " +
        "BT 3 Tr /F1 12 Tf 100 250 Td (OCRVERSTECK) Tj ET";

    private static byte[] Build()
    {
        var pdf = TestPdf.Simple(1);
        var pixels = new byte[20 * 10 * 3];
        for (var i = 0; i < 200; i++) (pixels[i * 3], pixels[i * 3 + 1], pixels[i * 3 + 2]) = ((byte)(i % 20 * 12), 200, 40);
        var image = pdf.Add(TestPdf.FlateStream(pixels, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(20)), ("Height", PdfInteger.Of(10)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)))));
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes(Content)));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image)))),
            ("Font", TestPdf.Dict(("F1", TestPdf.Ref(3))))));
        return pdf.Build();
    }

    private static void WaitUntil(Window window, Func<bool> condition, int seconds = 30)
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

    /// <summary>Presses, moves in small steps as a hand does, and releases.</summary>
    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        for (var k = 1; k <= 8; k++)
            window.MouseMove(new Point(from.X + (to.X - from.X) * k / 8, from.Y + (to.Y - from.Y) * k / 8));
        window.MouseUp(to, MouseButton.Left);
        UiSession.Capture(window);
    }

    private static void Click(Window window, Point at)
    {
        // a pause first: two clicks in a row would otherwise count as a double-click
        Thread.Sleep(600);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        UiSession.Capture(window);
    }

    private static Point Centre(Visual target, Window window) =>
        target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;

    private static string Text(PdfDocument doc) => string.Concat(PageInterpreter.Build(doc, doc.Pages[0]).Text.Select(t => t.Text));

    [Fact]
    public void WholeRedaction_WithTheMouse()
    {
        var pdf = TestFiles.Write(Build(), "maus-schwaerzen.pdf");
        var target = Path.Combine(Path.GetDirectoryName(pdf)!, "maus-schwaerzen-ergebnis.pdf");
        File.Delete(target);
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                // only the system's file dialog is replaced (it lies outside the program)
                vm.PickSavePath = (_, _, _) => Task.FromResult<string?>(target);
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                UiSession.Capture(window);
                T Ribbon<T>(string label) where T : Control =>
                    window.GetVisualDescendants().OfType<T>().Single(b => b.IsEffectivelyVisible &&
                        (b is RibbonButton rb ? rb.Label : b is RibbonToggleButton tb ? tb.Label : null) == Tr.T(label));
                void Press(Control control) => Click(window, Centre(control, window));

                // „Ganze Seite“ (Start), then the tab „Sicher“
                Press(Ribbon<RibbonButton>("Ganze Seite"));
                Press(window.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("ribbonTab") && Equals(b.Content, Tr.T("Sicher"))));
                Assert.Equal(RibbonTab.Secure, vm.RibbonTab);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                var text = await doc.GetTextAsync(0);
                Core.Pdf.Graphics.RectD Word(string word, int index = 0) =>
                    text.Quads(text.Find(word).ElementAt(index)).Select(q => q.Bounds).Aggregate((a, b) => a.Union(b));

                // 1. „Text schwärzen“: drag over the name in the first line
                Press(Ribbon<RibbonToggleButton>("Text schwärzen"));
                Assert.Equal(ViewTool.RedactText, vm.Tool);
                var name = Word("Max Mustermann");
                var nameMiddle = (name.Top + name.Bottom) / 2;
                Thread.Sleep(600);
                Drag(window, At(name.Left + 0.5, nameMiddle), At(name.Right - 0.5, nameMiddle));
                Assert.Single(doc.ElementsOn(0).OfType<RedactionElement>());

                // 2. „Bereich schwärzen“: the stack of image, grey area and text — drawn a little too narrow, then widened at the right handle
                Press(Ribbon<RibbonToggleButton>("Bereich schwärzen"));
                Assert.Equal(ViewTool.InsertRedaction, vm.Tool);
                Thread.Sleep(600);
                Drag(window, At(300, 500), At(490, 400));
                var area = doc.ElementsOn(0).OfType<RedactionElement>().Single(r => r.Rect.Left > 290);
                Assert.Equal(490, area.Rect.Right, 0);
                Thread.Sleep(600);
                Drag(window, At(area.Rect.Right, 450), At(area.Rect.Right + 10, 450));
                area = doc.ElementsOn(0).OfType<RedactionElement>().Single(r => r.Rect.Left > 290);
                Assert.Equal(500, area.Rect.Right, 0);

                // ... over text the same tool marks the text: the IBAN without the word „IBAN“
                var iban = Word("DE89 3704 0044 0532 0130 00");
                var ibanMiddle = (iban.Top + iban.Bottom) / 2;
                Thread.Sleep(600);
                Drag(window, At(iban.Left + 0.5, ibanMiddle), At(iban.Right - 0.5, ibanMiddle));
                Assert.Equal(3, doc.ElementsOn(0).OfType<RedactionElement>().Count());
                // the mark covers the whole number (a failure here is the drag, not the applying)
                var ibanMark = doc.ElementsOn(0).OfType<RedactionElement>().Single(r => r.Rect.Bottom <= ibanMiddle && r.Rect.Top >= ibanMiddle);
                Assert.True(ibanMark.Rect.Left <= iban.Left + 1 && ibanMark.Rect.Right >= iban.Right - 1,
                    $"Balken {ibanMark.Rect.Left:0.0}–{ibanMark.Rect.Right:0.0}, IBAN {iban.Left:0.0}–{iban.Right:0.0}");

                // 3. a slip in empty space, taken back with the button „Rückgängig“ in the title bar
                Thread.Sleep(600);
                Drag(window, At(400, 300), At(520, 250));
                Assert.Equal(4, doc.ElementsOn(0).OfType<RedactionElement>().Count());
                Press(window.GetVisualDescendants().OfType<RibbonButton>().Single(b => ReferenceEquals(b.Command, vm.UndoCommand)));
                Assert.Equal(3, doc.ElementsOn(0).OfType<RedactionElement>().Count());
                Shoot(window, "80-maus-markiert");

                // 4. „Verborgenes schwärzen“: the white text by a click on its frame, the invisible text by a double-click on its row and the
                // box in the flyout; a click elsewhere closes the flyout
                Press(Ribbon<RibbonToggleButton>("Verborgenes schwärzen"));
                WaitUntil(window, () => vm.IsHiddenTool && !doc.HiddenSearching && doc.HiddenRows.Count >= 2);
                var white = doc.HiddenRows.Single(r => r.Spot.Text.Replace(" ", "") == "WEISSGEHEIM");
                var ocr = doc.HiddenRows.Single(r => r.Spot.Text.Replace(" ", "") == "OCRVERSTECK");
                UiSession.Capture(window);
                var frame = view.HiddenFrames.Single(f => ReferenceEquals(f.Spot, white.Spot)).Frame;
                Click(window, view.TranslatePoint(frame.Center, window)!.Value);
                Assert.True(white.IsMarked);
                var panel = window.GetVisualDescendants().OfType<HiddenContentPanel>().Single();
                var row = panel.GetVisualDescendants().OfType<ListBoxItem>().Single(i => ReferenceEquals(i.DataContext, ocr))
                    .GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == ocr.Preview);
                Thread.Sleep(600);
                var rowAt = Centre(row, window);
                for (var k = 0; k < 2; k++)
                {
                    window.MouseDown(rowAt, MouseButton.Left);
                    window.MouseUp(rowAt, MouseButton.Left);
                }
                UiSession.Capture(window);
                Assert.True(doc.IsHiddenTextOpen);
                var flyout = window.GetVisualDescendants().OfType<HiddenTextFlyout>().Single();
                Assert.Equal("OCRVERSTECK", flyout.ShownText!.Replace(" ", ""));
                Press(flyout.GetVisualDescendants().OfType<CheckBox>().Single());
                Assert.True(ocr.IsMarked);
                Shoot(window, "81-maus-verborgen");
                Click(window, view.TranslatePoint(new Point(20, view.Bounds.Height / 2), window)!.Value);
                Assert.False(doc.IsHiddenTextOpen);
                Press(Ribbon<RibbonToggleButton>("Verborgenes schwärzen"));
                Assert.False(vm.IsHiddenTool);
                Assert.Equal(5, doc.ElementsOn(0).OfType<RedactionElement>().Count());

                // 5. „Schwärzungen anwenden“: the question names the name in the contact line, not marked yet; „Jetzt schwärzen“ with
                // „Diese Stellen auch schwärzen“ (ticked)
                var marks = doc.ElementsOn(0).OfType<RedactionElement>().ToList();
                Press(Ribbon<RibbonButton>("Schwärzungen anwenden"));
                WaitUntil(window, () => window.OwnedWindows.OfType<RedactionWindow>().Any());
                var question = window.OwnedWindows.OfType<RedactionWindow>().Single();
                UiSession.Capture(question);
                Shoot(question, "82-maus-rueckfrage");
                var unredacted = question.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "AlsoUnredacted");
                Assert.True(unredacted.IsEffectivelyVisible);
                Assert.True(unredacted.IsChecked);
                Assert.Contains(question.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && (t.Text ?? t.Inlines?.Text)?.Contains("Max Mustermann") == true);
                Thread.Sleep(600);
                var apply = question.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Tr.T("Jetzt schwärzen")));
                var applyAt = Centre(apply, question);
                question.MouseDown(applyAt, MouseButton.Left);
                question.MouseUp(applyAt, MouseButton.Left);
                WaitUntil(window, () => doc.HasAppliedRedactions && !doc.IsWorking);
                Assert.Empty(window.OwnedWindows.OfType<RedactionWindow>());
                Shoot(window, "83-maus-angewendet");

                // 6. „Speichern“ in the title bar: the question, „Als neue Datei speichern …“
                Press(window.GetVisualDescendants().OfType<RibbonButton>().Single(b => ReferenceEquals(b.Command, vm.SaveCommand) && b.Classes.Contains("icon")));
                WaitUntil(window, () => window.OwnedWindows.OfType<RedactedSaveWindow>().Any());
                var save = window.OwnedWindows.OfType<RedactedSaveWindow>().Single();
                UiSession.Capture(save);
                Thread.Sleep(600);
                var asNew = save.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Tr.T("Als neue Datei speichern …")));
                var asNewAt = Centre(asNew, save);
                save.MouseDown(asNewAt, MouseButton.Left);
                save.MouseUp(asNewAt, MouseButton.Left);
                WaitUntil(window, () => File.Exists(target) && !doc.IsModified && !doc.IsWorking);

                // 7. the saved file: everything marked is gone from it, everything else is still there
                var data = File.ReadAllBytes(target);
                // the IBAN by pieces with their spaces: four digits alone also stand in the time of saving (/ModDate „D:20261031183704“ held
                // „3704“, docs/FEHLERGEDAECHTNIS.md, No. 324)
                string[] gone = ["Mustermann", "DE89 3704", "3704 0044", "0532 0130", "0130 00", "UNTENTEXT", "WEISSGEHEIM", "OCRVERSTECK"];
                var leaks = PdfKnead.Cli.LeakSearch.Find(data, gone, null, out _);
                if (leaks.Count > 0)
                {
                    // keep the file and the marks to look at
                    File.Copy(target, target + ".leck.pdf", true);
                    File.WriteAllLines(target + ".leck.txt", [.. leaks, .. marks.Select(r => $"Balken {r.Rect.Left:0.00} {r.Rect.Bottom:0.00} {r.Rect.Right:0.00} {r.Rect.Top:0.00}")]);
                }
                Assert.Empty(leaks);
                using var saved = PdfDocument.Open(data);
                var left = Text(saved);
                foreach (var stays in new[] { "Name:", "IBAN", "Zweite Zeile bleibt", "Kontakt:", "Fusszeile bleibt" })
                    Assert.Contains(stays, left);
                // every group of the IBAN by itself is gone from the page text (no date in it)
                foreach (var group in new[] { "DE89", "3704", "0044", "0532", "0130" })
                    Assert.DoesNotContain(group, left);
                // no image sample of the original is left: the image lay wholly under the bar
                foreach (var image in Enumerable.Range(1, saved.ObjectCount + 8).Select(saved.GetObject).OfType<PdfStream>()
                             .Where(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image"))
                    Assert.DoesNotContain(saved.GetStreamData(image).ToArray(), b => b == 200);
                // what the page shows: black under the stack, white where the slip was taken back
                using var shown = Rendering.SkiaRenderer.RenderPage(PageInterpreter.Build(saved, saved.Pages[0]), saved, saved.Pages[0], 1, new Rendering.ImageCache());
                var top = saved.Pages[0].MediaBox.Top;
                var black = shown.GetPixel(400, (int)(top - 450));
                Assert.True(black.Red < 30 && black.Green < 30 && black.Blue < 30, $"unter dem Balken: {black}");
                var paper = shown.GetPixel(470, (int)(top - 275));
                Assert.True(paper.Red > 225 && paper.Green > 225 && paper.Blue > 225, $"zurückgenommener Fehlgriff: {paper}");
                // the original is untouched
                using var original = PdfDocument.Open(File.ReadAllBytes(pdf));
                Assert.Contains("Max Mustermann", Text(original));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
    /// <summary>
    /// The question „Geschwärzter Text steht noch an weiteren Stellen“ shows the hit with text of its own line only — without invisible
    /// text and without characters under a mark; across lines the piece read out hidden and marked text of other places
    /// (docs/FEHLERGEDAECHTNIS.md, No. 189).
    /// </summary>
    [Fact]
    public void UnredactedElsewhere_ContextFromItsLine_WithoutHiddenOrMarked()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes(
            "BT /F1 12 Tf 72 700 Td (Name: Max Mustermann) Tj ET " +
            "BT /F1 12 Tf 72 400 Td (Kontakt: Max Mustermann wohnt ) Tj 3 Tr (OCRWORT) Tj 0 Tr ( GEHEIMNR hier) Tj ET " +
            "BT 0 Tr /F1 12 Tf 72 380 Td (ANDEREZEILE) Tj ET")));
        var file = TestFiles.Write(pdf.Build(), "rueckfrage-umgebung.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([file]);
                var doc = vm.SelectedDocument!;
                var text = await doc.GetTextAsync(0);
                foreach (var word in new[] { "Name: Max Mustermann", "GEHEIMNR" })
                    doc.AddElement(0, RedactionElement.FromPageAreas(DocumentViewModel.AreasOf(text, Assert.Single(text.Find(word)))), "Test");
                var hit = Assert.Single(await doc.FindUnredactedAsync());
                Assert.Equal("Mustermann", hit.Found);   // words of four letters or more are searched for, „Max“ is not
                Assert.Equal("Kontakt: Max Mustermann wohnt … hier", hit.Context);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
    /// <summary>
    /// A small mark (narrower than the reach of two handles on the screen) can be moved by its middle, and still be reshaped from just
    /// outside its edge — the handles covered all of it, so it could only be reshaped (docs/FEHLERGEDAECHTNIS.md, No. 192).
    /// </summary>
    [Fact]
    public void SmallMark_MovedByItsMiddle_ReshapedFromOutside()
    {
        var pdf = TestFiles.Write(RedactionTests.TextPage("Eine Zeile"), "kleine-markierung.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Secure;
                vm.FitPageCommand.Execute(null);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                vm.SetToolCommand.Execute(ViewTool.InsertRedaction);

                // 6 × 10 pt in empty space: on the screen narrower than two handles' reach
                Drag(window, At(300, 300), At(306, 290));
                var mark = Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.True(view.PagePointToView(0, 306, 0).X - view.PagePointToView(0, 300, 0).X < 16);

                // its middle moves it 40 pt to the right, the same size
                Thread.Sleep(600);
                Drag(window, At(303, 295), At(343, 295));
                var moved = Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(mark.Rect.Left + 40, moved.Rect.Left, 0);
                Assert.Equal(mark.Rect.Width, moved.Rect.Width, 1);
                Assert.Equal(mark.Rect.Height, moved.Rect.Height, 1);

                // just outside its right edge: reshaped (wider)
                Thread.Sleep(600);
                var edge = view.TranslatePoint(view.PagePointToView(0, moved.Rect.Right, 295), window)!.Value + new Point(4, 0);
                Drag(window, edge, edge + new Point(30, 0));
                var wider = Assert.IsType<RedactionElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.True(wider.Rect.Width > moved.Rect.Width + 5, $"{wider.Rect.Width} statt breiter als {moved.Rect.Width}");
                Assert.Equal(moved.Rect.Left, wider.Rect.Left, 1);
                Assert.False(vm.ElementProperties.IsVisible);   // moved and reshaped: no panel came up while dragging

                // clicked without moving it, still in the tool: its panel with the reason (wish of 4.11.2026)
                Thread.Sleep(600);
                var middle = At(wider.Rect.Left + wider.Rect.Width / 2, 295);
                window.MouseDown(middle, MouseButton.Left);
                window.MouseUp(middle, MouseButton.Left);
                UiSession.Capture(window);
                Assert.True(vm.ElementProperties.IsVisible);
                Assert.True(vm.ElementProperties.IsRedaction);
                Assert.Same(wider, Assert.Single(doc.ElementsOn(0)));

                // a new bar beside it lands where it is drawn: the panel goes only after the release, the page does not move under the pointer
                Thread.Sleep(600);
                Drag(window, At(100, 200), At(160, 180));
                UiSession.Capture(window);
                var drawn = doc.ElementsOn(0).OfType<RedactionElement>().Single(m => !ReferenceEquals(m, wider));
                Assert.Equal(100, drawn.Rect.Left, 0);
                Assert.Equal(160, drawn.Rect.Right, 0);
                Assert.False(vm.ElementProperties.IsVisible);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
    /// <summary>
    /// A file that allows no changes: the redaction tools say so and mark nothing; after „Einschränkungen entfernen“ that message is gone
    /// and marking works — it stayed above the notice and still said no change was allowed (docs/FEHLERGEDAECHTNIS.md, No. 195).
    /// </summary>
    [Fact]
    public void FileWithoutChangeRights_MessageGoesWhenLifted()
    {
        var pdf = TestPdf.Simple(1);
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes("BT /F1 24 Tf 72 700 Td (Geschuetzter Text hier) Tj ET")));
        var file = TestFiles.Write(pdf.Build(cipher: Core.Pdf.Security.PdfCipher.Aes128,
            permissions: Core.Pdf.Security.PdfPermissions.Print | Core.Pdf.Security.PdfPermissions.FillForms), "ohne-aenderungsrecht.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([file]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Secure;
                vm.FitPageCommand.Execute(null);
                UiSession.Capture(window);
                Assert.False(doc.CanEditElements);
                Assert.True(vm.LiftRestrictionsCommand.CanExecute(null));
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                var text = await doc.GetTextAsync(0);
                var word = text.Quads(text.Find("Text")[0]).Select(q => q.Bounds).Aggregate((a, b) => a.Union(b));
                var middle = (word.Top + word.Bottom) / 2;
                void DragOverWord()
                {
                    window.MouseMove(At(word.Left + 1, middle));
                    UiSession.Capture(window);
                    Drag(window, At(word.Left + 1, middle), At(word.Right - 1, middle));
                }

                // „Text schwärzen“: the message, no tool, no mark
                vm.RedactTextCommand.Execute(null);
                var rights = Tr.T("Dieses Dokument erlaubt keine Änderungen (Rechte der Datei) — aufheben mit Sicher → Einschränkungen entfernen.");
                Assert.Equal(rights, vm.Message);
                Assert.NotEqual(ViewTool.RedactText, vm.Tool);
                vm.SetToolCommand.Execute(ViewTool.InsertRedaction);
                DragOverWord();
                Assert.Empty(doc.ElementsOn(0));

                // lifted with the confirmation: the message goes, marking works
                vm.PickRestrictions = (_, _, _, _) => Task.FromResult<RestrictionsReply?>(new RestrictionsReply(true));
                await vm.LiftRestrictionsCommand.ExecuteAsync(null);
                Assert.True(doc.CanEditElements);
                Assert.NotEqual(rights, vm.Message);
                vm.RedactTextCommand.Execute(null);
                Assert.Equal(ViewTool.RedactText, vm.Tool);
                Thread.Sleep(600);
                DragOverWord();
                Assert.Single(doc.ElementsOn(0));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
