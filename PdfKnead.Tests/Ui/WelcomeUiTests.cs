using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using PdfKnead.Core.Localization;
using PdfKnead.Core.Platform;
using PdfKnead.Services;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The first start in one window (decided on 24.11.2026): the language first, preselected as the program started and switched at once —
/// the texts of the window follow —, then the name for the own certificate and the question about updates. The choices go into the
/// settings; closing counts as an answer (nothing created, updates off); the window comes once.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class WelcomeUiTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"willkommen-{Guid.NewGuid():N}.p12");

    [Fact]
    public void FirstStart_ChoicesSaved_ShownOnce()
    {
        var file = TempFile();
        var before = UserSettings.Current;
        var language = Tr.Current;
        UiSession.Run(async () =>
        {
            try
            {
                UserSettings.Update(s => s with { CertificateOffered = false, UpdatesAsked = false, UpdateCheck = false, SignatureName = null, Language = null });
                var vm = new MainViewModel();
                var shown = 0;
                vm.ShowWelcome = model =>
                {
                    shown++;
                    Assert.True(model.AskName);
                    Assert.True(model.AskUpdates);
                    Assert.False(model.UpdatesOn);   // off at first
                    model.SelectedLanguage = model.Languages.Single(l => l.Code == "en");
                    Assert.Equal("en", Tr.Current.Code);   // switched at once
                    model.Name = "  Erika Mustermann ";
                    model.UpdatesOn = true;
                    return Task.FromResult(true);
                };
                await vm.OfferWelcomeAsync(file);
                Assert.Equal(1, shown);
                Assert.Equal("en", UserSettings.Current.Language);
                using (var own = SigningCertificates.LoadOwn(file))
                    Assert.Equal("Erika Mustermann", own!.Name);
                Assert.Equal("Erika Mustermann", UserSettings.Current.SignatureName);
                Assert.True(UserSettings.Current.UpdateCheck);
                Assert.True(UserSettings.Current.CertificateOffered);
                Assert.True(UserSettings.Current.UpdatesAsked);

                await vm.OfferWelcomeAsync(file);   // once
                Assert.Equal(1, shown);

                // closed instead of „Los geht’s“: nothing created, updates off though ticked, asked anyway; an existing certificate is not offered again
                var other = TempFile();
                UserSettings.Update(s => s with { CertificateOffered = false, UpdatesAsked = false, UpdateCheck = true });
                vm.ShowWelcome = model =>
                {
                    Assert.True(model.AskName);
                    Assert.Equal("Erika Mustermann", model.Name);   // the name known is offered
                    model.UpdatesOn = true;
                    return Task.FromResult(false);   // closed, not „Los geht’s“
                };
                await vm.OfferWelcomeAsync(other);
                Assert.False(File.Exists(other));
                Assert.False(UserSettings.Current.UpdateCheck);
                Assert.True(UserSettings.Current.CertificateOffered && UserSettings.Current.UpdatesAsked);
                UserSettings.Update(s => s with { CertificateOffered = false, UpdatesAsked = false });
                vm.ShowWelcome = model =>
                {
                    Assert.False(model.AskName);   // the own certificate exists
                    return Task.FromResult(true);
                };
                await vm.OfferWelcomeAsync(file);
            }
            finally
            {
                Tr.Use(language);
                UserSettings.Update(_ => before);
            }
        });
    }

    /// <summary>The window in German and, after choosing English, in English — its texts follow the language at once; „Los geht’s“ closes it.
    /// With PDFKNEAD_SCREENSHOTS both pictures are kept for the acceptance.</summary>
    [Fact]
    public void Window_LanguageSwitchesTheTexts_StartCloses()
    {
        var language = Tr.Current;
        UiSession.Run(async () =>
        {
            try
            {
                Tr.Use(Language.German);
                var model = new WelcomeViewModel(Core.Localization.Languages.All(out _), null, askName: true, askUpdates: true);
                var owner = new Window();
                owner.Show();
                var window = new WelcomeWindow(model);
                var closed = window.ShowDialog<bool?>(owner);
                string Heading() => window.GetVisualDescendants().OfType<TextBlock>().First(t => t.FontSize == 15).Text ?? "";
                var german = UiSession.Capture(window);
                Assert.Equal("Willkommen bei PDF-Knead", Heading());
                model.SelectedLanguage = model.Languages.Single(l => l.Code == "en");
                var english = UiSession.Capture(window);
                Assert.Equal(Tr.T("Willkommen bei PDF-Knead"), Heading());
                Assert.NotEqual("Willkommen bei PDF-Knead", Heading());
                if (UiSession.ScreenshotFolder() is { } folder)
                {
                    german.Save(Path.Combine(folder, "78-willkommen-deutsch.png"), PngBitmapEncoderOptions.Default);
                    english.Save(Path.Combine(folder, "78-willkommen-englisch.png"), PngBitmapEncoderOptions.Default);
                }
                window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StartButton")
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Assert.True(await closed);
                owner.Close();
            }
            finally
            {
                Tr.Use(language);
            }
        });
    }
}
