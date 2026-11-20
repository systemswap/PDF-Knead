namespace PdfKnead.EInvoice;

/// <summary>
/// A business term of the invoice (BT of EN 16931) as found in the XML: its identifier, the name of the mapping, the node it was read from
/// (element or attribute — the place stage 3 searches for and jumps to) and its text.
/// </summary>
public sealed class InvoiceField(string id, string name, InvoiceNode node)
{
    /// <summary>The identifier („BT-1“, „BT-DEX-001“).</summary>
    public string Id { get; } = id;

    /// <summary>The name in the mapping („Invoice_number“), key of the labels.</summary>
    public string Name { get; } = name;

    /// <summary>Where it was read from.</summary>
    public InvoiceNode Node { get; } = node;

    /// <summary>The value as written in the XML (not formatted); null if the element is empty.</summary>
    public string? Text => Node.Text;

    /// <summary>An attribute of the element of the value (currencyID, unitCode, schemeID …); null if absent or if the value is itself an attribute.</summary>
    public string? Attribute(string local) => Node.IsAttribute ? null : Node.Element.Attribute(local);

    /// <summary>The label in the current language (German or English).</summary>
    public string Label => InvoiceLabels.Of(Name, Id);
}

/// <summary>A business group (BG of EN 16931): seller, invoice line, VAT breakdown … with its terms and its groups, in document order.</summary>
public sealed class InvoiceGroup(string id, string name, InvoiceElement? element)
{
    /// <summary>The identifier („BG-4“; „INVOICE“ for the whole).</summary>
    public string Id { get; } = id;

    /// <summary>The name in the mapping („SELLER“).</summary>
    public string Name { get; } = name;

    /// <summary>The element the group was read from (null for groups put together from several, like payment instructions).</summary>
    public InvoiceElement? Element { get; } = element;

    /// <summary>The terms directly in the group.</summary>
    public List<InvoiceField> Fields { get; } = [];

    /// <summary>The groups directly in the group.</summary>
    public List<InvoiceGroup> Groups { get; } = [];

    /// <summary>Whether the group holds anything (empty groups are left out, as in the visualization).</summary>
    public bool HasContent => Fields.Count > 0 || Groups.Any(g => g.HasContent);

    /// <summary>The label in the current language.</summary>
    public string Label => InvoiceLabels.Of(Name, Id);

    /// <summary>The first term of that identifier directly in the group.</summary>
    public InvoiceField? Field(string id) => Fields.FirstOrDefault(f => f.Id == id);

    /// <summary>The text of the first term of that identifier directly in the group.</summary>
    public string? Text(string id) => Field(id)?.Text;

    /// <summary>The groups of that identifier directly in the group.</summary>
    public IEnumerable<InvoiceGroup> GroupsOf(string id) => Groups.Where(g => g.Id == id);

    /// <summary>The group and every group below it (own stack, document order).</summary>
    public IEnumerable<InvoiceGroup> AllGroups()
    {
        var pending = new Stack<InvoiceGroup>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            var g = pending.Pop();
            yield return g;
            for (var i = g.Groups.Count - 1; i >= 0; i--) pending.Push(g.Groups[i]);
        }
    }

    /// <summary>Every term in the group and below it (own stack, document order).</summary>
    public IEnumerable<InvoiceField> AllFields()
    {
        var pending = new Stack<InvoiceGroup>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            var g = pending.Pop();
            foreach (var f in g.Fields) yield return f;
            for (var i = g.Groups.Count - 1; i >= 0; i--) pending.Push(g.Groups[i]);
        }
    }
}

/// <summary>A value of the XML that no line of the mapping takes: shown as „Weitere Angaben“, so that nothing is left out.</summary>
/// <param name="Node">The element or attribute.</param>
public sealed record InvoiceFurtherValue(InvoiceNode Node)
{
    /// <summary>The path of the value (local names, „/@name“ for an attribute) — built when asked for, not for every value.</summary>
    public string Path => Node.Element.Path + (Node.Attribute is { } a ? "/@" + a : "");

    /// <summary>The value.</summary>
    public string? Text => Node.Text;
}

/// <summary>
/// The invoice in readable form (KONZEPT.md, section 5.24, stage 2): the groups and terms of EN 16931 read from the XML along the mapping of
/// the syntax, the values no mapping takes, and the source of the mapping. A representation, not the invoice — the XML is what counts.
/// </summary>
public sealed class InvoiceData(InvoiceKind kind, InvoiceGroup root, IReadOnlyList<InvoiceFurtherValue> further, string mapping, string? note = null)
{
    /// <summary>How the invoice was read, if not along its own mapping (ZUGFeRD 1); null otherwise.</summary>
    public string? Note { get; } = note;

    /// <summary>The fixed note of every representation (KONZEPT.md, section 5.24, stage 2): it is not the invoice, the XML counts.</summary>
    public static string Disclaimer => Tr.T("Darstellung, nicht die Rechnung – maßgeblich ist der XML-Teil.");

    /// <summary>What the XML is (syntax, profile).</summary>
    public InvoiceKind Kind { get; } = kind;

    /// <summary>The whole invoice: its terms and groups.</summary>
    public InvoiceGroup Root { get; } = root;

    /// <summary>Values of the XML that no line of the mapping takes.</summary>
    public IReadOnlyList<InvoiceFurtherValue> Further { get; } = further;

    /// <summary>The source and version of the mapping (shown with every representation).</summary>
    public string Mapping { get; } = mapping;

    /// <summary>The nodes the paths visited while reading (the work counted against the limit; FG 826).</summary>
    internal long Work { get; init; }

    /// <summary>
    /// The first term of each identifier, found in one walk when first asked for: the display asks for the currency BT-5 at every amount without
    /// its own, and a walk per question made an invoice of many lines without BT-5 quadratic (FG 825).
    /// </summary>
    private readonly Lazy<Dictionary<string, InvoiceField>> _first = new(() =>
    {
        var first = new Dictionary<string, InvoiceField>(StringComparer.Ordinal);
        foreach (var f in root.AllFields()) first.TryAdd(f.Id, f);
        return first;
    });

    /// <summary>The first term of that identifier anywhere in the invoice.</summary>
    public InvoiceField? Field(string id) => _first.Value.GetValueOrDefault(id);

    /// <summary>The text of the first term of that identifier anywhere in the invoice.</summary>
    public string? Text(string id) => Field(id)?.Text;

    /// <summary>The groups of that identifier at the top level (seller BG-4, lines BG-25 …).</summary>
    public IEnumerable<InvoiceGroup> Groups(string id) => Root.GroupsOf(id);
}
