using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Platform;

/// <summary>
/// The program's own log next to the program (“PDF-Knead.log”, otherwise in the user folder): crashes and unhandled errors, so
/// there is no need to search the Event Viewer. A crash in native code (Skia, HarfBuzz) cannot be caught in .NET; instead,
/// every running instance records in a small file what it is currently working on (“PDF-Knead.&lt;pid&gt;.laeuft”). If the clean
/// end is missing at the next start, the crash is written to the log with the last activity; on Windows together with the
/// Event Viewer entry (stack trace) that Windows itself writes.
/// </summary>
public static class AppLog
{
    /// <summary>File name of the log.</summary>
    public const string FileName = "PDF-Knead.log";
    private const string MarkerPrefix = "PDF-Knead.";
    private const string MarkerSuffix = ".laeuft";
    private const long MaxBytes = 1 << 20;   // above this size the log is moved to “.log.alt” and a new one is started

    private static readonly Lock Gate = new();
    private static string? _folder;
    private static string? _marker;
    private static DateTime _started;
    private static string? _activity;
    private static DateTime _activitySince;

    /// <summary>Gets what was last reported as running work (<see cref="Activity"/>); null = nothing.</summary>
    public static string? CurrentActivity => _activity;

    /// <summary>Gets the path of the log; null if no writable folder was found or <see cref="Start"/> has not been called.</summary>
    public static string? LogPath => _folder is null ? null : Path.Combine(_folder, FileName);

    /// <summary>At start-up: chooses the folder, records crashes of earlier runs, creates this instance's run file and subscribes to unhandled errors.</summary>
    /// <param name="folder">Folder for the log; null = next to the program if writable and not in a sync folder, otherwise the user folder.</param>
    /// <remarks>Crashes of earlier runs are reported on a background task.</remarks>
    public static void Start(string? folder = null)
    {
        _folder = folder ?? WritableFolder();
        if (_folder is null) return;
        _started = DateTime.Now;
        var pid = Environment.ProcessId;
        _marker = Path.Combine(_folder, MarkerPrefix + pid + MarkerSuffix);

        // Run files of other processes: if the process is no longer running, it ended without a clean exit.
        var crashed = new List<string>();
        foreach (var file in SafeFiles(_folder))
            if (PidOf(file) is { } other && other != pid && !IsRunning(other)) crashed.Add(file);
        WriteMarker();
        if (crashed.Count > 0) _ = Task.Run(() => ReportCrashes(crashed));

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("Unbehandelter Fehler — Programm beendet", e.ExceptionObject.ToString());
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Write("Fehler in einer Hintergrundaufgabe (nicht abgefangen)", e.Exception.ToString());
    }

    /// <summary>Clean exit: deletes the run file.</summary>
    public static void Stop()
    {
        if (_marker is null) return;
        try { File.Delete(_marker); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Records what is currently being worked on (rendering a page, saving …); it appears in the log after a crash.</summary>
    /// <param name="what">Description of the work; null = nothing any more.</param>
    /// <remarks>Rewrites the run file on every call.</remarks>
    public static void Activity(string? what)
    {
        _activity = what;
        _activitySince = DateTime.Now;
        WriteMarker();
    }

    /// <summary>Writes an entry with time, title and indented text.</summary>
    /// <param name="title">Title line of the entry.</param>
    /// <param name="details">Further text, indented line by line; null or blank = none.</param>
    /// <remarks>Thread-safe; does nothing without a log folder, and write errors are ignored.</remarks>
    public static void Write(string title, string? details = null)
    {
        if (LogPath is not { } path) return;
        var text = new StringBuilder();
        text.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append("  ").AppendLine(title);
        if (!string.IsNullOrWhiteSpace(details))
            foreach (var line in details.Replace("\r", "").TrimEnd().Split('\n'))
                text.Append("    ").AppendLine(line);
        lock (Gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".alt", overwrite: true);
                File.AppendAllText(path, text.ToString());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static readonly bool StartTimingOn = Environment.GetEnvironmentVariable("PDFKNEAD_STARTZEIT") == "1";
    private static StringBuilder? _startTiming;

    /// <summary>
    /// Records an intermediate start-up time, measured from the start of the process; only with PDFKNEAD_STARTZEIT=1. With
    /// <paramref name="done"/>, all intermediate times are written to the log as one entry.
    /// </summary>
    /// <param name="step">Name of the step.</param>
    /// <param name="done">Last step: write the collected times to the log.</param>
    public static void StartStep(string step, bool done = false)
    {
        if (!StartTimingOn) return;
        var ms = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
        lock (Gate) (_startTiming ??= new()).Append(CultureInfo.InvariantCulture, $"{ms,6:0} ms  {step}\n");
        if (!done) return;
        string text;
        lock (Gate)
        {
            text = _startTiming.ToString();
            _startTiming = null;
        }
        Write("Startzeit", text);
    }

    // ------------------------------------------------------------ Internal

    private static Encoding AnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>Time of an event from the line “Date: …” of wevtutil.</summary>
    private static DateTime? DateOf(string evt)
    {
        var i = evt.IndexOf("Date: ", StringComparison.Ordinal);
        if (i < 0) return null;
        var end = evt.IndexOfAny(['\r', '\n'], i);
        var text = evt[(i + 6)..(end < 0 ? evt.Length : end)].Trim();
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }

    /// <summary>
    /// Next to the program if writing is allowed there and it is not in a sync folder (OneDrive …: stack traces and paths containing the
    /// user name would be synchronised too); otherwise in the user folder.
    /// </summary>
    private static string? WritableFolder()
    {
        foreach (var dir in new[]
                 {
                     SyncFolder.Contains(AppLocations.ProgramFolder) ? null : AppLocations.ProgramFolder,
                     AppLocations.UserDataFolder,   // the same folder in the program; under %TEMP% in the tests (No. 1240)
                 })
        {
            if (dir is null) continue;
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, FileName);
                using (File.Open(probe, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
                return dir;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    private static void WriteMarker()
    {
        if (_marker is null) return;
        var lines = new[]
        {
            _started.ToString("o", CultureInfo.InvariantCulture),
            _activity is null ? "" : _activitySince.ToString("o", CultureInfo.InvariantCulture),
            _activity ?? "",
        };
        try { File.WriteAllLines(_marker, lines); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static IEnumerable<string> SafeFiles(string folder)
    {
        try { return Directory.GetFiles(folder, MarkerPrefix + "*" + MarkerSuffix); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static int? PidOf(string file)
    {
        var name = Path.GetFileName(file);
        var middle = name[MarkerPrefix.Length..^MarkerSuffix.Length];
        return int.TryParse(middle, out var pid) ? pid : null;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("PDF-Knead", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>For every orphaned run file: writes the crash to the log, with the last activity and (on Windows) the Event Viewer entries.</summary>
    private static void ReportCrashes(IEnumerable<string> markers)
    {
        foreach (var marker in markers)
        {
            string[] lines;
            try { lines = File.ReadAllLines(marker); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var started = lines.Length > 0 && DateTime.TryParse(lines[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var s)
                ? s : (DateTime?)null;
            var since = lines.Length > 1 && DateTime.TryParse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var a)
                ? a : (DateTime?)null;
            var activity = lines.Length > 2 && lines[2].Length > 0 ? lines[2] : null;

            var details = new StringBuilder();
            if (started is { } st) details.AppendLine("Gestartet: " + st.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            details.AppendLine(activity is null
                ? "Zuletzt: keine Rechnung (Absturz bei der Bedienung)"
                : $"Zuletzt: {activity} (seit {since?.ToString("HH:mm:ss", CultureInfo.InvariantCulture)})");
            if (OperatingSystem.IsWindows() && started is { } from && EventLogSince(from, PidOf(marker)) is { Length: > 0 } events)
                details.AppendLine().AppendLine("Ereignisanzeige (Windows):").Append(events);
            Write("Absturz — das Programm wurde unerwartet beendet", details.ToString());
            try { File.Delete(marker); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Entries of “Application Error” (with process ID) and “.NET Runtime” (with stack trace, without process ID: the one
    /// shortly before it in time) for this crash, via wevtutil (part of Windows), without an additional package. Empty if nothing
    /// was found or the log cannot be read.
    /// </summary>
    private static string EventLogSince(DateTime from, int? pid)
    {
        try
        {
            var utc = from.ToUniversalTime().AddSeconds(-5).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "wevtutil.exe"))   // full path: never a foreign wevtutil.exe from the working folder
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // When redirected, wevtutil writes in the ANSI code page (German: 1252).
                StandardOutputEncoding = AnsiEncoding(),
            };
            foreach (var arg in new[]
                     {
                         "qe", "Application", "/rd:true", "/c:20", "/f:text",
                         $"/q:*[System[(Provider[@Name='.NET Runtime'] or Provider[@Name='Application Error']) and TimeCreated[@SystemTime>='{utc}Z']]]",
                     })
                psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            if (p is null) return "";
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(10_000) || !output.Wait(1000)) return "";
            // An event starts with “Event[n]”; only those of this program.
            var events = output.Result.Replace("\0", "").Split("Event[", StringSplitOptions.RemoveEmptyEntries)
                .Where(e => e.Contains("PDF-Knead", StringComparison.OrdinalIgnoreCase))
                .Select(e => (Text: "Event[" + e.TrimEnd(), Date: DateOf(e), Runtime: e.Contains(".NET Runtime", StringComparison.Ordinal)))
                .ToList();
            // Crash report of this process (newest first); without a process ID, the newest one.
            var crash = events.FirstOrDefault(e => !e.Runtime && (pid is not { } id || e.Text.Contains($"0x{id:X}", StringComparison.OrdinalIgnoreCase)));
            if (crash.Text is null) crash = events.FirstOrDefault(e => !e.Runtime && pid is null);
            var at = crash.Text is null ? (DateTime?)null : crash.Date;
            var runtime = events.FirstOrDefault(e => e.Runtime && (at is not { } t || e.Date is { } d && d <= t.AddSeconds(2) && d >= t.AddSeconds(-30)));
            return string.Join(Environment.NewLine + Environment.NewLine,
                new[] { runtime.Text, crash.Text }.Where(t => t is not null));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return "";
        }
    }
}
