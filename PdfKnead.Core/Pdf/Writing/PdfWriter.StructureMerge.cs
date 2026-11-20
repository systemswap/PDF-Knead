using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>
        /// The structure trees of the files merged into a tagged copy (docs/ABLAUFPLAN.md, 6.5 steps 1–5 and 17; ISO 32000-2, 14.7.2, 14.7.5.4,
        /// 14.8.4.3; ISO 14289-2, 8.2.5.2; docs/FEHLERGEDAECHTNIS.md, No. 638 and 639): each tagged source is pruned to its pages written
        /// (<see cref="TreePruner"/>), its keys get a range of their own (<see cref="Source.StructKeyOffset"/>, written into its pages,
        /// annotations and forms), its pairs go into the one parent tree built anew, its roles, classes and IDs join those of the copy — renamed
        /// where they clash (<see cref="MergedNames"/>) — and its top elements hang under the top Document of the copy: one element per run of
        /// its pages in a row (Part; in 2.0 with the namespace for PDF 2.0 a DocumentFragment, or a Document for the whole file), at the place
        /// in the reading order the pages give. A source the steps built so far cannot take (namespaces, <see cref="MergeableTree"/>; an element
        /// meaning a standard type the copy maps to another, <see cref="TakesRemappedType"/>) keeps what it had before: its keys go, its content
        /// belongs to no element (the save says so).
        /// </summary>
        /// <summary>
        /// The pages of a merged file whose tree does not come along (<see cref="Source.StructKeyOffset"/> null): their marked content loses its
        /// /MCID — in the content streams, in the property lists named in /Properties and in the forms they draw (which also lose their
        /// /StructParents) — as their /StructParents went (No. 435): a page with structural content items needs a key into the parent tree
        /// (ISO 32000-2, 14.7.5.4, Table 31), and the items would name elements that are not there (docs/FEHLERGEDAECHTNIS.md, No. 689; found by
        /// the run of all ways to write: a PDF 2.0 file merged into a copy written as 1.7). The tags stay (/P, /Span …), so does every other
        /// entry of the lists. Changed objects as overrides of the source; the rest byte for byte.
        /// </summary>
        private void UnmarkForeignContent()
        {
            foreach (var source in _sourceOrder)
            {
                if (source.IsMain || source.StructKeyOffset is not null || source.Items.Count == 0) continue;
                var seen = new HashSet<int>();
                var n = 0;
                foreach (var item in source.Items.Take(100_000))
                {
                    if ((++n & 15) == 0) _cancel.ThrowIfCancellationRequested();
                    UnmarkContent(source, item.Page.Dictionary.GetRaw(PdfName.Contents), seen);
                    UnmarkResources(source, item.Page.Dictionary.GetRaw(PdfName.Resources), seen, 0);
                }
            }
        }

        private static readonly byte[] McidBytes = [(byte)'M', (byte)'C', (byte)'I', (byte)'D'];

        /// <summary>A property list without /MCID; null if it has none.</summary>
        private static PdfDictionary? WithoutMcid(PdfDictionary list)
        {
            if (!list.ContainsKey(PdfName.Get("MCID"))) return null;
            var copy = Clone(list);
            copy.Remove(PdfName.Get("MCID"));
            return copy;
        }

        /// <summary>The content streams named by /Contents (a stream or an array of them) without /MCID in their property lists.</summary>
        private static void UnmarkContent(Source source, PdfObject? contents, HashSet<int> seen)
        {
            switch (contents)
            {
                case PdfArray parts:
                    for (var i = 0; i < parts.Count && i < 10_000; i++) UnmarkContent(source, parts[i], seen);
                    break;
                case PdfReference reference when seen.Add(reference.Id.Number) && source.Doc.Resolve(reference.Id) is PdfStream stream:
                    if (Unmarked(source, stream) is { } plain) source.Overrides[reference.Id.Number] = plain;
                    break;
            }
        }

        /// <summary>
        /// A stream with its data decoded and without /MCID, its dictionary without the filters (a form also without its keys into the parent
        /// tree); null if nothing changes.
        /// </summary>
        private static PdfStream? Unmarked(Source source, PdfStream stream, bool form = false)
        {
            byte[] data;
            try
            {
                data = source.Doc.GetStreamData(stream);
            }
            catch (Exception e) when (e is PdfException or InvalidDataException)
            {
                return null;
            }
            var written = data.AsSpan().IndexOf(McidBytes) >= 0 ? MarkedContentIds.WithPropertyLists(data, WithoutMcid) : data;
            var keyed = form && (stream.Dictionary.ContainsKey(StructParents) || stream.Dictionary.ContainsKey(StructParentKey));
            if (ReferenceEquals(written, data) && !keyed) return null;
            var dict = Clone(stream.Dictionary);
            foreach (var key in (ReadOnlySpan<string>)["Filter", "DecodeParms", "Length", "DL"]) dict.Remove(PdfName.Get(key));
            if (form)
            {
                dict.Remove(StructParents);
                dict.Remove(StructParentKey);
            }
            return new PdfStream(dict, written);
        }

        /// <summary>Resources: their named property lists without /MCID, the forms they hold unmarked (8 levels deep).</summary>
        private static void UnmarkResources(Source source, PdfObject? raw, HashSet<int> seen, int depth)
        {
            if (depth > 8) return;
            var resources = raw is PdfReference r
                ? (source.Overrides.TryGetValue(r.Id.Number, out var o) ? o : source.Doc.Resolve(r.Id)) as PdfDictionary
                : raw as PdfDictionary;
            if (resources is null) return;
            // named property lists: one of its own changed as such; lists written into /Properties changed with /Properties (an object of its
            // own) or with the resources (an object of their own); within a page's own dictionary they stay (rare)
            var propertiesRaw = resources.GetRaw(PdfName.Get("Properties"));
            var properties = propertiesRaw is PdfReference pr ? source.Doc.Resolve(pr.Id) as PdfDictionary : propertiesRaw as PdfDictionary;
            if (properties is not null)
            {
                PdfDictionary? changed = null;
                foreach (var (name, value) in properties.Entries.Take(10_000))
                {
                    if (value is PdfReference listRef)
                    {
                        if (seen.Add(listRef.Id.Number) && source.Doc.Resolve(listRef.Id) is PdfDictionary list && WithoutMcid(list) is { } plainList)
                            source.Overrides[listRef.Id.Number] = plainList;
                    }
                    else if (value is PdfDictionary inline && WithoutMcid(inline) is { } plainInline) (changed ??= Clone(properties)).Set(name, plainInline);
                }
                if (changed is not null)
                {
                    if (propertiesRaw is PdfReference propertiesRef) source.Overrides[propertiesRef.Id.Number] = changed;
                    else if (raw is PdfReference resourcesRef)
                    {
                        var copy = Clone(resources);
                        copy.Set(PdfName.Get("Properties"), changed);
                        source.Overrides[resourcesRef.Id.Number] = copy;
                    }
                }
            }
            // the forms drawn
            if (resources.Get(XObject) is not PdfDictionary xobjects) return;
            foreach (var (_, value) in xobjects.Entries.Take(10_000))
            {
                if (value is not PdfReference formRef || !seen.Add(formRef.Id.Number) || source.Doc.Resolve(formRef.Id) is not PdfStream form ||
                    form.Dictionary.GetName(PdfName.Subtype)?.Value != "Form")
                    continue;
                if (Unmarked(source, form, form: true) is { } plainForm) source.Overrides[formRef.Id.Number] = plainForm;
                UnmarkResources(source, form.Dictionary.GetRaw(PdfName.Resources), seen, depth + 1);
            }
        }

        private void MergeStructure()
        {
            // the copy's tree — or a new one where the copy is untagged (6.5 c)
            if ((KeepsStructure() ? StructureEdits() : NewStructureTree()) is not { } plan) return;
            var mainRoot = plan.View.Root;
            foreach (var source in _sourceOrder)
            {
                if (source.IsMain || !MergeableTree(source, mainRoot, out var root)) continue;
                var written = source.PageOut.Keys.ToHashSet();
                var pruner = new TreePruner(source, written);
                var top = pruner.Prune(root.GetRaw(StructKids), null, 0);
                if (top is null || pruner.Budget < 0) continue;
                var unwrapped = SingleDocument(source, root, top);
                // its namespaces as the copy takes them (6.5 g); a tree that cannot come this way keeps the state before
                if (NamespacesOf(source, root, plan, top) is not { } spaces || TakesRemappedType(source, root, plan, top, unwrapped, spaces)) continue;

                // keys: a range of their own, as wide as the source's keys reach
                long reach = root.GetInteger(PdfName.Get("ParentTreeNextKey")) ?? 0;
                var tree = root.GetDictionary(ParentTreeKey);
                var pairs = tree is null ? [] : NumberTree.Pairs(tree).ToList();
                foreach (var (key, _) in pairs) reach = Math.Max(reach, key + 1);
                var offset = plan.ReserveKeys(reach);
                source.StructKeyOffset = offset;
                var unused = KeysOfPagesNotWritten(source.Doc, written);
                foreach (var (key, value) in pairs)
                    if (!unused.Contains(key) && !pruner.NamesOnlyRemoved(value)) plan.AddRebasedPair(key + offset, Rebase(value, source));

                // its roles, classes and IDs: those clashing with the copy's renamed in its elements, the others taken over (6.5 b)
                var names = MergedNames.Of(source, root, plan, top, this);
                top = new MergeRenamer(source, names, spaces).Kids(top, 0);
                foreach (var handle in spaces.Added) plan.AddNamespace(handle);
                foreach (var (role, standard) in names.Roles)
                    if (!plan.HasRole(role)) plan.AddRole(role, standard);
                foreach (var (name, value) in names.Classes)
                    if (plan.ClassOf(name) is null) plan.AddClass(name, Rebase(value, source));

                // the top elements: a single Document of the source is unwrapped (its language goes to the new elements)
                var items = top is PdfArray array ? [.. array] : new List<PdfObject> { top };
                string? lang = source.Doc.Catalog?.GetString(LangKey)?.ToText();
                if (unwrapped is not null && Current(source, unwrapped) is { } document)
                {
                    lang = document.GetString(LangKey)?.ToText() ?? lang;
                    items = document.GetRaw(StructKids) switch
                    {
                        PdfArray inner => [.. inner],
                        null => [],
                        var one => [one],
                    };
                    source.Removed.Add(unwrapped.Id.Number);
                }
                // only a valid tag is written: the one meant where it is clear („English“ → „en“), else none (No. 638)
                lang = lang?.Trim() is { Length: > 0 } given ? LanguageTags.Valid(given) ? given : LanguageTags.Proposal(given) : null;
                if (lang is not null && lang == DocumentLanguage) lang = null;
                source.PartLanguage = lang;

                // one element per run of its pages in a row, at the place of the run's first page
                var runs = PageRuns(source);
                var byRun = new List<PdfObject>[runs.Count];
                for (var i = 0; i < runs.Count; i++) byRun[i] = [];
                var last = 0;
                foreach (var item in items)
                {
                    var first = FirstPageOf(source, item);
                    var run = first is { } page ? runs.FindIndex(r => page >= r.First && page <= r.Last) : -1;
                    last = run >= 0 ? run : last;
                    byRun[last].Add(item);
                }
                var whole = runs.Count == 1 && written.Count == source.Doc.PageCount;
                var type = plan.Pdf20Types ? whole ? "Document" : "DocumentFragment" : "Part";
                // a part PDF-Knead made for this document (the redaction protocol): its elements join the top Document directly (6.6 i, No. 658)
                var own = _options.OwnParts?.Contains(source.Doc) == true;
                for (var i = 0; own && i < runs.Count; i++)
                    foreach (var item in byRun[i])
                    {
                        if (item is not PdfReference kid || Current(source, kid) is not { } element || !element.ContainsKey(S) ||
                            Rebase(item, source) is not OutputReference handle || plan.InsertTopKid(handle, runs[i].First) is not { } topDocument)
                            continue;
                        var copy = Clone(element);
                        copy.Set(PdfName.Get("P"), topDocument);
                        if (lang is not null) copy.Set(LangKey, new PdfString(PdfTextEncoding.Encode(lang)));
                        source.Overrides[kid.Id.Number] = copy;
                        source.FirstPart ??= handle;
                    }
                for (var i = 0; !own && i < runs.Count; i++)
                {
                    if (byRun[i].Count == 0 || plan.NewTopElement(type, runs[i].First, lang) is not { } made) continue;
                    source.FirstPart ??= made;
                    foreach (var item in byRun[i])
                    {
                        if (item is PdfReference kid && Current(source, kid) is { } element && element.ContainsKey(S))
                        {
                            var copy = Clone(element);
                            copy.Set(PdfName.Get("P"), made);
                            source.Overrides[kid.Id.Number] = copy;
                        }
                        plan.AppendKid(made, Rebase(item, source));
                    }
                }
                // the IDs into the /IDTree of the copy (not that of a Document unwrapped)
                foreach (var (id, element) in names.Ids)
                    if (!source.Removed.Contains(element.Id.Number)) plan.AddId(id, Rebase(element, source));
                // a new tree is written once a part came along: the catalog names it (6.5 c)
                if (ReferenceEquals(plan, _newTreePlan)) _newTreeRoot = _newTreeNumber;
            }
        }

        /// <summary>
        /// Whether a source's tree can be merged: tagged, its root an object of its own. (Namespaces: <see cref="NamespacesOf"/>; a page taken
        /// twice gets a structure of its own for its further copies: <see cref="DuplicatePageStructure"/>.)
        /// </summary>
        private static bool MergeableTree(Source source, PdfDictionary mainRoot, out PdfDictionary root)
        {
            root = null!;
            if (!StructureRoles.IsTagged(source.Doc.Catalog) || source.Doc.Catalog!.GetRaw(StructTreeRoot) is not PdfReference rootRef ||
                source.Doc.Resolve(rootRef.Id) is not PdfDictionary found)
                return false;
            root = found;
            return true;
        }

        /// <summary>
        /// The single Document at the top of a merged source's tree (pruned), unwrapped — its kids hang in the new element; null if there is
        /// none, or the source maps Document to another type (then it is an element like any other, renamed by <see cref="MergedNames"/>).
        /// </summary>
        private static PdfReference? SingleDocument(Source source, PdfDictionary root, PdfObject top) =>
            (top is PdfArray { Count: 1 } one ? one[0] : top) is PdfReference only && Current(source, only)?.GetName(S)?.Value == "Document" &&
            !(root.GetDictionary(RoleMapKey)?.Get(PdfName.Get("Document")) is PdfName mapped && mapped.Value != "Document")
                ? only
                : null;

        /// <summary>
        /// Whether an element the source keeps means a standard type the copy maps to another (against ISO 14289-1, 7.1, in the copy anyway; the
        /// type it means: its /S through the source's role map up to the first type the source does not map on): in the copy it would take the
        /// copy's meaning, and without namespaces no new name escapes the role map (a name mapped to that type is mapped on by the copy's entry,
        /// ISO 32000-2, 14.7.3) — such a source keeps the state before (6.5 g). The Document unwrapped does not count.
        /// </summary>
        private static bool TakesRemappedType(Source source, PdfDictionary root, StructurePlan plan, PdfObject top, PdfReference? unwrapped,
            MergedNamespaces spaces)
        {
            var roles = root.GetDictionary(RoleMapKey);
            PdfObject? Resolve(PdfObject? value) => value is PdfReference r ? (PdfObject?)Current(source, r) ?? source.Doc.Resolve(r.Id) : value;
            foreach (var (reference, element) in MergedElements(source, top))
            {
                if (unwrapped is not null && reference?.Id.Number == unwrapped.Id.Number || element.GetName(S) is not { } meant) continue;
                // an element in a namespace: the role map does not reach it — unless it becomes a type of PDF 1.7 (6.5 g)
                if (element.Get(NsEntry) is PdfDictionary)
                {
                    if (!spaces.To17 || Pdf17TypeOf(element, root, Resolve) is not { } as17) continue;
                    meant = PdfName.Get(as17);
                }
                else
                    for (var steps = 0; steps < 32 && roles?.Get(meant) is PdfName next && next != meant; steps++) meant = next;
                if (StandardStructure.IsStandard(meant.Value, StructureSpace.Pdf17) && plan.RoleOf(meant) is { } mapped && mapped != meant) return true;
            }
            return false;
        }

        /// <summary>
        /// The names of a merged source as the copy takes them (docs/ABLAUFPLAN.md, 6.5 b; ISO 32000-2, 14.7.2, 14.7.3, 14.7.6.2): a role it
        /// maps differently from the copy — or a standard type it maps to another, which in the copy's role map would change the copy's elements
        /// — gets a name of its own („Absatz_2“), its entry and every entry naming it follow; a class the copy has with other attributes the
        /// same; an ID an element of the copy has already (or one of its own elements before) gets a free one („P1_2“), /Headers of its table
        /// cells follow. A /Ref names elements by reference and follows by itself.
        /// </summary>
        private sealed class MergedNames
        {
            /// <summary>Renamed roles and classes: old name → new name.</summary>
            public readonly Dictionary<string, string> RoleRenames = new(StringComparer.Ordinal), ClassRenames = new(StringComparer.Ordinal);

            /// <summary>The role map entries of the source as the copy takes them (renamed where needed).</summary>
            public readonly List<(PdfName Type, PdfName Standard)> Roles = [];

            /// <summary>The classes of the source the copy does not have yet (renamed where needed), the values as the source has them.</summary>
            public readonly List<(PdfName Name, PdfObject Value)> Classes = [];

            /// <summary>The first element of the source with an ID: old ID (hexadecimal) → the one it has in the copy (for /Headers).</summary>
            public readonly Dictionary<string, byte[]> IdRenames = [];

            /// <summary>Each element of the source with an ID: the ID it has in the copy.</summary>
            public readonly Dictionary<PdfDictionary, byte[]> ElementIds = new(ReferenceEqualityComparer.Instance);

            /// <summary>The pairs for the /IDTree of the copy (elements that are objects of their own).</summary>
            public readonly List<(byte[] Id, PdfReference Element)> Ids = [];

            public static MergedNames Of(Source source, PdfDictionary root, StructurePlan plan, PdfObject top, Session session)
            {
                var names = new MergedNames();

                // roles: identities skipped (a standard type mapped to itself means itself; an own type to itself is a circle)
                var roleMap = root.GetDictionary(RoleMapKey);
                var entries = roleMap is null ? [] : roleMap.Entries
                    .Select(e => (Type: e.Key, Standard: roleMap.Get(e.Key) as PdfName))
                    .Where(e => e.Standard is not null && e.Standard != e.Type).Select(e => (e.Type, Standard: e.Standard!)).ToList();
                var takenRoles = entries.Select(e => e.Type.Value).ToHashSet(StringComparer.Ordinal);
                foreach (var (type, standard) in entries)
                {
                    var clash = plan.RoleOf(type) is { } mapped ? mapped != standard : StandardStructure.IsStandard(type.Value, StructureSpace.Pdf17);
                    if (!clash) continue;
                    var name = FreeName(type.Value, n => takenRoles.Contains(n) || plan.HasRole(PdfName.Get(n)) || StandardStructure.IsStandard(n, StructureSpace.Pdf17));
                    takenRoles.Add(name);
                    names.RoleRenames[type.Value] = name;
                }
                PdfName Role(PdfName n) => names.RoleRenames.TryGetValue(n.Value, out var renamed) ? PdfName.Get(renamed) : n;
                foreach (var (type, standard) in entries) names.Roles.Add((Role(type), Role(standard)));

                // classes: one with the same attributes is shared, one with others renamed
                if (root.GetDictionary(PdfName.Get("ClassMap")) is { } classMap)
                {
                    var takenClasses = classMap.Entries.Select(e => e.Key.Value).ToHashSet(StringComparer.Ordinal);
                    var main = session._sources[session._main];
                    foreach (var (name, value) in classMap.Entries)
                    {
                        var given = name;
                        if (plan.ClassOf(name) is { } existing)
                        {
                            if (SameAttributes(existing, main, value, source, 0)) continue;
                            var free = FreeName(name.Value, n => takenClasses.Contains(n) || plan.ClassOf(PdfName.Get(n)) is not null);
                            takenClasses.Add(free);
                            names.ClassRenames[name.Value] = free;
                            given = PdfName.Get(free);
                        }
                        names.Classes.Add((given, value));
                    }
                }

                // IDs: unique in the copy — against its own and those of the source not yet seen
                var elements = MergedElements(source, top).ToList();
                var sourceIds = elements.Select(e => e.Element.Get(IdKey)).OfType<PdfString>().Select(s => Convert.ToHexString(s.Bytes)).ToHashSet();
                var assigned = new HashSet<string>();
                foreach (var (reference, element) in elements)
                {
                    if (element.Get(IdKey) is not PdfString id) continue;
                    var final = id.Bytes;
                    if (plan.HasId(final) || assigned.Contains(Convert.ToHexString(final)))
                        for (var n = 2; ; n++)
                        {
                            final = WithSuffix(id.Bytes, n);
                            var hex = Convert.ToHexString(final);
                            if (!plan.HasId(final) && !assigned.Contains(hex) && !sourceIds.Contains(hex)) break;
                        }
                    assigned.Add(Convert.ToHexString(final));
                    names.IdRenames.TryAdd(Convert.ToHexString(id.Bytes), final);
                    names.ElementIds[element] = final;
                    if (reference is not null) names.Ids.Add((final, reference));
                }
                return names;
            }

            /// <summary>The first of „name_2“, „name_3“, … not taken.</summary>
            private static string FreeName(string name, Func<string, bool> taken)
            {
                for (var n = 2; ; n++)
                    if ($"{name}_{n}" is var candidate && !taken(candidate)) return candidate;
            }

            /// <summary>An ID with „_n“ appended — in UTF-16BE where the ID is a text string marked so (ISO 32000-2, 7.9.2.2).</summary>
            private static byte[] WithSuffix(byte[] id, int n)
            {
                var suffix = "_" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var bytes = id is [0xFE, 0xFF, ..] ? System.Text.Encoding.BigEndianUnicode.GetBytes(suffix) : System.Text.Encoding.ASCII.GetBytes(suffix);
                return [.. id, .. bytes];
            }

            /// <summary>
            /// Whether two values (each in its own file) are the same: names, numbers, strings, arrays and dictionaries alike — classes of class
            /// maps, namespaces (6.5 g).
            /// </summary>
            internal static bool SameAttributes(PdfObject? a, Source sa, PdfObject? b, Source sb, int depth)
            {
                if (depth > 8) return false;
                a = a is PdfReference ra ? sa.Overrides.TryGetValue(ra.Id.Number, out var oa) ? oa : sa.Doc.Resolve(ra.Id) : a;
                b = b is PdfReference rb ? sb.Overrides.TryGetValue(rb.Id.Number, out var ob) ? ob : sb.Doc.Resolve(rb.Id) : b;
                switch (a, b)
                {
                    case (PdfName x, PdfName y):
                        return x == y;
                    case (PdfInteger x, PdfInteger y):
                        return x.Value == y.Value;
                    case (PdfReal x, PdfReal y):
                        return x.Value == y.Value;
                    case (PdfBoolean x, PdfBoolean y):
                        return x.Value == y.Value;
                    case (PdfString x, PdfString y):
                        return x.Bytes.AsSpan().SequenceEqual(y.Bytes);
                    case (PdfArray x, PdfArray y):
                        if (x.Count != y.Count || x.Count > 1000) return false;
                        for (var i = 0; i < x.Count; i++)
                            if (!SameAttributes(x[i], sa, y[i], sb, depth + 1)) return false;
                        return true;
                    case (PdfDictionary x, PdfDictionary y):
                        if (x.Count != y.Count || x.Count > 1000) return false;
                        foreach (var (key, value) in x.Entries)
                            if (!y.ContainsKey(key) || !SameAttributes(value, sa, y.GetRaw(key), sb, depth + 1)) return false;
                        return true;
                    default:
                        return false;
                }
            }
        }

        /// <summary>
        /// The elements of a merged source below <paramref name="top"/> (pruned), each once, as the copy writes them: the reference where it is
        /// an object of its own. At most 200 000 steps, 64 levels.
        /// </summary>
        private static IEnumerable<(PdfReference? Reference, PdfDictionary Element)> MergedElements(Source source, PdfObject top)
        {
            var stack = new Stack<(PdfObject Item, int Depth)>([(top, 0)]);
            var seen = new HashSet<int>();
            var steps = 0;
            while (stack.TryPop(out var next) && ++steps < 200_000)
            {
                if (next.Depth > 64) continue;
                switch (next.Item)
                {
                    case PdfArray array:
                        for (var i = Math.Min(array.Count, 100_000) - 1; i >= 0; i--) stack.Push((array[i], next.Depth + 1));
                        break;
                    case PdfReference reference when !source.Removed.Contains(reference.Id.Number) && seen.Add(reference.Id.Number):
                        if (Current(source, reference) is not { } element || !IsElement(element)) break;
                        yield return (reference, element);
                        if (element.GetRaw(StructKids) is { } kids) stack.Push((kids, next.Depth + 1));
                        break;
                    case PdfDictionary direct when IsElement(direct):
                        yield return (null, direct);
                        if (direct.GetRaw(StructKids) is { } directKids) stack.Push((directKids, next.Depth + 1));
                        break;
                }
            }
        }

        /// <summary>A structure element (not a marked-content or object reference).</summary>
        private static bool IsElement(PdfDictionary dict) => dict.ContainsKey(S) && dict.GetName(PdfName.Type)?.Value is not ("MCR" or "OBJR");

        /// <summary>
        /// The elements of a merged source with the names it has in the copy (<see cref="MergedNames"/>): /S, /C, /ID and the /Headers of their
        /// attributes; an element of its own changed becomes a copy in <see cref="Source.Overrides"/>, a direct one a copy in its parent's /K,
        /// an attribute object of its own a copy too. At most 200 000 steps, 64 levels.
        /// </summary>
        private sealed class MergeRenamer(Source source, MergedNames names, MergedNamespaces spaces)
        {
            private readonly HashSet<int> _seen = [];
            private int _budget = 200_000;

            /// <summary>The /K with the renamed elements: the same object if nothing in it changed (references stay, their objects are replaced).</summary>
            public PdfObject Kids(PdfObject k, int depth)
            {
                if (depth > 64 || --_budget < 0) return k;
                switch (k)
                {
                    case PdfArray array:
                    {
                        PdfArray? copy = null;
                        for (var i = 0; i < array.Count; i++)
                        {
                            var kid = Kids(array[i], depth + 1);
                            if (copy is null && !ReferenceEquals(kid, array[i])) copy = new PdfArray([.. array.Take(i)]);
                            copy?.Add(kid);
                        }
                        return copy ?? array;
                    }
                    case PdfReference reference when !source.Removed.Contains(reference.Id.Number) && _seen.Add(reference.Id.Number):
                        if (Current(source, reference) is { } element && IsElement(element) && Element(element, depth) is var renamed &&
                            !ReferenceEquals(renamed, element))
                            source.Overrides[reference.Id.Number] = renamed;
                        return k;
                    case PdfDictionary direct when IsElement(direct):
                        return Element(direct, depth);
                    default:
                        return k;
                }
            }

            private PdfDictionary Element(PdfDictionary element, int depth)
            {
                PdfDictionary? copy = null;
                PdfDictionary Copy() => copy ??= Clone(element);
                // (the role map is that of the default namespace: an element in a namespace keeps its name, 6.5 g)
                if (!element.ContainsKey(NsEntry) && element.GetName(S) is { } type && names.RoleRenames.TryGetValue(type.Value, out var role))
                    Copy().Set(S, PdfName.Get(role));
                if (WithNamespace(copy ?? element, spaces, source) is { } spaced) copy = spaced;
                if (names.ClassRenames.Count > 0 && Renamed(element.GetRaw(ClassKey)) is { } classes) Copy().Set(ClassKey, classes);
                if (names.ElementIds.TryGetValue(element, out var id) && !(element.Get(IdKey) is PdfString own && own.Bytes.AsSpan().SequenceEqual(id)))
                    Copy().Set(IdKey, new PdfString(id));
                if (names.IdRenames.Count > 0 && element.GetRaw(AttributesKey) is { } attributes && Attributes(attributes) is { } changed)
                    Copy().Set(AttributesKey, changed);
                if (element.GetRaw(StructKids) is { } kids && Kids(kids, depth + 1) is var newKids && !ReferenceEquals(newKids, kids))
                    Copy().Set(StructKids, newKids);
                return copy ?? element;
            }

            /// <summary>/C with the renamed classes (a name, or an array of names each perhaps followed by a revision number); null if unchanged.</summary>
            private PdfObject? Renamed(PdfObject? value)
            {
                PdfName? Class(PdfObject? item) => item is PdfName n && names.ClassRenames.TryGetValue(n.Value, out var r) ? PdfName.Get(r) : null;
                var resolved = value is PdfReference reference ? Current(source, reference) ?? source.Doc.Resolve(reference.Id) : value;
                switch (resolved)
                {
                    case PdfName:
                        return Class(resolved);
                    case PdfArray array when array.Count <= 10_000:
                        var copy = new PdfArray();
                        var changed = false;
                        for (var i = 0; i < array.Count; i++)
                        {
                            var renamed = Class(array.Get(i));
                            changed |= renamed is not null;
                            copy.Add(renamed ?? array[i]);
                        }
                        return changed ? copy : null;
                    default:
                        return null;
                }
            }

            /// <summary>/A with the /Headers following the renamed IDs (ISO 32000-2, 14.8.5.7): the new value for /A, null if it stays (an attribute object of its own changed is replaced).</summary>
            private PdfObject? Attributes(PdfObject value)
            {
                switch (value)
                {
                    case PdfReference reference:
                        var target = source.Overrides.TryGetValue(reference.Id.Number, out var replaced) ? replaced : source.Doc.Resolve(reference.Id);
                        if (target is PdfDictionary or PdfArray && Attributes(target) is { } changed) source.Overrides[reference.Id.Number] = changed;
                        return null;
                    case PdfDictionary attribute:
                        if (attribute.Get(HeadersKey) is not PdfArray headers || headers.Count > 10_000) return null;
                        var renamed = new PdfArray();
                        var any = false;
                        for (var i = 0; i < headers.Count; i++)
                        {
                            if (headers.Get(i) is PdfString header && names.IdRenames.TryGetValue(Convert.ToHexString(header.Bytes), out var id) &&
                                !header.Bytes.AsSpan().SequenceEqual(id))
                            {
                                renamed.Add(new PdfString(id));
                                any = true;
                            }
                            else renamed.Add(headers[i]);
                        }
                        if (!any) return null;
                        var copy = Clone(attribute);
                        copy.Set(HeadersKey, renamed);
                        return copy;
                    case PdfArray list when list.Count <= 10_000:
                        PdfArray? newList = null;
                        for (var i = 0; i < list.Count; i++)
                        {
                            var item = list[i] is PdfInteger ? null : Attributes(list[i]);
                            if (newList is null && item is not null) newList = new PdfArray([.. list.Take(i)]);
                            newList?.Add(item ?? list[i]);
                        }
                        return newList;
                    default:
                        return null;
                }
            }
        }

        /// <summary>The runs of pages of the copy (indices) in a row that come from one source.</summary>
        private List<(int First, int Last)> PageRuns(Source source)
        {
            var runs = new List<(int First, int Last)>();
            for (var i = 0; i < _pages.Count; i++)
            {
                if (!ReferenceEquals(_pages[i].Source, source.Doc)) continue;
                if (runs.Count > 0 && runs[^1].Last == i - 1) runs[^1] = (runs[^1].First, i);
                else runs.Add((i, i));
            }
            return runs;
        }

        /// <summary>A dictionary of a source as the copy writes it (its replacement where it has one).</summary>
        private static PdfDictionary? Current(Source source, PdfReference reference) =>
            (source.Overrides.TryGetValue(reference.Id.Number, out var replaced) ? replaced : source.Doc.Resolve(reference.Id)) as PdfDictionary;

        /// <summary>The page of the copy (index) an element of a source begins on, following its /Pg and its kids; null if none.</summary>
        private int? FirstPageOf(Source source, PdfObject item)
        {
            var stack = new Stack<PdfObject>([item]);
            var steps = 0;
            while (stack.TryPop(out var next) && ++steps < 5000)
            {
                var dict = next is PdfReference r ? Current(source, r) : next as PdfDictionary;
                if (dict is null) continue;
                if (dict.GetRaw(Pg) is PdfReference pg && source.PageOut.TryGetValue(pg.Id.Number, out var number) && PageIndexOf(number) is var page and >= 0)
                    return page;
                switch (dict.GetRaw(StructKids))
                {
                    case PdfArray kids:
                        for (var i = kids.Count - 1; i >= 0; i--) stack.Push(kids[i]);
                        break;
                    case PdfReference or PdfDictionary:
                        stack.Push(dict.GetRaw(StructKids)!);
                        break;
                }
            }
            return null;
        }

        /// <summary>
        /// An object of a merged source with its keys into the parent tree shifted into the source's range (/StructParent of an annotation or
        /// an XObject, /StructParents of a form) — or without them (<paramref name="offset"/> null): a copy; for a stream only the dictionary,
        /// the data as they are.
        /// </summary>
        private static PdfObject WithStructKeysShifted(PdfObject obj, long? offset)
        {
            var dict = obj.AsDictionary();
            if (dict is null || NumberTree.KeyOf(dict.Get(StructParentKey)) is null && NumberTree.KeyOf(dict.Get(StructParents)) is null) return obj;
            var copy = Clone(dict);
            foreach (var key in (PdfName[])[StructParentKey, StructParents])
                if (dict.Get(key) is PdfInteger value)
                {
                    if (offset is { } shift) copy.Set(key, PdfInteger.Of(value.Value + shift));
                    else copy.Remove(key);
                }
            return obj is PdfStream stream
                ? stream.RawData is { } data ? new PdfStream(copy, data) : new PdfStream(copy, stream.DataOffset, stream.DataLength, stream.Id)
                : copy;
        }

        private static readonly PdfName S = PdfName.Get("S"), RoleMapKey = PdfName.Get("RoleMap"), ClassKey = PdfName.Get("C"), IdKey = PdfName.Get("ID"),
            AttributesKey = PdfName.Get("A"), HeadersKey = PdfName.Get("Headers");
    }
}
