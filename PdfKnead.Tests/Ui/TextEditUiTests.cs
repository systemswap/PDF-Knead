using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>UI tests for editing existing text: button on the Start tab, mode banner, block frames, locked blocks, Esc. See KONZEPT.md, section 5.14.</summary>
[Collection(nameof(UiCollection))]
public sealed class TextEditUiTests
{
    internal const string Content =
        "BT /F2 16 Tf 72 760 Td (Angebot) Tj ET " +
        "BT /F1 10 Tf 12 TL 72 730 Td (Wir danken Ihnen fuer Ihre Anfrage und bieten) Tj T* (Ihnen die folgenden Leistungen gerne an. Die) Tj T* (Preise gelten bis Ende des Monats.) Tj ET " +
        "BT /F5 24 Tf 400 760 Td (*123*) Tj ET";

    private static void Shoot(Window window, string name)
    {
        var frame = UiSession.Capture(window);
        if (UiSession.ScreenshotFolder() is { } folder)
            frame.Save(Path.Combine(folder, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    [Fact]
    public void Mode_FramesBlocks_BarcodeFontOpensTheCodePanel_EscEnds()
    {
        var pdf = TestFiles.Write(TextBlockTests.PageBytes(Content), "text-bearbeiten.pdf");
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

                // the Edit text button on the Start tab: mode on, banner at the top
                var button = window.GetVisualDescendants().OfType<RibbonToggleButton>().Single(b => b.Label == Tr.T("Bearbeiten") && b.IsEffectivelyVisible);
                var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                Assert.True(vm.IsEditTool);
                Assert.Equal(ViewTool.Edit, doc.Tool);
                Assert.True(vm.ModeBannerVisible);

                // the blocks are detected in the background
                var until = DateTime.UtcNow.AddSeconds(20);
                while (doc.BlocksOn(0) is null && DateTime.UtcNow < until)
                {
                    UiSession.Capture(window);
                    Thread.Sleep(20);
                }
                var blocks = doc.BlocksOn(0)!;
                Assert.Equal(3, blocks.Count);
                window.MouseMove(At(150, 718));
                UiSession.Capture(window);
                Shoot(window, "76-text-bearbeiten");

                // a bar code font goes to the code tool (phase 8.2): its code cannot be read (the font is not embedded), so the panel
                // „Code“ shows the content told from the text — no notice that it is locked
                var messages = new List<string>();
                doc.MessageRequested += messages.Add;
                window.MouseDown(At(420, 768), MouseButton.Left);
                window.MouseUp(At(420, 768), MouseButton.Left);
                until = DateTime.UtcNow.AddSeconds(20);
                while (doc.InspectedBarcode is null && DateTime.UtcNow < until)
                {
                    UiSession.Capture(window);
                    Thread.Sleep(20);
                }
                var code = doc.InspectedBarcode;
                Assert.NotNull(code);
                Assert.NotNull(code.FontBlock);
                Assert.Equal((Core.Barcodes.BarcodeFormat.Code39, "123"), (code.Code.Code.Format, code.Code.Code.Text));
                Assert.Equal(EditTarget.Code, doc.EditTarget);
                Assert.DoesNotContain(messages, m => m.Contains("nicht bearbeitbar", StringComparison.Ordinal));

                // Esc first lets go of the code, then ends the mode
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                if (vm.IsEditTool)
                {
                    Assert.NotEqual(EditTarget.Code, doc.EditTarget);
                    window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                }
                Assert.False(vm.IsEditTool);
                Assert.False(vm.ModeBannerVisible);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Clicking into a paragraph and typing: the old text disappears from the page content and the block takes its place; without a
    /// change the page stays untouched; Ctrl+Z reverts the whole edit; after saving, the file contains the new text and no longer the
    /// old one. See KONZEPT.md, section 5.14.
    /// </summary>
    [Fact]
    public void ClickTypeSave_OldTextGone_UnchangedLeavesPageUntouched()
    {
        var pdf = TestFiles.Write(TextBlockTests.PageBytes(Content), "text-tippen.pdf");
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
                vm.ToggleEditCommand.Execute(null);
                void Wait(Func<bool> condition)
                {
                    var until = DateTime.UtcNow.AddSeconds(20);
                    while (!condition() && DateTime.UtcNow < until)
                    {
                        UiSession.Capture(window);
                        Thread.Sleep(20);
                    }
                    Assert.True(condition(), "Zeitüberschreitung");
                }
                Wait(() => doc.BlocksOn(0) is not null);
                var originalItem = doc.History.Pages[0];

                // click without a change, then Esc: the page is exactly the old one again
                window.MouseDown(At(100, 727), MouseButton.Left);
                window.MouseUp(At(100, 727), MouseButton.Left);
                Wait(() => doc.RichEditor is not null);
                Assert.NotSame(originalItem, doc.History.Pages[0]);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.Null(doc.RichEditor);
                Assert.Same(originalItem, doc.History.Pages[0]);
                Assert.False(doc.History.CanUndo);

                // click after „Wir“ (72 + 1.499 em × 10 pt = 87.0), type: „Wir“ → „Wir alle“
                Wait(() => doc.BlocksOn(0) is not null);
                window.MouseDown(At(87.3, 733), MouseButton.Left);
                window.MouseUp(At(87.3, 733), MouseButton.Left);
                Wait(() => doc.RichEditor is not null);
                var editor = doc.RichEditor!;
                Assert.Equal(3, editor.Caret);
                window.KeyTextInput(" alle");
                UiSession.Capture(window);
                Shoot(window, "77-text-getippt");
                var element = Assert.IsType<Core.Pdf.Editing.RichTextElement>(doc.History.Pages[0].Elements.Single());
                Assert.StartsWith("Wir alle danken Ihnen", element.Content.Text);
                Assert.Equal(16, element.Content.Text.IndexOf("Ihnen", StringComparison.Ordinal) + 0);
                // the paragraph is no longer in the page content (only the heading and the barcode remain)
                var item = doc.History.Pages[0];
                var pageText = Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.BuildText(item.Source, item.Page)).Text;
                Assert.DoesNotContain("danken", pageText);
                Assert.Contains("Angebot", pageText);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.Null(doc.RichEditor);

                // one step: Ctrl+Z reverts everything, Ctrl+Y restores it
                Assert.Equal(Tr.T("Text bearbeiten"), doc.History.UndoDescription);
                vm.UndoCommand.Execute(null);
                Assert.Same(originalItem, doc.History.Pages[0]);
                vm.RedoCommand.Execute(null);
                Assert.Contains("Wir alle", Assert.IsType<Core.Pdf.Editing.RichTextElement>(doc.History.Pages[0].Elements.Single()).Content.Text);

                // save and reopen: the new text is on the page, the old one is no longer in the file
                await vm.SaveCommand.ExecuteAsync(null);
                var bytes = File.ReadAllBytes(pdf);
                using var saved = Core.Pdf.Document.PdfDocument.Open(bytes);
                var text = Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.BuildText(saved, saved.Pages[0])).Text.Replace('\n', ' ');
                Assert.Contains("Wir alle danken Ihnen", text);
                Assert.Contains("Angebot", text);
                Assert.Equal(1, CountOf(text, "danken"));
                Assert.True(saved.Pages[0].Dictionary.GetBoolean(Core.Pdf.Editing.ContentElements.PageKey));
                // after saving, PDF Knead shows a block again (recovered from the content), and the page itself is without it
                var reloaded = Assert.IsType<Core.Pdf.Editing.RichTextElement>(Assert.Single(doc.History.Pages[0].Elements));
                Assert.StartsWith("Wir alle danken Ihnen", reloaded.Content.Text);
                var reloadedItem = doc.History.Pages[0];
                Assert.DoesNotContain("danken", Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.BuildText(reloadedItem.Source, reloadedItem.Page)).Text);
                // saved a second time: no duplicated text
                await vm.SaveCommand.ExecuteAsync(null);
                using var again = Core.Pdf.Document.PdfDocument.Open(File.ReadAllBytes(pdf));
                Assert.Equal(1, CountOf(Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.BuildText(again, again.Pages[0])).Text, "danken"));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Properties panel: visible while editing. Selecting a word and clicking "Bold" makes only that word bold, and it no longer draws
    /// with the (regular) font of the file; typing then continues without a new click. Size, justification and line spacing take
    /// effect, and the note names the font. See KONZEPT.md, section 5.14.
    /// </summary>
    [Fact]
    public void PropertiesPanel_BoldSizeAlignmentSpacing_TypingContinues()
    {
        var pdf = TestFiles.Write(TextBlockTests.PageBytes(Content), "text-formatieren.pdf");
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
                void Wait(Func<bool> condition)
                {
                    var until = DateTime.UtcNow.AddSeconds(20);
                    while (!condition() && DateTime.UtcNow < until)
                    {
                        UiSession.Capture(window);
                        Thread.Sleep(20);
                    }
                    Assert.True(condition(), "Zeitüberschreitung");
                }
                vm.ToggleEditCommand.Execute(null);
                Wait(() => doc.BlocksOn(0) is not null);
                var panel = vm.RichTextProperties;
                Assert.False(panel.IsVisible);
                window.MouseDown(At(100, 727), MouseButton.Left);
                window.MouseUp(At(100, 727), MouseButton.Left);
                Wait(() => doc.RichEditor is not null);
                UiSession.Capture(window);
                Assert.True(panel.IsVisible);
                Assert.True(window.GetVisualDescendants().OfType<RichTextPropertiesPanel>().Single().IsEffectivelyVisible);
                Assert.NotEqual("", panel.FontNote);

                // select „danken“, click "Bold" with the mouse
                var editor = doc.RichEditor!;
                editor.SelectWord(editor.Current.Content.Text.IndexOf("danken", StringComparison.Ordinal));
                var boldButton = window.GetVisualDescendants().OfType<RichTextPropertiesPanel>().Single().GetVisualDescendants().OfType<ToggleButton>()
                    .Single(b => AutomationProperties.GetName(b) == Tr.T("Fett"));
                var center = boldButton.TranslatePoint(new Point(boldButton.Bounds.Width / 2, boldButton.Bounds.Height / 2), window)!.Value;
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                UiSession.Capture(window);
                editor = doc.RichEditor!;
                var content = editor.Current.Content;
                var at = content.Text.IndexOf("danken", StringComparison.Ordinal);
                Assert.All(content.StylesIn(at, 6), s => Assert.True(s.Bold && s.Original is null));
                Assert.False(content.StyleAt(1).Bold);
                Assert.True(panel.Bold);

                // keep typing without a new click: the keyboard focus is back on the page
                editor.SetCaret(at + 6);
                window.KeyTextInput("!");
                Assert.Contains("danken!", doc.RichEditor!.Current.Content.Text);

                // size, justification, line spacing
                doc.RichEditor!.SelectAll();
                panel.SizeText = "12";
                Assert.All(doc.RichEditor!.Current.Content.Spans, s => Assert.Equal(12, s.Style.Size));
                panel.SetAlignmentCommand.Execute(Core.Pdf.Text.BlockAlignment.Justify);
                Assert.Equal(Core.Pdf.Text.BlockAlignment.Justify, doc.RichEditor!.Current.Alignment);
                Assert.True(panel.AlignJustify);
                panel.Spacing = RichTextPropertiesViewModel.Spacings.First(s => s.Value == 2.0);
                Assert.Equal(2.0, doc.RichEditor!.Current.LineGap);
                UiSession.Capture(window);
                Shoot(window, "78-text-formatieren");

                // Input method (IME, dead keys on Linux): while editing, the page is its input client and the candidate window sits at the
                // caret; preedit text appears there and becomes real text when committed
                doc.RichEditor!.SetCaret(0);
                var request = new Avalonia.Input.TextInput.TextInputMethodClientRequestedEventArgs { RoutedEvent = InputElement.TextInputMethodClientRequestedEvent };
                view.RaiseEvent(request);
                var client = Assert.IsAssignableFrom<Avalonia.Input.TextInput.TextInputMethodClient>(request.Client);
                Assert.Same(view, client.TextViewVisual);
                var caretRect = client.CursorRectangle;
                var expected = view.PagePointToView(0, 72, 735);
                Assert.InRange(caretRect.X, expected.X - 3, expected.X + 3);
                Assert.True(caretRect.Height > 5);
                client.SetPreeditText("に");
                UiSession.Capture(window);
                client.SetPreeditText(null);
                window.KeyTextInput("日本");
                Assert.StartsWith("日本Wir", doc.RichEditor!.Current.Content.Text);

                // Pasted from another document: without that document's font, so it can be closed; as long as edited text uses a font of
                // this file, the file counts as a source in use
                using (var foreign = TextBlockTests.Page("BT /F1 10 Tf 72 700 Td (Fremd) Tj ET"))
                {
                    var foreignFont = foreign.Pages[0].Resources!.GetDictionary(Core.Pdf.Objects.PdfName.Get("Font"))!.GetDictionary(Core.Pdf.Objects.PdfName.Get("F1"))!;
                    var style = doc.RichEditor!.Style with { Original = new Core.Pdf.Editing.OriginalFont(foreign, foreignFont, false, false) };
                    doc.RichEditor!.SetCaret(0);
                    doc.RichEditor!.Paste(new Core.Pdf.Editing.RichText("Fremd ", style));
                    Assert.All(doc.RichEditor!.Current.Content.Spans, s => Assert.True(s.Style.Original is null || ReferenceEquals(s.Style.Original.Document, doc.Document)));
                    Assert.DoesNotContain(foreign, doc.History.Sources());
                }
                Assert.Contains(doc.Document, doc.History.Sources());

                // Keys: Tab inserts a tab character and keeps the focus in the document; AltGr (Ctrl+Alt) produces a character, not a
                // shortcut, so Polish AltGr+A „ą“ does not select all and AltGr+0 „}“ does not zoom
                doc.RichEditor!.SetCaret(0);
                window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");
                Assert.StartsWith("\t", doc.RichEditor!.Current.Content.Text);
                Assert.True(view.IsKeyboardFocusWithin);
                window.KeyPress(Key.A, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.A, "ą");
                window.KeyTextInput("ą");
                Assert.False(doc.RichEditor!.HasSelection);
                Assert.StartsWith("\tą", doc.RichEditor!.Current.Content.Text);
                var zoom = (doc.ZoomMode, doc.Zoom);
                window.KeyPress(Key.D0, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.Digit0, "}");
                window.KeyTextInput("}");
                Assert.Equal(zoom, (doc.ZoomMode, doc.Zoom));
                Assert.StartsWith("\tą}", doc.RichEditor!.Current.Content.Text);

                // everything is one step; when editing ends, the panel disappears
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.Null(doc.RichEditor);
                Assert.Equal(Tr.T("Text bearbeiten"), doc.History.UndoDescription);
                vm.UndoCommand.Execute(null);
                Assert.Empty(doc.History.Pages[0].Elements);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>Rotated page (/Rotate 90): clicking a block and typing edits the block, and the text is oriented as on the page.</summary>
    [Fact]
    public void RotatedPage_ClickBlockAndType_Edits()
    {
        var pdf = TestFiles.Write(Pdf.TextBlockTests.PageBytes("BT /F1 14 Tf 72 500 Td (Querformat Beschriftung) Tj ET", rotate: 90), "gedrehte seite.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1200, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitPageCommand.Execute(null);
                doc.ScrollToPage(0);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                vm.ToggleEditCommand.Execute(null);
                var until = DateTime.UtcNow.AddSeconds(20);
                while (doc.BlocksOn(0) is null && DateTime.UtcNow < until)
                {
                    UiSession.Capture(window);
                    Thread.Sleep(20);
                }
                var block = Assert.Single(doc.BlocksOn(0)!, b => b.Locked is null);
                var (x, y) = block.FrameToPage.Transform((block.Left + block.Right) / 2, block.Lines[0].Baseline + 3);
                var at = view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                until = DateTime.UtcNow.AddSeconds(20);
                while (doc.RichEditor is null && DateTime.UtcNow < until)
                {
                    UiSession.Capture(window);
                    Thread.Sleep(20);
                }
                Assert.NotNull(doc.RichEditor);
                doc.RichEditor!.SetCaret(doc.RichEditor.Current.Content.Length);
                window.KeyTextInput(" neu");
                UiSession.Capture(window);
                Assert.EndsWith(" neu", doc.RichEditor!.Current.Content.Text);
                Shoot(window, "79-text-gedrehte-seite");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>Digitally signed file: switching on text editing shows the hint that the signature becomes invalid, once only.</summary>
    [Fact]
    public void SignedDocument_EnteringTextEdit_WarnsOnce()
    {
        var pdf = new TestPdf();
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica"))));
        var stream = pdf.Add(TestPdf.FlateStream("BT /F1 12 Tf 72 700 Td (Vertrag) Tj ET"u8.ToArray()));
        var pages = pdf.Add(new PdfDictionary());
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("Contents", TestPdf.Ref(stream)),
            ("MediaBox", TestPdf.Box(0, 0, 595, 842)), ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(font))))))));
        var signature = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Sig")), ("Filter", TestPdf.N("Adobe.PPKLite")), ("SubFilter", TestPdf.N("adbe.pkcs7.detached")),
            ("ByteRange", new PdfArray { PdfInteger.Of(0), PdfInteger.Of(10), PdfInteger.Of(20), PdfInteger.Of(10) }), ("Contents", new PdfString(new byte[8], isHex: true))));
        var field = pdf.Add(TestPdf.Dict(("FT", TestPdf.N("Sig")), ("T", new PdfString("Unterschrift"u8.ToArray())), ("V", TestPdf.Ref(signature)),
            ("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(0, 0, 0, 0)), ("P", TestPdf.Ref(page))));
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray { TestPdf.Ref(page) }), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pages)),
            ("AcroForm", TestPdf.Dict(("Fields", new PdfArray { TestPdf.Ref(field) }), ("SigFlags", PdfInteger.Of(3))))));
        var file = TestFiles.Write(pdf.Build(), "digital unterschrieben.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.OpenAsync([file]);
                var doc = vm.SelectedDocument!;
                vm.ToggleEditCommand.Execute(null);
                var hint = Tr.T("Das Dokument ist digital unterschrieben — Bearbeiten macht die Unterschrift ungültig.");
                Assert.NotEqual(hint, vm.Message);   // switching the tool on says nothing yet (KONZEPT.md, section 5.22)
                doc.EditTarget = EditTarget.Code;    // the first choice does
                Assert.Equal(hint, vm.Message);
                vm.Message = "";
                vm.ToggleEditCommand.Execute(null);   // off …
                vm.ToggleEditCommand.Execute(null);   // … and on again, chosen again: no second hint
                doc.EditTarget = EditTarget.Code;
                Assert.NotEqual(hint, vm.Message);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// More visible pages than the limit of 24 (zoomed far out): all detected blocks stay cached. Otherwise the first detected pages
    /// drop out and are detected again on the next redraw, in an endless cycle.
    /// </summary>
    [Fact]
    public void ManyVisiblePages_BlocksAllStay()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(pages: 40).Build(), "viele seiten.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1000, Height = 700 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.ToggleEditCommand.Execute(null);
                var all = Enumerable.Range(0, 40).ToList();
                var until = DateTime.UtcNow.AddSeconds(30);
                while (true)
                {
                    doc.ShowingBlocks(all);
                    if (all.All(p => doc.BlocksOn(p) is not null)) break;
                    Assert.True(DateTime.UtcNow < until, "Zeitüberschreitung");
                    // without drawing in between; otherwise the view would report only its one or two visible pages
                    Thread.Sleep(20);
                }
                // right afterwards: all 40 are still there
                Assert.All(all, p => Assert.NotNull(doc.BlocksOn(p)));
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// A block that was only clicked returns to its original state, also when leaving the mode or saving. Otherwise it would stay
    /// converted and be saved that way: the page without its old text and the block in a substitute font.
    /// </summary>
    [Fact]
    public void ClickedBlock_ModeExitOrSave_LeavesPageUntouched()
    {
        var pdf = TestFiles.Write(TextBlockTests.PageBytes(Content), "text-angeklickt.pdf");
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
                void Wait(Func<bool> condition)
                {
                    var until = DateTime.UtcNow.AddSeconds(20);
                    while (!condition() && DateTime.UtcNow < until)
                    {
                        UiSession.Capture(window);
                        Thread.Sleep(20);
                    }
                    Assert.True(condition(), "Zeitüberschreitung");
                }
                var original = doc.History.Pages[0];

                // click, then end the mode
                vm.ToggleEditCommand.Execute(null);
                Wait(() => doc.BlocksOn(0) is not null);
                window.MouseDown(At(100, 727), MouseButton.Left);
                window.MouseUp(At(100, 727), MouseButton.Left);
                Wait(() => doc.RichEditor is not null);
                vm.ToggleEditCommand.Execute(null);
                Assert.Null(doc.RichEditor);
                Assert.Same(original, doc.History.Pages[0]);
                Assert.False(doc.History.CanUndo);

                // click, then save: no converted block in the file, and the old text stays unchanged in the page content
                vm.ToggleEditCommand.Execute(null);
                Wait(() => doc.BlocksOn(0) is not null);
                window.MouseDown(At(100, 727), MouseButton.Left);
                window.MouseUp(At(100, 727), MouseButton.Left);
                Wait(() => doc.RichEditor is not null);
                await vm.SaveCommand.ExecuteAsync(null);
                Assert.Null(doc.RichEditor);
                Assert.Empty(doc.History.Pages[0].Elements);
                using var saved = Core.Pdf.Document.PdfDocument.Open(File.ReadAllBytes(pdf));
                Assert.Null(saved.Pages[0].Dictionary.GetBoolean(Core.Pdf.Editing.ContentElements.PageKey));
                Assert.Empty(Core.Pdf.Editing.PageItem.From(saved)[0].Elements);
                Assert.Contains("danken", Core.Pdf.Text.PageText.Create(Core.Pdf.Content.PageInterpreter.BuildText(saved, saved.Pages[0])).Text);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    private static int CountOf(string text, string word)
    {
        var count = 0;
        for (var at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.Ordinal)) count++;
        return count;
    }
    /// <summary>
    /// Joining blocks with the mouse (KONZEPT.md, section 5.14): Ctrl+click on the heading and on the paragraph, then a plain click on one of
    /// them — the editor opens with both texts as one block; Esc, then Shift + dragging a frame over both — the same. A click without Ctrl on
    /// an unchosen block edits that block alone.
    /// </summary>
    [Fact]
    public void CtrlClickAndShiftFrame_JoinBlocksForOneEdit()
    {
        var pdf = TestFiles.Write(TextBlockTests.PageBytes(Content), "text-zusammenfassen.pdf");
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
                vm.Tool = ViewTool.Edit;
                var until = DateTime.UtcNow.AddSeconds(20);
                while (doc.BlocksOn(0) is null && DateTime.UtcNow < until)
                {
                    UiSession.Capture(window);
                    Thread.Sleep(20);
                }
                Assert.Equal(3, doc.BlocksOn(0)!.Count);
                async Task<string> EditorText()
                {
                    var limit = DateTime.UtcNow.AddSeconds(20);
                    while (doc.RichEditor is null && DateTime.UtcNow < limit)
                    {
                        UiSession.Capture(window);
                        await Task.Delay(20);
                    }
                    Assert.NotNull(doc.RichEditor);
                    return doc.RichEditor!.Current.Content.Text;
                }

                // Ctrl+click heading and paragraph, plain click on the paragraph: one editor with both
                window.MouseDown(At(90, 765), MouseButton.Left, RawInputModifiers.Control);
                window.MouseUp(At(90, 765), MouseButton.Left, RawInputModifiers.Control);
                window.MouseDown(At(150, 718), MouseButton.Left, RawInputModifiers.Control);
                window.MouseUp(At(150, 718), MouseButton.Left, RawInputModifiers.Control);
                Assert.Null(doc.RichEditor);
                UiSession.Capture(window);
                Shoot(window, "76b-bloecke-gewaehlt");
                window.MouseDown(At(150, 718), MouseButton.Left);
                window.MouseUp(At(150, 718), MouseButton.Left);
                var text = await EditorText();
                Assert.StartsWith("Angebot", text);
                Assert.Contains("Wir danken", text);
                Assert.Contains("Ende des Monats", text);
                Shoot(window, "76c-bloecke-zusammengefasst");

                // undo the edit, then Shift + a frame over both blocks
                doc.EndRichEdit();
                doc.Undo();
                Assert.Null(doc.RichEditor);
                window.MouseDown(At(60, 780), MouseButton.Left, RawInputModifiers.Shift);
                window.MouseMove(At(200, 740), RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
                window.MouseMove(At(300, 700), RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
                window.MouseUp(At(300, 700), MouseButton.Left, RawInputModifiers.Shift);
                text = await EditorText();
                Assert.StartsWith("Angebot", text);
                Assert.Contains("Ende des Monats", text);

                // Ctrl+click both again: a notice says how to go on, Enter edits them as one
                doc.EndRichEdit();
                doc.Undo();
                var messages = new List<string>();
                doc.MessageRequested += messages.Add;
                window.MouseDown(At(90, 765), MouseButton.Left, RawInputModifiers.Control);
                window.MouseUp(At(90, 765), MouseButton.Left, RawInputModifiers.Control);
                window.MouseDown(At(150, 718), MouseButton.Left, RawInputModifiers.Control);
                window.MouseUp(At(150, 718), MouseButton.Left, RawInputModifiers.Control);
                Assert.Contains(messages, m => m.Contains("Enter", StringComparison.Ordinal));
                view.Focus();
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                text = await EditorText();
                Assert.StartsWith("Angebot", text);
                Assert.Contains("Ende des Monats", text);

                // a plain click on an unchosen block edits it alone
                doc.EndRichEdit();
                doc.Undo();
                window.MouseDown(At(90, 765), MouseButton.Left);
                window.MouseUp(At(90, 765), MouseButton.Left);
                Assert.Equal("Angebot", (await EditorText()).Trim());
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
