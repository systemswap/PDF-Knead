using PdfKnead.Core.Scanning.Helper;
using PdfKnead.Core.Scanning.Sane;

namespace PdfKnead.Core.Scanning;

/// <summary>The scanner interface of the running system.</summary>
public static class ScanBackends
{
    /// <summary>
    /// The program file that can be started as scan helper (<see cref="ScanHelperHost.Argument"/>); set by the program at start, null where
    /// there is none (tests, command line) — then the scanner interface runs in the process itself.
    /// </summary>
    public static string? HelperExecutable { get; set; }

    // one per run of the program: the helper keeps its device search and stays while the program runs
    private static readonly Lazy<IScanBackend> OfThisSystem = new(() =>
    {
        var direct = Direct();
        return HelperExecutable is { } program
            ? new ScanHelperBackend(direct.Name, () => new HelperProcess(program), () => direct.IsAvailable(out var hint) ? null : hint)
            : direct;
    });

    /// <summary>
    /// The scanner interface of the system — WIA under Windows, SANE elsewhere — in a helper process when the program can start one (a
    /// driver that crashes or hangs then never takes the program with it, KONZEPT §5.25).
    /// </summary>
    public static IScanBackend ForThisSystem() => OfThisSystem.Value;

    /// <summary>The scanner interface of the system in this process: what the helper process runs, and the tests.</summary>
    public static IScanBackend Direct() => OperatingSystem.IsWindows() ? new Wia.WiaBackend() : new SaneBackend();
}

/// <summary>A scanner interface that is not there; it only says why.</summary>
/// <param name="name">The name of the interface.</param>
/// <param name="hint">What to tell the user.</param>
public sealed class UnavailableScanBackend(string name, string hint) : IScanBackend
{
    /// <inheritdoc/>
    public string Name { get; } = name;

    /// <inheritdoc/>
    public bool IsAvailable(out string? why)
    {
        why = hint;
        return false;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<ScanDevice>> FindDevicesAsync(CancellationToken cancel = default) =>
        Task.FromException<IReadOnlyList<ScanDevice>>(new ScanException(hint, ScanErrorKind.InterfaceMissing));

    /// <inheritdoc/>
    public Task<IScanSession> OpenAsync(ScanDevice device, CancellationToken cancel = default) =>
        Task.FromException<IScanSession>(new ScanException(hint, ScanErrorKind.InterfaceMissing));
}
