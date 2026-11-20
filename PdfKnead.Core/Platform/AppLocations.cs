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
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(string.IsNullOrEmpty(local) ? Path.GetTempPath() : local, "PDF-Knead");
        }
    }

    /// <summary>
    /// The AppImage runtime sets the path of the AppImage file in $APPIMAGE; without an AppImage,
    /// the program folder applies.
    /// </summary>
    internal static string Resolve(string? appImage, string baseDirectory) =>
        !string.IsNullOrEmpty(appImage) && Path.GetDirectoryName(appImage) is { Length: > 0 } dir
            ? dir
            : baseDirectory;
}
