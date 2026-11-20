using System.IO.Compression;
using System.Text.Json;

namespace PdfKnead.Core.Updates;

/// <summary>
/// Unpacks a loaded package into the waiting area and exchanges it on the next start (KONZEPT.md §5.26). Only the files of the package are
/// replaced — what the user put next to the program (own language files, imported handwriting, the log) stays; the replaced files go to the
/// previous version, from which a start that did not reach the main window is restored. A package for Linux is the AppImage itself.
/// </summary>
public sealed class UpdateInstaller(string stateFolder)
{
    /// <summary>The largest unpacked package.</summary>
    public const long MaxUnpacked = 2L << 30;

    /// <summary>The most files in a package.</summary>
    public const int MaxFiles = 20000;

    /// <summary>The folder of the waiting area, the previous version and the notes.</summary>
    public string Folder => stateFolder;

    private string Ready => Path.Combine(stateFolder, "bereit.json");
    private string Installed => Path.Combine(stateFolder, "installiert.json");

    /// <summary>The waiting area of a version.</summary>
    public string Staged(UpdateVersion version) => Path.Combine(stateFolder, "neu-" + version);

    /// <summary>Where the files a version replaced are kept.</summary>
    public string Previous => Path.Combine(stateFolder, "vorversion");

    /// <summary>
    /// Unpacks a zip package into the waiting area of its version. Every entry is checked: no absolute path, no „..“, no link, at most
    /// <see cref="MaxFiles"/> files and <see cref="MaxUnpacked"/> bytes; a package that breaks one is refused as a whole.
    /// </summary>
    /// <returns>The files of the package (relative paths with „/“), or null if refused.</returns>
    public IReadOnlyList<string>? Unpack(string zipFile, UpdateVersion version)
    {
        var target = Staged(version);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        try
        {
            using var zip = ZipFile.OpenRead(zipFile);
            if (zip.Entries.Count > MaxFiles) return Refuse(target);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/')) continue;   // a folder
                if (!SafeRelative(name) || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) return Refuse(target);   // path out, or a link
                total += entry.Length;
                if (total > MaxUnpacked) return Refuse(target);
                var path = Path.GetFullPath(Path.Combine(target, name));
                if (!path.StartsWith(root, StringComparison.Ordinal)) return Refuse(target);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: false);
                if (new FileInfo(path).Length != entry.Length) return Refuse(target);
                files.Add(name);
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Refuse(target);
        }
        return files.Count == 0 ? Refuse(target) : files;
    }

    /// <summary>
    /// Unpacks a package (.zip for Windows, .tar.gz for Linux, as publish.ps1 and publish.sh write them) into the waiting area. The packages
    /// hold one folder („windows-x64/…“, „linux-x64/…“): it is taken off, the files count from the program folder on.
    /// </summary>
    /// <returns>The files of the package (relative paths with „/“), or null if refused.</returns>
    public IReadOnlyList<string>? UnpackPackage(string file, UpdateVersion version)
    {
        var files = file.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? UnpackTar(file, version) : Unpack(file, version);
        return files is null ? null : StripTopFolder(Staged(version), files);
    }

    private IReadOnlyList<string>? UnpackTar(string tarGz, UpdateVersion version)
    {
        var target = Staged(version);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        try
        {
            using var stream = File.OpenRead(tarGz);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new System.Formats.Tar.TarReader(gzip);
            long total = 0;
            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is System.Formats.Tar.TarEntryType.Directory) continue;
                if (entry.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile))
                    return Refuse(target);   // links and devices never
                var name = entry.Name.Replace('\\', '/');
                if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
                if (!SafeRelative(name) || files.Count >= MaxFiles) return Refuse(target);
                total += entry.Length;
                if (total > MaxUnpacked) return Refuse(target);
                var path = Path.GetFullPath(Path.Combine(target, name));
                if (!path.StartsWith(root, StringComparison.Ordinal)) return Refuse(target);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: false);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, entry.Mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute));
                files.Add(name);
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or FormatException)
        {
            return Refuse(target);
        }
        return files.Count == 0 ? Refuse(target) : files;
    }

    /// <summary>When every file lies in the same first folder, that folder is taken off (moved up in the waiting area).</summary>
    private static IReadOnlyList<string> StripTopFolder(string staged, IReadOnlyList<string> files)
    {
        var first = files[0].Split('/')[0];
        if (!files.All(f => f.StartsWith(first + "/", StringComparison.Ordinal))) return files;
        var inner = Path.Combine(staged, first);
        var moved = Path.Combine(staged, ".paket-" + Guid.NewGuid().ToString("N"));
        Directory.Move(inner, moved);
        foreach (var entry in Directory.EnumerateFileSystemEntries(moved))
        {
            var to = Path.Combine(staged, Path.GetFileName(entry));
            if (Directory.Exists(entry)) Directory.Move(entry, to);
            else File.Move(entry, to);
        }
        Directory.Delete(moved);
        return [.. files.Select(f => f[(first.Length + 1)..])];
    }

    /// <summary>Takes a single file (the AppImage) into the waiting area as the package.</summary>
    public IReadOnlyList<string> StageSingle(string file, string name, UpdateVersion version)
    {
        var target = Staged(version);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        File.Move(file, Path.Combine(target, name), overwrite: true);
        return [name];
    }

    private IReadOnlyList<string>? Refuse(string target)
    {
        try
        {
            Directory.Delete(target, recursive: true);
        }
        catch (IOException)
        {
            // cleaned on the next start
        }
        return null;
    }

    /// <summary>A relative path that stays inside its folder: no root, no drive, no „..“, no empty part, plain characters.</summary>
    public static bool SafeRelative(string name)
    {
        if (name.Length is 0 or > 400 || name.StartsWith('/') || name.Contains(':') || name.Contains('\0')) return false;
        var parts = name.Split('/');
        return parts.All(p => p.Length > 0 && p is not "." and not ".." && !p.Any(char.IsControl));
    }

    /// <summary>Notes that a version waits in its area to be exchanged on the next start.</summary>
    public void MarkReady(UpdateVersion version, IReadOnlyList<string> files) =>
        Write(Ready, new State(version.ToString(), files, false, 0));

    /// <summary>The version waiting to be exchanged, with its files; null if none (or the note is damaged).</summary>
    public (UpdateVersion Version, IReadOnlyList<string> Files)? Waiting()
    {
        if (Read(Ready) is not { } state || UpdateVersion.Parse(state.Version) is not { } version) return null;
        return Directory.Exists(Staged(version)) ? (version, state.Files) : null;
    }

    /// <summary>Forgets a waiting version (after it was exchanged, or when it is no longer newer).</summary>
    public void Discard(UpdateVersion version)
    {
        TryDelete(Ready);
        try
        {
            if (Directory.Exists(Staged(version))) Directory.Delete(Staged(version), recursive: true);
        }
        catch (IOException)
        {
            // cleaned on the next start
        }
    }

    /// <summary>
    /// Exchanges the program in <paramref name="programFolder"/> for the waiting version: each file of the package that exists there goes to
    /// the previous version first, then the new one is copied in. Nothing else in the folder is touched. On a failure everything moved so far
    /// is put back.
    /// </summary>
    /// <returns>Whether the exchange is complete.</returns>
    public bool Apply(string programFolder, UpdateVersion version, IReadOnlyList<string> files)
    {
        var staged = Staged(version);
        if (Directory.Exists(Previous)) Directory.Delete(Previous, recursive: true);
        Directory.CreateDirectory(Previous);
        var moved = new List<string>();
        var added = new List<string>();
        try
        {
            foreach (var name in files)
            {
                if (!SafeRelative(name)) return false;
                var source = Path.Combine(staged, name);
                var target = Path.Combine(programFolder, name);
                if (File.Exists(target))
                {
                    var keep = Path.Combine(Previous, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                    File.Move(target, keep);
                    moved.Add(name);
                }
                else added.Add(name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(source) | (IsProgram(name) ? UnixFileMode.UserExecute : 0));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Restore(programFolder, moved, added);
            return false;
        }
        Write(Installed, new State(version.ToString(), files, false, 0, moved, added));
        Discard(version);
        return true;
    }

    private static bool IsProgram(string name) => name.EndsWith(".AppImage", StringComparison.Ordinal) || !Path.GetFileName(name).Contains('.');

    /// <summary>
    /// Whether the exchanged version started once without reaching its main window: then it failed, and the previous version is to be put
    /// back (by the previous program, <see cref="UpdateStart.RestoreArgument"/>).
    /// </summary>
    public bool NeedsRestore() => Read(Installed) is { Confirmed: false, Starts: >= 1 };

    /// <summary>Counts a start of an exchanged version that has not yet reached its main window.</summary>
    public void CountStart()
    {
        if (Read(Installed) is { Confirmed: false } state) Write(Installed, state with { Starts = state.Starts + 1 });
    }

    /// <summary>Puts the previous version back into <paramref name="programFolder"/> and forgets the exchange.</summary>
    /// <returns>Whether there was an exchange to put back.</returns>
    public bool CheckStart(string programFolder)
    {
        if (Read(Installed) is not { Confirmed: false } state) return false;
        Restore(programFolder, state.Moved ?? [], state.Added ?? []);
        TryDelete(Installed);
        return true;
    }

    /// <summary>Removes waiting areas that no longer wait (an exchanged or forgotten version; the program ran from one of them).</summary>
    public void CleanUp()
    {
        if (!Directory.Exists(stateFolder)) return;
        var waiting = Waiting() is { } w ? Path.GetFullPath(Staged(w.Version)) : null;
        foreach (var folder in Directory.EnumerateDirectories(stateFolder, "neu-*"))
        {
            if (string.Equals(Path.GetFullPath(folder), waiting, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // still in use: the next start
            }
        }
    }

    /// <summary>The main window of the exchanged version is open: the exchange counts, the previous version is no longer needed for a restore.</summary>
    public void Confirm()
    {
        if (Read(Installed) is not { Confirmed: false } state) return;
        Write(Installed, state with { Confirmed = true });
    }

    private void Restore(string programFolder, IReadOnlyList<string> moved, IReadOnlyList<string> added)
    {
        foreach (var name in added) TryDelete(Path.Combine(programFolder, name));
        foreach (var name in moved)
        {
            try
            {
                var target = Path.Combine(programFolder, name);
                TryDelete(target);
                File.Move(Path.Combine(Previous, name), target);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // left in the previous version; the log of the start names it
            }
        }
    }

    /// <summary>Whether the program may exchange its own files there (a folder under „Programme“ usually not).</summary>
    public static bool CanWrite(string programFolder)
    {
        try
        {
            var probe = Path.Combine(programFolder, ".update-probe-" + Environment.ProcessId);
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record State(string Version, IReadOnlyList<string> Files, bool Confirmed, int Starts,
        IReadOnlyList<string>? Moved = null, IReadOnlyList<string>? Added = null);

    private static State? Read(string file)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 4 << 20) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var r = doc.RootElement;
            static IReadOnlyList<string>? List(JsonElement e, string name) =>
                e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                    ? [.. a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(SafeRelative)]
                    : null;
            return new State(r.GetProperty("version").GetString() ?? "", List(r, "files") ?? [], r.GetProperty("confirmed").GetBoolean(),
                r.GetProperty("starts").GetInt32(), List(r, "moved"), List(r, "added"));
        }
        catch (Exception e) when (e is JsonException or IOException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Write(string file, State state)
    {
        Directory.CreateDirectory(stateFolder);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("version", state.Version);
            w.WriteBoolean("confirmed", state.Confirmed);
            w.WriteNumber("starts", state.Starts);
            void Array(string name, IReadOnlyList<string>? list)
            {
                if (list is null) return;
                w.WriteStartArray(name);
                foreach (var s in list) w.WriteStringValue(s);
                w.WriteEndArray();
            }
            Array("files", state.Files);
            Array("moved", state.Moved);
            Array("added", state.Added);
            w.WriteEndObject();
        }
        var temp = file + ".neu";
        File.WriteAllBytes(temp, buffer.ToArray());
        File.Move(temp, file, overwrite: true);
    }

    private static void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // left
        }
    }
}
