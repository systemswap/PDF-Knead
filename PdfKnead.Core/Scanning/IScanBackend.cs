namespace PdfKnead.Core.Scanning;

/// <summary>
/// The scanner interface of a system (SANE under Linux, WIA under Windows) behind one narrow surface: find devices, open one, read what
/// it offers, scan pages. The user interface knows only this.
/// </summary>
public interface IScanBackend
{
    /// <summary>The name of the interface for messages, e.g. „SANE“.</summary>
    string Name { get; }

    /// <summary>Whether the interface is installed; if not, <paramref name="hint"/> tells the user how to install it (translated).</summary>
    /// <param name="hint">The installation hint, or null when available.</param>
    /// <returns>Whether scanning is possible on this system.</returns>
    bool IsAvailable(out string? hint);

    /// <summary>Looks for scanners; network back ends can take some seconds.</summary>
    /// <param name="cancel">Cancels the search as far as the interface allows.</param>
    /// <returns>The devices found, possibly none.</returns>
    /// <exception cref="ScanException">The interface failed.</exception>
    Task<IReadOnlyList<ScanDevice>> FindDevicesAsync(CancellationToken cancel = default);

    /// <summary>Opens a scanner for a session (one session per device at a time).</summary>
    /// <param name="device">The device.</param>
    /// <param name="cancel">Cancels opening.</param>
    /// <returns>The session; dispose it to release the device.</returns>
    /// <exception cref="ScanException">The device cannot be opened (busy, gone, no access).</exception>
    Task<IScanSession> OpenAsync(ScanDevice device, CancellationToken cancel = default);
}

/// <summary>An opened scanner.</summary>
public interface IScanSession : IAsyncDisposable
{
    /// <summary>The device.</summary>
    ScanDevice Device { get; }

    /// <summary>What the device offers for the current source.</summary>
    ScanCapabilities Capabilities { get; }

    /// <summary>Selects a source and reads what the device offers with it (resolutions and area often differ per source).</summary>
    /// <param name="source">The source name as the driver names it.</param>
    /// <param name="cancel">Cancels the call.</param>
    /// <returns>The capabilities with this source.</returns>
    /// <exception cref="ScanException">The device refused.</exception>
    Task<ScanCapabilities> SelectSourceAsync(string source, CancellationToken cancel = default);

    /// <summary>
    /// Scans: one page from the flatbed, or pages from the feeder until it is empty. Each page is handed out as soon as it is complete, so
    /// pages read before an error are kept.
    /// </summary>
    /// <param name="settings">Source, resolution, colour mode and area; values the device does not offer are replaced by the closest.</param>
    /// <param name="progress">Receives the progress of the page being read.</param>
    /// <param name="cancel">Stops the scanner; pages already handed out stay.</param>
    /// <returns>The pages.</returns>
    /// <exception cref="ScanException">The device reported an error (jam, open cover, empty feeder before the first page …).</exception>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    IAsyncEnumerable<ScannedImage> ScanAsync(ScanSettings settings, IProgress<ScanProgress>? progress = null, CancellationToken cancel = default);
}
