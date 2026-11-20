using System.IO.Pipes;

namespace PdfKnead.Core.Scanning.Helper;

/// <summary>
/// The scan helper process: serves one program over two pipes, with the system's scanner interface in this process only. A driver that
/// crashes or hangs then takes down the helper, never the program with its open documents (KONZEPT §5.25). The helper ends when the
/// program closes its pipe (also when the program ends unexpectedly).
/// </summary>
public static class ScanHelperHost
{
    /// <summary>The argument that starts the program as scan helper, followed by the handles of the two pipes.</summary>
    public const string Argument = "--scan-helper";

    /// <summary>Runs the helper on the pipes whose handles the program passed (program → helper, helper → program).</summary>
    /// <param name="inHandle">The handle of the pipe the program writes to.</param>
    /// <param name="outHandle">The handle of the pipe the program reads from.</param>
    /// <param name="backend">The scanner interface.</param>
    /// <returns>The exit code: 0 when the program closed the pipe.</returns>
    public static int Run(string inHandle, string outHandle, IScanBackend backend)
    {
        using var input = new AnonymousPipeClientStream(PipeDirection.In, inHandle);
        using var output = new AnonymousPipeClientStream(PipeDirection.Out, outHandle);
        ServeAsync(input, output, backend).GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>Serves requests until the input ends.</summary>
    internal static async Task ServeAsync(Stream input, Stream output, IScanBackend backend)
    {
        var write = new SemaphoreSlim(1, 1);
        IScanSession? session = null;
        var scans = new Dictionary<int, CancellationTokenSource>();
        var running = new List<Task>();

        async Task Send(HelperFrame frame)
        {
            await write.WaitAsync();
            try { await HelperWire.WriteAsync(output, frame); }
            finally { write.Release(); }
        }

        Task Fail(int request, Exception e) => Send(new HelperFrame(HelperMessage.Error, request,
            HelperWire.Json(e switch
            {
                OperationCanceledException => new HelperError("", ScanErrorKind.Other, Cancelled: true),
                ScanException s => new HelperError(s.Message, s.Kind),
                _ => new HelperError(Tr.T("Unerwarteter Fehler beim Scannen — Einzelheiten stehen im Protokoll.") + " " + e.GetType().Name, ScanErrorKind.Other),
            }, HelperJson.Default.HelperError), []));

        while (await HelperWire.ReadAsync(input) is { } frame)
        {
            var id = frame.Request;
            try
            {
                switch (frame.Kind)
                {
                    case HelperMessage.FindDevices:
                        var devices = await backend.FindDevicesAsync();
                        await Send(new HelperFrame(HelperMessage.Result, id, HelperWire.Json(devices.ToList(), HelperJson.Default.ListScanDevice), []));
                        break;
                    case HelperMessage.Open:
                        if (session is not null) await session.DisposeAsync();
                        session = null;
                        var device = frame.Read(HelperJson.Default.ScanDevice) ?? throw new InvalidDataException("no device");
                        session = await backend.OpenAsync(device);
                        await Send(new HelperFrame(HelperMessage.Result, id,
                            HelperWire.Json(new HelperOpened(session.Device, session.Capabilities), HelperJson.Default.HelperOpened), []));
                        break;
                    case HelperMessage.SelectSource:
                        var opened = session ?? throw new ScanException(Tr.T("Der Scanner ist nicht erreichbar — ist er eingeschaltet?"), ScanErrorKind.Offline);
                        var caps = await opened.SelectSourceAsync(frame.Read(HelperJson.Default.String) ?? "");
                        await Send(new HelperFrame(HelperMessage.Result, id, HelperWire.Json(caps, HelperJson.Default.ScanCapabilities), []));
                        break;
                    case HelperMessage.Scan:
                        var current = session ?? throw new ScanException(Tr.T("Der Scanner ist nicht erreichbar — ist er eingeschaltet?"), ScanErrorKind.Offline);
                        var settings = frame.Read(HelperJson.Default.ScanSettings) ?? throw new InvalidDataException("no settings");
                        var stop = new CancellationTokenSource();
                        scans[id] = stop;
                        // the scan runs beside the loop, which goes on reading (a cancel must get through)
                        running.Add(Task.Run(async () =>
                        {
                            try
                            {
                                var progress = new DirectProgress(p => _ = Send(new HelperFrame(HelperMessage.Progress, id, HelperWire.Json(p, HelperJson.Default.ScanProgress), [])));
                                await foreach (var page in current.ScanAsync(settings, progress, stop.Token))
                                    await Send(new HelperFrame(HelperMessage.Page, id,
                                        HelperWire.Json(new HelperPage(page.Width, page.Height, page.Channels, page.Dpi, page.BlackWhite), HelperJson.Default.HelperPage),
                                        page.Pixels));
                                await Send(new HelperFrame(HelperMessage.Done, id, [], []));
                            }
                            catch (Exception e)
                            {
                                await Fail(id, e);
                            }
                            finally
                            {
                                lock (scans) scans.Remove(id);
                            }
                        }));
                        break;
                    case HelperMessage.Cancel:
                        lock (scans) if (scans.TryGetValue(frame.Read(HelperJson.Default.String) is { } s && int.TryParse(s, out var target) ? target : -1, out var cts)) cts.Cancel();
                        break;
                    case HelperMessage.Close:
                        if (session is not null) await session.DisposeAsync();
                        session = null;
                        await Send(new HelperFrame(HelperMessage.Result, id, [], []));
                        break;
                }
            }
            catch (Exception e)
            {
                await Fail(id, e);
            }
        }
        // the program has gone: stop what runs, release the device
        lock (scans) foreach (var cts in scans.Values) cts.Cancel();
        try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { /* ending anyway */ }
        // releasing the device may hang in the driver too: not longer than the scans were given
        if (session is not null)
            try { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { /* ending anyway */ }
    }

    /// <summary>Progress passed on at once, on the reporting thread.</summary>
    private sealed class DirectProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
