using System.Globalization;
using System.Text.RegularExpressions;

namespace PdfKnead.Core.Printing;

/// <summary>
/// Printing via CUPS (Linux, macOS): <c>lpstat</c> lists the printers, <c>lpoptions -l</c> reports all options of the printer (PPD
/// or IPP Everywhere), <c>lp</c> receives the sheets as PDF via standard input — no intermediate file is created on disk. Values for
/// paper, tray and quality are stored as "key=value" in <see cref="PrinterChoice.Value"/> (the keys differ between drivers).
/// </summary>
internal static partial class CupsPrinting
{
    public static IEnumerable<PrinterInfo> List()
    {
        var names = Printers.Run("lpstat", ["-e"]).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = Printers.Run("lpstat", ["-d"]).Output;
        var standard = line.Contains(':') && !line.Contains("no system default", StringComparison.OrdinalIgnoreCase)
            ? line[(line.IndexOf(':') + 1)..].Trim() : "";
        return names.Where(n => !n.Contains(' ')).Distinct().Select(n => new PrinterInfo(n, n == standard));
    }

    public static PrinterCapabilities Capabilities(string printer) => Parse(Printers.Run("lpoptions", ["-p", printer, "-l"]).Output);

    private static readonly HashSet<string> PaperKeys = ["PageSize", "media"], TrayKeys = ["InputSlot", "media-source"],
        DuplexKeys = ["Duplex", "sides"], ColorKeys = ["ColorModel", "print-color-mode", "ColorMode"],
        QualityKeys = ["Resolution", "print-quality", "cupsPrintQuality"],
        // Handled by PDF Knead itself (size, copies, orientation) — not offered again
        Skipped = ["PageRegion", "copies", "Collate", "orientation-requested", "number-up", "fit-to-page", "print-scaling", "page-ranges"];

    /// <summary>Parses the output of <c>lpoptions -p NAME -l</c>: lines "key/label: value1 *default value3".</summary>
    internal static PrinterCapabilities Parse(string output)
    {
        var papers = new List<PaperSize>();
        var trays = new List<PrinterChoice>();
        var qualities = new List<PrinterChoice>();
        var extra = new List<PrinterOption>();
        string? defaultPaper = null, defaultTray = null, defaultQuality = null;
        bool? duplex = null, color = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var slash = line.IndexOf('/');
            var key = line[..(slash > 0 && slash < colon ? slash : colon)].Trim();
            var label = slash > 0 && slash < colon ? line[(slash + 1)..colon].Trim() : key;
            string? standard = null;
            var values = new List<string>();
            foreach (var word in line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var value = word.TrimStart('*');
                if (value.Length == 0) continue;
                if (word.StartsWith('*')) standard = value;
                values.Add(value);
            }
            if (values.Count == 0) continue;

            if (PaperKeys.Contains(key))
            {
                foreach (var v in values)
                    if (PaperMillimeters(v) is var (w, h) && papers.All(p => p.Value != $"{key}={v}"))
                        papers.Add(new PaperSize($"{key}={v}", PaperLabel(v), Math.Min(w, h) * 72 / 25.4, Math.Max(w, h) * 72 / 25.4));
                if (standard is not null) defaultPaper ??= $"{key}={standard}";
            }
            else if (TrayKeys.Contains(key))
            {
                trays.AddRange(values.Select(v => new PrinterChoice($"{key}={v}", Readable(v))));
                if (standard is not null) defaultTray = $"{key}={standard}";
            }
            else if (DuplexKeys.Contains(key))
            {
                duplex = values.Any(v => v.Contains("two-sided", StringComparison.OrdinalIgnoreCase) ||
                                         v.StartsWith("DuplexNoTumble", StringComparison.OrdinalIgnoreCase) ||
                                         v.StartsWith("DuplexTumble", StringComparison.OrdinalIgnoreCase));
            }
            else if (ColorKeys.Contains(key))
            {
                var lower = values.Select(v => v.ToLowerInvariant()).ToList();
                var hasColor = lower.Any(v => v is "color" or "rgb" or "cmyk" or "rgbw" || v.Contains("color") && !v.Contains("gray"));
                var hasGray = lower.Any(v => v.Contains("monochrome") || v.Contains("gray") || v.Contains("grey") || v is "black" or "kgray");
                color = hasColor ? true : hasGray ? false : null;
            }
            else if (QualityKeys.Contains(key))
            {
                qualities.AddRange(values.Select(v => new PrinterChoice($"{key}={v}", Readable(v))));
                if (standard is not null) defaultQuality = $"{key}={standard}";
            }
            else if (!Skipped.Contains(key) && values.Count > 1)
            {
                extra.Add(new PrinterOption(key, label, [.. values.Select(v => new PrinterChoice(v, Readable(v)))], standard));
            }
        }
        return new PrinterCapabilities(papers, defaultPaper, trays, defaultTray, qualities, defaultQuality, duplex, color, 999, extra);
    }

    /// <summary>Dimensions of a paper name in mm: "A4", "Letter", IPP names such as "iso_a4_210x297mm" or "na_letter_8.5x11in"; null if unknown.</summary>
    internal static (double Width, double Height)? PaperMillimeters(string name)
    {
        if (IppSize().Match(name) is { Success: true } m)
        {
            var w = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var h = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var factor = m.Groups[3].Value == "in" ? 25.4 : 1;
            return w > 0 && h > 0 ? (w * factor, h * factor) : null;
        }
        var basic = name.Split('.')[0];   // "A4.Borderless", "A4.Fullbleed"
        return Pdf.Document.PageFormat.Millimeters(basic) ?? basic.ToLowerInvariant() switch
        {
            "b5" => (176, 250),
            "tabloid" or "11x17" => (279.4, 431.8),
            "executive" => (184.15, 266.7),
            "env10" => (104.8, 241.3),
            "envdl" or "dl" => (110, 220),
            "envc5" or "c5" => (162, 229),
            _ => null,
        };
    }

    [GeneratedRegex(@"_(\d+(?:\.\d+)?)x(\d+(?:\.\d+)?)(mm|in)$")]
    private static partial Regex IppSize();

    /// <summary>"iso_a4_210x297mm" → "A4", "na_letter_8.5x11in" → "Letter", otherwise the value in readable form.</summary>
    private static string PaperLabel(string value)
    {
        var parts = value.Split('_');
        return parts.Length >= 3 && IppSize().IsMatch(value) ? Readable(parts[1]) is var n && n.Length <= 2 ? n.ToUpperInvariant() : n : Readable(value);
    }

    private static string Readable(string value)
    {
        var text = value.Replace('-', ' ').Replace('_', ' ');
        return text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : value;
    }

    /// <summary>Command line for <c>lp</c>: options as IPP attributes (CUPS also translates them for older PPD drivers), data via stdin.</summary>
    internal static List<string> Arguments(PrintJob job, int copies)
    {
        var s = job.Settings;
        var args = new List<string> { "-d", job.Printer, "-t", job.Title, "-n", Math.Max(1, copies).ToString(CultureInfo.InvariantCulture) };
        void Option(string option) => args.AddRange(["-o", option]);
        if (copies > 1) Option(s.Collate ? "collate=true" : "collate=false");
        // The sheets already have paper size and margin: no further scaling
        Option("print-scaling=none");
        Option("fit-to-page=false");
        foreach (var value in new[] { s.Paper, s.Tray, s.Quality })
            if (value is not null && value.Contains('=')) Option(value);
        Option("sides=" + s.Duplex switch
        {
            DuplexMode.LongEdge => "two-sided-long-edge",
            DuplexMode.ShortEdge => "two-sided-short-edge",
            _ => "one-sided",
        });
        Option("print-color-mode=" + (s.Grayscale ? "monochrome" : "color"));
        foreach (var (key, value) in s.Extra)
            if (!Skipped.Contains(key) && !key.Contains(' ') && !value.Contains(' ')) Option($"{key}={value}");
        return args;
    }

    public static void Print(PrintJob job, IProgress<(int Done, int Total)>? progress, CancellationToken cancel)
    {
        // The PDF is streamed directly into lp: holding it completely in memory would make jobs over 2 GB impossible, and a hanging lp
        // would block forever
        var (code, output) = Printers.RunWriting("lp", Arguments(job, job.Settings.Copies), input =>
        {
            if (job.LowResolution) job.Source.WriteRasterPdf(job.Sheets, job.Dpi, input, cancel);
            else job.Source.WritePdf(job.Sheets, input, cancel);
        }, cancel);
        if (code != 0) throw new InvalidOperationException(Tr.T("Drucken fehlgeschlagen: {0}", output.Trim()));
        progress?.Report((job.Sheets.Count, job.Sheets.Count));
    }
}
