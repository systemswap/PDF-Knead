using Avalonia.Controls;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Jetzt schwärzen?“, the other places of the redacted text (KONZEPT.md, section 5.6; wish of 30.10.2026): exactly the marked text is chosen at
/// first, single words of it are not (only the word would turn black, „Einrichtung“ of „Einrichtung der B“, and the „B“ would stay); a line
/// jumps to its place and frames it; invisible text says so and goes without a bar, which would cover what lies there.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class RedactionHitsUiTests
{
    /// <summary>Page 1 with what is marked and a line with a word of it; page 2 with exactly the marked text, and invisible text over another word.</summary>
    private static byte[] File() => CraftedFileTests.Build(new Dictionary<int, object>
    {
        [1] = "<</Type/Catalog/Pages 2 0 R>>",
        [2] = "<</Type/Pages/Kids[4 0 R 5 0 R]/Count 2/MediaBox[0 0 595 842]>>",
        [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Font<</F1 3 0 R>>>>>>",
        [5] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/Resources<</Font<</F1 3 0 R>>>>>>",
        [6] = ("", "BT /F1 12 Tf 72 700 Td (Einrichtung der B) Tj ET BT /F1 12 Tf 72 660 Td (Geheimwort) Tj ET " +
                   "BT /F1 12 Tf 72 600 Td (Hier die Einrichtung B am Ort) Tj ET"),
        [7] = ("", "BT /F1 12 Tf 72 700 Td (Wieder Einrichtung der B dort) Tj ET BT /F1 12 Tf 72 600 Td (Sichtbares) Tj ET " +
                   "q BT 3 Tr /F1 12 Tf 72 600 Td (Geheimwort) Tj ET Q"),
    });

    private static async Task Mark(DocumentViewModel doc)
    {
        var text = await doc.GetTextAsync(0);
        foreach (var words in new[] { "Einrichtung der B", "Geheimwort" })
            doc.AddElement(0, RedactionElement.FromPageAreas(DocumentViewModel.AreasOf(text, Assert.Single(text.Find(words)))), "Test");
    }

    private static string TextOf(PdfDocument d, int page) => PageText.Create(Core.Pdf.Content.PageInterpreter.BuildText(d, d.Pages[page]), 0).Text;

    private static RedactionQuestion QuestionFor(DocumentViewModel doc, IReadOnlyList<RedactionHit> hits) => new(2, 1, null, true)
    {
        Unredacted = hits,
        OcrPages = [1],
        RedactedPages = new HashSet<int> { 0 },
        ShowHit = doc.ShowRedactionHitAsync,
        InstalledOcr = () => [],
    };

    [Fact]
    public void Hits_ExactOrAWordOfIt_TheHitSetOff_InvisibleNamed()
    {
        var pdf = TestFiles.Write(File(), "weitere-stellen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                await Mark(doc);
                var hits = await doc.FindUnredactedAsync();
                Assert.Equal(3, hits.Count);

                var word = Assert.Single(hits, h => !h.Whole);
                Assert.Equal((0, "Einrichtung"), (word.Page, word.Found));
                Assert.False(word.Invisible);
                Assert.Equal("Einrichtung", word.Context.Substring(word.InContext.Start, word.InContext.Length));
                Assert.Contains("Einrichtung B", word.Context);

                var whole = Assert.Single(hits, h => h.Whole && !h.Invisible);
                Assert.Equal((1, "Einrichtung der B"), (whole.Page, whole.Found));
                Assert.Equal("Einrichtung der B", whole.Context.Substring(whole.InContext.Start, whole.InContext.Length));

                var hidden = Assert.Single(hits, h => h.Invisible);
                Assert.Equal((1, "Geheimwort"), (hidden.Page, hidden.Found));
                Assert.True(hidden.Whole);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>
    /// With the defaults: the exact place on page 2 gets its bar, the word on page 1 stays; invisible text goes without a bar — the visible word it
    /// lies over stays (with a bar it would be gone), and page 2 does not count as „mit Schwärzung“ because of it.
    /// </summary>
    [Fact]
    public void Apply_ExactHitsOnly_InvisibleTextGoesWithoutABar()
    {
        var pdf = TestFiles.Write(File(), "weitere-stellen-anwenden.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                await Mark(doc);
                vm.ConfirmRedaction = question =>
                {
                    var dialog = new RedactionWindow(question);
                    Assert.True(dialog.AlsoUnredacted.IsChecked);
                    Assert.False(dialog.AlsoWords.IsChecked);
                    dialog.RemoveOcr.IsChecked = false;   // the invisible text must go by its line, not with the old text recognition
                    var reply = dialog.Reply(RedactionAnswer.Apply);
                    Assert.True(reply.AlsoUnredacted);
                    Assert.False(reply.AlsoWords);
                    return Task.FromResult(reply);
                };
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                Assert.True(doc.HasAppliedRedactions, vm.Message);
                Assert.Contains("Einrichtung B", TextOf(doc.Document, 0));
                var second = TextOf(doc.Document, 1);
                Assert.DoesNotContain("Einrichtung der B", second);
                Assert.DoesNotContain("Geheimwort", second);
                Assert.Contains("Sichtbares", second);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>The word chosen as well: only the word goes, the rest of its line stays.</summary>
    [Fact]
    public void Apply_WordsChosen_OnlyTheWordGoes()
    {
        var pdf = TestFiles.Write(File(), "weitere-stellen-woerter.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                await Mark(doc);
                vm.ConfirmRedaction = question =>
                {
                    var dialog = new RedactionWindow(question);
                    dialog.AlsoWords.IsChecked = true;
                    return Task.FromResult(dialog.Reply(RedactionAnswer.Apply));
                };
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                Assert.True(doc.HasAppliedRedactions, vm.Message);
                var first = TextOf(doc.Document, 0);
                Assert.DoesNotContain("Einrichtung", first);
                Assert.Contains("Hier die", first);
                Assert.Contains("am Ort", first);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>The line of invisible text follows the choice about the old text recognition: grey while it goes anyway, otherwise in orange with its tick.</summary>
    [Fact]
    public void InvisibleLine_FollowsTheChoiceAboutTheOldRecognition()
    {
        var pdf = TestFiles.Write(File(), "weitere-stellen-versteckt.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                await Mark(doc);
                var hits = await doc.FindUnredactedAsync();
                var dialog = new RedactionWindow(QuestionFor(doc, hits));
                var line = Assert.Single(dialog.HitLines, l => l.Hit.Invisible);

                // ticked, whole document (the defaults)
                Assert.True(line.GoesAlong);
                Assert.Equal(Tr.T("Seite {0} (versteckt, wird mit entfernt)", 2), line.Where);

                // only the pages with a redaction: the hit on page 2 stays unless chosen
                dialog.WholeDocument.IsChecked = false;
                Assert.False(line.GoesAlong);
                Assert.Equal(Tr.T("Seite {0} (versteckt)", 2), line.Where);
                dialog.WholeDocument.IsChecked = true;
                Assert.True(line.GoesAlong);

                // the old text recognition kept
                dialog.RemoveOcr.IsChecked = false;
                Assert.False(line.GoesAlong);
                Assert.Equal(Tr.T("Seite {0} (versteckt)", 2), line.Where);
                Assert.True(dialog.AlsoUnredacted.IsVisible);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>A line chosen jumps there and frames the place for as long as it is chosen; one list at a time; the frame does not outlive the question.</summary>
    [Fact]
    public void LineChosen_FramesThePlace_UntilNoneIsChosen()
    {
        var pdf = TestFiles.Write(File(), "weitere-stellen-rahmen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                await Mark(doc);
                var hits = await doc.FindUnredactedAsync();
                var dialog = new RedactionWindow(QuestionFor(doc, hits));
                async Task Until(Func<bool> done)
                {
                    for (var i = 0; i < 200 && !done(); i++) await Task.Delay(10);
                    Assert.True(done());
                }

                var hidden = Assert.Single(dialog.HitLines, l => l.Hit.Invisible);
                dialog.UnredactedList.SelectedItem = hidden;
                await Until(() => doc.ShownPlace is { Page: 1, Hidden: "Geheimwort" });
                Assert.NotEmpty(doc.ShownPlace!.Value.Areas);

                var word = Assert.Single(dialog.HitLines, l => !l.Hit.Whole);
                dialog.WordsList.SelectedItem = word;
                await Until(() => doc.ShownPlace is { Page: 0, Hidden: null });
                Assert.Null(dialog.UnredactedList.SelectedItem);

                dialog.WordsList.SelectedItem = null;
                await Until(() => doc.ShownPlace is null);

                // „Weiter markieren“ with a line chosen: no frame stays behind
                dialog.WordsList.SelectedItem = word;
                await Until(() => doc.ShownPlace is not null);
                vm.ConfirmRedaction = _ => Task.FromResult(new RedactionReply(RedactionAnswer.KeepMarking));
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                Assert.Null(doc.ShownPlace);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    /// <summary>Both sizes come with the program: no button to load models any more; a size that is missing cannot be chosen, small is preferred.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OcrSizes_OnlyWhatIsThere_SmallFirst(int installed)
    {
        UiSession.Run(() =>
        {
            var sizes = new[] { OcrModelSize.Small, OcrModelSize.Medium }.Take(installed).ToList();
            var dialog = new RedactionWindow(new RedactionQuestion(1, 1, null, true) { OcrPages = [0], InstalledOcr = () => sizes, OcrSize = OcrModelSize.Medium });
            Assert.Null(dialog.FindControl<Button>("LoadModels"));
            Assert.Equal(installed >= 1, dialog.FindControl<RadioButton>("OcrSmall")!.IsEnabled);
            Assert.Equal(installed >= 2, dialog.FindControl<RadioButton>("OcrMedium")!.IsEnabled);
            // the wished size if it is there, otherwise small (with none there the wish stays, but nothing can be chosen)
            Assert.Equal(installed != 1, dialog.FindControl<RadioButton>("OcrMedium")!.IsChecked == true);
            Assert.Equal(installed == 1, dialog.FindControl<RadioButton>("OcrSmall")!.IsChecked == true);
            return Task.CompletedTask;
        });
    }
}
