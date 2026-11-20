using Avalonia.Controls;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>„Suchen und schwärzen“ names the pages whose text cannot be read (docs/RECHERCHE-VERSTECKEN.md, B-6) or reads suspiciously (B-5).</summary>
[Collection(nameof(UiCollection))]
public sealed class UnreadableTextUiTests
{
    [Fact]
    public void SearchRedactWindow_NamesThePage()
    {
        var pdf = TestFiles.Write(UnreadableTextTests.File(), "nicht-lesbar.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                Assert.Equal([0], await doc.UnreadablePagesAsync());
                var dialog = new SearchRedactWindow((_, _, _) => Task.FromResult(new List<RedactionHit>()), () => doc.UnreadablePagesAsync());
                dialog.ShowUnreadable(await doc.UnreadablePagesAsync());
                var note = dialog.FindControl<TextBlock>("UnreadableText")!;
                Assert.True(note.IsVisible);
                Assert.Contains("1", note.Text);
                dialog.ShowUnreadable([]);
                Assert.False(note.IsVisible);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }

    [Fact]
    public void SearchRedactWindow_NamesThePageWithSuspiciousText()
    {
        var pdf = TestFiles.Write(MisreadTextTests.File(["x", "x", "x", "x", "x", "x"], "010203040506"), "auffaellig.pdf");
        UiSession.Run(async () =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 800 };
            window.Show();
            try
            {
                await vm.OpenAsync([pdf]);
                var doc = vm.SelectedDocument!;
                Assert.Equal([0], await doc.MisreadPagesAsync());
                Assert.Empty(await doc.UnreadablePagesAsync());
                var dialog = new SearchRedactWindow((_, _, _) => Task.FromResult(new List<RedactionHit>()));
                dialog.ShowMisread(await doc.MisreadPagesAsync());
                var note = dialog.FindControl<TextBlock>("MisreadText")!;
                Assert.True(note.IsVisible);
                Assert.Contains("Seite 1", note.Text);
                dialog.ShowMisread([]);
                Assert.False(note.IsVisible);
            }
            finally
            {
                vm.AskSaveChanges = _ => Task.FromResult(SaveChoice.Discard);
                window.Close();
            }
        });
    }
}
