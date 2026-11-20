using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using PdfKnead.Core.Platform;

namespace PdfKnead.Core.Scanning.Wia;

/// <summary>
/// An opened WIA device: its root item and the items that are sources (flatbed, feeder — chosen by category, else by name; the front and
/// back items of a feeder are no sources of their own, both sides are switched at the feeder). Every call runs on the worker; cancelling a
/// scan only sets a flag the next callback of the driver answers.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WiaSession : IScanSession
{
    private readonly ScanWorker _worker;
    private IWiaItem2? _root;
    private readonly List<(ScanSource Source, IWiaItem2 Item)> _sources;
    private readonly List<IWiaItem2> _items;

    private WiaSession(ScanWorker worker, ScanDevice device, IWiaItem2 root, List<(ScanSource, IWiaItem2)> sources, List<IWiaItem2> items)
    {
        _worker = worker;
        Device = device;
        _root = root;
        _sources = sources;
        _items = items;
        Capabilities = new ScanCapabilities([], null, [], [], 0, 0);
    }

    /// <inheritdoc/>
    public ScanDevice Device { get; }

    /// <inheritdoc/>
    public ScanCapabilities Capabilities { get; private set; }

    /// <summary>The session for an opened root item (on the worker; opening: <see cref="WiaBackend.Open"/>); the root is released on failure.</summary>
    internal static unsafe WiaSession Build(ScanWorker worker, ScanDevice device, IWiaItem2 root)
    {
        var items = new List<IWiaItem2>();
        var found = new List<(ScanSourceKind Kind, string Name, IWiaItem2 Item)>();
        try
        {
            if (root.EnumChildItems(null, out var children) >= 0 && children is not null)
            {
                try
                {
                    for (var i = 0; i < 64; i++)
                    {
                        if (children.Next(1, out var child, out var fetched) != Wia.SOk || fetched != 1 || child is null) break;
                        items.Add(child);
                        var category = child.GetItemCategory(out var c) >= 0 ? c : (Guid?)null;
                        var name = new WiaPropertyStorage((IWiaPropertyStorage)child).Text(Wia.IpaItemName);
                        if (WiaMapping.SourceKindOf(category, name) is { } kind && !found.Any(f => f.Kind == kind && kind != ScanSourceKind.Other))
                            found.Add((kind, name ?? "", child));
                    }
                }
                finally
                {
                    WiaBackend.Release(children);
                }
            }
            // a driver without recognisable items: its first item that transfers data counts as the flatbed
            if (found.Count == 0 && items.FirstOrDefault(i => i.GetItemType(out var type) >= 0 && (type & Wia.ItemTypeTransfer) != 0) is { } first)
                found.Add((ScanSourceKind.Flatbed, "", first));
            if (found.Count == 0) throw new ScanException(Tr.T("Der Scanner bietet keine Quelle zum Scannen an."));

            var rootProperties = new WiaPropertyStorage((IWiaPropertyStorage)root);
            var sources = new List<(ScanSource, IWiaItem2)>();
            foreach (var (kind, name, item) in found.OrderBy(f => f.Kind))
            {
                switch (kind)
                {
                    case ScanSourceKind.Flatbed:
                        sources.Add((new ScanSource(WiaMapping.FlatbedName, kind), item));
                        break;
                    case ScanSourceKind.Feeder:
                        sources.Add((new ScanSource(WiaMapping.FeederName, kind), item));
                        if (WiaMapping.FeederHasDuplex(new WiaPropertyStorage((IWiaPropertyStorage)item), rootProperties))
                            sources.Add((new ScanSource(WiaMapping.FeederDuplexName, ScanSourceKind.FeederDuplex), item));
                        break;
                    default:
                        sources.Add((new ScanSource(name.Length > 0 ? name : Tr.T("Weitere Quelle"), ScanSourceKind.Other), item));
                        break;
                }
            }
            var session = new WiaSession(worker, device, root, sources, items);
            session.Capabilities = session.CapabilitiesOf(sources[0]);
            return session;
        }
        catch
        {
            foreach (var item in items) WiaBackend.Release(item);
            WiaBackend.Release(root);
            throw;
        }
    }

    private WiaPropertyStorage RootProperties => new((IWiaPropertyStorage)(_root ?? throw Closed()));

    private static ScanException Closed() => new(Tr.T("Der Scanner ist nicht erreichbar — ist er eingeschaltet?"), ScanErrorKind.Offline);

    private ScanCapabilities CapabilitiesOf((ScanSource Source, IWiaItem2 Item) source) =>
        WiaMapping.CapabilitiesOf(new WiaPropertyStorage((IWiaPropertyStorage)source.Item), RootProperties, source.Source.Kind,
            [.. _sources.Select(s => s.Source)], source.Source);

    private (ScanSource Source, IWiaItem2 Item) SourceNamed(string? name) =>
        _sources.FirstOrDefault(s => s.Source.Name == name) is { Item: not null } named ? named
        : _sources.FirstOrDefault(s => s.Source.Name == Capabilities.Source?.Name) is { Item: not null } current ? current
        : _sources[0];

    /// <inheritdoc/>
    public Task<ScanCapabilities> SelectSourceAsync(string source, CancellationToken cancel = default) => _worker.Run(() =>
    {
        cancel.ThrowIfCancellationRequested();
        Capabilities = CapabilitiesOf(SourceNamed(source));
        return Capabilities;
    });

    /// <inheritdoc/>
    public async IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, IProgress<ScanProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancel = default)
    {
        var pages = Channel.CreateUnbounded<ScannedImage>(new UnboundedChannelOptions { SingleReader = true });
        IStoppable? callback = null;
        var gate = new Lock();
        // the flag is read by the next callback of the driver, on whatever thread it comes
        await using var stop = cancel.Register(() => { lock (gate) callback?.Cancel(); });
        var job = _worker.Run(() =>
        {
            try
            {
                Action<IStoppable> made = made => { lock (gate) callback = made; if (cancel.IsCancellationRequested) made.Cancel(); };
                // a driver that refused the transfer of WIA 2.0: the same device through WIA 1.0, for this and every further scan
                if (_legacy is { } legacy) legacy.ScanHere(settings, progress, pages.Writer, cancel, made);
                else Scan(settings, progress, pages.Writer, cancel, made);
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

    /// <summary>The scan job on the worker: settings, then one Download that delivers one page from the glass or the pages of the feeder.</summary>
    private void Scan(ScanSettings settings, IProgress<ScanProgress>? progress, ChannelWriter<ScannedImage> pages, CancellationToken cancel,
        Action<IStoppable> made)
    {
        cancel.ThrowIfCancellationRequested();
        var source = SourceNamed(settings.Source);
        Capabilities = CapabilitiesOf(source);
        var item = new WiaPropertyStorage((IWiaPropertyStorage)source.Item);
        var notes = new List<string>();
        var dpi = WiaMapping.Apply(item, RootProperties, source.Source.Kind, settings, Capabilities, notes);
        foreach (var note in notes) AppLog.Write("Scannen (WIA)", note);
        var feeder = source.Source.Kind is ScanSourceKind.Feeder or ScanSourceKind.FeederDuplex;
        var gray = settings.Mode == ScanColorMode.Gray;

        // the item itself, seen through another interface: not released here (that would release the item)
        var transfer = (IWiaTransfer)source.Item;
        PageCallback callback;
        int hr;
        for (var attempt = 0; ; attempt++)
        {
            callback = new PageCallback((page, _) => pages.TryWrite(WiaImage.Decode(page, dpi, gray)), progress);
            made(callback);
            hr = ForceLegacyForTests ? Wia.EInvalidArg : transfer.Download(0, callback);
            if (callback.Failure is { } failure) throw failure is ScanException or OperationCanceledException ? failure
                : new ScanException(Tr.T("Unerwarteter Fehler beim Scannen — Einzelheiten stehen im Protokoll.") + " " + failure.GetType().Name);
            if (cancel.IsCancellationRequested) throw new OperationCanceledException(cancel);
            // the driver refused before scanning anything (still busy with the last scan …): once more after a wait (Fehlergedächtnis 853)
            if (hr >= 0 || callback.Started || WiaMapping.RetryAfter(hr, attempt, feeder) is not { } wait) break;
            AppLog.Write("Scannen (WIA)", $"Download 0x{hr:X8} before any data, attempt {attempt + 2} in {wait.TotalSeconds:0} s");
            if (cancel.WaitHandle.WaitOne(wait)) throw new OperationCanceledException(cancel);
        }
        if (WiaMapping.FallsBackToLegacy(hr, callback.Started))
        {
            AppLog.Write("Scannen (WIA)", $"Download 0x{hr:X8} before any data: the device through WIA 1.0");
            _legacy = WiaBackend.OpenLegacy(_worker, Device) ?? throw WiaMapping.ErrorOf(hr, feeder);
            _legacy.ScanHere(settings, progress, pages, cancel, made);
            return;
        }
        if (hr >= 0) callback.Complete();
        else if (!WiaMapping.IsEndOfFeeder(hr, feeder, callback.Pages))
        {
            AppLog.Write("Scannen (WIA)", $"Download 0x{hr:X8}, device status 0x{callback.LastDeviceStatus:X8}, pages {callback.Pages}");
            // a general error after the driver reported the device's state (jam, cover open): the state says more
            throw WiaMapping.ErrorOf(hr is Wia.ErrorGeneral or Wia.EFail && callback.LastDeviceStatus < 0 ? callback.LastDeviceStatus : hr, feeder);
        }
        if (callback.Failure is { } late) throw late;
        if (callback.Pages == 0)
            throw feeder ? WiaMapping.ErrorOf(Wia.ErrorPaperEmpty) : new ScanException(Tr.T("Der Scanner hat keine Bilddaten geliefert."));
    }

    /// <summary>The same device through WIA 1.0, once its driver refused a transfer of WIA 2.0 (<see cref="WiaMapping.FallsBackToLegacy"/>).</summary>
    private WiaLegacySession? _legacy;

    /// <summary>Tests: every transfer of WIA 2.0 is taken as refused, so the way back to WIA 1.0 can be tried with any device.</summary>
    internal static bool ForceLegacyForTests { get; set; }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_root is null) return;
        if (_legacy is { } legacy) await legacy.DisposeAsync();
        var root = _root;
        _root = null;
        await _worker.Run(() =>
        {
            foreach (var item in _items) WiaBackend.Release(item);
            WiaBackend.Release(root);
        });
    }
}
