using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        private static readonly PdfName IdKeyA = PdfName.Get("ID"), NsKey = PdfName.Get("NS"), PKey = PdfName.Get("P");

        /// <summary>
        /// Level a of a part based on PDF 1.x (parts 1 to 3) with the document's own structure (docs/ABLAUFPLAN.md, 6.5 f, step 13; decision of
        /// the user, 8.11.2026): what PDF 1.7 cannot name of a tree with namespaces is written as PDF-Knead's own mapping says
        /// (<see cref="StandardStructure.Pdf17Equivalent"/>). Here, before the copy is written:
        /// <list type="bullet">
        /// <item>an element of the type Artifact of the namespace for PDF 2.0 — content that is no part of the document (line numbers, the reasons
        /// of redaction bars) — leaves the tree with its subtree, its marked content in the page becomes artifact content (<c>/Artifact BMC</c>,
        /// <see cref="ContentRewrite.ArtifactMcids"/>); before it became NonStruct or P and was read out. An Artifact element with content in a
        /// form (an MCR with /Stm) stays an element (its form is not rewritten per element) and is mapped like an own type;</item>
        /// <item>an FENote without /ID gets one (a Note has one, ISO 14289-1, 7.9), the /IDTree is built anew;</item>
        /// <item>the types of the other namespaces get, by name, the 1.7 type their namespace's mapping leads to (<see cref="PdfAState.NamespaceRoles"/>,
        /// written into the role map by <see cref="FixedStructureRoot"/>), as /NS and /Namespaces go (<see cref="PdfALevelA"/>).</item>
        /// </list>
        /// </summary>
        /// <remarks>
        /// In every part, too: a Figure of the tree answered as decoration („nur Schmuck“) leaves the tree the same way, its content artifact
        /// content (docs/ABLAUFPLAN.md, 6.6 e; L4) — before it became NonStruct, and its content stayed real content without meaning. One with
        /// content in a form stays NonStruct (<see cref="PdfALevelA"/>).
        /// </remarks>
        /// <returns>Whether the tree had Artifact elements and all of them left it (none kept for content in forms).</returns>
        private bool DowngradePdf20Elements(PdfReference rootRef, PdfDictionary root)
        {
            var state = _pdfA!;
            var downgrade = state.Part.Part() != 4 && root.ContainsKey(PdfName.Get("Namespaces"));
            var answers = _options.PdfAAccessibility;
            if (!downgrade && answers is null) return false;
            var keptArtifacts = 0;
            var main = _sources[_main];
            PdfObject? Resolve(PdfObject? value) =>
                value is PdfReference r ? main.Overrides.TryGetValue(r.Id.Number, out var replaced) ? replaced : _main.Resolve(r.Id) : value;
            var artifactTops = new List<(PdfReference Element, PdfObject? Parent)>();
            var notes = new List<PdfReference>();
            var roles = new Dictionary<string, string?>(StringComparer.Ordinal);
            var stack = new Stack<(PdfObject? Item, int Depth)>([(Resolve(rootRef) is PdfDictionary current ? current.GetRaw(StructKids) : null, 0)]);
            var seen = new HashSet<int>();
            var budget = 500_000;
            while (stack.TryPop(out var next) && --budget > 0)
            {
                if (next.Depth > 64) continue;
                switch (next.Item)
                {
                    case PdfArray list:
                        for (var i = Math.Min(list.Count, 100_000) - 1; i >= 0; i--) stack.Push((list[i], next.Depth + 1));
                        break;
                    case PdfReference reference when seen.Add(reference.Id.Number) && !main.Removed.Contains(reference.Id.Number):
                        if (Resolve(reference) is not PdfDictionary element || !IsElement(element)) break;
                        var type = StandardStructure.Resolve(element, root, Resolve);
                        // a figure answered as decoration: out of the tree, its content artifact content (6.6 e, No. 652)
                        var key = "e" + reference.Id.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (type?.Name == "Figure" && answers is not null && !element.ContainsKey(PdfName.Get("Alt")) && !element.ContainsKey(PdfName.Get("ActualText")) &&
                            answers.Answered(key, out var alt) && alt is null && !HasFormContent(element, Resolve))
                        {
                            artifactTops.Add((reference, element.GetRaw(PKey)));
                            break;
                        }
                        if (!downgrade)
                        {
                            if (element.GetRaw(StructKids) is { } plainKids) stack.Push((plainKids, next.Depth + 1));
                            break;
                        }
                        if (element.Get(NsKey) is PdfDictionary && element.GetName(S)?.Value is { } name)
                        {
                            // the 1.7 type the element's namespace leads to, by its name — the same name meaning two types decides nothing
                            var role = type is { } t ? t.Space == StructureSpace.Pdf17 ? t.Name : t.Space == StructureSpace.Pdf20 ? StandardStructure.Pdf17Equivalent(t.Name) : null : null;
                            roles[name] = roles.TryGetValue(name, out var before) && before != role ? null : role;
                        }
                        if (type is { Space: StructureSpace.Pdf20, Name: "Artifact" })
                        {
                            if (!HasFormContent(element, Resolve))
                            {
                                artifactTops.Add((reference, element.GetRaw(PKey)));
                                break;   // its subtree goes with it
                            }
                            keptArtifacts++;
                        }
                        if (type is { Space: StructureSpace.Pdf20, Name: "FENote" } && !element.ContainsKey(IdKeyA)) notes.Add(reference);
                        if (element.GetRaw(StructKids) is { } kids) stack.Push((kids, next.Depth + 1));
                        break;
                }
            }
            if (downgrade) state.NamespaceRoles = roles.Where(r => r.Value is not null).ToDictionary(r => r.Key, r => r.Value!, StringComparer.Ordinal);

            // the Artifact elements: out of their parents' /K, their subtrees not written, their marked content artifact content
            var gone = new HashSet<int>();
            foreach (var (element, _) in artifactTops) Subtree(element, Resolve, gone);
            foreach (var (_, parent) in artifactTops.DistinctBy(a => (a.Parent as PdfReference)?.Id.Number ?? -1))
            {
                if (parent is not PdfReference parentRef || Resolve(parentRef) is not PdfDictionary parentDict) continue;
                var copy = Clone(parentDict);
                copy.Set(StructKids, parentDict.GetRaw(StructKids) switch
                {
                    PdfArray list => new PdfArray([.. list.Where(k => !(k is PdfReference r && gone.Contains(r.Id.Number)))]),
                    PdfReference only when gone.Contains(only.Id.Number) => new PdfArray(),
                    var other => other ?? new PdfArray(),
                });
                main.Overrides[parentRef.Id.Number] = copy;
            }
            main.Removed.UnionWith(gone);
            if (gone.Count > 0 && root.GetDictionary(ParentTreeKey) is { } parentTree)
                foreach (var item in _pages)
                {
                    if (!ReferenceEquals(item.Source, _main) || NumberTree.KeyOf(item.Page.Dictionary.Get(StructParents)) is not { } key ||
                        Resolve(NumberTree.Value(parentTree, key)) is not PdfArray array)
                        continue;
                    var mcids = new HashSet<int>();
                    for (var mcid = 0; mcid < Math.Min(array.Count, 1 << 16); mcid++)
                        if (array[mcid] is PdfReference held && gone.Contains(held.Id.Number)) mcids.Add(mcid);
                    if (mcids.Count == 0) continue;
                    state.ArtifactMcidsByPage[item.Page.Dictionary] = mcids;
                    if (item.Page.Dictionary.Get(PdfName.Contents) is PdfStream single) state.ArtifactMcidsByStream[single] = mcids;
                }

            // the notes of PDF 2.0 without an ID: one of their own, not taken, in the /IDTree built anew
            if (notes.Count > 0)
            {
                var rootCopy = main.Overrides.TryGetValue(rootRef.Id.Number, out var replacedRoot) && replacedRoot is PdfDictionary own ? own : Clone(root);
                main.Overrides[rootRef.Id.Number] = rootCopy;
                var pairs = Resolve(rootCopy.GetRaw(PdfName.Get("IDTree"))) is PdfDictionary idTree ? NameTree.Pairs(idTree, Resolve).ToList() : [];
                var taken = pairs.Select(p => Convert.ToHexString(p.Key)).ToHashSet();
                var n = 0;
                foreach (var note in notes)
                {
                    byte[] id;
                    do id = System.Text.Encoding.ASCII.GetBytes("Note-" + (++n).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    while (!taken.Add(Convert.ToHexString(id)));
                    var copy = Clone((PdfDictionary)Resolve(note)!);
                    copy.Set(IdKeyA, new PdfString(id));
                    main.Overrides[note.Id.Number] = copy;
                    pairs.Add((id, note));
                }
                // beyond one leaf the kids as indirect references (7.9.6, Table 36) — before they were written directly (No. 779)
                rootCopy.Set(PdfName.Get("IDTree"), NameTree.Build(pairs, node =>
                {
                    var number = NewNumber();
                    _memoryQueue.Enqueue((number, Rebase(node, main)));
                    return new OutputReference(number);
                }));
            }
            return artifactTops.Count > 0 && keptArtifacts == 0;
        }

        /// <summary>Whether an element (or one below it) holds marked content of a form (a marked-content reference with /Stm).</summary>
        private static bool HasFormContent(PdfDictionary element, Func<PdfObject?, PdfObject?> resolve)
        {
            var stack = new Stack<PdfObject?>([element.GetRaw(StructKids)]);
            var steps = 0;
            while (stack.TryPop(out var next) && ++steps < 50_000)
                switch (resolve(next))
                {
                    case PdfArray list:
                        foreach (var kid in list.Take(100_000)) stack.Push(kid);
                        break;
                    case PdfDictionary dict when dict.GetName(PdfName.Type)?.Value == "MCR":
                        if (dict.ContainsKey(PdfName.Get("Stm"))) return true;
                        break;
                    case PdfDictionary dict when IsElement(dict):
                        stack.Push(dict.GetRaw(StructKids));
                        break;
                }
            return false;
        }

        /// <summary>The object numbers of an element and of the elements below it.</summary>
        private static void Subtree(PdfReference element, Func<PdfObject?, PdfObject?> resolve, HashSet<int> into)
        {
            var stack = new Stack<PdfObject?>([element]);
            var steps = 0;
            while (stack.TryPop(out var next) && ++steps < 200_000)
                switch (next)
                {
                    case PdfArray list:
                        foreach (var kid in list.Take(100_000)) stack.Push(kid);
                        break;
                    case PdfReference reference when into.Add(reference.Id.Number):
                        if (resolve(reference) is PdfDictionary dict && IsElement(dict)) stack.Push(dict.GetRaw(StructKids));
                        else into.Remove(reference.Id.Number);   // a content item (OBJR, MCR) as an object of its own stays
                        break;
                }
        }

        /// <summary>The MCIDs of a page's content (or of a content stream) that are artifact content in the copy (<see cref="DowngradePdf20Elements"/>).</summary>
        private IReadOnlySet<int>? ArtifactsFor(PdfStream? stream, PdfDictionary? page)
        {
            var state = _pdfA!;
            if (page is not null && state.ArtifactMcidsByPage.TryGetValue(page, out var byPage)) return byPage;
            return stream is not null && state.ArtifactMcidsByStream.TryGetValue(stream, out var byStream) ? byStream : null;
        }
    }
}
