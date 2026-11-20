namespace PdfKnead.EInvoice.Ui;

/// <summary>Short names of formats and profiles for the tab and the area (the names of the specifications, not translated).</summary>
internal static class InvoiceNames
{
    /// <summary>One word or two for the group „Info“: the profile of a CII invoice, else the format.</summary>
    public static string Short(InvoiceKind kind) => kind.Syntax switch
    {
        InvoiceSyntax.Cii => Profile(kind.Profile),
        InvoiceSyntax.ZugferdV1 => "ZUGFeRD 1",
        InvoiceSyntax.UblInvoice or InvoiceSyntax.UblCreditNote => kind.Profile switch
        {
            InvoiceProfile.XRechnung => "XRechnung (UBL)",
            InvoiceProfile.Peppol => "Peppol (UBL)",
            _ => "UBL",
        },
        InvoiceSyntax.OrderX => "Order-X",
        InvoiceSyntax.FatturaPa => "FatturaPA",
        _ => "?",
    };

    /// <summary>The syntax.</summary>
    public static string Format(InvoiceKind kind) => kind.Syntax switch
    {
        InvoiceSyntax.Cii => "UN/CEFACT CII",
        InvoiceSyntax.ZugferdV1 => "ZUGFeRD 1",
        InvoiceSyntax.UblInvoice => "UBL Invoice",
        InvoiceSyntax.UblCreditNote => "UBL Credit Note",
        InvoiceSyntax.OrderX => "Order-X",
        InvoiceSyntax.FatturaPa => "FatturaPA",
        _ => "?",
    };

    /// <summary>The profile as the specifications write it.</summary>
    public static string Profile(InvoiceProfile profile) => profile switch
    {
        InvoiceProfile.Minimum => "MINIMUM",
        InvoiceProfile.BasicWl => "BASIC WL",
        InvoiceProfile.Basic => "BASIC",
        InvoiceProfile.En16931 => "EN 16931",
        InvoiceProfile.Extended => "EXTENDED",
        InvoiceProfile.ExtendedCtcFr => "EXTENDED-CTC-FR",
        InvoiceProfile.XRechnung => "XRechnung",
        InvoiceProfile.Peppol => "Peppol",
        InvoiceProfile.ZugferdV1Basic => "ZUGFeRD 1 BASIC",
        InvoiceProfile.ZugferdV1Comfort => "ZUGFeRD 1 COMFORT",
        InvoiceProfile.ZugferdV1Extended => "ZUGFeRD 1 EXTENDED",
        InvoiceProfile.Other => Tr.T("unbekannt"),
        _ => Tr.T("keins"),
    };
}
