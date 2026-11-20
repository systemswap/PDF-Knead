using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        private StructurePlan? _structurePlan;
        private bool _structurePlanMade;

        /// <summary>
        /// The plan for changes to the structure tree of the main source (<see cref="StructurePlan"/>; docs/ABLAUFPLAN.md, 6.2 step 4): made the
        /// first time a step needs it, once the tree is pruned to the pages written and the pages have their numbers; null where the copy keeps
        /// no tree (untagged, cleaned up, no tagged content left) or its root is no object of its own. Steps that add content to a tagged copy
        /// (flattening, new annotations and fields, new text) give it its structure through the plan; <see cref="FinishStructureEdits"/> writes it.
        /// </summary>
        private StructurePlan? StructureEdits()
        {
            if (_structurePlanMade) return _structurePlan;
            if (_pageNumbers.Length == 0) return null;
            _structurePlanMade = true;
            if (!TaggedCopy() || _main.Catalog?.GetRaw(StructTreeRoot) is not PdfReference rootRef || _main.Resolve(rootRef.Id) is not PdfDictionary root)
                return null;
            return _structurePlan = StructurePlan.For(new StructureHost(this, rootRef, root));
        }

        /// <summary>Writes the changes of the plan (new elements, the parent tree built anew) before the objects are written.</summary>
        private void FinishStructureEdits()
        {
            _structurePlan?.Finish();
            _newTreePlan?.Finish();
        }

        private StructurePlan? _newTreePlan;
        private int _newTreeNumber;

        /// <summary>The number of the structure tree root made for the files merged into an untagged copy, once a tree of one came along.</summary>
        private int? _newTreeRoot;

        /// <summary>
        /// A new structure tree for the files merged into an untagged copy (docs/ABLAUFPLAN.md, 6.5 step 1 and 12; decision I: a tree
        /// is built once one of them is tagged): a new root, its Document made with the first part merged. Only for the merged trees — the pages
        /// of the main file stay untagged, the steps that add content to them do not tag it (<see cref="StructureEdits"/> stays null): the copy
        /// is tagged in part. Not for a copy whose structure is cleaned up, nor for PDF/A (level a tags the copy itself). Null if there is no
        /// such copy or no tagged file among those merged.
        /// </summary>
        private StructurePlan? NewStructureTree()
        {
            if (_newTreePlan is not null) return _newTreePlan;
            if (Tagged() || _pageNumbers.Length == 0 || _options.Cleanup.HasFlag(CleanupItems.Structure) || _pdfA is not null) return null;
            if (!_sourceOrder.Any(s => !s.IsMain && s.PageOut.Count > 0 && Accessibility.StructureRoles.IsTagged(s.Doc.Catalog)) &&
                _options.ElementStructure is not { Count: > 0 })
                return null;
            var number = _newTreeNumber = NewNumber();
            var root = new PdfDictionary();
            root.Set(PdfName.Type, PdfName.Get("StructTreeRoot"));
            return _newTreePlan = StructurePlan.For(new StructureHost(this, new OutputReference(number), root));
        }

        /// <summary>The writer as <see cref="StructurePlan"/> sees it: the main source with its replacements, the pages of the copy.</summary>
        private sealed class StructureHost(Session session, PdfObject rootReference, PdfDictionary sourceRoot) : IStructureHost
        {
            private readonly Source _main = session._sources[session._main];

            public PdfObject RootReference => rootReference;

            public PdfDictionary SourceRoot => sourceRoot;

            public bool Pdf20 => session.Pdf20;

            public int PageCount => session._pages.Count;

            public PdfObject? Resolve(PdfObject? value) =>
                value is PdfReference r ? _main.Overrides.TryGetValue(r.Id.Number, out var replaced) ? replaced : session._main.Resolve(r.Id) : value;

            public PdfDictionary Writable(PdfReference reference)
            {
                var number = reference.Id.Number;
                var original = session._main.Resolve(reference.Id);
                if (_main.Overrides.TryGetValue(number, out var replaced) && replaced is PdfDictionary own && !ReferenceEquals(own, original)) return own;
                var copy = Clone(replaced as PdfDictionary ?? original as PdfDictionary ?? new PdfDictionary());
                _main.Overrides[number] = copy;
                return copy;
            }

            public void Drop(PdfReference reference) => _main.Removed.Add(reference.Id.Number);

            public int NewNumber() => session.NewNumber();

            public PdfObject Rebase(PdfObject value) => session.Rebase(value, _main);

            public void Enqueue(int number, PdfObject obj) => session._memoryQueue.Enqueue((number, obj));

            public int PageNumber(int page) => session._pageNumbers[page];

            public int? PageIndexOf(PdfObject? pg) => pg switch
            {
                PdfReference r when _main.PageOut.TryGetValue(r.Id.Number, out var number) => session.PageIndexOf(number) is var i and >= 0 ? i : null,
                OutputReference o => session.PageIndexOf(o.Number) is var j and >= 0 ? j : null,
                _ => null,
            };

            public int? StructParents(int page)
            {
                // a further copy of a page with a structure of its own (No. 642)
                if (session._copyPageKeys.TryGetValue(session._pageNumbers[page], out var copy)) return (int)copy;
                var item = session._pages[page];
                // (a new tree: the main file's pages are untagged, a key they kept points nowhere)
                return rootReference is PdfReference && ReferenceEquals(item.Source, session._main) &&
                       NumberTree.KeyOf(item.Page.Dictionary.Get(Session.StructParents)) is long key and >= 0 and <= int.MaxValue
                    ? (int)key
                    : null;
            }

            public (byte[] Content, PdfDictionary? Properties) PageContent(int page)
            {
                var item = session._pages[page];
                using var content = PageInterpreter.PageContent(item.Source, item.Page.Dictionary);
                return (content.Span.ToArray(), item.Page.Resources?.GetDictionary(PdfName.Get("Properties")));
            }

            public object ContentKey(int page)
            {
                var item = session._pages[page];
                var contents = item.Page.Dictionary.GetRaw(PdfName.Contents);
                if (contents is PdfReference single) return (item.Source, single.Id.Number);
                if (contents is PdfArray { Count: > 0 } parts && parts[0] is PdfReference first) return (item.Source, first.Id.Number);
                return item.Page.Dictionary;
            }
        }
    }
}
