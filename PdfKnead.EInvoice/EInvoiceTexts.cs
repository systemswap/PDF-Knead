using System.Runtime.CompilerServices;

namespace PdfKnead.EInvoice;

/// <summary>
/// The English texts of the e-invoice module, keyed by the German text like English.cs. They live here, not in the core, so that the module
/// can be removed with its folders and leaves no text behind (KONZEPT.md, section 5.24, „Aufbau und Entfernen“); the module registers them
/// itself when it is loaded. The test EInvoiceTextsTests checks that every text of the module has its translation and none is left over.
/// </summary>
public static class EInvoiceTexts
{
    /// <summary>The table (German → English).</summary>
    public static IReadOnlyDictionary<string, string> Table => Texts;

    private static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal)
    {
        ["Darstellung, nicht die Rechnung – maßgeblich ist der XML-Teil."] = "A representation, not the invoice – the XML part is what counts.",
        ["ein Programm! Nicht öffnen."] = "a program! Do not open.",
        ["der Inhalt passt nicht zum angegebenen Typ"] = "the content does not match the declared type",
        ["Nach ZUGFeRD 1 gelesen: die Namen des Baums sind auf die der CII abgebildet, die Werte unverändert."] = "Read after ZUGFeRD 1: the names of the tree are mapped to those of CII, the values unchanged.",
        ["Katalog beschädigt: keine Anhänge lesbar"] = "Catalog damaged: no attachments readable",
        ["{0} beschädigt, Rest übergangen"] = "{0} damaged, the rest passed over",
        ["/AF des Katalogs"] = "/AF of the catalog",
        ["Namensbaum /EmbeddedFiles"] = "Name tree /EmbeddedFiles",
        ["Seitenbaum"] = "Page tree",
        ["Die Metadaten (XMP) nennen „{0}“ als eingebettete Rechnung; die Datei ist da, aber keine Rechnungs-XML."] =
            "The metadata (XMP) name “{0}” as the embedded invoice; the file is there, but it is no invoice XML.",
        ["Die Metadaten (XMP) nennen eine eingebettete Rechnung („{0}“), die Datei fehlt in der PDF."] =
            "The metadata (XMP) name an embedded invoice (“{0}”); the file is missing from the PDF.",
        ["kein PDF/A erklärt"] = "no PDF/A declared",
        ["erklärt: {0}"] = "declared: {0}",
        ["{0}: {1} Abweichungen der PDF/A-Prüfung, etwa {2}"] = "{0}: {1} deviations found by the PDF/A check, such as {2}",
        ["MIME-Typ (/Subtype)"] = "MIME type (/Subtype)",
        ["fehlt: {0}"] = "missing: {0}",
        ["XMP: {0}, XML: {1}"] = "XMP: {0}, XML: {1}",
        ["XMP: {0}, Datei: {1}"] = "XMP: {0}, file: {1}",
        ["Namensbaum /EmbeddedFiles: mehr als {0} Knoten besucht, Rest übergangen"] = "Name tree /EmbeddedFiles: more than {0} nodes visited, the rest passed over",
        ["Namensbaum /EmbeddedFiles: Schleife übergangen"] = "Name tree /EmbeddedFiles: loop passed over",
        ["Namensbaum /EmbeddedFiles: tiefer als erlaubt, Rest übergangen"] = "Name tree /EmbeddedFiles: deeper than allowed, the rest passed over",
        ["Namensbaum /EmbeddedFiles: mehr als {0} Knoten, Rest übergangen"] = "Name tree /EmbeddedFiles: more than {0} nodes, the rest passed over",
        ["Namensbaum /EmbeddedFiles: mehr als {0} Einträge, Rest übergangen"] = "Name tree /EmbeddedFiles: more than {0} entries, the rest passed over",
        ["{0} Rechnungs-XML im PDF; angezeigt wird „{1}“ — bitte prüfen, welche gilt."] = "{0} invoice XMLs in the PDF; “{1}” is shown — please check which one applies.",
        ["zu groß"] = "too large",
        ["nicht in UTF-8"] = "not in UTF-8",
        ["enthält eine DTD (DOCTYPE) — Zeichen eines Angriffs"] = "contains a DTD (DOCTYPE) — a sign of an attack",
        ["kein wohlgeformtes XML"] = "not well-formed XML",
        ["zu tief verschachtelt"] = "nested too deeply",
        ["zu viele Elemente"] = "too many elements",
        ["zu viele Attribute an einem Element"] = "too many attributes on one element",
        ["ein Attributwert ist zu lang"] = "an attribute value is too long",
        ["ein Name ist zu lang"] = "a name is too long",
        ["zu viele verschiedene Namen"] = "too many different names",
        ["zu viele Namensräume"] = "too many namespaces",
        ["zu aufwendig zu lesen"] = "too costly to read",
        // stage 3: the comparison with the visible text
        ["Anhang"] = "attachment",
        ["ohne Wert"] = "no value",
        ["Code ohne Sichtform"] = "a code with no visible form",
        ["Grenze der Prüfung erreicht"] = "limit of the check reached",
        ["kein lesbarer sichtbarer Text"] = "no readable visible text",
        ["{0} von {1} Wörtern"] = "{0} of {1} words",
        ["nur in unsichtbarem oder verdecktem Text"] = "only in invisible or covered text",
        ["nur ohne den Zusammenhang seiner Zeile"] = "only away from its line",
        ["nur mit anderem Vorzeichen"] = "only with the other sign",
        ["gefunden"] = "found",
        ["nur gerundet gefunden"] = "found only rounded",
        ["teilweise gefunden"] = "partly found",
        ["nicht gefunden – bitte prüfen"] = "not found – please check",
        ["im PDF nicht prüfbar"] = "cannot be checked in the PDF",
        ["nicht geprüft"] = "not checked",
        ["abgebrochen – {0} von {1} Seiten geprüft"] = "cancelled – {0} of {1} pages checked",
        ["1 Tag"] = "1 day",
        ["{0} Tagen"] = "{0} days",
        ["Skonto {0} % bei Zahlung innerhalb von {1}"] = "Cash discount of {0} % for payment within {1}",
        ["Skonto {0} % auf {2} bei Zahlung innerhalb von {1}"] = "Cash discount of {0} % on {2} for payment within {1}",
        ["(gekürzt, {0} Zeichen)"] = "(shortened, {0} characters)",
        ["lateinisch"] = "Latin",
        ["griechisch"] = "Greek",
        ["kyrillisch"] = "Cyrillic",
        ["{0}: enthält unsichtbare Steuer- oder Formatzeichen ({1}); in der Darstellung sind sie sichtbar gemacht."] =
            "{0}: contains invisible control or format characters ({1}); the display makes them visible.",
        ["{0}: enthält Zeichen außerhalb von A–Z und 0–9, die in einer IBAN nicht vorkommen."] =
            "{0}: contains characters outside A–Z and 0–9 that an IBAN does not have.",
        ["{0}: Die Prüfziffer der IBAN stimmt nicht (ISO 13616) – bitte die Kontoverbindung prüfen."] =
            "{0}: the check digits of the IBAN do not fit (ISO 13616) – please check the bank details.",
        ["{0}: mischt in einem Wort Buchstaben verschiedener Schriften ({1}) – sie können gleich aussehen."] =
            "{0}: mixes letters of different scripts in one word ({1}) – they can look alike.",
        ["Weitere Angabe {0}"] = "Further value {0}",
        ["… und {0} weitere dieser Art."] = "… and {0} more of this kind.",
        ["{0} Zahlungsgruppen: Die Angaben zur Lastschrift (BG-19) stehen nur bei den ersten {1}."] =
            "{0} payment groups: the direct debit details (BG-19) are shown in the first {1} only.",
        ["ein Text ist zu lang"] = "a text is too long",
        ["ein Anhang ist zu groß"] = "an attachment is too large",
        ["abgebrochen"] = "cancelled",
        ["Die Rechnungs-XML wurde nicht gelesen: {0}."] = "The invoice XML was not read: {0}.",
        ["Die XML-Deklaration nennt die Kodierung „{0}“; gelesen wurde als UTF-8, wie die Rechnungsformate es verlangen."] = "The XML declaration names the encoding “{0}”; it was read as UTF-8, as the invoice formats require.",
        ["Verarbeitungsanweisungen"] = "processing instructions",
        ["Die XML verweist nach außen ({0}); keinem Verweis wurde gefolgt."] = "The XML refers outside ({0}); no reference was followed.",
        ["Das PDF ist signiert, aber die Rechnungs-XML gehört nicht zur signierten Fassung — sie kam danach hinzu oder wurde danach geändert."] = "The PDF is signed, but the invoice XML is not part of the signed version — it was added or changed afterwards.",
        ["Die Rechnungs-XML wurde in einer späteren Revision der Datei ersetzt (Revision {0} von {1}); eine frühere Fassung enthält eine andere."] = "The invoice XML was replaced in a later revision of the file (revision {0} of {1}); an earlier version contains another one.",
        ["Die Rechnungs-XML kam mit einer späteren Revision der Datei hinzu (Revision {0} von {1}); die Fassungen davor enthalten keine."] = "The invoice XML was added with a later revision of the file (revision {0} of {1}); the versions before contain none.",
    };

    /// <summary>Registers the table with the translation as soon as the module is loaded — before any of its texts is asked for.</summary>
#pragma warning disable CA2255 // a module initializer in a library: here exactly what is wanted — the module brings its texts without the host doing anything
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => Tr.AddEnglish(Texts);
}
