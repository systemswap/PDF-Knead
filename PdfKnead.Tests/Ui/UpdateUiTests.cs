using Avalonia.Controls;
using Avalonia.VisualTree;
using PdfKnead.Services;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The updates in the window (KONZEPT §5.26): the quiet hint below the ribbon follows the state of the updates — „Laden“ for a version
/// found, „Jetzt neu starten“ when it waits, „Später“ hides it; the card in the settings with the switch. No request leaves the test (the
/// automatic check runs only from the start of the real program).
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class UpdateUiTests
{
    /// <summary>
    /// Off at first; at the first start PDF-Knead asks once (decided on 18.11.2026) — the answer goes into the settings, and the
    /// question does not come again, whatever the answer.
    /// </summary>
    [Fact]
    public void FirstStart_AsksOnce_AnswerSaved()
    {
        Assert.False(new Core.Platform.AppSettings().UpdateCheck);
        UiSession.Run(async () =>
        {
            var before = UserSettings.Current;
            try
            {
                UserSettings.Update(s => s with { UpdatesAsked = false, UpdateCheck = false });
                var vm = new MainViewModel();
                var asked = 0;
                vm.AskUpdates = () =>
                {
                    asked++;
                    return Task.FromResult<bool?>(true);
                };
                await vm.OfferUpdatesAsync();
                Assert.True(UserSettings.Current.UpdateCheck);
                Assert.True(UserSettings.Current.UpdatesAsked);
                await vm.OfferUpdatesAsync();
                Assert.Equal(1, asked);
                // „Nicht suchen“ (or the window closed) is an answer too
                UserSettings.Update(s => s with { UpdatesAsked = false, UpdateCheck = true });
                vm.AskUpdates = () => Task.FromResult<bool?>(null);
                await vm.OfferUpdatesAsync();
                Assert.False(UserSettings.Current.UpdateCheck);
                Assert.True(UserSettings.Current.UpdatesAsked);
                // the window: „Updates einschalten“ answers true
                var owner = new Window();
                owner.Show();
                var window = new FirstUpdateWindow();
                var answer = window.ShowDialog<bool?>(owner);
                UiSession.Capture(window);
                window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "YesButton")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.True(await answer);
                owner.Close();
            }
            finally
            {
                UserSettings.Update(_ => before);
            }
        });
    }

    [Fact]
    public void UpdateHint_FollowsTheState_LaterHidesIt()
    {
        UiSession.Run(() =>
        {
            var updates = PdfKnead.Services.Updates.Instance;
            var (state, message) = (updates.State, updates.Message);
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                UiSession.Capture(window);
                var bar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "UpdateBar");
                Assert.False(bar.IsVisible);
                updates.State = UpdateState.Available;
                updates.Message = "PDF-Knead 9.9.9 ist verfügbar.";
                UiSession.Capture(window);
                Assert.True(bar.IsVisible);
                Button Shown(string text) => bar.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content as string == Tr.T(text));
                Assert.NotNull(Shown("Laden"));
                Assert.Contains(bar.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "PDF-Knead 9.9.9 ist verfügbar.");
                updates.State = UpdateState.Ready;
                UiSession.Capture(window);
                Assert.NotNull(Shown("Jetzt neu starten"));
                Assert.DoesNotContain(bar.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Content as string == Tr.T("Laden"));
                vm.LaterUpdateCommand.Execute(null);
                UiSession.Capture(window);
                Assert.False(bar.IsVisible);
                // the settings: the card with the switch
                vm.ShowBackstageCommand.Execute(BackstagePage.Settings);
                UiSession.Capture(window);
                Assert.Single(window.GetVisualDescendants().OfType<CheckBox>(), c => c.Name == "UpdateCheckOption");
            }
            finally
            {
                updates.State = state;
                updates.Message = message;
                window.Close();
            }
        });
    }
}
