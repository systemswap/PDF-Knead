using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace PdfKnead.Core.Scanning.Helper;

/// <summary>A connection to a scan helper: its two streams, whether it has ended, and how to end it.</summary>
internal interface IHelperConnection : IAsyncDisposable
{
    /// <summary>What the program writes to the helper.</summary>
    Stream ToHelper { get; }

    /// <summary>What the helper writes to the program.</summary>
    Stream FromHelper { get; }

    /// <summary>Ends the helper at once (it hangs).</summary>
    void Kill();
}

/// <summary>
/// The scanner interface in a helper process (KONZEPT §5.25): every call goes to the helper; if it crashes, the pages already received stay
/// and the error says so; if it hangs, it is ended after a time limit. The helper is started on first use and again after it has ended,
/// and kept for the run of the program (scanner interfaces search the network once and then open devices at once).
/// </summary>
internal sealed class ScanHelperBackend : IScanBackend
{
    /// <summary>The longest wait for a device search (network back ends take seconds, some much longer when an address does not answer).</summary>
    internal TimeSpan SearchLimit { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>The longest wait for opening or changing the source.</summary>
    internal TimeSpan CallLimit { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long opening a scanner may take: a network scanner the system's driver cannot reach keeps it waiting — then it is not reachable,
    /// and the helper is not reported as crashed (Fehlergedächtnis 927).
    /// </summary>
    internal TimeSpan OpenLimit { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest time without any sign from the helper while scanning (a sheet in a slow scanner at 1200 dpi takes minutes, so it is generous).</summary>
    internal TimeSpan SilenceLimit { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a cancelled scan may take to stop before the helper is ended.</summary>
    internal TimeSpan CancelLimit { get; init; } = TimeSpan.FromSeconds(15);

    private readonly Func<IHelperConnection> _connect;
    private readonly Func<string?> _availability;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Client? _client;

    /// <param name="name">The name of the scanner interface the helper uses.</param>
    /// <param name="connect">Starts a helper.</param>
    /// <param name="availability">Null if the scanner interface is installed, otherwise the installation hint.</param>
    public ScanHelperBackend(string name, Func<IHelperConnection> connect, Func<string?> availability)
    {
        Name = name;
        _connect = connect;
        _availability = availability;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public bool IsAvailable(out string? hint)
    {
        hint = _availability();
        return hint is null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ScanDevice>> FindDevicesAsync(CancellationToken cancel = default)
    {
        var answer = await (await Connected()).Call(HelperMessage.FindDevices, [], SearchLimit, cancel);
        return answer.Read(HelperJson.Default.ListScanDevice) ?? [];
    }

    /// <inheritdoc/>
    public async Task<IScanSession> OpenAsync(ScanDevice device, CancellationToken cancel = default)
    {
        var client = await Connected();
        HelperFrame answer;
        try
        {
            answer = await client.Call(HelperMessage.Open, HelperWire.Json(device, HelperJson.Default.ScanDevice), OpenLimit, cancel);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new ScanException(Tr.T("Der Scanner antwortet nicht — ist er eingeschaltet und im selben Netz?"), ScanErrorKind.Offline);
        }
        var opened = answer.Read(HelperJson.Default.HelperOpened) ?? throw new ScanException(Tr.T("Der Scanner ist nicht erreichbar — ist er eingeschaltet?"), ScanErrorKind.Offline);
        return new Session(this, client, opened.Device, opened.Capabilities);
    }

    private async Task<Client> Connected()
    {
        await _gate.WaitAsync();
        try
        {
            if (_client is { Alive: true } client) return client;
            _client = new Client(this, _connect());
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The messages with one helper; requests are numbered, answers find their request by the number.</summary>
    private sealed class Client
    {
        private readonly ScanHelperBackend _owner;
        private readonly IHelperConnection _connection;
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly Dictionary<int, Channel<HelperFrame>> _waiting = [];
        private int _next;
        private volatile bool _alive = true;

        public Client(ScanHelperBackend owner, IHelperConnection connection)
        {
            _owner = owner;
            _connection = connection;
            _ = Task.Run(ReadLoop);
        }

        public bool Alive => _alive;

        /// <summary>Sends a request and waits for its answer (one Result or Error).</summary>
        public async Task<HelperFrame> Call(HelperMessage kind, byte[] json, TimeSpan limit, CancellationToken cancel)
        {
            var (id, answers) = await Send(kind, json);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(limit);
                HelperFrame frame;
                try
                {
                    frame = await answers.Reader.ReadAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
                {
                    Hung();
                    throw;
                }
                catch (ChannelClosedException e) when (e.InnerException is ScanException inner)
                {
                    throw inner;
                }
                return frame.Kind == HelperMessage.Error ? throw ErrorOf(frame) : frame;
            }
            finally
            {
                lock (_waiting) _waiting.Remove(id);
            }
        }

        /// <summary>Sends a scan and hands out its pages as they arrive.</summary>
        public async IAsyncEnumerable<ScannedImage> Scan(ScanSettings settings, IProgress<ScanProgress>? progress, [EnumeratorCancellation] CancellationToken cancel)
        {
            var (id, frames) = await Send(HelperMessage.Scan, HelperWire.Json(settings, HelperJson.Default.ScanSettings));
            var cancelled = false;
            try
            {
                using var stop = cancel.Register(() =>
                {
                    cancelled = true;
                    _ = Send(HelperMessage.Cancel, HelperWire.Json(id.ToString(System.Globalization.CultureInfo.InvariantCulture), HelperJson.Default.String));
                });
                while (true)
                {
                    HelperFrame frame;
                    using (var silence = new CancellationTokenSource(cancelled ? _owner.CancelLimit : _owner.SilenceLimit))
                    {
                        try
                        {
                            frame = await frames.Reader.ReadAsync(silence.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // no sign for too long (or a cancel that does not stop): the driver hangs — end the helper
                            Hung();
                            if (cancelled) throw new OperationCanceledException(cancel);
                            throw new ScanException(Tr.T("Der Scanner antwortet nicht mehr — die Verbindung wurde beendet. Schon gescannte Seiten bleiben."));
                        }
                        catch (ChannelClosedException e) when (e.InnerException is ScanException inner)
                        {
                            throw inner;
                        }
                    }
                    switch (frame.Kind)
                    {
                        case HelperMessage.Progress:
                            if (frame.Read(HelperJson.Default.ScanProgress) is var p) progress?.Report(p);
                            break;
                        case HelperMessage.Page:
                            var head = frame.Read(HelperJson.Default.HelperPage) ?? throw new InvalidDataException("scan helper: page without header");
                            if ((long)head.Width * head.Height * head.Channels != frame.Data.LongLength || head.Channels is not (1 or 3))
                                throw new ScanException(Tr.T("Der Scanner liefert ein unbekanntes Bildformat."));
                            yield return new ScannedImage(head.Width, head.Height, head.Channels, head.Dpi, frame.Data, head.BlackWhite);
                            break;
                        case HelperMessage.Done:
                            yield break;
                        case HelperMessage.Error:
                            throw ErrorOf(frame);
                    }
                }
            }
            finally
            {
                lock (_waiting) _waiting.Remove(id);
            }
        }

        private async Task<(int Id, Channel<HelperFrame> Answers)> Send(HelperMessage kind, byte[] json)
        {
            if (!_alive) throw Crashed();
            var id = Interlocked.Increment(ref _next);
            var answers = Channel.CreateUnbounded<HelperFrame>(new UnboundedChannelOptions { SingleReader = true });
            if (kind != HelperMessage.Cancel) lock (_waiting) _waiting[id] = answers;
            await _write.WaitAsync();
            try
            {
                await HelperWire.WriteAsync(_connection.ToHelper, new HelperFrame(kind, id, json, []));
            }
            catch (IOException)
            {
                Ended();
                throw Crashed();
            }
            finally
            {
                _write.Release();
            }
            return (id, answers);
        }

        private async Task ReadLoop()
        {
            try
            {
                while (await HelperWire.ReadAsync(_connection.FromHelper) is { } frame)
                {
                    Channel<HelperFrame>? target;
                    lock (_waiting) _waiting.TryGetValue(frame.Request, out target);
                    target?.Writer.TryWrite(frame);
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ObjectDisposedException)
            {
                // the pipe broke or carried nonsense: as if the helper had ended
            }
            Ended();
        }

        /// <summary>The helper hangs: end it; everything waiting gets the error.</summary>
        private void Hung()
        {
            try { _connection.Kill(); } catch (Exception) { /* already gone */ }
            Ended();
        }

        /// <summary>The helper has ended: everything waiting gets „crashed“, the next call starts a new helper.</summary>
        private void Ended()
        {
            if (!_alive) return;
            _alive = false;
            List<Channel<HelperFrame>> waiting;
            lock (_waiting) waiting = [.. _waiting.Values];
            foreach (var channel in waiting) channel.Writer.TryComplete(Crashed());
            _ = _connection.DisposeAsync().AsTask();
        }

        private static ScanException Crashed() => new(Tr.T("Der Scanner-Treiber ist abgestürzt — schon gescannte Seiten bleiben. Beim nächsten Scannen wird er neu gestartet."),
            ScanErrorKind.DriverCrashed);

        private static Exception ErrorOf(HelperFrame frame)
        {
            var error = frame.Read(HelperJson.Default.HelperError);
            if (error is null) return new ScanException(Tr.T("Unerwarteter Fehler beim Scannen — Einzelheiten stehen im Protokoll."));
            return error.Cancelled ? new OperationCanceledException() : new ScanException(error.Message, error.Kind);
        }
    }

    /// <summary>A device opened in the helper.</summary>
    private sealed class Session(ScanHelperBackend owner, Client client, ScanDevice device, ScanCapabilities capabilities) : IScanSession
    {
        public ScanDevice Device { get; } = device;

        public ScanCapabilities Capabilities { get; private set; } = capabilities;

        public async Task<ScanCapabilities> SelectSourceAsync(string source, CancellationToken cancel = default)
        {
            var answer = await client.Call(HelperMessage.SelectSource, HelperWire.Json(source, HelperJson.Default.String), owner.CallLimit, cancel);
            Capabilities = answer.Read(HelperJson.Default.ScanCapabilities) ?? Capabilities;
            return Capabilities;
        }

        public IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, IProgress<ScanProgress>? progress = null, CancellationToken cancel = default) =>
            client.Scan(settings, progress, cancel);

        public async ValueTask DisposeAsync()
        {
            if (!client.Alive) return;
            try { await client.Call(HelperMessage.Close, [], owner.CallLimit, CancellationToken.None); }
            catch (Exception e) when (e is ScanException or OperationCanceledException) { /* the helper is gone; nothing to release */ }
        }
    }
}

/// <summary>Starts the program itself as scan helper (<see cref="ScanHelperHost.Argument"/>) with two anonymous pipes.</summary>
internal sealed class HelperProcess : IHelperConnection
{
    private readonly Process _process;
    private readonly AnonymousPipeServerStream _toHelper;
    private readonly AnonymousPipeServerStream _fromHelper;

    /// <param name="executable">The program file (the running program).</param>
    public HelperProcess(string executable)
    {
        _toHelper = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        _fromHelper = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            // drivers write to the console (a network back end its web page): never into the pipes, and not into the program's console
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(ScanHelperHost.Argument);
        start.ArgumentList.Add(_toHelper.GetClientHandleAsString());
        start.ArgumentList.Add(_fromHelper.GetClientHandleAsString());
        _process = Process.Start(start) ?? throw new ScanException(Tr.T("Der Hilfsprozess zum Scannen ließ sich nicht starten."));
        // Windows ends the helper with the program, even when it is caught in a driver call (HelperJob)
        if (OperatingSystem.IsWindows()) HelperJob.Assign(_process.Handle);
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        // the copies of the client ends belong to the helper now
        _toHelper.DisposeLocalCopyOfClientHandle();
        _fromHelper.DisposeLocalCopyOfClientHandle();
    }

    public Stream ToHelper => _toHelper;

    public Stream FromHelper => _fromHelper;

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        await _toHelper.DisposeAsync();   // the helper sees the end of its input and stops
        await _fromHelper.DisposeAsync();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await _process.WaitForExitAsync(wait.Token); }
        catch (OperationCanceledException) { Kill(); }
        _process.Dispose();
    }
}
