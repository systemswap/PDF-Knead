using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using PdfKnead.Core.Scanning.Escl;

namespace PdfKnead.Core.Scanning;

/// <summary>
/// The scanner interface of the system (SANE or WIA) together with the own eSCL access (KONZEPT.md §5.25, S9d; decided by the user: eSCL is
/// the best way, SANE and WIA stay as tested ways and fallback). A device both find is one entry, its eSCL way first, the system's ways as
/// <see cref="ScanDevice.Alternates"/>. Opening falls back silently on the system's way when eSCL cannot reach the device. After a job ends
/// with an <see cref="EsclProtocolException"/> — a problem of the eSCL access with this device —, eSCL counts as disturbed for this device
/// until the program ends and the next scan takes the system's way; never in the middle of a job (pages could come twice).
/// </summary>
public sealed class CombinedScanBackend : IScanBackend
{
    private readonly IScanBackend _system;
    private readonly EsclBackend _escl;
    private readonly ConcurrentDictionary<string, bool> _disturbed = new(StringComparer.Ordinal);

    /// <summary>Joins the two.</summary>
    /// <param name="system">The scanner interface of the system.</param>
    /// <param name="escl">The own eSCL access; it asks its settings whether it is on.</param>
    public CombinedScanBackend(IScanBackend system, EsclBackend escl)
    {
        _system = system;
        _escl = escl;
    }

    /// <summary>The eSCL access (its diagnosis log, its settings).</summary>
    public EsclBackend Escl => _escl;

    /// <inheritdoc/>
    public string Name => _system.Name;

    /// <inheritdoc/>
    /// <remarks>With the eSCL access on, scanning is possible even without the system's interface (network scanners only).</remarks>
    public bool IsAvailable(out string? hint)
    {
        if (_system.IsAvailable(out hint)) return true;
        if (!_escl.Enabled) return false;
        hint = null;
        return true;
    }

    /// <summary>The eSCL session behind a session of this interface, or null when the system's way is used.</summary>
    public static EsclSession? EsclOf(IScanSession? session) => session switch
    {
        Session combined => combined.Escl,
        EsclSession escl => escl,
        _ => null,
    };

    /// <summary>Whether the device can also be reached through the system's interface.</summary>
    public static bool HasSystemWay(ScanDevice device) => device.AllIds.Any(i => !EsclBackend.Owns(i));

    /// <summary>Whether eSCL counts as disturbed for a device until the program ends.</summary>
    public bool IsDisturbed(ScanDevice device) => device.AllIds.Any(i => EsclBackend.Owns(i) && _disturbed.ContainsKey(i));

    /// <summary>Takes the system's way for this device until the program ends (also when the user declines its certificate).</summary>
    public void Avoid(ScanDevice device, string why)
    {
        foreach (var id in device.AllIds.Where(EsclBackend.Owns))
            if (_disturbed.TryAdd(id, true)) _escl.Log.Add("Rückfall", $"{device.DisplayName}: ab jetzt über {_system.Name} — {why}");
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ScanDevice>> FindDevicesAsync(CancellationToken cancel = default)
    {
        var useEscl = _escl.Enabled;
        var systemTask = _system.IsAvailable(out _) ? _system.FindDevicesAsync(cancel) : Task.FromResult<IReadOnlyList<ScanDevice>>([]);
        var esclTask = useEscl ? _escl.FindDevicesAsync(cancel) : Task.FromResult<IReadOnlyList<ScanDevice>>([]);
        IReadOnlyList<ScanDevice> system = [], escl = [];
        ScanException? systemFailed = null;
        try
        {
            system = await systemTask;
        }
        catch (ScanException e) when (useEscl)
        {
            systemFailed = e;
            _escl.Log.Add("Suche", $"{_system.Name}: {e.Message}");
        }
        try
        {
            escl = await esclTask;
        }
        catch (ScanException e)
        {
            _escl.Log.Add("Suche", "eSCL: " + e.Message);
        }
        if (systemFailed is not null && escl.Count == 0) throw systemFailed;
        return Join(system, escl);
    }

    /// <summary>
    /// Joins the devices of both: an eSCL device and a system device are one when they share an address or have the same name (network back
    /// ends of the system name a device as it announces itself). The order is the system's, devices only eSCL found come after.
    /// </summary>
    internal static List<ScanDevice> Join(IReadOnlyList<ScanDevice> system, IReadOnlyList<ScanDevice> escl)
    {
        var keyed = new List<(ScanDevice, string?, int)>();
        var claimed = new HashSet<ScanDevice>();
        var keys = new Dictionary<ScanDevice, string>();
        foreach (var e in escl)
        {
            var matches = system.Where(s => !claimed.Contains(s) && Same(e, s)).ToList();
            foreach (var s in matches.Take(1))
            {
                claimed.Add(s);
                keys[s] = e.Id;
            }
        }
        foreach (var s in system) keyed.Add((s, keys.GetValueOrDefault(s), 1));
        foreach (var e in escl) keyed.Add((e, e.Id, 0));
        // a device installed in the system keeps the name it has there (design decision: „Hostname – Typ“ made it too long) — the eSCL way
        // stays the first way to it; the host name is for network scanners the system does not know
        return [.. ScanDevice.Combine(keyed).Select(d => d.AllIds.Any(EsclBackend.Owns) && system.FirstOrDefault(s => d.AlternateIds.Contains(s.Id)) is { } installed
            ? d with { Vendor = installed.Vendor, Model = installed.Model, Host = null }
            : d)];
    }

    private static bool Same(ScanDevice escl, ScanDevice system)
    {
        var addresses = ScanDevice.Addresses(escl).ToList();
        if (ScanDevice.Addresses(system).Any(a => addresses.Any(b => a.Equals(b)))) return true;
        return Normal(escl.BaseName) is { Length: > 0 } n && n == Normal(system.BaseName);
    }

    private static string Normal(string name) => string.Join(' ', name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <inheritdoc/>
    /// <exception cref="EsclCertificateException">The eSCL way presents a certificate the user has not confirmed (the program asks, then opens
    /// again — or calls <see cref="Avoid"/> when the user declines).</exception>
    public async Task<IScanSession> OpenAsync(ScanDevice device, CancellationToken cancel = default)
    {
        var systemIds = device.AllIds.Where(i => !EsclBackend.Owns(i)).ToList();
        var hasEscl = device.AllIds.Any(EsclBackend.Owns);
        if (hasEscl && _escl.Enabled && !IsDisturbed(device))
        {
            try
            {
                return new Session(this, device, await _escl.OpenAsync(device, cancel), systemIds);
            }
            catch (ScanException e) when (e is not EsclCertificateException && systemIds.Count > 0)
            {
                _escl.Log.Add("Rückfall", $"{device.DisplayName}: eSCL nicht geöffnet ({e.Message}) — diesmal über {_system.Name}");
            }
        }
        if (systemIds.Count == 0)
            return hasEscl ? new Session(this, device, await _escl.OpenAsync(device, cancel), [])
                : throw new ScanException(Tr.T("Der Scanner ist nicht erreichbar — ist er eingeschaltet?"), ScanErrorKind.Offline);
        return new Session(this, device, await OpenSystemAsync(device, systemIds, cancel), systemIds);
    }

    private Task<IScanSession> OpenSystemAsync(ScanDevice device, List<string> systemIds, CancellationToken cancel) =>
        _system.OpenAsync(device with { Id = systemIds[0], Alternates = systemIds.Count > 1 ? string.Join(ScanDevice.Separator, systemIds.Skip(1)) : null }, cancel);

    /// <summary>
    /// A session that keeps the device as listed and changes to the system's way before the next job once eSCL is disturbed for it; the
    /// source chosen for eSCL is carried over by its kind (flatbed, feeder, both sides).
    /// </summary>
    private sealed class Session(CombinedScanBackend owner, ScanDevice device, IScanSession inner, List<string> systemIds) : IScanSession
    {
        private IScanSession _inner = inner;

        public ScanDevice Device => device;

        public ScanCapabilities Capabilities => _inner.Capabilities;

        public Task<ScanCapabilities> SelectSourceAsync(string source, CancellationToken cancel = default) =>
            _inner.SelectSourceAsync(Translate(source), cancel);

        /// <summary>The eSCL session, or null when the system's way is used.</summary>
        public EsclSession? Escl => _inner as EsclSession;

        public async IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, IProgress<ScanProgress>? progress = null,
            [EnumeratorCancellation] CancellationToken cancel = default)
        {
            if (_inner is EsclSession && systemIds.Count > 0 && owner.IsDisturbed(device))
            {
                var kind = KindOf(settings.Source);
                await _inner.DisposeAsync();
                _inner = await owner.OpenSystemAsync(device, systemIds, cancel);
                var source = _inner.Capabilities.Sources.FirstOrDefault(s => s.Kind == kind);
                if (source is not null && source.Name != _inner.Capabilities.Source?.Name) await _inner.SelectSourceAsync(source.Name, cancel);
                settings = settings with { Source = source?.Name ?? _inner.Capabilities.Source?.Name };
            }
            else settings = settings with { Source = settings.Source is { } s ? Translate(s) : null };
            var escl = _inner is EsclSession;
            await using var pages = _inner.ScanAsync(settings, progress, cancel).GetAsyncEnumerator(cancel);
            while (true)
            {
                try
                {
                    if (!await pages.MoveNextAsync()) yield break;
                }
                catch (EsclProtocolException e) when (escl && systemIds.Count > 0)
                {
                    owner.Avoid(device, e.Message);
                    throw;
                }
                yield return pages.Current;
            }
        }

        private ScanSourceKind KindOf(string? source) =>
            source is null ? ScanSourceKind.Flatbed
            : _inner.Capabilities.Sources.FirstOrDefault(s => s.Name == source)?.Kind ?? ScanSource.FromName(source).Kind;

        // a source name of the other way (the window may still show the eSCL names): the source of the same kind
        private string Translate(string source)
        {
            var sources = _inner.Capabilities.Sources;
            if (sources.Count == 0 || sources.Any(s => s.Name == source)) return source;
            var kind = EsclCapabilities.InputOf(source) switch
            {
                EsclInput.Platen => ScanSourceKind.Flatbed,
                EsclInput.Feeder => ScanSourceKind.Feeder,
                EsclInput.FeederDuplex => ScanSourceKind.FeederDuplex,
                _ => ScanSource.FromName(source).Kind,
            };
            return sources.FirstOrDefault(s => s.Kind == kind)?.Name ?? source;
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
