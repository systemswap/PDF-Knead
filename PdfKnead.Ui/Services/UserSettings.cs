using Avalonia;
using Avalonia.Styling;

namespace PdfKnead.Services;

/// <summary>
/// The user's settings at run time: loaded once, every change saved immediately.
/// Tests redirect <see cref="File"/> to a folder of their own.
/// </summary>
public static class UserSettings
{
    public static string File { get; set; } = AppSettings.DefaultFile;

    public static AppSettings Current { get; private set; } = new();

    public static void Load() => Current = AppSettings.Load(File);

    /// <summary>
    /// Applies a change to the settings and saves them immediately. The change goes onto the settings as the file holds them now, not onto
    /// this window's copy: another window of the program may have changed them meanwhile, and writing the old copy back would undo that
    /// (FG 867: „Dateien öffnen“ switched in one window, back again after another saved its recent files). If the file cannot be read just
    /// now, the copy of this window counts.
    /// </summary>
    /// <param name="change">Returns the changed settings from the current ones.</param>
    public static void Update(Func<AppSettings, AppSettings> change)
    {
        Current = change(AppSettings.TryLoad(File) ?? Current);
        Current.Save(File);
    }

    /// <summary>Applies the saved language, otherwise that of the system.</summary>
    public static void ApplyLanguage()
    {
        var all = Languages.All(out _);
        var language = all.FirstOrDefault(l => l.Code == Current.Language) ?? Languages.ForSystem(all);
        Tr.Use(language);
    }

    /// <summary>
    /// Applies the colour scheme. PDF Knead resolves “As system” to light or dark itself and follows changes of the system: Avalonia
    /// (12.1) resolves <see cref="ThemeVariant.Default"/> only at start-up; when switched to it from dark at run time, no colour found
    /// its value afterwards and texts and backgrounds became transparent. See docs/FEHLERGEDAECHTNIS.md, No. 70.
    /// </summary>
    public static void ApplyTheme()
    {
        if (Application.Current is not { } app) return;
        if (app.PlatformSettings is { } platform && !_following)
        {
            _following = true;
            platform.ColorValuesChanged += (_, _) =>
            {
                if (Current.Theme is not (ThemeChoice.Light or ThemeChoice.Dark)) ApplyTheme();
            };
        }
        app.RequestedThemeVariant = Current.Theme switch
        {
            ThemeChoice.Light => ThemeVariant.Light,
            ThemeChoice.Dark => ThemeVariant.Dark,
            _ => SystemTheme(app),
        };
    }

    private static bool _following;

    /// <summary>Returns light or dark as set in the system (light if the system gives no value).</summary>
    /// <param name="app">The application whose platform settings are read.</param>
    /// <returns><see cref="ThemeVariant.Dark"/> or <see cref="ThemeVariant.Light"/>.</returns>
    public static ThemeVariant SystemTheme(Application app) =>
        app.PlatformSettings?.GetColorValues().ThemeVariant == Avalonia.Platform.PlatformThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
}
