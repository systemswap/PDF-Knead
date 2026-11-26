using System.Collections.Concurrent;
using System.Security.Cryptography;
using PdfKnead.Core.Platform;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Handwriting fonts for signatures. The free ones are embedded in the program (folder "Schriften/Handschriften-frei"); fonts the
/// user adds are files in the folder "Schriften-Unterschrift" next to the program (the „Handschriften“ window (File → Fonts) adds
/// copies to it and removes them, so the program stays portable and the fonts move with it). No licensed font comes with the program. Only if that folder is not writable does an import go to the user folder. Signatures are saved as
/// outlines, never as a font: the font file itself never ends up in a PDF file.
/// </summary>
/// <remarks>Safe to call from rendering threads and the user interface at the same time.</remarks>
public static class SignatureFonts
{
    /// <summary>Name of the font folder next to the program and in the user folder.</summary>
    public const string FolderName = "Schriften-Unterschrift";

    /// <summary>Maximum font file size in bytes; larger files are not accepted (colour fonts with high-resolution images take several MB).</summary>
    public const long MaxFileSize = 64L << 20;

    /// <summary>Substitute when a signature names a font that does not exist (any more).</summary>
    internal const string Fallback = "PatrickHand-Regular.ttf";

    private const string EmbeddedPrefix = "Schriften/Handschriften/";

    /// <summary>For tests only: folder used instead of the user folder.</summary>
    internal static string? UserFolderForTests;

    private static readonly IReadOnlyList<string> EmbeddedNames =
        typeof(SignatureFonts).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(EmbeddedPrefix, StringComparison.Ordinal))
            .Select(n => n[EmbeddedPrefix.Length..])
            .ToList();

    // Parsed fonts: embedded ones under their name, files under path, modification time and size (a font that was removed and
    // imported again under the same name is a different font)
    private static readonly ConcurrentDictionary<string, OpenTypeFont> Parsed = new(StringComparer.Ordinal);

    // Removed fonts, kept until the program exits, so that signatures in open documents do not suddenly change their look
    private static readonly ConcurrentDictionary<string, OpenTypeFont> Removed = new(StringComparer.Ordinal);

    // One object (assigned atomically): rendering threads and the user interface ask at the same time
    private sealed record Listing(IReadOnlyList<string> Names, IReadOnlyDictionary<string, FolderFont> Paths);

    private sealed record FolderFont(string Path, string Key);

    private static Listing? _listing;

    /// <summary>Fallback target of an import when the folder next to the program is not writable.</summary>
    public static string UserFolder => UserFolderForTests ?? Path.Combine(AppLocations.UserDataFolder, FolderName);

    /// <summary>Folder next to the program (for an AppImage next to the .AppImage file): the target of an import.</summary>
    public static string ProgramFolder => Path.Combine(AppLocations.ProgramFolder, FolderName);

    /// <summary>File names of all usable handwriting fonts, in alphabetical order.</summary>
    /// <remarks>The folders are scanned on first use and again after <see cref="Refresh"/>.</remarks>
    public static IReadOnlyList<string> Names => (_listing ??= Scan()).Names;

    /// <summary>Returns where a handwriting font comes from: embedded, the folder next to the program or the user folder.</summary>
    /// <param name="file">File name from <see cref="Names"/>.</param>
    /// <returns>The origin; <see cref="SignatureFontOrigin.Folder"/> for names that are neither embedded nor in the user folder.</returns>
    public static SignatureFontOrigin Origin(string file)
    {
        if (EmbeddedNames.Contains(file)) return SignatureFontOrigin.Embedded;
        return (_listing ??= Scan()).Paths.TryGetValue(file, out var entry)
               && Path.GetDirectoryName(entry.Path) is { } dir && SameFolder(dir, UserFolder)
            ? SignatureFontOrigin.UserFolder
            : SignatureFontOrigin.Folder;
    }

    /// <summary>Returns the folder that "Open folder" shows: next to the program, or the user folder if that one cannot be created.</summary>
    /// <returns>The folder, which is created if necessary, or null if neither folder can be created.</returns>
    public static string? FolderToShow()
    {
        foreach (var folder in (string[])[ProgramFolder, UserFolder])
            try
            {
                Directory.CreateDirectory(folder);
                return folder;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // try the next one
            }
        return null;
    }

    /// <summary>All error texts of import and removal (German; the user interface translates them), for checking the translations.</summary>
    public static IReadOnlyList<string> Errors { get; } =
    [
        "Nur TrueType- oder OpenType-Schriften (.ttf, .otf) lassen sich importieren.", "Die Schriftdatei ist zu groß (mehr als 64 MB).",
        "Die Datei ließ sich nicht lesen.", "Die Datei ist keine brauchbare Schrift.",
        "Die Schrift ließ sich nicht in den Ordner kopieren.", "Eingebaute Schriften lassen sich nicht entfernen.",
        "Die Schrift ließ sich nicht entfernen.",
    ];

    /// <summary>
    /// Deletes a font from its folder (the „Handschriften“ window). Signatures in open documents that already use it keep it until the program
    /// exits; it is no longer offered for selection.
    /// </summary>
    /// <param name="file">File name from <see cref="Names"/>.</param>
    /// <returns>An error text (German, one of <see cref="Errors"/>), or null on success or if the font is not in any folder.</returns>
    public static string? Remove(string file)
    {
        if (EmbeddedNames.Contains(file)) return "Eingebaute Schriften lassen sich nicht entfernen.";
        if (!(_listing ??= Scan()).Paths.TryGetValue(file, out var entry)) return null;
        var font = TryGet(file);
        try
        {
            File.Delete(entry.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "Die Schrift ließ sich nicht entfernen.";
        }
        if (font is not null) Removed[file] = font;
        Parsed.TryRemove(entry.Key, out _);
        Refresh();
        return null;
    }

    /// <summary>Makes the next access scan the folders again (after an import, or when files were added to a folder).</summary>
    public static void Refresh() => _listing = null;

    /// <summary>Returns a handwriting font (parsed once, then shared); the substitute if it is missing or damaged.</summary>
    /// <param name="file">File name from <see cref="Names"/>.</param>
    /// <returns>The font or the substitute.</returns>
    public static OpenTypeFont Get(string file) => TryGet(file) ?? TryGet(Fallback) ?? Parse(LoadEmbedded(EmbeddedNames[0]));

    /// <summary>Returns the bytes of a font file (of the substitute if the file is missing).</summary>
    /// <param name="file">File name from <see cref="Names"/>.</param>
    /// <returns>The font file.</returns>
    public static byte[] Load(string file)
    {
        if (EmbeddedNames.Contains(file)) return LoadEmbedded(file);
        if ((_listing ??= Scan()).Paths.TryGetValue(file, out var entry) && ReadFile(entry.Path) is { } data) return data;
        return LoadEmbedded(Fallback);
    }

    private static OpenTypeFont? TryGet(string file)
    {
        if (Parsed.TryGetValue(file, out var known)) return known;
        if (EmbeddedNames.Contains(file)) return Parsed.GetOrAdd(file, f => Parse(LoadEmbedded(f)));
        if (!(_listing ??= Scan()).Paths.TryGetValue(file, out var entry)) return Removed.GetValueOrDefault(file);
        if (Parsed.TryGetValue(entry.Key, out known)) return known;
        return ReadFile(entry.Path) is { } data && TryParse(data) is { } font ? Parsed.GetOrAdd(entry.Key, font) : null;
    }

    // ------------------------------------------------------------ Import

    /// <summary>
    /// Copies a font file into the folder "Schriften-Unterschrift" next to the program (the original stays where it is; the program is
    /// portable and the fonts move with it). If that folder is not writable (under "Program Files", a write-protected USB stick), the
    /// font goes to the user folder. A name that already exists becomes "Name (2).ttf"; if the same file is already there, nothing is
    /// copied and <see cref="SignatureFontImport.AlreadyThere"/> is set.
    /// </summary>
    /// <param name="source">Path of a .ttf or .otf file.</param>
    /// <returns>The result with file name and family, or with a German error text from <see cref="Errors"/>.</returns>
    public static SignatureFontImport Import(string source) => Import(source, [ProgramFolder, UserFolder]);

    /// <summary>Imports into the first of <paramref name="folders"/> that is writable.</summary>
    internal static SignatureFontImport Import(string source, IReadOnlyList<string> folders)
    {
        var extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension is not (".ttf" or ".otf")) return SignatureFontImport.Failed("Nur TrueType- oder OpenType-Schriften (.ttf, .otf) lassen sich importieren.");
        byte[] data;
        try
        {
            if (new FileInfo(source).Length > MaxFileSize) return SignatureFontImport.Failed("Die Schriftdatei ist zu groß (mehr als 64 MB).");
            data = File.ReadAllBytes(source);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return SignatureFontImport.Failed("Die Datei ließ sich nicht lesen.");
        }
        if (TryParse(data) is not { } font) return SignatureFontImport.Failed("Die Datei ist keine brauchbare Schrift.");

        // Is the same file already there (embedded, next to the program or imported before)?
        var hash = SHA256.HashData(data);
        foreach (var name in Names)
            if (Load(name) is var existing && existing.Length == data.Length && SHA256.HashData(existing).AsSpan().SequenceEqual(hash))
                return new SignatureFontImport(name, Display(font), null, true);
        foreach (var path in folders.SelectMany(FilesIn))
            if (ReadFile(path) is { } existing && existing.Length == data.Length && SHA256.HashData(existing).AsSpan().SequenceEqual(hash))
                return new SignatureFontImport(Path.GetFileName(path), Display(font), null, true);

        var stem = Path.GetFileNameWithoutExtension(source);
        foreach (var bad in Path.GetInvalidFileNameChars()) stem = stem.Replace(bad, '_');
        if (stem.Trim().Length == 0) stem = "Schrift";
        var target = stem + extension;
        for (var n = 2; Names.Contains(target, StringComparer.OrdinalIgnoreCase) || folders.Any(f => File.Exists(Path.Combine(f, target))); n++)
            target = $"{stem} ({n}){extension}";
        for (var i = 0; i < folders.Count; i++)
        {
            var temporary = Path.Combine(folders[i], target + ".neu");
            try
            {
                Directory.CreateDirectory(folders[i]);
                // Write under another name first, then rename, so that a half-written import never appears in the list
                File.WriteAllBytes(temporary, data);
                File.Move(temporary, Path.Combine(folders[i], target));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                    // nothing left to clean up
                }
                continue;
            }
            Refresh();
            return new SignatureFontImport(target, Display(font), null, false) { Fallback = i > 0 };
        }
        return SignatureFontImport.Failed("Die Schrift ließ sich nicht in den Ordner kopieren.");
    }

    /// <summary>Returns the family name for display (without the prefix "Handwrite-" of self-digitised fonts).</summary>
    /// <param name="font">The font.</param>
    /// <returns>The display name.</returns>
    public static string Display(OpenTypeFont font) =>
        font.FamilyName.StartsWith("Handwrite-", StringComparison.Ordinal) ? font.FamilyName["Handwrite-".Length..] : font.FamilyName;

    // ------------------------------------------------------------ Folders

    private static Listing Scan()
    {
        var names = new List<string>(EmbeddedNames);
        var paths = new Dictionary<string, FolderFont>(StringComparer.Ordinal);
        foreach (var folder in Folders())
        {
            foreach (var path in FilesIn(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(path);
                if (Path.GetExtension(name).ToLowerInvariant() is not (".ttf" or ".otf")) continue;
                // Same name already present (embedded or in an earlier folder): the first one wins
                if (names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                string key;
                try
                {
                    var info = new FileInfo(path);
                    key = $"{path}\n{info.LastWriteTimeUtc.Ticks}\n{info.Length}";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                // Only fonts that can be read, so that a damaged file does not disturb the selection
                if (!Parsed.ContainsKey(key))
                {
                    if (ReadFile(path) is not { } data || TryParse(data) is not { } font) continue;
                    Parsed.TryAdd(key, font);
                }
                names.Add(name);
                paths[name] = new FolderFont(path, key);
            }
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return new Listing(names, paths);
    }

    /// <summary>The folders in order: next to the program (the executable or the inside of the AppImage), next to the AppImage file, the user folder.</summary>
    private static IEnumerable<string> Folders()
    {
        var seen = new List<string>();
        foreach (var folder in new[] { Path.Combine(AppContext.BaseDirectory, FolderName), ProgramFolder, UserFolder })
            if (!seen.Any(s => SameFolder(s, folder)))
            {
                seen.Add(folder);
                yield return folder;
            }
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string[] FilesIn(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static byte[]? ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= MaxFileSize ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses an external font file; damaged → null. Stricter than for fonts in PDF files (where the reader silently cuts tables that are
    /// too long): all tables lie completely inside the file, the required ones exist, and at least one letter can be drawn. Otherwise a
    /// colour font truncated to 3 000 bytes parses as a usable font.
    /// </summary>
    private static OpenTypeFont? TryParse(byte[] data)
    {
        if (!OpenTypeFont.LooksLikeSfnt(data) || !TablesInside(data)) return null;
        try
        {
            var font = OpenTypeFont.Parse(data);
            if (font is not { Broken: false, GlyphCount: > 0 }) return null;
            foreach (var tag in (ReadOnlySpan<string>)["cmap", "head", "hhea", "hmtx", "maxp"])
                if (!font.HasTable(tag)) return null;
            if (!(font.HasTable("glyf") && font.HasTable("loca")) && font.Cff is null && font.Cff2 is null) return null;
            var color = ColorGlyphs.For(font);
            foreach (var c in "AaMm")
                if (font.GlyphForUnicode(c) is var glyph and > 0 && font.AdvanceWidth(glyph) > 0
                    && (color?.Glyph(glyph) is not null || !font.Outline(glyph).IsEmpty))
                    return font;
            return null;
        }
        catch (Exception e) when (e is FontFormatException or IndexOutOfRangeException or ArgumentException or InvalidOperationException
                                       or OverflowException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>Checks the table directory: every table lies completely inside the file.</summary>
    private static bool TablesInside(byte[] data)
    {
        if (data.Length < 12) return false;
        var count = (data[4] << 8) | data[5];
        if (count == 0 || 12 + 16L * count > data.Length) return false;
        for (var i = 0; i < count; i++)
        {
            var record = 12 + 16 * i;
            var offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 8));
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(record + 12));
            if ((long)offset + length > data.Length) return false;
        }
        return true;
    }

    private static OpenTypeFont Parse(byte[] data) => OpenTypeFont.Parse(data);

    private static byte[] LoadEmbedded(string file)
    {
        using var stream = typeof(SignatureFonts).Assembly.GetManifestResourceStream(EmbeddedPrefix + file)
                           ?? throw new FileNotFoundException("Schrift nicht im Programm: " + file);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}

/// <summary>Where a handwriting font comes from.</summary>
public enum SignatureFontOrigin
{
    /// <summary>Embedded in the program (freely licensed).</summary>
    Embedded,

    /// <summary>Folder "Schriften-Unterschrift" next to the program.</summary>
    Folder,

    /// <summary>User folder: imported when the folder next to the program was not writable.</summary>
    UserFolder,
}

/// <summary>Result of an import: file name and family, or an error (German text, shown translated).</summary>
/// <param name="File">File name of the imported (or already present) font; null on error.</param>
/// <param name="Family">Display name of the font family; null on error.</param>
/// <param name="Error">German error text, or null on success.</param>
/// <param name="AlreadyThere">The same file was already present, nothing was copied.</param>
public sealed record SignatureFontImport(string? File, string? Family, string? Error, bool AlreadyThere)
{
    /// <summary>The font went to the user folder because the folder next to the program was not writable.</summary>
    public bool Fallback { get; init; }

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">German error text.</param>
    /// <returns>A result with only <see cref="Error"/> set.</returns>
    public static SignatureFontImport Failed(string error) => new(null, null, error, false);
}
