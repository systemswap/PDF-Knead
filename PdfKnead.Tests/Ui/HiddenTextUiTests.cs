using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Verborgenes schwärzen“ (KONZEPT.md, section 5.18): the tool frames the hidden spots and lists them (beside the page first), a click
/// on a frame marks a spot or takes the mark back, „Alles außerhalb schwärzen“ marks those beside the page, and only „Schwärzungen
/// anwenden“ removes them — only the marked ones.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class HiddenTextUiTests
{
    private static void WaitUntil(Window window, Func<bool> condition, int seconds = 20)
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

    private static string Word(HiddenTextSpot spot) => spot.Text.Replace(" ", "");

    [Fact]
    public void Tool_FramesListsMarksAndApplies()
    {
        var pdf = TestFiles.Write(HiddenTextTests.Build(), "verborgen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Secure;
                Assert.True(vm.RedactHiddenCommand.CanExecute(null));
                vm.RedactHiddenCommand.Execute(null);
                Assert.True(vm.IsHiddenTool);
                WaitUntil(window, () => !doc.HiddenSearching && doc.HiddenRows.Count == 5);

                // beside the page first, then the spots on the page
                var outside = Assert.Single(doc.HiddenOutside);
                Assert.Equal("DRAUSSEN", Word(outside.Spot));
                Assert.Same(outside, doc.HiddenRows[0]);
                Assert.Equal(4, doc.HiddenOnPage.Count);
                Assert.Equal(Tr.T("{0} verborgene Stellen", 5), doc.HiddenStatus);
                Assert.Equal(Tr.T("{0} Stellen", 5), doc.HiddenPosition);
                Shoot(window, "78-verborgenes-schwaerzen");

                // a click on the frame of the white text marks it; a second click takes the mark back
                var view = window.GetVisualDescendants().OfType<DocumentView>().Single();
                UiSession.Capture(window);
                var white = doc.HiddenRows.Single(r => Word(r.Spot) == "WEISSGEHEIM");
                Point FrameCentre()
                {
                    var frame = view.HiddenFrames.Single(f => ReferenceEquals(f.Spot, white.Spot)).Frame;
                    return view.TranslatePoint(frame.Center, window)!.Value;
                }
                void Click()
                {
                    var at = FrameCentre();
                    window.MouseDown(at, MouseButton.Left);
                    window.MouseUp(at, MouseButton.Left);
                }
                Click();
                Assert.True(white.IsMarked);
                Assert.Same(white, doc.SelectedHiddenRow);
                Assert.Same(white, doc.SelectedOnPageRow);
                Assert.Null(doc.SelectedOutsideRow);
                Assert.Equal(Tr.T("Stelle {0} von {1}", 2, 5), doc.HiddenPosition);
                Assert.Null(doc.AllOnPageMarked);   // some marked
                var mark = Assert.Single(doc.ElementsOn(0).OfType<RedactionElement>());
                Assert.True(mark.Hidden);
                Assert.Null(doc.SelectedElement);   // marks of hidden content are not selected
                Shoot(window, "79-verborgenes-markiert");
                Click();
                Assert.False(white.IsMarked);
                Assert.Empty(doc.ElementsOn(0));
                Click();
                Assert.True(white.IsMarked);

                // „Alle“ on the page marks everything but the OCR text, in one step; once more takes all back
                doc.AllOnPageMarked = true;
                Assert.True(doc.AllOnPageMarked);
                Assert.Equal(3, doc.RedactionCount);
                Assert.False(doc.HiddenRows.Single(r => Word(r.Spot) == "OCRTEXT").IsMarked);
                doc.History.Undo();
                Assert.Equal(1, doc.RedactionCount);
                doc.AllOnPageMarked = true;
                doc.AllOnPageMarked = false;
                Assert.Equal(0, doc.RedactionCount);
                Assert.False(doc.AllOnPageMarked);
                white.IsMarked = true;

                // „Alles außerhalb schwärzen“ (the box „Alle“ beside the page), ticking in the list, stepping through the spots
                doc.AllOutsideMarked = true;
                Assert.True(outside.IsMarked);
                Assert.True(doc.AllOutsideMarked);
                var tiny = doc.HiddenRows.Single(r => Word(r.Spot) == "WINZIG");
                tiny.IsMarked = true;
                Assert.Equal(3, doc.RedactionCount);
                Shoot(window, "79c-verborgenes-liste");
                doc.SelectedHiddenRow = null;
                doc.NextHiddenSpotCommand.Execute(null);
                Assert.Same(outside, doc.SelectedHiddenRow);
                doc.PreviousHiddenSpotCommand.Execute(null);
                Assert.Same(doc.HiddenRows[^1], doc.SelectedHiddenRow);

                // tool off: list gone, marks stay
                vm.RedactHiddenCommand.Execute(null);
                Assert.False(vm.IsHiddenTool);
                Assert.Empty(doc.HiddenRows);
                Assert.Equal(3, doc.RedactionCount);
                Shoot(window, "79a-verborgenes-aus");

                // only „Schwärzungen anwenden“ removes them — only the marked ones, the visible text stays (without removing hidden content and
                // the old text recognition along with it, which RedactionUiTests checks)
                vm.ConfirmRedaction = _ => Task.FromResult(new RedactionReply(RedactionAnswer.Apply, CleanHidden: false, RemoveOcr: false));
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                Assert.Equal(0, doc.RedactionCount);
                var left = HiddenText.Find(doc.Document, doc.Document.Pages[0]).Select(Word).Order().ToList();
                Assert.Equal(["OCRTEXT", "VERDECKT"], left);
                var text = (await doc.GetTextAsync(0)).Text;
                Assert.Contains("Sichtbar", text);
                Assert.Contains("SICHTBARDA", text);
                Assert.DoesNotContain("WEISSGEHEIM", text);

                // switched on again: the redacted page is searched anew
                vm.RedactHiddenCommand.Execute(null);
                WaitUntil(window, () => !doc.HiddenSearching && doc.HiddenRows.Count == 2);
                Assert.Empty(doc.HiddenOutside);
                Shoot(window, "79b-verborgenes-nach-anwenden");
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// A spot beside the page shows on the screen once its row is chosen, not only once it is marked: near the page it is framed where it
    /// lies, far beyond (here 500 pt left of and below the page) a badge at the edge of the view points to it. A click on it marks it; a row
    /// not chosen shows nothing.
    /// </summary>
    [Fact]
    public void OutsideSpot_ChosenRow_ShownOnScreen_ClickMarks()
    {
        var pdf = TestFiles.Write(HiddenTextTests.Build(), "verborgen-draussen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RibbonTab = RibbonTab.Secure;
                vm.RedactHiddenCommand.Execute(null);
                WaitUntil(window, () => !doc.HiddenSearching && doc.HiddenRows.Count == 5);
                var view = window.GetVisualDescendants().OfType<DocumentView>().Single();
                var outside = Assert.Single(doc.HiddenOutside);
                UiSession.Capture(window);
                Assert.DoesNotContain(view.HiddenFrames, f => ReferenceEquals(f.Spot, outside.Spot));   // not chosen: only in the list

                doc.SelectedHiddenRow = outside;
                UiSession.Capture(window);
                var shown = Assert.Single(view.HiddenFrames, f => ReferenceEquals(f.Spot, outside.Spot)).Frame;
                Assert.True(new Rect(view.Bounds.Size).Contains(shown), "on the screen");
                Shoot(window, "79d-verborgenes-ausserhalb");
                Assert.False(outside.IsMarked);
                var at = view.TranslatePoint(shown.Center, window)!.Value;
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Assert.True(outside.IsMarked);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Hidden text under a bar goes with the bar: the list shows it marked (the box locked, „wird mit dem Balken geschwärzt“), the
    /// frame says „wird geschwärzt“, a click does not add a second mark and „Alle“ skips it; taking the bar back makes it a spot of its
    /// own again (docs/FEHLERGEDAECHTNIS.md, No. 190).
    /// </summary>
    [Fact]
    public void SpotUnderABar_ShownAsGoingWithIt()
    {
        var pdf = TestFiles.Write(HiddenTextTests.Build(), "verborgen-unter-balken.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                // a bar over the black box with the covered word under it
                doc.AddElement(0, RedactionElement.FromPageAreas([new Core.Pdf.Document.PdfRectangle(55, 385, 185, 420)]), Tr.T("Bereich schwärzen"));
                vm.RedactHiddenCommand.Execute(null);
                WaitUntil(window, () => !doc.HiddenSearching && doc.HiddenRows.Count == 5);
                var covered = doc.HiddenRows.Single(r => Word(r.Spot) == "VERDECKT");
                Assert.True(covered.IsUnderBar);
                Assert.True(covered.IsMarked);
                Assert.Contains(Tr.T("wird mit dem Balken geschwärzt"), covered.Place);
                var box = window.GetVisualDescendants().OfType<HiddenContentPanel>().Single().GetVisualDescendants().OfType<ListBoxItem>()
                    .Single(i => ReferenceEquals(i.DataContext, covered)).GetVisualDescendants().OfType<CheckBox>().Single();
                Assert.False(box.IsEnabled);

                // neither a click on the frame nor the box adds a mark; „Alle“ marks the others only
                doc.ToggleHiddenMark(0, covered.Spot);
                covered.IsMarked = false;
                Assert.Single(doc.ElementsOn(0));
                Assert.True(covered.IsMarked);
                doc.AllOnPageMarked = true;
                Assert.True(doc.AllOnPageMarked);
                Assert.Equal(1 + 2, doc.ElementsOn(0).Count);   // the bar, white text and tiny text (OCR only one by one)
                Assert.DoesNotContain(doc.ElementsOn(0).OfType<RedactionElement>(), r => r.Hidden && r.Rect.Top < 420 && r.Rect.Bottom > 385);
                Shoot(window, "79e-verborgen-unter-balken");

                // the bar taken back: a spot of its own again
                doc.History.Undo();
                doc.History.Undo();
                UiSession.Capture(window);
                Assert.Empty(doc.ElementsOn(0));
                Assert.False(covered.IsUnderBar);
                Assert.False(covered.IsMarked);
                Assert.True(box.IsEnabled);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// A bar over a few letters of a long hidden line does not take the line: the spot stays a spot of its own, can be marked and goes
    /// wholly — the bar had the line's centre under it, the spot was shown as going with the bar and its rest stayed in the file
    /// (docs/FEHLERGEDAECHTNIS.md, No. 193).
    /// </summary>
    [Fact]
    public void BarOverPartOfAHiddenLine_LineStaysToMarkAndGoesWholly()
    {
        var pdf = TestFiles.Write(HiddenTextTests.Build(
            "BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET BT 3 Tr /F1 10 Tf 72 600 Td (OCRANFANG MITTELSTUECK OCRENDE) Tj ET"), "verborgen-teilbalken.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                // a bar over the middle word only (the line runs from 72 to about 240 pt)
                var text = await doc.GetTextAsync(0);
                doc.AddElement(0, RedactionElement.FromPageAreas(DocumentViewModel.AreasOf(text, Assert.Single(text.Find("MITTELSTUECK")))), Tr.T("Text schwärzen"));
                vm.RedactHiddenCommand.Execute(null);
                WaitUntil(window, () => !doc.HiddenSearching && doc.HiddenRows.Count == 1);
                var row = doc.HiddenRows[0];
                Assert.False(row.IsUnderBar);
                Assert.False(row.IsMarked);
                row.IsMarked = true;
                Assert.True(row.IsMarked);
                vm.RedactHiddenCommand.Execute(null);

                vm.ConfirmRedaction = _ => Task.FromResult(new RedactionReply(RedactionAnswer.Apply));
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                Assert.Empty(HiddenText.Find(doc.Document, doc.Document.Pages[0]));
                var after = (await doc.GetTextAsync(0)).Text;
                Assert.DoesNotContain("OCRANFANG", after);
                Assert.DoesNotContain("OCRENDE", after);
                Assert.Contains("Sichtbar", after);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// Two spots that overlap: marking the one does not show the other as marked — the centre of its line lay under the other's mark, so
    /// it counted as marked, nobody ticked it, and its characters outside that mark stayed in the file (docs/FEHLERGEDAECHTNIS.md, No. 194).
    /// </summary>
    [Fact]
    public void OverlappingSpots_MarkingOneDoesNotMarkTheOther()
    {
        // invisible text from 72 to about 150 pt, white text over its middle (another kind: another spot)
        var pdf = TestFiles.Write(HiddenTextTests.Build(
            "BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET BT 3 Tr /F1 10 Tf 72 600 Td (OCRLINKSUNDRECHTS) Tj ET " +
            "1 1 1 rg BT 0 Tr /F1 10 Tf 100 600 Td (WEISS) Tj ET 0 g"), "verborgen-ueberlappend.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RedactHiddenCommand.Execute(null);
                WaitUntil(window, () => !doc.HiddenSearching && doc.HiddenRows.Count == 2);
                var ocr = doc.HiddenRows.Single(r => r.Spot.Kind == HiddenTextKind.Invisible);
                var white = doc.HiddenRows.Single(r => r.Spot.Kind != HiddenTextKind.Invisible);
                white.IsMarked = true;
                Assert.True(white.IsMarked);
                Assert.False(ocr.IsMarked);
                ocr.IsMarked = true;
                Assert.True(ocr.IsMarked);
                vm.RedactHiddenCommand.Execute(null);

                vm.ConfirmRedaction = _ => Task.FromResult(new RedactionReply(RedactionAnswer.Apply));
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                var after = (await doc.GetTextAsync(0)).Text;
                Assert.DoesNotContain("OCR", after);
                Assert.DoesNotContain("RECHTS", after);
                Assert.DoesNotContain("WEISS", after);
                Assert.Contains("Sichtbar", after);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// A double-click on a row shows the whole text of its spot in a flyout beside the panel — copyable only if the document allows
    /// copying; while it is open, a click on another row shows that one's text; a click elsewhere or Esc closes it; a double-click on
    /// the check box only marks.
    /// </summary>
    [Fact]
    public void DoubleClick_ShowsTheWholeTextInAFlyout()
    {
        var pdf = TestFiles.Write(HiddenTextTests.Build(HiddenTextTests.LongInvisible() +
            " 1 1 1 rg BT 0 Tr /F1 10 Tf 300 100 Td (WEISSGEHEIM) Tj ET 0 g"), "verborgen-lang.pdf");
        var restrictedPdf = TestPdf.Simple(1);
        restrictedPdf.Replace(4, new Core.Pdf.Objects.PdfStream(new Core.Pdf.Objects.PdfDictionary(), System.Text.Encoding.ASCII.GetBytes(HiddenTextTests.LongInvisible())));
        var restricted = TestFiles.Write(restrictedPdf.Build(cipher: Core.Pdf.Security.PdfCipher.Aes256,
            // redaction marks are annotations and remove content when applied: bits 6 and 4 (Table 22; docs/FEHLERGEDAECHTNIS.md, No. 1111)
            permissions: Core.Pdf.Security.PdfPermissions.Print | Core.Pdf.Security.PdfPermissions.Modify | Core.Pdf.Security.PdfPermissions.Annotate),
            "verborgen-lang-gesperrt.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RedactHiddenCommand.Execute(null);
                WaitUntil(window, () => !doc.HiddenSearching && doc.HiddenRows.Count == 2);
                var row = doc.HiddenRows.Single(r => r.Spot.Kind == HiddenTextKind.Invisible);
                var white = doc.HiddenRows.Single(r => Word(r.Spot) == "WEISSGEHEIM");
                Assert.True(row.Preview.Length < 130);   // the row shows only the beginning

                var panel = window.GetVisualDescendants().OfType<HiddenContentPanel>().Single();
                var flyout = window.GetVisualDescendants().OfType<HiddenTextFlyout>().Single();
                var host = (Border)flyout.Parent!;
                UiSession.Capture(window);
                ListBoxItem Item(HiddenSpotRow r) => panel.GetVisualDescendants().OfType<ListBoxItem>().Single(i => ReferenceEquals(i.DataContext, r));
                Point Centre(Visual target) => target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
                void Click(Point at, int times = 1)
                {
                    for (var k = 0; k < times; k++)
                    {
                        window.MouseDown(at, MouseButton.Left);
                        window.MouseUp(at, MouseButton.Left);
                    }
                    UiSession.Capture(window);
                }
                TextBlock Preview(HiddenSpotRow r) => Item(r).GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == r.Preview);

                // on the check box: marks (and unmarks), no flyout
                Click(Centre(Item(row).GetVisualDescendants().OfType<CheckBox>().Single()), 2);
                Assert.False(host.IsVisible);

                // on the text: the flyout with the whole text, copyable. Whether two clicks make a double-click depends on the real time between
                // them: under load (the full test run) they can arrive too far apart and count as two single clicks — then the double-click once more
                Click(Centre(Preview(row)), 2);
                for (var again = 0; again < 2 && !host.IsVisible; again++) Click(Centre(Preview(row)), 2);
                Assert.True(doc.IsHiddenTextOpen);
                Assert.True(host.IsVisible);
                Assert.Equal(row.Spot.Text, flyout.ShownText);
                Assert.Contains("Z59", flyout.ShownText!.Replace(" ", ""));
                Assert.True(flyout.CanCopyText);
                Shoot(window, "79d-verborgener-text");

                // a click on another row: its text, the flyout stays open; a click into the flyout keeps it open too
                Click(Centre(Preview(white)));
                Assert.True(host.IsVisible);
                Assert.Same(white, doc.SelectedHiddenRow);
                Assert.Equal(white.Spot.Text, flyout.ShownText);
                var box = flyout.GetVisualDescendants().OfType<CheckBox>().Single();
                Click(Centre(box));
                Assert.True(white.IsMarked);   // the spot can be marked in the flyout as well
                Assert.True(host.IsVisible);

                // a click elsewhere (on the page area) closes it; the tool stays on
                var view = window.GetVisualDescendants().OfType<DocumentView>().Single();
                Click(view.TranslatePoint(new Point(40, view.Bounds.Height - 40), window)!.Value);
                Assert.False(doc.IsHiddenTextOpen);
                Assert.False(host.IsVisible);
                Assert.True(vm.IsHiddenTool);

                // Enter on the chosen row opens it, Esc closes it — the tool stays on
                Item(white).Focus();
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Assert.True(doc.IsHiddenTextOpen);
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.False(doc.IsHiddenTextOpen);
                Assert.True(vm.IsHiddenTool);

                // the tool off: closed
                doc.ShowHiddenText(row);
                vm.RedactHiddenCommand.Execute(null);
                Assert.False(doc.IsHiddenTextOpen);

                // a document that may be changed but not copied from: the text can only be read
                await vm.OpenAsync([restricted]);
                var locked = vm.SelectedDocument!;
                Assert.False(locked.CopyAllowed);
                vm.RedactHiddenCommand.Execute(null);
                WaitUntil(window, () => !locked.HiddenSearching && locked.HiddenRows.Count == 1);
                locked.ShowHiddenText(locked.HiddenRows[0]);
                UiSession.Capture(window);
                Assert.True(host.IsVisible);
                Assert.False(flyout.CanCopyText);
                Assert.Equal(locked.HiddenRows[0].Spot.Text, flyout.ShownText);
                Assert.DoesNotContain(flyout.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.IsEffectivelyVisible);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// „Alle“ from the half state marks all (it took the marks back); a spot counts as marked only if every line is covered; a mark of
    /// hidden content gives the gate no words to search for elsewhere — the visible text in its area is not redacted text
    /// (docs/FEHLERGEDAECHTNIS.md, No. 136).
    /// </summary>
    [Fact]
    public void AllFromHalfState_WholeSpot_NoWordsForTheGate()
    {
        var pdf = TestFiles.Write(HiddenTextTests.Build(
            "BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET " +
            "BT /F1 10 Tf 72 600 Td (SICHTBARDA) Tj ET q BT 3 Tr /F1 10 Tf 72 600 Td (OCRTEXT) Tj ET Q " +
            "BT /F1 10 Tf 72 300 Td (SICHTBARDA) Tj ET " +
            "1 1 1 rg BT /F1 10 Tf 300 700 Td 12 TL (WEISSEINS) Tj T* (WEISSZWEI) Tj ET 0 g " +
            "1 1 1 rg BT /F1 10 Tf 300 500 Td (WEISSDREI) Tj ET 0 g"), "verborgen-alle.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RedactHiddenCommand.Execute(null);
                WaitUntil(window, () => !doc.HiddenSearching && doc.HiddenRows.Count == 3);
                var two = doc.HiddenRows.Single(r => r.Spot.Lines.Count == 2);

                // a mark over the first line only: not marked; ticking adds the full mark
                doc.History.AddElement(0, RedactionElement.FromPageAreas([HiddenText.MarkAreas(two.Spot)[0]]) with { Hidden = true }, "Test");
                Assert.False(two.IsMarked);
                doc.HiddenRows.Single(r => Word(r.Spot) == "WEISSDREI").IsMarked = true;
                Assert.Null(doc.AllOnPageMarked);
                // from the half state the box sends „off“: all are marked all the same
                doc.AllOnPageMarked = false;
                Assert.True(doc.AllOnPageMarked);
                Assert.True(two.IsMarked);

                // the OCR text over SICHTBARDA marked: the gate does not offer SICHTBARDA elsewhere for black bars
                doc.HiddenRows.Single(r => r.Spot.Kind == HiddenTextKind.Invisible).IsMarked = true;
                var questions = new List<RedactionQuestion>();
                vm.ConfirmRedaction = q =>
                {
                    questions.Add(q);
                    return Task.FromResult(new RedactionReply(RedactionAnswer.Apply, AlsoUnredacted: true));
                };
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                Assert.Empty(Assert.Single(questions).Unredacted);
                var text = (await doc.GetTextAsync(0)).Text;
                Assert.Equal(2, text.Split("SICHTBARDA").Length - 1);
                Assert.DoesNotContain("OCRTEXT", text);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// The gate searches the removed hidden text elsewhere too: the same name in the document properties is offered for cleaning up
    /// (docs/FEHLERGEDAECHTNIS.md, No. 146).
    /// </summary>
    [Fact]
    public void HiddenMark_GateFindsItsTextElsewhere()
    {
        var file = TestPdf.Simple(1, "Vertrag Musterfrau");
        file.Replace(4, new Core.Pdf.Objects.PdfStream(new Core.Pdf.Objects.PdfDictionary(),
            "BT /F1 20 Tf 72 760 Td (Sichtbar) Tj ET 1 1 1 rg BT /F1 10 Tf 300 700 Td (Erika Musterfrau) Tj ET 0 g"u8.ToArray()));
        var pdf = TestFiles.Write(file.Build(), "verborgen-woanders.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.RedactHiddenCommand.Execute(null);
                WaitUntil(window, () => !doc.HiddenSearching && doc.HiddenRows.Count == 1);
                doc.HiddenRows[0].IsMarked = true;
                var questions = new List<RedactionQuestion>();
                vm.ConfirmRedaction = q =>
                {
                    questions.Add(q);
                    return Task.FromResult(new RedactionReply(RedactionAnswer.Apply));
                };
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                Assert.Contains(Assert.Single(questions).Elsewhere, e => e.Cleanup == Core.Pdf.Writing.CleanupItems.Metadata);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
