using System.Globalization;
using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Rendering;

namespace PdfKnead.ViewModels;

/// <summary>
/// What the panels say about a code (KONZEPT.md, section 5.19), the same for inserted codes and code fields: how to write the content
/// of its kind, why the content gives no code, the module size against the smallest that scanners read reliably, the contrast, and the
/// self-check — the code drawn as on the page and read back.
/// </summary>
public static class CodeHints
{
    /// <summary>How to write the content of a kind.</summary>
    /// <param name="format">The kind.</param>
    /// <param name="gs1">The content is GS1 data (QR and Data Matrix; GS1-128 always is).</param>
    /// <returns>The hint.</returns>
    public static string ContentHint(BarcodeFormat format, bool gs1) => format switch
    {
        BarcodeFormat.Ean13 => Tr.T("12 Ziffern — die Prüfziffer wird ergänzt (oder 13 mit Prüfziffer)."),
        BarcodeFormat.Ean8 => Tr.T("7 Ziffern — die Prüfziffer wird ergänzt (oder 8 mit Prüfziffer)."),
        BarcodeFormat.UpcA => Tr.T("11 Ziffern — die Prüfziffer wird ergänzt (oder 12 mit Prüfziffer)."),
        BarcodeFormat.UpcE => Tr.T("Nummernsystem 0 oder 1 und 6 Ziffern (oder 8 mit Prüfziffer)."),
        BarcodeFormat.Itf => Tr.T("Eine gerade Anzahl Ziffern, mindestens 6."),
        BarcodeFormat.Code39 => Tr.T("Großbuchstaben, Ziffern, Leerzeichen und - . $ / + %"),
        _ when CodeSymbol.UsesGs1(format, gs1) => Tr.T("Datenbezeichner in Klammern, z. B. (01)04012345678901(17)261231(10)A1"),
        BarcodeFormat.Code128 => Tr.T("Buchstaben, Ziffern und Zeichen (ASCII)."),
        _ => Tr.T("Beliebiger Text, eine Adresse, eine Überweisung …"),
    };

    /// <summary>Why the content gives no code; null if it does.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The reason, or null.</returns>
    public static string? Error(CodeElement code) => ElementAppearance.CodeSymbolOf(code).Error;

    /// <summary>„Modulbreite 0,42 mm“; empty without a symbol.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The text.</returns>
    public static string ModuleText(CodeElement code) => ElementAppearance.CodeModuleMm(code) is var mm and > 0
        ? Tr.T("Modulbreite {0} mm", mm.ToString("0.00", CultureInfo.CurrentCulture)) : "";

    /// <summary>Modules smaller than scanners read reliably; null otherwise.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The warning, or null.</returns>
    public static string? ModuleWarning(CodeElement code) => ElementAppearance.CodeModuleMm(code) is var mm and > 0 && mm < CodeSymbol.MinimumModuleMm(code.Format)
        ? Tr.T("Kleiner als {0} mm: viele Scanner lesen das nicht sicher — den Code größer ziehen.", CodeSymbol.MinimumModuleMm(code.Format).ToString("0.##", CultureInfo.CurrentCulture))
        : null;

    /// <summary>Too little contrast, or light modules on a dark background; null otherwise (and without a symbol).</summary>
    /// <param name="code">The code.</param>
    /// <returns>The warning, or null.</returns>
    public static string? ContrastWarning(CodeElement code) => Error(code) is not null ? null : ElementAppearance.CodeContrast(code) switch
    {
        < 0 => Tr.T("Helle Striche auf dunklem Grund: viele Scanner lesen das nicht."),
        < 0.4 => Tr.T("Zu wenig Kontrast zwischen Code und Hintergrund: dunkler Code auf hellem Grund ist am sichersten."),
        _ => null,
    };

    /// <summary>The self-check: draws the code as on the page and reads it back (in the background).</summary>
    /// <param name="code">The code (with a symbol).</param>
    /// <param name="cancel">Cancels the check.</param>
    /// <returns>Whether the drawn code reads back as the content of the symbol.</returns>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static async Task<bool> SelfCheck(CodeElement code, CancellationToken cancel)
    {
        var expected = ElementAppearance.CodeSymbolOf(code).Symbol?.Encoded;
        if (expected is null) return false;
        var read = await Task.Run(() => CodeCheck.Read(code, cancel), cancel);
        cancel.ThrowIfCancellationRequested();
        return read is not null && read.Format == code.Format && read.Text == expected;
    }

    /// <summary>Name of a code field by its kind: „QR-Code-Feld“, „EAN-13-Feld“.</summary>
    /// <param name="look">The look of the field's code.</param>
    /// <returns>The name.</returns>
    public static string FieldTitle(CodeLook look) => Tr.T("{0}-Feld", CodeSymbol.Name(look.Format));

    /// <summary>Line of the self-check.</summary>
    /// <param name="ok">Whether the code was read back.</param>
    /// <returns>The text.</returns>
    public static string CheckText(bool ok) => ok ? Tr.T("Probe gelesen: der Code ist lesbar.") : Tr.T("Probe nicht lesbar — Farben, Größe oder Ruhezone prüfen.");
}
