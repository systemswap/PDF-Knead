using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace PdfKnead.Core.Platform;

/// <summary>
/// Opening files in a window already open (setting „Dateien öffnen“, <see cref="AppSettings.OpenInRunningWindow"/>; design decision
/// 16.11.2026): a new start hands its files to the window used last and ends; without one that answers, it starts as usual. Local only and
/// only within the user's own account: a named pipe (Windows) or a Unix domain socket (Linux) with <see cref="PipeOptions.CurrentUserOnly"/>,
/// so that another user cannot connect, nor be connected to. Only full paths of existing files go across — at most <see cref="MaxFiles"/>,
/// each of at most <see cref="MaxPathLength"/> characters; the receiver opens them as if dropped on its window. Nothing goes to the network.
/// </summary>
public static partial class FileHandoff
{
    /// <summary>The most files one start hands over.</summary>
    public const int MaxFiles = 200;

    /// <summary>The longest path accepted (the long paths of Windows).</summary>
    public const int MaxPathLength = 32_767;

    /// <summary>The first words of a message: a stranger on the pipe is told apart from a start of PDF-Knead (and a later protocol).</summary>
    private const string Magic = "PDF-Knead Dateien 1";

    private const string Prefix = "pdf-knead-oeffnen-";

    /// <summary>How long a slow or silent client may hold the receiver.</summary>
    private static readonly TimeSpan ReadLimit = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The pipe a window of this process listens on. Under Linux a socket in the user's runtime folder ($XDG_RUNTIME_DIR, /run/user/…: only
    /// the user can enter it, systemd empties it at logout) instead of the temporary folder .NET would take — a full path is used as given.
    /// Under Windows a named pipe.
    /// </summary>
    public static string PipeName(int processId) => PipeName(processId, OperatingSystem.IsWindows() ? null : Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"));

    internal static string PipeName(int processId, string? runtimeFolder) =>
        runtimeFolder is { Length: > 0 } folder && Path.IsPathFullyQualified(folder) && Directory.Exists(folder)
            ? Path.Combine(folder, Prefix + processId)
            : Prefix + processId;

    /// <summary>The file naming the process whose window was used last (its number only).</summary>
    internal static string MarkerFile => Path.Combine(AppLocations.UserDataFolder, "letztes-fenster");

    // ------------------------------------------------------------ the new start

    /// <summary>
    /// Hands the files to the window used last, else to another open window of PDF-Knead (the youngest first); true if one took them — the
    /// new start then ends. False for anything but existing files (the start goes on as usual).
    /// </summary>
    public static bool TryHandOver(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        if (arguments.Count is 0 or > MaxFiles) return false;
        List<string> paths;
        try
        {
            paths = [.. arguments.Select(Path.GetFullPath)];
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }
        if (paths.Any(p => p.Length > MaxPathLength || !File.Exists(p))) return false;
        foreach (var process in Candidates())
        {
            AllowToFront(process);
            if (TrySend(PipeName(process), paths, timeout)) return true;
        }
        return false;
    }

    /// <summary>
    /// The processes to ask, only running ones of this program (a number in the marker may meanwhile belong to another program or none): the
    /// one used last first, then the others, youngest first; never this one.
    /// </summary>
    private static IEnumerable<int> Candidates()
    {
        List<(int Id, DateTime Started)> others = [];
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(current.ProcessName))
                using (p)
                {
                    try
                    {
                        others.Add((p.Id, p.StartTime));
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        // ended meanwhile, or of another user: not asked
                    }
                }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
        var marked = ReadMarker();
        return others.Where(o => o.Id != Environment.ProcessId).OrderByDescending(o => o.Id == marked).ThenByDescending(o => o.Started).Select(o => o.Id);
    }

    /// <summary>The longest wait for a pipe to connect: a process of the program that does not listen (a scan helper, a start under way) is passed by after a second; a window busy with another start is waited for that long.</summary>
    private const int ConnectLimit = 1000;

    /// <summary>Sends the paths to one pipe and waits for its answer; false if nobody listens, it refuses or does not answer in time.</summary>
    internal static bool TrySend(string pipeName, IReadOnlyList<string> paths, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect((int)Math.Clamp(timeout.TotalMilliseconds, 1, ConnectLimit));
            using var limit = new CancellationTokenSource(timeout);
            var message = new MemoryStream();
            WriteText(message, Magic);
            message.Write(BitConverter.GetBytes(paths.Count));
            foreach (var path in paths) WriteText(message, path);
            client.WriteAsync(message.ToArray(), limit.Token).AsTask().GetAwaiter().GetResult();
            client.FlushAsync(limit.Token).GetAwaiter().GetResult();
            var answer = new byte[1];
            return client.ReadAsync(answer, limit.Token).AsTask().GetAwaiter().GetResult() == 1 && answer[0] == 1;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------ the open window

    /// <summary>Listens for files handed over until disposed; <paramref name="received"/> is called off the interface thread.</summary>
    public static IDisposable Listen(Action<IReadOnlyList<string>> received) => Listen(PipeName(Environment.ProcessId), received);

    internal static Listener Listen(string pipeName, Action<IReadOnlyList<string>> received) => new(pipeName, received);

    /// <summary>Notes this process as the one whose window was used last (called when its window is activated).</summary>
    public static void MarkUsed()
    {
        try
        {
            Directory.CreateDirectory(AppLocations.UserDataFolder);
            File.WriteAllText(MarkerFile, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // without the note another open window is asked
        }
    }

    /// <summary>Removes the note if it names this process (called when the program ends).</summary>
    public static void Unmark()
    {
        try
        {
            if (ReadMarker() == Environment.ProcessId) File.Delete(MarkerFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static int? ReadMarker()
    {
        try
        {
            return int.TryParse(File.ReadAllText(MarkerFile).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out var id) && id > 0 ? id : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The window listening. The file manager opening several files starts several programs at once: <see cref="Waiting"/> pipes are kept
    /// ready, so that the system connects that many starts at once even while the program is busy (they then wait for the answer, which they
    /// may for longer); each next pipe is laid out before the current handover is answered. With one pipe only, starts under load fell into the
    /// gap and opened windows of their own (found by a test in the full run, 16.11.2026).
    /// </summary>
    internal sealed class Listener : IDisposable
    {
        /// <summary>The pipes listening at a time.</summary>
        internal const int Waiting = 4;

        /// <summary>All pipes of the name at a time: those listening and those being answered (the same number for each, as Windows asks).</summary>
        private const int Instances = 2 * Waiting;

        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _listening;

        public Listener(string pipeName, Action<IReadOnlyList<string>> received)
        {
            for (var i = 0; i < Waiting; i++) _ = Task.Run(() => LoopAsync(pipeName, received, _stop.Token));
        }

        /// <summary>Done when all <see cref="Waiting"/> pipes listen (a start before that may find nobody and open a window of its own).</summary>
        internal Task Ready => _ready.Task;

        public void Dispose() => _stop.Cancel();

        private static NamedPipeServerStream Create(string pipeName) =>
            new(pipeName, PipeDirection.InOut, Instances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        private async Task LoopAsync(string pipeName, Action<IReadOnlyList<string>> received, CancellationToken stop)
        {
            NamedPipeServerStream? waiting = null;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        if (waiting is null)
                        {
                            waiting = Create(pipeName);
                            if (Interlocked.Increment(ref _listening) == Waiting) _ready.TrySetResult();
                        }
                        await waiting.WaitForConnectionAsync(stop).ConfigureAwait(false);
                        var connected = waiting;
                        waiting = null;
                        // the next one listens before this one is answered
                        try
                        {
                            waiting = Create(pipeName);
                        }
                        catch (IOException)
                        {
                            // laid out again in the next round
                        }
                        await using (connected) await AnswerAsync(connected, received, stop).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
                    {
                        // the pipe could not be laid out (taken, closed): a moment, then again
                        waiting?.Dispose();
                        waiting = null;
                        try
                        {
                            await Task.Delay(200, stop).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }
                    }
                }
            }
            finally
            {
                waiting?.Dispose();
            }
        }

        /// <summary>
        /// Reads one message and answers it: taken over first, answered then — when the new start hears "yes" and ends, the files are with
        /// the window. A broken, slow or silent client gets nothing opened.
        /// </summary>
        private static async Task AnswerAsync(NamedPipeServerStream pipe, Action<IReadOnlyList<string>> received, CancellationToken stop)
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
                limit.CancelAfter(ReadLimit);
                var paths = await ReadAsync(pipe, limit.Token).ConfigureAwait(false);
                if (paths is not null) received(paths);
                await pipe.WriteAsync(new[] { paths is null ? (byte)0 : (byte)1 }, limit.Token).ConfigureAwait(false);
                await pipe.FlushAsync(limit.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // on to the next
            }
        }
    }

    // ------------------------------------------------------------ the message

    /// <summary>Reads one message: null for anything but the expected words and full paths within the limits (the whole message is refused).</summary>
    internal static async Task<IReadOnlyList<string>?> ReadAsync(Stream stream, CancellationToken token)
    {
        if (await ReadTextAsync(stream, Magic.Length * 4, token).ConfigureAwait(false) != Magic) return null;
        var countBytes = new byte[4];
        await stream.ReadExactlyAsync(countBytes, token).ConfigureAwait(false);
        var count = BitConverter.ToInt32(countBytes);
        if (count is < 1 or > MaxFiles) return null;
        var paths = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var path = await ReadTextAsync(stream, MaxPathLength * 4, token).ConfigureAwait(false);
            if (path is null || path.Length > MaxPathLength || path.Contains('\0') || !Path.IsPathFullyQualified(path)) return null;
            paths.Add(path);
        }
        return paths;
    }

    private static void WriteText(Stream stream, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(BitConverter.GetBytes(bytes.Length));
        stream.Write(bytes);
    }

    /// <summary>A text as its length in bytes and its UTF-8 bytes; null if the length is beyond <paramref name="maxBytes"/> (nothing more read).</summary>
    private static async Task<string?> ReadTextAsync(Stream stream, int maxBytes, CancellationToken token)
    {
        var lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, token).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBytes);
        if (length < 1 || length > maxBytes) return null;
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------ Windows: the window to the front

    /// <summary>
    /// Windows lets only the program in the foreground bring a window to the front: the new start (in the foreground, just opened by the user)
    /// allows the open window's process to do so before handing over. Elsewhere nothing to do.
    /// </summary>
    private static void AllowToFront(int processId)
    {
        if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(processId);
    }

    [LibraryImport("user32.dll", EntryPoint = "AllowSetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
