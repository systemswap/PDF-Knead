using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.ViewModels;

/// <summary>The names of the items of „Dokument bereinigen“: without a number (the question before signing) and with the details of a finding.</summary>
internal static class CleanupLabels
{
    /// <summary>The title of a finding: what, how many.</summary>
    /// <param name="finding">The finding.</param>
    /// <returns>The translated title.</returns>
    public static string Title(CleanupFinding finding) => finding.Item switch
    {
        CleanupItems.Metadata => Tr.T("Dokumenteigenschaften und Metadaten"),
        CleanupItems.Attachments => Tr.T("Eingebettete Dateien: {0}", finding.Count),
        CleanupItems.Scripts => Tr.T("JavaScript und Aktionen: {0}", finding.Count),
        CleanupItems.Thumbnails => Tr.T("Vorschaubilder der Seiten: {0}", finding.Count),
        CleanupItems.PrivateData => Tr.T("Daten anderer Programme (etwa die ganze Datei eines Zeichenprogramms)"),
        CleanupItems.Comments => Tr.T("Kommentare: {0}", finding.Count),
        CleanupItems.Bookmarks => Tr.T("Lesezeichen: {0}", finding.Count),
        CleanupItems.Structure => Tr.T("Strukturbaum mit Alternativtexten — die Datei ist danach nicht mehr barrierefrei"),
        CleanupItems.HiddenLayers => finding.Count == 1 ? Tr.T("Inhalt einer ausgeblendeten Ebene") : Tr.T("Inhalte ausgeblendeter Ebenen: {0}", finding.Count),
        CleanupItems.InvisibleText => finding.Count == 1
            ? Tr.T("Unsichtbarer Text auf 1 Seite (etwa die Texterkennung eines Scans — danach nicht mehr durchsuchbar)")
            : Tr.T("Unsichtbarer Text auf {0} Seiten (etwa die Texterkennung eines Scans — danach nicht mehr durchsuchbar)", finding.Count),
        CleanupItems.HiddenContent => finding.Count > 0
            ? Tr.T("Was niemand sieht: ausgeblendete Felder, Inhalte neben der Seite, ungenutzte Bilder")
            : Tr.T("Ausgeblendete Felder, die bleiben (etwas kann sie zeigen)"),
        CleanupItems.SuspiciousText => finding.Count == 1
            ? Tr.T("Vermutlich unsichtbarer Text auf 1 Seite (weiß auf weiß oder verdeckt — nur eine Vermutung)")
            : Tr.T("Vermutlich unsichtbarer Text auf {0} Seiten (weiß auf weiß oder verdeckt — nur eine Vermutung)", finding.Count),
        CleanupItems.OtherData => Tr.T("Weitere Dokumentdaten, die keine Seite zeigt"),
        CleanupItems.Notices => Tr.T("Copyright- und Lizenztexte in Schriften und Farbprofilen"),
        CleanupItems.HiddenImageParts => finding.Count == 1
            ? Tr.T("Verdeckte Teile von 1 Bild (ein Rahmen oder der Seitenrand schneidet es ab) — auf den sichtbaren Teil zuschneiden")
            : Tr.T("Verdeckte Teile von {0} Bildern (ein Rahmen oder der Seitenrand schneidet sie ab) — auf den sichtbaren Teil zuschneiden", finding.Count),
        CleanupItems.FieldValueMismatch => finding.Count == 1
            ? Tr.T("1 Formularfeld zeigt etwas anderes als seinen gespeicherten Wert — aus dem Wert neu anzeigen")
            : Tr.T("{0} Formularfelder zeigen etwas anderes als ihren gespeicherten Wert — aus dem Wert neu anzeigen", finding.Count),
        _ => finding.Item.ToString(),
    };

    /// <summary>What has to be said right under an item (consequences, licences); null if nothing.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The translated note, or null.</returns>
    public static string? Note(CleanupItems item) => item switch
    {
        CleanupItems.HiddenImageParts => Tr.T("(Die Seite sieht danach gleich aus. JPEG-Bilder werden dabei neu gespeichert.)"),
        CleanupItems.FieldValueMismatch => Tr.T("(Weiterverarbeiten, Exportieren und eine digitale Signatur nehmen den gespeicherten Wert. Danach zeigt das Feld ihn.)"),
        // licences: the notices may have to stay — said right under the box, not hidden in a tooltip
        CleanupItems.Notices => Tr.T("(Sie können beliebigen Text tragen. Viele Schriftlizenzen verlangen aber, dass diese Hinweise erhalten bleiben — nur entfernen, wenn die Lizenz das erlaubt.)"),
        _ => null,
    };

    /// <summary>What nobody sees is preselected; presumably invisible text (only a guess) and what is useful to the reader are not.</summary>
    public const CleanupItems Recommended = CleanupItems.Metadata | CleanupItems.Attachments | CleanupItems.Scripts |
                                            CleanupItems.Thumbnails | CleanupItems.PrivateData | CleanupItems.HiddenContent | CleanupItems.OtherData;

    /// <summary>
    /// Every item the sanitising offers, in the order shown. Only what the file contains is listed: a list of mostly greyed-out items was hard
    /// to take in.
    /// </summary>
    public static readonly CleanupItems[] Order =
    [
        CleanupItems.Metadata, CleanupItems.Attachments, CleanupItems.Scripts, CleanupItems.Thumbnails, CleanupItems.PrivateData,
        CleanupItems.Comments, CleanupItems.Bookmarks, CleanupItems.Structure, CleanupItems.HiddenLayers, CleanupItems.InvisibleText,
        CleanupItems.HiddenContent, CleanupItems.SuspiciousText, CleanupItems.OtherData, CleanupItems.Notices, CleanupItems.HiddenImageParts,
        CleanupItems.FieldValueMismatch,
    ];

    /// <summary>The label of an item (without a number).</summary>
    /// <param name="item">The item.</param>
    /// <returns>The translated label.</returns>
    public static string Of(CleanupItems item) => item switch
    {
        CleanupItems.Metadata => Tr.T("Dokumenteigenschaften und Metadaten"),
        CleanupItems.Attachments => Tr.T("Eingebettete Dateien"),
        CleanupItems.Scripts => Tr.T("JavaScript und Aktionen"),
        CleanupItems.Thumbnails => Tr.T("Vorschaubilder der Seiten"),
        CleanupItems.PrivateData => Tr.T("Daten anderer Programme (etwa die ganze Datei eines Zeichenprogramms)"),
        CleanupItems.Comments => Tr.T("Kommentare"),
        CleanupItems.Bookmarks => Tr.T("Lesezeichen"),
        CleanupItems.Structure => Tr.T("Strukturbaum mit Alternativtexten — die Datei ist danach nicht mehr barrierefrei"),
        CleanupItems.HiddenLayers => Tr.T("Inhalte ausgeblendeter Ebenen"),
        CleanupItems.InvisibleText => Tr.T("Unsichtbarer Text (etwa die Texterkennung eines Scans)"),
        CleanupItems.HiddenContent => Tr.T("Was niemand sieht: ausgeblendete Felder, Inhalte neben der Seite, ungenutzte Bilder"),
        CleanupItems.SuspiciousText => Tr.T("Vermutlich unsichtbarer Text (weiß auf weiß oder verdeckt)"),
        CleanupItems.OtherData => Tr.T("Weitere Dokumentdaten, die keine Seite zeigt"),
        CleanupItems.Notices => Tr.T("Copyright- und Lizenztexte in Schriften und Farbprofilen"),
        CleanupItems.HiddenImageParts => Tr.T("Verdeckte Teile von Bildern"),
        CleanupItems.FieldValueMismatch => Tr.T("Formularfelder, die etwas anderes zeigen als ihren gespeicherten Wert"),
        _ => item.ToString(),
    };
}
