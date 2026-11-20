using Avalonia.Controls;
using Avalonia.Media.Imaging;
using PdfKnead.Tests.Pdf;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The window „EU-Vertrauensliste“ (KONZEPT.md, section 5.17): loading on a click with stand-in lists, the state with date and countries, a
/// refused list of lists that leaves the kept one, removing.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class TrustListUiTests
{
    [Fact]
    public void Load_State_Refused_Remove()
    {
        using var world = new TrustListTests.World();
        UiSession.Run(async () =>
        {
            var model = new TrustListViewModel(world.Folder) { Source = new TrustListTests.Source(world.Files), Anchors = world.Anchors, Lotl = new Uri(TrustListTests.Lotl) };
            var window = new TrustListWindow(model);
            window.Show();
            try
            {
                Assert.False(model.HasLists);
                Assert.StartsWith(Tr.T("Nicht geladen."), model.State);
                await model.LoadCommand.ExecuteAsync(null);
                Assert.False(model.IsError, model.Message);
                Assert.True(model.HasLists, model.Message);
                Assert.True(model.Changed);
                Assert.Contains(Tr.T("{0} Länder geprüft", 2), model.State);
                if (UiSession.ScreenshotFolder() is { } folder)
                    UiSession.Capture(window).Save(Path.Combine(folder, "78-eu-vertrauensliste.png"), PngBitmapEncoderOptions.Default);

                // a list of lists by someone else: refused, the kept one stays
                using var stranger = new TrustListTests.Party("Fremder");
                var good = world.Files[TrustListTests.Lotl];
                world.Files[TrustListTests.Lotl] = good[..^40];
                await model.LoadCommand.ExecuteAsync(null);
                Assert.True(model.IsError);
                Assert.True(model.HasLists);
                world.Files[TrustListTests.Lotl] = good;

                model.RemoveCommand.Execute(null);
                Assert.False(model.HasLists);
                Assert.False(Directory.Exists(world.Folder));
            }
            finally
            {
                window.Close();
            }
        });
    }
}
