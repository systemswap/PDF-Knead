using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PdfKnead.ViewModels;
using PdfKnead.Views;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// The settings in tabs instead of one long list (decided on 21.11.2026): each card in its tab, the permissions with a plain description, and
/// nothing left of the license key, the kind of use or the introductory period (PDF-Knead is free for everyone since 21.11.2026).
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class SettingsTabsUiTests
{
    /// <summary>Shows one tab of the settings (the settings page must be open).</summary>
    internal static void Select(Window window, string tabName)
    {
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "SettingsTabs");
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(t => t.Name == tabName);
        Dispatcher.UIThread.RunJobs();
    }

    private static List<string?> Sections(Window window) =>
        window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "SettingsTabs").GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("section") && t.IsEffectivelyVisible).Select(t => t.Text).ToList();

    [Fact]
    public void EveryCardInItsTab_PermissionsExplained_NoLicenseLeft()
    {
        UiSession.Run(() =>
        {
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            try
            {
                vm.ShowBackstageCommand.Execute(BackstagePage.Settings);
                Dispatcher.UIThread.RunJobs();
                var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "SettingsTabs");
                Assert.Equal([Tr.T("Allgemein"), Tr.T("Schwärzen"), Tr.T("Updates und Datenschutz"), Tr.T("Nutzungsberechtigung")],
                    tabs.Items.OfType<TabItem>().Select(t => t.Header as string));

                Select(window, "GeneralSettingsTab");
                Assert.Contains(Tr.T("SPRACHE"), Sections(window));
                Assert.Contains(Tr.T("ERSCHEINUNGSBILD"), Sections(window));
                Assert.Contains(Tr.T("DATEIEN ÖFFNEN"), Sections(window));
                Assert.DoesNotContain(Tr.T("UPDATES"), Sections(window));

                Select(window, "RedactionSettingsTab");
                Assert.Equal([Tr.T("SCHWÄRZUNGSCODES")], Sections(window));

                Select(window, "UpdateSettingsTab");
                Assert.Equal([Tr.T("UPDATES"), Tr.T("DATENSCHUTZ")], Sections(window));

                Select(window, "PermissionSettingsTab");
                Assert.Equal([Tr.T("NUTZUNGSBERECHTIGUNG")], Sections(window));
                var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
                Assert.Contains(texts, t => t.StartsWith(Tr.T("Hier sehen Sie, welche Funktionen"), StringComparison.Ordinal));
                var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => b.Content as string).ToList();
                Assert.Contains(Tr.T("Berechtigungen anzeigen"), buttons);
                Assert.Contains(Tr.T("Funktionsdatei einspielen …"), buttons);
                Assert.DoesNotContain(buttons, b => b is not null && (b.Contains("Lizenz", StringComparison.Ordinal) || b.Contains("Nutzungsart", StringComparison.Ordinal)));
                Assert.DoesNotContain(texts, t => t.Contains("Einführungszeit", StringComparison.Ordinal) || t.Contains("Lizenzschlüssel", StringComparison.Ordinal));
                var shot = UiSession.Capture(window);
                if (UiSession.ScreenshotFolder() is { } folder)
                    shot.Save(Path.Combine(folder, "einstellungen-nutzungsberechtigung.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            finally
            {
                window.Close();
            }
            return Task.CompletedTask;
        });
    }
}
