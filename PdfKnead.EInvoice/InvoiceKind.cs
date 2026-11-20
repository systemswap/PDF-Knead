namespace PdfKnead.EInvoice;

/// <summary>The syntax of an invoice XML, by its root element (namespace and local name), never by its file name.</summary>
public enum InvoiceSyntax
{
    /// <summary>UN/CEFACT Cross Industry Invoice (ZUGFeRD 2.x, Factur-X, XRechnung CII).</summary>
    Cii,

    /// <summary>ZUGFeRD 1.0 (CrossIndustryDocument, its own tree).</summary>
    ZugferdV1,

    /// <summary>UBL 2.1 invoice (XRechnung UBL, Peppol BIS).</summary>
    UblInvoice,

    /// <summary>UBL 2.1 credit note.</summary>
    UblCreditNote,

    /// <summary>Order-X: an order, not an invoice.</summary>
    OrderX,

    /// <summary>FatturaPA (Italy): an e-invoice of its own syntax, not EN 16931 CII or UBL — recognised, not checked.</summary>
    FatturaPa,

    /// <summary>No e-invoice syntax PDF-Knead knows.</summary>
    Unknown,
}

/// <summary>The profile an invoice declares (BT-24 „Specification identifier“).</summary>
public enum InvoiceProfile
{
    Minimum,
    BasicWl,
    Basic,
    En16931,
    Extended,
    ExtendedCtcFr,
    XRechnung,
    Peppol,
    ZugferdV1Basic,
    ZugferdV1Comfort,
    ZugferdV1Extended,

    /// <summary>A specification identifier PDF-Knead does not know; shown as it is.</summary>
    Other,

    /// <summary>The XML names no specification identifier.</summary>
    None,
}

/// <summary>
/// What an invoice XML is: syntax, the declared specification identifier and the profile it stands for, the version of the EN 16931 it
/// refers to, and whether that profile is an e-invoice in the sense of § 14 UStG (KONZEPT.md, section 5.24: MINIMUM, BASIC WL and
/// ZUGFeRD 1.0 are not; this is information from the letters of the Federal Ministry of Finance, not a legal judgement).
/// </summary>
public sealed record InvoiceKind(InvoiceSyntax Syntax, string? Guideline, InvoiceProfile Profile, string? Norm)
{
    public const string Cii = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    public const string CiiRam = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    public const string CiiUdt = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100";
    public const string ZugferdV1 = "urn:ferd:CrossIndustryDocument:invoice:1p0";
    public const string ZugferdV1Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:12";
    public const string UblInvoiceNs = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    public const string UblCreditNoteNs = "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2";
    public const string UblCbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    public const string UblCac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    public const string OrderXNs = "urn:un:unece:uncefact:data:standard:SCRDMCCBDACIOMessageStructure:100";

    /// <summary>
    /// Whether the identifier is one of ZUGFeRD 2.0 („urn:zugferd.de:2p0:…“), the version before 2.0.1 (= Factur-X 1.0): the letters of the
    /// Federal Ministry of Finance name ZUGFeRD only from 2.0.1 on.
    /// </summary>
    public bool ZugferdV20 => Guideline?.Contains("urn:zugferd.de:2p0:", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Whether the profile is an e-invoice in Germany (BMF letters 15.10.2024 and 15.10.2025); null if they do not say — an unknown profile,
    /// or BASIC and EXTENDED of ZUGFeRD 2.0, which the letters do not name (then only the conformance to EN 16931 decides).
    /// </summary>
    public bool? EInvoiceInGermany => Profile switch
    {
        InvoiceProfile.Minimum or InvoiceProfile.BasicWl or InvoiceProfile.ZugferdV1Basic or InvoiceProfile.ZugferdV1Comfort or InvoiceProfile.ZugferdV1Extended => false,
        InvoiceProfile.Basic or InvoiceProfile.Extended when ZugferdV20 => null,
        InvoiceProfile.Basic or InvoiceProfile.En16931 or InvoiceProfile.Extended or InvoiceProfile.ExtendedCtcFr or InvoiceProfile.XRechnung
            or InvoiceProfile.Peppol => Syntax is InvoiceSyntax.Cii or InvoiceSyntax.UblInvoice or InvoiceSyntax.UblCreditNote,
        _ => null,
    };

    /// <summary>What a root element and its document context say.</summary>
    public static InvoiceKind Of(InvoiceXmlDocument xml)
    {
        var root = xml.Root;
        var syntax = (root.Namespace, root.Local) switch
        {
            (Cii, "CrossIndustryInvoice") => InvoiceSyntax.Cii,
            (ZugferdV1, "CrossIndustryDocument") => InvoiceSyntax.ZugferdV1,
            // the drafts of ZUGFeRD 1.0 (2014) used namespaces of their own under urn:ferd:
            (var ns, "CrossIndustryDocument") when ns.StartsWith("urn:ferd:", StringComparison.Ordinal) => InvoiceSyntax.ZugferdV1,
            (UblInvoiceNs, "Invoice") => InvoiceSyntax.UblInvoice,
            (UblCreditNoteNs, "CreditNote") => InvoiceSyntax.UblCreditNote,
            (OrderXNs, _) => InvoiceSyntax.OrderX,
            (var ns, "FatturaElettronica") when ns.StartsWith("http://ivaservizi.agenziaentrate.gov.it/", StringComparison.Ordinal) => InvoiceSyntax.FatturaPa,
            _ => InvoiceSyntax.Unknown,
        };
        var guideline = syntax switch
        {
            InvoiceSyntax.Cii => root.Child(Cii, "ExchangedDocumentContext")?.Child(CiiRam, "GuidelineSpecifiedDocumentContextParameter")?.Child(CiiRam, "ID")?.Text,
            // ZUGFeRD 1 and its drafts: by local name below the document context (the drafts' namespaces differ)
            InvoiceSyntax.ZugferdV1 => root.Children.FirstOrDefault(c => c.Local == "SpecifiedExchangedDocumentContext")?.Children
                .FirstOrDefault(c => c.Local == "GuidelineSpecifiedDocumentContextParameter")?.Children.FirstOrDefault(c => c.Local == "ID")?.Text,
            InvoiceSyntax.UblInvoice or InvoiceSyntax.UblCreditNote => root.Child(UblCbc, "CustomizationID")?.Text,
            _ => null,
        };
        guideline = guideline?.Trim();
        return new InvoiceKind(syntax, guideline, ProfileOf(guideline), NormOf(guideline));
    }

    /// <summary>The profile a specification identifier stands for (ZUGFeRD, Factur-X, XRechnung, Peppol; also the forms of ZUGFeRD 2.0).</summary>
    public static InvoiceProfile ProfileOf(string? guideline)
    {
        if (string.IsNullOrWhiteSpace(guideline)) return InvoiceProfile.None;
        var g = guideline.Trim().ToLowerInvariant();
        if (g.Contains(":order:")) return InvoiceProfile.Other;   // an order, not an invoice
        if (g.StartsWith("urn:ferd:", StringComparison.Ordinal))
            return g.EndsWith(":comfort", StringComparison.Ordinal) ? InvoiceProfile.ZugferdV1Comfort
                : g.EndsWith(":extended", StringComparison.Ordinal) ? InvoiceProfile.ZugferdV1Extended : InvoiceProfile.ZugferdV1Basic;
        // XRechnung, also the identifiers of its earlier versions (1.2 to 2.x: urn:xoev-de:kosit:standard:xrechnung_…)
        if (g.Contains("xeinkauf.de:kosit:xrechnung") || g.Contains("xeinkauf.de:kosit:extension:xrechnung") || g.Contains("xoev-de:kosit:standard:xrechnung"))
            return InvoiceProfile.XRechnung;
        if (g.Contains("peppol.eu:2017:poacc:billing")) return InvoiceProfile.Peppol;
        if (g.Contains("extended-ctc-fr")) return InvoiceProfile.ExtendedCtcFr;
        if (g.EndsWith(":minimum", StringComparison.Ordinal)) return InvoiceProfile.Minimum;
        if (g.EndsWith(":basicwl", StringComparison.Ordinal)) return InvoiceProfile.BasicWl;
        if (g.EndsWith(":basic", StringComparison.Ordinal)) return InvoiceProfile.Basic;
        if (g.EndsWith(":extended", StringComparison.Ordinal)) return InvoiceProfile.Extended;
        if (g is "urn:cen.eu:en16931:2017" or "urn:cen.eu:en16931:2026" || g.EndsWith(":en16931", StringComparison.Ordinal) || g.EndsWith(":comfort", StringComparison.Ordinal))
            return InvoiceProfile.En16931;
        return InvoiceProfile.Other;
    }

    /// <summary>The version of the EN 16931 a specification identifier names („2017“, „2026“); null if none.</summary>
    public static string? NormOf(string? guideline) =>
        guideline is null ? null
        : guideline.Contains("urn:cen.eu:en16931:2026", StringComparison.OrdinalIgnoreCase) ? "2026"
        : guideline.Contains("urn:cen.eu:en16931:2017", StringComparison.OrdinalIgnoreCase) ? "2017"
        : null;
}
