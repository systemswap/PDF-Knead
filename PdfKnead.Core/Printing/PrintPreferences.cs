namespace PdfKnead.Core.Printing;

/// <summary>Settings remembered per printer (the values apply to that printer only).</summary>
/// <param name="Paper">Value of the chosen <see cref="PaperSize"/>, or null if none was chosen.</param>
/// <param name="Tray">Value of the chosen paper tray (<see cref="PrinterChoice.Value"/>), or null.</param>
/// <param name="Quality">Value of the chosen print quality (<see cref="PrinterChoice.Value"/>), or null.</param>
/// <param name="Duplex">Single- or double-sided printing.</param>
/// <param name="Grayscale">Print in greyscale.</param>
/// <param name="Extra">Further printer options (<see cref="PrinterOption.Key"/> → chosen value), or null.</param>
public sealed record PrinterPreferences(
    string? Paper = null,
    string? Tray = null,
    string? Quality = null,
    DuplexMode Duplex = DuplexMode.OneSided,
    bool Grayscale = false,
    IReadOnlyDictionary<string, string>? Extra = null);

/// <summary>Most recently used print settings (the pages are chosen anew each time).</summary>
/// <param name="Printer">Most recently used printer; <see cref="PdfTarget"/> = "save as PDF".</param>
/// <param name="Scaling">How pages are scaled onto the sheet.</param>
/// <param name="Percent">Scale in percent for custom scaling.</param>
/// <param name="Orientation">Sheet orientation.</param>
/// <param name="PagesPerSheet">Number of pages per sheet side.</param>
/// <param name="Order">Order of the pages on a sheet with several pages.</param>
/// <param name="Borders">Draw a border around each page.</param>
/// <param name="Collate">Collate copies.</param>
/// <param name="Printers">Per-printer settings (at most <see cref="MaxPrinters"/>), or null.</param>
/// <param name="Booklet">Last printed as a booklet; <paramref name="BookletSettings"/> holds its settings.</param>
/// <param name="BookletSettings">Booklet settings, or null for the defaults.</param>
/// <param name="Poster">Last printed as a poster (see KONZEPT.md, section 5.11); <paramref name="PosterSettings"/> holds its settings.</param>
/// <param name="PosterSettings">Poster settings, or null for the defaults.</param>
public sealed record PrintPreferences(
    string? Printer = null,
    PrintScaling Scaling = PrintScaling.Fit,
    double Percent = 100,
    PrintOrientation Orientation = PrintOrientation.Auto,
    int PagesPerSheet = 1,
    SheetOrder Order = SheetOrder.Horizontal,
    bool Borders = false,
    bool Collate = true,
    IReadOnlyDictionary<string, PrinterPreferences>? Printers = null,
    bool Booklet = false,
    BookletOptions? BookletSettings = null,
    bool Poster = false,
    PosterOptions? PosterSettings = null)
{
    /// <summary>Most recently chosen print kind (poster takes precedence over booklet).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PrintKind Kind => Poster ? PrintKind.Poster : Booklet ? PrintKind.Booklet : PrintKind.Normal;

    /// <summary>Identifier for "save as PDF" (no printer has this name: colons are unusual in printer names).</summary>
    public const string PdfTarget = "::pdf";

    /// <summary>Maximum number of printers remembered (the most recently used ones).</summary>
    public const int MaxPrinters = 20;

    /// <summary>Returns the remembered settings of a printer.</summary>
    /// <param name="printer">Printer name.</param>
    /// <returns>The stored settings, or default settings if none are stored.</returns>
    public PrinterPreferences For(string printer) => Printers?.GetValueOrDefault(printer) ?? new PrinterPreferences();

    /// <summary>Returns a copy with <paramref name="printer"/> as the current printer and its settings stored.</summary>
    /// <param name="printer">Printer name.</param>
    /// <param name="preferences">Settings to remember for this printer.</param>
    /// <returns>The new preferences; the oldest entries are dropped beyond <see cref="MaxPrinters"/>.</returns>
    public PrintPreferences With(string printer, PrinterPreferences preferences)
    {
        var all = (Printers ?? new Dictionary<string, PrinterPreferences>()).Where(p => p.Key != printer)
            .TakeLast(MaxPrinters - 1).ToDictionary(p => p.Key, p => p.Value);
        all[printer] = preferences;
        return this with { Printer = printer, Printers = all };
    }
}
