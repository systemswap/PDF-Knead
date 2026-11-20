using System.Runtime.CompilerServices;

namespace PdfKnead.EInvoice.Ui;

/// <summary>
/// The English texts of the e-invoice interface, keyed by the German text like English.cs — here, so that removing the module leaves no text
/// behind (KONZEPT.md, section 5.24, „Aufbau und Entfernen“); registered when the assembly is loaded. EInvoiceModuleTests checks that every
/// text of the module (code and XAML) has its translation and none is left over.
/// </summary>
public static class EInvoiceUiTexts
{
    /// <summary>The table (German → English).</summary>
    public static IReadOnlyDictionary<string, string> Table => Texts;

    private static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal)
    {
        ["E-Rechnung"] = "E-invoice",
        ["E-Rechnung prüfen"] = "Check the e-invoice",
        ["Die eingebettete E-Rechnung prüfen: Regeln, Einbettung, Abgleich mit dem sichtbaren Teil — eine Hilfe, keine Aussage über die Gültigkeit"] =
            "Check the embedded e-invoice: rules, embedding, comparison with the visible part — an aid, no statement on validity",
        ["Kein Dokument"] = "No document",
        ["Keine E-Rechnung"] = "No e-invoice",
        ["E-Rechnung enthalten ({0})"] = "E-invoice included ({0})",
        ["E-Rechnung enthalten, nicht lesbar"] = "E-invoice included, not readable",
        ["Die PDF ließ sich für die Suche nicht öffnen."] = "The PDF could not be opened for the search.",
        ["In dieser PDF ist keine E-Rechnung eingebettet."] = "No e-invoice is embedded in this PDF.",
        ["Keine Anhänge."] = "No attachments.",
        ["Regelstand"] = "Rules as of",
        ["Auffälligkeiten: {0}"] = "Findings: {0}",
        ["1 Fehler"] = "1 error",
        ["{0} Fehler"] = "{0} errors",
        ["1 Warnung"] = "1 warning",
        ["{0} Warnungen"] = "{0} warnings",
        ["1 Hinweis"] = "1 note",
        ["{0} Hinweise"] = "{0} notes",
        ["… und 1 weitere Zeile"] = "… and 1 more line",
        ["Keine Auffälligkeit gefunden."] = "No finding.",
        ["E-Rechnung im Sinne des UStG: {0}"] = "E-invoice in the sense of the UStG: {0}",
        ["nein ({0})"] = "no ({0})",
        ["Eine Hilfe beim Prüfen — keine Aussage über die steuerliche oder rechtliche Gültigkeit. Maßgeblich ist die Rechnung selbst."] =
            "An aid when checking — no statement on tax or legal validity. The invoice itself is what counts.",
        ["Die Prüfung ist fehlgeschlagen: {0}"] = "The check failed: {0}",
        ["Befunde beim Lesen:"] = "Noticed while reading:",
        ["keins"] = "none",
        ["S. {0}"] = "p. {0}",
        ["Abgleich mit der PDF"] = "Comparison with the PDF",
        ["Abgleich mit der PDF ({0})"] = "Comparison with the PDF ({0})",
        ["Regeln ({0})"] = "Rules ({0})",
        ["… und {0} weitere Zeilen"] = "… and {0} more lines",
        ["Fundort"] = "Found in",
        ["Anmerkung auf Seite {0}"] = "annotation on page {0}",
        ["keine Rechnungsangaben"] = "no invoice data",
        ["nicht erklärt"] = "not declared",
        ["und {0} weitere"] = "and {0} more",
        ["Fehler"] = "Error",
        ["Warnung"] = "Warning",
        ["Rechnung"] = "Invoice",
        ["MIME-Typ"] = "MIME type",
        ["Die XML ließ sich nicht lesen: {0}"] = "The XML could not be read: {0}",
        ["Die Rechnungs-XML ist gespeichert: {0}"] = "The invoice XML is saved: {0}",
        ["Die XML ließ sich nicht speichern: {0}"] = "The XML could not be saved: {0}",
        ["Diese PDF enthält eine E-Rechnung. Maßgeblich ist die XML; Änderungen an den Seiten ändern sie nicht."] =
            "This PDF holds an e-invoice. The XML is what counts; changes to the pages do not change it.",
        ["Diese PDF enthält eine E-Rechnung. Erhalten bleibt sie nur als PDF/A-3 oder PDF/A-4f — bei den anderen Fassungen fällt sie weg."] =
            "This PDF holds an e-invoice. It is kept only as PDF/A-3 or PDF/A-4f — the other parts drop it.",
        ["Diese PDF enthält eine E-Rechnung (XML). Ihre Angaben lassen sich nicht schwärzen und stehen dort oft in anderer Schreibweise — „Gleich mit entfernen“ nimmt die E-Rechnung samt ihren Angaben in den Metadaten heraus."] =
            "This PDF holds an e-invoice (XML). Its data cannot be redacted and are often written otherwise there — „Remove it as well“ takes the e-invoice out together with its data in the metadata.",
        ["Ohne Entfernen bleibt die E-Rechnung mit allen Angaben in der Datei — auch mit dem, was Sie schwärzen."] =
            "Without removing, the e-invoice stays in the file with all its data — including what you redact.",
        ["Bleibt, solange nicht angehakt. Entfernt ist die PDF keine E-Rechnung mehr — die Rechnungs-XML und ihre Angaben in den Metadaten gehen."] =
            "Stays unless ticked. Removed, the PDF is no e-invoice any more — the invoice XML and its data in the metadata go.",
        ["E-Rechnung ({0})"] = "E-invoice ({0})",
        ["Diese PDF enthält eine E-Rechnung. Als Bild gesichert fällt sie weg — wie alle Anhänge und Metadaten."] =
            "This PDF holds an e-invoice. Saved as images, it goes — like all attachments and metadata.",
        ["S. {0} ({1} Stellen)"] = "p. {0} ({1} places)",
        ["„gefunden“ heißt: Der Wert steht sichtbar in der PDF. Welche Stelle zu welchem Feld gehört, prüft PDF-Knead nicht — bei mehreren Stellen springt jeder Klick zur nächsten."] =
            "„found“ means: the value stands visibly in the PDF. Which place belongs to which field PDF-Knead does not check — with several places each click goes to the next.",
        ["Für den Einkauf"] = "For purchasing",
        ["Zahlung"] = "Payment",
        ["Zahlungsempfänger"] = "Payee",
        ["Prüfziffer stimmt"] = "check digits fit",
        ["Prüfziffer stimmt nicht"] = "check digits do not fit",
        ["Die Prüfziffer der IBAN {0} stimmt nicht — so ist die Bankverbindung nicht gültig."] = "The check digits of the IBAN {0} do not fit — the account is not valid like this.",
        ["Die IBAN {0} der E-Rechnung steht nicht sichtbar in der PDF. Prüfen Sie die Bankverbindung vor der Zahlung über einen Weg, den Sie kennen — nicht über Angaben aus dieser Rechnung."] =
            "The IBAN {0} of the e-invoice does not stand visibly in the PDF. Check the account before paying through a channel you know — not through details from this invoice.",
        ["Das Konto {0} der E-Rechnung steht nicht sichtbar in der PDF. Prüfen Sie die Bankverbindung vor der Zahlung über einen Weg, den Sie kennen — nicht über Angaben aus dieser Rechnung."] =
            "The account {0} of the e-invoice does not stand visibly in the PDF. Check the account before paying through a channel you know — not through details from this invoice.",
        ["Netto"] = "Net",
        ["Umsatzsteuer"] = "VAT",
        ["Brutto"] = "Gross",
        ["Bereits gezahlt"] = "Paid already",
        ["Zu zahlen"] = "Amount due",
        ["Der zu zahlende Betrag {0} der E-Rechnung steht nicht sichtbar in der PDF."] = "The amount due {0} of the e-invoice does not stand visibly in the PDF.",
        ["Fällig"] = "Due",
        ["Zahlungsbedingungen"] = "Payment terms",
        ["Lastschrift"] = "Direct debit",
        ["wird abgebucht (Mandat {0})"] = "will be debited (mandate {0})",
        ["wird abgebucht"] = "will be debited",
        ["Abgebucht von"] = "Debited from",
        ["Der Zahlungsempfänger ist nicht der Rechnungssteller ({0})."] = "The payee is not the seller ({0}).",
        ["Ob Bankverbindung und Betrag sichtbar in der PDF stehen, ließ sich nicht prüfen — die PDF hat keinen lesbaren Text."] =
            "Whether account and amount stand visibly in the PDF could not be checked — the PDF has no readable text.",
        ["in der E-Rechnung nicht vorhanden"] = "not in the e-invoice",
        ["Die E-Rechnung nennt keine Bankverbindung. Eine IBAN, die nur im sichtbaren Teil steht, gehört nicht zur E-Rechnung — prüfen Sie sie vor der Zahlung über einen Weg, den Sie kennen."] =
            "The e-invoice names no account. An IBAN that stands only in the visible part is no part of the e-invoice — check it before paying through a channel you know.",
        ["Konto"] = "Account",
        ["im sichtbaren Teil prüfen"] = "check in the visible part",
        ["Dieses Profil ist keine E-Rechnung im Sinne des UStG — die Bankverbindung prüfen Sie im sichtbaren Teil."] =
            "This profile is no e-invoice in the sense of the UStG — check the account in the visible part.",
        ["Markiert alle Stellen in der PDF — jeder weitere Klick springt zur nächsten."] = "Marks all places in the PDF — each further click goes to the next.",
        ["keine IBAN"] = "no IBAN",
        ["vorhanden — ob fortlaufend, lässt sich an einer Rechnung nicht sehen"] = "present — whether it runs on, one invoice cannot show",
        ["vorhanden — vereinbarte Minderungen wie Skonto stehen, falls vereinbart, in den Zahlungsbedingungen"] =
            "present — agreed reductions such as a cash discount stand in the payment terms, if agreed",
        ["vorhanden"] = "present",
        ["nicht prüfbar"] = "not checkable",
        ["ein Datum steht nur im Text (Bemerkung oder Beschreibung einer Position) — ob es das Leistungsdatum ist, prüfen Sie selbst"] =
            "a date stands only in the text (a note or the description of a line) — whether it is the date of supply, check yourself",
        ["Gutschrift (Rechnungsart 389) — das Wort „Gutschrift“ selbst steht nicht in der XML; ob der Code allein genügt, ist nicht geklärt"] =
            "self-billed invoice (type 389) — the word „Gutschrift“ itself does not stand in the XML; whether the code alone suffices is not settled",
        ["Dieses Profil ist keine E-Rechnung im Sinne des UStG — maßgeblich ist der sichtbare Teil; die Pflichtangaben prüfen Sie dort."] =
            "This profile is no e-invoice in the sense of the UStG — the visible part counts; check the mandatory details there.",
        ["Pflichtangaben (§ 14 UStG): im sichtbaren Teil prüfen"] = "Mandatory details (§ 14 UStG): check in the visible part",
        ["Liefer- oder Leistungsdatum bei {0} von {1} Positionen"] = "date of supply or service on {0} of {1} lines",
        ["nicht nötig"] = "not needed",
        ["Pflichtangaben (§ 14 UStG): alle vorhanden"] = "Mandatory details (§ 14 UStG): all present",
        ["Pflichtangaben (§ 14 UStG): nichts fehlt, {0}"] = "Mandatory details (§ 14 UStG): nothing missing, {0}",
        ["1 Angabe selbst prüfen"] = "check 1 detail yourself",
        ["{0} Angaben selbst prüfen"] = "check {0} details yourself",
        ["Pflichtangaben (§ 14 UStG): 1 fehlt"] = "Mandatory details (§ 14 UStG): 1 missing",
        ["Pflichtangaben (§ 14 UStG): {0} fehlen"] = "Mandatory details (§ 14 UStG): {0} missing",
        ["Kleinbetragsrechnung bis 250 €: es genügen weniger Angaben (§ 33 UStDV)."] = "Invoice of small amount up to 250 €: fewer details suffice (§ 33 UStDV).",
        ["Name und Anschrift des Rechnungsstellers"] = "Name and address of the seller",
        ["Name und Anschrift des Leistungsempfängers"] = "Name and address of the buyer",
        ["Steuernummer oder USt-IdNr. des Rechnungsstellers"] = "Tax number or VAT ID of the seller",
        ["Ausstellungsdatum"] = "Date of issue",
        ["Rechnungsnummer"] = "Invoice number",
        ["Menge und Art der Lieferung oder Leistung"] = "Quantity and kind of the supply or service",
        ["Zeitpunkt der Lieferung oder Leistung"] = "Time of the supply or service",
        ["Entgelt nach Steuersätzen aufgeschlüsselt"] = "Amount broken down by tax rate",
        ["Steuersatz und Steuerbetrag oder Hinweis auf die Steuerbefreiung"] = "Tax rate and tax amount, or a note of the exemption",
        ["Hinweis auf die Aufbewahrungspflicht (nur bei Leistungen an Privatpersonen im Zusammenhang mit einem Grundstück)"] =
            "Note on the duty to keep the invoice (only for work on land for private persons)",
        ["Angabe „Gutschrift“ (nur bei Gutschriften)"] = "The word „Gutschrift“ (only on self-billed invoices)",
        ["Hinweis „Steuerschuldnerschaft des Leistungsempfängers“"] = "The note „Steuerschuldnerschaft des Leistungsempfängers“ (reverse charge)",
        ["USt-IdNr. beider Seiten (innergemeinschaftliche Lieferung)"] = "VAT IDs of both (intra-community supply)",
        ["Steuernummer oder USt-IdNr."] = "tax number or VAT ID",
        ["Positionen"] = "lines",
        ["Artikelname je Position"] = "item name on every line",
        ["Menge je Position"] = "quantity on every line",
        ["Liefer- oder Leistungsdatum"] = "date of supply or service",
        ["Aufschlüsselung je Steuersatz"] = "breakdown by tax rate",
        ["Steuersatz und Steuerbetrag bzw. Befreiungsgrund"] = "tax rate and amount, or the exemption reason",
        ["das Wort „Gutschrift“"] = "the word „Gutschrift“",
        ["Hinweis in der Steuerangabe"] = "note in the tax details",
        ["USt-IdNr. des Rechnungsstellers"] = "VAT ID of the seller",
        ["USt-IdNr. des Leistungsempfängers"] = "VAT ID of the buyer",
        ["Geprüft wird, ob die Angaben in der XML stehen — nicht, ob sie stimmen."] = "What is checked is whether the details stand in the XML — not whether they are right.",
        ["Rechnungsdatum"] = "Invoice date",
        ["Verkäufer"] = "Seller",
        ["Käufer"] = "Buyer",
        ["USt-IdNr."] = "VAT ID",
        ["Steuernummer"] = "Tax number",
        ["Nicht sichtbar in der PDF: {0}"] = "Not visible in the PDF: {0}",
        ["Was heißt das für Sie? Maßgeblich ist die E-Rechnung (XML): Bei Abweichungen gehen ihre Daten dem sichtbaren Teil vor (UStAE 14.4). Passt die sichtbare Seite nicht zu ihr, klären Sie mit dem Absender, welche Rechnung gilt — ein abweichender sichtbarer Teil kann als weitere Rechnung gelten (UStAE 14c.1 Abs. 4a)."] =
            "What does this mean for you? The e-invoice (XML) counts: where they differ, its data prevail over the visible part (UStAE 14.4). If the visible page does not fit it, clarify with the sender which invoice applies — a differing visible part may count as a further invoice (UStAE 14c.1 Abs. 4a).",
        ["Was heißt das für Sie?"] = "What does this mean for you?",
        ["1 Auffälligkeit betrifft"] = "1 finding concerns",
        ["{0} Auffälligkeiten betreffen"] = "{0} findings concern",
        ["1 Formatfehler"] = "1 format error",
        ["{0} Formatfehler"] = "{0} format errors",
        ["{0}: Die Datei entspricht nicht dem Format einer E-Rechnung — sie gilt dann als sonstige Rechnung, nicht als E-Rechnung (BMF-Schreiben vom 15.10.2025, Rn. 6a). Fordern Sie beim Absender eine korrigierte Rechnung an."] =
            "{0}: the file does not follow the format of an e-invoice — it then counts as an other invoice, not as an e-invoice (BMF letter of 15.10.2025, Rn. 6a). Ask the sender for a corrected invoice.",
        ["{0} eine Pflichtangabe nach § 14 Abs. 4 oder § 14a UStG — ein solcher Fehler macht die Rechnung nicht ordnungsgemäß und kann den Vorsteuerabzug gefährden (Rn. 35a). Fordern Sie eine berichtigte Rechnung an."] =
            "{0} a mandatory detail of § 14 Abs. 4 or § 14a UStG — such an error makes the invoice improper and may put the input tax deduction at risk (Rn. 35a). Ask for a corrected invoice.",
        ["{0} andere Geschäftsregeln — umsatzsteuerlich unbeachtlich, solange keine Pflichtangabe betroffen ist (Rn. 35a); meist ein Fehler im Programm des Absenders."] =
            "{0} other business rules — of no account for VAT as long as no mandatory detail is concerned (Rn. 35a); mostly an error in the sender's program.",
        ["{0} technische Angaben (PDF-Hülle, Metadaten, Profil) — solange sich die Rechnungsdaten sicher entnehmen lassen, für den Vorsteuerabzug meist ohne Bedeutung."] =
            "{0} technical details (the PDF around it, metadata, profile) — as long as the invoice data can be taken out safely, mostly of no account for the input tax deduction.",
        ["Dieses Profil ist keine E-Rechnung im Sinne des UStG — maßgeblich ist der sichtbare Teil; die Befunde betreffen nur die beigefügten Rechnungsdaten."] =
            "This profile is no e-invoice in the sense of the UStG — the visible part counts; the findings concern only the invoice data attached.",
        ["{0}: Die beigefügten Rechnungsdaten entsprechen nicht ihrem Schema."] = "{0}: the invoice data attached do not follow their schema.",
        ["{0} eine Pflichtangabe in den beigefügten Daten — prüfen Sie sie im sichtbaren Teil."] = "{0} a mandatory detail in the data attached — check it in the visible part.",
        ["Formatfehler"] = "format error",
        ["Pflichtangabe"] = "mandatory detail",
        ["Geschäftsregel"] = "business rule",
        ["technisch"] = "technical",
        ["Anhänge"] = "Attachments",
        ["Anhänge ({0})"] = "Attachments ({0})",
        ["Eigene Datei: {0}"] = "File of its own: {0}",
        ["Die Prüfung wurde abgebrochen — von Hand oder nach {0} Sekunden."] = "The check was cancelled — by hand or after {0} seconds.",
        ["Die Datei ist zu groß ({0} MB)."] = "The file is too large ({0} MB).",
        ["nicht lesbar oder zu groß"] = "not readable or too large",
        ["Der Anhang ließ sich nicht lesen: {0}"] = "The attachment could not be read: {0}",
        ["Der Anhang ist gespeichert: {0}"] = "The attachment is saved: {0}",
        ["Der Anhang ließ sich nicht speichern: {0}"] = "The attachment could not be saved: {0}",
        ["Die Rechnungs-XML ist {0:0.0} MB groß — Peppol und die Rechnungseingangsplattformen nehmen höchstens 15 MB samt Anhängen an."] =
            "The invoice XML is {0:0.0} MB — Peppol and the invoice receipt platforms accept at most 15 MB including attachments.",
        ["Diese Datei ist keine E-Rechnung, die PDF-Knead kennt (CII, UBL, ZUGFeRD 1)."] = "This file is no e-invoice PDF-Knead knows (CII, UBL, ZUGFeRD 1).",
        ["XRechnung als eigene Datei prüfen …"] = "Check an XRechnung file of its own …",
        ["oder eine XML-Datei auf dieses Feld ziehen"] = "or drag an XML file onto this area",
        ["XML speichern …"] = "Save XML …",
        ["Eine Kopie der eingebetteten Rechnungs-XML speichern, Byte für Byte wie eingebettet — die PDF bleibt unverändert"] =
            "Save a copy of the embedded invoice XML, byte for byte as embedded — the PDF stays unchanged",
        ["Die übrigen Anhänge der PDF zeigen und einzeln speichern — geöffnet wird keiner"] =
            "Show the other attachments of the PDF and save them one by one — none is opened",
    };

    /// <summary>Registers the English texts when the module is loaded.</summary>
#pragma warning disable CA2255 // a module initializer in a library: here exactly what is wanted — the module brings its texts without the host doing anything
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Register() => Tr.AddEnglish(Texts);
}
