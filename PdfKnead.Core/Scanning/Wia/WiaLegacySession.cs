using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using PdfKnead.Core.Platform;

namespace PdfKnead.Core.Scanning.Wia;

/// <summary>
/// A device opened through WIA 1.0 — for devices the WIA 2.0 device manager does not offer (Windows' class driver for eSCL). The device has
/// one item to scan with; the source (flatbed, feeder, feeder both sides) is chosen at the device. A scan transfers one page per call, in
/// bands; the feeder is read call by call until it reports no more paper. Every call runs on the worker.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WiaLegacySession : IScanSession
{
    /// <summary>The most pages of one feeder job (a driver that never reports the end must not loop forever).</summary>
    internal const int MaxPages = 2000;

    private readonly ScanWorker _worker;
    private IWiaItem? _root;
    private readonly IWiaItem _item;
    private readonly List<ScanSource> _sources;

    private WiaLegacySession(ScanWorker worker, ScanDevice device, IWiaItem root, IWiaItem item, List<ScanSource> sources)
    {
        _worker = worker;
        Device = device;
        _root = root;
        _item = item;
        _sources = sources;
        Capabilities = new ScanCapabilities([], null, [], [], 0, 0);
    }

    /// <inheritdoc/>
    public ScanDevice Device { get; }

    /// <inheritdoc/>
    public ScanCapabilities Capabilities { get; private set; }

    /// <summary>The session for an opened root item (on the worker); the root is released if no item to scan with is found.</summary>
    internal static WiaLegacySession Build(ScanWorker worker, ScanDevice device, IWiaItem root)
    {
        IWiaItem? item = null;
        try
        {
            if (root.EnumChildItems(out var children) >= 0 && children is not null)
            {
                try
                {
                    for (var i = 0; i < 64 && item is null; i++)
                    {
                        if (children.Next(1, out var child, out var fetched) != Wia.SOk || fetched != 1 || child is null) break;
                        // the item that transfers data (drivers call it „Scan“); folders and others are released
                        if (child.GetItemType(out var type) >= 0 && (type & (Wia.ItemTypeTransfer | 0x1)) != 0 && (type & Wia.ItemTypeFolder) == 0) item = child;
                        else WiaBackend.Release(child);
                    }
                }
                finally
                {
                    WiaBackend.Release(children);
                }
            }
            if (item is null) throw new ScanException(Tr.T("Der Scanner bietet keine Quelle zum Scannen an."));
            var rootProperties = new WiaPropertyStorage((IWiaPropertyStorage)root);
            var session = new WiaLegacySession(worker, device, root, item, WiaMapping.LegacySourcesOf(rootProperties));
            session.Capabilities = session.Select(session._sources[0]);
            return session;
        }
        catch
        {
            WiaBackend.Release(item);
            WiaBackend.Release(root);
            throw;
        }
    }

    private WiaPropertyStorage RootProperties => new((IWiaPropertyStorage)(_root ?? throw new ScanException(
        Tr.T("Der Scanner ist nicht erreichbar — ist er eingeschaltet?"), ScanErrorKind.Offline)));

    private WiaPropertyStorage ItemProperties => new((IWiaPropertyStorage)_item);

    /// <summary>Chooses the source at the device and reads what the item offers with it (the resolutions often differ per source).</summary>
    private ScanCapabilities Select(ScanSource source)
    {
        var root = RootProperties;
        if (root.Allowed(Wia.IpsDocumentHandlingSelect) is { Writable: true } allowed && allowed.Allows(WiaMapping.SelectOf(source.Kind))
            && root.Set(Wia.IpsDocumentHandlingSelect, WiaMapping.SelectOf(source.Kind)) is var hr and < 0)
            AppLog.Write("Scannen (WIA 1.0)", $"select {source.Name}: 0x{hr:X8}");
        return WiaMapping.CapabilitiesOf(ItemProperties, root, source.Kind, _sources, source);
    }

    private ScanSource SourceNamed(string? name) =>
        _sources.FirstOrDefault(s => s.Name == name) ?? _sources.FirstOrDefault(s => s.Name == Capabilities.Source?.Name) ?? _sources[0];

    /// <inheritdoc/>
    public Task<ScanCapabilities> SelectSourceAsync(string source, CancellationToken cancel = default) => _worker.Run(() =>
    {
        cancel.ThrowIfCancellationRequested();
        Capabilities = Select(SourceNamed(source));
        return Capabilities;
    });

    /// <inheritdoc/>
    public async IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, IProgress<ScanProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancel = default)
    {
        var pages = Channel.CreateUnbounded<ScannedImage>(new UnboundedChannelOptions { SingleReader = true });
        IStoppable? callback = null;
        var gate = new Lock();
        await using var stop = cancel.Register(() => { lock (gate) callback?.Cancel(); });
        var job = _worker.Run(() =>
        {
            try
            {
                ScanHere(settings, progress, pages.Writer, cancel, made => { lock (gate) callback = made; if (cancel.IsCancellationRequested) made.Cancel(); });
                pages.Writer.Complete();
            }
            catch (Exception e)
            {
                pages.Writer.Complete(e);
            }
        });
        await foreach (var page in pages.Reader.ReadAllAsync(CancellationToken.None)) yield return page;
        await job;
    }

    /// <summary>The scan job, on the worker (also for a WIA 2.0 session whose driver refused its transfer).</summary>
    internal unsafe void ScanHere(ScanSettings settings, IProgress<ScanProgress>? progress, ChannelWriter<ScannedImage> pages, CancellationToken cancel,
        Action<IStoppable> made)
    {
        cancel.ThrowIfCancellationRequested();
        var source = SourceNamed(settings.Source);
        Capabilities = Select(source);
        var notes = new List<string>();
        var dpi = WiaMapping.Apply(ItemProperties, RootProperties, source.Kind, settings, Capabilities, notes, legacy: true);
        foreach (var note in notes) AppLog.Write("Scannen (WIA 1.0)", note);
        var feeder = source.Kind is ScanSourceKind.Feeder or ScanSourceKind.FeederDuplex;
        var gray = settings.Mode == ScanColorMode.Gray;
        var callback = new BandCallback((page, _) => pages.TryWrite(WiaImage.Decode(page, dpi, gray)), progress);
        made(callback);
        // the item itself, seen through another interface: not released here (that would release the item)
        var transfer = (IWiaDataTransfer)_item;
        {
            for (int round = 0, attempt = 0; round < MaxPages; round++)
            {
                var before = callback.Pages;
                var bytes = callback.Bytes;
                // bands of 256 KB, two buffers: the driver fills one while the program copies the other
                var info = new WiaDataTransferInfo { Size = (uint)sizeof(WiaDataTransferInfo), BufferSize = 256 * 1024, DoubleBuffer = 1 };
                var hr = transfer.GetBandedData(&info, callback);
                if (callback.Failure is { } failure) throw failure is ScanException or OperationCanceledException ? failure
                    : new ScanException(Tr.T("Unerwarteter Fehler beim Scannen — Einzelheiten stehen im Protokoll.") + " " + failure.GetType().Name);
                if (cancel.IsCancellationRequested) throw new OperationCanceledException(cancel);
                if (hr < 0)
                {
                    callback.Drop();
                    if (WiaMapping.IsEndOfFeeder(hr, feeder, callback.Pages)) break;
                    // refused before any data (still busy with the last scan …): the same page once more after a wait (Fehlergedächtnis 853)
                    if (callback.Bytes == bytes && WiaMapping.RetryAfter(hr, attempt++, feeder) is { } wait)
                    {
                        AppLog.Write("Scannen (WIA 1.0)", $"transfer 0x{hr:X8} before any data, again in {wait.TotalSeconds:0} s");
                        if (cancel.WaitHandle.WaitOne(wait)) throw new OperationCanceledException(cancel);
                        round--;
                        continue;
                    }
                    AppLog.Write("Scannen (WIA 1.0)", $"transfer 0x{hr:X8}, pages {callback.Pages}");
                    throw WiaMapping.ErrorOf(hr, feeder);
                }
                callback.Complete();
                attempt = 0;   // the waits count per page
                if (callback.Failure is { } late) throw late;
                // the flatbed gives one page; a feeder transfer that brought nothing (or ended the medium) means the feeder is empty; one that
                // brought several pages comes from a driver that reads all pages in one transfer — the job is done (asking again would only
                // draw in paper laid on the feeder meanwhile, as seen with the class driver for eSCL); after exactly one page it is asked again,
                // for drivers that give one page per transfer
                if (!feeder || hr == Wia.StatusEndOfMedia || hr == Wia.SFalse || callback.Pages - before != 1) break;
            }
        }
        if (callback.Pages == 0)
            throw feeder ? WiaMapping.ErrorOf(Wia.ErrorPaperEmpty) : new ScanException(Tr.T("Der Scanner hat keine Bilddaten geliefert."));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_root is null) return;
        var root = _root;
        _root = null;
        await _worker.Run(() =>
        {
            WiaBackend.Release(_item);
            WiaBackend.Release(root);
        });
    }
}
