using System.IO.Pipes;
using System.Runtime.CompilerServices;
using PdfKnead.Core.Scanning;
using PdfKnead.Core.Scanning.Helper;

namespace PdfKnead.Tests.Scanning;

/// <summary>
/// The scan helper process (KONZEPT §5.25) without a second process: the helper's loop runs on pipes in this process with a scanner in
/// software. Round trip of devices, capabilities and pages; errors, cancelling, a crash and a hang of the driver.
/// </summary>
public sealed class ScanHelperTests
{
    [Fact]
    public async Task RoundTrip_DevicesSourcesPagesProgress()
    {
        var connections = new List<InProcessHelper>();
        var backend = new ScanHelperBackend("Test", () => Add(connections, new InProcessHelper(new Scanner())), () => null);
        var device = Assert.Single(await backend.FindDevicesAsync());
        Assert.Equal("Muster Scanner", device.DisplayName);
        await using var session = await backend.OpenAsync(device);
        Assert.Equal(ScanSourceKind.Flatbed, session.Capabilities.Source!.Kind);
        var feeder = await session.SelectSourceAsync("Feeder");
        Assert.Equal([75, 150, 300], feeder.Resolutions);
        var reported = new List<ScanProgress>();
        var pages = new List<ScannedImage>();
        await foreach (var page in session.ScanAsync(new ScanSettings("Feeder", 150, ScanColorMode.Gray), new Direct(reported.Add))) pages.Add(page);
        Assert.Equal(3, pages.Count);
        for (var i = 0; i < 3; i++) Assert.Equal(Scanner.Sheet(i).Pixels, pages[i].Pixels);   // samples arrive unchanged
        Assert.Equal(150, pages[0].Dpi);
        Assert.Contains(reported, p => p.Page == 3);
        Assert.Single(connections);   // one helper for everything
    }

    [Fact]
    public void Device_WithItsFurtherWays_ThroughTheWire()
    {
        // the helper opens the device by the ways the program chose: they must arrive, and the device must come back equal (profiles, the list)
        var device = new ScanDevice("escl", "", "Muster MFP 100", "", null, $"wsd{ScanDevice.Separator}hersteller");
        var back = System.Text.Json.JsonSerializer.Deserialize(HelperWire.Json(device, HelperJson.Default.ScanDevice), HelperJson.Default.ScanDevice);
        Assert.Equal(device, back);
        Assert.Equal(["escl", "wsd", "hersteller"], back!.AllIds);
    }

    [Fact]
    public async Task ErrorAfterPages_PagesStay_KindPassedOn()
    {
        var backend = new ScanHelperBackend("Test", () => new InProcessHelper(new Scanner { JamAfter = 1 }), () => null);
        await using var session = await backend.OpenAsync((await backend.FindDevicesAsync())[0]);
        var pages = 0;
        var e = await Assert.ThrowsAsync<ScanException>(async () =>
        {
            await foreach (var _ in session.ScanAsync(new ScanSettings("Feeder", 150, ScanColorMode.Gray))) pages++;
        });
        Assert.Equal(1, pages);
        Assert.Equal(ScanErrorKind.Jammed, e.Kind);
        Assert.Equal("Papierstau", e.Message);
    }

    [Fact]
    public async Task Cancel_StopsTheScan_HelperServesOn()
    {
        var scanner = new Scanner { WaitForCancel = true };
        var backend = new ScanHelperBackend("Test", () => new InProcessHelper(scanner), () => null);
        await using var session = await backend.OpenAsync((await backend.FindDevicesAsync())[0]);
        using var cancel = new CancellationTokenSource();
        var reading = Task.Run(async () =>
        {
            await foreach (var _ in session.ScanAsync(new ScanSettings("Feeder", 150, ScanColorMode.Gray), cancel: cancel.Token)) { }
        });
        await scanner.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(10)));
        // the same helper scans again
        scanner.WaitForCancel = false;
        var pages = 0;
        await foreach (var _ in session.ScanAsync(new ScanSettings("Feeder", 150, ScanColorMode.Gray))) pages++;
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task Crash_PagesStay_NextCallStartsANewHelper()
    {
        var connections = new List<InProcessHelper>();
        var backend = new ScanHelperBackend("Test", () => Add(connections, new InProcessHelper(new Scanner { CrashAfter = 2 })), () => null);
        await using (var session = await backend.OpenAsync((await backend.FindDevicesAsync())[0]))
        {
            var pages = 0;
            var e = await Assert.ThrowsAsync<ScanException>(async () =>
            {
                await foreach (var _ in session.ScanAsync(new ScanSettings("Feeder", 150, ScanColorMode.Gray))) pages++;
            });
            Assert.Equal(2, pages);
            Assert.Equal(ScanErrorKind.DriverCrashed, e.Kind);
        }
        // a new helper on the next call
        Assert.NotEmpty(await backend.FindDevicesAsync());
        Assert.Equal(2, connections.Count);
    }

    [Fact]
    public async Task Hang_HelperEnded_AfterTheSilenceLimit()
    {
        var connections = new List<InProcessHelper>();
        var backend = new ScanHelperBackend("Test", () => Add(connections, new InProcessHelper(new Scanner { Hang = true })), () => null)
        {
            SilenceLimit = TimeSpan.FromMilliseconds(400),
        };
        await using var session = await backend.OpenAsync((await backend.FindDevicesAsync())[0]);
        var e = await Assert.ThrowsAsync<ScanException>(async () =>
        {
            await foreach (var _ in session.ScanAsync(new ScanSettings("Feeder", 150, ScanColorMode.Gray))) { }
        });
        Assert.Contains(Tr.T("Der Scanner antwortet nicht mehr — die Verbindung wurde beendet. Schon gescannte Seiten bleiben."), e.Message);
        Assert.True(connections[0].Killed);
    }

    [Fact]
    public async Task Wire_LengthsBeyondTheLimits_Refused()
    {
        var bytes = new byte[13];
        bytes[0] = (byte)HelperMessage.Result;
        BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 5);   // JSON part of 2 GB
        await Assert.ThrowsAsync<InvalidDataException>(() => HelperWire.ReadAsync(new MemoryStream(bytes)));
        Assert.Null(await HelperWire.ReadAsync(new MemoryStream([1, 2, 3])));   // cut off: the other side has gone
    }

    private static InProcessHelper Add(List<InProcessHelper> list, InProcessHelper helper)
    {
        lock (list) list.Add(helper);
        return helper;
    }

    private sealed class Direct(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            lock (this) report(value);
        }
    }

    /// <summary>The helper's loop on two pipes in this process; „killing“ closes the helper's side.</summary>
    private sealed class InProcessHelper : IHelperConnection
    {
        private readonly AnonymousPipeServerStream _toHelper = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _fromHelper = new(PipeDirection.In);
        private readonly AnonymousPipeClientStream _helperIn;
        private readonly AnonymousPipeClientStream _helperOut;

        public InProcessHelper(Scanner scanner)
        {
            _helperIn = new AnonymousPipeClientStream(PipeDirection.In, _toHelper.ClientSafePipeHandle);
            _helperOut = new AnonymousPipeClientStream(PipeDirection.Out, _fromHelper.ClientSafePipeHandle);
            scanner.Crash = () => _helperOut.Dispose();
            _ = Task.Run(() => ScanHelperHost.ServeAsync(_helperIn, _helperOut, scanner));
        }

        public bool Killed { get; private set; }

        public Stream ToHelper => _toHelper;

        public Stream FromHelper => _fromHelper;

        public void Kill()
        {
            Killed = true;
            _helperOut.Dispose();
            _helperIn.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            _toHelper.Dispose();
            _fromHelper.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A scanner in software: flatbed and a feeder with three sheets of 40 × 30 grey pixels.</summary>
    private sealed class Scanner : IScanBackend
    {
        public int JamAfter = -1;
        public int CrashAfter = -1;
        public bool Hang;
        public volatile bool WaitForCancel;
        public Action? Crash;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "Test";

        public bool IsAvailable(out string? hint)
        {
            hint = null;
            return true;
        }

        public Task<IReadOnlyList<ScanDevice>> FindDevicesAsync(CancellationToken cancel = default) =>
            Task.FromResult<IReadOnlyList<ScanDevice>>([new ScanDevice("muster:1", "Muster", "Scanner", "flatbed")]);

        public Task<IScanSession> OpenAsync(ScanDevice device, CancellationToken cancel = default) => Task.FromResult<IScanSession>(new Session(this, device));

        public static ScannedImage Sheet(int number) =>
            new(40, 30, 1, 150, [.. Enumerable.Range(0, 1200).Select(i => (byte)((i * 7 + number * 31) & 0xFF))]);

        private sealed class Session(Scanner owner, ScanDevice device) : IScanSession
        {
            private string _source = "Flatbed";

            public ScanDevice Device => device;

            public ScanCapabilities Capabilities => new([new("Flatbed", ScanSourceKind.Flatbed), new("Feeder", ScanSourceKind.Feeder)],
                ScanSource.FromName(_source), _source == "Feeder" ? [75, 150, 300] : [75, 150, 300, 600], [ScanColorMode.Gray], 216, 297);

            public Task<ScanCapabilities> SelectSourceAsync(string source, CancellationToken cancel = default)
            {
                _source = source;
                return Task.FromResult(Capabilities);
            }

            public async IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, IProgress<ScanProgress>? progress = null,
                [EnumeratorCancellation] CancellationToken cancel = default)
            {
                for (var sheet = 0; sheet < 3; sheet++)
                {
                    if (owner.Hang) await Task.Delay(Timeout.Infinite, CancellationToken.None);
                    if (owner.WaitForCancel)
                    {
                        owner.Waiting.TrySetResult();
                        await Task.Delay(Timeout.Infinite, cancel);
                    }
                    if (sheet == owner.JamAfter) throw new ScanException("Papierstau", ScanErrorKind.Jammed);
                    if (sheet == owner.CrashAfter)
                    {
                        await Task.Delay(100, CancellationToken.None);   // the pages before have gone out
                        owner.Crash?.Invoke();
                        await Task.Delay(Timeout.Infinite, CancellationToken.None);
                    }
                    progress?.Report(new ScanProgress(sheet + 1, 0.5));
                    yield return Sheet(sheet) with { Dpi = settings.Dpi };
                }
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
