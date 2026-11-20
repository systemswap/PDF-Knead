using System.Net;
using Avalonia.Controls;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// „Text erkennen“ in the open document (KONZEPT.md, section 5.9): the pages with an image get an invisible text layer as one undoable step,
/// pages without one are left; cancelling keeps what is done; the layer stays when saving, also after the invisible text was cleaned up
/// before; a recognition that cannot start says so.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class TextRecognitionUiTests
{
    /// <summary>Three pages: a scan (grey image over the page), real text only, another scan.</summary>
    private static byte[] File()
    {
        const string Scan = "q 595 0 0 842 0 0 cm /Im Do Q";
        return CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R 5 0 R 10 0 R]/Count 3/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</XObject<</Im 8 0 R>>>>>>",
            [5] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/Resources<</Font<</F1 3 0 R>>>>>>",
            [6] = ("", Scan),
            [7] = ("", "BT /F1 12 Tf 72 700 Td (Echter Text) Tj ET"),
            [8] = ("/Type/XObject/Subtype/Image/Width 4/Height 4/BitsPerComponent 8/ColorSpace/DeviceGray", new string('æ', 16)),
            [10] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</XObject<</Im 8 0 R>>>>>>",
        });
    }

    /// <summary>Reads "Seite n" on every image, at the top left; can stop the run after the first page.</summary>
    private sealed class Reader(Action? afterFirst = null) : ITextRecognizer
    {
        public int Pages { get; private set; }
        public bool Disposed { get; private set; }

        public IReadOnlyList<OcrLine> Recognize(OcrImage image, CancellationToken cancel = default)
        {
            Pages++;
            if (Pages == 1) afterFirst?.Invoke();
            var s = image.Width / 595.0;
            OcrPoint[] Box(double x, double w) => [new(x * s, 100 * s), new((x + w) * s, 100 * s), new((x + w) * s, 116 * s), new(x * s, 116 * s)];
            return [new OcrLine([new OcrWord("Seite", Box(72, 40)), new OcrWord($"{Pages}", Box(116, 10))], 0.97)];
        }

        public void Dispose() => Disposed = true;
    }

    private static string Text(PdfDocument doc, int page) => PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[page]), 0).Text;

    [Fact]
    public void Recognise_ScansGetText_OneUndoStep_StaysWhenSaved()
    {
        var pdf = TestFiles.Write(File(), "texterkennung.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                // the button must learn that it is usable once a document is open (otherwise it stays grey)
                var told = 0;
                vm.RecognizeTextCommand.CanExecuteChanged += (_, _) => told++;
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                Assert.True(told > 0 && vm.RecognizeTextCommand.CanExecute(null));
                // invisible text cleaned up before: the new layer must still be saved
                await doc.CleanAsync(CleanupItems.InvisibleText);
                var reader = new Reader();
                vm.OpenRecognizer = _ => reader;
                RecognizeTextViewModel? asked = null;
                vm.ShowRecognizeText = model =>
                {
                    asked = model;
                    Assert.True(model.IsAll);
                    return Task.FromResult(true);
                };
                await vm.RecognizeTextCommand.ExecuteAsync(null);
                Assert.NotNull(asked);
                Assert.Equal(2, reader.Pages);   // the page with real text only is not read
                Assert.True(reader.Disposed);
                Assert.Equal(["Seite 1", "Echter Text", "Seite 2"], Enumerable.Range(0, 3).Select(i => Text(doc.Document, i)));
                Assert.Contains("2", vm.Notice);
                Assert.Null(doc.RecognitionStatus);

                vm.UndoCommand.Execute(null);
                Assert.Equal("", Text(doc.Document, 0));
                vm.RedoCommand.Execute(null);
                Assert.Equal("Seite 1", Text(doc.Document, 0));

                var copy = Path.Combine(Path.GetDirectoryName(pdf)!, "texterkennung-kopie.pdf");
                await doc.SaveAsync(copy, doc.SaveOptions("1.7"));
                using var saved = PdfDocument.Open(System.IO.File.ReadAllBytes(copy));
                Assert.Equal(["Seite 1", "Echter Text", "Seite 2"], Enumerable.Range(0, 3).Select(i => Text(saved, i)));
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void Cancelled_AfterTheFirstPage_ItKeepsItsText()
    {
        var pdf = TestFiles.Write(File(), "texterkennung-abbrechen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var reader = new Reader(() => doc.CancelRecognitionCommand.Execute(null));
                vm.OpenRecognizer = _ => reader;
                vm.ShowRecognizeText = _ => Task.FromResult(true);
                await vm.RecognizeTextCommand.ExecuteAsync(null);
                Assert.Equal(1, reader.Pages);
                Assert.Equal("Seite 1", Text(doc.Document, 0));
                Assert.Equal("", Text(doc.Document, 2));
                Assert.Contains("abgebrochen", vm.Notice);
                Assert.False(doc.IsRecognizing);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void ModelsNotLoadable_SaysSo_DocumentUnchanged()
    {
        var pdf = TestFiles.Write(File(), "texterkennung-ohne-modelle.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                var before = doc.Document;
                vm.OpenRecognizer = _ => throw new TextRecognitionException("damaged");
                vm.ShowRecognizeText = _ => Task.FromResult(true);
                await vm.RecognizeTextCommand.ExecuteAsync(null);
                Assert.Contains("neu entpacken", vm.Message);   // the models come with the program: a damaged installation
                Assert.Same(before, doc.Document);
                Assert.False(doc.CanUndo);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Window_PagesAndSize_ChecksTheInput()
    {
        UiSession.Run(() =>
        {
            var model = new RecognizeTextViewModel(12, 4, OcrModelSize.Small);
            Assert.Equal(Enumerable.Range(0, 12), model.Pages);
            model.IsCurrent = true;
            Assert.Equal([4], model.Pages);
            model.IsRange = true;
            model.RangeText = "2-3, 9";
            Assert.Equal([1, 2, 8], model.Pages);
            // without models (a damaged installation — they come with the program) it says so and cannot start
            if (!OcrModels.IsInstalled(OcrModelSize.Small) && !OcrModels.IsInstalled(OcrModelSize.Medium))
            {
                Assert.False(model.CanStart);
                Assert.Contains("ocr", model.Error);
            }
            var window = new RecognizeTextWindow(model);
            window.Show();
            window.Close();
            return Task.CompletedTask;
        });
    }

}

/// <summary>
/// „Jetzt schwärzen?“ → „Neue Texterkennung anlegen“ (KONZEPT.md, section 5.6, step c): checked when the document had a text recognition and
/// models are there; greyed out while the old one is kept; after redacting, the pages whose old one went are read anew — from the redacted
/// page, so nothing under a bar comes back — as their own undo step.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class RedactionNewOcrUiTests
{
    /// <summary>Two scanned pages, each with an old text recognition naming „Mustermann“ at the top left (x 72–139, y 760–772).</summary>
    private static byte[] File()
    {
        const string Page = "q 595 0 0 842 0 0 cm /Im Do Q q BT 3 Tr /F1 12 Tf 72 760 Td (Mustermann {0}) Tj ET Q";
        return CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R 5 0 R]/Count 2/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Font<</F1 3 0 R>>/XObject<</Im 8 0 R>>>>>>",
            [5] = "<</Type/Page/Parent 2 0 R/Contents 7 0 R/Resources<</Font<</F1 3 0 R>>/XObject<</Im 8 0 R>>>>>>",
            [6] = ("", string.Format(Page, "Eins")),
            [7] = ("", string.Format(Page, "Zwei")),
            [8] = ("/Type/XObject/Subtype/Image/Width 4/Height 4/BitsPerComponent 8/ColorSpace/DeviceGray", new string('æ', 16)),
        });
    }

    /// <summary>Reads „Neu“ on every page, and notes how dark the image is where the bar on page 1 lies.</summary>
    private sealed class Reader : ITextRecognizer
    {
        public List<byte> UnderTheBar { get; } = [];

        public IReadOnlyList<OcrLine> Recognize(OcrImage image, CancellationToken cancel = default)
        {
            var s = image.Width / 595.0;
            int x = (int)(90 * s), y = (int)((842 - 766) * s);
            UnderTheBar.Add(image.Rgba[(y * image.Width + x) * 4]);
            OcrPoint[] box = [new(72 * s, 300 * s), new(120 * s, 300 * s), new(120 * s, 316 * s), new(72 * s, 316 * s)];
            return [new OcrLine([new OcrWord("Neu", box)], 0.95)];
        }

        public void Dispose()
        {
        }
    }

    private static string Text(PdfDocument doc, int page) => PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[page]), 0).Text;

    [Fact]
    public void ShowOcr_TheWholeRecognitionByPage_WithoutVisibleText()
    {
        var pdf = TestFiles.Write(File(), "ocr-anzeigen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                string? shown = null;
                vm.ShowOcrText = (title, text, copy) =>
                {
                    shown = text;
                    var w = new OcrTextWindow(title, text, copy, null);
                    w.Show();
                    w.Close();
                    return Task.CompletedTask;
                };
                await vm.ShowOcrCommand.ExecuteAsync(null);
                Assert.Equal("— Seite 1 —\nMustermann Eins\n\n— Seite 2 —\nMustermann Zwei", shown);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InvisibleText_VisibleTextLeftOut()
    {
        var pdf = CraftedFileTests.Build(new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[4 0 R]/Count 1/MediaBox[0 0 595 842]>>",
            [3] = "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
            [4] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</Font<</F1 3 0 R>>>>>>",
            [6] = ("", "BT /F1 12 Tf 72 700 Td (Sichtbar) Tj ET q BT 3 Tr /F1 12 Tf 72 680 Td (Ocr Zeile) Tj ET Q BT /F1 12 Tf 72 660 Td (Noch sichtbar) Tj ET"),
        });
        using var doc = PdfDocument.Open(pdf);
        Assert.Equal("Ocr Zeile", DocumentViewModel.InvisibleText(PageText.Create(PageInterpreter.BuildText(doc, doc.Pages[0]), 0)));
    }

    [Fact]
    public void Window_CheckedWithModels_GreyedWhenTheOldOneStays_WithoutModelsNothingToChoose()
    {
        UiSession.Run(() =>
        {
            var question = new RedactionQuestion(1, 1, null, CanChooseImages: true)
            {
                OcrPages = [0, 1], InstalledOcr = () => [OcrModelSize.Small], OcrSize = OcrModelSize.Medium,
            };
            var window = new RedactionWindow(question);
            // the wished size is not there: small
            Assert.Equal(OcrModelSize.Small, window.Reply(RedactionAnswer.Apply).NewOcr);
            Assert.False(window.FindControl<RadioButton>("OcrMedium")!.IsEnabled);
            window.FindControl<CheckBox>("RemoveOcr")!.IsChecked = false;
            Assert.False(window.FindControl<CheckBox>("NewOcr")!.IsEnabled);
            Assert.Null(window.Reply(RedactionAnswer.Apply).NewOcr);
            // pages as images need it as well
            window.FindControl<CheckBox>("AsImages")!.IsChecked = true;
            Assert.Equal(OcrModelSize.Small, window.Reply(RedactionAnswer.Apply).NewOcr);

            // the same choice right under „als Bild flachlegen“ (wish of 4.11.2026): ticked there, ticked above, and back
            var below = window.FindControl<CheckBox>("AsImagesOcr")!;
            Assert.True(below.IsEnabled);
            Assert.True(below.IsChecked);
            below.IsChecked = false;
            Assert.False(window.FindControl<CheckBox>("NewOcr")!.IsChecked);
            Assert.Null(window.Reply(RedactionAnswer.Apply).NewOcr);
            below.IsChecked = true;
            Assert.True(window.FindControl<CheckBox>("NewOcr")!.IsChecked);
            Assert.Equal(OcrModelSize.Small, window.Reply(RedactionAnswer.Apply).NewOcr);
            window.FindControl<CheckBox>("AsImages")!.IsChecked = false;
            Assert.False(below.IsEnabled);

            var without = new RedactionWindow(question with { InstalledOcr = () => [] });
            Assert.False(without.FindControl<CheckBox>("AsImagesOcr")!.IsEnabled);
            Assert.False(without.FindControl<CheckBox>("NewOcr")!.IsEnabled);
            Assert.Null(without.FindControl<Button>("LoadModels"));   // nothing to load any more: the models come with the program
            Assert.Null(without.Reply(RedactionAnswer.Apply).NewOcr);

            // a document without a text recognition and no image option: nothing to offer
            var none = new RedactionWindow(new RedactionQuestion(1, 1, null, CanChooseImages: false) { InstalledOcr = () => [OcrModelSize.Small] });
            Assert.False(none.FindControl<StackPanel>("NewOcrPanel")!.IsVisible);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void AfterRedacting_PagesReadAnewFromWhatStaysVisible_OwnUndoStep()
    {
        var pdf = TestFiles.Write(File(), "schwaerzen-neue-ocr.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                Assert.Equal("Mustermann Eins", Text(doc.Document, 0));
                // a bar over the dark spot where „Mustermann“ stands on page 1
                doc.AddElement(0, new RedactionElement { Rect = new PdfRectangle(70, 755, 142, 775) }, "Test");
                var reader = new Reader();
                vm.OpenRecognizer = _ => reader;
                vm.ConfirmRedaction = question =>
                {
                    var dialog = new RedactionWindow(question with { InstalledOcr = () => [OcrModelSize.Small] });
                    Assert.True(dialog.FindControl<CheckBox>("NewOcr")!.IsChecked);
                    return Task.FromResult(dialog.Reply(RedactionAnswer.Apply));
                };
                await vm.ApplyRedactionsCommand.ExecuteAsync(null);
                Assert.True(doc.HasAppliedRedactions, vm.Message);
                // both pages read anew (the old recognition went in the whole document), page 1 with the black bar in the picture
                Assert.Equal(2, reader.UnderTheBar.Count);
                Assert.True(reader.UnderTheBar[0] < 40, $"under the bar: {reader.UnderTheBar[0]}");
                Assert.Equal(["Neu", "Neu"], new[] { Text(doc.Document, 0), Text(doc.Document, 1) });
                Assert.Contains("2 Seiten", vm.Notice);
                // own undo step: back to the redacted pages without any text recognition
                vm.UndoCommand.Execute(null);
                Assert.Equal(["", ""], new[] { Text(doc.Document, 0), Text(doc.Document, 1) });
                Assert.True(doc.HasAppliedRedactions);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
