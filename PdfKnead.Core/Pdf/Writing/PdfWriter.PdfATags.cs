using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>The numbers of the pages in the new file, by their index in the written order.</summary>
        private int[] _pageNumbers = [];

        private static readonly PdfName StructParents = PdfName.Get("StructParents");
        private static readonly PdfName Lang = PdfName.Get("Lang");

        /// <summary>
        /// Level a (ISO 19005-2, 6.7; ISO 19005-1, 6.8; KONZEPT.md, section 5.21): the language — as given, else the document's own; a document
        /// with a structure tree keeps it (its role map is put right as it is written, <see cref="FixedStructureRoot"/>); a document without one
        /// gets one made from its text blocks and figures (<see cref="PdfATagging.Build"/>): the elements as objects of their own, the parent tree,
        /// and the marks for each page's content, which <see cref="ContentConformance.Rewrite"/> writes in.
        /// </summary>
        private void PrepareTagging()
        {
            var state = _pdfA!;
            if (!state.Part.Accessible()) return;
            var part = state.Part.Part();
            var answers = _options.PdfAAccessibility ?? new PdfAAccessibility();
            // as given, else the document's own, else the language its text is in
            state.Language = PdfATagging.CleanLanguage(answers.Language, part) ??
                             PdfATagging.CleanLanguage(_main.Catalog?.GetString(Lang)?.ToText(), part) ??
                             PdfATagging.CleanLanguage(PdfATagging.ProposeLanguage([.. _pages.Select(p => (p.Source, p.Page))], _cancel), part) ?? "";
            // a structure the copy keeps (none when it is cleaned up or no tagged content is left: one is made, No. 626)
            if (!Cleanup.HasFlag(CleanupItems.Structure) && _main.Catalog?.GetDictionary(StructTreeRoot) is { } root)
            {
                // a tree with namespaces in a part of PDF 1.x: Artifact elements as artifact content, notes with IDs, roles by namespace (6.5 f)
                var allArtifactsGone = _main.Catalog.GetRaw(StructTreeRoot) is PdfReference rootRef && DowngradePdf20Elements(rootRef, root);
                state.StructureTypes = StructureTypes(root);
                // (the Artifact elements gone from the tree need no role — those kept with content in forms do)
                if (allArtifactsGone) state.StructureTypes.Remove("Artifact");
                state.RenamedTypes = RenamedTypes(root, state.StructureTypes, part);
                state.StructureOccurrences = StructureOccurrences(root);
                if (state.RenamedTypes is { } renamed)
                {
                    string Written(string t) => renamed.TryGetValue(t, out var r) ? r.Renamed : t;
                    state.StructureOccurrences = [.. state.StructureOccurrences.Select(o => o with
                    {
                        Type = Written(o.Type), Parent = Written(o.Parent), Kids = [.. o.Kids.Select(Written)], Siblings = [.. o.Siblings.Select(Written)],
                    })];
                }
                return;
            }
            var tagged = PdfATagging.Build([.. _pages.Select(p => (p.Source, p.Page))], answers, _cancel);
            state.Tags = tagged;
            for (var i = 0; i < _pages.Count; i++)
            {
                if (tagged.Pages[i] is not { } marks) continue;
                var page = _pages[i].Page.Dictionary;
                // a content stream two pages share is marked for the first only
                if (page.Get(PdfName.Contents) is PdfStream single)
                {
                    if (!state.MarksByStream.TryAdd(single, marks)) continue;
                }
                else state.MarksByPage[page] = marks;
                state.PageKeys[page] = i;
            }
            state.StructureRoot = NewNumber();
            var documentNumber = NewNumber();
            var parents = new Dictionary<int, SortedDictionary<int, int>>();
            var document = Element(tagged.Document, documentNumber, state.StructureRoot, parents);
            _memoryQueue.Enqueue((documentNumber, document));
            var nums = new PdfArray();
            var keyed = state.PageKeys.Values.ToHashSet();
            foreach (var (page, byMcid) in parents.OrderBy(p => p.Key))
            {
                if (!keyed.Contains(page)) continue;
                var list = new PdfArray();
                var next = 0;
                foreach (var (mcid, element) in byMcid)
                {
                    for (; next < mcid; next++) list.Add(PdfNull.Instance);
                    list.Add(new OutputReference(element));
                    next = mcid + 1;
                }
                nums.Add(PdfInteger.Of(page));
                // the array an object of its own (ISO 32000-2, 7.9.6 for number trees alike; No. 779)
                var listNumber = NewNumber();
                _memoryQueue.Enqueue((listNumber, list));
                nums.Add(new OutputReference(listNumber));
            }
            var parentTree = new PdfDictionary();
            parentTree.Set(PdfName.Get("Nums"), nums);
            var structRoot = new PdfDictionary();
            structRoot.Set(PdfName.Type, PdfName.Get("StructTreeRoot"));
            structRoot.Set(PdfName.Get("K"), new OutputReference(documentNumber));
            structRoot.Set(PdfName.Get("ParentTree"), parentTree);
            structRoot.Set(PdfName.Get("ParentTreeNextKey"), PdfInteger.Of(_pages.Count));
            _memoryQueue.Enqueue((state.StructureRoot, structRoot));
        }

        /// <summary>
        /// One element of a structure made for the copy and, depth first, those below it (each an object of its own, queued): its type, parent,
        /// page, alternative text, and as /K its marked contents or its children — more than 8 000 children grouped in NonStruct (PDF/A-1 allows
        /// no longer list). Notes which element each marked content of a page belongs to.
        /// </summary>
        private PdfDictionary Element(PdfATagging.Node node, int number, int parent, Dictionary<int, SortedDictionary<int, int>> parents)
        {
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, PdfName.Get("StructElem"));
            dict.Set(PdfName.Get("S"), PdfName.Get(node.Type));
            dict.Set(PdfName.Get("P"), new OutputReference(parent));
            if (node.Alt is { } alt) dict.Set(PdfName.Get("Alt"), PdfATagging.Text(alt));
            if (node.Page >= 0 && node.Page < _pageNumbers.Length && node.Mcids.Count > 0)
            {
                dict.Set(PdfName.Get("Pg"), new OutputReference(_pageNumbers[node.Page]));
                if (!parents.TryGetValue(node.Page, out var byMcid)) parents[node.Page] = byMcid = [];
                foreach (var mcid in node.Mcids) byMcid[mcid] = number;
                dict.Set(PdfName.Get("K"), node.Mcids.Count == 1 ? PdfInteger.Of(node.Mcids[0]) : new PdfArray([.. node.Mcids.Select(m => (PdfObject)PdfInteger.Of(m))]));
                return dict;
            }
            var kids = node.Kids;
            if (kids.Count > 8000)
            {
                // groups without meaning, so that no list grows beyond what PDF/A-1 allows
                var grouped = new List<PdfATagging.Node>();
                for (var start = 0; start < kids.Count; start += 8000)
                {
                    var group = new PdfATagging.Node("NonStruct");
                    group.Kids.AddRange(kids.Skip(start).Take(8000));
                    grouped.Add(group);
                }
                kids = grouped;
            }
            var refs = new PdfArray();
            foreach (var kid in kids)
            {
                var kidNumber = NewNumber();
                _memoryQueue.Enqueue((kidNumber, Element(kid, kidNumber, number, parents)));
                refs.Add(new OutputReference(kidNumber));
            }
            dict.Set(PdfName.Get("K"), refs);
            return dict;
        }

        /// <summary>The structure types the elements of a structure tree use.</summary>
        private static HashSet<string> StructureTypes(PdfDictionary root)
        {
            var types = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<PdfObject?>();
            stack.Push(root.Get(PdfName.Get("K")));
            var budget = 500_000;
            while (stack.TryPop(out var k) && --budget > 0)
                switch (k)
                {
                    case PdfArray array:
                        for (var i = 0; i < array.Count; i++) stack.Push(array.Get(i));
                        break;
                    case PdfDictionary element when seen.Add(element):
                        if (element.GetName(PdfName.Get("S"))?.Value is { } type) types.Add(type);
                        stack.Push(element.Get(PdfName.Get("K")));
                        break;
                }
            return types;
        }

        /// <summary>
        /// Parts 2 and 3 forbid mapping a standard type (6.7.3.4). Removing the entry alone made the element literally what its name says — a
        /// „Table“ meant as a box (<c>/RoleMap &lt;&lt; /Table /Div &gt;&gt;</c>) with paragraphs became a table holding paragraphs. Such types get an
        /// own name mapped to the source's target instead (docs/FEHLERGEDAECHTNIS.md, No. 502). Null if there are none.
        /// </summary>
        private static Dictionary<string, (string Renamed, string Target)>? RenamedTypes(PdfDictionary root, HashSet<string> types, int part)
        {
            if (part == 1 || root.Get(PdfName.Get("RoleMap")) is not PdfDictionary roles) return null;
            Dictionary<string, (string, string)>? renamed = null;
            foreach (var (key, _) in roles.Entries)
            {
                if (roles.Get(key) is not PdfName target || target.Value == key.Value || !types.Contains(key.Value) ||
                    !PdfACheck.StandardStructureType(key.Value, part)) continue;
                var name = key.Value + "_eigen";
                for (var n = 2; types.Contains(name) || roles.ContainsKey(PdfName.Get(name)); n++) name = $"{key.Value}_eigen{n}";
                (renamed ??= new(StringComparer.Ordinal))[key.Value] = (name, target.Value);
            }
            return renamed;
        }

        /// <summary>
        /// The structure tree root of a tagged document for level a: a role map in which every type its elements use is standard or maps to one
        /// — without a circle, and (parts 2 and 3) without mapping a standard type (6.7.3.4). A type that does not reach a standard one maps to
        /// the nearest by its name (headings, paragraphs, figures, lists, tables, links, notes) that keeps the containment rules where it is used
        /// (<see cref="ChooseRoles"/>, No. 507), otherwise to P where it holds content, else to NonStruct, which groups without meaning. Null where
        /// nothing changes.
        /// </summary>
        private PdfDictionary? FixedStructureRoot(PdfDictionary root, Func<PdfDictionary> writable)
        {
            var state = _pdfA!;
            if (state.StructureTypes is not { } types) return null;
            var part = state.Part.Part();
            var roles = root.Get(PdfName.Get("RoleMap")) as PdfDictionary;
            var fixedRoles = new PdfDictionary();
            if (roles is not null)
                foreach (var (key, _) in roles.Entries)
                    if (roles.Get(key) is PdfName value && !(part != 1 && PdfACheck.StandardStructureType(key.Value, part)))
                        fixedRoles.Set(key, value);
            // the names of a tree with namespaces: the 1.7 type their namespace leads to (6.5 f; the namespaces go, PdfALevelA)
            foreach (var (name, role) in state.NamespaceRoles ?? [])
                if (!PdfACheck.StandardStructureType(name, part) && !fixedRoles.ContainsKey(PdfName.Get(name)) && PdfACheck.StandardStructureType(role, part))
                    fixedRoles.Set(PdfName.Get(name), PdfName.Get(role));
            // the renamed standard types keep the meaning the source gave them (No. 502)
            var renamedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (_, (renamed, target)) in state.RenamedTypes ?? [])
            {
                fixedRoles.Set(PdfName.Get(renamed), PdfName.Get(target));
                renamedNames.Add(renamed);
            }
            var unreached = new List<string>();
            foreach (var type in types.Concat(renamedNames))
            {
                if (PdfACheck.StandardStructureType(type, part)) continue;
                var seen = new HashSet<string>(StringComparer.Ordinal) { type };
                var current = type;
                var reaches = false;
                while (fixedRoles.Get(PdfName.Get(current)) is PdfName mapped)
                {
                    if (PdfACheck.StandardStructureType(mapped.Value, part))
                    {
                        reaches = true;
                        break;
                    }
                    if (!seen.Add(mapped.Value)) break;
                    current = mapped.Value;
                }
                // (a renamed type that reaches none is chosen like an own type: its old name comes back only where the rules allow it there)
                if (!reaches)
                    foreach (var link in seen)
                        if (!unreached.Contains(link)) unreached.Add(link);
            }
            if (unreached.Count > 0)
            {
                string? Mapped(string t)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    for (var current = t; seen.Add(current) && fixedRoles.Get(PdfName.Get(current)) is PdfName m; current = m.Value)
                        if (PdfACheck.StandardStructureType(m.Value, part)) return m.Value;
                    return null;
                }
                foreach (var (type, role) in ChooseRoles(unreached, Mapped, state.StructureOccurrences ?? [], part))
                    fixedRoles.Set(PdfName.Get(type), PdfName.Get(role));
            }
            if (roles is not null && roles.Count == fixedRoles.Count && fixedRoles.Entries.All(e => roles.Get(e.Key) is PdfName n && n == (PdfName)e.Value))
                return null;
            var result = writable();
            if (fixedRoles.Count > 0) result.Set(PdfName.Get("RoleMap"), fixedRoles);
            else result.Remove(PdfName.Get("RoleMap"));
            return result;
        }

        /// <summary>
        /// Level a, a dictionary as it is written: /Lang made a language tag where it can be („de_DE“ → „de-DE“), else removed (6.7.4); a Figure
        /// element of the document's own structure with the answer given for it — the alternative text, or decoration (the element groups
        /// without meaning then); in a structure made for the copy, annotations lose their old place in a parent tree that no longer exists.
        /// </summary>
        private void PdfALevelA(PdfDictionary dict, Func<PdfDictionary> writable, int number)
        {
            var state = _pdfA!;
            if (!state.Part.Accessible()) return;
            if (dict.Get(Lang) is PdfString lang && !PdfACheck.LanguageTag(lang, state.Part.Part()))
            {
                if (PdfATagging.CleanLanguage(PdfACheck.RawText(lang), state.Part.Part()) is { } clean) writable().Set(Lang, PdfATagging.Text(clean));
                else writable().Remove(Lang);
            }
            if (state.Tags is not null && dict.ContainsKey(PdfName.Get("StructParent")) && dict.IsType(PdfName.Get("Annot")))
                writable().Remove(PdfName.Get("StructParent"));
            // a part of PDF 1.x knows no namespaces: /NS and /Namespaces go, the role map says what each name is (6.5 f)
            if (state.NamespaceRoles is not null)
            {
                if (dict.IsType(PdfName.Get("StructTreeRoot")) && dict.ContainsKey(PdfName.Get("Namespaces"))) writable().Remove(PdfName.Get("Namespaces"));
                else if (dict.ContainsKey(PdfName.Get("NS")) && dict.ContainsKey(PdfName.Get("S")) && !dict.IsType(PdfName.Get("StructTreeRoot")))
                    writable().Remove(PdfName.Get("NS"));
            }
            // a standard type the source mapped elsewhere: renamed, not read literally (No. 502)
            if (state.RenamedTypes is { } renamedTypes && dict.GetName(PdfName.Get("S"))?.Value is { } s && renamedTypes.TryGetValue(s, out var renamedType) &&
                !dict.IsType(PdfName.Get("StructTreeRoot")) && (dict.IsType(PdfName.Get("StructElem")) || dict.ContainsKey(PdfName.Get("P"))))
                writable().Set(PdfName.Get("S"), PdfName.Get(renamedType.Renamed));
            if (state.Tags is null && number > 0 && dict.IsType(PdfName.Get("StructElem")) && state.Origin.TryGetValue(number, out var at) && ReferenceEquals(at.Source.Doc, _main) &&
                _options.PdfAAccessibility is { } answers && !dict.ContainsKey(PdfName.Get("Alt")) && !dict.ContainsKey(PdfName.Get("ActualText")) &&
                answers.Figures.ContainsKey("e" + at.Old.ToString(System.Globalization.CultureInfo.InvariantCulture)) &&
                answers.Answered("e" + at.Old.ToString(System.Globalization.CultureInfo.InvariantCulture), out var alt))
            {
                if (alt is not null) writable().Set(PdfName.Get("Alt"), PdfATagging.Text(alt));
                else writable().Set(PdfName.Get("S"), PdfName.Get("NonStruct"));
            }
        }

        /// <summary>Level a in the catalogue: marked (/MarkInfo /Marked true), the language, the structure made for the copy.</summary>
        private void PdfATaggedCatalog(PdfDictionary catalog)
        {
            var state = _pdfA!;
            if (!state.Part.Accessible()) return;
            var markInfo = catalog.Get(PdfName.Get("MarkInfo")) is PdfDictionary given ? Clone(given) : new PdfDictionary();
            markInfo.Set(PdfName.Get("Marked"), PdfBoolean.True);
            catalog.Set(PdfName.Get("MarkInfo"), markInfo);
            if (state.Language.Length > 0) catalog.Set(Lang, PdfATagging.Text(state.Language));
            if (state.Tags is not null) catalog.Set(StructTreeRoot, new OutputReference(state.StructureRoot));
        }

        /// <summary>The marks for a content stream of a page (or the page's content in parts) in a structure made for the copy; null if none.</summary>
        private PdfATagging.PageMarks? MarksFor(PdfStream? stream, PdfDictionary? page)
        {
            var state = _pdfA!;
            if (state.Tags is null) return null;
            if (page is not null && state.MarksByPage.TryGetValue(page, out var byPage)) return byPage;
            if (page?.Get(PdfName.Contents) is PdfStream single && state.MarksByStream.TryGetValue(single, out var bySingle)) return bySingle;
            return stream is not null && state.MarksByStream.TryGetValue(stream, out var byStream) ? byStream : null;
        }
    }
}
