namespace PdfKnead.EInvoice;

/// <summary>
/// The business rules of the profile EXTENDED of ZUGFeRD / Factur-X (stage 4, part e3; KONZEPT.md, „Weg für Stufe 4, Teil e“): the rules
/// BR-FXEXT the specification Factur-X 1.09.2 / ZUGFeRD 2.5.2 lists for EXTENDED (sheet „Business Rules“, Zuordnung/profile.tsv), in place
/// of the rules of EN 16931 it does not list there. EXTENDED knows sub lines: a line may name its parent (BT-X-304) and its subtype
/// (BT-X-8: DETAIL, GROUP, INFORMATION); only a line of the subtype DETAIL — or without one — is an item of the invoice, its amounts add up.
/// Logistics service charges (BG-X-42) count as charges, amounts collected for a third party (BT-179) go into the amount due, and the sums
/// may differ by one cent per item added up. Written from the specification in code of our own, on the elements of the XML (most of these
/// terms have no place in the readable form); the validation artefacts of the package (Apache-2.0) are the reference of the comparison.
/// Not as rules of their own: BR-FXEXT-05 (the subtype from its code list — the element rule FX-SCH-A-000335 checks the same list),
/// BR-FXEXT-07, -09 and -10 (they make terms optional or say what is not added up: the rules on DETAIL lines carry them), BR-FXEXT-CO-06 and
/// -CO-08 („code and text name the same kind“: not to be checked on a free text, as BR-CO-05 to -08). BR-FXEXT-BR-27 (a net price not
/// negative) stands in the sheet but for no profile — EXTENDED leaves out BR-27 as well, and so do its artefacts. Only for a CII invoice of
/// EXTENDED.
/// </summary>
public static partial class InvoiceRules
{
    /// <summary>A line of the XML with what the rules of EXTENDED ask of it.</summary>
    /// <param name="Status">The subtype (BT-X-8) as written; null if the line names none.</param>
    /// <param name="Detail">Whether the line is an item of the invoice: of the subtype DETAIL (as written) or without a subtype.</param>
    private sealed record ExtLine(InvoiceElement Element, InvoiceGroup Group, string Id, string Parent, string? Status, bool Detail,
        InvoiceElement? Amount, InvoiceElement? Settlement);

    /// <summary>An item of an EXTENDED invoice with its VAT category: a DETAIL line (0), a document level allowance (1) or charge (2), a
    /// logistics service charge (3).</summary>
    private sealed record ExtItem(int Kind, string? Category, decimal? Rate, string Reason, string ReasonCode, decimal Amount);

    /// <summary>What the rules add up per VAT category and rate (and exemption reason): the amounts of each kind and how many items.</summary>
    private sealed class ExtSums
    {
        public decimal Lines, Allowances, Charges, Logistics;
        public int Count;

        public void Add(ExtItem i)
        {
            Count++;
            switch (i.Kind)
            {
                case 0: Lines += i.Amount; break;
                case 1: Allowances += i.Amount; break;
                case 2: Charges += i.Amount; break;
                default: Logistics += i.Amount; break;
            }
        }
    }

    /// <summary>The XML of an EXTENDED invoice as its rules read it, made once per invoice.</summary>
    private sealed class ExtendedView
    {
        public ExtendedView(Context c)
        {
            var root = c.Root.Element!;
            var transaction = root.Child(Rsm, "SupplyChainTradeTransaction");
            Settlement = transaction?.Child(Ram, "ApplicableHeaderTradeSettlement");
            Totals = Settlement?.Child(Ram, "SpecifiedTradeSettlementHeaderMonetarySummation");
            foreach (var e in transaction?.ChildrenNamed(Ram, "IncludedSupplyChainTradeLineItem") ?? [])
            {
                var document = e.Child(Ram, "AssociatedDocumentLineDocument");
                var status = document?.Child(Ram, "LineStatusReasonCode");
                var settlement = e.Child(Ram, "SpecifiedLineTradeSettlement");
                Lines.Add(new ExtLine(e, c.GroupOf(e), Collapse(document?.Child(Ram, "LineID")?.Text), Collapse(document?.Child(Ram, "ParentLineID")?.Text),
                    status is null ? null : status.Text ?? "", status is null || status.Text == "DETAIL",
                    settlement?.Child(Ram, "SpecifiedTradeSettlementLineMonetarySummation")?.Child(Ram, "LineTotalAmount"), settlement));
            }
            foreach (var l in Lines.Where(l => l.Detail))
                Items.Add(Item(0, l.Settlement, "ApplicableTradeTax", l.Amount));
            foreach (var a in c.All("BG-20")) Items.Add(Item(1, a.Element, "CategoryTradeTax", a.Element?.Child(Ram, "ActualAmount")));
            foreach (var ch in c.All("BG-21")) Items.Add(Item(2, ch.Element, "CategoryTradeTax", ch.Element?.Child(Ram, "ActualAmount")));
            foreach (var s in Logistics) Items.Add(Item(3, s, "AppliedTradeTax", s.Child(Ram, "AppliedAmount")));
            // the sums per category and rate (O: per category), and per category, rate and exemption reason — once, not once per breakdown (FG 832)
            foreach (var i in Items)
            {
                if (i.Category is not { } category) continue;
                var rate = category == "O" ? null : i.Rate;
                Of(_byRate, (category, rate, "", "")).Add(i);
                Of(_byReason, (category, rate, i.Reason, i.ReasonCode)).Add(i);
            }
        }

        public InvoiceElement? Settlement { get; }

        public InvoiceElement? Totals { get; }

        public List<ExtLine> Lines { get; } = [];

        public List<ExtItem> Items { get; } = [];

        public IEnumerable<InvoiceElement> Logistics => Settlement?.ChildrenNamed(Ram, "SpecifiedLogisticsServiceCharge") ?? [];

        public IEnumerable<ExtLine> DetailLines => Lines.Where(l => l.Detail);

        private readonly Dictionary<(string, decimal?, string, string), ExtSums> _byRate = [];
        private readonly Dictionary<(string, decimal?, string, string), ExtSums> _byReason = [];

        private static ExtSums Of(Dictionary<(string, decimal?, string, string), ExtSums> sums, (string, decimal?, string, string) key) =>
            sums.TryGetValue(key, out var s) ? s : sums[key] = new ExtSums();

        /// <summary>The items of a category and rate (O: of the category), or with the exemption reason as well; none: empty sums.</summary>
        public ExtSums Sums(string category, decimal? rate, string? reason, string? code) =>
            (reason is null ? _byRate : _byReason).GetValueOrDefault((category, category == "O" ? null : rate, reason ?? "", code ?? "")) ?? new ExtSums();

        private static ExtItem Item(int kind, InvoiceElement? owner, string taxName, InvoiceElement? amount)
        {
            var (category, rate, reason, code) = VatOf(owner, taxName);
            return new ExtItem(kind, category, rate, reason, code, DecimalOf(amount) ?? 0);
        }
    }

    /// <summary>The VAT of an item: category, rate and exemption reason (text and code, space collapsed) of its first tax element of the tax VAT.</summary>
    private static (string? Category, decimal? Rate, string Reason, string Code) VatOf(InvoiceElement? owner, string taxName)
    {
        foreach (var t in owner?.ChildrenNamed(Ram, taxName) ?? [])
            if (string.Equals(ChildText(t, "TypeCode"), "VAT", StringComparison.OrdinalIgnoreCase))
                return (ChildText(t, "CategoryCode"), DecimalOf(t.Child(Ram, "RateApplicablePercent")), Collapse(t.Child(Ram, "ExemptionReason")?.Text),
                    Collapse(t.Child(Ram, "ExemptionReasonCode")?.Text));
        return (null, null, "", "");
    }

    private static bool IsExtended(Context c) =>
        c.Invoice.Kind.Syntax == InvoiceSyntax.Cii && c.Invoice.Kind.Profile == InvoiceProfile.Extended && c.Root.Element is not null;

    private static ExtendedView ExtOf(Context c) => (ExtendedView)(c.ExtendedView ??= new ExtendedView(c));

    private static RuleCheck FxExt(string id, InvoiceRuleSeverity severity, string german, string english, Func<Context, IEnumerable<(InvoiceGroup, InvoiceField?)>> find) =>
        new(new InvoiceRule(id, ProfileSource, severity, german, english), c => IsExtended(c) ? find(c) : []);

    /// <summary>Whether a value is within one cent per item of another — the slack of the sums of EXTENDED.</summary>
    private static bool WithinCents(decimal value, decimal expected, int items) => Math.Abs(value - expected) <= 0.01m * items;

    /// <summary>The lines by the identifier of their parent, made once per rule and invoice (a prepared invoice with many groups must not cost
    /// groups times lines).</summary>
    private static ILookup<string, ExtLine> ChildrenByParent(ExtendedView v) => v.Lines.Where(l => l.Parent.Length > 0).ToLookup(l => l.Parent, StringComparer.Ordinal);

    private static IEnumerable<RuleCheck> ExtendedRules(IReadOnlySet<string> itemAttributeCodes)
    {
        const InvoiceRuleSeverity F = InvoiceRuleSeverity.Fatal, W = InvoiceRuleSeverity.Warning;

        // ---- notes, VAT registrations, item attributes
        yield return FxExt("BR-FXEXT-01", F, "Eine Bemerkung zur Rechnung mit Betreffcode (BT-21) hat weder Text (BT-22) noch Inhaltscode (BT-X-5).",
            "An invoice note with a subject code (BT-21) has neither text (BT-22) nor content code (BT-X-5).",
            c => NotesWithoutContent(c, c.Root.Element!.Child(Rsm, "ExchangedDocument")?.ChildrenNamed(Ram, "IncludedNote") ?? []));
        yield return FxExt("BR-FXEXT-02", F, "Eine Bemerkung zur Position mit Betreffcode (BT-X-10) hat weder Text (BT-127) noch Inhaltscode (BT-X-9).",
            "An invoice line note with a subject code (BT-X-10) has neither text (BT-127) nor content code (BT-X-9).",
            c => NotesWithoutContent(c, ExtOf(c).Lines.SelectMany(l => l.Element.Child(Ram, "AssociatedDocumentLineDocument")?.ChildrenNamed(Ram, "IncludedNote") ?? [])));
        // every party but the seller has only a VAT identifier (the list of the specification names all others the profile has)
        yield return FxExt("BR-FXEXT-03", F, "Eine Steuerregistrierung außerhalb des Verkäufers ist keine USt-IdNr. (Schema „VA“).",
            "A tax registration outside the seller is no VAT identifier (scheme „VA“).",
            c => Descendants(c.Root.Element!).Where(e => e.Local == "SpecifiedTaxRegistration" && e.Namespace == Ram && !UnderSeller(e))
                .SelectMany(r => r.ChildrenNamed(Ram, "ID")).Where(id => id.Attribute("schemeID") != "VA").Select(id => (c.GroupOf(id), c.FieldOf(id))));
        yield return new RuleCheck(new InvoiceRule("BR-FXEXT-04", ProfileSource, W,
                "Der Code einer Artikeleigenschaft (BT-X-11) steht nicht in der Liste, die das Profil empfiehlt (UNTDID 6313 und Erweiterung).",
                "The code of an item attribute (BT-X-11) is not in the list the profile recommends (UNTDID 6313 and extension)."), _ => [],
            c => IsExtended(c)
                ? Characteristics(c).Select(a => a.Child(Ram, "TypeCode")).Where(t => t is not null && Collapse(t.Text) is { Length: > 0 } v && !itemAttributeCodes.Contains(v))
                    .Select(t => (c.GroupOf(t!), c.FieldOf(t!), (string?)t!.Text))
                : []);
        yield return FxExt("BR-FXEXT-BR-54-1", F, "Eine Artikeleigenschaft (BG-32) hat weder Namen (BT-160) noch Code (BT-X-11).",
            "An item attribute (BG-32) has neither name (BT-160) nor code (BT-X-11).",
            c => Characteristics(c).Where(a => !Filled(a.Child(Ram, "Description")) && !Filled(a.Child(Ram, "TypeCode"))).Select(a => (c.GroupOf(a), (InvoiceField?)null)));
        yield return FxExt("BR-FXEXT-BR-54-2", F, "Eine Artikeleigenschaft (BG-32) hat nicht genau eines: Wert (BT-161) oder Wert mit Einheit (BT-X-12 mit Einheit).",
            "An item attribute (BG-32) does not have exactly one of: value (BT-161) or value with unit (BT-X-12 with its unit).",
            c => Characteristics(c).Where(a => !OneValue(a)).Select(a => (c.GroupOf(a), (InvoiceField?)null)));

        // ---- sub lines: subtype, parent, the amounts of a group
        yield return FxExt("BR-FXEXT-06", F, "Eine Position mit übergeordneter Position (BT-X-304) hat keinen Untertyp (BT-X-8).",
            "An invoice line with a parent line (BT-X-304) has no subtype (BT-X-8).",
            c => ExtOf(c).Lines.Where(l => l.Parent.Length > 0 && Collapse(l.Status).Length == 0).Select(l => (l.Group, (InvoiceField?)null)));
        yield return FxExt("BR-FXEXT-08", F, "Der Nettobetrag einer Gruppenposition (BT-131) ist nicht die Summe der Nettobeträge ihrer Positionen DETAIL und GROUP.",
            "The net amount of a group line (BT-131) is not the sum of the net amounts of its DETAIL and GROUP lines.",
            c =>
            {
                var v = ExtOf(c);
                var children = ChildrenByParent(v);
                return v.Lines.Where(l => l.Status == "GROUP" && Filled(l.Amount) && !GroupSumFits(l, l.Id.Length == 0 ? [] : children[l.Id]))
                    .Select(l => (l.Group, c.FieldOf(l.Amount!)));
            });
        yield return FxExt("BR-FXEXT-11", F, "Die übergeordnete Position (BT-X-304) ist keine andere Position dieser Rechnung (BT-126).",
            "The parent line (BT-X-304) is no other line of this invoice (BT-126).",
            c =>
            {
                var v = ExtOf(c);
                var ids = v.Lines.Where(l => l.Id.Length > 0).ToLookup(l => l.Id, StringComparer.Ordinal);
                return v.Lines.Where(l => l.Parent.Length > 0 && !ids[l.Parent].Any(other => !ReferenceEquals(other, l))).Select(l => (l.Group, (InvoiceField?)null));
            });
        yield return FxExt("BR-FXEXT-12", F, "Eine Gruppenposition mit Nettobetrag hat eine Gruppenposition darunter ohne Nettobetrag (BT-131).",
            "A group line with a net amount has a group line below it without a net amount (BT-131).",
            c =>
            {
                var v = ExtOf(c);
                var children = ChildrenByParent(v);
                return v.Lines.Where(l => l.Status == "GROUP" && Filled(l.Amount) && l.Id.Length > 0
                        && children[l.Id].Any(child => !ReferenceEquals(child, l) && child.Status == "GROUP" && !Filled(child.Amount)))
                    .Select(l => (l.Group, (InvoiceField?)null));
            });

        // ---- what a DETAIL line must have (a GROUP or INFORMATION line may leave it out)
        foreach (var (id, de, en, has) in new (string, string, string, Func<ExtLine, bool>)[]
                 {
                     ("BR-FXEXT-BR-22", "keine Menge (BT-129)", "no invoiced quantity (BT-129)", l => Filled(Quantity(l))),
                     ("BR-FXEXT-BR-23", "keine Einheit der Menge (BT-130)", "no unit of measure (BT-130)", l => !string.IsNullOrWhiteSpace(Quantity(l)?.Attribute("unitCode"))),
                     ("BR-FXEXT-BR-24", "keinen Nettobetrag (BT-131)", "no net amount (BT-131)", l => Filled(l.Amount)),
                     ("BR-FXEXT-BR-26", "keinen Nettopreis (BT-146)", "no item net price (BT-146)", l => NetPrice(l) is not null),
                     ("BR-FXEXT-CO-04", "keine Steuerkategorie (BT-151)", "no VAT category (BT-151)", l => VatOf(l.Settlement, "ApplicableTradeTax").Category is { Length: > 0 }),
                 })
        {
            var check = has;
            yield return FxExt(id, F, $"Eine Position DETAIL (oder ohne Untertyp) hat {de}.", $"A DETAIL invoice line (or one without a subtype) has {en}.",
                c => ExtOf(c).DetailLines.Where(l => !check(l)).Select(l => (l.Group, (InvoiceField?)null)));
        }

        // ---- the reason of a charge: text, code or the code of another tax (BT-177, BT-193 — the same element as the code, by its list)
        foreach (var id in new[] { "BR-FXEXT-BR-38", "BR-FXEXT-CO-22" })
            yield return FxExt(id, F, "Ein Zuschlag auf Rechnungsebene hat weder Grund (BT-104) noch Code (BT-105 oder BT-177).",
                "A document level charge has neither reason (BT-104) nor reason code (BT-105 or BT-177).", c => MissingBoth(c.All("BG-21"), "BT-104", "BT-105"));
        foreach (var id in new[] { "BR-FXEXT-BR-44", "BR-FXEXT-CO-24" })
            yield return FxExt(id, F, "Ein Zuschlag auf Positionsebene hat weder Grund (BT-144) noch Code (BT-145 oder BT-193).",
                "An invoice line charge has neither reason (BT-144) nor reason code (BT-145 or BT-193).", c => MissingBoth(c.All("BG-28"), "BT-144", "BT-145"));

        // ---- the document totals: DETAIL lines, logistics service charges, one cent of slack per item
        yield return FxExt("BR-FXEXT-CO-10", F, "Die Summe der Positionsbeträge (BT-106) ist nicht die Summe der Nettobeträge der Positionen DETAIL (bis 1 Cent je Position).",
            "The sum of line net amounts (BT-106) is not the sum of the net amounts of the DETAIL lines (up to 1 cent per line).",
            c => Total(c, "BT-106", t => WithinCents(t, Cents(DetailSum(c)), DetailCount(c))));
        yield return FxExt("BR-FXEXT-CO-11", F, "Die Summe der Nachlässe (BT-107) ist nicht die Summe der Nachlässe auf Rechnungsebene (bis 1 Cent je Nachlass).",
            "The sum of allowances (BT-107) is not the sum of the document level allowances (up to 1 cent per allowance).",
            c => KindCount(c, 1) == 0 && !Has(c.First("BG-22") ?? c.Root, "BT-107") ? []
                : Total(c, "BT-107", t => WithinCents(t, Cents(KindSum(c, 1)), KindCount(c, 1))));
        yield return FxExt("BR-FXEXT-CO-12", F, "Die Summe der Zuschläge (BT-108) ist nicht die Summe der Zuschläge und Logistikgebühren (bis 1 Cent je Posten).",
            "The sum of charges (BT-108) is not the sum of the document level charges and logistics service charges (up to 1 cent per item).",
            // as the artefacts: without document level charges and without the sum of charges nothing to add up, also beside logistics charges
            c => KindCount(c, 2) == 0 && !Has(c.First("BG-22") ?? c.Root, "BT-108") ? []
                : Total(c, "BT-108", t => WithinCents(t, Cents(KindSum(c, 2)) + Cents(KindSum(c, 3)), KindCount(c, 2) + KindCount(c, 3))));
        yield return FxExt("BR-FXEXT-CO-13", F, "Der Gesamtbetrag ohne Umsatzsteuer (BT-109) ist nicht Positionen DETAIL − Nachlässe + Zuschläge und Logistikgebühren (bis 1 Cent je Posten).",
            "The total without VAT (BT-109) is not DETAIL lines − allowances + charges and logistics service charges (up to 1 cent per item).",
            c => Total(c, "BT-109", t => WithinCents(t, Cents(DetailSum(c)) - Cents(KindSum(c, 1)) + Cents(KindSum(c, 2)) + Cents(KindSum(c, 3)), AllItems(c))));
        yield return FxExt("BR-FXEXT-CO-15", F, "Der Gesamtbetrag mit Umsatzsteuer (BT-112) ist nicht ohne Umsatzsteuer (BT-109) plus Umsatzsteuer in Rechnungswährung (BT-110; bis 1 Cent je Posten).",
            "The total with VAT (BT-112) is not the total without VAT (BT-109) plus the VAT in the invoice currency (BT-110; up to 1 cent per item).",
            c => GrossTotalFits(c));
        yield return FxExt("BR-FXEXT-CO-16", F, "Der fällige Betrag (BT-115) ist nicht Gesamtbetrag (BT-112) − Vorauszahlung (BT-113) + Rundung (BT-114) + für Dritte eingezogene Beträge (BT-179).",
            "The amount due (BT-115) is not the total (BT-112) − paid amount (BT-113) + rounding (BT-114) + amounts collected for a third party (BT-179).",
            c =>
            {
                var third = ExtOf(c).Settlement?.ChildrenNamed(Ram, "SpecifiedFinancialAdjustment").Sum(a => DecimalOf(a.Child(Ram, "ActualAmount")) ?? 0) ?? 0;
                return Total(c, "BT-115", due => c.First("BG-22") is { } t
                    && due == (Number(t, "BT-112") ?? 0) - (Number(t, "BT-113") ?? 0) + (Number(t, "BT-114") ?? 0) + third);
            });

        // ---- the VAT categories: items of a category need its breakdown; its taxable amount adds up (per rate, or per rate and exemption
        // reason); the tax of the standard rate fits
        foreach (var cat in Categories())
        {
            var k = cat;
            yield return FxExt($"BR-FXEXT-{k.Family}-01", F,
                $"Positionen DETAIL, Nachlässe, Zuschläge oder Logistikgebühren mit „{k.German}“ ({k.Code}) verlangen eine Steueraufschlüsselung dieser Kategorie.",
                $"DETAIL lines, allowances, charges or logistics service charges with „{k.English}“ ({k.Code}) need a VAT breakdown of this category.",
                c => ExtOf(c).Items.Any(i => i.Category == k.Code) && !c.All("BG-23").Any(b => VatText(b, "BT-118") == k.Code) ? [(c.Root, null)] : []);
            yield return FxExt($"BR-FXEXT-{k.Family}-08b", F,
                $"Der Basisbetrag einer Aufschlüsselung „{k.German}“ (BT-116) ist nicht Positionen DETAIL − Nachlässe + Zuschläge und Logistikgebühren dieser Kategorie"
                + $"{(k.Code == "O" ? "" : " und dieses Satzes")} (auch nicht je Befreiungsgrund; bis 1 Cent je Posten).",
                $"The taxable amount of a „{k.English}“ breakdown (BT-116) is not DETAIL lines − allowances + charges and logistics service charges of this category"
                + $"{(k.Code == "O" ? "" : " and rate")} (nor per exemption reason; up to 1 cent per item).",
                c => c.All("BG-23").Where(b => VatText(b, "BT-118") == k.Code && !BasisFits(c, k.Code, b)).Select(b => (b, b.Field("BT-116"))));
            if (k.Code == "S")
                yield return FxExt("BR-FXEXT-S-09b", F,
                    "Der Steuerbetrag einer Aufschlüsselung „Normalsatz“ (BT-117) ist nicht Basisbetrag × Satz / 100 (bis 1 Cent je Posten der Kategorie und des Satzes).",
                    "The tax amount of a „standard rate“ breakdown (BT-117) is not taxable amount × rate / 100 (up to 1 cent per item of the category and rate).",
                    c => c.All("BG-23").Where(b => VatText(b, "BT-118") == "S" && !StandardTaxFits(c, b)).Select(b => (b, b.Field("BT-117"))));
        }
    }

    // ------------------------------------------------------------ helpers of the rules of EXTENDED

    private static bool UnderSeller(InvoiceElement e)
    {
        for (var p = e.Parent; p is not null; p = p.Parent)
            if (p.Local == "SellerTradeParty" && p.Namespace == Ram) return true;
        return false;
    }

    private static IEnumerable<(InvoiceGroup, InvoiceField?)> NotesWithoutContent(Context c, IEnumerable<InvoiceElement> notes) =>
        notes.Where(n => Filled(n.Child(Ram, "SubjectCode")) && !Filled(n.Child(Ram, "Content")) && !Filled(n.Child(Ram, "ContentCode")))
            .Select(n => (c.GroupOf(n), c.FieldOf(n.Child(Ram, "SubjectCode")!)));

    /// <summary>Every item attribute of the invoice (also of an included product, as the artefacts look for it anywhere).</summary>
    private static IEnumerable<InvoiceElement> Characteristics(Context c) =>
        Descendants(c.Root.Element!).Where(e => e.Local == "ApplicableProductCharacteristic" && e.Namespace == Ram);

    /// <summary>BR-FXEXT-BR-54-2: a value, or a value with a unit of measure — one of them, not both.</summary>
    private static bool OneValue(InvoiceElement attribute)
    {
        var value = Filled(attribute.Child(Ram, "Value"));
        var measure = attribute.Child(Ram, "ValueMeasure");
        return value ? !Filled(measure) : Filled(measure) && !string.IsNullOrWhiteSpace(measure!.Attribute("unitCode"));
    }

    private static InvoiceElement? Quantity(ExtLine l) => l.Element.Child(Ram, "SpecifiedLineTradeDelivery")?.Child(Ram, "BilledQuantity");

    private static InvoiceElement? NetPrice(ExtLine l) =>
        l.Element.Child(Ram, "SpecifiedLineTradeAgreement")?.Child(Ram, "NetPriceProductTradePrice")?.Child(Ram, "ChargeAmount");

    /// <summary>BR-FXEXT-08: the net amount of a group line is the sum of its DETAIL and GROUP lines, rounded to the cent; a missing or unreadable
    /// amount among them leaves nothing to add up (as the artefacts count it: no number).</summary>
    private static bool GroupSumFits(ExtLine group, IEnumerable<ExtLine> children)
    {
        if (DecimalOf(group.Amount) is not { } stated) return false;
        var sum = 0m;
        foreach (var child in children)
        {
            if (ReferenceEquals(child, group) || child.Status is not ("DETAIL" or "GROUP")) continue;
            if (DecimalOf(child.Amount) is not { } amount) return false;
            sum += amount;
        }
        return stated == Cents(sum);
    }

    private static decimal DetailSum(Context c) => KindSum(c, 0);

    private static int DetailCount(Context c) => KindCount(c, 0);

    /// <summary>The amounts of one kind of item (0 DETAIL lines, 1 allowances, 2 charges, 3 logistics service charges), as written; a missing one adds nothing.</summary>
    private static decimal KindSum(Context c, int kind) => ExtOf(c).Items.Where(i => i.Kind == kind).Sum(i => i.Amount);

    private static int KindCount(Context c, int kind) => ExtOf(c).Items.Count(i => i.Kind == kind);

    /// <summary>How many items the totals add up: DETAIL lines, document level allowances and charges, logistics service charges.</summary>
    private static int AllItems(Context c) => ExtOf(c).Items.Count;

    /// <summary>A total of the document totals checked by a condition; a missing one fails (as the artefacts count it), without totals nothing is asked.</summary>
    private static IEnumerable<(InvoiceGroup, InvoiceField?)> Total(Context c, string id, Func<decimal, bool> fits)
    {
        if (c.First("BG-22") is not { } totals) return [];
        return totals.Field(id) is { } field && Number(field) is { } value && fits(value) ? [] : [(totals, totals.Field(id))];
    }

    /// <summary>
    /// BR-FXEXT-CO-15: with exactly one VAT total in the invoice currency (its currency as written) the total with VAT is the total without VAT
    /// plus it, up to one cent per item; otherwise the total with VAT is the total without VAT.
    /// </summary>
    private static IEnumerable<(InvoiceGroup, InvoiceField?)> GrossTotalFits(Context c)
    {
        if (c.First("BG-22") is not { } totals) return [];
        var v = ExtOf(c);
        var currency = v.Settlement?.Child(Ram, "InvoiceCurrencyCode")?.Text;
        var vat = v.Totals?.ChildrenNamed(Ram, "TaxTotalAmount").Where(t => currency is not null && t.Attribute("currencyID") == currency).ToList() ?? [];
        if (Number(totals, "BT-109") is not { } net || Number(totals, "BT-112") is not { } gross) return [(totals, totals.Field("BT-112"))];
        var fits = vat.Count == 1
            ? DecimalOf(vat[0]) is { } tax && WithinCents(gross, net + tax, AllItems(c))
            : gross == net;
        return fits ? [] : [(totals, totals.Field("BT-112"))];
    }

    /// <summary>
    /// BR-FXEXT-…-08b: the taxable amount of a breakdown is lines (DETAIL) − allowances + charges + logistics service charges of its category and
    /// rate (O: of its category), each sum rounded to the cent, up to one cent per item — taken per rate, or per rate and exemption reason (text
    /// and code of the breakdown and of the items, space collapsed); one of the two must add up. A missing taxable amount, or a missing rate
    /// (but for O), does not add up.
    /// </summary>
    private static bool BasisFits(Context c, string category, InvoiceGroup breakdown)
    {
        var rate = VatNumber(breakdown, "BT-119");
        if (Number(breakdown, "BT-116") is not { } basis || category != "O" && rate is null) return false;
        var v = ExtOf(c);
        bool Adds(ExtSums s) => WithinCents(basis, Cents(s.Lines) - Cents(s.Allowances) + Cents(s.Charges) + Cents(s.Logistics), s.Count);
        return Adds(v.Sums(category, rate, Collapse(breakdown.Field("BT-120")?.Text), Collapse(breakdown.Field("BT-121")?.Text)))
            || Adds(v.Sums(category, rate, null, null));
    }

    /// <summary>BR-FXEXT-S-09b: the tax of a standard rate breakdown is taxable amount × rate / 100 rounded to the cent, up to one cent per item of
    /// its category and rate (or of its rate and exemption reason); missing amounts or rate do not fit.</summary>
    private static bool StandardTaxFits(Context c, InvoiceGroup breakdown)
    {
        if (Number(breakdown, "BT-116") is not { } basis || VatNumber(breakdown, "BT-119") is not { } rate || Number(breakdown, "BT-117") is not { } tax)
            return false;
        var v = ExtOf(c);
        var expected = Cents(basis * rate / 100);
        return WithinCents(tax, expected, v.Sums("S", rate, Collapse(breakdown.Field("BT-120")?.Text), Collapse(breakdown.Field("BT-121")?.Text)).Count)
            || WithinCents(tax, expected, v.Sums("S", rate, null, null).Count);
    }
}
