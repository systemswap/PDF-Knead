namespace PdfKnead.EInvoice;

/// <summary>
/// The further rules of XRechnung 3.0 (stage 4, part d2; KONZEPT.md, „Weg für Stufe 4, Teil d“): those of its extension (BR-DEX-01 to
/// BR-DEX-14, chapter 17.2 of the specification XRechnung 3.0.2, and BR-DEX-15), the temporary rules (BR-TMP-2 to BR-TMP-7, BR-DE-TMP-32)
/// and those of CVD, the invoices for clean vehicles (BR-DE-CVD-01 to BR-DE-CVD-06, BR-TMP-CVD-01). The temporary rules, BR-DEX-15 and the
/// rules of CVD stand in no specification text at hand: they are written from the validation artefacts of the KoSIT — the message of a rule
/// says what it asks, its test where and what exactly is checked — each in code of our own (docs/ENTSCHEIDUNGEN.md, stage 4, part d2).
/// In the extension BR-DEX-01 and BR-DEX-04 to -08 take the place of BR-CL-24, -10, -11, -21, -25 and -26, and in UBL BR-DEX-09 that of
/// BR-CO-16 (the specification says so); in CVD BR-TMP-CVD-01 takes that of BR-CL-13. The code walk of part c then checks those elements
/// against the wider list under the identifier of the rule that takes the place.
/// </summary>
public static partial class InvoiceRules
{
    private const string Qdt = "urn:un:unece:uncefact:data:standard:QualifiedDataType:100";

    private static readonly string[] DigaCodes = ["XR01", "XR02", "XR03"];

    private static RuleCheck XOnly(XRechnungKind kind, string id, InvoiceRuleSeverity severity, string german, string english,
        Func<Context, IEnumerable<(InvoiceGroup, InvoiceField?)>> find) =>
        new(new InvoiceRule(id, XRechnungSource, severity, german, english), c => KindOf(c) == kind ? find(c) : []);

    /// <summary>A rule of the extension or of CVD that the code walk finds (with the code as written).</summary>
    private static RuleCheck XCoded(XRechnungKind kind, string id, string german, string english) =>
        new(new InvoiceRule(id, XRechnungSource, InvoiceRuleSeverity.Fatal, german, english), _ => [],
            c => KindOf(c) == kind && c.CodeFindings.TryGetValue(id, out var list) ? list.Select(f => (c.GroupOf(f.Element), c.FieldOf(f.Element), f.Value)) : []);

    /// <summary>
    /// The code checks of an invoice of the extension or of CVD: the rules whose place another one takes are asked under its identifier, with
    /// its wider list — the extension adds the codes XR01 to XR03 of digital health applications (chapter 17.3) and the MIME code
    /// application/xml (BR-DEX-01), CVD the scheme „CVD“ of the vehicle category, on the item classification of a line (BR-TMP-CVD-01).
    /// </summary>
    private static CodeCheck[] XRechnungCodeChecks(CodeCheck[] checks, bool cii, XRechnungKind kind) => kind switch
    {
        XRechnungKind.Extension =>
        [
            .. checks.Select(check => check.Rule switch
            {
                "BR-CL-10" => check with { Rule = "BR-DEX-04", Allowed = WithDiga(check.Allowed) },
                "BR-CL-11" => check with { Rule = "BR-DEX-05", Allowed = WithDiga(check.Allowed) },
                "BR-CL-21" => check with { Rule = "BR-DEX-06", Allowed = WithDiga(check.Allowed) },
                "BR-CL-25" => check with { Rule = "BR-DEX-07", Allowed = WithDiga(check.Allowed) },
                "BR-CL-26" => check with { Rule = "BR-DEX-08", Allowed = WithDiga(check.Allowed) },
                // the rule of the extension asks every attachment for its MIME code: one without has none of the list
                "BR-CL-24" => check with { Rule = "BR-DEX-01", OnlyWithAttribute = false, Allowed = (e, value) => Mime(e, value) || value == "application/xml" },
                _ => check,
            }),
        ],
        XRechnungKind.Cvd =>
        [
            .. checks.Select(check => check.Rule == "BR-CL-13"
                ? check with { Rule = "BR-TMP-CVD-01", Context = e => OnLineItem(e, cii), Allowed = (_, value) => IsIn("Item", value) || IsOneOf(value, "CVD") }
                : check),
        ],
        _ => checks,
    };

    private static Func<InvoiceElement, string?, bool> WithDiga(Func<InvoiceElement, string?, bool> allowed) =>
        (e, value) => allowed(e, value) || IsOneOf(value, DigaCodes);

    /// <summary>Whether a classification code is that of the item of a line of the invoice itself (CII: of a line, UBL: not of a sub line).</summary>
    private static bool OnLineItem(InvoiceElement code, bool cii)
    {
        var classification = code.Parent;
        var item = classification?.Parent;
        var line = item?.Parent;
        var under = line?.Parent;
        return cii
            ? classification is { Local: "DesignatedProductClassification", Namespace: Ram } && item is { Local: "SpecifiedTradeProduct", Namespace: Ram }
              && line is { Local: "IncludedSupplyChainTradeLineItem", Namespace: Ram } && under is { Local: "SupplyChainTradeTransaction", Namespace: Rsm, Parent.Parent: null }
            : classification is { Local: "CommodityClassification", Namespace: Cac } && item is { Local: "Item", Namespace: Cac }
              && line is { Local: "InvoiceLine" or "CreditNoteLine", Namespace: Cac } && under is { Parent: null };
    }

    /// <summary>
    /// Whether BR-DEX-09 takes the place of BR-CO-16: in the extension, in UBL — CII has no third party payments (chapter 18 of the
    /// specification: the extension is bound to UBL), there BR-CO-16 stays, as in the artefacts.
    /// </summary>
    private static bool DueAmountByExtension(Context c) => c.Invoice.Kind.Syntax != InvoiceSyntax.Cii && KindOf(c) == XRechnungKind.Extension;

    /// <summary>Every element below the root and the root itself, in document order (own stack, any depth).</summary>
    private static IEnumerable<InvoiceElement> Descendants(InvoiceElement root)
    {
        var pending = new Stack<InvoiceElement>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            yield return e;
            for (var i = e.Children.Count - 1; i >= 0; i--) pending.Push(e.Children[i]);
        }
    }

    private static IEnumerable<InvoiceElement> Down(IEnumerable<InvoiceElement> from, string ns, string local) => from.SelectMany(e => e.ChildrenNamed(ns, local));

    private static IEnumerable<InvoiceElement> Down(InvoiceElement? from, string ns, string local) => from?.ChildrenNamed(ns, local) ?? [];

    /// <summary>A value that is there once space is collapsed (XPath boolean(normalize-space(.))).</summary>
    private static bool Given(InvoiceElement e) => Collapse(e.Text).Length > 0;

    /// <summary>The term an element is, or else the term its parent is (a date of CII is the term's child).</summary>
    private static InvoiceField? FieldNear(Context c, InvoiceElement e) => c.FieldOf(e) ?? (e.Parent is { } p ? c.FieldOf(p) : null);

    // ------------------------------------------------------------ the extension (BR-DEX)

    private static IEnumerable<RuleCheck> ExtensionRules()
    {
        const XRechnungKind E = XRechnungKind.Extension;
        const InvoiceRuleSeverity F = InvoiceRuleSeverity.Fatal, W = InvoiceRuleSeverity.Warning;
        yield return XCoded(E, "BR-DEX-01", "Der MIME-Typ eines Anhangs (BT-125) ist keiner der in der Erweiterung erlaubten (PDF, PNG, JPEG, CSV, XLSX, ODS, XML).",
            "The MIME code of an attachment (BT-125) is none of those allowed in the extension (PDF, PNG, JPEG, CSV, XLSX, ODS, XML).");
        yield return XOnly(E, "BR-DEX-02", W, "Der Nettobetrag einer Position oder Unterposition (BT-131) ist nicht die Summe der Nettobeträge ihrer Unterpositionen.",
            "The net amount of a line or sub line (BT-131) is not the sum of the net amounts of its sub lines.",
            c => c.Invoice.Kind.Syntax == InvoiceSyntax.UblInvoice ? SubLinesNotAddingUp(SyntaxOf(c)).Select(e => (c.GroupOf(e), FieldNear(c, e.Child(Cbc, "LineExtensionAmount") ?? e))) : []);
        yield return XOnly(E, "BR-DEX-03", F, "Eine Unterposition hat nicht genau eine Umsatzsteuerangabe (BG-DEX-06).", "A sub line does not have exactly one VAT information (BG-DEX-06).",
            c => c.Invoice.Kind.Syntax == InvoiceSyntax.UblInvoice
                ? Down(SyntaxOf(c).SubInvoiceLines, Cac, "Item").Where(i => i.ChildrenNamed(Cac, "ClassifiedTaxCategory").Count() != 1).Select(i => (c.GroupOf(i), (InvoiceField?)null))
                : []);
        yield return XCoded(E, "BR-DEX-04", "Das Schema einer Kennung (BT-29, BT-46, BT-60) steht nicht in ISO/IEC 6523 und ist kein Code der Erweiterung (XR01–XR03; in UBL für Verkäufer und Zahlungsempfänger auch SEPA).",
            "The scheme of an identifier (BT-29, BT-46, BT-60) is not in ISO/IEC 6523 and no code of the extension (XR01–XR03; in UBL also SEPA for seller and payee).");
        yield return XCoded(E, "BR-DEX-05", "Das Schema einer Registernummer (BT-30, BT-47, BT-61) steht nicht in ISO/IEC 6523 und ist kein Code der Erweiterung (XR01–XR03).",
            "The scheme of a legal registration identifier (BT-30, BT-47, BT-61) is not in ISO/IEC 6523 and no code of the extension (XR01–XR03).");
        yield return XCoded(E, "BR-DEX-06", "Das Schema der Artikelkennung (BT-157) steht nicht in ISO/IEC 6523 und ist kein Code der Erweiterung (XR01–XR03).",
            "The scheme of the item standard identifier (BT-157) is not in ISO/IEC 6523 and no code of the extension (XR01–XR03).");
        yield return XCoded(E, "BR-DEX-07", "Das Schema einer elektronischen Adresse (BT-34, BT-49) steht nicht in der EAS-Liste und ist kein Code der Erweiterung (XR01–XR03).",
            "The scheme of an electronic address (BT-34, BT-49) is not in the EAS list and no code of the extension (XR01–XR03).");
        yield return XCoded(E, "BR-DEX-08", "Das Schema der Lieferortkennung (BT-71) steht nicht in ISO/IEC 6523 und ist kein Code der Erweiterung (XR01–XR03).",
            "The scheme of the deliver-to location identifier (BT-71) is not in ISO/IEC 6523 and no code of the extension (XR01–XR03).");
        yield return XOnly(E, "BR-DEX-09", F, "Der fällige Betrag (BT-115) ist nicht Gesamtbetrag (BT-112) − Vorauszahlung (BT-113) + Rundung (BT-114) + Summe der Fremdforderungen (BT-DEX-002).",
            "The amount due (BT-115) is not the total (BT-112) − paid amount (BT-113) + rounding (BT-114) + the sum of the third party payments (BT-DEX-002).",
            c => DueAmountByExtension(c) ? DueAmountsNotAddingUp(SyntaxOf(c)).Select(t => (c.GroupOf(t), FieldNear(c, t.Child(Cbc, "PayableAmount") ?? t))) : []);
        // a third party payment (BG-DEX-09) of a UBL invoice: its type, amount and description; the amount with at most two decimals and in
        // the invoice currency
        IEnumerable<(InvoiceGroup, InvoiceField?)> Payments(Context c, Func<Syntax, InvoiceElement, bool> wrong, bool amountField = false)
        {
            if (c.Invoice.Kind.Syntax != InvoiceSyntax.UblInvoice) return [];
            var s = SyntaxOf(c);
            return s.Root.ChildrenNamed(Cac, "PrepaidPayment").Where(p => wrong(s, p))
                .Select(p => (c.GroupOf(p), amountField && p.Child(Cbc, "PaidAmount") is { } a ? c.FieldOf(a) : null));
        }
        yield return XOnly(E, "BR-DEX-10", F, "Eine Fremdforderung (BG-DEX-09) hat keine Art (BT-DEX-001).", "A third party payment (BG-DEX-09) has no type (BT-DEX-001).",
            c => Payments(c, (_, p) => !p.ChildrenNamed(Cbc, "ID").Any(Given)));
        yield return XOnly(E, "BR-DEX-11", F, "Eine Fremdforderung (BG-DEX-09) hat keinen Betrag (BT-DEX-002).", "A third party payment (BG-DEX-09) has no amount (BT-DEX-002).",
            c => Payments(c, (_, p) => !p.ChildrenNamed(Cbc, "PaidAmount").Any(Given)));
        yield return XOnly(E, "BR-DEX-12", F, "Eine Fremdforderung (BG-DEX-09) hat keine Beschreibung (BT-DEX-003).", "A third party payment (BG-DEX-09) has no description (BT-DEX-003).",
            c => Payments(c, (_, p) => !p.ChildrenNamed(Cbc, "InstructionID").Any(Given)));
        // the digits after the first point as written (also a space after them counts, as in the artefacts)
        yield return XOnly(E, "BR-DEX-13", F, "Der Betrag einer Fremdforderung (BT-DEX-002) hat mehr als zwei Nachkommastellen.", "The amount of a third party payment (BT-DEX-002) has more than two decimals.",
            c => Payments(c, (_, p) => p.Child(Cbc, "PaidAmount")?.Text is { } t && t.IndexOf('.') is var dot and >= 0 && Runes(t[(dot + 1)..], 3) > 2, amountField: true));
        yield return XOnly(E, "BR-DEX-14", F, "Die Währung des Betrags einer Fremdforderung (BT-DEX-002) ist nicht die Rechnungswährung (BT-5).",
            "The currency of the amount of a third party payment (BT-DEX-002) is not the invoice currency (BT-5).",
            c => Payments(c, (s, p) => !p.ChildrenNamed(Cbc, "PaidAmount").Any(a => a.Attribute("currencyID") is { } currency
                && s.Root.ChildrenNamed(Cbc, "DocumentCurrencyCode").Any(d => (d.Text ?? "") == currency)), amountField: true));
        yield return XOnly(E, "BR-DEX-15", W, "Eine Position verweist auf eine übergeordnete (ParentLineID): Unterpositionen gibt es in XRechnung nur in UBL.",
            "A line refers to a parent line (ParentLineID): XRechnung has sub lines only in UBL.",
            c => c.Invoice.Kind.Syntax == InvoiceSyntax.Cii ? ParentLineReferences(SyntaxOf(c)).Select(e => (c.GroupOf(e), (InvoiceField?)null)).Distinct() : []);
    }

    /// <summary>The number of characters (code points, as XPath counts them) of a text, counted up to a limit.</summary>
    private static int Runes(string text, int limit)
    {
        var n = 0;
        foreach (var _ in text.EnumerateRunes())
            if (++n >= limit) break;
        return n;
    }

    /// <summary>
    /// BR-DEX-02: a line of the invoice or a sub line with sub lines whose net amount is not the sum of theirs (as written, in decimals; a sub
    /// line without one adds nothing, a parent without one has no amount that equals).
    /// </summary>
    private static IEnumerable<InvoiceElement> SubLinesNotAddingUp(Syntax s)
    {
        foreach (var parent in s.Root.ChildrenNamed(Cac, "InvoiceLine").Concat(s.SubInvoiceLines))
        {
            var subs = parent.ChildrenNamed(Cac, "SubInvoiceLine").ToList();
            if (subs.Count == 0) continue;
            var sum = subs.Sum(l => DecimalOf(l.Child(Cbc, "LineExtensionAmount")) ?? 0);
            if (DecimalOf(parent.Child(Cbc, "LineExtensionAmount")) != sum) yield return parent;
        }
    }

    /// <summary>
    /// BR-DEX-09: amount due = total with VAT − paid amount + rounding + the third party payments, without rounding (as BR-CO-16, whose place it
    /// takes); a missing amount due or total does not add up.
    /// </summary>
    private static IEnumerable<InvoiceElement> DueAmountsNotAddingUp(Syntax s)
    {
        var third = Down(s.Root.ChildrenNamed(Cac, "PrepaidPayment"), Cbc, "PaidAmount").Sum(a => DecimalOf(a) ?? 0);
        foreach (var totals in s.Root.ChildrenNamed(Cac, "LegalMonetaryTotal"))
        {
            var due = DecimalOf(totals.Child(Cbc, "PayableAmount"));
            var gross = DecimalOf(totals.Child(Cbc, "TaxInclusiveAmount"));
            var paid = DecimalOf(totals.Child(Cbc, "PrepaidAmount")) ?? 0;
            var rounding = DecimalOf(totals.Child(Cbc, "PayableRoundingAmount")) ?? 0;
            if (due is null || gross is null || due != gross - paid + rounding + third) yield return totals;
        }
    }

    /// <summary>BR-DEX-15: the references to a parent line of CII, where a line names its ParentLineID (as the artefacts: only with lines that have a line document).</summary>
    private static IEnumerable<InvoiceElement> ParentLineReferences(Syntax s)
    {
        if (!s.Lines.Any(l => l.Child(Ram, "AssociatedDocumentLineDocument") is not null)) return [];
        return Descendants(s.Root).Where(e => e.Local == "ParentLineID" && e.Namespace == Ram);
    }

    // ------------------------------------------------------------ the temporary rules (BR-TMP, BR-DE-TMP)

    private static IEnumerable<RuleCheck> TemporaryRules()
    {
        const InvoiceRuleSeverity F = InvoiceRuleSeverity.Fatal, W = InvoiceRuleSeverity.Warning;
        yield return X("BR-TMP-2", F, "Der Ort eines externen Dokuments (BT-124) ist keine absolute URL mit Schema (etwa „https:“).",
            "The external document location (BT-124) is no absolute URL with a scheme (such as „https:“).",
            c => ExternalLocationsWrong(c, SyntaxOf(c)));
        yield return X("BR-TMP-3", F, "Brutto- und Nettopreis einer Position haben nicht dieselbe Basismenge (BT-149, wie geschrieben) oder nicht dieselbe Einheit (BT-150).",
            "The gross and the net price of a line do not have the same base quantity (BT-149, as written) or not the same unit (BT-150).",
            c => SyntaxOf(c) is { Cii: true } s ? s.Lines.Where(l => !PriceBasesAlike(l)).Select(l => (c.GroupOf(l), (InvoiceField?)null)) : []);
        yield return X("BR-TMP-4", W, "Ein Anhang (BG-24) hat mehr als eine Beschreibung (BT-123).", "A supporting document (BG-24) has more than one description (BT-123).",
            c => SupportingDocuments(SyntaxOf(c)).Where(d => d.ChildrenNamed(Ram, "Name").Count() > 1).Select(d => (c.GroupOf(d), (InvoiceField?)null)));
        yield return X("BR-TMP-5", W, "Ein Anhang (BG-24) hat mehr als ein angehängtes Dokument (BT-125).", "A supporting document (BG-24) has more than one attached document (BT-125).",
            c => SupportingDocuments(SyntaxOf(c)).Where(d => d.ChildrenNamed(Ram, "AttachmentBinaryObject").Count() > 1).Select(d => (c.GroupOf(d), (InvoiceField?)null)));
        yield return X("BR-TMP-6", W, "Ein Datum hat nicht die Form JJJJ-MM-TT.", "A date does not have the form YYYY-MM-DD.",
            c => SyntaxOf(c) is { Cii: false } s
                ? Descendants(s.Root).Where(e => e.Namespace == Cbc && e.Local is "IssueDate" or "DueDate" or "StartDate" or "EndDate" or "ActualDeliveryDate"
                        or "TaxPointDate" or "PaymentDueDate" && !DateForm(Collapse(e.Text), dashes: true))
                    .Select(e => (c.GroupOf(e), FieldNear(c, e)))
                : []);
        yield return X("BR-TMP-7", W, "Ein Datum hat nicht das Format 102 (Attribut format=\"102\", JJJJMMTT).", "A date does not have the format 102 (attribute format=\"102\", YYYYMMDD).",
            c => SyntaxOf(c) is { Cii: true } s
                ? Descendants(s.Root).Where(e => (e.Local == "DateTimeString" && e.Namespace is Udt or Qdt || e.Local == "DateString" && e.Namespace == Udt)
                        && !(Collapse(e.Attribute("format")) == "102" && DateForm(Collapse(e.Text), dashes: false)))
                    .Select(e => (c.GroupOf(e), FieldNear(c, e)))
                : []);
        yield return X("BR-DE-TMP-32", InvoiceRuleSeverity.Information,
            "Die Rechnung nennt kein Liefer- oder Leistungsdatum: weder Lieferdatum (BT-72), Rechnungszeitraum (BG-14) noch in jeder Position einen Zeitraum (BG-26).",
            "The invoice states no date of delivery or service: neither the actual delivery date (BT-72), an invoicing period (BG-14) nor a period in every line (BG-26).",
            c => DeliveryDateStated(SyntaxOf(c)) ? [] : [(c.Root, null)]);
    }

    /// <summary>
    /// BR-TMP-2: an absolute URL as the pattern of the artefacts reads it — a letter, then at least one letter, digit, „+“, „.“ or „-“, then
    /// „:“ (a drive letter is no scheme); by hand, no pattern.
    /// </summary>
    private static bool AbsoluteUrl(string? value)
    {
        if (string.IsNullOrEmpty(value) || !char.IsAsciiLetter(value[0])) return false;
        var i = 1;
        while (i < value.Length && (char.IsAsciiLetterOrDigit(value[i]) || value[i] is '+' or '.' or '-')) i++;
        return i >= 2 && i < value.Length && value[i] == ':';
    }

    /// <summary>
    /// BR-TMP-2: CII the location (URIID) of a supporting document of the agreement, where it has one; UBL every external reference of an
    /// additional document — one without a location has none that is a URL either.
    /// </summary>
    private static IEnumerable<(InvoiceGroup, InvoiceField?)> ExternalLocationsWrong(Context c, Syntax s)
    {
        if (s.Cii)
        {
            foreach (var document in SupportingDocuments(s))
                foreach (var uri in document.ChildrenNamed(Ram, "URIID"))
                    if (!AbsoluteUrl(uri.Text)) yield return (c.GroupOf(document), c.FieldOf(uri));
            yield break;
        }
        foreach (var reference in Down(Down(s.Root.ChildrenNamed(Cac, "AdditionalDocumentReference"), Cac, "Attachment"), Cac, "ExternalReference"))
        {
            var uri = reference.Child(Cbc, "URI");
            if (!AbsoluteUrl(uri?.Text)) yield return (c.GroupOf(reference), uri is null ? null : c.FieldOf(uri));
        }
    }

    /// <summary>The supporting documents of CII (BG-24): the additional documents of the agreement with the type code 916 as written.</summary>
    private static IEnumerable<InvoiceElement> SupportingDocuments(Syntax s) => s.Cii
        ? Down(Down(s.Transaction, Ram, "ApplicableHeaderTradeAgreement"), Ram, "AdditionalReferencedDocument")
            .Where(d => d.ChildrenNamed(Ram, "TypeCode").Any(t => t.Text == "916"))
        : [];

    /// <summary>
    /// BR-TMP-3 (CII): where gross and net price both have a base quantity, one of them as written equals one of the other, and where both
    /// have a unit, one unit of the one equals one of the other (as the test of the artefacts compares: as text).
    /// </summary>
    private static bool PriceBasesAlike(InvoiceElement line)
    {
        var agreements = line.ChildrenNamed(Ram, "SpecifiedLineTradeAgreement").ToList();
        var gross = Down(Down(agreements, Ram, "GrossPriceProductTradePrice"), Ram, "BasisQuantity").ToList();
        var net = Down(Down(agreements, Ram, "NetPriceProductTradePrice"), Ram, "BasisQuantity").ToList();
        if (gross.Count == 0 || net.Count == 0) return true;
        if (!gross.Any(g => net.Any(n => (g.Text ?? "") == (n.Text ?? "")))) return false;
        var grossUnits = gross.Select(g => g.Attribute("unitCode")).OfType<string>().ToList();
        var netUnits = net.Select(n => n.Attribute("unitCode")).OfType<string>().ToList();
        return grossUnits.Count == 0 || netUnits.Count == 0 || grossUnits.Any(netUnits.Contains);
    }

    /// <summary>
    /// A date of the form the rule names: UBL „JJJJ-MM-TT“, CII „JJJJMMTT“ — with the digits 0 to 9 (the pattern of the artefacts, „\d“ of
    /// XPath, would also let digits of other scripts pass; the format means these, ENTSCHEIDUNGEN, stage 4, part d2).
    /// </summary>
    private static bool DateForm(string value, bool dashes) => dashes
        ? value.Length == 10 && value[4] == '-' && value[7] == '-' && value.Where((ch, i) => i is not (4 or 7)).All(char.IsAsciiDigit)
        : value.Length == 8 && value.All(char.IsAsciiDigit);

    /// <summary>
    /// BR-DE-TMP-32: an actual delivery date, an invoicing period, or a period in every line (as the artefacts ask: the elements are there;
    /// an invoice without lines has a period in every one).
    /// </summary>
    private static bool DeliveryDateStated(Syntax s)
    {
        if (s.Cii)
        {
            if (s.Transaction is not { } transaction) return true;   // the artefacts ask the transaction; without it there is nothing to ask
            return Down(Down(Down(transaction, Ram, "ApplicableHeaderTradeDelivery"), Ram, "ActualDeliverySupplyChainEvent"), Ram, "OccurrenceDateTime").Any()
                   || Down(Down(transaction, Ram, "ApplicableHeaderTradeSettlement"), Ram, "BillingSpecifiedPeriod").Any()
                   || s.Lines.All(l => Down(Down(l, Ram, "SpecifiedLineTradeSettlement"), Ram, "BillingSpecifiedPeriod").Any());
        }
        return Down(s.Root.ChildrenNamed(Cac, "Delivery"), Cbc, "ActualDeliveryDate").Any()
               || s.Root.Child(Cac, "InvoicePeriod") is not null
               || s.Lines.All(l => l.Child(Cac, "InvoicePeriod") is not null);
    }

    // ------------------------------------------------------------ CVD (BR-DE-CVD, BR-TMP-CVD)

    private static IEnumerable<RuleCheck> CvdRules()
    {
        const XRechnungKind V = XRechnungKind.Cvd;
        const InvoiceRuleSeverity F = InvoiceRuleSeverity.Fatal;
        yield return XOnly(V, "BR-DE-CVD-01", F, "Die Vertragsnummer (BT-12) fehlt (Rechnung über saubere Fahrzeuge, CVD).", "The contract reference (BT-12) is missing (invoice on clean vehicles, CVD).",
            c => SyntaxOf(c) is var s && s.Cii
                ? Down(s.Transaction, Ram, "ApplicableHeaderTradeAgreement").Where(a => !Down(Down(a, Ram, "ContractReferencedDocument"), Ram, "IssuerAssignedID").Any(Given))
                    .Select(a => (c.GroupOf(a), (InvoiceField?)null))
                : Down(Down(s.Root, Cac, "ContractDocumentReference"), Cbc, "ID").Any(Given) ? [] : [(c.Root, null)]);
        yield return XOnly(V, "BR-DE-CVD-02", F, "Die Vergabe- oder Losnummer (BT-17) fehlt (Rechnung über saubere Fahrzeuge, CVD).", "The tender or lot reference (BT-17) is missing (invoice on clean vehicles, CVD).",
            c => SyntaxOf(c) is var s && s.Cii
                ? Down(s.Transaction, Ram, "ApplicableHeaderTradeAgreement")
                    .Where(a => !a.ChildrenNamed(Ram, "AdditionalReferencedDocument").Any(d => Collapse(d.Child(Ram, "TypeCode")?.Text) == "50" && d.Child(Ram, "IssuerAssignedID") is { } id && Given(id)))
                    .Select(a => (c.GroupOf(a), (InvoiceField?)null))
                : Down(Down(s.Root, Cac, "OriginatorDocumentReference"), Cbc, "ID").Any(Given) ? [] : [(c.Root, null)]);
        yield return XOnly(V, "BR-DE-CVD-03", F, "Keine Position hat eine Fahrzeugklasse (BT-158 mit dem Schema „CVD“) zusammen mit dem Merkmal „cva“ (BT-160).",
            "No line has a vehicle category (BT-158 with the scheme „CVD“) together with the attribute „cva“ (BT-160).",
            c => SyntaxOf(c) is var s && (!s.Cii || s.Transaction is not null) && !LineItems(s).Any(i => VehicleCodes(s, i).Any() && CvaAttributes(s, i).Any()) ? [(c.Root, null)] : []);
        yield return XOnly(V, "BR-DE-CVD-04", F, "Eine Fahrzeugklasse (BT-158 mit dem Schema „CVD“) ist keine von M1, M2, M3, N1, N2, N3.",
            "A vehicle category (BT-158 with the scheme „CVD“) is none of M1, M2, M3, N1, N2, N3.",
            c => SyntaxOf(c) is var s
                ? LineItems(s).SelectMany(i => ClassificationCodes(s, i)).Where(k => Collapse(k.Attribute("listID")) == "CVD"
                        && Collapse(k.Text) is not ("M1" or "M2" or "M3" or "N1" or "N2" or "N3"))
                    .Select(k => (c.GroupOf(k), c.FieldOf(k)))
                : []);
        yield return XOnly(V, "BR-DE-CVD-05", F, "Der Wert des Merkmals „cva“ (BT-161) ist keiner von clean, zero-emission, other.",
            "The value of the attribute „cva“ (BT-161) is none of clean, zero-emission, other.",
            c => SyntaxOf(c) is var s
                ? LineItems(s).SelectMany(i => CvaAttributes(s, i)).Where(a => Collapse(a.Child(s.Cii ? Ram : Cbc, "Value")?.Text) is not ("clean" or "zero-emission" or "other"))
                    .Select(a => (c.GroupOf(a), (InvoiceField?)null))
                : []);
        yield return XOnly(V, "BR-DE-CVD-06-a", F, "Eine Position mit Fahrzeugklasse (Schema „CVD“) hat nicht genau ein Merkmal „cva“ (BT-160).",
            "A line with a vehicle category (scheme „CVD“) does not have exactly one attribute „cva“ (BT-160).",
            c => SyntaxOf(c) is var s
                ? LineItems(s).Where(i => VehicleCodes(s, i).Any() && CvaAttributes(s, i).Count() != 1).Select(i => (c.GroupOf(i), (InvoiceField?)null))
                : []);
        yield return XOnly(V, "BR-DE-CVD-06-b", F, "Eine Position mit dem Merkmal „cva“ hat nicht genau eine Fahrzeugklasse (BT-158 mit dem Schema „CVD“).",
            "A line with the attribute „cva“ does not have exactly one vehicle category (BT-158 with the scheme „CVD“).",
            c => SyntaxOf(c) is var s
                ? LineItems(s).Where(i => CvaAttributes(s, i).Any() && VehicleCodes(s, i).Count() != 1).Select(i => (c.GroupOf(i), (InvoiceField?)null))
                : []);
        yield return XCoded(V, "BR-TMP-CVD-01", "Das Schema einer Artikelklassifizierung (BT-158) steht nicht in UNTDID 7143 und ist nicht „CVD“.",
            "The scheme of an item classification (BT-158) is not in UNTDID 7143 and not „CVD“.");
    }

    /// <summary>The items of the lines of the invoice itself: CII the product of every line, UBL the item of every line (not of a sub line).</summary>
    private static IEnumerable<InvoiceElement> LineItems(Syntax s) => s.Lines.SelectMany(l => l.ChildrenNamed(s.Cii ? Ram : Cac, s.Cii ? "SpecifiedTradeProduct" : "Item"));

    private static IEnumerable<InvoiceElement> ClassificationCodes(Syntax s, InvoiceElement item) => s.Cii
        ? Down(item.ChildrenNamed(Ram, "DesignatedProductClassification"), Ram, "ClassCode")
        : Down(item.ChildrenNamed(Cac, "CommodityClassification"), Cbc, "ItemClassificationCode");

    /// <summary>The classification codes of an item with the scheme „CVD“ exactly as written (the artefacts compare it so here).</summary>
    private static IEnumerable<InvoiceElement> VehicleCodes(Syntax s, InvoiceElement item) => ClassificationCodes(s, item).Where(k => k.Attribute("listID") == "CVD");

    /// <summary>The attributes of an item named „cva“ exactly as written (CII the description, UBL the name of the attribute).</summary>
    private static IEnumerable<InvoiceElement> CvaAttributes(Syntax s, InvoiceElement item) => s.Cii
        ? item.ChildrenNamed(Ram, "ApplicableProductCharacteristic").Where(a => a.ChildrenNamed(Ram, "Description").Any(d => d.Text == "cva"))
        : item.ChildrenNamed(Cac, "AdditionalItemProperty").Where(a => a.ChildrenNamed(Cbc, "Name").Any(n => n.Text == "cva"));
}
