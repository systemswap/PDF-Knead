namespace PdfKnead.Core.Pdf.Editing;

/// <summary>Labels of form fields for screen readers (docs/ABLAUFPLAN.md, 3.3 d; docs/FEHLERGEDAECHTNIS.md, No. 493).</summary>
public static partial class FieldLabels
{
    /// <summary>
    /// The label of a field for screen readers (docs/FEHLERGEDAECHTNIS.md, No. 493): the tooltip; else the name, if it is no technical
    /// one (a field name adds nothing for a listener, ISO 14289-2, 8.10.2); null if there is none.
    /// </summary>
    public static string? ScreenReaderLabel(string tooltip, string name)
    {
        if (tooltip.Trim() is { Length: > 0 } given) return given;
        var trimmed = name.Trim();
        return trimmed.Length > 0 && !TechnicalName(trimmed) ? trimmed : null;
    }

    /// <summary>
    /// A technical field name: letters, digits and separators without a space, ending in a number („Text1“, „Feld_3“, „Textfeld12“,
    /// „form1[0].Name[0]“) — the names programs and PDF-Knead itself give („Textfeld1“, „Optionsgruppe2“).
    /// </summary>
    public static bool TechnicalName(string name) => TechnicalNamePattern().IsMatch(name);

    [System.Text.RegularExpressions.GeneratedRegex(@"^[\p{L}\p{N}_.\[\]-]*\p{N}\]?$")]
    private static partial System.Text.RegularExpressions.Regex TechnicalNamePattern();
}
