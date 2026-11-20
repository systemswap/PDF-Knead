using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using PdfKnead.Core.Platform;

namespace PdfKnead.Core.Scanning.Wia;

/// <summary>
/// Scanning under Windows through WIA 2.0, the system's scanner interface; network scanners come through Windows' class drivers for eSCL
/// and WSD. Every call runs on one worker thread in the multithreaded apartment: the drivers run in the WIA service, not in this process,
/// so no message loop is needed, and the callbacks of a transfer arrive on threads of the COM runtime while Download waits.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WiaBackend : IScanBackend
{
    // one worker for the whole process: COM objects stay on the thread that made them
    private static readonly Lazy<ScanWorker> Worker = new(() => new ScanWorker("WIA"));

    /// <summary>The COM wrappers for the generated interfaces.</summary>
    internal static readonly StrategyBasedComWrappers Wrappers = new();

    private static readonly Guid IidDevMgr2 = new("79C07CF1-CBDD-41ee-8EC3-F00080CADA7A");

    /// <inheritdoc/>
    public string Name => "WIA";

    /// <inheritdoc/>
    /// <remarks>WIA belongs to Windows: nothing to install. Whether its service runs shows when searching (then the hint is the error).</remarks>
    public bool IsAvailable(out string? hint)
    {
        var available = OperatingSystem.IsWindows() && Environment.Is64BitProcess;
        hint = available ? null : Tr.T("Scannen über WIA braucht das 64-Bit-Programm unter Windows.");
        return available;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ScanDevice>> FindDevicesAsync(CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        return await Worker.Value.Run(FindDevices).WaitAsync(cancel);
    }

    /// <inheritdoc/>
    public async Task<IScanSession> OpenAsync(ScanDevice device, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        return await Worker.Value.Run(() => Open(Worker.Value, device)).WaitAsync(cancel);
    }

    /// <summary>
    /// The devices WIA knows (on the worker): those of the WIA 2.0 device manager and, not repeated, those only the WIA 1.0 manager offers
    /// (Windows' class driver for eSCL); none, but drivers only for TWAIN installed: the hint as the error.
    /// </summary>
    internal static IReadOnlyList<ScanDevice> FindDevices()
    {
        var found = Enumerate();
        if (found.Count == 0 && TwainHint(TwainFolders()) is { } hint) throw new ScanException(hint);
        return found;
    }

    /// <summary>
    /// The devices of both device managers: one entry per device, the best way first (<see cref="WiaMapping.Combine"/>); with
    /// <paramref name="combine"/> off every way as an entry of its own (tests that try one way, <see cref="WiaMapping.Order"/>).
    /// </summary>
    internal static List<ScanDevice> Enumerate(bool combine = true)
    {
        var found = new List<(ScanDevice Device, string? Uuid)>();
        var manager = Manager();
        try
        {
            // local devices: network scanners set up in Windows count as local (their class drivers run here)
            var hr = manager.EnumDeviceInfo(Wia.DevinfoEnumLocal, out var devices);
            if (hr < 0 || devices is null) throw WiaMapping.ErrorOf(hr);
            found.AddRange(Read(devices));
        }
        finally
        {
            Release(manager);
        }
        // WIA 1.0: a device missing in WIA 2.0 is still a device; if this manager fails, the devices of 2.0 stay
        if (Manager1(out var legacy) is { } manager1)
        {
            try
            {
                if (manager1.EnumDeviceInfo(Wia.DevinfoEnumLocal, out var devices) >= 0 && devices is not null)
                    found.AddRange(Read(devices).Where(d => !found.Any(f => f.Device.Id == d.Device.Id)));
            }
            finally
            {
                Release(manager1);
            }
        }
        else if (legacy != 0) AppLog.Write("Scannen (WIA)", $"WIA 1.0: 0x{legacy:X8}");
        return combine ? WiaMapping.Combine(found) : WiaMapping.Order(found);
    }

    /// <summary>
    /// Opens a device (on the worker): its ways in turn, the best first (<see cref="WiaMapping.OpenFirst{T}"/>), each through WIA 2.0, else
    /// through WIA 1.0 (devices 2.0 does not offer); a device no longer known by any of its ids is looked for once more by its name (network
    /// devices set up again in Windows get a new instance id). The session keeps the device as listed, whichever way opened it.
    /// </summary>
    internal static IScanSession Open(ScanWorker worker, ScanDevice device)
    {
        string? openedBy = null;
        var (session, hr) = WiaMapping.OpenFirst(device.AllIds, id =>
        {
            var tried = TryOpen(worker, device, id);
            if (tried.Session is not null) openedBy = id;
            return tried;
        });
        if (session is not null)
        {
            if (openedBy != device.Id) AppLog.Write("Scannen (WIA)", "the best way failed, opened by a further way of the device");
            return session;
        }
        // busy, cover open …: the device is there, its state is the answer
        if (WiaMapping.ErrorOf(hr) is { Kind: not (ScanErrorKind.Other or ScanErrorKind.Offline) } state) throw state;
        var again = Enumerate().FirstOrDefault(d => !d.AllIds.Intersect(device.AllIds).Any()
            && string.Equals(d.BaseName, device.BaseName, StringComparison.OrdinalIgnoreCase));
        if (again is not null)
        {
            (session, var hr2) = WiaMapping.OpenFirst(again.AllIds, id => TryOpen(worker, again, id));
            if (session is not null) return session;
            hr = hr2;
        }
        throw WiaMapping.ErrorOf(hr) is { Kind: not (ScanErrorKind.Other or ScanErrorKind.Offline) } known ? known
            : new ScanException(Tr.T("Der Scanner ist nicht erreichbar — ist er eingeschaltet?"), ScanErrorKind.Offline);
    }

    /// <summary>Opens one way of a device (on the worker): WIA 2.0, else WIA 1.0; the session carries <paramref name="device"/> as listed.</summary>
    private static (IScanSession? Session, int Hr) TryOpen(ScanWorker worker, ScanDevice device, string id)
    {
        int hr2;
        var manager = Manager();
        try
        {
            hr2 = manager.CreateDevice(0, id, out var root);
            if (hr2 >= 0 && root is not null) return (WiaSession.Build(worker, device, root), 0);
            Release(root);
        }
        finally
        {
            Release(manager);
        }
        if (Manager1(out var failed) is not { } manager1) return (null, hr2 == Wia.NoDeviceAvailable && failed != 0 ? failed : hr2);
        try
        {
            var hr1 = manager1.CreateDevice(id, out var root1);
            if (hr1 >= 0 && root1 is not null) return (WiaLegacySession.Build(worker, device, root1), 0);
            Release(root1);
            // the answer that says more: a device WIA 2.0 does not offer is „no device“ there
            return (null, hr2 == Wia.NoDeviceAvailable ? hr1 : hr2);
        }
        finally
        {
            Release(manager1);
        }
    }

    /// <summary>A device through WIA 1.0 (on the worker), for a WIA 2.0 session whose driver refused the transfer; null if that fails too.</summary>
    internal static WiaLegacySession? OpenLegacy(ScanWorker worker, ScanDevice device)
    {
        if (Manager1(out _) is not { } manager) return null;
        try
        {
            // every way of the device through WIA 1.0, the best first
            foreach (var id in device.AllIds)
            {
                if (manager.CreateDevice(id, out var root) >= 0 && root is not null) return WiaLegacySession.Build(worker, device, root);
                Release(root);
            }
            return null;
        }
        finally
        {
            Release(manager);
        }
    }

    /// <summary>The device information of an enumerator, read and released.</summary>
    private static List<(ScanDevice Device, string? Uuid)> Read(IEnumWiaDevInfo devices)
    {
        var found = new List<(ScanDevice Device, string? Uuid)>();
        try
        {
            for (var i = 0; i < 256; i++)
            {
                if (devices.Next(1, out var info, out var fetched) != Wia.SOk || fetched != 1 || info is null) break;
                try
                {
                    if (WiaMapping.DeviceOf(new WiaPropertyStorage(info), out var uuid) is { } device) found.Add((device, uuid));
                }
                finally
                {
                    Release(info);
                }
            }
        }
        finally
        {
            Release(devices);
        }
        return found;
    }

    private static readonly Guid IidDevMgr = new("5eb2502a-8cf1-11d1-bf92-0060081ed811");

    /// <summary>The device manager of WIA 1.0, or null with the HRESULT (the devices of 2.0 then stand alone).</summary>
    internal static IWiaDevMgr? Manager1(out int hr)
    {
        EnsureCom();
        hr = Com.CoCreateInstance(Wia.ClsidDevMgr, 0, 0x1 | Com.ClsctxLocalServer, IidDevMgr, out var pointer);
        if (hr < 0 || pointer == 0) return null;
        try
        {
            return (IWiaDevMgr)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>A new device manager (on the worker); a stopped or missing scanner service gives the hint to start it.</summary>
    internal static IWiaDevMgr2 Manager()
    {
        EnsureCom();
        const uint InprocOrLocal = 0x1 | Com.ClsctxLocalServer;
        var hr = Com.CoCreateInstance(Wia.ClsidDevMgr2, 0, InprocOrLocal, IidDevMgr2, out var pointer);
        if (hr < 0 || pointer == 0)
            throw hr is Wia.EServiceNotActive or ServiceDisabled or ClassNotRegistered or Wia.RpcServerUnavailable or ServerExecFailure
                ? new ScanException(WiaMapping.ServiceHint, ScanErrorKind.InterfaceMissing)
                : WiaMapping.ErrorOf(hr);
        try
        {
            return (IWiaDevMgr2)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    // HRESULT_FROM_WIN32(ERROR_SERVICE_DISABLED), REGDB_E_CLASSNOTREG (WIA not part of this Windows), CO_E_SERVER_EXEC_FAILURE
    private const int ServiceDisabled = unchecked((int)0x80070422), ClassNotRegistered = unchecked((int)0x80040154),
        ServerExecFailure = unchecked((int)0x80080005);

    [ThreadStatic] private static bool _comReady;

    /// <summary>Joins the multithreaded apartment once per thread (COINIT_MULTITHREADED); a thread already in one stays there.</summary>
    private static void EnsureCom()
    {
        if (_comReady) return;
        _ = CoInitializeEx(0, 0);   // S_OK, S_FALSE (already) or RPC_E_CHANGED_MODE (an STA thread, as in tests): all usable
        _comReady = true;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint flags);

    /// <summary>Releases a COM object at once (the device is free again as soon as its root item is released).</summary>
    internal static void Release(object? com)
    {
        if (com is ComObject c) c.FinalRelease();
    }

    /// <summary>The folders of TWAIN data sources (32 and 64 bit) under the Windows folder.</summary>
    internal static IEnumerable<string> TwainFolders()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return windows.Length == 0 ? [] : [Path.Combine(windows, "twain_32"), Path.Combine(windows, "twain_64")];
    }

    /// <summary>
    /// The hint for devices that have only a TWAIN driver: the data sources (*.ds) are only found as files, never loaded — no driver code
    /// runs. The data source with which TWAIN reaches WIA devices (wiatwain.ds) does not count.
    /// </summary>
    /// <param name="folders">The TWAIN folders.</param>
    /// <returns>The hint, or null if there is no TWAIN-only driver.</returns>
    internal static string? TwainHint(IEnumerable<string> folders)
    {
        var names = new List<string>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*.ds", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
                    .Take(64).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var file in files.Where(f => !Path.GetFileName(f).Equals("wiatwain.ds", StringComparison.OrdinalIgnoreCase)))
            {
                // the folder of a data source is usually named after its maker or device; directly in the TWAIN folder, the file name
                var parent = Path.GetDirectoryName(file);
                var name = parent is not null && !string.Equals(parent.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(parent) : Path.GetFileNameWithoutExtension(file);
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            }
        }
        return names.Count == 0 ? null
            : Tr.T("Über WIA wurde kein Scanner gefunden. Installiert ist ein Scanner-Treiber nur für TWAIN ({0}) — Scannen über TWAIN kommt in einem späteren Schritt.",
                string.Join(", ", names.Take(3)));
    }
}
