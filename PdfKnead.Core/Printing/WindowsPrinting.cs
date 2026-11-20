using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PdfKnead.Core.Printing;

/// <summary>
/// Printing under Windows with the print spooler (winspool.drv, gdi32.dll) — own P/Invoke calls instead of System.Drawing (no extra
/// package, AOT-compatible). Settings are held in a DEVMODE (public header, followed by the driver's private data); the sheets are
/// rendered in bands and placed on the page with StretchDIBits, so memory stays small even for A3 at 600 dpi.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class WindowsPrinting
{
    // ------------------------------------------------------------ Printers

    public static IEnumerable<PrinterInfo> List()
    {
        var standard = DefaultPrinter();
        return EnumNames().Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => new PrinterInfo(n, string.Equals(n, standard, StringComparison.OrdinalIgnoreCase)));
    }

    private static string? DefaultPrinter()
    {
        var size = 0;
        GetDefaultPrinterW(null, ref size);
        if (size <= 0 || size > 4096) return null;
        var buffer = stackalloc char[size];
        return GetDefaultPrinterW(buffer, ref size) ? new string(buffer) : null;
    }

    private static List<string> EnumNames()
    {
        const uint flags = 2 | 4;   // PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS
        EnumPrintersW(flags, null, 4, null, 0, out var needed, out _);
        if (needed == 0) return [];
        var buffer = new byte[needed];
        fixed (byte* p = buffer)
        {
            if (!EnumPrintersW(flags, null, 4, p, needed, out _, out var count)) return [];
            var list = new List<string>((int)count);
            for (var i = 0; i < count; i++)
                if (((PrinterInfo4*)p)[i].Name is var name && name != null) list.Add(new string(name));
            return list;
        }
    }

    /// <summary>Port of the printer (for DeviceCapabilities); PRINTER_INFO_5 does not query the printer itself.</summary>
    private static string? Port(string printer)
    {
        if (!OpenPrinterW(printer, out var handle, 0)) return null;
        try
        {
            GetPrinterW(handle, 5, null, 0, out var needed);
            if (needed == 0) return null;
            var buffer = new byte[needed];
            fixed (byte* p = buffer)
                return GetPrinterW(handle, 5, p, needed, out _) && ((PrinterInfo5*)p)->Port != null ? new string(((PrinterInfo5*)p)->Port) : null;
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    // ------------------------------------------------------------ Capabilities

    public static PrinterCapabilities Capabilities(string printer)
    {
        var port = Port(printer);
        var devMode = DefaultDevMode(printer);

        // Paper: codes, names (64 characters each), dimensions (tenths of a mm)
        var papers = new List<PaperSize>();
        var codes = Words(printer, port, DcPapers);
        var names = Names(printer, port, DcPaperNames, 64, codes.Length);
        var sizes = Points(printer, port, DcPaperSize, codes.Length);
        for (var i = 0; i < codes.Length && i < sizes.Length; i++)
        {
            var (w, h) = (sizes[i].X / 10.0 * 72 / 25.4, sizes[i].Y / 10.0 * 72 / 25.4);
            if (w < 36 || h < 36) continue;   // custom size without dimensions
            var label = i < names.Length && names[i].Length > 0 ? names[i] : Pdf.Document.PageFormat.Describe(w, h);
            papers.Add(new PaperSize(codes[i].ToString(CultureInfo.InvariantCulture), label, Math.Min(w, h), Math.Max(w, h)));
        }

        var trays = new List<PrinterChoice>();
        var bins = Words(printer, port, DcBins);
        var binNames = Names(printer, port, DcBinNames, 24, bins.Length);
        for (var i = 0; i < bins.Length; i++)
            trays.Add(new PrinterChoice(bins[i].ToString(CultureInfo.InvariantCulture), i < binNames.Length && binNames[i].Length > 0 ? binNames[i] : $"#{bins[i]}"));

        var qualities = new List<PrinterChoice>();
        var count = DeviceCapabilitiesW(printer, port, DcEnumResolutions, null, null);
        if (count is > 0 and < 256)
        {
            var values = new int[count * 2];
            fixed (int* v = values) DeviceCapabilitiesW(printer, port, DcEnumResolutions, (byte*)v, null);
            for (var i = 0; i < count; i++)
                if (values[2 * i] > 0 && values[2 * i + 1] > 0)
                    qualities.Add(new PrinterChoice($"{values[2 * i]}x{values[2 * i + 1]}",
                        values[2 * i] == values[2 * i + 1] ? Tr.T("{0} dpi", values[2 * i]) : Tr.T("{0} × {1} dpi", values[2 * i], values[2 * i + 1])));
        }
        qualities = [.. qualities.DistinctBy(q => q.Value)];

        string? defaultPaper = null, defaultTray = null, defaultQuality = null;
        if (devMode is not null)
        {
            var dm = new DevMode(devMode);
            defaultPaper = dm.PaperSize is > 0 and var ps ? ps.ToString(CultureInfo.InvariantCulture) : null;
            defaultTray = dm.DefaultSource is > 0 and var src ? src.ToString(CultureInfo.InvariantCulture) : null;
            if (dm.PrintQuality > 0) defaultQuality = $"{dm.PrintQuality}x{(dm.YResolution > 0 ? dm.YResolution : dm.PrintQuality)}";
        }
        var duplex = DeviceCapabilitiesW(printer, port, DcDuplex, null, null);
        var color = DeviceCapabilitiesW(printer, port, DcColorDevice, null, null);
        var copies = DeviceCapabilitiesW(printer, port, DcCopies, null, null);
        return new PrinterCapabilities(papers, defaultPaper, trays, defaultTray, qualities, defaultQuality,
            duplex < 0 ? null : duplex == 1, color < 0 ? null : color == 1, Math.Max(1, copies), []);
    }

    private static ushort[] Words(string printer, string? port, ushort capability)
    {
        var count = DeviceCapabilitiesW(printer, port, capability, null, null);
        if (count is <= 0 or > 4096) return [];
        var values = new ushort[count];
        fixed (ushort* v = values) DeviceCapabilitiesW(printer, port, capability, (byte*)v, null);
        return values;
    }

    private static string[] Names(string printer, string? port, ushort capability, int length, int expected)
    {
        var count = DeviceCapabilitiesW(printer, port, capability, null, null);
        if (count <= 0 || count != expected) return [];
        var chars = new char[count * length];
        fixed (char* c = chars) DeviceCapabilitiesW(printer, port, capability, (byte*)c, null);
        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            var span = chars.AsSpan(i * length, length);
            var end = span.IndexOf('\0');
            names[i] = (end < 0 ? span : span[..end]).ToString().Trim();
        }
        return names;
    }

    private static (int X, int Y)[] Points(string printer, string? port, ushort capability, int expected)
    {
        var count = DeviceCapabilitiesW(printer, port, capability, null, null);
        if (count <= 0 || count != expected) return [];
        var values = new int[count * 2];
        fixed (int* v = values) DeviceCapabilitiesW(printer, port, capability, (byte*)v, null);
        return [.. Enumerable.Range(0, count).Select(i => (values[2 * i], values[2 * i + 1]))];
    }

    // ------------------------------------------------------------ DEVMODE

    /// <summary>Default settings of the printer (DEVMODE including driver data); null if the driver provides none.</summary>
    private static byte[]? DefaultDevMode(string printer)
    {
        if (!OpenPrinterW(printer, out var handle, 0)) return null;
        try
        {
            var size = DocumentPropertiesW(0, handle, printer, null, null, 0);
            if (size is < DevMode.PublicSize or > 1 << 20) return null;
            var buffer = new byte[size];
            fixed (byte* b = buffer)
                return DocumentPropertiesW(0, handle, printer, b, null, DmOutBuffer) == IdOk ? buffer : null;
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    /// <summary>DEVMODE with the chosen settings; the driver then adjusts its private data accordingly (DM_IN_BUFFER).</summary>
    private static byte[]? DevModeFor(string printer, PrinterSettings settings, bool? landscape, int copies)
    {
        // From the manufacturer's dialog: the buffer may be larger than the DEVMODE (requiring an exact size match would discard valid data)
        var data = settings.DriverData is { Length: >= DevMode.PublicSize } own && new DevMode(own).TotalSize is var total &&
                   total >= DevMode.PublicSize && total <= own.Length
            ? own.AsSpan(0, total).ToArray() : DefaultDevMode(printer);
        if (data is null) return null;
        var dm = new DevMode(data);
        if (landscape is { } l) dm.Orientation = (short)(l ? 2 : 1);
        if (short.TryParse(settings.Paper, NumberStyles.None, CultureInfo.InvariantCulture, out var paper) && paper > 0)
        {
            // A remembered custom paper size (dmPaperLength/Width) would take precedence — remove it; driver scaling to 100 %
            dm.PaperSize = paper;
            dm.ClearCustomSize();
            dm.Scale = 100;
        }
        if (short.TryParse(settings.Tray, NumberStyles.None, CultureInfo.InvariantCulture, out var tray) && tray > 0) dm.DefaultSource = tray;
        if (settings.Quality?.Split('x') is [var x, var y] && short.TryParse(x, out var qx) && short.TryParse(y, out var qy) && qx > 0 && qy > 0)
            (dm.PrintQuality, dm.YResolution) = (qx, qy);
        dm.Duplex = settings.Duplex switch
        {
            DuplexMode.LongEdge => 2,    // DMDUP_VERTICAL: long edge in portrait orientation
            DuplexMode.ShortEdge => 3,   // DMDUP_HORIZONTAL
            _ => 1,
        };
        dm.Color = (short)(settings.Grayscale ? 1 : 2);
        dm.Copies = (short)Math.Clamp(copies, 1, short.MaxValue);
        dm.Collate = (short)(settings.Collate ? 1 : 0);
        if (!OpenPrinterW(printer, out var handle, 0)) return data;
        try
        {
            var merged = new byte[Math.Max(data.Length, DocumentPropertiesW(0, handle, printer, null, null, 0))];
            fixed (byte* input = data)
            fixed (byte* output = merged)
                return DocumentPropertiesW(0, handle, printer, output, input, DmInBuffer | DmOutBuffer) == IdOk ? merged : data;
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    /// <summary>Reads what the DEVMODE contains (after the manufacturer's dialog) as settings for the program's own fields; manual duplex is kept.</summary>
    public static PrinterSettings Read(byte[] data, PrinterSettings current)
    {
        if (data.Length < DevMode.PublicSize) return current;
        var dm = new DevMode(data);
        return current with
        {
            Paper = dm.PaperSize > 0 ? dm.PaperSize.ToString(CultureInfo.InvariantCulture) : current.Paper,
            Tray = dm.DefaultSource > 0 ? dm.DefaultSource.ToString(CultureInfo.InvariantCulture) : current.Tray,
            Quality = dm.PrintQuality > 0 ? $"{dm.PrintQuality}x{(dm.YResolution > 0 ? dm.YResolution : dm.PrintQuality)}" : current.Quality,
            Duplex = current.Duplex == DuplexMode.Manual ? DuplexMode.Manual : dm.Duplex switch { 2 => DuplexMode.LongEdge, 3 => DuplexMode.ShortEdge, _ => DuplexMode.OneSided },
            Grayscale = dm.Color == 1,
            Copies = dm.Copies > 0 ? dm.Copies : current.Copies,
            DriverData = data,
        };
    }

    public static byte[]? DriverDialog(nint owner, string printer, PrinterSettings settings)
    {
        var input = DevModeFor(printer, settings, null, settings.Copies);
        if (input is null || !OpenPrinterW(printer, out var handle, 0)) return null;
        try
        {
            var output = new byte[Math.Max(input.Length, DocumentPropertiesW(owner, handle, printer, null, null, 0))];
            fixed (byte* i = input)
            fixed (byte* o = output)
                return DocumentPropertiesW(owner, handle, printer, o, i, DmInBuffer | DmOutBuffer | DmInPrompt) == IdOk ? output : null;
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    // ------------------------------------------------------------ Margin

    public static double? Margin(string printer, PrinterSettings settings)
    {
        var dm = DevModeFor(printer, settings, landscape: false, 1);
        nint dc;
        fixed (byte* d = dm) dc = CreateICW("WINSPOOL", printer, null, d);
        if (dc == 0) return null;
        try
        {
            var page = PageInfo.Of(dc);
            return page.DpiX <= 0 || page.DpiY <= 0 ? null : page.LargestMargin;
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    /// <summary>Page metrics of the device: resolution, paper, printable area (all in device pixels).</summary>
    private readonly record struct PageInfo(int DpiX, int DpiY, int PhysicalWidth, int PhysicalHeight, int OffsetX, int OffsetY, int Width, int Height)
    {
        public static PageInfo Of(nint dc) => new(GetDeviceCaps(dc, LogPixelsX), GetDeviceCaps(dc, LogPixelsY),
            GetDeviceCaps(dc, PhysicalWidthCap), GetDeviceCaps(dc, PhysicalHeightCap), GetDeviceCaps(dc, PhysicalOffsetX),
            GetDeviceCaps(dc, PhysicalOffsetY), GetDeviceCaps(dc, HorzRes), GetDeviceCaps(dc, VertRes));

        /// <summary>Largest of the four non-printable margins in points.</summary>
        public double LargestMargin => Math.Max(
            Math.Max(OffsetX, PhysicalWidth - OffsetX - Width) * 72.0 / DpiX,
            Math.Max(OffsetY, PhysicalHeight - OffsetY - Height) * 72.0 / DpiY);
    }

    // ------------------------------------------------------------ Printing

    public static void Print(PrintJob job, IProgress<(int Done, int Total)>? progress, CancellationToken cancel)
    {
        var settings = job.Settings;
        var port = Port(job.Printer);
        var maxCopies = DeviceCapabilitiesW(job.Printer, port, DcCopies, null, null);
        // The driver makes the copies if it can (less data in the job); otherwise each sheet is sent several times
        var driverCopies = settings.Copies > 1 && maxCopies >= settings.Copies;
        var firstSheet = job.Sheets.FirstOrDefault(s => s >= 0, -1);
        var landscape = firstSheet >= 0 && job.Source.SheetSize(firstSheet) is var (fw, fh) && fw > fh;
        var dm = DevModeFor(job.Printer, settings, landscape, driverCopies ? settings.Copies : 1)
                 ?? throw new InvalidOperationException(Tr.T("Der Drucker „{0}“ ist nicht erreichbar.", job.Printer));

        nint dc;
        fixed (byte* d = dm) dc = CreateDCW("WINSPOOL", job.Printer, null, d);
        if (dc == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), Tr.T("Der Drucker „{0}“ ist nicht erreichbar.", job.Printer));
        var started = false;
        try
        {
            fixed (char* title = job.Title)
            fixed (char* output = job.OutputFile)
            {
                var info = new DocInfo { Size = sizeof(DocInfo), DocName = title, Output = job.OutputFile is null ? null : output };
                if (StartDocW(dc, &info) <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), Tr.T("Der Druckauftrag ließ sich nicht anlegen."));
            }
            started = true;

            var order = Printers.CopyOrder(job.Sheets, driverCopies ? 1 : Math.Max(1, settings.Copies), settings.Collate,
                settings.Duplex is DuplexMode.LongEdge or DuplexMode.ShortEdge);

            var done = 0;
            byte[]? buffer = null;   // one buffer for all sheets (instead of up to 16 MB per sheet)
            foreach (var sheet in order)
            {
                cancel.ThrowIfCancellationRequested();
                if (sheet >= 0 && job.Source.SheetSize(sheet) is var (w, h) && w > h != landscape)
                {
                    // Orientation per sheet: ResetDC between the pages (before StartPage)
                    landscape = w > h;
                    var mode = new DevMode(dm);
                    mode.Orientation = (short)(landscape ? 2 : 1);
                    nint reset;
                    fixed (byte* d = dm) reset = ResetDCW(dc, d);
                    if (reset == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), Tr.T("Der Drucker hat die Seite abgelehnt."));
                }
                if (StartPage(dc) <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), Tr.T("Der Drucker hat die Seite abgelehnt."));
                if (sheet >= 0) DrawSheet(dc, job, sheet, ref buffer, cancel);
                if (EndPage(dc) <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), Tr.T("Der Drucker hat die Seite abgelehnt."));
                progress?.Report((++done, order.Count));
            }
            EndDoc(dc);
            started = false;
        }
        finally
        {
            if (started) AbortDoc(dc);
            DeleteDC(dc);
        }
    }

    /// <summary>
    /// Renders the sheet in bands (up to 16 MB each) and places them on the page; the origin is the sheet edge, not the printable area.
    /// In greyscale one byte per pixel with a grey palette (a third of the data for the spooler).
    /// </summary>
    private static void DrawSheet(nint dc, PrintJob job, int sheet, ref byte[]? buffer, CancellationToken cancel)
    {
        var page = PageInfo.Of(dc);
        if (page.DpiX <= 0 || page.DpiY <= 0) return;
        var (w, h) = job.Source.SheetSize(sheet);
        var gray = job.Settings.Grayscale;
        var ppp = Math.Min(job.Dpi, page.DpiX) / 72.0;
        var width = Math.Max(1, (int)Math.Ceiling(w * ppp));
        var height = Math.Max(1, (int)Math.Ceiling(h * ppp));
        var stride = (width * (gray ? 1 : 3) + 3) & ~3;
        var bandRows = Math.Clamp((16 << 20) / stride, 16, height);
        if (buffer is null || buffer.Length < stride * bandRows) buffer = new byte[stride * bandRows];
        double sx = page.DpiX / 72.0 / ppp, sy = page.DpiY / 72.0 / ppp;
        var info = stackalloc byte[sizeof(BitmapInfoHeader) + 256 * 4];
        var header = (BitmapInfoHeader*)info;
        *header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader), Width = width, Planes = 1, BitCount = (ushort)(gray ? 8 : 24), ClrUsed = gray ? 256u : 0,
        };
        if (gray)
        {
            var palette = (uint*)(info + sizeof(BitmapInfoHeader));
            for (uint i = 0; i < 256; i++) palette[i] = i | i << 8 | i << 16;
        }
        for (var top = 0; top < height; top += bandRows)
        {
            cancel.ThrowIfCancellationRequested();
            var rows = Math.Min(bandRows, height - top);
            job.Source.RenderBand(sheet, ppp, top, width, rows, buffer.AsSpan(0, stride * rows), stride, gray, cancel);
            header->Height = rows;   // positive: bottom row first
            var y0 = (int)Math.Round(top * sy) - page.OffsetY;
            var y1 = (int)Math.Round((top + rows) * sy) - page.OffsetY;
            int drawn;
            fixed (byte* bits = buffer)
                drawn = StretchDIBits(dc, -page.OffsetX, y0, (int)Math.Round(width * sx), y1 - y0, 0, 0, width, rows, bits, header, 0, SrcCopy);
            if (drawn == 0) throw new InvalidOperationException(Tr.T("Der Drucker hat die Seite abgelehnt."));
        }
    }

    /// <summary>Public part of DEVMODEW (wingdi.h); fields are set together with their bit in dmFields.</summary>
    private readonly ref struct DevMode(byte[] data)
    {
        public const int PublicSize = 220;
        private readonly Span<byte> _d = data;

        public int TotalSize => BinaryPrimitives.ReadUInt16LittleEndian(_d[68..]) + BinaryPrimitives.ReadUInt16LittleEndian(_d[70..]);
        private uint Fields
        {
            get => BinaryPrimitives.ReadUInt32LittleEndian(_d[72..]);
            set => BinaryPrimitives.WriteUInt32LittleEndian(_d[72..], value);
        }

        private short Get(int offset) => BinaryPrimitives.ReadInt16LittleEndian(_d[offset..]);

        private void Set(int offset, uint field, short value)
        {
            BinaryPrimitives.WriteInt16LittleEndian(_d[offset..], value);
            Fields |= field;
        }

        public short Orientation { get => Get(76); set => Set(76, 0x1, value); }
        public short PaperSize { get => Get(78); set => Set(78, 0x2, value); }
        public short Scale { get => Get(84); set => Set(84, 0x10, value); }

        /// <summary>Clears DM_PAPERLENGTH and DM_PAPERWIDTH (they would take precedence over dmPaperSize).</summary>
        public void ClearCustomSize() => Fields &= ~(0x4u | 0x8u);
        public short Copies { get => Get(86); set => Set(86, 0x100, value); }
        public short DefaultSource { get => Get(88); set => Set(88, 0x200, value); }
        public short PrintQuality { get => Get(90); set => Set(90, 0x400, value); }
        public short Color { get => Get(92); set => Set(92, 0x800, value); }
        public short Duplex { get => Get(94); set => Set(94, 0x1000, value); }
        public short YResolution { get => Get(96); set => Set(96, 0x2000, value); }
        public short Collate { get => Get(100); set => Set(100, 0x8000, value); }
    }

    // ------------------------------------------------------------ Native calls

    private const ushort DcPapers = 2, DcPaperSize = 3, DcBins = 6, DcDuplex = 7, DcBinNames = 12, DcEnumResolutions = 13,
        DcPaperNames = 16, DcCopies = 18, DcColorDevice = 32;
    private const uint DmOutBuffer = 2, DmInPrompt = 4, DmInBuffer = 8, SrcCopy = 0x00CC0020;
    private const int IdOk = 1, HorzRes = 8, VertRes = 10, LogPixelsX = 88, LogPixelsY = 90, PhysicalWidthCap = 110, PhysicalHeightCap = 111,
        PhysicalOffsetX = 112, PhysicalOffsetY = 113;

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo4
    {
        public char* Name;
        public char* Server;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo5
    {
        public char* Name;
        public char* Port;
        public uint Attributes;
        public uint DeviceNotSelectedTimeout;
        public uint TransmissionRetryTimeout;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DocInfo
    {
        public int Size;
        public char* DocName;
        public char* Output;
        public char* Datatype;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [LibraryImport("winspool.drv", EntryPoint = "GetDefaultPrinterW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDefaultPrinterW(char* buffer, ref int size);

    [LibraryImport("winspool.drv", EntryPoint = "EnumPrintersW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumPrintersW(uint flags, string? name, uint level, byte* buffer, uint size, out uint needed, out uint returned);

    [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenPrinterW(string name, out nint handle, nint defaults);

    [LibraryImport("winspool.drv", EntryPoint = "ClosePrinter")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClosePrinter(nint handle);

    [LibraryImport("winspool.drv", EntryPoint = "GetPrinterW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPrinterW(nint handle, uint level, byte* buffer, uint size, out uint needed);

    [LibraryImport("winspool.drv", EntryPoint = "DocumentPropertiesW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DocumentPropertiesW(nint window, nint printer, string device, byte* output, byte* input, uint mode);

    [LibraryImport("winspool.drv", EntryPoint = "DeviceCapabilitiesW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DeviceCapabilitiesW(string device, string? port, ushort capability, byte* output, byte* devMode);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDCW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateDCW(string driver, string device, string? output, byte* devMode);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateICW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateICW(string driver, string device, string? output, byte* devMode);

    [LibraryImport("gdi32.dll", EntryPoint = "ResetDCW")]
    private static partial nint ResetDCW(nint dc, byte* devMode);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteDC")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll", EntryPoint = "GetDeviceCaps")]
    private static partial int GetDeviceCaps(nint dc, int index);

    [LibraryImport("gdi32.dll", EntryPoint = "StartDocW", SetLastError = true)]
    private static partial int StartDocW(nint dc, DocInfo* info);

    [LibraryImport("gdi32.dll", EntryPoint = "StartPage", SetLastError = true)]
    private static partial int StartPage(nint dc);

    [LibraryImport("gdi32.dll", EntryPoint = "EndPage", SetLastError = true)]
    private static partial int EndPage(nint dc);

    [LibraryImport("gdi32.dll", EntryPoint = "EndDoc")]
    private static partial int EndDoc(nint dc);

    [LibraryImport("gdi32.dll", EntryPoint = "AbortDoc")]
    private static partial int AbortDoc(nint dc);

    [LibraryImport("gdi32.dll", EntryPoint = "StretchDIBits")]
    private static partial int StretchDIBits(nint dc, int xDest, int yDest, int destWidth, int destHeight, int xSrc, int ySrc,
        int srcWidth, int srcHeight, void* bits, BitmapInfoHeader* info, uint usage, uint rop);
}
