using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Saves to a file without damaging it in case of an error: first completely into a temporary file in the same folder,
/// then moved into place. This also works over the file that is currently open: Windows does not allow overwriting a
/// memory-mapped file, but does allow renaming and deleting it (the open document reads the old data until it is closed).
/// </summary>
public static class PdfFileSaver
{
    /// <summary>Writes the pages to a file safely.</summary>
    /// <param name="main">The main document.</param>
    /// <param name="pages">The pages to write.</param>
    /// <param name="path">The target path; may be the file that is currently open.</param>
    /// <param name="options">The save options; null for the defaults.</param>
    /// <param name="cancel">Cancels the writing; the target stays unchanged.</param>
    /// <returns>The result of the writer.</returns>
    /// <exception cref="UnauthorizedAccessException">The target file is read-only.</exception>
    /// <remarks>Blocks until the data has been flushed to disk.</remarks>
    public static PdfSaveResult Save(PdfDocument main, IReadOnlyList<PageItem> pages, string path,
        PdfSaveOptions? options = null, CancellationToken cancel = default) =>
        Save(path, output => PdfWriter.Write(main, pages, output, options, cancel), pages.Count);

    /// <summary>Writes a file safely with any writer (for example already signed bytes).</summary>
    /// <param name="path">The target path; may be the file that is currently open.</param>
    /// <param name="write">Writes the whole file into the stream and returns the result.</param>
    /// <returns>The result of <paramref name="write"/>.</returns>
    /// <exception cref="UnauthorizedAccessException">The target file is read-only.</exception>
    /// <remarks>Blocks until the data has been flushed to disk.</remarks>
    public static PdfSaveResult Save(string path, Func<Stream, PdfSaveResult> write) => Save(path, write, null);

    /// <param name="path">The target path.</param>
    /// <param name="write">Writes the whole file.</param>
    /// <param name="pages">How many pages the file must have; null: at least one.</param>
    /// <param name="pdf">Whether a PDF is written (checked before it replaces the target); false for other files.</param>
    private static PdfSaveResult Save(string path, Func<Stream, PdfSaveResult> write, int? pages, bool pdf = true)
    {
        path = Path.GetFullPath(path);
        // Read-only: report it right away, not only after everything has been written
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException(Tr.T("Die Datei ist schreibgeschützt."));
        var folder = Path.GetDirectoryName(path)!;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var temp = Path.Combine(folder, $"{Path.GetFileName(path)}.{tag}.pdfknead-tmp");
        Remember(temp);   // if the program is aborted midway, the next start removes it
        try
        {
            PdfSaveResult result;
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                result = write(output);
                output.Flush(flushToDisk: true);
            }
            // redacted pages: checked once more in the file as written, before it replaces the target (docs/FEHLERGEDAECHTNIS.md, No. 461)
            if (result.RedactionChecks is { } checks)
            {
                using (var written = PdfDocument.OpenWritten(temp, result.Security)) Editing.Redactor.CheckWritten(written, checks.Pages, checks.ContentAsRedacted);
                result = result with { RedactionChecks = null };
            }
            // the file as written must open as it should before it replaces anything: an error of the writer must not cost the original
            // (docs/VOR-DEM-VERKAUF.md, §7a; docs/FEHLERGEDAECHTNIS.md, No. 750)
            if (pdf) CheckReadable(temp, result.Security, pages);
            Replace(temp, path, tag);
            Platform.UnixNative.FlushFolder(folder);   // Linux: the rename on the disk as well (docs/VOR-DEM-VERKAUF.md, §7a)
            Forget(temp);
            return result;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Opens the file just written as a reader would: cross-references as written (not repaired), a catalogue, the pages expected.
    /// </summary>
    /// <exception cref="PdfException">It does not open like that; the target stays as it was.</exception>
    private static void CheckReadable(string temp, PdfKnead.Core.Pdf.Security.StandardSecurityHandler? security, int? pages)
    {
        string? reason;
        try
        {
            using var written = PdfDocument.OpenWritten(temp, security);
            reason = written.Repaired ? Tr.T("Querverweise stimmen nicht")
                : written.PageCount == 0 || pages is { } expected && written.PageCount != expected ? Tr.T("Seitenzahl stimmt nicht")
                : null;
        }
        catch (PdfException e) when (e is not PdfPasswordException)
        {
            reason = e.Message;
        }
        if (reason is not null)
            throw new PdfException(Tr.T("Die geschriebene Datei ist fehlerhaft ({0}) — das Original bleibt unverändert.", reason));
    }

    /// <summary>
    /// Writes any other file the user saves (images, word lists, reports, certificates) the same safe way: completely into a temporary file
    /// in the same folder, flushed to disk, then moved into place in one step — a crash leaves the old file or the new one, never half of it
    /// (docs/VOR-DEM-VERKAUF.md, §7a).
    /// </summary>
    /// <param name="path">The target path.</param>
    /// <param name="write">Writes the whole content into the stream.</param>
    /// <exception cref="UnauthorizedAccessException">The target file is read-only.</exception>
    public static void SaveFile(string path, Action<Stream> write) =>
        Save(path, output =>
        {
            write(output);
            return new PdfSaveResult("", output.Position, 0, false);
        }, null, pdf: false);

    private static void Replace(string temp, string path, string tag)
    {
        if (!File.Exists(path))
        {
            File.Move(temp, path);
            return;
        }
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException(Tr.T("Die Datei ist schreibgeschützt."));
        try
        {
            File.Move(temp, path, overwrite: true);
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Memory-mapped (large file that is open): rename instead of overwriting
        }
        var old = $"{path}.{tag}.pdfknead-alt";
        Remember(old);   // the old version (for example unredacted): if it cannot be deleted now, then at the next start
        File.Move(path, old);
        try
        {
            File.Move(temp, path);
        }
        catch
        {
            File.Move(old, path);
            Forget(old);
            throw;
        }
        TryDelete(old);
    }

    // Leftovers that could not be deleted yet (the open file is memory-mapped) and temporary files in progress: tried again
    // on closing, and recorded in a list (LeftoverList), so that a crash, termination via the Task Manager or a second
    // instance that kept the file open does not leave an unredacted original next to the file. See docs/FEHLERGEDAECHTNIS.md, No. 59.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Leftovers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock ListGate = new();

    /// <summary>File for the list of leftovers still to be deleted (set by the program, in the user folder); null = kept in memory only.</summary>
    public static string? LeftoverList { get; set; }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            Forget(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still memory-mapped: try again later; otherwise the original (for example unredacted) would silently stay next to the file
            Remember(path);
        }
    }

    /// <summary>Deletes old versions that were left behind (after the documents that read them have been closed).</summary>
    /// <remarks>Temporary files of saves in progress are not touched; files that still cannot be deleted stay on the list.</remarks>
    public static void DeleteLeftovers()
    {
        foreach (var path in Leftovers.Keys)
            if (!path.EndsWith(".pdfknead-tmp", StringComparison.OrdinalIgnoreCase)) TryDelete(path);   // not temporary files in progress
    }

    /// <summary>
    /// At program start: deletes leftovers of earlier runs (list <see cref="LeftoverList"/>). Only files with the program's own
    /// name pattern "….xxxxxxxx.pdfknead-alt" or "-tmp" and a fully qualified path, so that a tampered list cannot cause
    /// anything else to be deleted. Entries of another instance that is still running stay untouched: under Linux a file
    /// can be deleted while it is being written, and its save would fail at the end (or lose the old version it has just
    /// renamed). Only the entries of earlier runs are touched — never a temporary file of a save of this instance that is in progress
    /// (docs/FEHLERGEDAECHTNIS.md, No. 265).
    /// </summary>
    public static void DeleteLeftoversOfEarlierRuns()
    {
        if (LeftoverList is not { } list || !File.Exists(list)) return;
        var earlier = new List<string>();
        lock (ListGate)
        {
            if (ReadList(list) is not { } entries) return;
            foreach (var (owner, path) in entries)
                if (IsOwnLeftover(path) && (owner is null || !IsRunning(owner)) && Leftovers.TryAdd(path, 0)) earlier.Add(path);
        }
        foreach (var path in earlier)
            if (!File.Exists(path)) Forget(path);
            else TryDelete(path);
        Save();
    }

    private static bool IsOwnLeftover(string path)
    {
        if (path.Length == 0 || !Path.IsPathFullyQualified(path)) return false;
        var name = Path.GetFileName(path);
        var suffix = name.EndsWith(".pdfknead-alt", StringComparison.OrdinalIgnoreCase) ? ".pdfknead-alt"
            : name.EndsWith(".pdfknead-tmp", StringComparison.OrdinalIgnoreCase) ? ".pdfknead-tmp" : null;
        if (suffix is null) return false;
        var stem = name[..^suffix.Length];
        var dot = stem.LastIndexOf('.');
        return dot > 0 && stem.Length - dot - 1 == 8 && stem[(dot + 1)..].All(Uri.IsHexDigit);
    }

    private static void Remember(string path)
    {
        if (Leftovers.TryAdd(path, 0)) Save();
    }

    private static void Forget(string path)
    {
        if (Leftovers.TryRemove(path, out _)) Save();
    }

    // The list is shared by all running instances. Each line: "<owner>\t<path>", the owner being the instance whose save or old
    // version it is (process number and start time, so that a reused number does not count). Lines without an owner come
    // from older versions and belong to nobody.
    private static readonly string Self = OwnerOf(Environment.ProcessId);

    private static string OwnerOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return string.Create(CultureInfo.InvariantCulture, $"{processId}:{process.StartTime.ToUniversalTime().Ticks}");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{processId}:0");
        }
    }

    // Whether the instance that wrote a line still runs; in doubt (start time not readable) it counts as running — its
    // files then stay until a later start
    private static bool IsRunning(string owner)
    {
        if (owner == Self) return true;
        var colon = owner.IndexOf(':');
        if (colon <= 0 || !int.TryParse(owner.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return false;
        var running = OwnerOf(id);
        return running == owner || running.EndsWith(":0", StringComparison.Ordinal) && IsAlive(id);

        static bool IsAlive(int id)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                return !process.HasExited;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return false;
            }
        }
    }

    private static List<(string? Owner, string Path)>? ReadList(string list)
    {
        try
        {
            if (!File.Exists(list)) return [];
            return [.. File.ReadAllLines(list).Where(l => l.Length > 0).Select(l => l.IndexOf('\t') is var tab and > 0 ? (l[..tab], l[(tab + 1)..]) : ((string?)null, l))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Save()
    {
        if (LeftoverList is not { } list) return;
        lock (ListGate)
        {
            try
            {
                // Our own entries, plus those of other instances that are still running or whose files still exist (they are
                // deleted at the next start); our own lines are replaced by the current state
                var lines = Leftovers.Keys.Select(path => $"{Self}\t{path}").ToList();
                foreach (var (owner, path) in ReadList(list) ?? [])
                    if (owner != Self && IsOwnLeftover(path) && !Leftovers.ContainsKey(path) && (File.Exists(path) || owner is not null && IsRunning(owner)))
                        lines.Add(owner is null ? path : $"{owner}\t{path}");
                if (lines.Count == 0)
                {
                    if (File.Exists(list)) File.Delete(list);
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(list)!);
                File.WriteAllLines(list, lines);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The list is only an aid: without it, leftovers are still deleted on closing
            }
        }
    }
}
