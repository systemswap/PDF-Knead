using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using PdfKnead.Core.Platform;
using PdfKnead.Services;
using PdfKnead.ViewModels;

namespace PdfKnead.Tests.Ui;

/// <summary>
/// Tests for switching the colour scheme at run time. After Dark → "Like the system" the scheme must be resolved; otherwise texts and
/// backgrounds become transparent, and labels are missing from the screenshots of all later windows.
/// See docs/FEHLERGEDAECHTNIS.md, No. 70.
/// </summary>
[Collection(nameof(UiCollection))]
public sealed class ThemeTests
{
    [Fact]
    public void BackToSystemTheme_ResolvesColours() => UiSession.Run(() =>
    {
        var app = Application.Current!;
        var settings = new SettingsViewModel();
        var before = settings.SelectedTheme;
        try
        {
            foreach (var choice in new[] { ThemeChoice.Dark, ThemeChoice.System, ThemeChoice.Light, ThemeChoice.System })
            {
                settings.SelectedTheme = settings.Themes.First(t => t.Choice == choice);
                var window = new Window { Content = new TextBlock { Text = "x" } };
                window.Show();
                try
                {
                    var expected = choice switch
                    {
                        ThemeChoice.Dark => ThemeVariant.Dark,
                        ThemeChoice.Light => ThemeVariant.Light,
                        _ => UserSettings.SystemTheme(app),
                    };
                    Assert.Equal(expected, window.ActualThemeVariant);
                    Assert.True(window.TryFindResource("TextBrush", window.ActualThemeVariant, out var text), $"{choice}: TextBrush");
                    Assert.True(window.TryFindResource("AppBackgroundBrush", window.ActualThemeVariant, out var background), $"{choice}: AppBackgroundBrush");
                    Assert.Equal(255, ((SolidColorBrush)text!).Color.A);
                    Assert.Equal(255, ((SolidColorBrush)background!).Color.A);
                }
                finally
                {
                    window.Close();
                }
            }
        }
        finally
        {
            settings.SelectedTheme = before;
            UserSettings.ApplyTheme();
        }
    });
}
