namespace PdfKnead.Core.Platform;

/// <summary>
/// Where the program is located and where it stores its data. Custom language files (languages/)
/// and the log lie next to the program; for an AppImage next to the .AppImage file, because its
/// contents are read-only and are mounted under a different path on every start (/tmp/.mount_…).
/// </summary>
public static class AppLocations
{
    /// <summary>Gets the folder of the .exe or of the .AppImage file.</summary>
    public static string ProgramFolder { get; } =
        Resolve(Environment.GetEnvironmentVariable("APPIMAGE"), AppContext.BaseDirectory);

    /// <summary>Gets the program's per-user data folder: ~/.local/share/PDF-Knead or %LOCALAPPDATA%\PDF-Knead.</summary>
    /// <remarks>Falls back to the temporary folder if the local application data folder is unknown. The folder is not created here.</remarks>
    public static string UserDataFolder
    {
        get
        {
            if (UserDataFolderForTests is { } tests) return tests;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(string.IsNullOrEmpty(local) ? Path.GetTempPath() : local, "PDF-Knead");
        }
    }

    /// <summary>
    /// A folder the test project sets before anything else runs, so that no test reads or writes the user's own data — settings, the own
    /// signing certificate, device keys, redaction codes, trust lists, signature fonts, update state (before, a test started on its own could
    /// write test values into the user's settings; docs/FEHLERGEDAECHTNIS.md, No. 1240). Null in the program.
    /// </summary>
    internal static string? UserDataFolderForTests { get; set; }

    /// <summary>
    /// The AppImage runtime sets the path of the AppImage file in $APPIMAGE; without an AppImage,
    /// the program folder applies.
    /// </summary>
    internal static string Resolve(string? appImage, string baseDirectory) =>
        !string.IsNullOrEmpty(appImage) && Path.GetDirectoryName(appImage) is { Length: > 0 } dir
            ? dir
            : baseDirectory;
}
