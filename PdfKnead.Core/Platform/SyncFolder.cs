namespace PdfKnead.Core.Platform;

/// <summary>
/// Detects whether a file lies in a folder that a cloud service synchronises (OneDrive, Dropbox, Google Drive, Nextcloud, iCloud).
/// Overwritten versions remain in the version history and recycle bin of such a service, which matters when redacting over the
/// original file.
/// </summary>
public static class SyncFolder
{
    private static readonly string[] Names = ["Dropbox", "Google Drive", "My Drive", "GoogleDrive", "Nextcloud", "ownCloud", "iCloudDrive", "iCloud Drive"];

    /// <summary>Determines whether the given path lies in a synchronised cloud folder.</summary>
    /// <param name="path">Path of the file or folder; relative paths are resolved against the current directory.</param>
    /// <returns>
    /// <see langword="true"/> if the path lies under a root named by the environment variables <c>OneDrive</c>, <c>OneDriveCommercial</c>
    /// or <c>OneDriveConsumer</c>, or if one of its path segments starts with “OneDrive” or matches a known service folder name
    /// (case-insensitive); otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>A heuristic based on folder names only; it does not query the sync clients.</remarks>
    public static bool Contains(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var variable in (ReadOnlySpan<string>)["OneDrive", "OneDriveCommercial", "OneDriveConsumer"])
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } root && Under(full, root)) return true;
        var parts = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) || Names.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    private static bool Under(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
