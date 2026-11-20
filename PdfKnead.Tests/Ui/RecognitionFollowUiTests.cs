using Avalonia;
using Avalonia.VisualTree;
using PdfKnead.Controls;
using PdfKnead.Core.Ocr;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Text recognition shows where it is (KONZEPT.md, section 5.9; wish of 30.10.2026): the page being read framed, the lines being read in green, and
/// the view goes along — unless the user scrolls; it follows again with the button or when the user comes back to the page being read.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class RecognitionFollowUiTests
{
    /// <summary>Six pages with an image.</summary>
    private static byte[] File()
    {
        const string Scan = "q 595 0 0 842 0 0 cm /Im Do Q";
        var objects = new Dictionary<int, object>
        {
            [1] = "<</Type/Catalog/Pages 2 0 R>>",
            [2] = "<</Type/Pages/Kids[10 0 R 11 0 R 12 0 R 13 0 R 14 0 R 15 0 R]/Count 6/MediaBox[0 0 595 842]>>",
            [6] = ("", Scan),
            [8] = ("/Type/XObject/Subtype/Image/Width 4/Height 4/BitsPerComponent 8/ColorSpace/DeviceGray", new string('æ', 16)),
        };
        for (var i = 10; i <= 15; i++) objects[i] = "<</Type/Page/Parent 2 0 R/Contents 6 0 R/Resources<</XObject<</Im 8 0 R>>>>>>";
        return CraftedFileTests.Build(objects);
    }

    /// <summary>Tells how far it has come on every page and waits there until the test lets it go on.</summary>
    private sealed class Gated : ITextRecognizer
    {
        public readonly SemaphoreSlim Next = new(0);
        public int Page;

        public IReadOnlyList<OcrLine> Recognize(OcrImage image, CancellationToken cancel = default) => Recognize(image, null, cancel);

        public IReadOnlyList<OcrLine> Recognize(OcrImage image, Action<OcrProgress>? progress, CancellationToken cancel = default)
        {
            Page++;
            var s = image.Width / 595.0;
            OcrPoint[] Box(double y) => [new(72 * s, y * s), new(300 * s, y * s), new(300 * s, (y + 16) * s), new(72 * s, (y + 16) * s)];
            progress?.Invoke(new OcrProgress([Box(100), Box(140), Box(180)], 1, 1));
            while (!Next.Wait(20, CancellationToken.None))
                if (cancel.IsCancellationRequested) throw new OperationCanceledException(cancel);
            return [new OcrLine([new OcrWord("Seite", Box(100))], 0.97)];
        }

        public void Dispose() { }
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 1000 && !done(); i++) await Task.Delay(10);
        Assert.True(done());
    }

    [Fact]
    public void Follows_UntilTheUserScrolls_BackByButtonOrByComingBack()
    {
        var pdf = TestFiles.Write(File(), "texterkennung-mitlaufen.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            var reader = new Gated();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                vm.FitWidthCommand.Execute(null);
                UiSession.Capture(window);
                var view = window.GetVisualDescendants().OfType<DocumentViewer>().Single().View;
                vm.OpenRecognizer = _ => reader;
                vm.ShowRecognizeText = _ => Task.FromResult(true);
                var run = vm.RecognizeTextCommand.ExecuteAsync(null);

                // page 1: the place known, the second line of three being read, nothing written yet
                await Until(() => doc.RecognitionPlace is { Page: 0, Lines.Count: 1 });
                Assert.True(doc.RecognitionFollows);
                var line = doc.RecognitionPlace!.Value.Lines[0];
                Assert.Equal(842 - 156, line.Top, 0);   // a RectD counts from the smaller value

                // the view goes along by itself: this does not count as the user scrolling
                reader.Next.Release();
                await Until(() => doc.RecognitionPlace is { Page: 1, Lines.Count: 1 });
                await Until(() => ReferenceEquals(doc.CurrentPage, doc.Pages[1]));
                Assert.True(doc.RecognitionFollows);

                // the user scrolls within the page: it stays where it is
                var scrolled = view.Offset + new Vector(0, 40);
                view.Offset = scrolled;
                Assert.False(doc.RecognitionFollows);
                if (UiSession.ScreenshotFolder() is { } folder)
                    Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)?.Save(Path.Combine(folder, "texterkennung-mitlaufen.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                reader.Next.Release();
                await Until(() => doc.RecognitionPlace?.Page == 2);
                await Task.Delay(100);
                Assert.Equal(scrolled.Y, view.Offset.Y);

                // the button: following again, at once
                doc.FollowRecognitionCommand.Execute(null);
                Assert.True(doc.RecognitionFollows);
                await Until(() => ReferenceEquals(doc.CurrentPage, doc.Pages[2]));

                // away from the page being read and back: following again
                view.Offset = new Vector(view.Offset.X, 0);
                Assert.False(doc.RecognitionFollows);
                doc.ScrollToPage(2);
                Assert.True(doc.RecognitionFollows);

                // finished: nothing shown any more, following is on for the next run
                for (var i = 0; i < 4; i++) reader.Next.Release();
                await run;
                Assert.Null(doc.RecognitionPlace);
                Assert.True(doc.RecognitionFollows);
                Assert.Equal(6, reader.Page);
            }
            finally
            {
                reader.Next.Release(100);
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
