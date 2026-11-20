using System.Globalization;

namespace PdfKnead.EInvoice;

/// <summary>How heavy a violated rule weighs (as the rule set states it).</summary>
public enum InvoiceRuleSeverity
{
    /// <summary>The rule set calls it an error.</summary>
    Fatal,

    /// <summary>The rule set calls it a warning.</summary>
    Warning,

    /// <summary>The rule set gives it as information (XRechnung: BR-DE-TMP-32).</summary>
    Information,
}

/// <summary>
/// A business rule (KONZEPT.md, section 5.24, stage 4): its identifier as the rule set names it, the source with its version, the weight, and
/// a short description of our own in German and English — never the text of the standard.
/// </summary>
public sealed record InvoiceRule(string Id, string Source, InvoiceRuleSeverity Severity, string German, string English)
{
    /// <summary>The description in the current language.</summary>
    public string Description => Tr.Current.IsGerman ? German : English;
}

/// <summary>
/// A violated rule and where: the group it concerns, the term (null if the term is missing or the element is none), for a code list rule
/// the code as written (null otherwise; it may be missing), on the last listed violation of a rule how many more were found but not
/// listed (<see cref="InvoiceRules.MaxListedPerRule"/>), and for a finding of the schema check its place in the XML (the path of the element
/// or attribute, „/rsm:CrossIndustryInvoice/…/ram:LineTotalAmount“; null for the others) — the value is then the value as written.
/// </summary>
public sealed record InvoiceRuleViolation(InvoiceRule Rule, InvoiceGroup Group, InvoiceField? Field, string? Value = null, int Further = 0, string? Place = null);

/// <summary>
/// The business rules of EN 16931 checked on the readable form (stage 4, part a; KONZEPT.md, section 5.24, „Weg für Stufe 4“): the integrity
/// constraints (BR-01 to BR-65), the conditions (BR-CO-03 to BR-CO-26), the decimals of amounts (BR-DEC) and the split payment (BR-B).
/// Written from EN 16931-1 in code of our own — on the business terms, so once for CII and UBL; the calculations on the values of the XML only
/// (P6), in decimals. A violation is an „Auffälligkeit“ with the rule's identifier; no violation is „keine Auffälligkeit gefunden“, never
/// „gültig“.
/// </summary>
public static partial class InvoiceRules
{
    /// <summary>The source and version of the rules of this part.</summary>
    public const string Source = "EN 16931-1:2017+A1:2019";

    /// <summary>A rule and how it is found; the code list rules find with the code (<paramref name="FindWithValue"/>), the schema check and
    /// the hints with the place in the XML as well (<paramref name="FindWithPlace"/>).</summary>
    private sealed record RuleCheck(InvoiceRule Rule, Func<Context, IEnumerable<(InvoiceGroup Group, InvoiceField? Field)>> Find,
        Func<Context, IEnumerable<(InvoiceGroup Group, InvoiceField? Field, string? Value)>>? FindWithValue = null,
        Func<Context, IEnumerable<(InvoiceGroup Group, InvoiceField? Field, string? Value, XmlPlace Place)>>? FindWithPlace = null);

    /// <summary>
    /// Where a finding stands in the XML: its element and, for an attribute, the name. The path is made only for a finding that is listed
    /// (<see cref="Check(InvoiceData, int, CancellationToken)"/>), not for the ones the last listed only counts (FG 850).
    /// </summary>
    private readonly record struct XmlPlace(InvoiceElement Element, string? Attribute);

    /// <summary>What the rules look at: the invoice, its groups by identifier at any depth, its lines.</summary>
    private sealed class Context
    {
        public Context(InvoiceData invoice)
        {
            Invoice = invoice;
            Root = invoice.Root;
            var pending = new Stack<(InvoiceGroup Group, int Depth)>();
            pending.Push((Root, 0));
            while (pending.Count > 0)
            {
                var (g, depth) = pending.Pop();
                if (!_byId.TryGetValue(g.Id, out var list)) _byId[g.Id] = list = [];
                list.Add(g);
                _depth[g] = depth;
                for (var i = g.Groups.Count - 1; i >= 0; i--) pending.Push((g.Groups[i], depth + 1));
            }
            // the lines of the rules: every invoice line; in CII the sub lines are lines of their own as well (their syntax has only lines)
            Lines = [.. All("BG-25"), .. invoice.Kind.Syntax == InvoiceSyntax.Cii ? All("BG-DEX-01") : []];
        }

        private readonly Dictionary<string, List<InvoiceGroup>> _byId = new(StringComparer.Ordinal);

        public InvoiceData Invoice { get; }

        public InvoiceGroup Root { get; }

        public List<InvoiceGroup> Lines { get; }

        /// <summary>The items with their VAT category (lines, document level allowances and charges), read once per invoice.</summary>
        public List<VatItem> VatItems => _vatItems ??= Items(this);

        private List<VatItem>? _vatItems;

        /// <summary>The lines of the rules (in CII with the sub lines) with their VAT category, read once per invoice.</summary>
        public List<VatItem> LineVatItems => _lineVatItems ??= [.. Lines.Select(Line)];

        private List<VatItem>? _lineVatItems;

        /// <summary>
        /// Every line and every sub line (both syntaxes) with its VAT category, read once per invoice: which categories the invoice has, for
        /// the rules 01 and 02 of the categories — as both artefacts count them (CII every line item, UBL every item VAT category).
        /// </summary>
        public List<VatItem> CategoryLineItems => _categoryLineItems ??= [.. All("BG-25").Concat(All("BG-DEX-01")).Select(Line)];

        private List<VatItem>? _categoryLineItems;

        /// <summary>
        /// Lines − allowances + charges per VAT category and rate, added up once per invoice: the rules 08 ask it for every breakdown, and a
        /// prepared invoice with thousands of breakdowns and lines must not cost their product (FG 832).
        /// </summary>
        public decimal Sum(string code, decimal? rate, bool perRate)
        {
            if (_sums is null)
            {
                _sums = [];
                foreach (var i in VatItems)
                {
                    if (i.Category is not { } category) continue;
                    var signed = i.Kind == 1 ? -i.Amount : i.Amount;
                    _sums[(category, i.Rate, true)] = _sums.GetValueOrDefault((category, i.Rate, true)) + signed;
                    _sums[(category, null, false)] = _sums.GetValueOrDefault((category, null, false)) + signed;
                }
            }
            return _sums.GetValueOrDefault((code, perRate ? rate : null, perRate));
        }

        private Dictionary<(string Code, decimal? Rate, bool PerRate), decimal>? _sums;

        /// <summary>The syntax view of the XRechnung rules, made once per invoice (InvoiceRules.XRechnung.cs).</summary>
        public object? SyntaxView { get; set; }

        /// <summary>Which specification of XRechnung 3.0 the invoice names (none: the XRechnung rules do not apply), found once per invoice.</summary>
        public XRechnungKind? XRechnung { get; set; }

        /// <summary>What the schema check found, made once per invoice (InvoiceRules.Schema.cs).</summary>
        public object? SchemaFindings { get; set; }

        /// <summary>The XML of an EXTENDED invoice as its rules read it, made once per invoice (InvoiceRules.Extended.cs).</summary>
        public object? ExtendedView { get; set; }

        /// <summary>The element rules of its profile the invoice breaks, by identifier (one walk per invoice; InvoiceRules.ProfileStructure.cs).</summary>
        public Dictionary<string, List<(InvoiceElement Element, string? Value)>>? ProfileFindings { get; set; }

        /// <summary>The codes not in their list, by rule (one walk over the XML per invoice).</summary>
        public Dictionary<string, List<(InvoiceElement Element, string? Value)>> CodeFindings => _codeFindings ??= InvoiceRules.CodeFindings(this);

        private Dictionary<string, List<(InvoiceElement Element, string? Value)>>? _codeFindings;

        /// <summary>The group nearest around an element of the XML (the innermost one whose element holds it); the invoice if none.</summary>
        public InvoiceGroup GroupOf(InvoiceElement element)
        {
            if (_groupOf is null)
            {
                _groupOf = [];
                foreach (var list in _byId.Values)
                    foreach (var g in list)
                        if (g.Element is { } e && (!_groupOf.TryGetValue(e, out var known) || Depth(g) > Depth(known))) _groupOf[e] = g;
            }
            for (var e = element; e is not null; e = e.Parent)
                if (_groupOf.TryGetValue(e, out var g)) return g;
            return Root;
        }

        private Dictionary<InvoiceElement, InvoiceGroup>? _groupOf;

        private readonly Dictionary<InvoiceGroup, int> _depth = [];

        private int Depth(InvoiceGroup g) => _depth.GetValueOrDefault(g);

        /// <summary>The term an element of the XML is (null if it is none).</summary>
        public InvoiceField? FieldOf(InvoiceElement element)
        {
            if (_fieldOf is null)
            {
                _fieldOf = [];
                foreach (var f in Root.AllFields())
                    if (!f.Node.IsAttribute) _fieldOf.TryAdd(f.Node.Element, f);
            }
            return _fieldOf.GetValueOrDefault(element);
        }

        private Dictionary<InvoiceElement, InvoiceField>? _fieldOf;

        /// <summary>The groups of that identifier anywhere, in document order.</summary>
        public IReadOnlyList<InvoiceGroup> All(string id) => _byId.TryGetValue(id, out var list) ? list : [];

        public InvoiceGroup? First(string id) => All(id) is [var g, ..] ? g : null;
    }

    private static readonly RuleCheck[] Checks = Build();

    /// <summary>Every rule of this part.</summary>
    public static IReadOnlyList<InvoiceRule> All { get; } = [.. Checks.Select(c => c.Rule)];

    /// <summary>
    /// At most so many violations of one rule are listed, the last of them counts the others (FG 838): a prepared invoice with 200 000 empty
    /// elements must not make a list of 200 000, which costs memory and helps no reader.
    /// </summary>
    public const int MaxListedPerRule = 100;

    /// <summary>The violated rules of an invoice, in the order of the rules.</summary>
    public static IReadOnlyList<InvoiceRuleViolation> Check(InvoiceData invoice, CancellationToken cancel = default) => Check(invoice, MaxListedPerRule, cancel);

    /// <summary>The violated rules of an invoice, at most <paramref name="maxListedPerRule"/> listed per rule (all of them for a comparison place by place).</summary>
    public static IReadOnlyList<InvoiceRuleViolation> Check(InvoiceData invoice, int maxListedPerRule, CancellationToken cancel = default)
    {
        var context = new Context(invoice);
        var found = new List<InvoiceRuleViolation>();
        foreach (var check in Checks)
        {
            cancel.ThrowIfCancellationRequested();
            if (!AppliesInProfile(check.Rule, context)) continue;   // the rules of the profile only (InvoiceRules.Profiles.cs)
            var listed = 0;
            var further = 0;
            IEnumerable<(InvoiceGroup Group, InvoiceField? Field, string? Value, XmlPlace? Place)> all = check.FindWithPlace is { } withPlace
                ? withPlace(context).Select(f => (f.Group, f.Field, f.Value, (XmlPlace?)f.Place))
                : check.FindWithValue is { } withValue
                    ? withValue(context).Select(f => (f.Group, f.Field, f.Value, (XmlPlace?)null))
                    : check.Find(context).Select(f => (f.Group, f.Field, (string?)null, (XmlPlace?)null));
            foreach (var (group, field, value, place) in all)
            {
                if (listed < maxListedPerRule)
                {
                    // the path only now, for a listed finding (FG 850)
                    found.Add(new InvoiceRuleViolation(check.Rule, group, field, value, 0, place is { } at ? PlaceOf(at.Element, at.Attribute) : null));
                    listed++;
                }
                else further++;
            }
            if (further > 0) found[^1] = found[^1] with { Further = further };
        }
        return found;
    }

    // ------------------------------------------------------------ helpers

    private static bool Has(InvoiceGroup g, string id) => g.Fields.Any(f => f.Id == id && !string.IsNullOrWhiteSpace(f.Text) || f.Id == id && f.Node is { IsAttribute: false, Element.Binary: not null });

    private static string? Text(InvoiceGroup g, string id) => g.Field(id)?.Text?.Trim();

    private static decimal? Number(InvoiceField? f) => f?.Text is { } t ? InvoiceFormat.Number(t) : null;

    private static decimal? Number(InvoiceGroup g, string id) => Number(g.Field(id));

    private static IEnumerable<(InvoiceGroup, InvoiceField?)> Missing(InvoiceGroup g, string id)
    {
        if (!Has(g, id)) yield return (g, null);
    }

    private static IEnumerable<(InvoiceGroup, InvoiceField?)> MissingIn(IEnumerable<InvoiceGroup> groups, string id) => groups.SelectMany(g => Missing(g, id));

    private static IEnumerable<(InvoiceGroup, InvoiceField?)> MissingBoth(IEnumerable<InvoiceGroup> groups, string a, string b) =>
        groups.Where(g => !Has(g, a) && !Has(g, b)).Select(g => (g, (InvoiceField?)null));

    /// <summary>
    /// Every term of that identifier in the group and below it — not in the groups of another line held in it (a sub line, BG-DEX-01, below
    /// its line in both syntaxes): a line without item name, price or VAT category must not borrow those of its sub lines (FG 839).
    /// </summary>
    private static IEnumerable<InvoiceField> Fields(InvoiceGroup g, string id)
    {
        var pending = new Stack<InvoiceGroup>();
        pending.Push(g);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var f in current.Fields)
                if (f.Id == id) yield return f;
            for (var i = current.Groups.Count - 1; i >= 0; i--)
                if (current.Groups[i].Id is not ("BG-25" or "BG-DEX-01")) pending.Push(current.Groups[i]);
        }
    }

    /// <summary>The decimals of a number as written (digits after the point).</summary>
    private static int Decimals(string text)
    {
        var t = text.Trim();
        var dot = t.IndexOf('.');
        return dot < 0 ? 0 : t.Length - dot - 1;
    }

    private static decimal Sum(IEnumerable<InvoiceField> fields) => fields.Sum(f => Number(f) ?? 0);

    /// <summary>
    /// A calculated amount rounded to the cent as the validation artefacts round it (half a cent up, towards positive infinity); the stated
    /// amount is compared as written, so a stated amount with a third decimal does not equal it (BR-DEC says why).
    /// </summary>
    private static decimal Cents(decimal value) => Math.Floor(value * 100 + 0.5m) / 100;

    /// <summary>A number rounded to a whole one the same way (for „rounds to zero“).</summary>
    private static decimal Whole(decimal value) => Math.Floor(value + 0.5m);

    /// <summary>
    /// Whether a VAT category or rate belongs to the tax VAT: in CII the TypeCode of its tax element, in UBL the identifier of its TaxScheme
    /// (case and surrounding space not counted). A category of another tax — or of a tax element that names none — is no VAT category, as the
    /// syntax bindings and the validation artefacts of both syntaxes read it.
    /// </summary>
    private static bool IsVat(InvoiceField f)
    {
        if (f.Node.IsAttribute || f.Node.Element.Parent is not { } tax) return true;
        foreach (var child in tax.Children)
        {
            if (child.Local == "TypeCode") return string.Equals(child.Text?.Trim(), "VAT", StringComparison.OrdinalIgnoreCase);
            if (child.Local == "TaxScheme")
                return child.Children.FirstOrDefault(c => c.Local == "ID") is { } id && string.Equals(id.Text?.Trim(), "VAT", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>The term of a group if it belongs to VAT (categories BT-95, BT-102, BT-118, BT-151 and their rates).</summary>
    private static InvoiceField? VatField(InvoiceGroup g, string id) => g.Fields.FirstOrDefault(f => f.Id == id && IsVat(f));

    private static string? VatText(InvoiceGroup g, string id) => VatField(g, id)?.Text?.Trim();

    private static decimal? VatNumber(InvoiceGroup g, string id) => Number(VatField(g, id));

    /// <summary>
    /// Whether a term is a VAT identifier: the tax representative's (BT-63) in CII only with the scheme „VA“ (the display mapping takes any
    /// tax registration of the representative; seller and buyer are mapped with their scheme already).
    /// </summary>
    private static bool IsVatId(Context c, InvoiceField f) =>
        f.Id != "BT-63" || c.Invoice.Kind.Syntax != InvoiceSyntax.Cii || f.Node.IsAttribute || f.Attribute("schemeID")?.Trim() == "VA";

    /// <summary>The text of a child element (by local name) of an element; null if it has none.</summary>
    private static string? ChildText(InvoiceElement? e, string local) => e?.Children.FirstOrDefault(c => c.Local == local)?.Text?.Trim();

    private static RuleCheck R(string id, string german, string english, Func<Context, IEnumerable<(InvoiceGroup, InvoiceField?)>> find,
        InvoiceRuleSeverity severity = InvoiceRuleSeverity.Fatal) =>
        new(new InvoiceRule(id, Source, severity, german, english), find);

    // ------------------------------------------------------------ the rules

    private static RuleCheck[] Build()
    {
        var list = new List<RuleCheck>
        {
            // integrity constraints: what an invoice must contain
            R("BR-01", "Die Spezifikationskennung (BT-24) fehlt.", "The specification identifier (BT-24) is missing.",
                c => c.Root.AllFields().Any(f => f.Id == "BT-24" && !string.IsNullOrWhiteSpace(f.Text)) ? [] : [(c.First("BG-2") ?? c.Root, null)]),
            R("BR-02", "Die Rechnungsnummer (BT-1) fehlt.", "The invoice number (BT-1) is missing.", c => Missing(c.Root, "BT-1")),
            R("BR-03", "Das Rechnungsdatum (BT-2) fehlt.", "The invoice issue date (BT-2) is missing.", c => Missing(c.Root, "BT-2")),
            R("BR-04", "Der Code der Rechnungsart (BT-3) fehlt.", "The invoice type code (BT-3) is missing.", c => Missing(c.Root, "BT-3")),
            R("BR-05", "Der Code der Rechnungswährung (BT-5) fehlt.", "The invoice currency code (BT-5) is missing.", c => Missing(c.Root, "BT-5")),
            R("BR-06", "Der Name des Verkäufers (BT-27) fehlt.", "The seller name (BT-27) is missing.",
                c => c.First("BG-4") is { } s ? Missing(s, "BT-27") : [(c.Root, null)]),
            R("BR-07", "Der Name des Käufers (BT-44) fehlt.", "The buyer name (BT-44) is missing.",
                c => c.First("BG-7") is { } b ? Missing(b, "BT-44") : [(c.Root, null)]),
            R("BR-08", "Die Anschrift des Verkäufers (BG-5) fehlt.", "The seller postal address (BG-5) is missing.",
                c => c.First("BG-4") is { } s && s.GroupsOf("BG-5").Any() ? [] : [(c.First("BG-4") ?? c.Root, null)]),
            R("BR-09", "Der Ländercode in der Anschrift des Verkäufers (BT-40) fehlt.", "The seller country code (BT-40) is missing.",
                c => MissingIn(c.First("BG-4")?.GroupsOf("BG-5") ?? [], "BT-40")),
            R("BR-10", "Die Anschrift des Käufers (BG-8) fehlt.", "The buyer postal address (BG-8) is missing.",
                c => c.First("BG-7") is { } b && b.GroupsOf("BG-8").Any() ? [] : [(c.First("BG-7") ?? c.Root, null)]),
            R("BR-11", "Der Ländercode in der Anschrift des Käufers (BT-55) fehlt.", "The buyer country code (BT-55) is missing.",
                c => MissingIn(c.First("BG-7")?.GroupsOf("BG-8") ?? [], "BT-55")),
            R("BR-12", "Die Summe der Positionsbeträge (BT-106) fehlt.", "The sum of invoice line net amounts (BT-106) is missing.",
                c => Missing(c.First("BG-22") ?? c.Root, "BT-106")),
            R("BR-13", "Der Gesamtbetrag ohne Umsatzsteuer (BT-109) fehlt.", "The invoice total without VAT (BT-109) is missing.",
                c => Missing(c.First("BG-22") ?? c.Root, "BT-109")),
            R("BR-14", "Der Gesamtbetrag mit Umsatzsteuer (BT-112) fehlt.", "The invoice total with VAT (BT-112) is missing.",
                c => Missing(c.First("BG-22") ?? c.Root, "BT-112")),
            R("BR-15", "Der fällige Betrag (BT-115) fehlt.", "The amount due for payment (BT-115) is missing.",
                c => Missing(c.First("BG-22") ?? c.Root, "BT-115")),
            R("BR-16", "Die Rechnung hat keine Position (BG-25).", "The invoice has no invoice line (BG-25).",
                c => c.All("BG-25").Count == 0 ? [(c.Root, null)] : []),
            R("BR-17", "Der Name des Zahlungsempfängers (BT-59) fehlt.", "The payee name (BT-59) is missing.", c => MissingIn(c.All("BG-10"), "BT-59")),
            R("BR-18", "Der Name des steuerlichen Vertreters (BT-62) fehlt.", "The tax representative name (BT-62) is missing.",
                c => MissingIn(c.All("BG-11"), "BT-62")),
            R("BR-19", "Die Anschrift des steuerlichen Vertreters (BG-12) fehlt.", "The tax representative postal address (BG-12) is missing.",
                c => c.All("BG-11").Where(g => !g.GroupsOf("BG-12").Any()).Select(g => (g, (InvoiceField?)null))),
            R("BR-20", "Der Ländercode in der Anschrift des steuerlichen Vertreters (BT-69) fehlt.", "The tax representative country code (BT-69) is missing.",
                c => MissingIn(c.All("BG-12"), "BT-69")),
            R("BR-21", "Eine Position hat keine Kennung (BT-126).", "An invoice line has no identifier (BT-126).", c => MissingIn(c.Lines, "BT-126")),
            R("BR-22", "Eine Position hat keine Menge (BT-129).", "An invoice line has no quantity (BT-129).", c => MissingIn(c.Lines, "BT-129")),
            R("BR-23", "Eine Position hat keine Einheit der Menge (BT-130).", "An invoice line has no unit of measure (BT-130).", c => MissingIn(c.Lines, "BT-130")),
            R("BR-24", "Eine Position hat keinen Nettobetrag (BT-131).", "An invoice line has no net amount (BT-131).", c => MissingIn(c.Lines, "BT-131")),
            R("BR-25", "Eine Position hat keinen Artikelnamen (BT-153).", "An invoice line has no item name (BT-153).",
                c => c.Lines.Where(l => !Fields(l, "BT-153").Any(f => !string.IsNullOrWhiteSpace(f.Text))).Select(l => (l, (InvoiceField?)null))),
            R("BR-26", "Eine Position hat keinen Nettopreis (BT-146).", "An invoice line has no item net price (BT-146).",
                c => c.Lines.Where(l => !Fields(l, "BT-146").Any()).Select(l => (l, (InvoiceField?)null))),
            // a line without a net price has none that is zero or more either (as the validation artefacts of both syntaxes count it)
            R("BR-27", "Ein Nettopreis (BT-146) ist negativ oder fehlt.", "An item net price (BT-146) is negative or missing.",
                c => c.Lines.SelectMany(l => Fields(l, "BT-146").Any()
                    ? Fields(l, "BT-146").Where(f => Number(f) < 0).Select(f => (l, (InvoiceField?)f))
                    : [(l, (InvoiceField?)null)])),
            R("BR-28", "Ein Bruttopreis (BT-148) ist negativ.", "An item gross price (BT-148) is negative.",
                c => c.Lines.SelectMany(l => Fields(l, "BT-148").Where(f => Number(f) < 0).Select(f => (l, (InvoiceField?)f)))),
            R("BR-29", "Das Ende des Rechnungszeitraums (BT-74) liegt vor seinem Anfang (BT-73).", "The invoicing period ends (BT-74) before it starts (BT-73).",
                c => Before(c.All("BG-14"), "BT-73", "BT-74")),
            R("BR-30", "Das Ende des Positionszeitraums (BT-135) liegt vor seinem Anfang (BT-134).", "The line period ends (BT-135) before it starts (BT-134).",
                c => Before(c.All("BG-26"), "BT-134", "BT-135")),
            R("BR-31", "Ein Nachlass auf Rechnungsebene hat keinen Betrag (BT-92).", "A document level allowance has no amount (BT-92).", c => MissingIn(c.All("BG-20"), "BT-92")),
            R("BR-32", "Ein Nachlass auf Rechnungsebene hat keine Steuerkategorie (BT-95).", "A document level allowance has no VAT category (BT-95).", c => NoVat(c.All("BG-20"), "BT-95")),
            R("BR-33", "Ein Nachlass auf Rechnungsebene hat weder Grund (BT-97) noch Code (BT-98).", "A document level allowance has neither reason (BT-97) nor reason code (BT-98).",
                c => MissingBoth(c.All("BG-20"), "BT-97", "BT-98")),
            R("BR-36", "Ein Zuschlag auf Rechnungsebene hat keinen Betrag (BT-99).", "A document level charge has no amount (BT-99).", c => MissingIn(c.All("BG-21"), "BT-99")),
            R("BR-37", "Ein Zuschlag auf Rechnungsebene hat keine Steuerkategorie (BT-102).", "A document level charge has no VAT category (BT-102).", c => NoVat(c.All("BG-21"), "BT-102")),
            R("BR-38", "Ein Zuschlag auf Rechnungsebene hat weder Grund (BT-104) noch Code (BT-105).", "A document level charge has neither reason (BT-104) nor reason code (BT-105).",
                c => MissingBoth(c.All("BG-21"), "BT-104", "BT-105")),
            R("BR-41", "Ein Nachlass auf Positionsebene hat keinen Betrag (BT-136).", "An invoice line allowance has no amount (BT-136).", c => MissingIn(c.All("BG-27"), "BT-136")),
            R("BR-42", "Ein Nachlass auf Positionsebene hat weder Grund (BT-139) noch Code (BT-140).", "An invoice line allowance has neither reason (BT-139) nor reason code (BT-140).",
                c => MissingBoth(c.All("BG-27"), "BT-139", "BT-140")),
            R("BR-43", "Ein Zuschlag auf Positionsebene hat keinen Betrag (BT-141).", "An invoice line charge has no amount (BT-141).", c => MissingIn(c.All("BG-28"), "BT-141")),
            R("BR-44", "Ein Zuschlag auf Positionsebene hat weder Grund (BT-144) noch Code (BT-145).", "An invoice line charge has neither reason (BT-144) nor reason code (BT-145).",
                c => MissingBoth(c.All("BG-28"), "BT-144", "BT-145")),
            R("BR-45", "Eine Steueraufschlüsselung hat keinen Basisbetrag (BT-116).", "A VAT breakdown has no taxable amount (BT-116).", c => MissingIn(c.All("BG-23"), "BT-116")),
            R("BR-46", "Eine Steueraufschlüsselung hat keinen Steuerbetrag (BT-117).", "A VAT breakdown has no tax amount (BT-117).", c => MissingIn(c.All("BG-23"), "BT-117")),
            R("BR-47", "Eine Steueraufschlüsselung hat keine Steuerkategorie (BT-118).", "A VAT breakdown has no VAT category (BT-118).", c => NoVat(c.All("BG-23"), "BT-118")),
            R("BR-48", "Eine Steueraufschlüsselung hat keinen Steuersatz (BT-119), obwohl sie steuerbar ist.", "A VAT breakdown has no VAT rate (BT-119) although it is subject to VAT.",
                c => c.All("BG-23").Where(g => VatText(g, "BT-118") != "O" && VatField(g, "BT-119") is null).Select(g => (g, (InvoiceField?)null))),
            R("BR-49", "Eine Zahlungsanweisung hat keinen Code der Zahlungsart (BT-81).", "A payment instruction has no payment means type code (BT-81).",
                c => MissingIn(c.All("BG-16"), "BT-81")),
            // as the validation artefacts of both syntaxes: the account of a payment by credit transfer (code 30 or 58)
            R("BR-50", "Eine Überweisung (Code 30 oder 58) hat keine Kontokennung (BT-84).", "A credit transfer (code 30 or 58) has no payment account identifier (BT-84).",
                c => MissingIn(c.All("BG-17").Where(a => (ChildText(a.Element?.Parent, "TypeCode") ?? ChildText(a.Element?.Parent, "PaymentMeansCode")) is "30" or "58"), "BT-84")),
            R("BR-51", "Die Kartennummer (BT-87) hat nicht 4 bis 6 Ziffern (nur die letzten Ziffern sind erlaubt).",
                "The payment card number (BT-87) does not have 4 to 6 digits (only the last digits are allowed).",
                c => c.All("BG-18").SelectMany(g => g.Fields.Where(f => f.Id == "BT-87" && (f.Text?.Trim().Length ?? 0) is < 4 or > 6).Select(f => (g, (InvoiceField?)f)))),
            // in CII a referenced document is a supporting one only with the type code 916 (the display shows every referenced document)
            R("BR-52", "Eine rechnungsbegründende Unterlage hat keine Kennung (BT-122).", "A supporting document has no reference (BT-122).",
                c => MissingIn(c.All("BG-24").Where(g => c.Invoice.Kind.Syntax != InvoiceSyntax.Cii || ChildText(g.Element, "TypeCode") == "916"), "BT-122")),
            R("BR-53", "Mit Buchungswährung (BT-6) fehlt der Steuerbetrag in dieser Währung (BT-111).", "With an accounting currency (BT-6) the VAT total in it (BT-111) is missing.",
                c => Has(c.Root, "BT-6") && !c.All("BG-22").Any(g => Has(g, "BT-111")) ? [(c.First("BG-22") ?? c.Root, null)] : []),
            R("BR-54", "Eine Artikeleigenschaft hat nicht Name (BT-160) und Wert (BT-161).", "An item attribute does not have both name (BT-160) and value (BT-161).",
                c => c.All("BG-32").Where(g => !Has(g, "BT-160") || !Has(g, "BT-161")).Select(g => (g, (InvoiceField?)null))),
            R("BR-55", "Ein Verweis auf eine vorausgegangene Rechnung hat keine Rechnungsnummer (BT-25).", "A preceding invoice reference has no invoice number (BT-25).",
                c => MissingIn(c.All("BG-3"), "BT-25")),
            R("BR-56", "Der steuerliche Vertreter hat keine USt-IdNr. (BT-63).", "The tax representative has no VAT identifier (BT-63).",
                c => c.All("BG-11").Where(g => !g.Fields.Any(f => f.Id == "BT-63" && !string.IsNullOrWhiteSpace(f.Text) && IsVatId(c, f))).Select(g => (g, (InvoiceField?)null))),
            R("BR-57", "Die Lieferanschrift hat keinen Ländercode (BT-80).", "The deliver-to address has no country code (BT-80).", c => MissingIn(c.All("BG-15"), "BT-80")),
            R("BR-61", "Bei Zahlung per Überweisung (Code 30 oder 58) fehlt die Kontokennung (BT-84).",
                "With payment by credit transfer (code 30 or 58) the payment account identifier (BT-84) is missing.",
                c => CreditTransfersWithoutAccount(c)),
            R("BR-62", "Die elektronische Adresse des Verkäufers (BT-34) hat keine Schemakennung.", "The seller electronic address (BT-34) has no scheme identifier.",
                c => NoAttribute(c.All("BG-4"), "BT-34", "schemeID")),
            R("BR-63", "Die elektronische Adresse des Käufers (BT-49) hat keine Schemakennung.", "The buyer electronic address (BT-49) has no scheme identifier.",
                c => NoAttribute(c.All("BG-7"), "BT-49", "schemeID")),
            R("BR-64", "Die Artikelkennung nach Schema (BT-157) hat keine Schemakennung.", "The item standard identifier (BT-157) has no scheme identifier.",
                c => NoAttribute(c.All("BG-31"), "BT-157", "schemeID")),
            R("BR-65", "Die Artikelklassifizierung (BT-158) hat keine Schemakennung.", "The item classification identifier (BT-158) has no scheme identifier.",
                c => NoAttribute(c.All("BG-31"), "BT-158", "listID")),

            // conditions
            R("BR-CO-03", "Steuerdatum (BT-7) und Code des Steuerdatums (BT-8) dürfen nicht beide angegeben sein.",
                "The VAT point date (BT-7) and its code (BT-8) must not both be given.",
                c => Has(c.Root, "BT-7") && c.All("BG-23").Any(g => Has(g, "BT-8")) || Has(c.Root, "BT-7") && Has(c.Root, "BT-8") ? [(c.Root, null)] : []),
            R("BR-CO-04", "Eine Position hat keine Steuerkategorie (BT-151).", "An invoice line has no VAT category (BT-151).",
                c => c.Lines.Where(l => !Fields(l, "BT-151").Any(IsVat)).Select(l => (l, (InvoiceField?)null))),
            R("BR-CO-09", "Eine USt-IdNr. (BT-31, BT-48, BT-63) beginnt nicht mit dem Ländercode (ISO 3166-1, für Griechenland EL).",
                "A VAT identifier (BT-31, BT-48, BT-63) does not begin with a country code (ISO 3166-1, EL for Greece).",
                // per party (seller, buyer, tax representative): each field once, however many there are
                c => new[] { "BG-4", "BG-7", "BG-11" }.SelectMany(c.All).SelectMany(g => g.AllFields()
                    .Where(f => f.Id is "BT-31" or "BT-48" or "BT-63" && IsVatId(c, f) && !CountryPrefix(f.Text)).Select(f => (g, (InvoiceField?)f)))),
            R("BR-CO-10", "Die Summe der Positionsbeträge (BT-106) ist nicht die Summe der Nettobeträge (BT-131).",
                "The sum of line net amounts (BT-106) is not the sum of the line net amounts (BT-131).",
                c => Totals(c, "BT-106", LineSum(c))),
            R("BR-CO-11", "Die Summe der Nachlässe (BT-107) ist nicht die Summe der Nachlässe auf Rechnungsebene (BT-92).",
                "The sum of allowances (BT-107) is not the sum of the document level allowances (BT-92).",
                c => Totals(c, "BT-107", c.All("BG-20").Sum(g => Number(g, "BT-92") ?? 0), missingIsWrong: c.All("BG-20").Count > 0)),
            R("BR-CO-12", "Die Summe der Zuschläge (BT-108) ist nicht die Summe der Zuschläge auf Rechnungsebene (BT-99).",
                "The sum of charges (BT-108) is not the sum of the document level charges (BT-99).",
                c => Totals(c, "BT-108", c.All("BG-21").Sum(g => Number(g, "BT-99") ?? 0), missingIsWrong: c.All("BG-21").Count > 0)),
            R("BR-CO-13", "Der Gesamtbetrag ohne Umsatzsteuer (BT-109) ist nicht Positionsbeträge − Nachlässe + Zuschläge.",
                "The total without VAT (BT-109) is not line amounts − allowances + charges.",
                // from the sum of the lines (BT-106), as the validation artefacts do; without it the total cannot be confirmed either. Not
                // rounded: the artefacts of CII round here, those of UBL not always — so as the standard writes it
                c => c.First("BG-22") is { } t
                    ? Number(t, "BT-106") is { } lines
                        ? Totals(c, "BT-109", lines - (Number(t, "BT-107") ?? 0) + (Number(t, "BT-108") ?? 0), rounded: false) : [(t, t.Field("BT-109"))]
                    : []),
            // only where the VAT total is given (it is optional; the validation artefacts check it only then)
            R("BR-CO-14", "Die Umsatzsteuer gesamt (BT-110) ist nicht die Summe der Steuerbeträge (BT-117).",
                "The VAT total (BT-110) is not the sum of the VAT category tax amounts (BT-117).",
                c => c.First("BG-22") is { } t && t.Field("BT-110") is not null ? Totals(c, "BT-110", c.All("BG-23").Sum(g => Number(g, "BT-117") ?? 0)) : []),
            // without the invoice currency (BT-5) the VAT total in it cannot be told apart — BR-05 says what is wrong (both artefacts so);
            // without any document totals (BG-22) the total with VAT is not the sum either
            R("BR-CO-15", "Der Gesamtbetrag mit Umsatzsteuer (BT-112) ist nicht ohne Umsatzsteuer (BT-109) plus Umsatzsteuer (BT-110).",
                "The total with VAT (BT-112) is not the total without VAT (BT-109) plus VAT (BT-110).",
                c => !Has(c.Root, "BT-5") ? []
                    : c.First("BG-22") is not { } t ? [(c.Root, null)]
                    : Number(t, "BT-109") is { } net ? Totals(c, "BT-112", net + (Number(t, "BT-110") ?? 0)) : [(t, t.Field("BT-112"))]),
            // not rounded: the artefacts of CII compare exactly here, those of UBL round — so as the standard writes it; in the extension of
            // XRechnung in UBL BR-DEX-09 takes its place (third party payments)
            R("BR-CO-16", "Der fällige Betrag (BT-115) ist nicht Gesamtbetrag (BT-112) − Vorauszahlung (BT-113) + Rundung (BT-114).",
                "The amount due (BT-115) is not the total (BT-112) − paid amount (BT-113) + rounding (BT-114).",
                c => c.First("BG-22") is not { } t || DueAmountByExtension(c) ? []
                    : Number(t, "BT-112") is { } gross
                        ? Totals(c, "BT-115", gross - (Number(t, "BT-113") ?? 0) + (Number(t, "BT-114") ?? 0), rounded: false) : [(t, t.Field("BT-115"))]),
            // as the validation artefacts of both syntaxes read it: the amounts without their sign, up to one unit of the currency either way
            // (the tax may have been rounded per line); with a rate that rounds to zero, or none, the tax must round to zero
            R("BR-CO-17", "Ein Steuerbetrag (BT-117) passt nicht zu Basisbetrag (BT-116) × Satz (BT-119) / 100 (Abweichung von einer Währungseinheit oder mehr).",
                "A VAT category tax amount (BT-117) does not fit taxable amount (BT-116) × rate (BT-119) / 100 (a difference of one unit of the currency or more).",
                c => c.All("BG-23").Where(g => !TaxFits(Number(g, "BT-116"), VatNumber(g, "BT-119"), Number(g, "BT-117"), zeroRateMeansZero: true))
                    .Select(g => (g, g.Field("BT-117")))),
            // as the validation artefacts: only with lines (without them BR-16 says what is wrong) — in a profile without lines (BASIC WL,
            // where BR-16 does not apply) always, as its rules ask
            R("BR-CO-18", "Die Rechnung hat keine Steueraufschlüsselung (BG-23).", "The invoice has no VAT breakdown (BG-23).",
                c => (c.Lines.Count > 0 || ProfileWithoutLines(c)) && c.All("BG-23").Count == 0 ? [(c.Root, null)] : []),
            R("BR-CO-19", "Ein Rechnungszeitraum hat weder Anfang (BT-73) noch Ende (BT-74).", "An invoicing period has neither start (BT-73) nor end (BT-74).",
                c => EmptyPeriods(c, lines: false, "BG-14", "BT-73", "BT-74")),
            R("BR-CO-20", "Ein Positionszeitraum hat weder Anfang (BT-134) noch Ende (BT-135).", "A line period has neither start (BT-134) nor end (BT-135).",
                c => EmptyPeriods(c, lines: true, "BG-26", "BT-134", "BT-135")),
            R("BR-CO-21", "Ein Nachlass auf Rechnungsebene hat weder Grund (BT-97) noch Code (BT-98).", "A document level allowance has neither reason (BT-97) nor code (BT-98).",
                c => MissingBoth(c.All("BG-20"), "BT-97", "BT-98")),
            R("BR-CO-22", "Ein Zuschlag auf Rechnungsebene hat weder Grund (BT-104) noch Code (BT-105).", "A document level charge has neither reason (BT-104) nor code (BT-105).",
                c => MissingBoth(c.All("BG-21"), "BT-104", "BT-105")),
            R("BR-CO-23", "Ein Nachlass auf Positionsebene hat weder Grund (BT-139) noch Code (BT-140).", "A line allowance has neither reason (BT-139) nor code (BT-140).",
                c => MissingBoth(c.All("BG-27"), "BT-139", "BT-140")),
            R("BR-CO-24", "Ein Zuschlag auf Positionsebene hat weder Grund (BT-144) noch Code (BT-145).", "A line charge has neither reason (BT-144) nor code (BT-145).",
                c => MissingBoth(c.All("BG-28"), "BT-144", "BT-145")),
            R("BR-CO-25", "Bei positivem fälligem Betrag (BT-115) fehlen Fälligkeitsdatum (BT-9) und Zahlungsbedingungen (BT-20).",
                "With a positive amount due (BT-115) both the due date (BT-9) and the payment terms (BT-20) are missing.",
                c => c.First("BG-22") is { } t && Number(t, "BT-115") > 0 && !c.Root.AllFields().Any(f => f.Id is "BT-9" or "BT-20" && !string.IsNullOrWhiteSpace(f.Text))
                    ? [(t, t.Field("BT-115"))] : [],
                // in the standard, but not checked by its validation artefacts (1.3.17): a warning, not an error
                InvoiceRuleSeverity.Warning),
            R("BR-CO-26", "Für den Verkäufer fehlt jede Kennung: weder Kennung (BT-29), Registernummer (BT-30) noch USt-IdNr. (BT-31).",
                "The seller has no identifier at all: neither identifier (BT-29), legal registration (BT-30) nor VAT identifier (BT-31).",
                c => c.First("BG-4") is not { } s ? [(c.Root, null)]
                    : !s.AllFields().Any(f => f.Id is "BT-29" or "BT-30" or "BT-31" && !string.IsNullOrWhiteSpace(f.Text)) ? [(s, null)] : []),

            // split payment (Italy)
            R("BR-B-01", "Steuerkategorie „Split Payment“ (B) nur in italienischen Inlandsrechnungen.", "The VAT category split payment (B) only in Italian domestic invoices.",
                c => HasCategory(c, "B") && (Country(c, "BG-4", "BG-5", "BT-40") != "IT" || Country(c, "BG-7", "BG-8", "BT-55") != "IT") ? [(c.Root, null)] : []),
            R("BR-B-02", "Eine Rechnung mit „Split Payment“ (B) darf keinen Normalsatz (S) enthalten.", "An invoice with split payment (B) must not contain the standard rate (S).",
                c => HasCategory(c, "B") && HasCategory(c, "S") ? [(c.Root, null)] : []),
        };
        // decimals: amounts of the document, of the VAT breakdown, of the lines and their allowances and charges at most two
        foreach (var (id, bt, group) in new[]
                 {
                     ("BR-DEC-01", "BT-92", "BG-20"), ("BR-DEC-02", "BT-93", "BG-20"), ("BR-DEC-05", "BT-99", "BG-21"), ("BR-DEC-06", "BT-100", "BG-21"),
                     ("BR-DEC-09", "BT-106", "BG-22"), ("BR-DEC-10", "BT-107", "BG-22"), ("BR-DEC-11", "BT-108", "BG-22"), ("BR-DEC-12", "BT-109", "BG-22"),
                     ("BR-DEC-13", "BT-110", "BG-22"), ("BR-DEC-14", "BT-112", "BG-22"), ("BR-DEC-15", "BT-111", "BG-22"), ("BR-DEC-16", "BT-113", "BG-22"),
                     ("BR-DEC-17", "BT-114", "BG-22"), ("BR-DEC-18", "BT-115", "BG-22"), ("BR-DEC-19", "BT-116", "BG-23"), ("BR-DEC-20", "BT-117", "BG-23"),
                     ("BR-DEC-23", "BT-131", "BG-25"), ("BR-DEC-24", "BT-136", "BG-27"), ("BR-DEC-25", "BT-137", "BG-27"), ("BR-DEC-27", "BT-141", "BG-28"),
                     ("BR-DEC-28", "BT-142", "BG-28"),
                 })
        {
            var term = bt;
            var where = group;
            // the net amount of every line of the rules: in CII also of a sub line, which is a line of its own there (FG 843)
            list.Add(R(id, $"Der Betrag {term} hat mehr als zwei Nachkommastellen.", $"The amount {term} has more than two decimals.",
                c => (where == "BG-25" ? c.Lines : c.All(where))
                    .SelectMany(g => g.Fields.Where(f => f.Id == term && f.Text is { } t && Decimals(t) > 2).Select(f => (g, (InvoiceField?)f)))));
        }
        list.AddRange(VatRules());
        list.AddRange(CodeRules());
        list.AddRange(XRechnungRules());
        // the tables of the profiles, read once: the element rules and the codes of an item attribute the rules of EXTENDED recommend
        var structures = LoadProfileStructures();
        list.AddRange(ProfileStructureRules(structures));
        list.AddRange(ExtendedRules(structures.Codes.TryGetValue((InvoiceProfile.Extended, "BR-FXEXT-04"), out var attributes)
            ? attributes : throw new InvalidDataException("profilcodes.tsv: Liste BR-FXEXT-04 von EXTENDED fehlt")));
        list.AddRange(SchemaRules());
        list.AddRange(HintRules());
        return [.. list.OrderBy(c => Order(c.Rule.Id))];
    }

    /// <summary>
    /// The order of the rules: BR-n, BR-CO, BR-DEC, BR-B, the VAT categories in the order of the standard, the code lists (BR-CL), then
    /// XRechnung (BR-DE with BR-DE-TMP-32, BR-DEX, BR-TMP, those of CVD, the rules of Peppol) — by number, then by name (BR-DE-23-a, -b).
    /// </summary>
    private static (int Family, int Number, string Id) Order(string id)
    {
        var parts = id.Split('-');
        if (parts[0] == "PEPPOL") return (40, int.TryParse(parts[^1].TrimStart('R'), CultureInfo.InvariantCulture, out var r) ? r : 0, id);
        // the business rules of EXTENDED (BR-FXEXT) after them: the own ones, those of the standard it revises (BR, CO), the VAT categories
        if (parts.Length > 2 && parts[1] == "FXEXT")
            return (45, parts[2] switch
            {
                "BR" => 100,
                "CO" => 200,
                var f when char.IsAsciiDigit(f[0]) => 0,
                var f => 300 + 10 * Array.IndexOf<string>(["S", "Z", "E", "AE", "IC", "G", "O", "IG", "IP"], f),
            } + (parts.Skip(2).FirstOrDefault(p => char.IsAsciiDigit(p[0])) is { } digits
                && int.TryParse(new string(digits.TakeWhile(char.IsAsciiDigit).ToArray()), CultureInfo.InvariantCulture, out var x) ? x % 100 : 0), id);
        // the element rules of the profiles of ZUGFeRD / Factur-X (FX-SCH-A-000019) last, by number
        if (parts[0] == "FX") return (50, int.TryParse(parts[^1], CultureInfo.InvariantCulture, out var fx) ? fx : 0, id);
        // the rules of PDF-Knead itself at the very end: the schema check first (PDFK-XSD-00 to -06), then the others (PDFK-FX-01)
        if (parts[0] == "PDFK") return (parts[1] == "XSD" ? 55 : 60, int.TryParse(parts[^1], CultureInfo.InvariantCulture, out var own) ? own : 0, id);
        var family = parts.Length == 2 ? 1 : parts.Contains("CVD") ? 33 : parts[1] switch
        {
            "CO" => 2,
            "DEC" => 3,
            "B" => 4,
            "CL" => 20,
            "DE" => 30,
            "DEX" => 31,
            "TMP" => 32,
            // the VAT categories in the order of the standard (here, not as a field: Build runs while the fields are being initialised)
            var f => 5 + Array.IndexOf<string>(["S", "Z", "E", "AE", "IC", "G", "O", "IG", "IP"], f),
        };
        var number = parts.Skip(1).Select(p => int.TryParse(p, CultureInfo.InvariantCulture, out var n) ? n : -1).FirstOrDefault(n => n >= 0);
        return (family, number, id);
    }

    // ------------------------------------------------------------ rule helpers

    private static IEnumerable<(InvoiceGroup, InvoiceField?)> Before(IEnumerable<InvoiceGroup> groups, string start, string end) =>
        groups.Where(g => InvoiceFormat.Date(Text(g, start) ?? "") is { } a && InvoiceFormat.Date(Text(g, end) ?? "") is { } b && b < a)
            .Select(g => (g, g.Field(end)));

    private static IEnumerable<(InvoiceGroup, InvoiceField?)> NoAttribute(IEnumerable<InvoiceGroup> groups, string id, string attribute) =>
        groups.SelectMany(g => g.Fields.Where(f => f.Id == id && !f.Node.IsAttribute && string.IsNullOrWhiteSpace(f.Attribute(attribute))).Select(f => (g, (InvoiceField?)f)));

    /// <summary>
    /// A total of the document totals that must equal a calculated value, rounded to the cent where the validation artefacts of both syntaxes
    /// round it (<paramref name="rounded"/>). A required total that is missing does not equal it either (as the artefacts count it); an
    /// optional one (<paramref name="missingIsWrong"/> false) may be missing — the sums of allowances and charges only where there are none.
    /// </summary>
    private static IEnumerable<(InvoiceGroup, InvoiceField?)> Totals(Context c, string id, decimal expected, bool missingIsWrong = true, bool rounded = true)
    {
        if (c.First("BG-22") is not { } totals) yield break;
        if (totals.Field(id) is not { } field || Number(field) is not { } value)
        {
            if (missingIsWrong) yield return (totals, totals.Field(id));
            yield break;
        }
        if (value != (rounded ? Cents(expected) : expected)) yield return (totals, field);
    }

    /// <summary>
    /// The sum of the net amounts of the lines of the rules (BT-131): in UBL of the invoice lines, not of their sub lines (SubInvoiceLine, nested
    /// in its line); in CII of every line — a line naming a parent (ParentLineID) is a line of its own in the model of the standard, as the
    /// artefacts of CII count it (FG 843).
    /// </summary>
    private static decimal LineSum(Context c) => c.Lines.Sum(l => Number(l, "BT-131") ?? 0);

    private static bool CountryPrefix(string? vat)
    {
        var v = vat?.Trim() ?? "";
        if (v.Length < 2 || !char.IsAsciiLetterUpper(v[0]) || !char.IsAsciiLetterUpper(v[1])) return false;
        return v[..2] == "EL" || InvoiceCodes.Name("Country", v[..2]) is not null;
    }

    /// <summary>Whether the invoice has a VAT category anywhere; in EXTENDED also on a logistics service charge (BT-X-273), which is a charge there.</summary>
    private static bool HasCategory(Context c, string code) =>
        c.Root.AllFields().Any(f => f.Id is "BT-95" or "BT-102" or "BT-118" or "BT-151" && f.Text?.Trim() == code && IsVat(f))
        || IsExtended(c) && ExtOf(c).Items.Any(i => i.Kind == 3 && i.Category == code);

    /// <summary>
    /// BR-61 as the validation artefacts of both syntaxes read it: each payment means of its own with the code 30 or 58 needs an account
    /// identifier of its own — the display groups payment means of one code into one instruction (BG-16), where one account would hide a
    /// missing one. Also when the account element is missing altogether (the standard asks for it; the artefacts of CII then look at
    /// nothing). ZUGFeRD 1 (its own tree) by the instructions.
    /// </summary>
    private static IEnumerable<(InvoiceGroup, InvoiceField?)> CreditTransfersWithoutAccount(Context c)
    {
        var syntax = c.Invoice.Kind.Syntax;
        if (syntax is not (InvoiceSyntax.Cii or InvoiceSyntax.UblInvoice or InvoiceSyntax.UblCreditNote) || c.Root.Element is not { } root)
        {
            foreach (var g in c.All("BG-16").Where(g => Text(g, "BT-81") is "30" or "58" && !g.GroupsOf("BG-17").Any(a => Has(a, "BT-84"))))
                yield return (g, null);
            yield break;
        }
        var cii = syntax == InvoiceSyntax.Cii;
        IEnumerable<InvoiceElement> means = cii
            ? root.Child(Rsm, "SupplyChainTradeTransaction")?.Child(Ram, "ApplicableHeaderTradeSettlement")?.ChildrenNamed(Ram, "SpecifiedTradeSettlementPaymentMeans") ?? []
            : root.ChildrenNamed(Cac, "PaymentMeans");
        foreach (var m in means)
        {
            var code = ChildText(m, cii ? "TypeCode" : "PaymentMeansCode");
            if (code is not ("30" or "58")) continue;
            var accounts = cii ? m.ChildrenNamed(Ram, "PayeePartyCreditorFinancialAccount") : m.ChildrenNamed(Cac, "PayeeFinancialAccount");
            if (accounts.Any(a => cii
                    ? !string.IsNullOrWhiteSpace(ChildText(a, "IBANID")) || !string.IsNullOrWhiteSpace(ChildText(a, "ProprietaryID"))
                    : !string.IsNullOrWhiteSpace(a.Child(Cbc, "ID")?.Text)))
                continue;
            yield return (c.All("BG-16").FirstOrDefault(g => Text(g, "BT-81") == code) ?? c.Root, null);
        }
    }

    /// <summary>
    /// BR-CO-19 and BR-CO-20: a period that is given needs a start or an end. Checked on the period elements of the invoice itself (CII
    /// BillingSpecifiedPeriod, UBL InvoicePeriod; of the document or of a line), as both artefacts do — an empty period element leaves no
    /// group in the readable form (it holds nothing to show), and the rule is about exactly that case. ZUGFeRD 1 (its own tree) by the groups.
    /// </summary>
    private static IEnumerable<(InvoiceGroup, InvoiceField?)> EmptyPeriods(Context c, bool lines, string group, string start, string end)
    {
        var syntax = c.Invoice.Kind.Syntax;
        if (syntax is not (InvoiceSyntax.Cii or InvoiceSyntax.UblInvoice or InvoiceSyntax.UblCreditNote) || c.Root.Element is not { } root)
        {
            foreach (var found in MissingBoth(c.All(group), start, end)) yield return found;
            yield break;
        }
        var cii = syntax == InvoiceSyntax.Cii;
        var periods = new List<InvoiceElement>();
        if (cii)
        {
            var transaction = root.Child(Rsm, "SupplyChainTradeTransaction");
            if (!lines)
                periods.AddRange(transaction?.Child(Ram, "ApplicableHeaderTradeSettlement")?.ChildrenNamed(Ram, "BillingSpecifiedPeriod") ?? []);
            else
                foreach (var line in transaction?.ChildrenNamed(Ram, "IncludedSupplyChainTradeLineItem") ?? [])
                    periods.AddRange(line.Child(Ram, "SpecifiedLineTradeSettlement")?.ChildrenNamed(Ram, "BillingSpecifiedPeriod") ?? []);
        }
        else if (!lines)
            periods.AddRange(root.ChildrenNamed(Cac, "InvoicePeriod"));
        else
            foreach (var line in root.Children.Where(e => e.Namespace == Cac && e.Local is "InvoiceLine" or "CreditNoteLine"))
                periods.AddRange(line.ChildrenNamed(Cac, "InvoicePeriod"));
        foreach (var period in periods)
        {
            var given = cii
                ? period.Child(Ram, "StartDateTime") is not null || period.Child(Ram, "EndDateTime") is not null
                // in UBL the period element also carries the VAT point date code (BT-8): with only that it is no invoicing period
                : period.Child(Cbc, "StartDate") is not null || period.Child(Cbc, "EndDate") is not null || period.Child(Cbc, "DescriptionCode") is not null;
            if (!given) yield return (c.GroupOf(period), null);
        }
    }

    /// <summary>The groups that have no VAT term of that identifier (a category of another tax is none).</summary>
    private static IEnumerable<(InvoiceGroup, InvoiceField?)> NoVat(IEnumerable<InvoiceGroup> groups, string id) =>
        groups.Where(g => !g.Fields.Any(f => f.Id == id && !string.IsNullOrWhiteSpace(f.Text) && IsVat(f))).Select(g => (g, (InvoiceField?)null));

    /// <summary>
    /// Whether a tax amount fits taxable amount × rate / 100, as the validation artefacts of both syntaxes read BR-CO-17 and the rate rules of
    /// the categories (09): without the signs, rounded to the cent, less than one unit of the currency apart (the tax may have been rounded per
    /// line). Missing amounts or a missing rate do not fit — with <paramref name="zeroRateMeansZero"/> a rate that rounds to zero, or none, asks
    /// for a tax that rounds to zero instead.
    /// </summary>
    private static bool TaxFits(decimal? basis, decimal? rate, decimal? tax, bool zeroRateMeansZero)
    {
        if (tax is not { } t) return false;
        if (zeroRateMeansZero && (rate is null || Whole(rate.Value) == 0)) return Whole(t) == 0;
        if (basis is not { } b || rate is not { } r) return false;
        return Math.Abs(Math.Abs(t) - Cents(Math.Abs(b) * r / 100)) < 1;
    }

    private static string? Country(Context c, string party, string address, string id) =>
        c.First(party)?.GroupsOf(address).FirstOrDefault() is { } a ? Text(a, id) : null;
}
