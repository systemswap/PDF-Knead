using System.Text.RegularExpressions;

namespace PdfKnead.EInvoice;

/// <summary>
/// The rules of XRechnung 3.0 (stage 4, part d1; KONZEPT.md, „Weg für Stufe 4, Teil d“): the national business rules BR-DE (chapter 12.6
/// of the specification XRechnung 3.0.2) and the rules taken over from Peppol BIS Billing 3.0 (PEPPOL-EN16931-R…, chapter 12.5 and 13.4),
/// with the identifiers the validation artefacts of the KoSIT give them. Only for an invoice whose specification identifier (BT-24) is that
/// of XRechnung 3.0 — the CIUS, its extension or CVD — as the scenarios of the reference checker choose them. Written from the
/// specification in code of our own; where the artefacts check an element of the syntax of its own (payment means, empty elements,
/// periods of lines), on the elements of the XML, as in part c. The rules of the extension, the temporary rules and those of CVD (part d2)
/// stand in InvoiceRules.XRechnung.Extension.cs.
/// </summary>
public static partial class InvoiceRules
{
    /// <summary>The source of the XRechnung rules.</summary>
    public const string XRechnungSource = "XRechnung 3.0.2 (KoSIT)";

    private const string XRechnungCius = "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0";
    private const string XRechnungExtension = XRechnungCius + "#conformant#urn:xeinkauf.de:kosit:extension:xrechnung_3.0";
    private const string XRechnungCvd = XRechnungCius + "#compliant#urn:xeinkauf.de:kosit:xrechnung:cvd_0.9";

    /// <summary>The specification of XRechnung 3.0 an invoice names in BT-24: none, the CIUS, its extension or CVD.</summary>
    private enum XRechnungKind
    {
        None,
        Cius,
        Extension,
        Cvd,
    }

    /// <summary>Which XRechnung rules apply: BT-24 exactly one of the three identifiers of XRechnung 3.0, CII or UBL (found once per invoice).</summary>
    private static XRechnungKind KindOf(Context c) => c.XRechnung ??=
        c.Invoice.Kind.Syntax is InvoiceSyntax.Cii or InvoiceSyntax.UblInvoice or InvoiceSyntax.UblCreditNote && c.Root.Element is not null
            ? c.Root.AllFields().FirstOrDefault(f => f.Id == "BT-24")?.Text switch
            {
                XRechnungCius => XRechnungKind.Cius,
                XRechnungExtension => XRechnungKind.Extension,
                XRechnungCvd => XRechnungKind.Cvd,
                _ => XRechnungKind.None,
            }
            : XRechnungKind.None;

    private static bool IsXRechnung(Context c) => KindOf(c) != XRechnungKind.None;

    private static RuleCheck X(string id, InvoiceRuleSeverity severity, string german, string english, Func<Context, IEnumerable<(InvoiceGroup, InvoiceField?)>> find) =>
        new(new InvoiceRule(id, XRechnungSource, severity, german, english), c => IsXRechnung(c) ? find(c) : []);

    // with a time limit, as every pattern of the module (a prepared value must not cost long); a value that runs into it counts as not
    // having the form
    private static readonly Regex SkontoLine = new Regex(@"^#SKONTO#TAGE=[0-9]+#PROZENT=[0-9]+\.[0-9]{2}(#BASISBETRAG=-?[0-9]+\.[0-9]{2})?#$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Email = new Regex(@"^[^@\s]+@([^@.\s]+\.)+[^@.\s]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static bool Fits(Regex pattern, string value)
    {
        try
        {
            return pattern.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>The document (root) element, the settlement and the payment means of the invoice — CII or UBL.</summary>
    private sealed record Syntax(bool Cii, InvoiceElement Root)
    {
        public InvoiceElement? Transaction => Cii ? Root.Child(Rsm, "SupplyChainTradeTransaction") : null;

        public InvoiceElement? Settlement => Transaction?.Child(Ram, "ApplicableHeaderTradeSettlement");

        public IEnumerable<InvoiceElement> PaymentMeans => Cii
            ? Settlement?.ChildrenNamed(Ram, "SpecifiedTradeSettlementPaymentMeans") ?? []
            : Root.ChildrenNamed(Cac, "PaymentMeans");

        public string? Code(InvoiceElement means) => Collapse(ChildText(means, Cii ? "TypeCode" : "PaymentMeansCode"));

        /// <summary>In CII a direct debit mandate in any payment terms (BT-89), found once per invoice.</summary>
        public bool HasMandate => _mandate ??= Cii && Settlement?.ChildrenNamed(Ram, "SpecifiedTradePaymentTerms").Any(t => t.Child(Ram, "DirectDebitMandateID") is not null) == true;

        private bool? _mandate;

        /// <summary>In CII the direct debit of the settlement: mandate or creditor identifier (BT-89, BT-90), found once per invoice.</summary>
        public bool SettlementDebit => HasMandate || Cii && Settlement?.Child(Ram, "CreditorReferenceID") is not null;

        public IEnumerable<InvoiceElement> Lines => Cii
            ? Transaction?.ChildrenNamed(Ram, "IncludedSupplyChainTradeLineItem") ?? []
            : Root.Children.Where(e => e.Namespace == Cac && e.Local is "InvoiceLine" or "CreditNoteLine");

        /// <summary>Every sub line of UBL (SubInvoiceLine, at any depth, as the artefacts look for it), found once per invoice.</summary>
        public List<InvoiceElement> SubInvoiceLines => _subLines ??= Cii ? [] : [.. Descendants(Root).Where(e => e.Local == "SubInvoiceLine" && e.Namespace == Cac)];

        private List<InvoiceElement>? _subLines;

        /// <summary>Every price of UBL (cac:Price at any depth: of lines and of sub lines, where the Peppol rules on prices look), found once per invoice.</summary>
        public List<InvoiceElement> Prices => _prices ??= Cii ? [] : [.. Descendants(Root).Where(e => e.Local == "Price" && e.Namespace == Cac)];

        private List<InvoiceElement>? _prices;
    }

    /// <summary>The syntax view of the invoice, made once per invoice (the rules ask it often; what it finds once it keeps).</summary>
    private static Syntax SyntaxOf(Context c) => (Syntax)(c.SyntaxView ??= new Syntax(c.Invoice.Kind.Syntax == InvoiceSyntax.Cii, c.Root.Element!));

    private static bool Filled(InvoiceElement? e) => !string.IsNullOrWhiteSpace(e?.Text);

    /// <summary>Whether the invoice has a direct debit (BG-19): in CII a mandate, the creditor identifier or a debited account; in UBL a mandate.</summary>
    private static bool HasDirectDebit(Syntax s) => s.Cii
        ? s.SettlementDebit || s.PaymentMeans.Any(m => m.Child(Ram, "PayerPartyDebtorFinancialAccount")?.Child(Ram, "IBANID") is not null)
        : s.PaymentMeans.Any(m => m.Child(Cac, "PaymentMandate") is not null);

    /// <summary>An IBAN by ISO 13616: two letters, two check digits, up to 30 letters or digits; the remainder 1 when divided by 97 (space taken out).</summary>
    private static bool IsIban(string? text)
    {
        var iban = new string((text ?? "").Where(ch => !char.IsWhiteSpace(ch)).ToArray());
        if (iban.Length is < 4 or > 34 || !char.IsAsciiLetterUpper(iban[0]) || !char.IsAsciiLetterUpper(iban[1]) || !char.IsAsciiDigit(iban[2])
            || !char.IsAsciiDigit(iban[3]) || !iban.All(char.IsAsciiLetterOrDigit))
            return false;
        var remainder = 0;
        foreach (var ch in iban[4..] + iban[..4])
        {
            var value = char.IsAsciiDigit(ch) ? ch - '0' : char.ToUpperInvariant(ch) - 'A' + 10;
            remainder = value < 10 ? (remainder * 10 + value) % 97 : (remainder * 100 + value) % 97;
        }
        return remainder == 1;
    }

    /// <summary>The slack of the Peppol calculations: 0.02, for the forint 0.5.</summary>
    private static decimal Slack(Context c) => Text(c.Root, "BT-5") == "HUF" ? 0.5m : 0.02m;

    private static decimal? DecimalOf(InvoiceElement? e) => e?.Text is { } t ? InvoiceFormat.Number(t) : null;

    private static IEnumerable<RuleCheck> XRechnungRules()
    {
        const InvoiceRuleSeverity F = InvoiceRuleSeverity.Fatal, W = InvoiceRuleSeverity.Warning;

        // ---- BR-DE: what an invoice to public buyers in Germany must contain (specification XRechnung 3.0.2, chapter 12.6)
        yield return X("BR-DE-1", F, "Die Rechnung hat keine Zahlungsanweisung (BG-16).", "The invoice has no payment instructions (BG-16).",
            c => SyntaxOf(c).PaymentMeans.Any() ? [] : [(c.Root, null)]);
        // the specification asks for the contact without condition: also where the seller is missing altogether
        yield return X("BR-DE-2", F, "Der Verkäufer hat keinen Kontakt (BG-6).", "The seller has no contact (BG-6).",
            c => c.All("BG-6").Count == 0 ? [(c.First("BG-4") ?? c.Root, null)] : []);
        yield return X("BR-DE-3", F, "Die Anschrift des Verkäufers hat keinen Ort (BT-37).", "The seller address has no city (BT-37).",
            c => MissingIn(c.All("BG-4").SelectMany(g => g.GroupsOf("BG-5")), "BT-37"));
        yield return X("BR-DE-4", F, "Die Anschrift des Verkäufers hat keine Postleitzahl (BT-38).", "The seller address has no post code (BT-38).",
            c => MissingIn(c.All("BG-4").SelectMany(g => g.GroupsOf("BG-5")), "BT-38"));
        yield return X("BR-DE-5", F, "Der Kontakt des Verkäufers hat keine Kontaktstelle (BT-41).", "The seller contact has no contact point (BT-41).",
            c => MissingIn(c.All("BG-6"), "BT-41"));
        yield return X("BR-DE-6", F, "Der Kontakt des Verkäufers hat keine Telefonnummer (BT-42).", "The seller contact has no telephone number (BT-42).",
            c => MissingIn(c.All("BG-6"), "BT-42"));
        yield return X("BR-DE-7", F, "Der Kontakt des Verkäufers hat keine E-Mail-Adresse (BT-43).", "The seller contact has no email address (BT-43).",
            c => MissingIn(c.All("BG-6"), "BT-43"));
        yield return X("BR-DE-8", F, "Die Anschrift des Käufers hat keinen Ort (BT-52).", "The buyer address has no city (BT-52).",
            c => MissingIn(c.All("BG-7").SelectMany(g => g.GroupsOf("BG-8")), "BT-52"));
        yield return X("BR-DE-9", F, "Die Anschrift des Käufers hat keine Postleitzahl (BT-53).", "The buyer address has no post code (BT-53).",
            c => MissingIn(c.All("BG-7").SelectMany(g => g.GroupsOf("BG-8")), "BT-53"));
        yield return X("BR-DE-10", F, "Die Lieferanschrift hat keinen Ort (BT-77).", "The deliver-to address has no city (BT-77).", c => MissingIn(c.All("BG-15"), "BT-77"));
        yield return X("BR-DE-11", F, "Die Lieferanschrift hat keine Postleitzahl (BT-78).", "The deliver-to address has no post code (BT-78).", c => MissingIn(c.All("BG-15"), "BT-78"));
        yield return X("BR-DE-14", F, "Eine Steueraufschlüsselung hat keinen Steuersatz (BT-119).", "A VAT breakdown has no VAT rate (BT-119).",
            c => c.All("BG-23").Where(g => VatField(g, "BT-119") is not { } f || string.IsNullOrWhiteSpace(f.Text)).Select(g => (g, (InvoiceField?)null)));
        yield return X("BR-DE-15", F, "Die Käuferreferenz (BT-10) fehlt.", "The buyer reference (BT-10) is missing.", c => Missing(c.Root, "BT-10"));
        yield return X("BR-DE-16", F, "Mit den Steuerkategorien S, Z, E, AE, K, G, L oder M fehlt USt-IdNr. (BT-31), Steuernummer (BT-32) und steuerlicher Vertreter (BG-11) des Verkäufers.",
            "With the VAT categories S, Z, E, AE, K, G, L or M the seller VAT identifier (BT-31), tax registration (BT-32) and tax representative (BG-11) are all missing.",
            c => c.VatItems.Any(i => i.Category is "S" or "Z" or "E" or "AE" or "K" or "G" or "L" or "M")
                 && !AnyId(c, "BG-4", "BT-31", "BT-32") && c.All("BG-11").Count == 0 ? [(c.Root, null)] : []);
        yield return X("BR-DE-17", W, "Der Code der Rechnungsart (BT-3) ist keiner der empfohlenen (326, 380, 381, 384, 389, 875, 876, 877).",
            "The invoice type code (BT-3) is none of the recommended ones (326, 380, 381, 384, 389, 875, 876, 877).",
            c => Collapse(Text(c.Root, "BT-3")) is "326" or "380" or "381" or "384" or "389" or "875" or "876" or "877" ? [] : [(c.Root, c.Root.Field("BT-3"))]);
        yield return X("BR-DE-18", F, "Eine Skonto-Angabe in den Zahlungsbedingungen (BT-20) hat nicht die Form „#SKONTO#TAGE=n#PROZENT=n.nn#“ (dazu wahlweise „BASISBETRAG=n.nn#“) mit Zeilenende.",
            "A cash discount in the payment terms (BT-20) does not have the form „#SKONTO#TAGE=n#PROZENT=n.nn#“ (optionally with „BASISBETRAG=n.nn#“) followed by a line end.",
            c => SkontoFits(SyntaxOf(c)) ? [] : [(c.Root, c.Root.AllFields().FirstOrDefault(f => f.Id == "BT-20"))]);
        yield return X("BR-DE-19", W, "Bei SEPA-Überweisung (Code 58) ist die Kontokennung (BT-84) keine gültige IBAN.", "With a SEPA credit transfer (code 58) the payment account identifier (BT-84) is no valid IBAN.",
            c => SyntaxOf(c) is var s ? s.PaymentMeans.Where(m => s.Code(m) == "58" && !IsIban(s.Cii
                    ? m.Child(Ram, "PayeePartyCreditorFinancialAccount")?.Child(Ram, "IBANID")?.Text
                    : m.Child(Cac, "PayeeFinancialAccount")?.Child(Cbc, "ID")?.Text)).Select(m => (c.GroupOf(m), (InvoiceField?)null)) : []);
        yield return X("BR-DE-20", W, "Bei SEPA-Lastschrift (Code 59) ist das belastete Konto (BT-91) keine gültige IBAN.", "With a SEPA direct debit (code 59) the debited account identifier (BT-91) is no valid IBAN.",
            c => SyntaxOf(c) is var s ? s.PaymentMeans.Where(m => s.Code(m) == "59" && !IsIban(s.Cii
                    ? m.Child(Ram, "PayerPartyDebtorFinancialAccount")?.Child(Ram, "IBANID")?.Text
                    : m.Child(Cac, "PaymentMandate")?.Child(Cac, "PayerFinancialAccount")?.Child(Cbc, "ID")?.Text)).Select(m => (c.GroupOf(m), (InvoiceField?)null)) : []);
        yield return X("BR-DE-21", W, "Die Spezifikationskennung (BT-24) ist nicht die Kennung von XRechnung.", "The specification identifier (BT-24) is not that of XRechnung.",
            _ => []);   // the rules apply only to an invoice whose BT-24 is one of the identifiers — so this holds whenever they apply
        yield return X("BR-DE-22", F, "Zwei Anhänge (BT-125) haben denselben Dateinamen (ohne Rücksicht auf Groß- und Kleinschreibung).",
            "Two attachments (BT-125) have the same file name (without regard to case).",
            c => DuplicateFileNames(SyntaxOf(c)) ? [(c.Root, null)] : []);
        foreach (var check in PaymentMeansRules()) yield return check;
        yield return X("BR-DE-26", W, "Eine Rechnungskorrektur (Code 384) hat keinen Verweis auf die vorausgegangene Rechnung (BG-3).",
            "A corrected invoice (code 384) has no preceding invoice reference (BG-3).",
            c => Collapse(Text(c.Root, "BT-3")) == "384" && !(SyntaxOf(c) is var s && (s.Cii
                    ? s.Settlement?.Child(Ram, "InvoiceReferencedDocument") is not null
                    : s.Root.Child(Cac, "BillingReference")?.Child(Cac, "InvoiceDocumentReference") is not null)) ? [(c.Root, c.Root.Field("BT-3"))] : []);
        yield return X("BR-DE-27", W, "Die Telefonnummer des Verkäuferkontakts (BT-42) hat keine drei Ziffern.", "The seller contact telephone number (BT-42) has fewer than three digits.",
            c => c.All("BG-6").Where(g => (Text(g, "BT-42") ?? "").Count(char.IsAsciiDigit) < 3).Select(g => (g, g.Field("BT-42"))));
        yield return X("BR-DE-28", W, "Die E-Mail-Adresse des Verkäuferkontakts (BT-43) hat nicht die Form einer E-Mail-Adresse.", "The seller contact email address (BT-43) does not have the form of an email address.",
            c => c.All("BG-6").Where(g => !Fits(Email, Collapse(Text(g, "BT-43")))).Select(g => (g, g.Field("BT-43"))));
        yield return X("BR-DE-30", F, "Bei Lastschrift (BG-19) fehlt die Gläubiger-Identifikationsnummer (BT-90).", "With a direct debit (BG-19) the bank assigned creditor identifier (BT-90) is missing.",
            c => SyntaxOf(c) is var s && HasDirectDebit(s) && !(s.Cii
                    ? s.Settlement?.Child(Ram, "CreditorReferenceID") is not null
                    : new[] { s.Root.Child(Cac, "AccountingSupplierParty")?.Child(Cac, "Party"), s.Root.Child(Cac, "PayeeParty") }
                        .Any(p => p?.ChildrenNamed(Cac, "PartyIdentification").Any(i => i.Child(Cbc, "ID")?.Attribute("schemeID") == "SEPA") == true))
                ? [(c.First("BG-19") ?? c.Root, null)] : []);
        yield return X("BR-DE-31", F, "Bei Lastschrift (BG-19) fehlt das belastete Konto (BT-91).", "With a direct debit (BG-19) the debited account identifier (BT-91) is missing.",
            c => SyntaxOf(c) is var s && HasDirectDebit(s) && !(s.Cii
                    ? s.PaymentMeans.Any(m => m.Child(Ram, "PayerPartyDebtorFinancialAccount")?.Child(Ram, "IBANID") is not null)
                    : s.PaymentMeans.Any(m => m.Child(Cac, "PaymentMandate")?.Child(Cac, "PayerFinancialAccount")?.Child(Cbc, "ID") is not null))
                ? [(c.First("BG-19") ?? c.Root, null)] : []);

        foreach (var check in PeppolRules()) yield return check;
        foreach (var check in ExtensionRules()) yield return check;
        foreach (var check in TemporaryRules()) yield return check;
        foreach (var check in CvdRules()) yield return check;
    }

    /// <summary>BR-DE-23 to BR-DE-25: per payment means its code asks for its own group and forbids the others (-a and -b, as the artefacts split them).</summary>
    private static IEnumerable<RuleCheck> PaymentMeansRules()
    {
        const InvoiceRuleSeverity F = InvoiceRuleSeverity.Fatal;
        IEnumerable<(InvoiceGroup, InvoiceField?)> Find(Context c, Func<string?, bool> codes, Func<Syntax, InvoiceElement, bool> wrong)
        {
            var s = SyntaxOf(c);
            return s.PaymentMeans.Where(m => codes(s.Code(m)) && wrong(s, m)).Select(m => (c.GroupOf(m), (InvoiceField?)null));
        }
        static bool Transfer(Syntax s, InvoiceElement m) => m.Child(s.Cii ? Ram : Cac, s.Cii ? "PayeePartyCreditorFinancialAccount" : "PayeeFinancialAccount") is not null;
        static bool Card(Syntax s, InvoiceElement m) => m.Child(s.Cii ? Ram : Cac, s.Cii ? "ApplicableTradeSettlementFinancialCard" : "CardAccount") is not null;
        // the direct debit of this payment means: in CII the mandate and creditor identifier of the settlement or its debited account
        static bool Debit(Syntax s, InvoiceElement m) => s.Cii
            ? HasDirectDebitOf(s, m)
            : m.Child(Cac, "PaymentMandate") is not null;
        static bool IsTransfer(string? code) => code is "30" or "58";
        static bool IsCard(string? code) => code is "48" or "54" or "55";
        static bool IsDebit(string? code) => code == "59";

        yield return X("BR-DE-23-a", F, "Eine Überweisung (Code 30, 58) hat keine Kontoangaben (BG-17).", "A credit transfer (code 30, 58) has no credit transfer information (BG-17).",
            c => Find(c, IsTransfer, (s, m) => !Transfer(s, m)));
        yield return X("BR-DE-23-b", F, "Eine Überweisung (Code 30, 58) hat Kartenangaben (BG-18) oder eine Lastschrift (BG-19).", "A credit transfer (code 30, 58) has card information (BG-18) or a direct debit (BG-19).",
            c => Find(c, IsTransfer, (s, m) => Card(s, m) || Debit(s, m)));
        yield return X("BR-DE-24-a", F, "Eine Kartenzahlung (Code 48, 54, 55) hat keine Kartenangaben (BG-18).", "A card payment (code 48, 54, 55) has no card information (BG-18).",
            c => Find(c, IsCard, (s, m) => !Card(s, m)));
        yield return X("BR-DE-24-b", F, "Eine Kartenzahlung (Code 48, 54, 55) hat Kontoangaben (BG-17) oder eine Lastschrift (BG-19).", "A card payment (code 48, 54, 55) has credit transfer information (BG-17) or a direct debit (BG-19).",
            c => Find(c, IsCard, (s, m) => Transfer(s, m) || Debit(s, m)));
        yield return X("BR-DE-25-a", F, "Eine Lastschrift (Code 59) hat keine Lastschriftangaben (BG-19).", "A direct debit (code 59) has no direct debit information (BG-19).",
            c => Find(c, IsDebit, (s, m) => !Debit(s, m)));
        yield return X("BR-DE-25-b", F, "Eine Lastschrift (Code 59) hat Kontoangaben (BG-17) oder Kartenangaben (BG-18).", "A direct debit (code 59) has credit transfer information (BG-17) or card information (BG-18).",
            c => Find(c, IsDebit, (s, m) => Transfer(s, m) || Card(s, m)
                || s.Cii && (m.Child(Ram, "PayeeSpecifiedCreditorFinancialInstitution") is not null || m.Child(Ram, "PayerSpecifiedDebtorFinancialInstitution") is not null)));
    }

    // the settlement part once per invoice (FG 838): asked per payment means it cost payment means × payment terms
    private static bool HasDirectDebitOf(Syntax s, InvoiceElement means) =>
        s.SettlementDebit || means.Child(Ram, "PayerPartyDebtorFinancialAccount")?.Child(Ram, "IBANID") is not null;

    /// <summary>
    /// BR-DE-18: every line of the first payment terms that begins with „#“ is a cash discount of the form of the specification, and after the
    /// last one follows a line end (space before it allowed).
    /// </summary>
    private static bool SkontoFits(Syntax s)
    {
        var terms = s.Cii
            ? s.Settlement?.ChildrenNamed(Ram, "SpecifiedTradePaymentTerms").FirstOrDefault()?.Child(Ram, "Description")?.Text
            : s.Root.ChildrenNamed(Cac, "PaymentTerms").FirstOrDefault()?.Child(Cbc, "Note")?.Text;
        if (terms is null) return true;
        var lines = terms.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => Collapse(l).StartsWith('#')).ToList();
        if (lines.Count == 0) return true;
        if (!lines.All(l => Fits(SkontoLine, Collapse(l)))) return false;
        // after the last entry („#…#“ within one piece between line breaks, from its first to its last „#“) the text must begin with a line
        // end, space before it allowed
        var end = -1;
        var hashes = 0;
        for (var i = 0; i < terms.Length; i++)
        {
            if (terms[i] is '\r' or '\n')
            {
                hashes = 0;
                continue;
            }
            if (terms[i] == '#' && ++hashes >= 2) end = i + 1;
        }
        var tail = end < 0 ? terms : terms[end..];
        var at = 0;
        while (at < tail.Length && tail[at] is ' ' or '\t' or '\r') at++;
        return at < tail.Length && tail[at] == '\n';
    }

    /// <summary>BR-DE-22: two attachments among siblings with the same file name — without regard to case, as the specification says.</summary>
    private static bool DuplicateFileNames(Syntax s)
    {
        var parents = new List<InvoiceElement>();
        var pending = new Stack<InvoiceElement>();
        pending.Push(s.Root);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            if (e.Children.Any(ch => ch.Local is "AdditionalReferencedDocument" or "AdditionalDocumentReference")) parents.Add(e);
            foreach (var ch in e.Children) pending.Push(ch);
        }
        foreach (var parent in parents)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var document in parent.Children.Where(ch => ch.Local is "AdditionalReferencedDocument" or "AdditionalDocumentReference"))
            {
                var name = s.Cii
                    ? document.Child(Ram, "AttachmentBinaryObject")?.Attribute("filename")
                    : document.Child(Cac, "Attachment")?.Child(Cbc, "EmbeddedDocumentBinaryObject")?.Attribute("filename");
                if (name is not null && !names.Add(name)) return true;
            }
        }
        return false;
    }

    /// <summary>The rules of Peppol BIS Billing 3.0 that XRechnung 3.0 took over (PEPPOL-EN16931-R…).</summary>
    private static IEnumerable<RuleCheck> PeppolRules()
    {
        const InvoiceRuleSeverity F = InvoiceRuleSeverity.Fatal, W = InvoiceRuleSeverity.Warning;
        yield return X("PEPPOL-EN16931-R001", F, "Die Prozesskennung (BT-23) fehlt.", "The business process type (BT-23) is missing.",
            c => c.Root.AllFields().Any(f => f.Id == "BT-23" && !string.IsNullOrWhiteSpace(f.Text)) || ProcessElement(SyntaxOf(c)) ? [] : [(c.First("BG-2") ?? c.Root, null)]);
        yield return X("PEPPOL-EN16931-R005", F, "Die Buchungswährung (BT-6) ist gleich der Rechnungswährung (BT-5).", "The VAT accounting currency (BT-6) equals the invoice currency (BT-5).",
            c => Has(c.Root, "BT-6") && Collapse(Text(c.Root, "BT-6")) == Collapse(Text(c.Root, "BT-5")) ? [(c.Root, c.Root.Field("BT-6"))] : []);
        yield return X("PEPPOL-EN16931-R008", F, "Die Rechnung enthält ein leeres Element.", "The invoice contains an empty element.",
            c => EmptyElements(SyntaxOf(c)).Select(e => (c.GroupOf(e), (InvoiceField?)null)));
        yield return X("PEPPOL-EN16931-R010", F, "Die elektronische Adresse des Käufers (BT-49) fehlt.", "The buyer electronic address (BT-49) is missing.",
            c => PartyElement(SyntaxOf(c), buyer: true) is { } p && !HasEndpoint(SyntaxOf(c), p) ? [(c.First("BG-7") ?? c.Root, null)] : []);
        yield return X("PEPPOL-EN16931-R020", F, "Die elektronische Adresse des Verkäufers (BT-34) fehlt.", "The seller electronic address (BT-34) is missing.",
            c => PartyElement(SyntaxOf(c), buyer: false) is { } p && !HasEndpoint(SyntaxOf(c), p) ? [(c.First("BG-4") ?? c.Root, null)] : []);
        yield return X("PEPPOL-EN16931-R040", F, "Ein Nachlass oder Zuschlag ist nicht Basisbetrag × Prozentsatz / 100 (auf 0,02 genau).",
            "An allowance or charge is not base amount × percentage / 100 (to 0.02).",
            c => AllowanceCharges(SyntaxOf(c)).Where(a => a.Base is { } b && a.Percent is { } p && Math.Abs((a.Amount ?? 0) - b * p / 100) > Slack(c))
                .Select(a => (c.GroupOf(a.Element), (InvoiceField?)null)));
        yield return X("PEPPOL-EN16931-R041", F, "Ein Nachlass oder Zuschlag hat einen Prozentsatz, aber keinen Basisbetrag.", "An allowance or charge has a percentage but no base amount.",
            c => AllowanceCharges(SyntaxOf(c)).Where(a => a.HasPercent && !a.HasBase).Select(a => (c.GroupOf(a.Element), (InvoiceField?)null)));
        yield return X("PEPPOL-EN16931-R042", F, "Ein Nachlass oder Zuschlag hat einen Basisbetrag, aber keinen Prozentsatz.", "An allowance or charge has a base amount but no percentage.",
            c => AllowanceCharges(SyntaxOf(c)).Where(a => a.HasBase && !a.HasPercent).Select(a => (c.GroupOf(a.Element), (InvoiceField?)null)));
        yield return X("PEPPOL-EN16931-R043", F, "Das Kennzeichen Nachlass/Zuschlag ist nicht genau „true“ oder „false“.", "The allowance/charge indicator is not exactly „true“ or „false“.",
            c => IndicatorsNotTrueOrFalse(SyntaxOf(c)).Select(e => (c.GroupOf(e), (InvoiceField?)null)));
        yield return X("PEPPOL-EN16931-R044", F, "Am Preis ist ein Zuschlag angegeben; dort ist nur ein Nachlass erlaubt.", "A charge is given on the price; only an allowance is allowed there.",
            c => PriceChargesNotAllowances(SyntaxOf(c)).Select(e => (c.GroupOf(e), (InvoiceField?)null)));
        yield return X("PEPPOL-EN16931-R046", F, "Der Nettopreis (BT-146) ist nicht Bruttopreis (BT-148) minus Rabatt (BT-147).", "The item net price (BT-146) is not gross price (BT-148) minus price discount (BT-147).",
            c => NetPriceNotGrossMinusDiscount(SyntaxOf(c)).Select(e => (c.GroupOf(e), (InvoiceField?)null)));
        yield return X("PEPPOL-EN16931-R053", F, "Die Rechnung hat mehr als einen Steuerbetrag in der Rechnungswährung (BT-110).", "The invoice has more than one VAT total in the invoice currency (BT-110).",
            c => TaxTotalsInInvoiceCurrency(c, SyntaxOf(c)) ? [] : [(c.First("BG-22") ?? c.Root, null)]);
        yield return X("PEPPOL-EN16931-R054", F, "Ein Steuerbetrag in Buchungswährung (BT-111) fehlt, wo BT-6 angegeben ist, oder steht ohne BT-6.",
            "A VAT total in accounting currency (BT-111) is missing where BT-6 is given, or is given without BT-6.",
            c => TaxTotalsInAccountingCurrency(c, SyntaxOf(c)) ? [] : [(c.First("BG-22") ?? c.Root, null)]);
        // with an accounting currency and a VAT total, a missing total in accounting currency has no sign that fits either (both artefacts so)
        yield return X("PEPPOL-EN16931-R055", F, "Umsatzsteuer gesamt (BT-110) und in Buchungswährung (BT-111) haben verschiedene Vorzeichen (oder BT-111 fehlt).",
            "The VAT total (BT-110) and the VAT total in accounting currency (BT-111) have different signs (or BT-111 is missing).",
            c => Has(c.Root, "BT-6") && c.First("BG-22") is { } t && Number(t, "BT-110") is { } a
                 && (Number(t, "BT-111") is not { } b || a < 0 && b > 0 || a > 0 && b < 0)
                ? [(t, t.Field("BT-111"))] : []);
        yield return X("PEPPOL-EN16931-R061", F, "Bei Lastschrift (Code 49, 59) fehlt die Mandatsreferenz (BT-89).", "With a direct debit (code 49, 59) the mandate reference (BT-89) is missing.",
            c => SyntaxOf(c) is var s ? s.PaymentMeans.Where(m => s.Code(m) is "49" or "59" && !(s.Cii
                    ? s.HasMandate
                    : m.Child(Cac, "PaymentMandate")?.Child(Cbc, "ID") is not null)).Select(m => (c.GroupOf(m), (InvoiceField?)null)) : []);
        yield return X("PEPPOL-EN16931-R101", F, "Ein Verweis einer Position ist nicht die Objektkennung (Typ 130).", "A document reference of a line is not the invoiced object identifier (type 130).",
            c => SyntaxOf(c) is var s ? s.Lines.Where(l => LineReferences(s, l) is { Count: > 0 } refs && !refs.Contains("130")).Select(l => (c.GroupOf(l), (InvoiceField?)null)) : []);
        yield return X("PEPPOL-EN16931-R110", F, "Der Positionszeitraum beginnt (BT-134) vor dem Rechnungszeitraum (BT-73).", "A line period starts (BT-134) before the invoicing period (BT-73).",
            c => LinePeriodsOutside(c, SyntaxOf(c), start: true));
        yield return X("PEPPOL-EN16931-R111", F, "Der Positionszeitraum endet (BT-135) nach dem Rechnungszeitraum (BT-74).", "A line period ends (BT-135) after the invoicing period (BT-74).",
            c => LinePeriodsOutside(c, SyntaxOf(c), start: false));
        yield return X("PEPPOL-EN16931-R120", W, "Der Nettobetrag einer Position (BT-131) ist nicht Menge × Nettopreis / Basismenge + Zuschläge − Nachlässe (auf 0,02 genau).",
            "An invoice line net amount (BT-131) is not quantity × net price / base quantity + charges − allowances (to 0.02).",
            c => LinesNotAddingUp(c, SyntaxOf(c)));
        yield return X("PEPPOL-EN16931-R121", F, "Die Basismenge des Preises (BT-149) ist nicht größer null.", "The item price base quantity (BT-149) is not above zero.",
            c => BaseQuantities(SyntaxOf(c)).Where(q => !(DecimalOf(q) > 0)).Select(q => (c.GroupOf(q), c.FieldOf(q))));
        yield return X("PEPPOL-EN16931-R130", F, "Die Einheit der Basismenge (BT-150) ist nicht die der Menge (BT-130).", "The unit of the item price base quantity (BT-150) is not that of the invoiced quantity (BT-130).",
            c => BaseQuantityUnitsDiffer(SyntaxOf(c)).Select(q => (c.GroupOf(q), c.FieldOf(q))));
    }

    private static bool ProcessElement(Syntax s) => s.Cii
        ? s.Root.Child(Rsm, "ExchangedDocumentContext")?.Child(Ram, "BusinessProcessSpecifiedDocumentContextParameter")?.Child(Ram, "ID") is not null
        : s.Root.Child(Cbc, "ProfileID") is not null;

    private static InvoiceElement? PartyElement(Syntax s, bool buyer) => s.Cii
        ? s.Transaction?.Child(Ram, "ApplicableHeaderTradeAgreement")?.Child(Ram, buyer ? "BuyerTradeParty" : "SellerTradeParty")
        : s.Root.Child(Cac, buyer ? "AccountingCustomerParty" : "AccountingSupplierParty")?.Child(Cac, "Party");

    private static bool HasEndpoint(Syntax s, InvoiceElement party) => s.Cii
        ? party.Child(Ram, "URIUniversalCommunication")?.Child(Ram, "URIID") is not null
        : party.Child(Cbc, "EndpointID") is not null;

    /// <summary>
    /// PEPPOL-EN16931-R008: elements without children and without text (attributes do not count); an attachment without content is empty too —
    /// written open and closed it holds no bytes, as one tag none at all (FG 849). CII may leave the delivery empty, UBL the order reference.
    /// </summary>
    private static IEnumerable<InvoiceElement> EmptyElements(Syntax s)
    {
        var pending = new Stack<InvoiceElement>();
        pending.Push(s.Root);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            if (e.Children.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(e.Text) && e.Binary is not { Length: > 0 }
                    && !(s.Cii ? e.Local == "ApplicableHeaderTradeDelivery" && e.Namespace == Ram : e.Local == "ID" && e.Namespace == Cbc && e.Parent?.Local == "OrderReference"))
                    yield return e;
                continue;
            }
            for (var i = e.Children.Count - 1; i >= 0; i--) pending.Push(e.Children[i]);
        }
    }

    private sealed record AllowanceCharge(InvoiceElement Element, decimal? Amount, decimal? Base, decimal? Percent, bool HasBase, bool HasPercent);

    /// <summary>The allowances and charges of the document and of the lines (not those of the price).</summary>
    private static IEnumerable<AllowanceCharge> AllowanceCharges(Syntax s)
    {
        IEnumerable<InvoiceElement> elements = s.Cii
            ? (s.Settlement?.ChildrenNamed(Ram, "SpecifiedTradeAllowanceCharge") ?? [])
                .Concat(s.Lines.SelectMany(l => l.Child(Ram, "SpecifiedLineTradeSettlement")?.ChildrenNamed(Ram, "SpecifiedTradeAllowanceCharge") ?? []))
            : s.Root.ChildrenNamed(Cac, "AllowanceCharge").Concat(s.Lines.SelectMany(l => l.ChildrenNamed(Cac, "AllowanceCharge")));
        foreach (var e in elements)
        {
            var amount = s.Cii ? e.Child(Ram, "ActualAmount") : e.Child(Cbc, "Amount");
            var basis = s.Cii ? e.Child(Ram, "BasisAmount") : e.Child(Cbc, "BaseAmount");
            var percent = s.Cii ? e.Child(Ram, "CalculationPercent") : e.Child(Cbc, "MultiplierFactorNumeric");
            yield return new AllowanceCharge(e, DecimalOf(amount), DecimalOf(basis), DecimalOf(percent), basis is not null, percent is not null);
        }
    }

    private static IEnumerable<InvoiceElement> IndicatorsNotTrueOrFalse(Syntax s)
    {
        if (s.Cii)
        {
            var pending = new Stack<InvoiceElement>();
            pending.Push(s.Root);
            while (pending.Count > 0)
            {
                var e = pending.Pop();
                if (e.Namespace == Ram && e.Local is "SpecifiedTradeAllowanceCharge" or "AppliedTradeAllowanceCharge"
                    && Collapse(e.Child(Ram, "ChargeIndicator")?.Child(Udt, "Indicator")?.Text) is not ("true" or "false"))
                    yield return e;
                foreach (var ch in e.Children) pending.Push(ch);
            }
            yield break;
        }
        foreach (var a in s.Root.ChildrenNamed(Cac, "AllowanceCharge").Concat(s.Lines.SelectMany(l => l.ChildrenNamed(Cac, "AllowanceCharge"))))
            if (Collapse(a.Child(Cbc, "ChargeIndicator")?.Text) is not ("true" or "false"))
                yield return a;
    }

    /// <summary>PEPPOL-EN16931-R044: CII the gross price of every line, UBL every price (also of a sub line, as the artefacts look) — a charge is not allowed there.</summary>
    private static IEnumerable<InvoiceElement> PriceChargesNotAllowances(Syntax s)
    {
        if (!s.Cii)
        {
            foreach (var a in s.Prices.SelectMany(p => p.ChildrenNamed(Cac, "AllowanceCharge")))
                if (Collapse(a.Child(Cbc, "ChargeIndicator")?.Text) != "false") yield return a;
            yield break;
        }
        foreach (var line in s.Lines)
        {
            var gross = line.Child(Ram, "SpecifiedLineTradeAgreement")?.Child(Ram, "GrossPriceProductTradePrice");
            var applied = gross?.ChildrenNamed(Ram, "AppliedTradeAllowanceCharge").ToList() ?? [];
            if (gross is not null && applied.Any(a => a.Child(Ram, "ActualAmount") is not null)
                && !applied.Any(a => a.Child(Ram, "ChargeIndicator")?.Child(Udt, "Indicator")?.Text == "false"))
                yield return gross;
        }
    }

    /// <summary>PEPPOL-EN16931-R046: net price = gross price − discount; CII per line, UBL per price (also of a sub line).</summary>
    private static IEnumerable<InvoiceElement> NetPriceNotGrossMinusDiscount(Syntax s)
    {
        if (!s.Cii)
        {
            foreach (var price in s.Prices)
                foreach (var a in price.ChildrenNamed(Cac, "AllowanceCharge"))
                    if (a.Child(Cbc, "BaseAmount") is { } basis && DecimalOf(price.Child(Cbc, "PriceAmount")) != DecimalOf(basis) - DecimalOf(a.Child(Cbc, "Amount")))
                        yield return a;
            yield break;
        }
        foreach (var line in s.Lines)
        {
            var agreement = line.Child(Ram, "SpecifiedLineTradeAgreement");
            var gross = agreement?.Child(Ram, "GrossPriceProductTradePrice");
            if (gross?.Child(Ram, "ChargeAmount") is not { } grossAmount) continue;
            var discount = DecimalOf(gross.ChildrenNamed(Ram, "AppliedTradeAllowanceCharge").FirstOrDefault()?.Child(Ram, "ActualAmount")) ?? 0;
            if (DecimalOf(agreement!.Child(Ram, "NetPriceProductTradePrice")?.Child(Ram, "ChargeAmount")) != DecimalOf(grossAmount) - discount) yield return gross;
        }
    }

    /// <summary>PEPPOL-EN16931-R053: CII at most one VAT total in the invoice currency; UBL exactly one tax total with subtotals.</summary>
    private static bool TaxTotalsInInvoiceCurrency(Context c, Syntax s)
    {
        if (!s.Cii) return s.Root.ChildrenNamed(Cac, "TaxTotal").Count(t => t.Child(Cac, "TaxSubtotal") is not null) == 1;
        var currency = s.Settlement?.Child(Ram, "InvoiceCurrencyCode")?.Text;
        return currency is null || TaxTotalAmounts(s).Count(t => t.Attribute("currencyID") is { } id && id == currency) <= 1;
    }

    /// <summary>
    /// PEPPOL-EN16931-R054: one VAT total in another currency (or tax total without subtotals) exactly when BT-6 is given. Compared, as
    /// XPath compares, only where both are there: a total without currency, or an invoice without invoice currency, counts as no other one.
    /// </summary>
    private static bool TaxTotalsInAccountingCurrency(Context c, Syntax s)
    {
        var expected = (s.Cii ? s.Settlement?.Child(Ram, "TaxCurrencyCode") : s.Root.Child(Cbc, "TaxCurrencyCode")) is null ? 0 : 1;
        if (!s.Cii) return s.Root.ChildrenNamed(Cac, "TaxTotal").Count(t => t.Child(Cac, "TaxSubtotal") is null) == expected;
        var currency = s.Settlement?.Child(Ram, "InvoiceCurrencyCode")?.Text;
        var others = currency is null ? 0 : TaxTotalAmounts(s).Count(t => t.Attribute("currencyID") is { } id && id != currency);
        return others == expected;
    }

    private static IEnumerable<InvoiceElement> TaxTotalAmounts(Syntax s) =>
        s.Settlement?.Child(Ram, "SpecifiedTradeSettlementHeaderMonetarySummation")?.ChildrenNamed(Ram, "TaxTotalAmount") ?? [];

    private static List<string> LineReferences(Syntax s, InvoiceElement line) => s.Cii
        ? [.. (line.Child(Ram, "SpecifiedLineTradeSettlement")?.ChildrenNamed(Ram, "AdditionalReferencedDocument") ?? []).Select(d => ChildText(d, "TypeCode") ?? "")]
        : [.. line.ChildrenNamed(Cac, "DocumentReference").Select(d => ChildText(d, "DocumentTypeCode") ?? "")];

    /// <summary>PEPPOL-EN16931-R110/R111: where the invoicing period has a start (end), no line period starts before it (ends after it).</summary>
    private static IEnumerable<(InvoiceGroup, InvoiceField?)> LinePeriodsOutside(Context c, Syntax s, bool start)
    {
        string? Date(InvoiceElement? period) => s.Cii
            ? period?.Child(Ram, start ? "StartDateTime" : "EndDateTime")?.Child(Udt, "DateTimeString")?.Text
            : period?.Child(Cbc, start ? "StartDate" : "EndDate")?.Text;
        var invoice = Date(s.Cii ? s.Settlement?.Child(Ram, "BillingSpecifiedPeriod") : s.Root.Child(Cac, "InvoicePeriod"));
        if (invoice is null || InvoiceFormat.Date(invoice) is not { } limit) yield break;
        foreach (var line in s.Lines)
        {
            var period = s.Cii ? line.Child(Ram, "SpecifiedLineTradeSettlement")?.Child(Ram, "BillingSpecifiedPeriod") : line.Child(Cac, "InvoicePeriod");
            if (InvoiceFormat.Date(Date(period) ?? "") is { } d && (start ? d < limit : d > limit)) yield return (c.GroupOf(period!), null);
        }
    }

    /// <summary>PEPPOL-EN16931-R120: net amount of a line against quantity × net price / base quantity + charges − allowances (each sum to the cent).</summary>
    private static IEnumerable<(InvoiceGroup, InvoiceField?)> LinesNotAddingUp(Context c, Syntax s)
    {
        foreach (var line in s.Lines)
        {
            decimal amount, quantity, price, baseQuantity;
            IEnumerable<InvoiceElement> charges;
            if (s.Cii)
            {
                var settlement = line.Child(Ram, "SpecifiedLineTradeSettlement");
                var net = line.Child(Ram, "SpecifiedLineTradeAgreement")?.Child(Ram, "NetPriceProductTradePrice");
                amount = DecimalOf(settlement?.Child(Ram, "SpecifiedTradeSettlementLineMonetarySummation")?.Child(Ram, "LineTotalAmount")) ?? 0;
                quantity = DecimalOf(line.Child(Ram, "SpecifiedLineTradeDelivery")?.Child(Ram, "BilledQuantity")) ?? 1;
                price = DecimalOf(net?.Child(Ram, "ChargeAmount")) ?? 0;
                // BT-149 may stand at the net or at the gross price in CII (BR-TMP-3 asks them to be equal)
                var gross = line.Child(Ram, "SpecifiedLineTradeAgreement")?.Child(Ram, "GrossPriceProductTradePrice");
                baseQuantity = DecimalOf(net?.Child(Ram, "BasisQuantity")) is { } b && b != 0 ? b
                    : DecimalOf(gross?.Child(Ram, "BasisQuantity")) is { } g && g != 0 ? g : 1;
                charges = settlement?.ChildrenNamed(Ram, "SpecifiedTradeAllowanceCharge") ?? [];
            }
            else
            {
                amount = DecimalOf(line.Child(Cbc, "LineExtensionAmount")) ?? 0;
                quantity = DecimalOf(line.Child(Cbc, line.Local == "InvoiceLine" ? "InvoicedQuantity" : "CreditedQuantity")) ?? 1;
                price = DecimalOf(line.Child(Cac, "Price")?.Child(Cbc, "PriceAmount")) ?? 0;
                baseQuantity = DecimalOf(line.Child(Cac, "Price")?.Child(Cbc, "BaseQuantity")) is { } b && b != 0 ? b : 1;
                charges = line.ChildrenNamed(Cac, "AllowanceCharge");
            }
            decimal allowances = 0, added = 0;
            foreach (var a in charges)
            {
                var indicator = Collapse(s.Cii ? a.Child(Ram, "ChargeIndicator")?.Child(Udt, "Indicator")?.Text : a.Child(Cbc, "ChargeIndicator")?.Text);
                var value = DecimalOf(s.Cii ? a.Child(Ram, "ActualAmount") : a.Child(Cbc, "Amount")) ?? 0;
                if (indicator == "false") allowances += value;
                else if (indicator == "true") added += value;
            }
            if (Math.Abs(amount - (quantity * (price / baseQuantity) + Cents(added) - Cents(allowances))) > Slack(c)) yield return (c.GroupOf(line), null);
        }
    }

    private static IEnumerable<InvoiceElement> BaseQuantities(Syntax s) => s.Lines.SelectMany(line => BaseQuantitiesOf(s, line));

    /// <summary>The base quantities of the prices of one line (CII net and gross price, UBL the price).</summary>
    private static IEnumerable<InvoiceElement> BaseQuantitiesOf(Syntax s, InvoiceElement line) => s.Cii
        ? new[] { "NetPriceProductTradePrice", "GrossPriceProductTradePrice" }
            .Select(p => line.Child(Ram, "SpecifiedLineTradeAgreement")?.Child(Ram, p)?.Child(Ram, "BasisQuantity")).OfType<InvoiceElement>()
        : new[] { line.Child(Cac, "Price")?.Child(Cbc, "BaseQuantity") }.OfType<InvoiceElement>();

    /// <summary>
    /// PEPPOL-EN16931-R130: the unit of a base quantity is that of the quantity. CII per line; UBL every price (also of a sub line) against the
    /// quantity of the line it stands in — the invoiced one in an invoice, the credited one in a credit note — where that line has one, as
    /// the artefacts read it.
    /// </summary>
    private static IEnumerable<InvoiceElement> BaseQuantityUnitsDiffer(Syntax s)
    {
        if (!s.Cii)
        {
            var invoice = s.Root.Local == "Invoice";
            foreach (var price in s.Prices)
            {
                var line = price.Parent!;
                var has = line.Child(Cbc, "InvoicedQuantity") is not null || line.Child(Cbc, "CreditedQuantity") is not null;
                var quantity = line.Child(Cbc, invoice ? "InvoicedQuantity" : "CreditedQuantity");
                foreach (var q in price.ChildrenNamed(Cbc, "BaseQuantity"))
                    if (q.Attribute("unitCode") is { } unit && has && unit != quantity?.Attribute("unitCode")) yield return q;
            }
            yield break;
        }
        foreach (var line in s.Lines)
        {
            var quantity = line.Child(Ram, "SpecifiedLineTradeDelivery")?.Child(Ram, "BilledQuantity");
            foreach (var q in BaseQuantitiesOf(s, line))
                if (q.Attribute("unitCode") is { } unit && unit != quantity?.Attribute("unitCode")) yield return q;
        }
    }
}
