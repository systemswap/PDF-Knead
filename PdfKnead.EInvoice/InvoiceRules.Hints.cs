namespace PdfKnead.EInvoice;

/// <summary>
/// The hints of the check (stage 4, part g; KONZEPT.md, „Weg und Stand Stufe 4, Teil g“): what is no violation of a rule but worth a look —
/// information, each with its place in the XML. A value outside the scope of EN 16931: a term the standard does not have (most syntax rules
/// of CEN/TS 16931-3 say just that — here one kind of hint with the place, not hundreds of rules; which values they are, the mapping of stage
/// 2 decides, compared with the visualization of the KoSIT). An element without content (in the model of the standard no value; XRechnung
/// has its rule PEPPOL-EN16931-R008 for it, the artefacts of the profiles from BASIC on warn of it) — the same elements as R008 of XRechnung:
/// not the ones the schema demands even when there is nothing to say (in CII the delivery, in UBL the number of an order reference that
/// holds only the sales order), for there „leave it out“ would be wrong advice. What a profile of ZUGFeRD / Factur-X does
/// not use is the hint PDFK-FX-02 (InvoiceRules.ProfileStructure.cs); what the reader noticed — references never followed, another encoding
/// named — are findings of stage 1 (InvoiceLocator).
/// </summary>
public static partial class InvoiceRules
{
    /// <summary>The source of the hints.</summary>
    public const string HintSource = "PDF-Knead: Hinweise";

    /// <summary>
    /// Whether the invoice is measured against EN 16931 itself: UBL; CII of XRechnung, Peppol or without a profile — not a profile of ZUGFeRD /
    /// Factur-X, which says itself what it does not use (PDFK-FX-02), nor ZUGFeRD 1, which is older than the standard.
    /// </summary>
    private static bool AgainstTheStandard(Context c) =>
        c.Invoice.Kind.Syntax is InvoiceSyntax.UblInvoice or InvoiceSyntax.UblCreditNote
        || c.Invoice.Kind.Syntax == InvoiceSyntax.Cii && c.Invoice.Kind.Profile is not (InvoiceProfile.Minimum or InvoiceProfile.BasicWl
            or InvoiceProfile.Basic or InvoiceProfile.En16931 or InvoiceProfile.Extended or InvoiceProfile.ExtendedCtcFr);

    private static IEnumerable<RuleCheck> HintRules()
    {
        const InvoiceRuleSeverity I = InvoiceRuleSeverity.Information;
        yield return new RuleCheck(new InvoiceRule("PDFK-EN-01", HintSource, I,
                "Diese Angabe liegt außerhalb des Umfangs der EN 16931: die Norm hat keinen Begriff dafür (sie steht bei den weiteren Angaben).",
                "This value lies outside the scope of EN 16931: the standard has no term for it (it is among the further values)."), _ => [],
            FindWithPlace: c => AgainstTheStandard(c)
                ? c.Invoice.Further.Select(f => (c.GroupOf(f.Node.Element), (InvoiceField?)null, f.Text, new XmlPlace(f.Node.Element, f.Node.Attribute)))
                : []);
        yield return new RuleCheck(new InvoiceRule("PDFK-XML-01", HintSource, I,
                "Ein Element ist leer: im Modell der Norm ist es keine Angabe — weglassen oder füllen.",
                "An element is empty: in the model of the standard it is no value — leave it out or fill it."), _ => [],
            FindWithPlace: c => IsXRechnung(c) || c.Root.Element is null
                ? []
                : EmptyElements(SyntaxOf(c)).Select(e => (c.GroupOf(e), (InvoiceField?)null, (string?)null, new XmlPlace(e, null))));
    }
}
