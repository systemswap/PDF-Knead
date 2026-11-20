using System.Runtime.InteropServices;

namespace PdfKnead.Core.Scanning.Sane;

/// <summary>
/// The SANE library of the system (<c>libsane.so.1</c>, „Scanner Access Now Easy“, SANE Standard 1.06, chapter 4 „The SANE Application
/// Programmer Interface“), loaded at run time and never shipped. Only what scanning needs. All calls except <see cref="Cancel"/> must come
/// from one thread (<see cref="ScanWorker"/>); the standard does not require the back ends to be reentrant (4.3).
/// </summary>
internal sealed unsafe partial class SaneLibrary
{
    /// <summary>
    /// A SANE configuration folder used instead of the system's (tests: only the virtual test device, nothing of the system). Takes effect
    /// at the first initialisation of the process; set before.
    /// </summary>
    internal static string? ConfigDirectoryForTests;

    // SANE_Status (4.2.7)
    internal const int StatusGood = 0, StatusUnsupported = 1, StatusCancelled = 2, StatusDeviceBusy = 3, StatusInval = 4, StatusEof = 5,
        StatusJammed = 6, StatusNoDocs = 7, StatusCoverOpen = 8, StatusIoError = 9, StatusNoMem = 10, StatusAccessDenied = 11;
    // SANE_Value_Type, SANE_Unit, SANE_Constraint_Type (4.2.9)
    internal const int TypeBool = 0, TypeInt = 1, TypeFixed = 2, TypeString = 3, TypeButton = 4, TypeGroup = 5;
    internal const int UnitNone = 0, UnitPixel = 1, UnitBit = 2, UnitMm = 3, UnitDpi = 4;
    internal const int ConstraintNone = 0, ConstraintRange = 1, ConstraintWordList = 2, ConstraintStringList = 3;
    // capabilities (4.2.9.7)
    internal const int CapSoftSelect = 1, CapSoftDetect = 4, CapInactive = 32;
    // SANE_Action and the info bits of sane_control_option (4.3.7)
    internal const int ActionGetValue = 0, ActionSetValue = 1;
    internal const int InfoReloadOptions = 2;
    // SANE_Frame (4.2.10)
    internal const int FrameGray = 0, FrameRgb = 1, FrameRed = 2, FrameGreen = 3, FrameBlue = 4;

    /// <summary>SANE_Device (4.2.8): name, vendor, model, type as C strings.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Device
    {
        public nint Name, Vendor, Model, Type;
    }

    /// <summary>SANE_Option_Descriptor (4.2.9): three strings, five words, then the constraint pointer (natural alignment).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct OptionDescriptor
    {
        public nint Name, Title, Description;
        public int Type, Unit, Size, Cap, ConstraintType;
        public nint Constraint;
    }

    /// <summary>SANE_Parameters (4.2.10).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Parameters
    {
        public int Format, LastFrame, BytesPerLine, PixelsPerLine, Lines, Depth;
    }

    private static readonly Lock LoadGate = new();
    private static SaneLibrary? _loaded;
    private static bool _tried;

    private readonly delegate* unmanaged<int*, nint, int> _init;
    private readonly delegate* unmanaged<Device***, int, int> _getDevices;
    private readonly delegate* unmanaged<byte*, nint*, int> _open;
    private readonly delegate* unmanaged<nint, void> _close;
    private readonly delegate* unmanaged<nint, int, OptionDescriptor*> _getOptionDescriptor;
    private readonly delegate* unmanaged<nint, int, int, void*, int*, int> _controlOption;
    private readonly delegate* unmanaged<nint, Parameters*, int> _getParameters;
    private readonly delegate* unmanaged<nint, int> _start;
    private readonly delegate* unmanaged<nint, byte*, int, int*, int> _read;
    private readonly delegate* unmanaged<nint, void> _cancel;
    private bool _initialised;

    private SaneLibrary(nint library)
    {
        nint Export(string name) => NativeLibrary.GetExport(library, name);
        _init = (delegate* unmanaged<int*, nint, int>)Export("sane_init");
        _getDevices = (delegate* unmanaged<Device***, int, int>)Export("sane_get_devices");
        _open = (delegate* unmanaged<byte*, nint*, int>)Export("sane_open");
        _close = (delegate* unmanaged<nint, void>)Export("sane_close");
        _getOptionDescriptor = (delegate* unmanaged<nint, int, OptionDescriptor*>)Export("sane_get_option_descriptor");
        _controlOption = (delegate* unmanaged<nint, int, int, void*, int*, int>)Export("sane_control_option");
        _getParameters = (delegate* unmanaged<nint, Parameters*, int>)Export("sane_get_parameters");
        _start = (delegate* unmanaged<nint, int>)Export("sane_start");
        _read = (delegate* unmanaged<nint, byte*, int, int*, int>)Export("sane_read");
        _cancel = (delegate* unmanaged<nint, void>)Export("sane_cancel");
    }

    /// <summary>The library, loaded once per process; null if SANE is not installed.</summary>
    public static SaneLibrary? Load()
    {
        lock (LoadGate)
        {
            if (_tried) return _loaded;
            _tried = true;
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsFreeBSD()) return null;
            foreach (var name in (string[])["libsane.so.1", "libsane.so"])
            {
                if (!NativeLibrary.TryLoad(name, out var library)) continue;
                try
                {
                    _loaded = new SaneLibrary(library);
                    break;
                }
                catch (EntryPointNotFoundException)
                {
                    // a library of that name without the SANE functions
                }
            }
            return _loaded;
        }
    }

    /// <summary>sane_init, once (the library stays initialised for the session: back ends keep their network search state).</summary>
    public void EnsureInitialised()
    {
        if (_initialised) return;
        if (ConfigDirectoryForTests is { } folder) SetEnvironment("SANE_CONFIG_DIR", folder, 1);
        int version;
        Check(_init(&version, 0));
        _initialised = true;
    }

    /// <summary>sane_get_devices: the list is copied at once, it is valid only until the next call (4.3.3).</summary>
    public List<ScanDevice> GetDevices()
    {
        EnsureInitialised();
        Device** list;
        Check(_getDevices(&list, 0));
        var devices = new List<ScanDevice>();
        for (var i = 0; list != null && list[i] != null && i < 256; i++)
        {
            var d = list[i];
            var id = Text(d->Name);
            if (id.Length > 0) devices.Add(new ScanDevice(id, Text(d->Vendor), Text(d->Model), Text(d->Type)));
        }
        return ScanDevice.CombineBackEnds(ScanDevice.WithoutOwnShares(devices, OwnAddresses()));
    }

    /// <summary>The addresses of this computer (all interfaces, loopback included); empty if they cannot be read.</summary>
    private static List<System.Net.IPAddress> OwnAddresses()
    {
        try
        {
            return [.. System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(u => u.Address)];
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>sane_open.</summary>
    public nint Open(string name)
    {
        EnsureInitialised();
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        nint handle;
        int status;
        fixed (byte* p = bytes) status = _open(p, &handle);
        // INVAL from sane_open: the back end does not know the name (any more) — switched off, or a network back end gave it a new id
        if (status == StatusInval) throw new ScanException(Tr.T("Der Scanner ist nicht erreichbar — ist er eingeschaltet?"), ScanErrorKind.Offline);
        Check(status);
        return handle;
    }

    /// <summary>sane_close.</summary>
    public void Close(nint handle)
    {
        _close(handle);
        KeepSigpipeIgnored();
    }

    /// <summary>sane_get_option_descriptor; null beyond the last option.</summary>
    public OptionDescriptor* Descriptor(nint handle, int option) => _getOptionDescriptor(handle, option);

    /// <summary>sane_control_option.</summary>
    /// <returns>The status and the info bits.</returns>
    public (int Status, int Info) Control(nint handle, int option, int action, void* value)
    {
        var info = 0;
        var status = _controlOption(handle, option, action, value, &info);
        return (status, info);
    }

    /// <summary>sane_get_parameters.</summary>
    public Parameters GetParameters(nint handle)
    {
        Parameters p;
        Check(_getParameters(handle, &p));
        return p;
    }

    /// <summary>sane_start; returns the status (NO_DOCS ends a feeder job).</summary>
    public int Start(nint handle)
    {
        var status = _start(handle);
        KeepSigpipeIgnored();
        return status;
    }

    /// <summary>sane_read into <paramref name="buffer"/>; returns the status and the bytes read.</summary>
    public (int Status, int Length) Read(nint handle, Span<byte> buffer)
    {
        var length = 0;
        int status;
        fixed (byte* p = buffer) status = _read(handle, p, buffer.Length, &length);
        return (status, length);
    }

    /// <summary>sane_cancel: the one call that may come from another thread (4.3.11); a blocked read then returns CANCELLED.</summary>
    public void Cancel(nint handle)
    {
        _cancel(handle);
        KeepSigpipeIgnored();
    }

    /// <summary>
    /// Sets SIGPIPE back to „ignore“ for the process. SANE's thread helper (used by back ends that read in a thread of their own, the test device
    /// among them) sets it to its default — terminate — for the whole process when a scan starts, so that its reader ends on a closed pipe.
    /// .NET relies on it being ignored: afterwards any write into a pipe or connection the other side has closed ended the process without a
    /// word (exit code 141; Fehlergedächtnis 959). Ignored, the reader gets a write error instead and ends as well.
    /// </summary>
    internal static void KeepSigpipeIgnored()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD()) _ = Signal(SigPipe, SigIgnore);
    }

    private const int SigPipe = 13;
    private static readonly nint SigIgnore = 1;

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "signal")]
    private static extern nint Signal(int signal, nint handler);

    // the variable must reach the C environment that libsane reads (Environment.SetEnvironmentVariable keeps its own copy on Unix)
    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnvironment(string name, string value, int overwrite);

    /// <summary>A C string (UTF-8, as SANE strings are in practice) or empty.</summary>
    internal static string Text(nint p) => p == 0 ? "" : Marshal.PtrToStringUTF8(p) ?? "";

    /// <summary>Throws the matching <see cref="ScanException"/> for an error status.</summary>
    internal static void Check(int status)
    {
        if (status == StatusGood) return;
        if (status == StatusCancelled) throw new OperationCanceledException();
        throw Error(status);
    }

    /// <summary>The exception for an error status, with a message for the user.</summary>
    internal static ScanException Error(int status) => status switch
    {
        StatusDeviceBusy => new ScanException(Tr.T("Der Scanner ist gerade belegt — vielleicht von einem anderen Programm."), ScanErrorKind.Busy),
        StatusJammed => new ScanException(Tr.T("Papierstau im Scanner."), ScanErrorKind.Jammed),
        StatusNoDocs => new ScanException(Tr.T("Im Einzug liegt kein Papier."), ScanErrorKind.NoPaper),
        StatusCoverOpen => new ScanException(Tr.T("Eine Abdeckung des Scanners ist offen."), ScanErrorKind.CoverOpen),
        StatusIoError => new ScanException(Tr.T("Die Verbindung zum Scanner ist gestört.")),
        StatusNoMem => new ScanException(Tr.T("Dem Scanner-Treiber fehlt Arbeitsspeicher.")),
        StatusAccessDenied => new ScanException(Tr.T("Kein Zugriff auf den Scanner — fehlen die Rechte?")),
        StatusInval => new ScanException(Tr.T("Der Scanner lehnt die Einstellung ab.")),
        StatusUnsupported => new ScanException(Tr.T("Der Scanner kann das nicht.")),
        _ => new ScanException(Tr.T("Der Scanner meldet einen Fehler ({0}).", status)),
    };
}
