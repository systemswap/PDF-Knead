using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Notizen“ as an area on the right (KONZEPT.md, section 5.5a; wish of 30.10.2026 — the note at a bar could not be opened): all notes of the
/// document with who, when and their answers; a line jumps to its note and frames it; a click on a note opens the area with it.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class NotesUiTests
{
    /// <summary>Page 1: a sticky note with an answer and a popup, a highlight with a comment and one without, free text and a field; page 2 without notes.</summary>
    private static byte[] File() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[3 0 R 4 0 R]/Count 2/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Page/Parent 2 0 R/Annots[10 0 R 11 0 R 12 0 R 13 0 R 14 0 R 15 0 R 17 0 R]>>",
        [4] = "<</Type/Page/Parent 2 0 R>>",
        [10] = "<</Type/Annot/Subtype/Text/Rect[100 700 120 720]/Contents(Bitte pruefen)/T(Erika Mustermann)/Subj(Frage)/M(D:20261030101500+01'00')/Popup 11 0 R/Name/Comment/AP<</N 16 0 R>>>>",
        [11] = "<</Type/Annot/Subtype/Popup/Rect[200 700 300 760]/Parent 10 0 R>>",
        [12] = "<</Type/Annot/Subtype/Text/Rect[100 700 120 720]/IRT 10 0 R/Contents(Erledigt)/T(Max Mustermann)>>",
        [13] = "<</Type/Annot/Subtype/Highlight/Rect[100 600 200 612]/QuadPoints[100 612 200 612 100 600 200 600]/C[1 1 0]/Contents(Wichtig)>>",
        [14] = "<</Type/Annot/Subtype/Highlight/Rect[100 500 200 512]/QuadPoints[100 512 200 512 100 500 200 500]/C[1 1 0]>>",
        [15] = "<</Type/Annot/Subtype/FreeText/Rect[100 400 300 430]/Contents(Auf der Seite)/DA(/Helv 12 Tf 0 g)>>",
        [16] = ("/Type/XObject/Subtype/Form/BBox[0 0 20 20]", "0.5 g 0 0 20 20 re f"),
        [17] = "<</Type/Annot/Subtype/Text/Rect[100 300 120 320]/Contents(Ohne Bild)>>",
    });

    [Fact]
    public void Notes_ListedWithWhoAndAnswers_LineFramesAndClosingClears()
    {
        var pdf = TestFiles.Write(File(), "notizen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                Assert.True(vm.ToggleNotesCommand.CanExecute(null));
                vm.ToggleNotesCommand.Execute(null);
                Assert.True(doc.IsNotesOpen);
                Assert.Equal(3, doc.NoteRows.Count);
                Assert.False(doc.NotesNothing);

                var note = doc.NoteRows[0];
                Assert.Equal("Bitte pruefen", note.Text);
                Assert.Equal("Frage", note.Subject);
                Assert.Contains("Erika Mustermann", note.Header);
                Assert.Contains(Tr.T("Seite {0}", 1), note.Header);
                Assert.Contains("30", note.Header);
                Assert.True(note.HasReplies);
                Assert.Contains("Max Mustermann", note.Replies);
                Assert.Contains("Erledigt", note.Replies);

                Assert.Equal("Wichtig", doc.NoteRows[1].Text);
                Assert.Equal("Ohne Bild", doc.NoteRows[2].Text);

                doc.SelectedNoteRow = note;
                var shown = Assert.NotNull(doc.ShownPlace);
                Assert.Equal(0, shown.Page);
                Assert.Equal(100, Assert.Single(shown.Areas).Left);

                doc.SelectedNoteRow = null;
                Assert.Null(doc.ShownPlace);

                doc.SelectedNoteRow = note;
                vm.CloseNotesCommand.Execute(null);
                Assert.False(doc.IsNotesOpen);
                Assert.Null(doc.ShownPlace);
                Assert.Empty(doc.NoteRows);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>A note chosen on the page opens the area with it; with a tool that has an area of its own it does not.</summary>
    [Fact]
    public void NoteClicked_OpensTheAreaWithIt()
    {
        var pdf = TestFiles.Write(File(), "notizen-klick.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var element = doc.History.Pages[0].Elements.OfType<ForeignAnnotation>().First(f => f.Kind == "Text");

                vm.Tool = ViewTool.Edit;
                doc.SelectedElement = new ElementRef(0, element);
                Assert.False(doc.IsNotesOpen);
                doc.SelectedElement = null;

                vm.Tool = ViewTool.SelectText;
                doc.SelectedElement = new ElementRef(0, element);
                Assert.True(doc.IsNotesOpen);
                Assert.Equal("Bitte pruefen", doc.SelectedNoteRow?.Text);
                Assert.NotNull(doc.ShownPlace);

                // a note without an appearance of its own is no element, a click finds it all the same (the symbol at the top left of /Rect)
                Assert.False(doc.ChooseNoteAt(0, 400, 400));
                Assert.True(doc.ChooseNoteAt(0, 110, 310));
                Assert.Equal("Ohne Bild", doc.SelectedNoteRow?.Text);
                doc.SelectedElement = null;
                doc.SelectedElement = new ElementRef(0, element);

                // deleting the note takes it out of the list, and the frame goes with it
                Assert.True(doc.DeleteSelectedElement());
                Assert.DoesNotContain(doc.NoteRows, r => r.Text == "Bitte pruefen");
                Assert.Null(doc.SelectedNoteRow);
                Assert.Null(doc.ShownPlace);

                // one area at a time
                vm.Tool = ViewTool.Edit;
                Assert.False(doc.IsNotesOpen);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>With the mouse: the button in the ribbon, then a click on a note symbol with „Text markieren“, with and without an appearance of its own.</summary>
    [Fact]
    public void WithTheMouse_ButtonAndClickOnASymbol()
    {
        var pdf = TestFiles.Write(File(), "notizen-maus.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 900 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitPageCommand.Execute(null);
                UiSession.Capture(window);
                void Click(Point at)
                {
                    window.MouseDown(at, MouseButton.Left);
                    window.MouseUp(at, MouseButton.Left);
                    UiSession.Capture(window);
                }
                var button = window.GetVisualDescendants().OfType<RibbonToggleButton>().Single(b => b.IsEffectivelyVisible && b.Label == Tr.T("Notizen"));
                Assert.True(button.IsEffectivelyEnabled);
                Click(button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value);
                Assert.True(doc.IsNotesOpen);
                Click(button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value);
                Assert.False(doc.IsNotesOpen);

                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                Assert.Equal(ViewTool.SelectText, vm.Tool);
                Click(At(110, 710));
                Assert.True(doc.IsNotesOpen);
                Assert.Equal("Bitte pruefen", doc.SelectedNoteRow?.Text);
                if (UiSession.ScreenshotFolder() is { } folder)
                    UiSession.Capture(window).Save(Path.Combine(folder, "notizen.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                Click(At(110, 310));
                Assert.Equal("Ohne Bild", doc.SelectedNoteRow?.Text);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>The note of a redaction reason (B-14) can be read here: that is what the area is for.</summary>
    [Fact]
    public void NoteOfARedactionReason_CanBeRead()
    {
        var pdf = TestFiles.Write(CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            [4] = ("", "BT /F1 12 Tf 72 700 Td (Geheimer Name) Tj ET"),
            [5] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        }), "notizen-grund.pdf");
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
                doc.AddElement(0, RedactionElement.FromPageAreas(DocumentViewModel.AreasOf(text, Assert.Single(text.Find("Geheimer Name")))), "Test");
                vm.ToggleNotesCommand.Execute(null);
                Assert.True(doc.NotesNothing);
                vm.ConfirmRedaction = _ => Task.FromResult(new RedactionReply(RedactionAnswer.Apply,
                    Reasons: new RedactionReasonSettings("Datenschutz", false, true, RedactionProtocolPlace.None, "Kuerzel")));
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                Assert.True(doc.HasAppliedRedactions, vm.Message);
                var row = Assert.Single(doc.NoteRows);
                Assert.Contains("Datenschutz", row.Text);
                Assert.Contains("Kuerzel", row.Header);
                Assert.Equal(Tr.T("Schwärzungsgrund"), row.Subject);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// A note with NoZoom (/F 8) is drawn above 100 % at its screen size, at the top-left corner of /Rect: a click finds it only there — at 400 %
    /// in the top-left quarter of /Rect, at 100 % in all of it (ISO 32000-2, 12.5.3; docs/FEHLERGEDAECHTNIS.md, No. 576).
    /// </summary>
    [Fact]
    public void NoZoomNote_FoundWhereItIsShown()
    {
        var pdf = TestFiles.Write(CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[3 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Page/Parent 2 0 R/Annots[4 0 R]>>",
            [4] = "<</Type/Annot/Subtype/Text/Rect[100 300 120 320]/F 8/Contents(Klein bei Zoom)>>",
        }), "notiz-nozoom.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                Assert.False(doc.ChooseNoteAt(0, 118, 302, 0.25));
                Assert.True(doc.ChooseNoteAt(0, 102, 318, 0.25));
                Assert.Equal("Klein bei Zoom", doc.SelectedNoteRow?.Text);
                Assert.True(doc.ChooseNoteAt(0, 118, 302));

                // the view measures with the scale its tiles are drawn with (No. 589): at 120 % the tiles are drawn at 2 px/pt, so 1.333 / 2,
                // not 1.333 / 1.6
                doc.ZoomMode = ZoomMode.Custom;
                doc.Zoom = 1.2;
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Assert.Equal(DocumentView.DipPerPoint / 2, view.NoZoomScaleForTests, 9);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>Pixels of the light yellow of a note (0xFFF1A8) in a rendered frame.</summary>
    private static int NoteYellow(Avalonia.Media.Imaging.WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        var count = 0;
        var bgra = buffer.Format == Avalonia.Platform.PixelFormat.Bgra8888;
        for (var y = 0; y < buffer.Size.Height; y++)
        for (var x = 0; x < buffer.Size.Width; x++)
        {
            var i = y * buffer.RowBytes + x * 4;
            int r = bgra ? bytes[i + 2] : bytes[i], g = bytes[i + 1], b = bgra ? bytes[i] : bytes[i + 2];
            if (Math.Abs(r - 255) < 8 && Math.Abs(g - 241) < 8 && Math.Abs(b - 168) < 8) count++;
        }
        return count;
    }

    /// <summary>
    /// G-6: an own note (written with NoZoom) keeps its size on screen above 100 % — at 400 % its bubble covers as many pixels as at 100 % —, a
    /// click in "Bearbeiten" finds it only where it is shown (the top-left corner of its rectangle), it has no handles but can be moved
    /// (ISO 32000-2, 12.5.3; docs/FEHLERGEDAECHTNIS.md, No. 577).
    /// </summary>
    [Fact]
    public void OwnNote_KeepsItsSizeWhenZoomed()
    {
        var pdf = TestFiles.Write(TestPdf.Simple(1).Build(), "eigene-notiz-zoom.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var note = doc.AddNote(0, 450, 150)!;
                doc.CloseNotes();
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                Point At(double x, double y) => view.TranslatePoint(view.PagePointToView(0, x, y), window)!.Value;
                void Show(double zoom)
                {
                    doc.ZoomMode = ZoomMode.Custom;
                    doc.Zoom = zoom;
                    UiSession.Capture(window);
                    var target = view.PagePointToView(0, 460, 160);
                    view.Offset += new Vector(target.X - view.Bounds.Width / 2, target.Y - view.Bounds.Height / 2);
                }
                Show(1);
                var at100 = NoteYellow(UiSession.Capture(window));
                Show(4);
                var at400 = NoteYellow(UiSession.Capture(window));
                Assert.True(at100 > 200, $"100 %: {at100}");
                Assert.InRange(at400, at100 * 0.8, at100 * 1.25);

                vm.SetToolCommand.Execute(ViewTool.Edit);
                doc.SelectedElement = null;
                void Click(Point p)
                {
                    window.MouseDown(p, MouseButton.Left);
                    window.MouseUp(p, MouseButton.Left);
                    UiSession.Capture(window);
                }
                Click(At(468, 152));   // bottom right of the rectangle: not shown there
                Assert.False(doc.SelectedElement is { Element: NoteElement });
                Thread.Sleep(600);     // no double-click
                Click(At(451, 169));   // top left: the note
                Assert.True(doc.SelectedElement is { Element: NoteElement }, "Notiz oben links nicht getroffen");

                // moved by dragging from where it is shown; its rectangle keeps its size
                Thread.Sleep(600);
                window.MouseDown(At(451, 169), MouseButton.Left);
                window.MouseMove(At(461, 159));
                window.MouseMove(At(471, 149));
                window.MouseUp(At(471, 149), MouseButton.Left);
                UiSession.Capture(window);
                var moved = Assert.IsType<NoteElement>(Assert.Single(doc.ElementsOn(0)));
                Assert.Equal(note.Rect.Width, moved.Rect.Width, 3);
                Assert.True(moved.Rect.Left > note.Rect.Left + 10, $"{note.Rect} → {moved.Rect}");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
