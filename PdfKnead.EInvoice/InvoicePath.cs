using System.Globalization;
using System.Text;

namespace PdfKnead.EInvoice;

/// <summary>A node a path reaches: an element of the invoice, or the value of one of its attributes.</summary>
/// <param name="Element">The element (for an attribute: the element that carries it).</param>
/// <param name="Attribute">The local name of the attribute; null for the element itself.</param>
public readonly record struct InvoiceNode(InvoiceElement Element, string? Attribute = null)
{
    /// <summary>Whether the node is an attribute value.</summary>
    public bool IsAttribute => Attribute is not null;

    /// <summary>The value: the attribute's value, or the element's own text (null if it has none, also for an empty node).</summary>
    public string? Text => Element is null ? null : Attribute is { } a ? Element.Attribute(a) : Element.Text;
}

/// <summary>
/// The narrow path language of the mapping tables (KONZEPT.md, section 5.24, „Weg für Stufe 2“): the subset of XPath the XRechnung
/// visualization uses for its selections and patterns, and nothing more — child steps with namespace prefixes, „.“, „..“, a final attribute,
/// the axes following-sibling, preceding-sibling, ancestor and parent, and conditions built from comparisons (=, != against text, numbers
/// or a list of texts, or another path; normalize-space and upper-case around an operand — the generator writes them where the validation
/// artefacts read a code without regard to space and case), exists, empty, not, and, or, a bare path and a position. Anything else is refused when the table
/// is read (<see cref="FormatException"/>), never guessed at run time. Evaluated on the tree of the hardened reader, without recursion over
/// the input beyond the depth of the path; a pattern is matched from the node upwards through its ancestors, so it costs the length of the
/// path, not the size of the document.
/// </summary>
public sealed class InvoicePath
{
    private enum Axis { Child, Attribute, Self, Parent, FollowingSibling, PrecedingSibling, Ancestor }

    private sealed record Step(Axis Axis, string Namespace, string Local, IReadOnlyList<Condition> Conditions);

    private abstract record Condition;
    private sealed record Or(IReadOnlyList<Condition> Parts) : Condition;
    private sealed record And(IReadOnlyList<Condition> Parts) : Condition;
    private sealed record Not(Condition Inner) : Condition;
    private sealed record Exists(InvoicePath Path, bool Negated) : Condition;
    private sealed record Position(int Value) : Condition;
    private sealed record Compare(Operand Left, bool Equal, Operand Right) : Condition;

    private abstract record Operand;
    private sealed record PathOperand(InvoicePath Path) : Operand;

    /// <summary>normalize-space or upper-case around an operand, applied to each of its values.</summary>
    private sealed record Function(string Name, Operand Inner) : Operand;
    private sealed record Texts(IReadOnlyList<string> Values) : Operand;
    private sealed record Number(double Value) : Operand
    {
        /// <summary>The number as the one value of a comparison, made once.</summary>
        public IReadOnlyList<string> Text { get; } = [Value.ToString("R", CultureInfo.InvariantCulture)];
    }

    private readonly IReadOnlyList<Step> _steps;

    private readonly int _simpleFrom = -1;

    /// <summary>The attribute of a path that is only „@name“ (or „./@name“): its value is read directly, without lists.</summary>
    private readonly string? _singleAttribute;

    private InvoicePath(bool absolute, IReadOnlyList<Step> steps)
    {
        Absolute = absolute;
        _steps = steps;
        // the common form „./a/b/c“ or „./a/@b“ without conditions: walked directly, without lists per step
        var from = steps.Count > 0 && steps[0] is { Axis: Axis.Self, Conditions.Count: 0 } ? 1 : 0;
        if (!absolute && steps.Count > from && steps.Skip(from).Select((s, k) => (s, k)).All(x => x.s.Conditions.Count == 0
                && (x.s.Axis == Axis.Child || x.s.Axis == Axis.Attribute && from + x.k == steps.Count - 1)))
            _simpleFrom = from;
        if (!absolute && steps.Count == from + 1 && steps[from] is { Axis: Axis.Attribute, Conditions.Count: 0 } only) _singleAttribute = only.Local;
    }

    /// <summary>The empty result, shared: callers only read what <see cref="Select"/> returns.</summary>
    private static readonly List<InvoiceNode> Nothing = [];

    /// <summary>
    /// The work a reading may do: the nodes the paths visit, counted against a limit that grows with the size of the invoice. A reading that
    /// would exceed it is refused (<see cref="InvoiceXmlRejection.TooComplex"/>) instead of running for minutes — the guarantee behind the
    /// indexes, for shapes no test has thought of (FG 826).
    /// </summary>
    internal sealed class Budget(long limit)
    {
        /// <summary>The nodes visited so far.</summary>
        public long Used { get; private set; }

        /// <summary>The most the reading may visit.</summary>
        public long Limit { get; } = limit;

        public void Spend(long nodes)
        {
            Used += nodes;
            if (Used > Limit) throw new InvoiceXmlException(InvoiceXmlRejection.TooComplex);
        }
    }

    [ThreadStatic] private static Budget? t_budget;

    /// <summary>Counts the paths evaluated on this thread against the budget until the returned scope ends.</summary>
    internal static BudgetScope Use(Budget budget)
    {
        var previous = t_budget;
        t_budget = budget;
        return new BudgetScope(previous);
    }

    /// <summary>Ends the counting of <see cref="Use"/> and restores what was counted before.</summary>
    internal readonly struct BudgetScope(Budget? previous) : IDisposable
    {
        public void Dispose()
        {
            t_budget = previous;
            if (previous is null) TrimLists();   // the outermost scope ends: no large list stays with the thread (FG 842)
        }
    }

    private static void Spend(long nodes) => t_budget?.Spend(nodes);

    // Lists lent within an evaluation and given back when it is done with them, so that a condition checked for each of many nodes does not
    // allocate them anew each time (FG 829: 35 000 amounts with a currency condition took 46 times their size). A list is only given back
    // once it can no longer be seen from outside; a list handed out to a caller is simply not given back.
    [ThreadStatic] private static Stack<List<InvoiceNode>>? t_nodeLists;
    [ThreadStatic] private static Stack<List<string>>? t_stringLists;

    private static List<InvoiceNode> LendNodes() => t_nodeLists is { Count: > 0 } pool ? pool.Pop() : new List<InvoiceNode>(4);

    private static void GiveBack(List<InvoiceNode> list)
    {
        if (ReferenceEquals(list, Nothing) || list.Capacity > MaxPooled) return;   // the shared empty result stays; a huge list is left to the collector
        list.Clear();
        (t_nodeLists ??= new()).Push(list);
    }

    private static List<string> LendStrings() => t_stringLists is { Count: > 0 } pool ? pool.Pop() : new List<string>(4);

    /// <summary>
    /// The largest list given back for use again: also the lists over thousands of siblings (30 000 identifiers in one tax registration
    /// asked by several conditions) are made once, not once per question (FG 842); larger ones are left to the collector.
    /// </summary>
    private const int MaxPooled = 1 << 16;

    /// <summary>Drops the lists over 256 entries the thread keeps for use again — at the end of the outermost budget scope.</summary>
    private static void TrimLists()
    {
        static void Trim<T>(Stack<List<T>>? pool)
        {
            if (pool is null || pool.All(l => l.Capacity <= 256)) return;
            var small = pool.Where(l => l.Capacity <= 256).ToList();
            pool.Clear();
            foreach (var l in small) pool.Push(l);
        }
        Trim(t_nodeLists);
        Trim(t_stringLists);
    }

    private static void GiveBack(List<string> list)
    {
        if (list.Capacity > MaxPooled) return;
        list.Clear();
        (t_stringLists ??= new()).Push(list);
    }

    /// <summary>Whether the path starts at the root („/…“).</summary>
    public bool Absolute { get; }

    /// <summary>Whether the path ends in an attribute.</summary>
    public bool EndsInAttribute => _steps.Count > 0 && _steps[^1].Axis == Axis.Attribute;

    /// <summary>Reads a path of a mapping table.</summary>
    /// <param name="text">The path.</param>
    /// <param name="namespaces">The prefixes of the table.</param>
    /// <exception cref="FormatException">A form outside the narrow language, or an unknown prefix.</exception>
    public static InvoicePath Parse(string text, IReadOnlyDictionary<string, string> namespaces)
    {
        var parser = new Parser(Tokenize(text), namespaces, text);
        var path = parser.PathExpr();
        parser.End();
        return path;
    }

    // ------------------------------------------------------------ evaluation

    /// <summary>
    /// The nodes the path reaches from an element (an absolute path starts at the root of its tree), in document order. The list is only to be
    /// read (an empty result is one shared list).
    /// </summary>
    public List<InvoiceNode> Select(InvoiceElement context)
    {
        if (_simpleFrom >= 0)
        {
            var keep = new Keep();
            var visited = 0L;
            Walk(context, _simpleFrom, ref keep, ref visited);
            Spend(visited);
            return keep.Found ?? Nothing;
        }
        var current = LendNodes();
        current.Add(new(Absolute ? RootOf(context) : context));
        if (Absolute)
        {
            // the first step names the root itself
            if (_steps.Count == 0 || !Fits(_steps[0], current[0].Element))
            {
                GiveBack(current);
                return Nothing;
            }
            if (_steps[0].Conditions.Count > 0)
            {
                var kept = LendNodes();
                Filter(current, _steps[0].Conditions, kept);
                GiveBack(current);
                current = kept;
            }
        }
        return Steps(current, Absolute ? 1 : 0);
    }

    /// <summary>How many nodes the path reaches from an element — counted, no list kept (the counts of the element rules of the profiles, FG 842).</summary>
    public int Count(InvoiceElement context)
    {
        if (_simpleFrom >= 0)
        {
            var tally = new Tally();
            var visited = 0L;
            Walk(context, _simpleFrom, ref tally, ref visited);
            Spend(visited);
            return tally.Count;
        }
        var found = Select(context);
        var n = found.Count;
        GiveBack(found);   // lent by the evaluation and seen by no one else
        return n;
    }

    /// <summary>
    /// The nodes the path reaches from several elements together, as XPath evaluates a path from a node-set: each step goes from the distinct
    /// nodes of the step before. Many elements with one common parent go up to it once — evaluated element by element, a group of 10 000
    /// payment means went from each member to the settlement and its 10 000 siblings (FG 829). Distinct nodes, in the order reached.
    /// </summary>
    public List<InvoiceNode> SelectFrom(IReadOnlyList<InvoiceElement> contexts)
    {
        if (contexts.Count == 0) return Nothing;
        // one element; or an absolute path, which starts at the root whatever the element (one tree)
        if (contexts.Count == 1 || Absolute) return Select(contexts[0]);
        if (_simpleFrom >= 0)
        {
            // child steps from distinct elements reach distinct nodes: element by element is the same, and cheaper
            var all = new List<InvoiceNode>();
            foreach (var c in contexts) all.AddRange(Select(c));
            return all;
        }
        var start = new List<InvoiceNode>(contexts.Count);
        foreach (var c in contexts) start.Add(new InvoiceNode(c));
        var current = Distinct(start);
        if (!ReferenceEquals(current, start)) GiveBack(start);
        return Steps(current, 0);
    }

    /// <summary>
    /// The steps from <paramref name="first"/> on, each from the nodes of the one before (distinct where an axis can meet a node twice). The
    /// list passed in belongs to the evaluation: once a step is done with it, it is given back; the last one goes to the caller.
    /// </summary>
    private List<InvoiceNode> Steps(List<InvoiceNode> current, int first)
    {
        for (var i = first; i < _steps.Count && current.Count > 0; i++)
        {
            var step = _steps[i];
            if (step.Axis == Axis.Self && step.Conditions.Count == 0) continue;   // „.“: the same nodes
            var next = LendNodes();
            List<InvoiceNode>? reached = null;   // one list for the nodes of each axis step, cleared per node (conditions run per node)
            var visited = 0L;
            foreach (var node in current)
            {
                visited++;
                if (node.IsAttribute) continue;   // nothing below an attribute
                if (step.Conditions.Count == 0)
                {
                    // the common steps, straight into the result (a large invoice runs them a million times)
                    Along(step, node.Element, next);
                    continue;
                }
                reached ??= LendNodes();
                reached.Clear();
                Along(step, node.Element, reached);
                visited += reached.Count;
                Filter(reached, step.Conditions, next);
            }
            if (reached is not null) GiveBack(reached);
            Spend(visited + next.Count);
            // children, attributes and the node itself of distinct nodes are distinct; the other axes can reach a node twice
            var result = step.Axis is Axis.Child or Axis.Attribute or Axis.Self || current.Count == 1 ? next : Distinct(next);
            if (!ReferenceEquals(result, next)) GiveBack(next);
            GiveBack(current);   // the nodes of the step before: done with
            current = result;
        }
        return current;
    }

    /// <summary>
    /// What a walk of a simple path does with each node it reaches; false ends the walk. A struct, passed by reference: the walk is made for
    /// each kind and allocates nothing of its own.
    /// </summary>
    private interface IReach
    {
        bool Reached(InvoiceNode node);
    }

    /// <summary>Keeps every node reached, in a list of its own (handed to the caller).</summary>
    private struct Keep : IReach
    {
        public List<InvoiceNode>? Found;

        public bool Reached(InvoiceNode node)
        {
            (Found ??= []).Add(node);
            return true;
        }
    }

    /// <summary>Counts the nodes reached.</summary>
    private struct Tally : IReach
    {
        public int Count;

        public bool Reached(InvoiceNode node)
        {
            Count++;
            return true;
        }
    }

    /// <summary>Ends the walk at the first node reached.</summary>
    private struct First : IReach
    {
        public bool Found;

        public bool Reached(InvoiceNode node)
        {
            Found = true;
            return false;
        }
    }

    /// <summary>
    /// Compares the string value of each node reached with fixed values, as the general comparison of XPath does (true if any pair compares
    /// so); ends the walk at the first pair that does.
    /// </summary>
    private struct FirstFit(List<string> values, bool numeric, bool equal) : IReach
    {
        public bool Found;

        public bool Reached(InvoiceNode node)
        {
            var value = StringValue(node);
            Spend(values.Count);
            for (var k = 0; k < values.Count; k++)
                if (Same(value, values[k], numeric) == equal)
                {
                    Found = true;
                    return false;
                }
            return true;
        }
    }

    /// <summary>
    /// Child steps without conditions, depth first (document order); the depth is the length of the path, never the input's. An element with
    /// many children is gone through by its index of names (FG 826). <paramref name="visited"/> counts the elements gone through and the
    /// nodes reached. False if <paramref name="reach"/> ended the walk.
    /// </summary>
    private bool Walk<T>(InvoiceElement e, int i, ref T reach, ref long visited) where T : struct, IReach
    {
        var step = _steps[i];
        visited++;
        if (step.Axis == Axis.Attribute)
        {
            if (e.Attribute(step.Local) is null) return true;
            visited++;
            return reach.Reached(new InvoiceNode(e, step.Local));
        }
        var children = e.Children;
        var last = i == _steps.Count - 1;
        if (children.Count > InvoiceElement.IndexFrom)
        {
            foreach (var k in e.PositionsOf(step.Namespace, step.Local))
                if (!Further(children[k], i, last, ref reach, ref visited)) return false;
            return true;
        }
        for (var k = 0; k < children.Count; k++)
            if (Fits(step, children[k]) && !Further(children[k], i, last, ref reach, ref visited)) return false;
        return true;
    }

    /// <summary>A child that fits step <paramref name="i"/>: reached if the step is the last, else walked on.</summary>
    private bool Further<T>(InvoiceElement child, int i, bool last, ref T reach, ref long visited) where T : struct, IReach
    {
        if (!last) return Walk(child, i + 1, ref reach, ref visited);
        visited++;
        return reach.Reached(new InvoiceNode(child));
    }

    /// <summary>
    /// Whether a node fits the path read as a pattern: its own name and its ancestors' names, each with its conditions — an absolute pattern up
    /// to the root, a relative one (allowed in XSLT, it fits anywhere) only as far as its steps go.
    /// </summary>
    public bool Matches(InvoiceNode node) => Matches(node, null);

    /// <summary>
    /// <see cref="Matches(InvoiceNode)"/>, remembering in <paramref name="memo"/> whether an element held the conditions of a step: many nodes
    /// below one element (30 000 identifiers in one tax registration) then ask that element's conditions once, not once each (FG 842).
    /// </summary>
    public bool Matches(InvoiceNode node, Dictionary<(InvoicePath Path, int Step, InvoiceElement Element), bool>? memo)
    {
        var i = _steps.Count - 1;
        var element = node.Element;
        if (node.IsAttribute)
        {
            if (i < 0 || _steps[i].Axis != Axis.Attribute || _steps[i].Local != node.Attribute) return false;
            i--;
        }
        else if (i >= 0 && _steps[i].Axis == Axis.Attribute) return false;
        for (; i >= 0; i--, element = element.Parent!)
        {
            if (element is null) return false;
            var step = _steps[i];
            if (step.Axis != Axis.Child || !Fits(step, element)) return false;
            if (step.Conditions.Count > 0)
            {
                // only an ancestor is asked again by its other descendants; the node's own element is asked once, so it is not remembered
                // (30 000 identifiers would otherwise leave 30 000 entries per path behind)
                var remember = memo is not null && !ReferenceEquals(element, node.Element);
                if (!remember || !memo!.TryGetValue((this, i, element), out var held))
                {
                    held = true;
                    for (var k = 0; k < step.Conditions.Count && held; k++)
                        held = Holds(step.Conditions[k], new InvoiceNode(element), 1, 1);
                    if (remember) memo![(this, i, element)] = held;
                }
                if (!held) return false;
            }
            if (i == 0) return !Absolute || element.Parent is null;   // an absolute pattern's first step is the root
        }
        return false;
    }

    private static InvoiceElement RootOf(InvoiceElement e)
    {
        while (e.Parent is not null) e = e.Parent;
        return e;
    }

    private static bool Fits(Step step, InvoiceElement e) => e.Local == step.Local && e.Namespace == step.Namespace;

    /// <summary>The nodes one step reaches from an element, added to <paramref name="into"/> (no iterator, no list of its own).</summary>
    private static void Along(Step step, InvoiceElement e, List<InvoiceNode> into)
    {
        switch (step.Axis)
        {
            case Axis.Self:
                into.Add(new(e));
                break;
            case Axis.Parent:
                if (e.Parent is { } p && (step.Local == "" || Fits(step, p))) into.Add(new(p));
                break;
            case Axis.Child:
            {
                var children = e.Children;
                if (children.Count > InvoiceElement.IndexFrom)
                    foreach (var k in e.PositionsOf(step.Namespace, step.Local)) into.Add(new(children[k]));
                else
                    for (var k = 0; k < children.Count; k++)
                        if (Fits(step, children[k])) into.Add(new(children[k]));
                break;
            }
            case Axis.Attribute:
                if (e.Attribute(step.Local) is not null) into.Add(new(e, step.Local));
                break;
            case Axis.Ancestor:
                for (var a = e.Parent; a is not null; a = a.Parent)
                    if (Fits(step, a)) into.Add(new(a));
                break;
            case Axis.FollowingSibling:
            case Axis.PrecedingSibling:
            {
                if (e.Parent is not { } parent) break;
                var siblings = parent.Children;
                var at = e.Position;   // its place, known since reading — no search through the siblings (FG 826)
                var following = step.Axis == Axis.FollowingSibling;
                if (siblings.Count > InvoiceElement.IndexFrom)
                {
                    // only the siblings of that name, nearest first, through the parent's index
                    var named = parent.PositionsOf(step.Namespace, step.Local);
                    var k = Array.BinarySearch(named, at);
                    var from = k >= 0 ? (following ? k + 1 : k - 1) : (following ? ~k : ~k - 1);
                    if (following)
                        for (var j = from; j < named.Length; j++) into.Add(new(siblings[named[j]]));
                    else
                        for (var j = from; j >= 0; j--) into.Add(new(siblings[named[j]]));
                    break;
                }
                if (following)
                {
                    for (var k = at + 1; k < siblings.Count; k++)
                        if (Fits(step, siblings[k])) into.Add(new(siblings[k]));
                }
                else
                {
                    for (var k = at - 1; k >= 0; k--)
                        if (Fits(step, siblings[k])) into.Add(new(siblings[k]));
                }
                break;
            }
        }
    }

    /// <summary>
    /// The nodes that keep every condition, added to <paramref name="into"/>; each condition sees the positions among the nodes the one
    /// before it kept (as in XPath). One condition — nearly always — needs no list in between.
    /// </summary>
    private static void Filter(List<InvoiceNode> nodes, IReadOnlyList<Condition> conditions, List<InvoiceNode> into)
    {
        for (var c = 0; c < conditions.Count - 1; c++)
        {
            var kept = new List<InvoiceNode>();
            for (var i = 0; i < nodes.Count; i++)
                if (Holds(conditions[c], nodes[i], i + 1, nodes.Count)) kept.Add(nodes[i]);
            nodes = kept;
        }
        var last = conditions[^1];
        for (var i = 0; i < nodes.Count; i++)
            if (Holds(last, nodes[i], i + 1, nodes.Count)) into.Add(nodes[i]);
    }

    private static List<InvoiceNode> Distinct(List<InvoiceNode> nodes)
    {
        if (nodes.Count < 2) return nodes;
        var seen = new HashSet<(InvoiceElement, string?)>(new NodeComparer());
        var result = new List<InvoiceNode>(nodes.Count);
        foreach (var n in nodes)
            if (seen.Add((n.Element, n.Attribute))) result.Add(n);
        return result;
    }

    private sealed class NodeComparer : IEqualityComparer<(InvoiceElement, string?)>
    {
        public bool Equals((InvoiceElement, string?) a, (InvoiceElement, string?) b) => ReferenceEquals(a.Item1, b.Item1) && a.Item2 == b.Item2;
        public int GetHashCode((InvoiceElement, string?) x) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(x.Item1), x.Item2);
    }

    private static bool Holds(Condition condition, InvoiceNode node, int position, int count) => condition switch
    {
        Or o => AnyHolds(o.Parts, node, position, count),
        And a => AllHold(a.Parts, node, position, count),
        Not n => !Holds(n.Inner, node, position, count),
        Exists e => !node.IsAttribute && Any(e.Path, node.Element) != e.Negated,
        Position p => position == p.Value,
        Compare c => Compared(c, node),
        _ => false,
    };

    /// <summary>Whether the path reaches anything from the element (the list of nodes is given back at once).</summary>
    private static bool Any(InvoicePath path, InvoiceElement element)
    {
        if (path._singleAttribute is { } name) return element.Attribute(name) is not null;
        if (path._simpleFrom >= 0)
        {
            // ends at the first node: „[ram:ID]“ over 30 000 identifiers looks at one (FG 842)
            var first = new First();
            var visited = 0L;
            path.Walk(element, path._simpleFrom, ref first, ref visited);
            Spend(visited);
            return first.Found;
        }
        var nodes = path.Select(element);
        var any = nodes.Count > 0;
        GiveBack(nodes);
        return any;
    }

    private static bool AnyHolds(IReadOnlyList<Condition> parts, InvoiceNode node, int position, int count)
    {
        for (var i = 0; i < parts.Count; i++)
            if (Holds(parts[i], node, position, count)) return true;
        return false;
    }

    private static bool AllHold(IReadOnlyList<Condition> parts, InvoiceNode node, int position, int count)
    {
        for (var i = 0; i < parts.Count; i++)
            if (!Holds(parts[i], node, position, count)) return false;
        return true;
    }

    private static bool Compared(Compare c, InvoiceNode node)
    {
        // a simple path against fixed values (the common „ram:ID/@schemeID = 'VA'“): node by node, ending at the first that compares so —
        // over 30 000 identifiers neither a list of them nor of their values, for each such question (FG 842)
        var (path, fixedValues) = c.Left is PathOperand l && c.Right is Texts or Number ? (l.Path, c.Right)
            : c.Right is PathOperand r && c.Left is Texts or Number ? (r.Path, c.Left) : (null, null);
        if (path is { _simpleFrom: >= 0, _singleAttribute: null } && !node.IsAttribute)
        {
            var values = LendStrings();
            try
            {
                Values(fixedValues!, node, values);
                var fit = new FirstFit(values, c.Left is Number || c.Right is Number, c.Equal);
                var visited = 0L;
                path.Walk(node.Element, path._simpleFrom, ref fit, ref visited);
                Spend(visited);
                return fit.Found;
            }
            finally
            {
                GiveBack(values);
            }
        }
        var left = LendStrings();
        var right = LendStrings();
        try
        {
            Values(c.Left, node, left);
            Values(c.Right, node, right);
            Spend((long)left.Count * right.Count);   // at most so many pairs; many against many is work like any other
            var numeric = c.Left is Number || c.Right is Number;
            // general comparison of XPath: true if any pair compares so
            for (var i = 0; i < left.Count; i++)
                for (var k = 0; k < right.Count; k++)
                    if (Same(left[i], right[k], numeric) == c.Equal) return true;
            return false;
        }
        finally
        {
            GiveBack(left);
            GiveBack(right);
        }
    }

    /// <summary>The string values of an operand, added to <paramref name="into"/>.</summary>
    private static void Values(Operand o, InvoiceNode node, List<string> into)
    {
        switch (o)
        {
            case PathOperand p when !node.IsAttribute:
            {
                if (p.Path._singleAttribute is { } name)
                {
                    // „@currencyID“: the value itself, without lists
                    if (node.Element.Attribute(name) is { } value) into.Add(value);
                    Spend(1);
                    break;
                }
                var nodes = p.Path.Select(node.Element);
                foreach (var n in nodes) into.Add(StringValue(n));
                GiveBack(nodes);   // only read here, then done with
                break;
            }
            case Texts t:
                into.AddRange(t.Values);
                break;
            case Number n:
                into.AddRange(n.Text);
                break;
            case Function f:
            {
                var inner = LendStrings();
                try
                {
                    Values(f.Inner, node, inner);
                    foreach (var value in inner) into.Add(f.Name == "upper-case" ? value.ToUpperInvariant() : NormalizeSpace(value));
                    Spend(inner.Count);
                }
                finally
                {
                    GiveBack(inner);
                }
                break;
            }
        }
    }

    /// <summary>normalize-space of XPath: space, tab, line feed and carriage return collapsed to one space and taken off at both ends.</summary>
    private static string NormalizeSpace(string value)
    {
        var sb = new StringBuilder(value.Length);
        var space = false;
        foreach (var ch in value)
        {
            if (ch is ' ' or '\t' or '\n' or '\r')
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The string values of elements with children, each made once: the tree no longer changes once read, and a condition on an element
    /// holding 300 000 empty children was asked for each rule of its pattern — each time a walk over all of them (FG 847).
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InvoiceElement, string> StringValues = new();

    /// <summary>The string value of XPath: an attribute's value, or all text below the element joined.</summary>
    private static string StringValue(InvoiceNode n)
    {
        if (n.IsAttribute) return n.Text ?? "";
        if (n.Element.Children.Count == 0) return n.Element.Text ?? "";
        if (StringValues.TryGetValue(n.Element, out var known)) return known;
        var value = JoinedText(n.Element);
        StringValues.AddOrUpdate(n.Element, value);
        return value;
    }

    /// <summary>All text below an element joined, in document order (own stack); the nodes it visits are counted against the budget.</summary>
    private static string JoinedText(InvoiceElement element)
    {
        var sb = new StringBuilder();
        var pending = new Stack<InvoiceElement>();
        pending.Push(element);
        var visited = 0L;
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            visited++;
            sb.Append(e.Text);
            for (var i = e.Children.Count - 1; i >= 0; i--) pending.Push(e.Children[i]);
        }
        Spend(visited);
        return sb.ToString();
    }

    private static bool Same(string a, string b, bool numeric)
    {
        if (!numeric) return string.Equals(a, b, StringComparison.Ordinal);
        return double.TryParse(a.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(b.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && x == y;
    }

    // ------------------------------------------------------------ reading

    private enum Kind { Name, Text, Number, Slash, At, Dot, DotDot, Axis, LParen, RParen, LBracket, RBracket, Comma, Eq, NotEq, End }

    private readonly record struct Token(Kind Kind, string Value);

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/')
            {
                if (i + 1 < text.Length && text[i + 1] == '/') throw new FormatException($"„//“ wird nicht unterstützt: {text}");
                tokens.Add(new(Kind.Slash, "/")); i++; continue;
            }
            if (c == '@') { tokens.Add(new(Kind.At, "@")); i++; continue; }
            if (c == '(') { tokens.Add(new(Kind.LParen, "(")); i++; continue; }
            if (c == ')') { tokens.Add(new(Kind.RParen, ")")); i++; continue; }
            if (c == '[') { tokens.Add(new(Kind.LBracket, "[")); i++; continue; }
            if (c == ']') { tokens.Add(new(Kind.RBracket, "]")); i++; continue; }
            if (c == ',') { tokens.Add(new(Kind.Comma, ",")); i++; continue; }
            if (c == '=') { tokens.Add(new(Kind.Eq, "=")); i++; continue; }
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '=') { tokens.Add(new(Kind.NotEq, "!=")); i += 2; continue; }
            if (c == '.')
            {
                if (i + 1 < text.Length && text[i + 1] == '.') { tokens.Add(new(Kind.DotDot, "..")); i += 2; continue; }
                tokens.Add(new(Kind.Dot, ".")); i++; continue;
            }
            if (c is '\'' or '"')
            {
                var end = text.IndexOf(c, i + 1);
                if (end < 0) throw new FormatException($"offene Zeichenkette: {text}");
                tokens.Add(new(Kind.Text, text[(i + 1)..end]));
                i = end + 1;
                continue;
            }
            if (char.IsDigit(c))
            {
                var start = i;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                tokens.Add(new(Kind.Number, text[start..i]));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '-' or '.' || (text[i] == ':' && i + 1 < text.Length && text[i + 1] != ':')))
                    i++;
                var name = text[start..i];
                if (i + 1 < text.Length && text[i] == ':' && text[i + 1] == ':')
                {
                    tokens.Add(new(Kind.Axis, name));
                    i += 2;
                    continue;
                }
                tokens.Add(new(Kind.Name, name));
                continue;
            }
            throw new FormatException($"Zeichen „{c}“ wird nicht unterstützt: {text}");
        }
        tokens.Add(new(Kind.End, ""));
        return tokens;
    }

    private sealed class Parser(List<Token> tokens, IReadOnlyDictionary<string, string> namespaces, string text)
    {
        private int _at;

        private Token Peek => tokens[_at];

        private Token Take(Kind kind)
        {
            if (Peek.Kind != kind) throw new FormatException($"erwartet {kind}, gefunden „{Peek.Value}“: {text}");
            return tokens[_at++];
        }

        public void End() => Take(Kind.End);

        public InvoicePath PathExpr()
        {
            var absolute = false;
            if (Peek.Kind == Kind.Slash)
            {
                absolute = true;
                _at++;
            }
            var steps = new List<Step> { StepExpr() };
            while (Peek.Kind == Kind.Slash)
            {
                _at++;
                steps.Add(StepExpr());
            }
            for (var i = 0; i < steps.Count - 1; i++)
                if (steps[i].Axis == Axis.Attribute) throw new FormatException($"ein Attribut nur am Ende: {text}");
            return new InvoicePath(absolute, steps);
        }

        private Step StepExpr()
        {
            Axis axis;
            string ns = "", local = "";
            switch (Peek.Kind)
            {
                case Kind.Dot:
                    _at++;
                    return new Step(Axis.Self, "", "", Conditions());
                case Kind.DotDot:
                    _at++;
                    return new Step(Axis.Parent, "", "", Conditions());
                case Kind.At:
                    _at++;
                    local = Take(Kind.Name).Value;
                    if (local.Contains(':')) throw new FormatException($"Attribute mit Namensraum werden nicht unterstützt: {text}");
                    return new Step(Axis.Attribute, "", local, Conditions());
                case Kind.Axis:
                    axis = Take(Kind.Axis).Value switch
                    {
                        "following-sibling" => Axis.FollowingSibling,
                        "preceding-sibling" => Axis.PrecedingSibling,
                        "ancestor" => Axis.Ancestor,
                        "parent" => Axis.Parent,
                        "child" => Axis.Child,
                        var other => throw new FormatException($"Achse „{other}“ wird nicht unterstützt: {text}"),
                    };
                    break;
                default:
                    axis = Axis.Child;
                    break;
            }
            (ns, local) = QName(Take(Kind.Name).Value);
            return new Step(axis, ns, local, Conditions());
        }

        private (string, string) QName(string name)
        {
            var colon = name.IndexOf(':');
            if (colon < 0) throw new FormatException($"Name ohne Präfix „{name}“: {text}");
            var prefix = name[..colon];
            if (!namespaces.TryGetValue(prefix, out var uri)) throw new FormatException($"unbekanntes Präfix „{prefix}“: {text}");
            return (uri, name[(colon + 1)..]);
        }

        private List<Condition> Conditions()
        {
            var list = new List<Condition>();
            while (Peek.Kind == Kind.LBracket)
            {
                _at++;
                list.Add(Peek.Kind == Kind.Number && tokens[_at + 1].Kind == Kind.RBracket
                    ? new Position(int.Parse(Take(Kind.Number).Value, CultureInfo.InvariantCulture))
                    : OrExpr());
                Take(Kind.RBracket);
            }
            return list;
        }

        private Condition OrExpr()
        {
            var parts = new List<Condition> { AndExpr() };
            while (Peek is { Kind: Kind.Name, Value: "or" })
            {
                _at++;
                parts.Add(AndExpr());
            }
            return parts.Count == 1 ? parts[0] : new Or(parts);
        }

        private Condition AndExpr()
        {
            var parts = new List<Condition> { Unary() };
            while (Peek is { Kind: Kind.Name, Value: "and" })
            {
                _at++;
                parts.Add(Unary());
            }
            return parts.Count == 1 ? parts[0] : new And(parts);
        }

        private Condition Unary()
        {
            if (Peek is { Kind: Kind.Name, Value: "not" or "exists" or "empty" } && tokens[_at + 1].Kind == Kind.LParen)
            {
                var word = tokens[_at].Value;
                _at += 2;
                Condition inner = word == "not" ? OrExpr() : new Exists(PathExpr(), word == "empty");
                Take(Kind.RParen);
                return word == "not" ? new Not(inner) : inner;
            }
            if (Peek.Kind == Kind.LParen)
            {
                _at++;
                var inner = OrExpr();
                Take(Kind.RParen);
                return inner;
            }
            var left = OperandExpr();
            if (Peek.Kind is Kind.Eq or Kind.NotEq)
            {
                var equal = Peek.Kind == Kind.Eq;
                _at++;
                return new Compare(left, equal, OperandExpr());
            }
            if (left is PathOperand p) return new Exists(p.Path, false);   // a bare path: its node exists
            throw new FormatException($"eine Bedingung ohne Vergleich: {text}");
        }

        private Operand OperandExpr()
        {
            switch (Peek.Kind)
            {
                case Kind.Text:
                    return new Texts([Take(Kind.Text).Value]);
                case Kind.Number:
                    return new Number(double.Parse(Take(Kind.Number).Value, CultureInfo.InvariantCulture));
                case Kind.LParen:
                {
                    _at++;
                    var values = new List<string> { Take(Kind.Text).Value };
                    while (Peek.Kind == Kind.Comma)
                    {
                        _at++;
                        values.Add(Take(Kind.Text).Value);
                    }
                    Take(Kind.RParen);
                    return new Texts(values);
                }
                case Kind.Name when Peek.Value is "normalize-space" or "upper-case" && tokens[_at + 1].Kind == Kind.LParen:
                {
                    var name = tokens[_at].Value;
                    _at += 2;
                    var inner = OperandExpr();
                    Take(Kind.RParen);
                    return new Function(name, inner);
                }
                default:
                    return new PathOperand(PathExpr());
            }
        }
    }
}
