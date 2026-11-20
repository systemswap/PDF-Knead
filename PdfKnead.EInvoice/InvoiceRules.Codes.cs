namespace PdfKnead.EInvoice;

/// <summary>
/// The code list rules (stage 4, part c; BR-CL-01 to BR-CL-26): a code or scheme identifier must be one of its list. These rules are not in
/// EN 16931-1; the syntax bindings and the validation artefacts define them on elements of each syntax, so they are checked here on the
/// elements of the XML, per syntax, where the artefacts look — the codes themselves come from the code lists of the European Commission
/// (<see cref="InvoiceCodes"/>, Zuordnung/codes.tsv), which hold the same values. A value counts after its surrounding space is taken off and
/// inner space collapsed, as the artefacts read it; one with a space inside, or none, is no code. One walk over the tree per invoice.
/// ZUGFeRD 1 is no syntax of EN 16931: no code list rules for it.
/// </summary>
public static partial class InvoiceRules
{
    /// <summary>The source of the code list rules: the syntax bindings, with the code lists of the Commission.</summary>
    public const string CodeSource = "CEN/TS 16931-3-2, -3-3; Codelisten der Europäischen Kommission (Stand 2026-11-15)";

    private const string Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    private const string Rsm = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    private const string Udt = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100";
    private const string Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private const string Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";

    /// <summary>
    /// A code list rule on one kind of element: the element (namespace, local name), where it must stand, the attribute that holds the code
    /// (null: the text), whether the element only counts with that attribute, and whether a value is allowed (with the element, for rules
    /// that depend on where it stands).
    /// </summary>
    private sealed record CodeCheck(string Rule, string Namespace, string Local, Func<InvoiceElement, bool> Context, string? Attribute,
        bool OnlyWithAttribute, Func<InvoiceElement, string?, bool> Allowed);

    /// <summary>A code of the list, read as the artefacts read it (collapsed, no space inside, not empty); VATEX without regard to case.</summary>
    private static Func<InvoiceElement, string?, bool> In(string list, bool upper = false) => (_, value) => IsIn(list, value, upper);

    private static bool IsIn(string list, string? value, bool upper = false) =>
        Collapse(value) is { Length: > 0 } v && !v.Contains(' ') && InvoiceCodes.Contains(list, upper ? v.ToUpperInvariant() : v);

    private static bool IsOneOf(string? value, params string[] codes) =>
        Collapse(value) is { Length: > 0 } v && !v.Contains(' ') && codes.Contains(v, StringComparer.Ordinal);

    private static Func<InvoiceElement, string?, bool> OneOf(params string[] codes) => (_, value) => IsOneOf(value, codes);

    /// <summary>Space as XML counts it (space, tab, line feed, carriage return) collapsed to one and taken off at both ends.</summary>
    private static string Collapse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new System.Text.StringBuilder(value.Length);
        var space = false;
        foreach (var ch in value)
        {
            if (ch is ' ' or '\t' or '\n' or '\r')
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static bool Under(InvoiceElement e, string ns, string local) => e.Parent is { } p && p.Local == local && p.Namespace == ns;

    private static bool Within(InvoiceElement e, string ns, string local)
    {
        for (var p = e.Parent; p is not null; p = p.Parent)
            if (p.Local == local && p.Namespace == ns) return true;
        return false;
    }

    /// <summary>Whether an allowance or charge element is a charge (true), an allowance (false), or neither (indicator not a boolean as XPath reads one).</summary>
    private static bool? IsCharge(InvoiceElement? allowanceCharge, bool cii)
    {
        var text = cii
            ? allowanceCharge?.Child(Ram, "ChargeIndicator")?.Child(Udt, "Indicator")?.Text
            : allowanceCharge?.Child(Cbc, "ChargeIndicator")?.Text;
        return text?.Trim() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => null,
        };
    }

    private static readonly string[] MimeCodes =
    [
        "application/pdf", "image/png", "image/jpeg", "text/csv", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.oasis.opendocument.spreadsheet",
    ];

    /// <summary>The MIME code exactly as written (the artefacts compare it without taking space off).</summary>
    private static bool Mime(InvoiceElement e, string? value) => value is not null && MimeCodes.Contains(value, StringComparer.Ordinal);

    /// <summary>The VAT categories: the list of the Commission and „B“ (split payment, Italy), which both artefacts take (BR-B).</summary>
    private static bool VatCategory(InvoiceElement e, string? value) => IsIn("5305", value) || IsOneOf(value, "B");

    private static CodeCheck[] CiiCodeChecks() =>
    [
        new("BR-CL-01", Ram, "TypeCode", e => Under(e, Rsm, "ExchangedDocument"), null, false, In("1001")),
        new("BR-CL-03", Ram, "TaxTotalAmount", _ => true, "currencyID", true, In("Currency")),
        new("BR-CL-04", Ram, "InvoiceCurrencyCode", _ => true, null, false, In("Currency")),
        new("BR-CL-05", Ram, "TaxCurrencyCode", _ => true, null, false, In("Currency")),
        new("BR-CL-06", Ram, "DueDateTypeCode", _ => true, null, false, OneOf("5", "29", "72")),
        new("BR-CL-07", Ram, "ReferenceTypeCode", _ => true, null, false, In("1153")),
        new("BR-CL-08", Ram, "SubjectCode", _ => true, null, false, In("Text")),
        new("BR-CL-10", Ram, "GlobalID", e => !Within(e, Ram, "SpecifiedTradeProduct") && !Within(e, Ram, "ShipToTradeParty"), "schemeID", true, In("ICD")),
        new("BR-CL-11", Ram, "ID", e => !Within(e, Ram, "SpecifiedTaxRegistration"), "schemeID", true, In("ICD")),
        new("BR-CL-13", Ram, "ClassCode", _ => true, "listID", true, In("Item")),
        new("BR-CL-14", Ram, "CountryID", _ => true, null, false, In("Country")),
        new("BR-CL-15", Ram, "ID", e => Under(e, Ram, "OriginTradeCountry"), null, false, In("Country")),
        new("BR-CL-16", Ram, "TypeCode", e => Under(e, Ram, "SpecifiedTradeSettlementPaymentMeans"), null, false, In("Payment")),
        new("BR-CL-17", Ram, "CategoryCode", e => Under(e, Ram, "CategoryTradeTax"), null, false, VatCategory),
        new("BR-CL-18", Ram, "CategoryCode", e => Under(e, Ram, "ApplicableTradeTax"), null, false, VatCategory),
        new("BR-CL-19", Ram, "ReasonCode", e => Under(e, Ram, "SpecifiedTradeAllowanceCharge") && IsCharge(e.Parent, cii: true) == false, null, false, In("Allowance")),
        new("BR-CL-20", Ram, "ReasonCode", e => Under(e, Ram, "SpecifiedTradeAllowanceCharge") && IsCharge(e.Parent, cii: true) == true, null, false, In("Charge")),
        new("BR-CL-21", Ram, "GlobalID", e => Under(e, Ram, "SpecifiedTradeProduct"), "schemeID", true, In("ICD")),
        new("BR-CL-22", Ram, "ExemptionReasonCode", _ => true, null, false, In("VATEX", upper: true)),
        new("BR-CL-23", Ram, "BasisQuantity", _ => true, "unitCode", true, In("Unit")),
        new("BR-CL-23", Ram, "BilledQuantity", _ => true, "unitCode", true, In("Unit")),
        new("BR-CL-24", Ram, "AttachmentBinaryObject", _ => true, "mimeCode", true, Mime),
        new("BR-CL-25", Ram, "URIID", e => Under(e, Ram, "URIUniversalCommunication"), "schemeID", true, In("EAS")),
        new("BR-CL-26", Ram, "GlobalID", e => Under(e, Ram, "ShipToTradeParty") && e.Parent!.Parent is { } d && d.Local == "ApplicableHeaderTradeDelivery" && d.Namespace == Ram,
            "schemeID", true, In("ICD")),
    ];

    private static CodeCheck[] UblCodeChecks()
    {
        var checks = new List<CodeCheck>
        {
            // an invoice takes the invoice types of the list (and 81, as the artefacts allow it there), a credit note the credit note types
            new("BR-CL-01", Cbc, "InvoiceTypeCode", _ => true, null, false,
                (_, value) => IsIn("1001", value) && !IsIn("1001-Gutschrift", value) || IsOneOf(value, "81")),
            new("BR-CL-01", Cbc, "CreditNoteTypeCode", _ => true, null, false, In("1001-Gutschrift")),
            new("BR-CL-04", Cbc, "DocumentCurrencyCode", _ => true, null, false, In("Currency")),
            new("BR-CL-05", Cbc, "TaxCurrencyCode", _ => true, null, false, In("Currency")),
            new("BR-CL-06", Cbc, "DescriptionCode", e => Under(e, Cac, "InvoicePeriod"), null, false, OneOf("3", "35", "432")),
            new("BR-CL-07", Cbc, "ID", e => (Under(e, Cac, "AdditionalDocumentReference") || Under(e, Cac, "DocumentReference"))
                && e.Parent!.Children.Any(c => c.Local == "DocumentTypeCode" && c.Namespace == Cbc && c.Text == "130"), "schemeID", true, In("1153")),
            // the subject code of a note: three characters between the first two „#“
            new("BR-CL-08", Cbc, "Note", e => e.Parent is { Parent: null }, null, false, (_, value) => NoteSubjectFits(value)),
            // a scheme of ISO 6523 — or „SEPA“ (the creditor identifier) for the seller and the payee
            new("BR-CL-10", Cbc, "ID", e => Under(e, Cac, "PartyIdentification"), "schemeID", true,
                (e, value) => IsIn("ICD", value) || IsOneOf(value, "SEPA") && (Within(e, Cac, "AccountingSupplierParty") || Within(e, Cac, "PayeeParty"))),
            new("BR-CL-11", Cbc, "CompanyID", e => Under(e, Cac, "PartyLegalEntity"), "schemeID", true, In("ICD")),
            new("BR-CL-13", Cbc, "ItemClassificationCode", e => Under(e, Cac, "CommodityClassification"), "listID", true, In("Item")),
            new("BR-CL-14", Cbc, "IdentificationCode", e => Under(e, Cac, "Country"), null, false, In("Country")),
            new("BR-CL-15", Cbc, "IdentificationCode", e => Under(e, Cac, "OriginCountry"), null, false, In("Country")),
            new("BR-CL-16", Cbc, "PaymentMeansCode", e => Under(e, Cac, "PaymentMeans"), null, false, In("Payment")),
            new("BR-CL-17", Cbc, "ID", e => Under(e, Cac, "TaxCategory"), null, false, VatCategory),
            new("BR-CL-18", Cbc, "ID", e => Under(e, Cac, "ClassifiedTaxCategory"), null, false, VatCategory),
            new("BR-CL-19", Cbc, "AllowanceChargeReasonCode", e => Under(e, Cac, "AllowanceCharge") && IsCharge(e.Parent, cii: false) == false, null, false, In("Allowance")),
            new("BR-CL-20", Cbc, "AllowanceChargeReasonCode", e => Under(e, Cac, "AllowanceCharge") && IsCharge(e.Parent, cii: false) == true, null, false, In("Charge")),
            new("BR-CL-21", Cbc, "ID", e => Under(e, Cac, "StandardItemIdentification"), "schemeID", true, In("ICD")),
            new("BR-CL-22", Cbc, "TaxExemptionReasonCode", _ => true, null, false, In("VATEX", upper: true)),
            new("BR-CL-24", Cbc, "EmbeddedDocumentBinaryObject", _ => true, "mimeCode", true, Mime),
            new("BR-CL-25", Cbc, "EndpointID", _ => true, "schemeID", true, In("EAS")),
            new("BR-CL-26", Cbc, "ID", e => Under(e, Cac, "DeliveryLocation"), "schemeID", true, In("ICD")),
        };
        // every amount carries its currency, a code of ISO 4217 (UBL binds it to all amounts — a missing one is no code either)
        foreach (var amount in new[]
                 {
                     "Amount", "BaseAmount", "PriceAmount", "TaxAmount", "TaxableAmount", "LineExtensionAmount", "TaxExclusiveAmount",
                     "TaxInclusiveAmount", "AllowanceTotalAmount", "ChargeTotalAmount", "PrepaidAmount", "PayableRoundingAmount", "PayableAmount",
                 })
            checks.Add(new("BR-CL-03", Cbc, amount, _ => true, "currencyID", false, In("Currency")));
        foreach (var quantity in new[] { "InvoicedQuantity", "BaseQuantity", "CreditedQuantity" })
            checks.Add(new("BR-CL-23", Cbc, quantity, _ => true, "unitCode", true, In("Unit")));
        return [.. checks];
    }

    /// <summary>A note of UBL with a subject code „#XXX#…“: three characters between the first two „#“ must be a code of UNTDID 4451.</summary>
    private static bool NoteSubjectFits(string? note)
    {
        if (note is null) return true;
        var first = note.IndexOf('#');
        if (first < 0) return true;
        var second = note.IndexOf('#', first + 1);
        if (second < 0 || second - first - 1 != 3) return true;
        return InvoiceCodes.Contains("Text", note.Substring(first + 1, 3));
    }

    /// <summary>
    /// The code checks of a syntax by element, for the one walk over the tree; for the extension of XRechnung and for CVD with the rules that
    /// take the place of some of them (InvoiceRules.XRechnung.Extension.cs).
    /// </summary>
    private static Dictionary<(string Namespace, string Local), CodeCheck[]> CodeChecksOf(bool cii, XRechnungKind kind) =>
        XRechnungCodeChecks(cii ? CiiCodeChecks() : UblCodeChecks(), cii, kind).GroupBy(c => (c.Namespace, c.Local)).ToDictionary(g => g.Key, g => g.ToArray());

    // per syntax (CII, UBL) and kind of XRechnung (plain EN 16931 and the CIUS alike, the extension, CVD) made once
    private static readonly Lazy<Dictionary<(string, string), CodeCheck[]>>[] CodeCheckSets =
    [
        .. new[] { true, false }.SelectMany(cii => new[] { XRechnungKind.None, XRechnungKind.Extension, XRechnungKind.Cvd }
            .Select(kind => new Lazy<Dictionary<(string, string), CodeCheck[]>>(() => CodeChecksOf(cii, kind)))),
    ];

    private static Dictionary<(string, string), CodeCheck[]> CodeChecksFor(bool cii, XRechnungKind kind) =>
        CodeCheckSets[(cii ? 0 : 3) + kind switch
        {
            XRechnungKind.Extension => 1,
            XRechnungKind.Cvd => 2,
            _ => 0,
        }].Value;

    /// <summary>Every code that is not in its list, by rule: one walk over the elements of the invoice (own stack, any depth).</summary>
    private static Dictionary<string, List<(InvoiceElement Element, string? Value)>> CodeFindings(Context c)
    {
        var found = new Dictionary<string, List<(InvoiceElement, string?)>>(StringComparer.Ordinal);
        var checks = c.Invoice.Kind.Syntax switch
        {
            InvoiceSyntax.Cii => CodeChecksFor(cii: true, KindOf(c)),
            InvoiceSyntax.UblInvoice or InvoiceSyntax.UblCreditNote => CodeChecksFor(cii: false, KindOf(c)),
            _ => null,
        };
        if (checks is null || c.Root.Element is not { } root) return found;
        var pending = new Stack<InvoiceElement>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            for (var i = e.Children.Count - 1; i >= 0; i--) pending.Push(e.Children[i]);
            if (!checks.TryGetValue((e.Namespace, e.Local), out var candidates)) continue;
            foreach (var check in candidates)
            {
                var value = check.Attribute is { } a ? e.Attribute(a) : e.Text;
                if (check.OnlyWithAttribute && value is null || !check.Context(e)) continue;
                if (!check.Allowed(e, value))
                {
                    if (!found.TryGetValue(check.Rule, out var list)) found[check.Rule] = list = [];
                    list.Add((e, value));
                }
                break;   // as in the artefacts: of the rules that fit an element, only the first is asked
            }
        }
        return found;
    }

    /// <summary>The 23 code list rules, with own descriptions; each reports the element (its group, its term if it is one) and the code.</summary>
    private static IEnumerable<RuleCheck> CodeRules()
    {
        foreach (var (id, german, english) in new[]
                 {
                     ("BR-CL-01", "Der Code der Rechnungsart (BT-3) steht nicht in der Liste (UNTDID 1001, Auswahl der EN 16931; UBL trennt Rechnung und Gutschrift).",
                         "The invoice type code (BT-3) is not in its list (UNTDID 1001, the selection of EN 16931; UBL tells invoice and credit note apart)."),
                     ("BR-CL-03", "Die Währung eines Betrags (currencyID) ist kein Code nach ISO 4217.", "The currency of an amount (currencyID) is no code of ISO 4217."),
                     ("BR-CL-04", "Der Code der Rechnungswährung (BT-5) ist kein Code nach ISO 4217.", "The invoice currency code (BT-5) is no code of ISO 4217."),
                     ("BR-CL-05", "Der Code der Buchungswährung (BT-6) ist kein Code nach ISO 4217.", "The VAT accounting currency code (BT-6) is no code of ISO 4217."),
                     ("BR-CL-06", "Der Code des Steuerdatums (BT-8) ist keiner der erlaubten (CII 5, 29, 72; UBL 3, 35, 432).",
                         "The VAT point date code (BT-8) is none of the allowed ones (CII 5, 29, 72; UBL 3, 35, 432)."),
                     ("BR-CL-07", "Das Schema der Objektkennung (BT-18) steht nicht in UNTDID 1153.", "The scheme of the invoiced object identifier (BT-18) is not in UNTDID 1153."),
                     ("BR-CL-08", "Der Betreff-Code einer Bemerkung (BT-21) steht nicht in UNTDID 4451.", "The subject code of a note (BT-21) is not in UNTDID 4451."),
                     ("BR-CL-10", "Das Schema einer Kennung (BT-29, BT-46, BT-60) steht nicht in ISO/IEC 6523 (in UBL für Verkäufer und Zahlungsempfänger auch SEPA).",
                         "The scheme of an identifier (BT-29, BT-46, BT-60) is not in ISO/IEC 6523 (in UBL also SEPA for seller and payee)."),
                     ("BR-CL-11", "Das Schema einer Registernummer (BT-30, BT-47, BT-61) steht nicht in ISO/IEC 6523.",
                         "The scheme of a legal registration identifier (BT-30, BT-47, BT-61) is not in ISO/IEC 6523."),
                     ("BR-CL-13", "Das Schema der Artikelklassifizierung (BT-158) steht nicht in UNTDID 7143.", "The scheme of the item classification (BT-158) is not in UNTDID 7143."),
                     ("BR-CL-14", "Ein Ländercode (BT-40, BT-55, BT-69, BT-80) ist kein Code nach ISO 3166-1.", "A country code (BT-40, BT-55, BT-69, BT-80) is no code of ISO 3166-1."),
                     ("BR-CL-15", "Der Code des Herkunftslands (BT-159) ist kein Code nach ISO 3166-1.", "The item country of origin (BT-159) is no code of ISO 3166-1."),
                     ("BR-CL-16", "Der Code der Zahlungsart (BT-81) steht nicht in UNTDID 4461.", "The payment means type code (BT-81) is not in UNTDID 4461."),
                     ("BR-CL-17", "Eine Steuerkategorie (Nachlass, Zuschlag; in UBL auch Aufschlüsselung) steht nicht in UNTDID 5305 (Auswahl der EN 16931).",
                         "A VAT category (allowance, charge; in UBL also breakdown) is not in UNTDID 5305 (the selection of EN 16931)."),
                     ("BR-CL-18", "Eine Steuerkategorie (Position; in CII auch Aufschlüsselung) steht nicht in UNTDID 5305 (Auswahl der EN 16931).",
                         "A VAT category (invoice line; in CII also breakdown) is not in UNTDID 5305 (the selection of EN 16931)."),
                     ("BR-CL-19", "Der Code eines Nachlassgrunds (BT-98, BT-140) steht nicht in UNTDID 5189.", "An allowance reason code (BT-98, BT-140) is not in UNTDID 5189."),
                     ("BR-CL-20", "Der Code eines Zuschlagsgrunds (BT-105, BT-145) steht nicht in UNTDID 7161.", "A charge reason code (BT-105, BT-145) is not in UNTDID 7161."),
                     ("BR-CL-21", "Das Schema der Artikelkennung (BT-157) steht nicht in ISO/IEC 6523.", "The scheme of the item standard identifier (BT-157) is not in ISO/IEC 6523."),
                     ("BR-CL-22", "Der Code des Befreiungsgrunds (BT-121) steht nicht in der VATEX-Liste.", "The VAT exemption reason code (BT-121) is not in the VATEX list."),
                     ("BR-CL-23", "Eine Einheit (BT-130, BT-150) steht nicht in den UN/ECE-Empfehlungen 20 und 21.", "A unit of measure (BT-130, BT-150) is not in UN/ECE Recommendations 20 and 21."),
                     ("BR-CL-24", "Der MIME-Typ eines Anhangs (BT-125) ist keiner der erlaubten (PDF, PNG, JPEG, CSV, XLSX, ODS).",
                         "The MIME code of an attachment (BT-125) is none of the allowed ones (PDF, PNG, JPEG, CSV, XLSX, ODS)."),
                     ("BR-CL-25", "Das Schema einer elektronischen Adresse (BT-34, BT-49) steht nicht in der EAS-Liste.", "The scheme of an electronic address (BT-34, BT-49) is not in the EAS list."),
                     ("BR-CL-26", "Das Schema der Lieferortkennung (BT-71) steht nicht in ISO/IEC 6523.", "The scheme of the deliver-to location identifier (BT-71) is not in ISO/IEC 6523."),
                 })
        {
            var rule = id;
            yield return new RuleCheck(new InvoiceRule(rule, CodeSource, InvoiceRuleSeverity.Fatal, german, english), _ => [],
                c => c.CodeFindings.TryGetValue(rule, out var list) ? list.Select(f => (c.GroupOf(f.Element), c.FieldOf(f.Element), f.Value)) : []);
        }
    }
}
