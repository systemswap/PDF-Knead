using System.Text.RegularExpressions;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.EInvoice;

/// <summary>
/// The business rules for hybrid documents (stage 4, part h; KONZEPT.md, section 5.24, „Plan Stufe 4, Teil h“): how the invoice XML lies in
/// its PDF — PDF/A, the XMP invoice data with their extension schema, the embedded file (name, relationship, type) — and the profile against
/// the countries of seller and buyer. From the specification ZUGFeRD 2.5.2 / Factur-X 1.09.2 (table „Business Rules HYBRID“ and the hybrid
/// code lists) in code of our own, the descriptions in our words; only for an invoice found in a PDF. A PDF with a UBL invoice and no invoice
/// data in its XMP is no hybrid document: it is not measured against these rules. ZUGFeRD 1 has a specification of its own: only PDF/A and
/// the embedding are checked. Where the specification contradicts itself, the decision is in docs/ENTSCHEIDUNGEN.md („E-Rechnung, Teil h“).
/// </summary>
public static class InvoiceHybridRules
{
    /// <summary>The source and version of these rules.</summary>
    public const string Source = "ZUGFeRD 2.5.2 / Factur-X 1.09.2";

    private const InvoiceRuleSeverity F = InvoiceRuleSeverity.Fatal, W = InvoiceRuleSeverity.Warning, I = InvoiceRuleSeverity.Information;

    // the code lists of the specification (sheet „Codelists for Hybrid“)
    internal static readonly string[] DocumentTypes = ["INVOICE", "ORDER", "ORDER_RESPONSE", "ORDER_CHANGE"];
    internal static readonly string[] ConformanceLevels = ["MINIMUM", "BASIC WL", "BASIC", "COMFORT", "EN 16931", "EXTENDED", "XRECHNUNG"];
    internal static readonly string[] FileNames = ["factur-x.xml", "xrechnung.xml", "order-x.xml"];
    internal static readonly string[] Versions = ["1.0", "1p0", "2p0", "2p1", "2p2"];

    // ZUGFeRD 2.0, still supported (6.3.2): its file name, and no level XRECHNUNG or COMFORT
    private const string Zugferd20FileName = "zugferd-invoice.xml";

    // the properties an extension schema of the invoice data describes, the values of /AFRelationship (ISO 32000-2, 14.13)
    private static readonly string[] PropertyNames = ["DocumentType", "DocumentFileName", "Version", "ConformanceLevel"];

    // an XRechnung version as chapter 8 asks for it with the level XRECHNUNG („2p1“, „3.0“)
    private static readonly Regex XRechnungVersion = new(@"^\d{1,2}(\.|p)\d{1,2}(\.\d{1,2})?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>BR-HYBRID-02: PDF/A-3, or PDF/A-4f.</summary>
    public static readonly InvoiceRule PdfA = new("BR-HYBRID-02", Source, F,
        "Der PDF-Umschlag ist nicht als PDF/A-3 (oder PDF/A-4f) erklärt, oder die PDF/A-Prüfung findet Abweichungen.",
        "The PDF envelope is not declared PDF/A-3 (or PDF/A-4f), or the PDF/A check finds deviations.");

    /// <summary>BR-HYBRID-03: the XMP describes the invoice data in a PDF/A extension schema.</summary>
    public static readonly InvoiceRule Extension = new("BR-HYBRID-03", Source, F,
        "Die Metadaten (XMP) beschreiben die Rechnungsangaben nicht vollständig in einem PDF/A-Erweiterungsschema.",
        "The metadata (XMP) do not fully describe the invoice data in a PDF/A extension schema.");

    /// <summary>BR-HYBRID-04: the namespace of the extension schema.</summary>
    public static readonly InvoiceRule ExtensionUri = new("BR-HYBRID-04", Source, F,
        "Das Erweiterungsschema der Rechnungsangaben nennt einen anderen Namensraum, als die Spezifikation festlegt.",
        "The extension schema of the invoice data names another namespace than the specification lays down.");

    /// <summary>BR-HYBRID-05: the prefix of the extension schema.</summary>
    public static readonly InvoiceRule ExtensionPrefix = new("BR-HYBRID-05", Source, F,
        "Das Erweiterungsschema der Rechnungsangaben nennt ein anderes Präfix als fx.",
        "The extension schema of the invoice data names another prefix than fx.");

    /// <summary>BR-HYBRID-06: fx:DocumentType from the code list, fitting the kind of the XML.</summary>
    public static readonly InvoiceRule DocumentType = new("BR-HYBRID-06", Source, F,
        "fx:DocumentType fehlt, steht nicht in der Codeliste oder passt nicht zur Art der XML (Rechnung oder Bestellung).",
        "fx:DocumentType is missing, not in the code list or does not fit the kind of the XML (invoice or order).");

    /// <summary>BR-HYBRID-07: fx:ConformanceLevel from the code list.</summary>
    public static readonly InvoiceRule Level = new("BR-HYBRID-07", Source, F,
        "fx:ConformanceLevel fehlt oder steht nicht in der Codeliste (Schreibweise mit Leerzeichen: „EN 16931“, „BASIC WL“).",
        "fx:ConformanceLevel is missing or not in the code list (written with a space: “EN 16931”, “BASIC WL”).");

    /// <summary>BR-HYBRID-08: fx:DocumentFileName from the code list, with its rule of use.</summary>
    public static readonly InvoiceRule XmpFileName = new("BR-HYBRID-08", Source, F,
        "fx:DocumentFileName fehlt, steht nicht in der Codeliste oder passt nicht zum Profil (factur-x.xml nie mit XRECHNUNG, xrechnung.xml nur damit).",
        "fx:DocumentFileName is missing, not in the code list or does not fit the level (factur-x.xml never with XRECHNUNG, xrechnung.xml only with it).");

    /// <summary>BR-HYBRID-09: fx:Version from the code list.</summary>
    public static readonly InvoiceRule Version = new("BR-HYBRID-09", Source, F,
        "fx:Version fehlt oder steht nicht in der Codeliste.",
        "fx:Version is missing or not in the code list.");

    /// <summary>BR-HYBRID-10: fx:Version 1.0.</summary>
    public static readonly InvoiceRule VersionOne = new("BR-HYBRID-10", Source, W,
        "fx:Version ist ein Wert älterer Fassungen, nicht 1.0.",
        "fx:Version is a value of earlier versions, not 1.0.");

    /// <summary>BR-HYBRID-11: /AFRelationship after the table of the specification.</summary>
    public static readonly InvoiceRule Relationship = new("BR-HYBRID-11", Source, W,
        "/AFRelationship der Rechnungs-XML folgt nicht der Tabelle der Spezifikation (MINIMUM und BASIC WL: Data, sonst Alternative; in Frankreich auch Source oder Data).",
        "/AFRelationship of the invoice XML does not follow the table of the specification (MINIMUM and BASIC WL: Data, else Alternative; in France also Source or Data).");

    /// <summary>BR-HYBRID-12: embedded as the specification asks.</summary>
    public static readonly InvoiceRule Embedding = new("BR-HYBRID-12", Source, F,
        "Die Rechnungs-XML ist nicht eingebettet, wie die Spezifikation es verlangt: als Datei mit MIME-Typ, im Namensbaum /EmbeddedFiles und im /AF des Katalogs.",
        "The invoice XML is not embedded as the specification asks: as a file with a MIME type, in the name tree /EmbeddedFiles and in the /AF of the catalog.");

    /// <summary>BR-HYBRID-13: the name of the embedded file from the code list, with its rule of use.</summary>
    public static readonly InvoiceRule FileName = new("BR-HYBRID-13", Source, F,
        "Der Name der eingebetteten Datei steht nicht in der Codeliste oder passt nicht zum Profil der XML (factur-x.xml, bei XRechnung xrechnung.xml).",
        "The name of the embedded file is not in the code list or does not fit the profile of the XML (factur-x.xml, for XRechnung xrechnung.xml).");

    /// <summary>BR-HYBRID-14: the name of the embedded file is the one fx:DocumentFileName names.</summary>
    public static readonly InvoiceRule FileNameMatch = new("BR-HYBRID-14", Source, W,
        "Die eingebettete Datei heißt anders, als fx:DocumentFileName sie nennt.",
        "The embedded file has another name than fx:DocumentFileName gives.");

    /// <summary>BR-HYBRID-15: fx:ConformanceLevel matches the profile of the XML.</summary>
    public static readonly InvoiceRule LevelMatch = new("BR-HYBRID-15", Source, W,
        "fx:ConformanceLevel nennt ein anderes Profil, als die XML erklärt.",
        "fx:ConformanceLevel names another profile than the XML declares.");

    /// <summary>BR-HYBRID-DE-01: no MINIMUM between seller and buyer in Germany.</summary>
    public static readonly InvoiceRule NoMinimumDe = new("BR-HYBRID-DE-01", Source, F,
        "Verkäufer und Käufer sitzen in Deutschland: das Profil MINIMUM ist dann nicht erlaubt.",
        "Seller and buyer are in Germany: the profile MINIMUM is not allowed then.");

    /// <summary>BR-HYBRID-DE-02: no BASIC WL between seller and buyer in Germany.</summary>
    public static readonly InvoiceRule NoBasicWlDe = new("BR-HYBRID-DE-02", Source, F,
        "Verkäufer und Käufer sitzen in Deutschland: das Profil BASIC WL ist dann nicht erlaubt.",
        "Seller and buyer are in Germany: the profile BASIC WL is not allowed then.");

    /// <summary>BR-HYBRID-FR-01: no XRECHNUNG between seller and buyer in France.</summary>
    public static readonly InvoiceRule NoXRechnungFr = new("BR-HYBRID-FR-01", Source, F,
        "Verkäufer und Käufer sitzen in Frankreich: das Profil XRECHNUNG ist dann nicht erlaubt.",
        "Seller and buyer are in France: the profile XRECHNUNG is not allowed then.");

    /// <summary>BR-FX-DE-04: the date of the supply between seller and buyer in Germany.</summary>
    public static readonly InvoiceRule DeliveryDateDe = new("BR-FX-DE-04", Source, W,
        "Verkäufer und Käufer sitzen in Deutschland, keine Anzahlungsrechnung: es fehlt das Liefer-/Leistungsdatum (BT-72, BG-14 oder an jeder Position BG-26) — oder ohne BT-72 das Lieferland (BT-80).",
        "Seller and buyer are in Germany, no down payment invoice: the date of supply is missing (BT-72, BG-14 or BG-26 at every line) — or without BT-72 the country of delivery (BT-80).");

    /// <summary>PDFK-PDF-01: the MIME type is not text/xml.</summary>
    public static readonly InvoiceRule MimeType = new("PDFK-PDF-01", InvoiceRules.HintSource, I,
        "Der MIME-Typ der Rechnungs-XML ist nicht text/xml, wie die Spezifikation ihn für Factur-X nennt.",
        "The MIME type of the invoice XML is not text/xml, which the specification gives for Factur-X.");

    /// <summary>PDFK-PDF-02: no /Params /ModDate.</summary>
    public static readonly InvoiceRule ModDate = new("PDFK-PDF-02", InvoiceRules.HintSource, I,
        "Die eingebettete Rechnungs-XML nennt kein Änderungsdatum (/Params /ModDate), wie die Spezifikation es empfiehlt.",
        "The embedded invoice XML gives no modification date (/Params /ModDate), as the specification recommends.");

    /// <summary>PDFK-PDF-03: ZUGFeRD 1, only PDF/A and the embedding checked.</summary>
    public static readonly InvoiceRule ZugferdV1Note = new("PDFK-PDF-03", InvoiceRules.HintSource, I,
        "Rechnung nach ZUGFeRD 1: für sie gilt eine eigene Spezifikation; geprüft sind nur PDF/A und die Einbettung.",
        "Invoice after ZUGFeRD 1: a specification of its own applies; only PDF/A and the embedding are checked.");

    /// <summary>PDFK-PDF-04: the XMP describe a CII invoice, the XML is UBL.</summary>
    public static readonly InvoiceRule UblInHybrid = new("PDFK-PDF-04", InvoiceRules.HintSource, W,
        "Die Metadaten (XMP) beschreiben eine Rechnung nach ZUGFeRD oder Factur-X (CII), die eingebettete XML ist UBL.",
        "The metadata (XMP) describe an invoice after ZUGFeRD or Factur-X (CII), the embedded XML is UBL.");

    // seller and buyer in Germany: the XML is the invoice, deviations from PDF/A are warnings only (BR-FX-DE-03, decided in ENTSCHEIDUNGEN)
    private static readonly InvoiceRule PdfAGermany = PdfA with
    {
        Severity = W,
        German = PdfA.German + " Verkäufer und Käufer in Deutschland: nur eine Warnung, die XML ist die Rechnung (BR-FX-DE-03).",
        English = PdfA.English + " Seller and buyer in Germany: a warning only, the XML is the invoice (BR-FX-DE-03).",
    };

    private static readonly InvoiceRule ExtensionGermany = Extension with
    {
        Severity = W,
        German = Extension.German + " Verkäufer und Käufer in Deutschland: nur eine Warnung (BR-FX-DE-03).",
        English = Extension.English + " Seller and buyer in Germany: a warning only (BR-FX-DE-03).",
    };

    /// <summary>Every rule of this part, as listed in the specification (the downgraded forms of BR-FX-DE-03 not counted).</summary>
    public static IReadOnlyList<InvoiceRule> All { get; } =
    [
        PdfA, Extension, ExtensionUri, ExtensionPrefix, DocumentType, Level, XmpFileName, Version, VersionOne, Relationship, Embedding, FileName,
        FileNameMatch, LevelMatch, NoMinimumDe, NoBasicWlDe, NoXRechnungFr, DeliveryDateDe, MimeType, ModDate, ZugferdV1Note, UblInHybrid,
    ];

    /// <summary>The places named with a violation (in the PDF, not in the XML).</summary>
    private static class Place
    {
        public const string PdfAId = "XMP pdfaid", PdfAResult = "PDF/A", Schemas = "XMP pdfaExtension:schemas", SchemaUri = "XMP pdfaSchema:namespaceURI",
            SchemaPrefix = "XMP pdfaSchema:prefix", SchemaProperties = "XMP pdfaSchema:property", Type = "XMP DocumentType", Level = "XMP ConformanceLevel",
            Name = "XMP DocumentFileName", Version = "XMP Version", Relationship = "PDF /AFRelationship", Embedded = "PDF /EmbeddedFiles, /AF, /Subtype",
            F = "PDF /F", Uf = "PDF /UF", Subtype = "PDF /Subtype", Params = "PDF /Params", Countries = "BT-40, BT-55", Supply = "BT-72, BG-14, BG-26, BT-80";
    }

    /// <summary>The violated rules of the invoice of a PDF; empty for a loose XML, an invoice that was refused, or a PDF that is no hybrid document.</summary>
    /// <param name="doc">The PDF.</param>
    /// <param name="location">Where its invoice was found (<see cref="InvoiceLocator.Locate"/>).</param>
    /// <param name="invoice">The invoice in readable form.</param>
    /// <param name="cancel">Cancels the check (the PDF/A check takes its time on a large file).</param>
    public static IReadOnlyList<InvoiceRuleViolation> Check(PdfDocument doc, InvoiceLocation location, InvoiceData invoice, CancellationToken cancel = default)
    {
        var found = new List<InvoiceRuleViolation>();
        if (location.File is not { } file || location.Kind is not { } kind) return found;
        void Add(InvoiceRule rule, string? value, string place, InvoiceField? field = null) =>
            found.Add(new InvoiceRuleViolation(rule, invoice.Root, field, value, Place: place));

        var xmp = location.Xmp is { Namespace.Length: > 0 } data ? data : null;
        var v1 = kind.Syntax == InvoiceSyntax.ZugferdV1 || xmp?.Namespace == InvoiceSources.ZugferdV1Namespace;
        var ubl = kind.Syntax is InvoiceSyntax.UblInvoice or InvoiceSyntax.UblCreditNote;
        // a hybrid document: a CII, ZUGFeRD 1 or Order-X XML, or invoice data in the XMP; a UBL invoice merely attached to a PDF is none
        if (!v1 && xmp is null && kind.Syntax is not (InvoiceSyntax.Cii or InvoiceSyntax.OrderX)) return found;

        var seller = Country(invoice, "BT-40");
        var buyer = Country(invoice, "BT-55");
        var germany = seller == "DE" && buyer == "DE";

        CheckPdfA(doc, germany ? PdfAGermany : PdfA, Add, cancel);
        CheckEmbedding(file, Add);
        if (v1)
        {
            Add(ZugferdV1Note, null, Place.Schemas);
            return found;
        }
        if (ubl)
        {
            Add(UblInHybrid, xmp?.Namespace, Place.Schemas);
            return found;
        }
        if (file.MimeType is { } mime && mime.Contains('/') && mime != "text/xml") Add(MimeType, mime, Place.Subtype);
        if (!file.HasModDate) Add(ModDate, null, Place.Params);

        var legacy = xmp?.Namespace is InvoiceSources.Zugferd20Namespace or InvoiceSources.Zugferd20NamespaceWritten;
        var order = kind.Syntax == InvoiceSyntax.OrderX || kind.Guideline?.Contains(":order:", StringComparison.OrdinalIgnoreCase) == true;
        CheckExtension(location.Xmp?.Extensions ?? [], xmp?.Namespace ?? InvoiceSources.FacturXNamespace, germany, Add);
        CheckXmpValues(xmp, kind, legacy, order, Add);
        CheckFile(file, xmp, kind, legacy, order, seller == "FR", Add);
        CheckCountries(invoice, kind, germany, seller == "FR" && buyer == "FR", Add);
        if (germany) CheckSupplyDate(invoice, Add);
        return found;
    }

    /// <summary>The country code of a term, trimmed and in capitals; null if missing.</summary>
    private static string? Country(InvoiceData invoice, string id) => invoice.Text(id)?.Trim().ToUpperInvariant();

    /// <summary>BR-HYBRID-02: the declared part (PDF/A-3 any level, or PDF/A-4f) and the PDF/A check of the core for that part.</summary>
    private static void CheckPdfA(PdfDocument doc, InvoiceRule rule, Action<InvoiceRule, string?, string, InvoiceField?> add, CancellationToken cancel)
    {
        var declared = PdfACheck.Declared(doc);
        if (declared is not (PdfAConformance.A3a or PdfAConformance.A3b or PdfAConformance.A3u or PdfAConformance.A4f))
        {
            add(rule, declared is null or PdfAConformance.None ? Tr.T("kein PDF/A erklärt") : Tr.T("erklärt: {0}", Name(declared.Value)), Place.PdfAId, null);
            return;
        }
        var deviations = PdfACheck.Check(doc, cancel);
        if (deviations.Count > 0)
            add(rule, Tr.T("{0}: {1} Abweichungen der PDF/A-Prüfung, etwa {2}", Name(declared.Value), deviations.Count,
                string.Join(", ", deviations.Select(d => d.Id).Distinct().Take(3))), Place.PdfAResult, null);
    }

    private static string Name(PdfAConformance part) => $"PDF/A-{part.Part()}{part.Level()?.ToLowerInvariant()}";

    /// <summary>BR-HYBRID-12: a file with a MIME type, in the name tree /EmbeddedFiles and associated in the catalog's /AF.</summary>
    private static void CheckEmbedding(InvoiceFile file, Action<InvoiceRule, string?, string, InvoiceField?> add)
    {
        var missing = new List<string>();
        if (!file.Places.Any(p => p.Place == InvoiceFilePlace.EmbeddedFiles)) missing.Add(Tr.T("Namensbaum /EmbeddedFiles"));
        if (!file.Places.Any(p => p.Place == InvoiceFilePlace.CatalogAf)) missing.Add(Tr.T("/AF des Katalogs"));
        if (file.MimeType is not { } mime || !mime.Contains('/')) missing.Add(Tr.T("MIME-Typ (/Subtype)"));
        if (missing.Count > 0) add(Embedding, Tr.T("fehlt: {0}", string.Join(", ", missing)), Place.Embedded, null);
    }

    /// <summary>BR-HYBRID-03, -04, -05: the extension schema of the invoice data — its namespace, prefix and the four properties.</summary>
    private static void CheckExtension(IReadOnlyList<InvoiceXmpSchema> schemas, string ns, bool germany, Action<InvoiceRule, string?, string, InvoiceField?> add)
    {
        var rule = germany ? ExtensionGermany : Extension;
        var schema = schemas.FirstOrDefault(s => s.NamespaceUri == ns);
        if (schema is null)
        {
            // a schema meant for the invoice data, written with another namespace (a letter, „#“ or „:invoice“ missing): BR-HYBRID-04
            var near = schemas.FirstOrDefault(s => s.Prefix is "fx" or "zf"
                || s.NamespaceUri?.Contains("CrossIndustryDocument", StringComparison.OrdinalIgnoreCase) == true);
            if (near is not null) add(ExtensionUri, near.NamespaceUri, Place.SchemaUri, null);
            else add(rule, null, Place.Schemas, null);
            return;
        }
        // ZUGFeRD 2.0: 2.5.2 names zf (6.3.2), the official samples of 2.0 write fx, generators both — either counts (ENTSCHEIDUNGEN);
        // otherwise fx
        var legacy = ns is InvoiceSources.Zugferd20Namespace or InvoiceSources.Zugferd20NamespaceWritten;
        if (!(schema.Prefix == "fx" || legacy && schema.Prefix == "zf")) add(ExtensionPrefix, schema.Prefix, Place.SchemaPrefix, null);
        var missing = PropertyNames.Where(p => !schema.Properties.Contains(p)).ToList();
        if (missing.Count > 0) add(rule, Tr.T("fehlt: {0}", string.Join(", ", missing)), Place.SchemaProperties, null);
    }

    /// <summary>BR-HYBRID-06 to -10 and -15: the four values of the XMP invoice data against the code lists and against the XML.</summary>
    private static void CheckXmpValues(InvoiceXmp? xmp, InvoiceKind kind, bool legacy, bool order, Action<InvoiceRule, string?, string, InvoiceField?> add)
    {
        var type = xmp?.DocumentType;
        if (type is null || !DocumentTypes.Contains(type) || (type == "INVOICE") == order) add(DocumentType, type, Place.Type, null);

        var level = xmp?.ConformanceLevel;
        var levelKnown = level is not null && ConformanceLevels.Contains(level)
            && (level != "COMFORT" || order) && (!legacy || level is not ("XRECHNUNG" or "COMFORT"));
        if (!levelKnown) add(Level, level, Place.Level, null);

        var name = xmp?.DocumentFileName;
        if (name != ExpectedName(order, legacy, level == "XRECHNUNG")) add(XmpFileName, name, Place.Name, null);

        // with the level XRECHNUNG chapter 8 asks for the version of the XRechnung, not of Factur-X (decided in ENTSCHEIDUNGEN)
        var version = xmp?.Version;
        if (!(level == "XRECHNUNG" && version is not null && XRechnungVersion.IsMatch(version)))
        {
            if (version is null || !Versions.Contains(version)) add(Version, version, Place.Version, null);
            else if (version != "1.0") add(VersionOne, version, Place.Version, null);
        }

        if (levelKnown && InvoiceLocator.LevelOf(level!, false) is { } declared && !InvoiceLocator.SameProfile(declared, kind.Profile))
            add(LevelMatch, Tr.T("XMP: {0}, XML: {1}", level!, InvoiceFormat.Plain(kind.Guideline ?? "–", 80)), Place.Level, null);
    }

    /// <summary>The file name a document of this kind is embedded under (code list „Filename“ with its rules of use; ZUGFeRD 2.0 after 6.3.2).</summary>
    private static string ExpectedName(bool order, bool legacy, bool xrechnung) =>
        order ? "order-x.xml" : legacy ? Zugferd20FileName : xrechnung ? "xrechnung.xml" : "factur-x.xml";

    /// <summary>BR-HYBRID-11, -13, -14: the relationship and the names of the embedded file.</summary>
    private static void CheckFile(InvoiceFile file, InvoiceXmp? xmp, InvoiceKind kind, bool legacy, bool order, bool sellerInFrance,
        Action<InvoiceRule, string?, string, InvoiceField?> add)
    {
        string[] allowed = kind.Profile switch
        {
            InvoiceProfile.Minimum or InvoiceProfile.BasicWl => ["Data"],
            InvoiceProfile.XRechnung => ["Alternative"],
            _ => sellerInFrance ? ["Alternative", "Source", "Data"] : ["Alternative"],
        };
        if (file.Relationship is not { } relationship || !allowed.Contains(relationship)) add(Relationship, file.Relationship, Place.Relationship, null);

        var expected = ExpectedName(order, legacy, kind.Profile == InvoiceProfile.XRechnung);
        if (file.Name is null && file.UnicodeName is null) add(FileName, null, Place.F, null);
        if (file.Name is { } f && f != expected) add(FileName, f, Place.F, null);
        if (file.UnicodeName is { } uf && uf != expected && uf != file.Name) add(FileName, uf, Place.Uf, null);

        if (xmp?.DocumentFileName is { } named && named != file.Name && named != file.UnicodeName)
            add(FileNameMatch, Tr.T("XMP: {0}, Datei: {1}", InvoiceFormat.Plain(named, 80), InvoiceFormat.Plain(file.DisplayName, 80)), Place.Name, null);
    }

    /// <summary>BR-HYBRID-DE-01, -DE-02, -FR-01: the profile against the countries of seller (BT-40) and buyer (BT-55).</summary>
    private static void CheckCountries(InvoiceData invoice, InvoiceKind kind, bool germany, bool france, Action<InvoiceRule, string?, string, InvoiceField?> add)
    {
        var field = invoice.Field("BT-40");
        if (germany && kind.Profile == InvoiceProfile.Minimum) add(NoMinimumDe, "MINIMUM", Place.Countries, field);
        if (germany && kind.Profile == InvoiceProfile.BasicWl) add(NoBasicWlDe, "BASIC WL", Place.Countries, field);
        if (france && kind.Profile == InvoiceProfile.XRechnung) add(NoXRechnungFr, "XRECHNUNG", Place.Countries, field);
    }

    /// <summary>BR-FX-DE-04: an invoice other than a down payment (386) gives the date of supply — BT-72, BG-14 or BG-26 at every line — and, without BT-72, the country of delivery (BT-80).</summary>
    private static void CheckSupplyDate(InvoiceData invoice, Action<InvoiceRule, string?, string, InvoiceField?> add)
    {
        if (invoice.Text("BT-3")?.Trim() == "386") return;
        var lines = invoice.Groups("BG-25").ToList();
        var delivered = invoice.Field("BT-72") is not null;
        var dated = delivered || invoice.Groups("BG-14").Any(g => g.HasContent)
            || (lines.Count > 0 && lines.All(l => l.GroupsOf("BG-26").Any(g => g.HasContent)));
        if (!dated) add(DeliveryDateDe, null, Place.Supply, invoice.Field("BT-3"));
        else if (!delivered && invoice.Field("BT-80") is null) add(DeliveryDateDe, "BT-80", Place.Supply, invoice.Field("BT-3"));
    }
}
