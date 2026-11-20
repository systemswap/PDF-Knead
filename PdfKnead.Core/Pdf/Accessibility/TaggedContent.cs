using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Accessibility;

/// <summary>What the content of one page marks, and how it fits the structure tree (<see cref="TaggedContent.Analyse"/>).</summary>
internal sealed class PageTagging
{
    /// <summary>The page (index).</summary>
    public required int Page { get; init; }

    /// <summary>
    /// Painting operators neither in marked content whose MCID the parent tree gives to an element nor in an artifact (ISO 14289-1, 7.1;
    /// ISO 14289-2, 8.2.2) — forms drawn counted with what they paint.
    /// </summary>
    public int Untagged { get; internal set; }

    /// <summary>Artifacts inside tagged content (ISO 14289-1, 7.1).</summary>
    public int ArtifactInTagged { get; internal set; }

    /// <summary>Tagged content inside an artifact (ISO 14289-1, 7.1).</summary>
    public int TaggedInArtifact { get; internal set; }

    /// <summary>Marked content with an MCID the parent tree gives to no element (what it paints counts as untagged too).</summary>
    public int McidsWithoutElement { get; internal set; }

    /// <summary>An MCID marked a second time in the same content (a page, or a form with a key of its own).</summary>
    public int DuplicateMcids { get; internal set; }

    /// <summary>Reference XObjects drawn (ISO 14289-1, 7.20).</summary>
    public int ReferenceXObjects { get; internal set; }

    /// <summary>
    /// Marked content with /ActualText whose language nothing tells: no /Lang there, around it, at its element or above, or in the catalogue
    /// (ISO 14289-1, 7.2).
    /// </summary>
    public int ActualTextWithoutLanguage { get; internal set; }

    /// <summary>Marked content with /Alt whose language nothing tells (as <see cref="ActualTextWithoutLanguage"/>).</summary>
    public int AltWithoutLanguage { get; internal set; }

    /// <summary>Marked content with /E whose language nothing tells (as <see cref="ActualTextWithoutLanguage"/>).</summary>
    public int ExpansionWithoutLanguage { get; internal set; }

    /// <summary>Whether all of the page's content was looked at (no budget reached).</summary>
    public bool Complete { get; internal set; } = true;
}

/// <summary>The marked content of all pages, and the forms with MCIDs drawn more than once (ISO 14289-1, 7.20).</summary>
/// <param name="Pages">Each page.</param>
/// <param name="FormsWithMcidsDrawnTwice">Forms holding MCIDs that are drawn more than once (on one page or on several).</param>
internal sealed record DocumentTagging(IReadOnlyList<PageTagging> Pages, int FormsWithMcidsDrawnTwice);

/// <summary>What a check of the structure tree found (<see cref="TaggedContent.ParentTree"/>).</summary>
internal enum StructureIssueKind
{
    /// <summary>A key of the parent tree occurs twice.</summary>
    DuplicateKey,

    /// <summary>A key of the parent tree is not below /ParentTreeNextKey.</summary>
    KeyNotBelowNextKey,

    /// <summary>A pair of the parent tree names nothing (null, nothing that is an element, an array of nulls).</summary>
    PairWithoutTarget,

    /// <summary>An element, marked-content reference or object reference names a page (/Pg) that is not a page of the document.</summary>
    PageNotInDocument,

    /// <summary>A marked-content reference names a content stream (/Stm) that is not there.</summary>
    StreamMissing,

    /// <summary>An object reference names an object (/Obj) that is not there.</summary>
    ObjectMissing,

    /// <summary>An element has no parent (/P; ISO 14289-1, 7.1; ISO 14289-2, 8.2.1).</summary>
    ParentMissing,

    /// <summary>An entry of the /IDTree names no element (ISO 32000-2, 14.7.2).</summary>
    IdWithoutElement,
}

/// <summary>A finding of <see cref="TaggedContent.ParentTree"/>: what, and where (a key or an object number).</summary>
internal readonly record struct StructureIssue(StructureIssueKind Kind, long Where);

/// <summary>
/// The check helpers for tagged content (docs/ABLAUFPLAN.md, 6.2 step 8; stage C of the check „barrierefrei?“, 6.7): the content of the pages
/// against the structure tree, the parent tree and the references of the tree, the containment rules. Read-only, within budgets; every
/// structure test of phase 6 runs all three and expects nothing.
/// </summary>
internal static class TaggedContent
{
    private const int MaxOperatorsPerPage = 2_000_000, MaxFormDepth = 16, MaxElements = 500_000;

    private static readonly PdfName Mcid = PdfName.Get("MCID"), LangKey = PdfName.Get("Lang"), ActualText = PdfName.Get("ActualText"),
        Alt = PdfName.Get("Alt"), E = PdfName.Get("E"), Properties = PdfName.Get("Properties"), StructParents = PdfName.Get("StructParents"),
        P = PdfName.Get("P"), K = PdfName.Get("K"), Pg = PdfName.Get("Pg"), Stm = PdfName.Get("Stm"), Obj = PdfName.Get("Obj"), S = PdfName.Get("S"), XObject = PdfName.Get("XObject");

    /// <summary>The marked content of every page against the parent tree (forms drawn looked into).</summary>
    public static DocumentTagging Analyse(PdfDocument doc, CancellationToken cancel = default)
    {
        var walk = new Walk(doc);
        var pages = new List<PageTagging>(doc.Pages.Count);
        for (var i = 0; i < doc.Pages.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            pages.Add(walk.Page(i));
        }
        return new DocumentTagging(pages, walk.FormsWithMcidsDrawnTwice);
    }

    /// <summary>The marked content of one page against the parent tree.</summary>
    public static PageTagging Unassigned(PdfDocument doc, int page) => new Walk(doc).Page(page);

    /// <summary>
    /// The parent tree and the references of the tree: keys once and below /ParentTreeNextKey, no pair naming nothing; every /Pg a page of
    /// the document, every /Stm and /Obj there, every element with /P, every entry of the /IDTree an element (ISO 32000-2, 14.7.2, 14.7.5;
    /// docs/FEHLERGEDAECHTNIS.md, No. 363 and 626). <see cref="StructureIssue.Where"/>: the key, the object number of the element, or the
    /// position of the entry in the /IDTree.
    /// </summary>
    public static List<StructureIssue> ParentTree(PdfDocument doc, CancellationToken cancel = default)
    {
        var issues = new List<StructureIssue>();
        if (doc.Catalog?.GetDictionary(PdfName.Get("StructTreeRoot")) is not { } root) return issues;
        if (root.GetDictionary(PdfName.Get("ParentTree")) is { } tree)
        {
            var next = root.GetInteger(PdfName.Get("ParentTreeNextKey"));
            var keys = new HashSet<long>();
            foreach (var (key, raw) in NumberTree.Pairs(tree))
            {
                if (!keys.Add(key)) issues.Add(new(StructureIssueKind.DuplicateKey, key));
                if (next is { } n && key >= n) issues.Add(new(StructureIssueKind.KeyNotBelowNextKey, key));
                if (!NamesSomething(doc, raw)) issues.Add(new(StructureIssueKind.PairWithoutTarget, key));
            }
        }
        var pages = new HashSet<PdfDictionary>(doc.Pages.Select(p => p.Dictionary), ReferenceEqualityComparer.Instance);
        var pageNumbers = doc.Pages.Where(p => p.Id is not null).Select(p => p.Id!.Value.Number).ToHashSet();
        bool OnPage(PdfDictionary owner) => owner.GetRaw(Pg) switch
        {
            null => true,
            PdfReference r => pageNumbers.Contains(r.Id.Number),
            var direct => direct is PdfDictionary d && pages.Contains(d),
        };
        foreach (var (element, number) in Elements(doc, root, cancel))
        {
            if (element.Get(P) is not PdfDictionary) issues.Add(new(StructureIssueKind.ParentMissing, number));
            if (!OnPage(element)) issues.Add(new(StructureIssueKind.PageNotInDocument, number));
            foreach (var kid in Items(element))
            {
                if (kid is not PdfDictionary item) continue;
                var type = item.GetName(PdfName.Type)?.Value;
                if (type is "MCR" or "OBJR" && !OnPage(item)) issues.Add(new(StructureIssueKind.PageNotInDocument, number));
                if (type == "MCR" && item.ContainsKey(Stm) && item.Get(Stm) is not PdfStream) issues.Add(new(StructureIssueKind.StreamMissing, number));
                if (type == "OBJR" && item.Get(Obj) is null or PdfNull) issues.Add(new(StructureIssueKind.ObjectMissing, number));
            }
        }
        if (root.GetDictionary(PdfName.Get("IDTree")) is { } ids)
        {
            var position = 0L;
            foreach (var value in NameTreeValues(ids))
            {
                if (value is not PdfDictionary element || !element.ContainsKey(S)) issues.Add(new(StructureIssueKind.IdWithoutElement, position));
                position++;
            }
        }
        return issues;
    }

    /// <summary>The values of a name tree (ISO 32000-2, 7.9.6), resolved, each node once, within a budget.</summary>
    private static IEnumerable<PdfObject?> NameTreeValues(PdfDictionary root)
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfDictionary Node, int Depth)>();
        stack.Push((root, 0));
        var budget = 1_000_000;
        while (stack.TryPop(out var item) && budget > 0)
        {
            if (item.Depth > 32 || !seen.Add(item.Node)) continue;
            if (item.Node.Get(PdfName.Get("Names")) is PdfArray names)
                for (var i = 1; i < names.Count && --budget > 0; i += 2)
                    yield return names.Get(i);
            if (item.Node.Get(PdfName.Get("Kids")) is PdfArray kids)
                for (var i = Math.Min(kids.Count, 100_000) - 1; i >= 0 && --budget > 0; i--)
                    if (kids.Get(i) is PdfDictionary kid) stack.Push((kid, item.Depth + 1));
        }
    }

    /// <summary>The containment rules the tree breaks (<see cref="Containment.Violations"/>).</summary>
    public static List<string> Nesting(PdfDocument doc) =>
        doc.Catalog?.GetDictionary(PdfName.Get("StructTreeRoot")) is { } root ? Containment.Violations(root) : [];

    /// <summary>Whether a value of the parent tree names an element: one directly, or an array with at least one (nulls are unused MCIDs).</summary>
    private static bool NamesSomething(PdfDocument doc, PdfObject value)
    {
        var resolved = value is PdfReference r ? doc.Resolve(r.Id) : value;
        if (resolved is PdfDictionary element) return element.ContainsKey(S);
        if (resolved is not PdfArray array) return false;
        for (var i = 0; i < array.Count && i < 1 << 20; i++)
            if (array.Get(i) is PdfDictionary d && d.ContainsKey(S)) return true;
        return false;
    }

    /// <summary>The entries of an element's /K, resolved.</summary>
    private static IEnumerable<PdfObject> Items(PdfDictionary element) => element.Get(K) switch
    {
        PdfArray array => Enumerable.Range(0, Math.Min(array.Count, 100_000)).Select(array.Get),
        null => [],
        var one => [one],
    };

    /// <summary>The elements of the tree (each once, depth first), with their object number (-1 for a direct one).</summary>
    private static IEnumerable<(PdfDictionary Element, long Number)> Elements(PdfDocument doc, PdfDictionary root, CancellationToken cancel)
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfObject? Raw, int Depth)>();
        void PushKids(PdfDictionary owner, int depth)
        {
            switch (owner.GetRaw(K))
            {
                case PdfArray array:
                    for (var i = Math.Min(array.Count, 100_000) - 1; i >= 0; i--) stack.Push((array[i], depth));
                    break;
                case PdfReference r when doc.Resolve(r.Id) is PdfArray indirect:
                    for (var i = Math.Min(indirect.Count, 100_000) - 1; i >= 0; i--) stack.Push((indirect[i], depth));
                    break;
                case { } one:
                    stack.Push((one, depth));
                    break;
            }
        }
        PushKids(root, 0);
        var count = 0;
        while (stack.TryPop(out var item) && count < MaxElements)
        {
            if (item.Depth > 256) continue;
            var resolved = item.Raw is PdfReference r ? doc.Resolve(r.Id) : item.Raw;
            if (resolved is not PdfDictionary element || !element.ContainsKey(S) || element.GetName(PdfName.Type)?.Value is "MCR" or "OBJR" ||
                !seen.Add(element))
                continue;
            if ((++count & 1023) == 0) cancel.ThrowIfCancellationRequested();
            yield return (element, item.Raw is PdfReference own ? own.Id.Number : -1);
            PushKids(element, item.Depth + 1);
        }
    }

    /// <summary>The walk through the content of the pages, with what it carries from page to page (the parent tree read once, the forms drawn).</summary>
    private sealed class Walk(PdfDocument doc)
    {
        private readonly Dictionary<long, PdfObject>? _parentTree = ReadParentTree(doc);
        private readonly bool _documentLanguage = doc.Catalog?.Get(LangKey) is PdfString { Bytes.Length: > 0 };
        private readonly Dictionary<PdfStream, int> _formUses = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<PdfStream> _formsWithMcids = new(ReferenceEqualityComparer.Instance);
        private PageTagging _page = null!;
        private int _operators;

        public int FormsWithMcidsDrawnTwice => _formsWithMcids.Count(f => _formUses.GetValueOrDefault(f) > 1);

        private static Dictionary<long, PdfObject>? ReadParentTree(PdfDocument doc)
        {
            if (doc.Catalog?.GetDictionary(PdfName.Get("StructTreeRoot"))?.GetDictionary(PdfName.Get("ParentTree")) is not { } tree) return null;
            var pairs = new Dictionary<long, PdfObject>();
            foreach (var (key, value) in NumberTree.Pairs(tree)) pairs.TryAdd(key, value);
            return pairs;
        }

        /// <summary>The open marked content: inside tagged content (an MCID the tree gives to an element), inside an artifact, a language known.</summary>
        private readonly record struct State(bool Tagged, bool Artifact, bool Language);

        // the resources of the page walked (No. 791)
        private PdfDictionary? _pageResources;

        public PageTagging Page(int index)
        {
            _page = new PageTagging { Page = index };
            _operators = 0;
            var page = doc.Pages[index];
            using var content = PageInterpreter.PageContent(doc, page.Dictionary);
            var key = NumberTree.KeyOf(page.Dictionary.Get(StructParents));
            _pageResources = page.Resources;
            Stream(content.Span.ToArray(), page.Resources, key, new HashSet<long>(), new State(false, false, _documentLanguage), 0, []);
            return _page;
        }

        /// <summary>The element the parent tree gives an MCID of a content (by its key); null if none.</summary>
        private PdfDictionary? ElementOf(long? key, long mcid)
        {
            if (key is not { } k || _parentTree is null || !_parentTree.TryGetValue(k, out var raw)) return null;
            var value = raw is PdfReference r ? doc.Resolve(r.Id) : raw;
            return value is PdfArray array && mcid < array.Count && array.Get((int)mcid) is PdfDictionary element && element.ContainsKey(S) ? element : null;
        }

        /// <summary>Whether an element or one of its ancestors names a language.</summary>
        private static bool HasLanguage(PdfDictionary element)
        {
            PdfDictionary? current = element;
            for (var i = 0; i < 256 && current is not null && current.ContainsKey(S); i++)
            {
                if (current.Get(LangKey) is PdfString { Bytes.Length: > 0 }) return true;
                current = current.Get(P) as PdfDictionary;
            }
            return false;
        }

        /// <param name="data">The decoded content.</param>
        /// <param name="resources">Its resources.</param>
        /// <param name="key">Its key into the parent tree (a page's /StructParents, or a form's own); null: none.</param>
        /// <param name="mcids">The MCIDs marked so far under that key in this content.</param>
        /// <param name="outer">The marked content around it (for a form: where it is drawn).</param>
        /// <param name="depth">How deep in forms.</param>
        /// <param name="open">The forms being walked (a form drawing itself ends there).</param>
        /// <returns>Whether the content marks an MCID itself.</returns>
        private bool Stream(byte[] data, PdfDictionary? resources, long? key, HashSet<long> mcids, State outer, int depth, HashSet<PdfStream> open)
        {
            var parser = new ContentParser(data);
            var marks = new Stack<State>();
            var marksMcid = false;
            State Current() => marks.Count > 0 ? marks.Peek() : outer;
            while (parser.Next() is { } op)
            {
                if (++_operators > MaxOperatorsPerPage)
                {
                    _page.Complete = false;
                    return marksMcid;
                }
                switch (op)
                {
                    case "BMC" or "BDC":
                        var around = Current();
                        var tag = parser.Operands.Count >= 1 ? parser.Operands[0].Object as PdfName : null;
                        var list = op == "BDC" && parser.Operands.Count >= 2
                            ? parser.Operands[1].Object switch
                            {
                                PdfDictionary inline => inline,
                                PdfName name => resources?.GetDictionary(Properties)?.Get(name) as PdfDictionary,
                                _ => null,
                            }
                            : null;
                        var state = around with { Language = around.Language || list?.Get(LangKey) is PdfString { Bytes.Length: > 0 } };
                        if (tag?.Value == "Artifact")
                        {
                            if (around.Tagged) _page.ArtifactInTagged++;
                            state = state with { Artifact = true };
                        }
                        else if (list?.Get(Mcid) is PdfInteger { Value: >= 0 } id)
                        {
                            marksMcid = true;
                            if (around.Artifact) _page.TaggedInArtifact++;
                            if (!mcids.Add(id.Value)) _page.DuplicateMcids++;
                            if (ElementOf(key, id.Value) is { } element)
                                state = state with { Tagged = true, Language = state.Language || HasLanguage(element) };
                            else _page.McidsWithoutElement++;
                        }
                        if (list is not null && !state.Language)
                        {
                            if (list.ContainsKey(ActualText)) _page.ActualTextWithoutLanguage++;
                            if (list.ContainsKey(Alt)) _page.AltWithoutLanguage++;
                            if (list.ContainsKey(E)) _page.ExpansionWithoutLanguage++;
                        }
                        marks.Push(state);
                        break;
                    case "EMC":
                        marks.TryPop(out _);
                        break;
                    case "Do" when parser.Operands.Count >= 1 && parser.Operands[0].Name is { } xname &&
                                   resources?.GetDictionary(XObject)?.Get(xname) is PdfStream xobject &&
                                   xobject.Dictionary.GetName(PdfName.Subtype)?.Value == "Form":
                        Form(xobject, resources, key, mcids, Current(), depth, open);
                        break;
                    default:
                        if (StructureRoles.Paints(op))
                        {
                            var now = Current();
                            if (!now.Tagged && !now.Artifact) _page.Untagged++;
                        }
                        break;
                }
            }
            return marksMcid;
        }

        /// <summary>A form drawn: its content with the marked content around the Do; with /StructParents its MCIDs under its own key.</summary>
        private void Form(PdfStream form, PdfDictionary? outerResources, long? key, HashSet<long> mcids, State around, int depth, HashSet<PdfStream> open)
        {
            if (form.Dictionary.ContainsKey(PdfName.Get("Ref"))) _page.ReferenceXObjects++;
            _formUses[form] = _formUses.GetValueOrDefault(form) + 1;
            if (depth >= MaxFormDepth || !open.Add(form))
            {
                _page.Complete = false;
                return;
            }
            try
            {
                byte[] data;
                try
                {
                    data = doc.GetStreamData(form);
                }
                catch (PdfException)
                {
                    return;
                }
                var own = NumberTree.KeyOf(form.Dictionary.Get(StructParents));
                // without resources of its own: those of the page (7.8.3, sentence 18; docs/FEHLERGEDAECHTNIS.md, No. 791)
                var resources = form.Dictionary.GetDictionary(PdfName.Resources) ?? _pageResources ?? outerResources;
                if (Stream(data, resources, own ?? key, own is not null ? [] : mcids, around, depth + 1, open)) _formsWithMcids.Add(form);
            }
            finally
            {
                open.Remove(form);
            }
        }
    }
}
