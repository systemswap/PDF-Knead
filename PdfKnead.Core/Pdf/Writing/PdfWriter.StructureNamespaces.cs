using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        private static readonly PdfName NamespacesKey = PdfName.Get("Namespaces"), NsEntry = PdfName.Get("NS");

        /// <summary>The namespaces the copy has so far (handle as written, dictionary, its file): the main file's, then those merged (6.5 g).</summary>
        private List<(PdfObject Handle, PdfDictionary Space, Source From)>? _copyNamespaces;

        /// <summary>
        /// The namespaces of a merged file as the copy takes them (docs/ABLAUFPLAN.md, 6.5 g, step 10; ISO 32000-2, 14.7.4; ISO/TS 32005):
        /// written as PDF 2.0, each namespace of the file is one of the copy's where it is the same — the same entries, its role map too; never
        /// by its address alone —, else it is added to /Namespaces of the copy; the elements of a file without namespaces whose type both
        /// standard namespaces define get the copy's namespace for PDF 2.0 (types common to both belong to the 2.0 namespace in a mixed file).
        /// Written as 1.7 or older, a file with namespaces is written as PDF 1.7 can name it: each element in a namespace gets the 1.7 type its
        /// namespace leads to (<see cref="StandardStructure.Pdf17Equivalent"/>), its /NS goes.
        /// </summary>
        private sealed class MergedNamespaces
        {
            /// <summary>A namespace of the file (object number) that is one of the copy's: the copy's handle for it.</summary>
            public readonly Dictionary<int, PdfObject> Same = [];

            /// <summary>The namespaces of the file the copy gets (their handles as written).</summary>
            public readonly List<PdfObject> Added = [];

            /// <summary>The copy's namespace for PDF 2.0 for elements of common types of a file without namespaces; null: none.</summary>
            public PdfObject? Pdf20;

            /// <summary>Written below PDF 2.0: the elements in namespaces become types of PDF 1.7, their /NS goes.</summary>
            public bool To17;

            /// <summary>The role map of the file (default namespace): a type it maps keeps its meaning by the map, not by the 2.0 namespace.</summary>
            public PdfDictionary? Roles;

            /// <summary>The file's tree root, for resolving the types of its elements in namespaces.</summary>
            public PdfDictionary Root = null!;
        }

        /// <summary>
        /// The namespaces of a merged file (see <see cref="MergedNamespaces"/>); null if its tree cannot come along this way — namespaces where
        /// the copy is below 2.0 and an element PDF 1.7 cannot name (an Artifact element, MathML, a type no namespace leads to a standard
        /// one), a namespace that is no object of its own, or the copy itself with namespaces below 2.0.
        /// </summary>
        private MergedNamespaces? NamespacesOf(Source source, PdfDictionary root, StructurePlan plan, PdfObject top)
        {
            var main = _sources[_main];
            if (_copyNamespaces is null)
            {
                _copyNamespaces = [];
                foreach (var entry in plan.NamespaceEntries())
                    if (entry is PdfReference reference && Current(main, reference) is { } space) _copyNamespaces.Add((plan.Written(entry), space, main));
            }
            var spaces = new MergedNamespaces { Roles = root.GetDictionary(RoleMapKey), Root = root, To17 = !Pdf20 };
            var own = root.Get(NamespacesKey) as PdfArray;
            if (!Pdf20 && _copyNamespaces.Count > 0) return null;   // the copy itself with namespaces below 2.0: as before
            if (spaces.To17)
            {
                if (own is null) return spaces;
                PdfObject? Resolve(PdfObject? value) => value is PdfReference r ? (PdfObject?)Current(source, r) ?? source.Doc.Resolve(r.Id) : value;
                foreach (var (_, element) in MergedElements(source, top))
                    if (element.Get(NsEntry) is PdfDictionary && Pdf17TypeOf(element, root, Resolve) is null) return null;
                return spaces;
            }
            if (own is not null)
                for (var i = 0; i < Math.Min(own.Count, 1000); i++)
                {
                    if (own[i] is not PdfReference reference || Current(source, reference) is not { } space) return null;
                    var same = _copyNamespaces.FirstOrDefault(c => MergedNames.SameAttributes(c.Space, c.From, space, source, 0));
                    if (same.Space is not null) spaces.Same[reference.Id.Number] = same.Handle;
                    else
                    {
                        var handle = Rebase(reference, source);
                        spaces.Added.Add(handle);
                        _copyNamespaces.Add((handle, space, source));
                    }
                }
            else if (plan.Pdf20Types)
                spaces.Pdf20 = _copyNamespaces.FirstOrDefault(c => StandardStructure.SpaceOf(c.Space) == StructureSpace.Pdf20).Handle;
            return spaces;
        }

        /// <summary>The type of PDF 1.7 an element in a namespace is written as (its namespace's standard type, mapped); null: none (Artifact, MathML, none reached).</summary>
        private static string? Pdf17TypeOf(PdfDictionary element, PdfDictionary root, Func<PdfObject?, PdfObject?> resolve) =>
            StandardStructure.Resolve(element, root, resolve) switch
            {
                { Space: StructureSpace.Pdf17 } t => t.Name,
                { Space: StructureSpace.Pdf20 } t => StandardStructure.Pdf17Equivalent(t.Name),
                _ => null,
            };

        /// <summary>
        /// An element of a merged file with the namespace it has in the copy (<see cref="MergedNamespaces"/>): the copy's handle for a namespace
        /// that is the same; the 2.0 namespace for a common type of a file without namespaces (where its role map does not map it); below 2.0
        /// its 1.7 type without /NS. Null if nothing changes.
        /// </summary>
        private static PdfDictionary? WithNamespace(PdfDictionary element, MergedNamespaces spaces, Source source)
        {
            var ns = element.GetRaw(NsEntry);
            if (spaces.To17)
            {
                if (ns is null) return null;
                PdfObject? Resolve(PdfObject? value) => value is PdfReference r ? (PdfObject?)Current(source, r) ?? source.Doc.Resolve(r.Id) : value;
                if (Pdf17TypeOf(element, spaces.Root, Resolve) is not { } type) return null;
                var copy = Clone(element);
                copy.Set(S, PdfName.Get(type));
                copy.Remove(NsEntry);
                return copy;
            }
            if (ns is PdfReference reference && spaces.Same.TryGetValue(reference.Id.Number, out var handle))
            {
                var copy = Clone(element);
                copy.Set(NsEntry, handle);
                return copy;
            }
            if (ns is null && spaces.Pdf20 is { } pdf20 && element.GetName(S)?.Value is { } name && StandardStructure.IsStandard(name, StructureSpace.Pdf20) &&
                StandardStructure.IsStandard(name, StructureSpace.Pdf17) && spaces.Roles?.ContainsKey(PdfName.Get(name)) != true)
            {
                var copy = Clone(element);
                copy.Set(NsEntry, pdf20);
                return copy;
            }
            return null;
        }
    }
}
