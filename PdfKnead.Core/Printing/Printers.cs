using System.Diagnostics;

namespace PdfKnead.Core.Printing;

/// <summary>An installed printer.</summary>
/// <param name="Name">Printer name as known to the system.</param>
/// <param name="IsDefault">Whether this is the system's default printer.</param>
public sealed record PrinterInfo(string Name, bool IsDefault)
{
    /// <summary>Display text: the name, marked as default where applicable (localised).</summary>
    /// <returns>The display text.</returns>
    public override string ToString() => IsDefault ? Tr.T("{0} (Standard)", Name) : Name;
}

/// <summary>Double-sided printing.</summary>
public enum DuplexMode
{
    /// <summary>Single-sided.</summary>
    OneSided,

    /// <summary>Flip on the long edge — like a book in portrait orientation.</summary>
    LongEdge,

    /// <summary>Flip on the short edge — like a pad bound at the top.</summary>
    ShortEdge,

    /// <summary>By hand: front sides first, turn the stack over, then the back sides (printers without duplex unit).</summary>
    Manual,
}

/// <summary>One choice of a printer option.</summary>
/// <param name="Value">Value passed to the printer.</param>
/// <param name="Label">Text shown to the user.</param>
public sealed record PrinterChoice(string Value, string Label)
{
    /// <summary>Returns <see cref="Label"/>.</summary>
    /// <returns>The display text.</returns>
    public override string ToString() => Label;
}

/// <summary>Paper size offered by the printer; dimensions in points, portrait orientation.</summary>
/// <param name="Value">Value passed to the printer.</param>
/// <param name="Label">Text shown to the user.</param>
/// <param name="Width">Width in points.</param>
/// <param name="Height">Height in points.</param>
public sealed record PaperSize(string Value, string Label, double Width, double Height)
{
    /// <summary>Returns <see cref="Label"/>.</summary>
    /// <returns>The display text.</returns>
    public override string ToString() => Label;
}

/// <summary>Further option offered by the printer (under Linux from CUPS, e.g. stapling, paper type).</summary>
/// <param name="Key">Option name passed to the printer.</param>
/// <param name="Label">Text shown to the user.</param>
/// <param name="Choices">Available values.</param>
/// <param name="Default">Default value, or null if unknown.</param>
public sealed record PrinterOption(string Key, string Label, IReadOnlyList<PrinterChoice> Choices, string? Default);

/// <summary>
/// What a printer can do. For <see cref="Duplex"/> and <see cref="Color"/>, null means "unknown" (the system does not tell) — the
/// option stays selectable; false means definitely not available — the option is greyed out.
/// </summary>
/// <param name="Papers">Paper sizes offered.</param>
/// <param name="DefaultPaper">Value of the default paper size, or null.</param>
/// <param name="Trays">Paper trays offered.</param>
/// <param name="DefaultTray">Value of the default tray, or null.</param>
/// <param name="Qualities">Print qualities offered.</param>
/// <param name="DefaultQuality">Value of the default quality, or null.</param>
/// <param name="Duplex">Whether double-sided printing is available; null = unknown.</param>
/// <param name="Color">Whether colour printing is available; null = unknown.</param>
/// <param name="MaxCopies">Maximum number of copies per job.</param>
/// <param name="Extra">Further options offered by the printer.</param>
public sealed record PrinterCapabilities(
    IReadOnlyList<PaperSize> Papers,
    string? DefaultPaper,
    IReadOnlyList<PrinterChoice> Trays,
    string? DefaultTray,
    IReadOnlyList<PrinterChoice> Qualities,
    string? DefaultQuality,
    bool? Duplex,
    bool? Color,
    int MaxCopies,
    IReadOnlyList<PrinterOption> Extra)
{
    /// <summary>Capabilities when the system reports nothing: no lists, everything unknown, at most 999 copies.</summary>
    public static PrinterCapabilities Unknown { get; } = new([], null, [], null, [], null, null, null, 999, []);
}

/// <summary>Settings of a print job as the printer understands them (values from <see cref="PrinterCapabilities"/>).</summary>
public sealed record PrinterSettings
{
    /// <summary>Value of the paper size (<see cref="PaperSize.Value"/>), or null for the printer default.</summary>
    public string? Paper { get; init; }

    /// <summary>Value of the paper tray (<see cref="PrinterChoice.Value"/>), or null for the printer default.</summary>
    public string? Tray { get; init; }

    /// <summary>Value of the print quality (<see cref="PrinterChoice.Value"/>), or null for the printer default.</summary>
    public string? Quality { get; init; }

    /// <summary>Single- or double-sided printing.</summary>
    public DuplexMode Duplex { get; init; }

    /// <summary>Print in greyscale.</summary>
    public bool Grayscale { get; init; }

    /// <summary>Number of copies.</summary>
    public int Copies { get; init; } = 1;

    /// <summary>Collate copies.</summary>
    public bool Collate { get; init; } = true;

    /// <summary>Further options (Linux): key → value.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } = new Dictionary<string, string>();

    /// <summary>Windows: settings from the manufacturer's dialog (DEVMODE), for this session only; null = none.</summary>
    public byte[]? DriverData { get; init; }
}

/// <summary>The sheets of a job, rendered in bands (Windows) or written as PDF (CUPS, save as PDF).</summary>
public interface ISheetSource
{
    /// <summary>Number of sheets.</summary>
    int SheetCount { get; }

    /// <summary>Size of the sheet in points as it lies (landscape: wider than high).</summary>
    /// <param name="sheet">Zero-based sheet index.</param>
    /// <returns>Width and height in points.</returns>
    (double Width, double Height) SheetSize(int sheet);

    /// <summary>
    /// Renders a band of the sheet: rows <paramref name="top"/> … <paramref name="top"/> + <paramref name="rows"/> at
    /// <paramref name="pixelsPerPoint"/>, as 8-bit BGR (<paramref name="gray"/>: one grey byte per pixel), bottom row first (like a
    /// Windows DIB), row length <paramref name="stride"/>.
    /// </summary>
    /// <param name="sheet">Zero-based sheet index.</param>
    /// <param name="pixelsPerPoint">Resolution in pixels per point.</param>
    /// <param name="top">First row of the band, counted from the top of the sheet.</param>
    /// <param name="width">Width of the band in pixels.</param>
    /// <param name="rows">Number of rows in the band.</param>
    /// <param name="pixels">Target buffer, at least <paramref name="rows"/> × <paramref name="stride"/> bytes.</param>
    /// <param name="stride">Row length in bytes.</param>
    /// <param name="gray">One grey byte per pixel instead of BGR.</param>
    /// <param name="cancel">Cancels rendering.</param>
    void RenderBand(int sheet, double pixelsPerPoint, int top, int width, int rows, Span<byte> pixels, int stride, bool gray, CancellationToken cancel);

    /// <summary>Writes the sheets <paramref name="sheets"/> as PDF (−1 = blank page, e.g. the back of the last sheet).</summary>
    /// <param name="sheets">Sheet indices in output order.</param>
    /// <param name="output">Target stream.</param>
    /// <param name="cancel">Cancels writing.</param>
    void WritePdf(IReadOnlyList<int> sheets, Stream output, CancellationToken cancel);

    /// <summary>Writes the sheets as images in a PDF (permission "low resolution only" with CUPS).</summary>
    /// <param name="sheets">Sheet indices in output order (−1 = blank page).</param>
    /// <param name="dpi">Image resolution in dots per inch.</param>
    /// <param name="output">Target stream.</param>
    /// <param name="cancel">Cancels writing.</param>
    void WriteRasterPdf(IReadOnlyList<int> sheets, int dpi, Stream output, CancellationToken cancel);
}

/// <summary>A print job.</summary>
/// <param name="Printer">Printer name.</param>
/// <param name="Title">Job title shown in the print queue.</param>
/// <param name="Source">The sheets to print.</param>
/// <param name="Sheets">Which sheets, in this order (indices into <paramref name="Source"/>; −1 = blank back side).</param>
/// <param name="Settings">Printer settings of the job.</param>
/// <param name="Dpi">Resolution at which Windows renders the sheets (300, 600; 150 with "low resolution only").</param>
/// <param name="OutputFile">Windows: write to this file instead of the printer (for drivers that write files, such as "Microsoft Print to PDF"; tests).</param>
/// <param name="LowResolution">Permission "low resolution only": CUPS also receives only images at <paramref name="Dpi"/>.</param>
public sealed record PrintJob(string Printer, string Title, ISheetSource Source, IReadOnlyList<int> Sheets, PrinterSettings Settings, int Dpi = 300,
    string? OutputFile = null, bool LowResolution = false);

/// <summary>
/// System printers: Windows via the print spooler (winspool/GDI, own P/Invoke calls, no extra package), Linux/macOS via CUPS
/// (<c>lpstat</c>, <c>lpoptions</c>, <c>lp</c>). Everything can hang if the print service hangs — callers invoke it in the background
/// so that a hanging print service does not freeze the dialog.
/// </summary>
public static class Printers
{
    /// <summary>Lists the installed printers.</summary>
    /// <returns>The printers, default printer first, then by name; empty if no print service is reachable.</returns>
    public static IReadOnlyList<PrinterInfo> List()
    {
        try
        {
            IEnumerable<PrinterInfo> list = OperatingSystem.IsWindows() ? WindowsPrinting.List()
                : OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() ? CupsPrinting.List() : [];
            return [.. list.OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLogSafe("Druckerliste", e);
            return [];
        }
    }

    /// <summary>Queries what the printer can do (as far as the system tells).</summary>
    /// <param name="printer">Printer name.</param>
    /// <returns>The capabilities, or <see cref="PrinterCapabilities.Unknown"/> if the query fails or the platform is not supported.</returns>
    public static PrinterCapabilities Capabilities(string printer)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsPrinting.Capabilities(printer);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return CupsPrinting.Capabilities(printer);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLogSafe("Druckerfähigkeiten", e);
        }
        return PrinterCapabilities.Unknown;
    }

    /// <summary>Largest margin in points that the printer cannot print on with this paper.</summary>
    /// <param name="printer">Printer name.</param>
    /// <param name="settings">Job settings (paper, tray …).</param>
    /// <returns>The margin in points; null if unknown (always outside Windows).</returns>
    public static double? Margin(string printer, PrinterSettings settings)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return WindowsPrinting.Margin(printer, settings);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLogSafe("Druckrand", e);
        }
        return null;
    }

    /// <summary>Whether the printer manufacturer's dialog can be opened (Windows only).</summary>
    public static bool HasDriverDialog => OperatingSystem.IsWindows();

    /// <summary>
    /// Windows: shows the printer manufacturer's dialog over <paramref name="owner"/> (window handle). Runs on the UI thread (the dialog
    /// is modal).
    /// </summary>
    /// <param name="owner">Handle of the owner window.</param>
    /// <param name="printer">Printer name.</param>
    /// <param name="settings">Current job settings, used to initialise the dialog.</param>
    /// <returns>The new settings (DEVMODE), or null if cancelled or not on Windows.</returns>
    public static byte[]? DriverDialog(nint owner, string printer, PrinterSettings settings) =>
        OperatingSystem.IsWindows() ? WindowsPrinting.DriverDialog(owner, printer, settings) : null;

    /// <summary>Reads what is set after the manufacturer's dialog (paper, tray, duplex …), for the program's own fields.</summary>
    /// <param name="data">DEVMODE returned by <see cref="DriverDialog"/>.</param>
    /// <param name="current">Current settings, used for everything the data does not contain.</param>
    /// <returns>The updated settings; <paramref name="current"/> unchanged outside Windows.</returns>
    public static PrinterSettings FromDriverData(byte[] data, PrinterSettings current) =>
        OperatingSystem.IsWindows() ? WindowsPrinting.Read(data, current) : current;

    /// <summary>Prints the job (Windows via the spooler, otherwise via CUPS).</summary>
    /// <param name="job">The print job.</param>
    /// <param name="progress">Reports the finished sheet (1 …) and the total number, or null.</param>
    /// <param name="cancel">Cancels printing.</param>
    public static void Print(PrintJob job, IProgress<(int Done, int Total)>? progress, CancellationToken cancel)
    {
        if (OperatingSystem.IsWindows()) WindowsPrinting.Print(job, progress, cancel);
        else CupsPrinting.Print(job, progress, cancel);
    }

    /// <summary>
    /// Order of the sheets for copies made by PDF Knead itself. When printing double-sided, each copy is padded to an even number of
    /// sides, and uncollated copies repeat front and back together — otherwise the second copy would start on the back of the
    /// first.
    /// </summary>
    internal static List<int> CopyOrder(IReadOnlyList<int> sheets, int copies, bool collate, bool duplex)
    {
        if (copies <= 1) return [.. sheets];
        var one = new List<int>(sheets);
        if (duplex && one.Count % 2 == 1) one.Add(-1);
        var order = new List<int>(one.Count * copies);
        if (collate)
            for (var c = 0; c < copies; c++) order.AddRange(one);
        else if (duplex)
            for (var i = 0; i < one.Count; i += 2)
                for (var c = 0; c < copies; c++) order.AddRange(one.Skip(i).Take(2));
        else
            foreach (var s in one) order.AddRange(Enumerable.Repeat(s, copies));
        return order;
    }

    private static void AppLogSafe(string what, Exception e)
    {
        try { Platform.AppLog.Write(what, $"{e.GetType().Name}: {e.Message}"); }
        catch (Exception) { }
    }

    /// <summary>
    /// Full path of a system tool (lp, lpstat, lpoptions): only from the system folders. Under Unix, .NET also searches a bare name in
    /// the program folder and the current working folder — a file "lpstat" lying there (e.g. in an extracted archive) would otherwise
    /// be started as soon as the print window opens. Under Windows only the system folder is used for the same reason.
    /// </summary>
    internal static string ToolPath(string file)
    {
        if (Path.IsPathFullyQualified(file)) return file;
        // Windows: only the system folder (CreateProcess would otherwise search the program and working folders first)
        if (OperatingSystem.IsWindows())
        {
            var system = Path.Combine(Environment.SystemDirectory, Path.HasExtension(file) ? file : file + ".exe");
            return File.Exists(system) ? system : throw new System.ComponentModel.Win32Exception(2, Tr.T("{0} ließ sich nicht starten.", file));
        }
        foreach (var folder in (ReadOnlySpan<string>)["/usr/bin", "/bin", "/usr/local/bin", "/usr/sbin", "/opt/homebrew/bin"])
            if (Path.Combine(folder, file) is var path && File.Exists(path)) return path;
        throw new System.ComponentModel.Win32Exception(2, Tr.T("{0} ließ sich nicht starten.", file));   // like a missing program
    }

    /// <summary>
    /// Starts a command and fills its standard input with <paramref name="write"/> (buffered, without holding everything in memory).
    /// Cancelling terminates the process; if it ends early, its error message is returned instead of "pipe closed".
    /// </summary>
    internal static (int Code, string Output) RunWriting(string file, IEnumerable<string> args, Action<Stream> write, CancellationToken cancel,
        int timeoutMs = 120_000)
    {
        var psi = new ProcessStartInfo(ToolPath(file))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["LC_ALL"] = "C";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(Tr.T("{0} ließ sich nicht starten.", file));
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);
        void Kill()
        {
            try { p.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        using (cancel.Register(Kill))
        {
            try
            {
                // Do not dispose: that would close the input before StandardInput.Close (and throw if the process ended early)
                var input = new BufferedStream(p.StandardInput.BaseStream, 1 << 20);
                write(input);
                input.Flush();
            }
            catch (IOException)
            {
                // The process stopped reading early (or was terminated on cancellation): its message is returned below
            }
            finally
            {
                try { p.StandardInput.Close(); }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            }
            cancel.ThrowIfCancellationRequested();
            if (!p.WaitForExit(timeoutMs))
            {
                Kill();
                throw new TimeoutException(Tr.T("{0} antwortet nicht.", file));
            }
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    /// <summary>
    /// Starts a command, reads its output in parallel and waits for a limited time — if the print service hangs, the process is
    /// terminated (<see cref="TimeoutException"/>). Output in English (LC_ALL=C), otherwise it would be translated.
    /// </summary>
    internal static (int Code, string Output) Run(string file, IEnumerable<string> args, ReadOnlySpan<byte> input = default, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(ToolPath(file))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = !input.IsEmpty,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["LC_ALL"] = "C";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException(Tr.T("{0} ließ sich nicht starten.", file));
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!input.IsEmpty)
        {
            p.StandardInput.BaseStream.Write(input);
            p.StandardInput.Close();
        }
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw new TimeoutException(Tr.T("{0} antwortet nicht.", file));
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
