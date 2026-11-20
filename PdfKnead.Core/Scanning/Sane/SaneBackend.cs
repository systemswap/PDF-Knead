namespace PdfKnead.Core.Scanning.Sane;

/// <summary>Scanning under Linux through SANE, the system's scanner interface; network scanners come through its eSCL and WSD back ends.</summary>
public sealed class SaneBackend : IScanBackend
{
    // one worker for the whole process: SANE keeps global state and is not reentrant
    private static readonly Lazy<ScanWorker> Worker = new(() => new ScanWorker("SANE"));

    /// <inheritdoc/>
    public string Name => "SANE";

    /// <summary>How to install SANE on Debian and Fedora (with the back end for network scanners).</summary>
    public static string InstallHint =>
        Tr.T("Zum Scannen fehlt SANE, die Scanner-Schnittstelle von Linux. Debian/Ubuntu: sudo apt install libsane1 sane-utils sane-airscan — Fedora: sudo dnf install sane-backends sane-airscan");

    /// <inheritdoc/>
    public bool IsAvailable(out string? hint)
    {
        var available = SaneLibrary.Load() is not null;
        hint = available ? null : InstallHint;
        return available;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ScanDevice>> FindDevicesAsync(CancellationToken cancel = default)
    {
        var sane = Library();
        cancel.ThrowIfCancellationRequested();
        return await Worker.Value.Run(sane.GetDevices).WaitAsync(cancel);
    }

    /// <inheritdoc/>
    public async Task<IScanSession> OpenAsync(ScanDevice device, CancellationToken cancel = default)
    {
        var sane = Library();
        cancel.ThrowIfCancellationRequested();
        return await Worker.Value.Run(() =>
        {
            // the ways of the device in turn, the best first: another back end when the best cannot reach it (the program chooses the driver
            // itself); the session keeps the device as listed
            ScanException? failed = null;
            foreach (var id in device.AllIds)
            {
                try
                {
                    return SaneSession.Open(sane, Worker.Value, device, id);
                }
                catch (ScanException e) when (e.Kind == ScanErrorKind.Offline)
                {
                    failed ??= e;
                }
            }
            // network back ends (airscan) and some USB back ends give a device a new id after a restart: search once more, in the back end of
            // the best way only, for the same name
            var prefix = device.Id.Split(':')[0] + ":";
            var again = sane.GetDevices().FirstOrDefault(d => d.Id.StartsWith(prefix, StringComparison.Ordinal)
                && string.Equals(d.BaseName, device.BaseName, StringComparison.OrdinalIgnoreCase) && !device.Has(d.Id));
            return again is not null ? SaneSession.Open(sane, Worker.Value, device, again.Id)
                : throw failed ?? new ScanException(Tr.T("Der Scanner ist nicht erreichbar — ist er eingeschaltet?"), ScanErrorKind.Offline);
        });
    }

    private static SaneLibrary Library() =>
        SaneLibrary.Load() ?? throw new ScanException(InstallHint, ScanErrorKind.InterfaceMissing);
}
