using System.Reflection;

namespace PdfKnead.EInvoice;

/// <summary>
/// The mapping of one syntax (CII, UBL invoice, UBL credit note) from business terms to XML, and the reading of an invoice along it
/// (KONZEPT.md, section 5.24, „Weg für Stufe 2“). The tables are generated from the XRechnung visualization of the KoSIT
/// (Zuordnung/erzeugen.py, Apache-2.0) and embedded; each row says: in group P, term or group X is the node the path S reaches, if it fits
/// the pattern M. Built by hand and named as such: the grouping of the payment instructions (BG-16), the choice between the base quantity
/// of the net and of the gross price (CII BT-149, BT-150), the guards „only if present“ (BT-9, BT-20, which need nothing), the notes of
/// UBL (BG-1, a subject code in front of its note) and the qualifiers of the syntax that carry no term (see <see cref="InvoiceReader"/>).
/// Any other context in a table is an error when the table is loaded, never guessed.
/// </summary>
public sealed class InvoiceMap
{
    /// <summary>A row of a table.</summary>
    internal sealed record Rule(string Parent, string Id, string SelectText, InvoicePath? Select, InvoicePath? Match, string Name, string Context);

    private readonly Dictionary<string, List<Rule>> _children = new(StringComparer.Ordinal);

    private InvoiceMap(InvoiceSyntax syntax, string source, IReadOnlyDictionary<string, string> namespaces)
    {
        Syntax = syntax;
        Source = source;
        Namespaces = namespaces;
    }

    /// <summary>The syntax of the table.</summary>
    public InvoiceSyntax Syntax { get; }

    /// <summary>Where the table comes from (its first line).</summary>
    public string Source { get; }

    /// <summary>The prefixes of the table.</summary>
    internal IReadOnlyDictionary<string, string> Namespaces { get; }

    /// <summary>The rows below a group („ROOT“ for the invoice).</summary>
    internal IReadOnlyList<Rule> Children(string parent) => _children.TryGetValue(parent, out var list) ? list : [];

    /// <summary>All rows (tests).</summary>
    internal IEnumerable<Rule> Rules => _children.Values.SelectMany(r => r);

    private static readonly Lazy<InvoiceMap> CiiMap = new(() => Load("cii.tsv", InvoiceSyntax.Cii));
    private static readonly Lazy<InvoiceMap> UblInvoiceMap = new(() => Load("ubl-invoice.tsv", InvoiceSyntax.UblInvoice));
    private static readonly Lazy<InvoiceMap> UblCreditNoteMap = new(() => Load("ubl-creditnote.tsv", InvoiceSyntax.UblCreditNote));

    /// <summary>The mapping of a syntax; null for a syntax without one (ZUGFeRD 1 is read through CII, see <see cref="InvoiceReader"/>).</summary>
    public static InvoiceMap? Of(InvoiceSyntax syntax) => syntax switch
    {
        InvoiceSyntax.Cii => CiiMap.Value,
        InvoiceSyntax.UblInvoice => UblInvoiceMap.Value,
        InvoiceSyntax.UblCreditNote => UblCreditNoteMap.Value,
        _ => null,
    };

    /// <summary>The lines of an embedded table.</summary>
    internal static IEnumerable<string> Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Zuordnung." + name)
            ?? throw new InvalidDataException($"Tabelle {name} fehlt im Baustein");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }

    /// <summary>Loads a table: every path is read now; a form or context outside what is handled is an error here.</summary>
    internal static InvoiceMap Load(string name, InvoiceSyntax syntax)
    {
        var lines = Resource(name).ToList();
        var namespaces = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Where(l => l.StartsWith("#ns\t", StringComparison.Ordinal)))
        {
            var cols = line.Split('\t');
            namespaces[cols[1]] = cols[2];
        }
        var map = new InvoiceMap(syntax, lines.FirstOrDefault()?.TrimStart('#', ' ') ?? name, namespaces);
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var c = line.Split('\t');
            if (c.Length != 6) throw new InvalidDataException($"{name}: Zeile mit {c.Length} Spalten");
            var (parent, id, select, match, rowName, context) = (c[0], c[1], c[2], c[3], c[4], c[5]);
            Check(name, id, context);
            var grouped = context.StartsWith("group:", StringComparison.Ordinal);
            var rule = new Rule(parent, id, select,
                grouped ? null : InvoicePath.Parse(select, namespaces),
                match.Length == 0 ? null : InvoicePath.Parse(match, namespaces),
                rowName, context);
            if (!map._children.TryGetValue(parent, out var list)) map._children[parent] = list = [];
            list.Add(rule);
        }
        return map;
    }

    /// <summary>The contexts the reader handles; anything else means the source changed in a way that needs a look.</summary>
    private static void Check(string table, string id, string context)
    {
        if (context.Length == 0) return;
        if (context.StartsWith("group:", StringComparison.Ordinal) && id is "BT-81" or "BT-82" or "BT-83" or "BG-17" or "BG-18" or "BG-19") return;
        if (context.StartsWith("if:count(", StringComparison.Ordinal) && id is "BT-9" or "BT-20") return;
        if (id is "BT-149" or "BT-150" && context.Contains("choose:", StringComparison.Ordinal)) return;
        if (id == "BT-7" && context == "distinct") return;
        throw new InvalidDataException($"{table}: unbekannter Kontext bei {id}: {context}");
    }
}

/// <summary>
/// Reads an invoice XML into <see cref="InvoiceData"/> along the mapping of its syntax (KONZEPT.md, section 5.24, stage 2). The terms keep
/// the node they came from; values no row takes become „Weitere Angaben“. Read with an own stack of the groups (the mapping is at most a few
/// levels deep, the XML was bounded by the hardened reader).
/// </summary>
public static class InvoiceReader
{
    /// <summary>Reads an invoice; null for a syntax without a mapping (FatturaPA, Order-X, unknown).</summary>
    /// <param name="xml">The XML of the hardened reader.</param>
    /// <param name="kind">What it is.</param>
    /// <param name="cancel">Cancels reading.</param>
    /// <exception cref="InvoiceXmlException">
    /// <see cref="InvoiceXmlRejection.TooComplex"/>: the paths would visit far more nodes than the invoice has elements (FG 826).
    /// </exception>
    public static InvoiceData? Read(InvoiceXmlDocument xml, InvoiceKind kind, CancellationToken cancel = default)
    {
        string? note = null;
        if (kind.Syntax == InvoiceSyntax.ZugferdV1)
        {
            // ZUGFeRD 1: its tree with the names of CII 2, read with the mapping of CII
            xml = ZugferdV1.Translate(xml);
            kind = kind with { Syntax = InvoiceSyntax.Cii };
            note = Tr.T("Nach ZUGFeRD 1 gelesen: die Namen des Baums sind auf die der CII abgebildet, die Werte unverändert.");
        }
        if (InvoiceMap.Of(kind.Syntax) is not { } map) return null;
        var root = new InvoiceGroup("INVOICE", "invoice", xml.Root);
        var taken = new HashSet<(InvoiceElement, string?)>(new NodeKey());
        var budget = new InvoicePath.Budget(WorkPerElement * Math.Max(xml.Elements, 1000));
        using (InvoicePath.Use(budget))
        {
            Fill(map, root, "ROOT", xml.Root, taken, cancel);
            if (map.Syntax == InvoiceSyntax.Cii) SubLines(root, taken);
            if (map.Syntax is InvoiceSyntax.UblInvoice or InvoiceSyntax.UblCreditNote) UblNotes(map, root, xml.Root, taken);
            Payments(map, root, xml.Root, taken, cancel);
            Qualifiers(map, xml.Root, taken);
        }
        return new InvoiceData(kind, root, Further(xml.Root, taken), map.Source, note) { Work = budget.Used };
    }

    /// <summary>
    /// How many nodes the paths may visit per element of the invoice (at least per 1 000 elements) before the reading is refused as too costly
    /// (FG 826). Measured over the collections: see docs/PRUEFUNGEN.md, section 6y.
    /// </summary>
    internal const long WorkPerElement = 100;

    private static void Fill(InvoiceMap map, InvoiceGroup group, string ruleOf, InvoiceElement context, HashSet<(InvoiceElement, string?)> taken,
        CancellationToken cancel)
    {
        var pending = new Stack<(InvoiceGroup Group, string RuleOf, InvoiceElement Context)>();
        pending.Push((group, ruleOf, context));
        var seen = new HashSet<(string, InvoiceElement, string?)>();   // one set, cleared per group (a large invoice has 60 000 lines)
        while (pending.Count > 0)
        {
            cancel.ThrowIfCancellationRequested();
            var (g, of, ctx) = pending.Pop();
            // emptied per group — but a set grown large (the invoice's 60 000 lines) is replaced: Clear costs its whole capacity each time
            if (seen.Count > 256) seen = [];
            else seen.Clear();
            foreach (var rule in map.Children(of))
            {
                if (rule.Select is null) continue;                                // payment grouping, built in Payments
                if (rule.Id is "BT-149" or "BT-150" && rule.Context.Contains("choose:", StringComparison.Ordinal))
                {
                    BaseQuantity(rule, g, ctx, seen, taken);
                    continue;
                }
                var distinctTexts = rule.Context == "distinct" ? new HashSet<string?>() : null;
                foreach (var node in rule.Select.Select(ctx))
                {
                    if (rule.Match is not null && !rule.Match.Matches(node)) continue;
                    if (distinctTexts is not null && !distinctTexts.Add(node.Text))
                    {
                        taken.Add((node.Element, node.Attribute));   // the same value again (CII BT-7 in every breakdown): one term
                        continue;
                    }
                    if (!seen.Add((rule.Id, node.Element, node.Attribute))) continue;   // two rows of one term reaching the same node
                    if (rule.Id.StartsWith("BG", StringComparison.Ordinal))
                    {
                        var child = new InvoiceGroup(rule.Id, rule.Name, node.Element);
                        g.Groups.Add(child);
                        pending.Push((child, rule.Id, node.Element));
                    }
                    else if (!string.IsNullOrWhiteSpace(node.Text) || node is { IsAttribute: false, Element.Binary: not null })   // an empty element is no value; an attachment is
                    {
                        g.Fields.Add(new InvoiceField(rule.Id, rule.Name, node));
                        taken.Add((node.Element, node.Attribute));
                    }
                }
            }
        }
        Prune(group);
    }

    /// <summary>How deep sub invoice lines of CII are nested at most; a line deeper down stays among the lines (KONZEPT.md, section 5.24, stage 2).</summary>
    internal const int MaxSubLineDepth = 16;

    /// <summary>
    /// The sub invoice lines of CII (ZUGFeRD EXTENDED, XRechnung extension; built by hand, the visualization has none for CII): a line whose
    /// ParentLineID names the LineID of another line is shown inside it as a sub invoice line (BG-DEX-01), as UBL nests its SubInvoiceLine.
    /// A parent that is not there or whose LineID is given twice, a chain that runs in a circle, or one deeper than
    /// <see cref="MaxSubLineDepth"/> leaves the line where it is, and its ParentLineID stays visible as a further value. Without recursion:
    /// each line's level is found along its parents once.
    /// </summary>
    private static void SubLines(InvoiceGroup root, HashSet<(InvoiceElement, string?)> taken)
    {
        var lines = root.Groups.Where(g => g.Id == "BG-25" && g.Element is not null).ToList();
        if (lines.Count < 2) return;
        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        var twice = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Text("BT-126")?.Trim() is { Length: > 0 } id && !byId.TryAdd(id, i)) twice.Add(id);
        var parent = new int[lines.Count];
        var parentNode = new InvoiceElement?[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            parent[i] = -1;
            var node = lines[i].Element!.Child(InvoiceKind.CiiRam, "AssociatedDocumentLineDocument")?.Child(InvoiceKind.CiiRam, "ParentLineID");
            if (node?.Text?.Trim() is { Length: > 0 } p && !twice.Contains(p) && byId.TryGetValue(p, out var at) && at != i)
            {
                parent[i] = at;
                parentNode[i] = node;
            }
        }
        // the level of each line (1 = among the lines): 0 not known yet, -1 on the chain being followed
        var level = new int[lines.Count];
        var chain = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (level[i] != 0) continue;
            chain.Clear();
            var j = i;
            while (j >= 0 && level[j] == 0)
            {
                level[j] = -1;
                chain.Add(j);
                j = parent[j];
            }
            if (j >= 0 && level[j] == -1)
                for (var k = chain.IndexOf(j); k < chain.Count; k++) parent[chain[k]] = -1;   // a circle: all of it stays among the lines
            for (var k = chain.Count - 1; k >= 0; k--)
            {
                var x = chain[k];
                level[x] = parent[x] < 0 ? 1 : level[parent[x]] + 1;
                if (level[x] > MaxSubLineDepth)
                {
                    parent[x] = -1;
                    level[x] = 1;
                }
            }
        }
        // move the sub lines, in document order, into the group of their parent line
        var sub = new InvoiceGroup?[lines.Count];
        var moved = new HashSet<InvoiceGroup>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < lines.Count; i++)
        {
            if (parent[i] < 0) continue;
            var group = new InvoiceGroup("BG-DEX-01", "SUB_INVOICE_LINE", lines[i].Element);
            group.Fields.AddRange(lines[i].Fields);
            group.Groups.AddRange(lines[i].Groups);
            sub[i] = group;
            moved.Add(lines[i]);
            taken.Add((parentNode[i]!, null));   // the nesting shows it
        }
        if (moved.Count == 0) return;
        for (var i = 0; i < lines.Count; i++)
            if (parent[i] >= 0) (sub[parent[i]] ?? lines[parent[i]]).Groups.Add(sub[i]!);
        root.Groups.RemoveAll(moved.Contains);
    }

    /// <summary>Groups without any term are left out, as the visualization does.</summary>
    private static void Prune(InvoiceGroup group)
    {
        var pending = new Stack<InvoiceGroup>();
        pending.Push(group);
        var all = new List<InvoiceGroup>();
        while (pending.Count > 0)
        {
            var g = pending.Pop();
            all.Add(g);
            foreach (var c in g.Groups) pending.Push(c);
        }
        for (var i = all.Count - 1; i >= 0; i--) all[i].Groups.RemoveAll(c => !c.HasContent);
    }

    /// <summary>
    /// BT-149 and BT-150 (CII): the base quantity of the item price and its unit — of the gross price if both prices carry one, else of the one
    /// that does (built by hand after the choice in the visualization).
    /// </summary>
    private static void BaseQuantity(InvoiceMap.Rule rule, InvoiceGroup g, InvoiceElement ctx, HashSet<(string, InvoiceElement, string?)> seen,
        HashSet<(InvoiceElement, string?)> taken)
    {
        if (!seen.Add((rule.Id, ctx, "choice"))) return;   // the three rows of the choice are one decision
        var gross = GrossBase.Value.Select(ctx);
        var net = NetBase.Value.Select(ctx);
        var chosen = gross.Count > 0 ? gross : net;
        // both carry one: the gross price's counts; the net price's is part of it when it says the same (the gross values in a set — a price
        // with many quantities must not compare each with each, FG 826)
        if (gross.Count > 0)
        {
            var grossTexts = gross.Select(g => (rule.Id == "BT-150" ? new InvoiceNode(g.Element, "unitCode") : g).Text).ToHashSet(StringComparer.Ordinal);
            foreach (var n in net)
            {
                var other = rule.Id == "BT-150" ? new InvoiceNode(n.Element, "unitCode") : n;
                if (grossTexts.Contains(other.Text)) taken.Add((other.Element, other.Attribute));
            }
        }
        foreach (var e in chosen)
        {
            var node = rule.Id == "BT-150" ? new InvoiceNode(e.Element, "unitCode") : e;
            if (node.Text is null) continue;
            g.Fields.Add(new InvoiceField(rule.Id, rule.Name, node));
            taken.Add((node.Element, node.Attribute));
        }
    }

    private static readonly Dictionary<string, string> Ram = new() { ["ram"] = InvoiceKind.CiiRam };
    private static readonly Lazy<InvoicePath> GrossBase = new(() => InvoicePath.Parse("./ram:GrossPriceProductTradePrice/ram:BasisQuantity", Ram));
    private static readonly Lazy<InvoicePath> NetBase = new(() => InvoicePath.Parse("./ram:NetPriceProductTradePrice/ram:BasisQuantity", Ram));

    /// <summary>
    /// The notes of UBL (BG-1), built by hand after the visualization: a note of three capital letters followed by another note is the
    /// subject code (BT-21) of that note (BT-22); every other note stands alone.
    /// </summary>
    private static void UblNotes(InvoiceMap map, InvoiceGroup root, InvoiceElement xmlRoot, HashSet<(InvoiceElement, string?)> taken)
    {
        var notes = xmlRoot.ChildrenNamed(InvoiceKind.UblCbc, "Note").ToList();
        var groups = new List<InvoiceGroup>();
        for (var i = 0; i < notes.Count; i++)
        {
            var group = new InvoiceGroup("BG-1", "INVOICE_NOTE", notes[i]);
            var text = notes[i].Text ?? "";
            if (i + 1 < notes.Count && text.Length == 3 && text.All(c => c is >= 'A' and <= 'Z'))
            {
                group.Fields.Add(new InvoiceField("BT-21", "Invoice_note_subject_code", new InvoiceNode(notes[i])));
                taken.Add((notes[i], null));
                i++;
            }
            if (!string.IsNullOrWhiteSpace(notes[i].Text))
            {
                group.Fields.Add(new InvoiceField("BT-22", "Invoice_note", new InvoiceNode(notes[i])));
                taken.Add((notes[i], null));
            }
            if (group.HasContent) groups.Add(group);
        }
        // in the order of the visualization: after the terms of the invoice, before the parties
        var at = root.Groups.FindIndex(g => g.Id != "BG-2" && g.Id.StartsWith("BG", StringComparison.Ordinal));
        root.Groups.InsertRange(at < 0 ? root.Groups.Count : at, groups);
    }

    /// <summary>
    /// Elements of the syntax that carry no term but qualify one — the tax scheme „VAT“, the indicator allowance or charge, the code that tells
    /// a supporting document from an invoiced object or a tender reference. They are part of the term they qualify, not „Weitere Angaben“,
    /// as long as they hold one of the values the syntax binding gives them (the tax scheme without regard to case, the indicator also as 1 or
    /// 0 — as the validation artefacts read them, FG 834); any other value stays visible.
    /// </summary>
    private static readonly InvoiceSyntax[] Cii = [InvoiceSyntax.Cii];
    private static readonly InvoiceSyntax[] Ubl = [InvoiceSyntax.UblInvoice, InvoiceSyntax.UblCreditNote];

    /// <summary>The qualifiers: syntax, grandparent (empty: any), parent, element, the values it may hold (empty: any value).</summary>
    private static readonly (InvoiceSyntax[] Syntaxes, string Grandparent, string Parent, string Local, string[] Values)[] QualifierList =
    [
        (Cii, "", "ApplicableTradeTax", "TypeCode", ["VAT"]),
        (Cii, "", "CategoryTradeTax", "TypeCode", ["VAT"]),
        (Cii, "", "ChargeIndicator", "Indicator", ["true", "false", "1", "0"]),
        (Cii, "", "AdditionalReferencedDocument", "TypeCode", ["916", "50", "130"]),
        (Cii, "SpecifiedLineTradeSettlement", "AdditionalReferencedDocument", "ReferenceTypeCode", []),   // scheme of the line object (BT-128)
        (Ubl, "", "TaxScheme", "ID", ["VAT"]),
        (Ubl, "PartyTaxScheme", "TaxScheme", "ID", []),        // in a tax registration it tells the VAT number (BT-31) from another (BT-32)
        (Ubl, "", "AllowanceCharge", "ChargeIndicator", ["true", "false", "1", "0"]),
        (Ubl, "", "AdditionalDocumentReference", "DocumentTypeCode", ["916", "50", "130"]),
        (Ubl, "InvoiceLine", "DocumentReference", "DocumentTypeCode", ["130"]),
        (Ubl, "CreditNoteLine", "DocumentReference", "DocumentTypeCode", ["130"]),
        (Ubl, "", "CardAccount", "NetworkID", []),             // required by the UBL schema, no term of EN 16931
    ];

    private static void Qualifiers(InvoiceMap map, InvoiceElement root, HashSet<(InvoiceElement, string?)> taken)
    {
        var list = QualifierList.Where(q => q.Syntaxes.Contains(map.Syntax)).ToArray();
        var pending = new Stack<InvoiceElement>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            if (e.Parent is { } p && e.Text is { } text)
                foreach (var q in list)
                    if (e.Local == q.Local && p.Local == q.Parent && (q.Grandparent.Length == 0 || p.Parent?.Local == q.Grandparent)
                        && (q.Values.Length == 0 || q.Values.Any(v => string.Equals(text.Trim(), v, v == "VAT" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))))
                        taken.Add((e, null));
            foreach (var c in e.Children) pending.Push(c);
        }
    }

    /// <summary>
    /// The payment instructions (BG-16), built by hand after the grouping of the visualization: the payment means are grouped by their code;
    /// per group the code (BT-81) and text (BT-82) of the first, the remittance information (BT-83), the accounts (BG-17), cards (BG-18) and
    /// the direct debit (BG-19) of all members.
    /// </summary>
    private static void Payments(InvoiceMap map, InvoiceGroup root, InvoiceElement xmlRoot, HashSet<(InvoiceElement, string?)> taken, CancellationToken cancel)
    {
        var rules = map.Children("ROOT").Where(r => r.Select is null).ToList();
        if (rules.Count == 0) return;
        var spec = rules[0].Context.Split(" | ")[0]["group:".Length..];
        var hash = spec.LastIndexOf('#');
        var members = InvoicePath.Parse(spec[..hash], map.Namespaces).Select(xmlRoot);
        var key = InvoicePath.Parse("./" + spec[(hash + 1)..], map.Namespaces);
        // the groups in the order of their first member, found by their code through an index — a search through the groups for each member
        // made many payment means of different codes quadratic (FG 826)
        var groups = new List<(string? Key, List<InvoiceElement> Members)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var withoutCode = -1;
        foreach (var m in members)
        {
            var keyNodes = key.Select(m.Element);
            foreach (var kn in keyNodes) taken.Add((kn.Element, kn.Attribute));   // the code the group is formed by: part of the group
            var k = keyNodes.Count > 0 ? keyNodes[0].Text : null;   // payment means without their code form a group of their own (FG 824)
            var at = k is null ? withoutCode : index.GetValueOrDefault(k, -1);
            if (at < 0)
            {
                at = groups.Count;
                groups.Add((k, []));
                if (k is null) withoutCode = at;
                else index[k] = at;
            }
            groups[at].Members.Add(m.Element);
        }
        // the paths of the rows, parsed once and not again for every group (FG 826)
        var prepared = new List<(InvoiceMap.Rule Rule, bool AllMembers, InvoicePath Path, InvoicePath? Inner)>();
        foreach (var rule in rules)
        {
            var select = rule.SelectText;
            var allMembers = false;
            if (select.StartsWith("current-group()[1]", StringComparison.Ordinal)) select = "." + select["current-group()[1]".Length..];
            else if (select.StartsWith("current-group()", StringComparison.Ordinal))
            {
                allMembers = true;
                select = "." + select["current-group()".Length..];
            }
            // otherwise „.“ in a for-each-group is the first member
            InvoicePath? inner = null;
            if (rule.Context.Contains("group:current-group()/", StringComparison.Ordinal))
            {
                var spec2 = rule.Context.Split(" | ")[1]["group:current-group()".Length..];
                inner = InvoicePath.Parse("." + spec2[..spec2.LastIndexOf('#')], map.Namespaces);
            }
            prepared.Add((rule, allMembers, InvoicePath.Parse(select, map.Namespaces), inner));
        }
        for (var number = 0; number < groups.Count; number++)
        {
            cancel.ThrowIfCancellationRequested();
            var list = groups[number].Members;
            HashSet<InvoiceElement>? memberSet = null;   // only needed past the repeated groups
            var bg16 = new InvoiceGroup("BG-16", "PAYMENT_INSTRUCTIONS", null);
            foreach (var (rule, allMembers, path, inner) in prepared)
            {
                // from all members together, step by step over distinct nodes (FG 829), or from the first
                var nodes = allMembers ? path.SelectFrom(list) : path.Select(list[0]);
                if (inner is not null)
                {
                    // UBL BT-83: the remittance information of all members, each value once
                    var all = inner.SelectFrom(list);
                    foreach (var n in all) taken.Add((n.Element, n.Attribute));   // repeated in each member: the one value counts
                    nodes = [.. all.GroupBy(n => n.Text).Select(g => g.First())];
                }
                var seen = nodes.Count > 1 ? new HashSet<(InvoiceElement, string?)>(new NodeKey()) : null;
                foreach (var node in nodes)
                {
                    if (seen is not null && !seen.Add((node.Element, node.Attribute))) continue;
                    if (rule.Match is not null && !rule.Match.Matches(node)) continue;
                    if (rule.Id.StartsWith("BG", StringComparison.Ordinal))
                    {
                        // a group outside the members (CII BG-19: the direct debit of the whole settlement) is the same for every payment
                        // group; the visualization repeats it in each — here in the first ones only, so that many payment groups do not
                        // multiply it (FG 826; InvoiceFormat.Check says so)
                        if (number >= MaxRepeatedPaymentGroups
                            && !Within(node.Element, memberSet ??= new HashSet<InvoiceElement>(list, ReferenceEqualityComparer.Instance))) continue;
                        var child = new InvoiceGroup(rule.Id, rule.Name, node.Element);
                        bg16.Groups.Add(child);
                        Fill(map, child, rule.Id, node.Element, taken, cancel);
                    }
                    else if (node.Text is not null)
                    {
                        bg16.Fields.Add(new InvoiceField(rule.Id, rule.Name, node));
                        taken.Add((node.Element, node.Attribute));
                    }
                }
            }
            if (bg16.HasContent) root.Groups.Add(bg16);
        }
    }

    /// <summary>
    /// In how many payment groups a group outside their members is repeated (CII BG-19). EN 16931 has one payment instruction; the
    /// visualization forms one per payment means code, so the usual invoice has one to three.
    /// </summary>
    internal const int MaxRepeatedPaymentGroups = 16;

    /// <summary>Whether the element is one of the members or lies inside one (its ancestors, at most the depth of the tree).</summary>
    private static bool Within(InvoiceElement element, HashSet<InvoiceElement> members)
    {
        for (var e = element; e is not null; e = e.Parent)
            if (members.Contains(e)) return true;
        return false;
    }

    /// <summary>The values of the XML no row took: elements with text, and attributes other than the usual qualifiers of a taken value.</summary>
    private static List<InvoiceFurtherValue> Further(InvoiceElement root, HashSet<(InvoiceElement, string?)> taken)
    {
        var further = new List<InvoiceFurtherValue>();
        var pending = new Stack<InvoiceElement>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            var elementTaken = taken.Contains((e, null));
            if (!elementTaken && !string.IsNullOrWhiteSpace(e.Text) || !elementTaken && e.Binary is not null)
                further.Add(new InvoiceFurtherValue(new InvoiceNode(e)));
            foreach (var (ns, local, _) in e.Attributes)
            {
                if (ns.Length > 0 || taken.Contains((e, local))) continue;
                // the qualifiers of a taken value (scheme, currency, unit, format, list) belong to it
                if (elementTaken && local is "schemeID" or "currencyID" or "unitCode" or "format" or "listID" or "listAgencyID" or "listVersionID"
                        or "schemeAgencyID" or "mimeCode" or "filename" or "languageID") continue;
                further.Add(new InvoiceFurtherValue(new InvoiceNode(e, local)));
            }
            for (var i = e.Children.Count - 1; i >= 0; i--) pending.Push(e.Children[i]);
        }
        return further;
    }

    private sealed class NodeKey : IEqualityComparer<(InvoiceElement, string?)>
    {
        public bool Equals((InvoiceElement, string?) a, (InvoiceElement, string?) b) => ReferenceEquals(a.Item1, b.Item1) && a.Item2 == b.Item2;
        public int GetHashCode((InvoiceElement, string?) x) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(x.Item1), x.Item2);
    }
}
