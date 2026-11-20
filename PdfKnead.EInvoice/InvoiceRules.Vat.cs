namespace PdfKnead.EInvoice;

/// <summary>
/// The rules of the VAT categories (stage 4, part b; EN 16931-1, section 6.4.3): for each category the same pattern — items of the category
/// need a VAT breakdown of it (01), the identifiers of seller and buyer it requires or forbids (02 to 04), a fitting rate (05 to 07), a
/// taxable amount that adds up (08), a tax amount that fits (09), an exemption reason it requires or forbids (10); intra-community supply also
/// a delivery date or period and the country of delivery (IC-11, IC-12); „not subject to VAT“ nothing else beside it (O-11 to O-14). Written
/// from the standard in code of our own. Only categories and rates of the tax VAT count (<see cref="IsVat"/>). Where the validation artefacts
/// of CII and UBL read a rule alike, it is read so here (the tax within one unit of the currency, missing amounts as not fitting); where they
/// differ, as the standard says (01 from the items to the breakdown only, 08 exactly).
/// </summary>
public static partial class InvoiceRules
{
    private enum Rate
    {
        Positive,
        Zero,
        NotNegative,
        None,
    }

    /// <summary>A VAT category: its code (UNTDID 5305), the family of its rules, its name, the rate it needs, whether it needs an exemption
    /// reason, and whether one breakdown per rate (true) or exactly one (false).</summary>
    private sealed record Category(string Code, string Family, string German, string English, Rate Rate, bool Exempt, bool PerRate);

    // created when asked, not as a field: the fields of the two files of this class are initialised in no fixed order
    private static Category[] Categories() =>
    [
        new("S", "S", "Normalsatz", "standard rate", Rate.Positive, false, true),
        new("Z", "Z", "Nullsatz", "zero rate", Rate.Zero, false, false),
        new("E", "E", "steuerbefreit", "exempt", Rate.Zero, true, false),
        new("AE", "AE", "Umkehrung der Steuerschuldnerschaft", "reverse charge", Rate.Zero, true, false),
        new("K", "IC", "innergemeinschaftliche Lieferung", "intra-community supply", Rate.Zero, true, false),
        new("G", "G", "Ausfuhr außerhalb der EU", "export outside the EU", Rate.Zero, true, false),
        new("O", "O", "nicht steuerbar", "not subject to VAT", Rate.None, true, false),
        new("L", "IG", "IGIC (Kanarische Inseln)", "IGIC (Canary Islands)", Rate.NotNegative, false, true),
        new("M", "IP", "IPSI (Ceuta und Melilla)", "IPSI (Ceuta and Melilla)", Rate.NotNegative, false, true),
    ];

    /// <summary>An item of the invoice that carries a VAT category: a line, a document level allowance or charge.</summary>
    private sealed record VatItem(InvoiceGroup Group, string? Category, decimal? Rate, decimal Amount, int Kind);   // kind: 0 line, 1 allowance, 2 charge

    private static List<VatItem> Items(Context c)   // once per invoice: Context.VatItems
    {
        var items = new List<VatItem>();
        // the lines of the rules: in CII also the sub lines, lines of their own there (FG 843); in UBL the invoice lines
        foreach (var l in c.Lines) items.Add(Line(l));
        foreach (var a in c.All("BG-20")) items.Add(new VatItem(a, VatText(a, "BT-95"), VatNumber(a, "BT-96"), Number(a, "BT-92") ?? 0, 1));
        foreach (var ch in c.All("BG-21")) items.Add(new VatItem(ch, VatText(ch, "BT-102"), VatNumber(ch, "BT-103"), Number(ch, "BT-99") ?? 0, 2));
        return items;
    }

    /// <summary>A line as an item: its VAT category and rate (of its own, not of a sub line) and its net amount.</summary>
    private static VatItem Line(InvoiceGroup l) =>
        new(l, Fields(l, "BT-151").FirstOrDefault(IsVat)?.Text?.Trim(), Number(Fields(l, "BT-152").FirstOrDefault(IsVat)), Number(l, "BT-131") ?? 0, 0);

    /// <summary>The lines of the rules (in CII also the sub lines) with their category, for the rules about lines.</summary>
    private static IEnumerable<VatItem> LineItems(Context c) => c.LineVatItems;

    private static bool AnyId(Context c, string party, params string[] ids) => c.All(party).Any(g => ids.Any(id => g.AllFields().Any(f => f.Id == id && !string.IsNullOrWhiteSpace(f.Text))));

    private static IEnumerable<RuleCheck> VatRules()
    {
        foreach (var cat in Categories())
        {
            var k = cat;
            string Id(int n) => $"BR-{k.Family}-{n:00}";
            string Kind(int kind, bool german) => (kind, german) switch
            {
                (0, true) => "Positionen",
                (1, true) => "Nachlässe auf Rechnungsebene",
                (2, true) => "Zuschläge auf Rechnungsebene",
                (0, false) => "invoice lines",
                (1, false) => "document level allowances",
                _ => "document level charges",
            };
            // which categories the invoice has (01, 02: present) counts every line and sub line, as both artefacts count them; the rate of
            // a line (05) and the sums (08) are asked of the lines of the rules (in CII with the sub lines, FG 843)
            IEnumerable<VatItem> Of(Context c, int? kind = null, bool present = false) =>
                (kind == 0 ? present ? c.CategoryLineItems : LineItems(c)
                    : kind is null && present ? c.CategoryLineItems.Concat(c.VatItems.Where(i => i.Kind != 0))
                    : c.VatItems.Where(i => kind is null || i.Kind == kind)).Where(i => i.Category == k.Code);
            IEnumerable<InvoiceGroup> Breakdowns(Context c) => c.All("BG-23").Where(g => VatText(g, "BT-118") == k.Code);

            // 01: items of the category need a breakdown of it — for a category with one rate exactly one, for the categories with several
            // rates at least one. From the items only, as the standard says: the artefacts of CII also ask the other way round (a breakdown
            // without items), those of UBL do not; a breakdown without items is found by 08 where its taxable amount is not zero
            yield return R(Id(1), k.PerRate
                    ? $"Positionen, Nachlässe oder Zuschläge mit „{k.German}“ ({k.Code}) verlangen eine Steueraufschlüsselung dieser Kategorie."
                    : $"Mit „{k.German}“ ({k.Code}) gehören Posten dieser Kategorie und genau eine Steueraufschlüsselung zusammen.",
                k.PerRate
                    ? $"Lines, allowances or charges with „{k.English}“ ({k.Code}) need a VAT breakdown of this category."
                    : $"With „{k.English}“ ({k.Code}) items of this category and exactly one VAT breakdown go together.",
                c =>
                {
                    if (!Of(c, present: true).Any()) return [];
                    var breakdowns = Breakdowns(c).Count();
                    return (k.PerRate ? breakdowns == 0 : breakdowns != 1) ? [(c.Root, null)] : [];
                });

            // 02 to 04: the identifiers the category needs (or forbids)
            for (var kind = 0; kind <= 2; kind++)
            {
                var which = kind;
                var (needDe, needEn) = k.Code switch
                {
                    "AE" => ("eine Kennung des Verkäufers (BT-31, BT-32 oder BT-63) und des Käufers (BT-48 oder BT-47)",
                             "an identifier of the seller (BT-31, BT-32 or BT-63) and of the buyer (BT-48 or BT-47)"),
                    "K" => ("die USt-IdNr. des Verkäufers (BT-31 oder BT-63) und des Käufers (BT-48)", "the VAT identifier of the seller (BT-31 or BT-63) and of the buyer (BT-48)"),
                    "G" => ("die USt-IdNr. des Verkäufers (BT-31 oder BT-63)", "the VAT identifier of the seller (BT-31 or BT-63)"),
                    "O" => ("keine USt-IdNr. (BT-31, BT-63, BT-48) — sie dürfen nicht angegeben sein", "no VAT identifier (BT-31, BT-63, BT-48) — they must not be given"),
                    _ => ("eine Kennung des Verkäufers (BT-31, BT-32 oder BT-63)", "an identifier of the seller (BT-31, BT-32 or BT-63)"),
                };
                yield return R(Id(2 + which), $"{Kind(which, true)} mit „{k.German}“ ({k.Code}) verlangen {needDe}.", $"{Kind(which, false)} with „{k.English}“ ({k.Code}) need {needEn}.",
                    c => Of(c, which, present: true).Any() && !IdentifiersFit(c, k.Code) ? [(Of(c, which, present: true).First().Group, null)] : []);
            }

            // 05 to 07: the rate
            for (var kind = 0; kind <= 2; kind++)
            {
                var which = kind;
                var (rateDe, rateEn) = k.Rate switch
                {
                    Rate.Positive => ("einen Steuersatz größer null", "a VAT rate greater than zero"),
                    Rate.Zero => ("den Steuersatz 0", "the VAT rate 0"),
                    Rate.NotNegative => ("einen Steuersatz von null oder mehr", "a VAT rate of zero or more"),
                    _ => ("keinen Steuersatz", "no VAT rate"),
                };
                yield return R(Id(5 + which), $"{Kind(which, true)} mit „{k.German}“ ({k.Code}) verlangen {rateDe}.", $"{Kind(which, false)} with „{k.English}“ ({k.Code}) need {rateEn}.",
                    c => Of(c, which).Where(i => !RateFits(k.Rate, i.Rate, i.Group, which)).Select(i => (i.Group, (InvoiceField?)null)));
            }

            // 08: the taxable amount adds up (per rate for the categories with several rates — a breakdown of them without a rate has no rate
            // to add up for, BR-48 says what is wrong); a missing taxable amount does not add up either
            yield return R(Id(8), $"Der Basisbetrag einer Aufschlüsselung „{k.German}“ (BT-116) ist nicht Positionen − Nachlässe + Zuschläge dieser Kategorie{(k.PerRate ? " und dieses Satzes" : "")}.",
                $"The taxable amount of a „{k.English}“ breakdown (BT-116) is not lines − allowances + charges of this category{(k.PerRate ? " and rate" : "")}.",
                c => Breakdowns(c).Where(b => !(k.PerRate && VatNumber(b, "BT-119") is null) && Number(b, "BT-116") != Expected(c, k, b))
                    .Select(b => (b, b.Field("BT-116"))));

            // 09: the tax amount — with a rate as BR-CO-17 (within one unit of the currency; a missing amount or rate does not fit), without
            // one zero; a missing tax amount is not zero either
            yield return R(Id(9), k.Rate is Rate.Positive or Rate.NotNegative
                    ? $"Der Steuerbetrag einer Aufschlüsselung „{k.German}“ (BT-117) passt nicht zu Basisbetrag × Satz / 100 (Abweichung von einer Währungseinheit oder mehr)."
                    : $"Der Steuerbetrag einer Aufschlüsselung „{k.German}“ (BT-117) ist nicht 0.",
                k.Rate is Rate.Positive or Rate.NotNegative
                    ? $"The tax amount of a „{k.English}“ breakdown (BT-117) does not fit taxable amount × rate / 100 (a difference of one unit of the currency or more)."
                    : $"The tax amount of a „{k.English}“ breakdown (BT-117) is not 0.",
                c => Breakdowns(c).Where(b => k.Rate is Rate.Positive or Rate.NotNegative
                        ? !TaxFits(Number(b, "BT-116"), VatNumber(b, "BT-119"), Number(b, "BT-117"), zeroRateMeansZero: false)
                        : Number(b, "BT-117") != 0)
                    .Select(b => (b, b.Field("BT-117"))));

            // 10: the exemption reason
            yield return R(Id(10), k.Exempt
                    ? $"Eine Aufschlüsselung „{k.German}“ verlangt einen Befreiungsgrund (BT-120) oder seinen Code (BT-121)."
                    : $"Eine Aufschlüsselung „{k.German}“ darf keinen Befreiungsgrund (BT-120, BT-121) haben.",
                k.Exempt
                    ? $"A „{k.English}“ breakdown needs an exemption reason (BT-120) or its code (BT-121)."
                    : $"A „{k.English}“ breakdown must not have an exemption reason (BT-120, BT-121).",
                c => Breakdowns(c).Where(b => k.Exempt ? !Has(b, "BT-120") && !Has(b, "BT-121") : Has(b, "BT-120") || Has(b, "BT-121")).Select(b => (b, (InvoiceField?)null)));

            if (k.Code == "K")
            {
                yield return R("BR-IC-11", "Eine Aufschlüsselung „innergemeinschaftliche Lieferung“ verlangt das Lieferdatum (BT-72) oder den Rechnungszeitraum (BG-14).",
                    "An intra-community supply breakdown needs the actual delivery date (BT-72) or the invoicing period (BG-14).",
                    c => Breakdowns(c).Any() && !c.Root.AllFields().Any(f => f.Id is "BT-72" or "BT-73" or "BT-74" && !string.IsNullOrWhiteSpace(f.Text))
                        ? [(Breakdowns(c).First(), null)] : []);
                yield return R("BR-IC-12", "Eine Aufschlüsselung „innergemeinschaftliche Lieferung“ verlangt das Lieferland (BT-80).",
                    "An intra-community supply breakdown needs the deliver-to country code (BT-80).",
                    c => Breakdowns(c).Any() && !c.Root.AllFields().Any(f => f.Id == "BT-80" && !string.IsNullOrWhiteSpace(f.Text)) ? [(Breakdowns(c).First(), null)] : []);
            }
            // „not subject to VAT“: no other breakdown and no item of another category beside it (an item or breakdown without a category is
            // none of another one — BR-CO-04, BR-32, BR-37, BR-47 say what is missing; both artefacts so)
            if (k.Code == "O")
            {
                yield return R("BR-O-11", "Neben einer Aufschlüsselung „nicht steuerbar“ darf es keine andere Steueraufschlüsselung geben.",
                    "Beside a „not subject to VAT“ breakdown there must be no other VAT breakdown.",
                    c => Breakdowns(c).Any() && c.All("BG-23").Any(b => VatText(b, "BT-118") is { } other && other != "O") ? [(c.Root, null)] : []);
                foreach (var (id, kind, de, en) in new[]
                         {
                             ("BR-O-12", 0, "keine Position", "no invoice line"), ("BR-O-13", 1, "keinen Nachlass auf Rechnungsebene", "no document level allowance"),
                             ("BR-O-14", 2, "keinen Zuschlag auf Rechnungsebene", "no document level charge"),
                         })
                {
                    var which = kind;
                    yield return R(id, $"Neben „nicht steuerbar“ darf es {de} mit einer anderen Steuerkategorie geben.", $"Beside „not subject to VAT“ there must be {en} of another VAT category.",
                        c => Breakdowns(c).Any()
                            ? (which == 0 ? LineItems(c) : c.VatItems.Where(i => i.Kind == which)).Where(i => i.Category is { } other && other != "O")
                                .Select(i => (i.Group, (InvoiceField?)null))
                            : []);
                }
            }
        }
    }

    /// <summary>Whether the identifiers of seller and buyer fit what the category asks.</summary>
    private static bool IdentifiersFit(Context c, string code)
    {
        bool sellerVat = AnyId(c, "BG-4", "BT-31") || AnyId(c, "BG-11", "BT-63");
        bool seller = sellerVat || AnyId(c, "BG-4", "BT-32");
        return code switch
        {
            "AE" => seller && AnyId(c, "BG-7", "BT-48", "BT-47"),
            "K" => sellerVat && AnyId(c, "BG-7", "BT-48"),
            "G" => sellerVat,
            "O" => !sellerVat && !AnyId(c, "BG-7", "BT-48"),
            _ => seller,
        };
    }

    /// <summary>Whether the rate of an item fits (a missing rate fits only „no rate“).</summary>
    private static bool RateFits(Rate rate, decimal? value, InvoiceGroup group, int kind)
    {
        var given = kind == 0 ? Fields(group, "BT-152").Any(IsVat) : VatField(group, kind == 1 ? "BT-96" : "BT-103") is not null;
        return rate switch
        {
            Rate.Positive => value > 0,
            Rate.Zero => value == 0,
            Rate.NotNegative => value >= 0,
            _ => !given,
        };
    }

    /// <summary>
    /// The taxable amount a breakdown must have: lines − allowances + charges of its category (and rate, where it has several), exactly — the
    /// artefacts of CII round the sums or allow one unit, those of UBL compare exactly or allow one unit, by category; so as the standard writes it.
    /// </summary>
    private static decimal Expected(Context c, Category k, InvoiceGroup breakdown) => c.Sum(k.Code, VatNumber(breakdown, "BT-119"), k.PerRate);
}
