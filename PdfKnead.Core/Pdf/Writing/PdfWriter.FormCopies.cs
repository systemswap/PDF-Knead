using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        private static readonly PdfName StmKey = PdfName.Get("Stm");

        /// <summary>Further copies of a form with keys of its own (<see cref="PlanFormCopies"/>): written without the keys, their marked content as artifacts.</summary>
        private readonly HashSet<PdfStream> _artifactCopies = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Forms rebuilt for one invocation (<see cref="PdfStream.CopyOf"/>: redacting, removing hidden content, editing text or images) whose
        /// original has keys into the parent tree (/StructParents; docs/ABLAUFPLAN.md, 6.6 a; docs/FEHLERGEDAECHTNIS.md, No. 134, 647): the first
        /// copy of each takes the original's place — a marked-content reference naming the original (/Stm) names it
        /// (<see cref="WithFormCopies"/>); before, the original was not written and /Stm became null, which means „in the page's content“
        /// (ISO 32000-2, 14.7.5.2), so readers gave the element foreign content of the page with the same MCID. Each further copy (the form
        /// invoked again, or on another page) loses /StructParents and its marked content becomes artifact content — one key names one stream
        /// (14.7.5.4), and the content is drawn by the first copy already (recommendation of the plan). Pages in their order.
        /// </summary>
        private void PlanFormCopies()
        {
            var seen = new HashSet<PdfStream>(ReferenceEqualityComparer.Instance);
            foreach (var item in _pages)
            {
                var source = _sources[item.Source];
                var stack = new Stack<(PdfDictionary? Resources, int Depth)>([(item.Page.Resources, 0)]);
                while (stack.TryPop(out var next))
                {
                    if (next.Depth > 8 || next.Resources is not { } resources) continue;
                    // the content streams the resources name: forms, tiling patterns, the groups of soft masks (the rewriter copies all three)
                    var streams = new List<PdfStream>();
                    if (resources.GetDictionary(PdfName.Get("XObject")) is { } xobjects)
                        foreach (var (name, _) in xobjects.Entries.Take(10_000))
                            if (xobjects.Get(name) is PdfStream form && form.Dictionary.GetName(PdfName.Subtype)?.Value == "Form") streams.Add(form);
                    if (resources.GetDictionary(PdfName.Get("Pattern")) is { } patterns)
                        foreach (var (name, _) in patterns.Entries.Take(10_000))
                            if (patterns.Get(name) is PdfStream pattern) streams.Add(pattern);
                    if (resources.GetDictionary(PdfName.Get("ExtGState")) is { } states)
                        foreach (var (name, _) in states.Entries.Take(10_000))
                            if (states.GetDictionary(name)?.GetDictionary(PdfName.Get("SMask"))?.Get(PdfName.Get("G")) is PdfStream group) streams.Add(group);
                    foreach (var stream in streams)
                    {
                        if (!seen.Add(stream)) continue;
                        if (stream.CopyOf is { } original && stream.Dictionary.ContainsKey(StructParents) && !source.FormCopies.TryAdd(original, stream))
                            _artifactCopies.Add(stream);
                        // a form characters left: its elements lose their replacement texts — not one only rewritten for an edited image
                        // (6.6 a part 2, No. 648; before every rewritten form counted, and a figure in a form lost its /Alt)
                        if (stream.CopyOf is { } edited && stream.LostText) source.EditedForms.Add(edited);
                        stack.Push((stream.Dictionary.GetDictionary(PdfName.Resources), next.Depth + 1));
                    }
                }
            }
        }

        /// <summary>
        /// An object of a source with the marked-content references (MCR, itself or in the /K of an element) to a rewritten form pointed at
        /// its first copy (<see cref="PlanFormCopies"/>); the object itself if none.
        /// </summary>
        private PdfObject WithFormCopies(Source source, PdfObject obj)
        {
            if (source.FormCopies.Count == 0 || obj is not PdfDictionary dict) return obj;
            PdfDictionary? Fixed(PdfDictionary item)
            {
                if (item.GetName(PdfName.Type)?.Value != "MCR" || item.GetRaw(StmKey) is not PdfReference stm ||
                    !source.FormCopies.TryGetValue(stm.Id.Number, out var copy))
                    return null;
                var changed = Clone(item);
                changed.Set(StmKey, Sourced(copy, source));
                return changed;
            }
            if (Fixed(dict) is { } reference) return reference;
            if (!dict.ContainsKey(S)) return dict;
            switch (dict.GetRaw(StructKids))
            {
                case PdfDictionary single when Fixed(single) is { } fixedSingle:
                {
                    var copy = Clone(dict);
                    copy.Set(StructKids, fixedSingle);
                    return copy;
                }
                case PdfArray kids:
                {
                    var items = new List<PdfObject>(kids.Count);
                    var changed = false;
                    for (var i = 0; i < kids.Count; i++)
                    {
                        var fixedItem = i < 100_000 && kids[i] is PdfDictionary item ? Fixed(item) : null;
                        changed |= fixedItem is not null;
                        items.Add(fixedItem ?? kids[i]);
                    }
                    if (!changed) return dict;
                    var copy = Clone(dict);
                    copy.Set(StructKids, new PdfArray(items));
                    return copy;
                }
                default:
                    return dict;
            }
        }

        /// <summary>
        /// A further copy of a form with keys of its own as it is written (<see cref="PlanFormCopies"/>): without /StructParents, its marked
        /// content with an MCID as artifact content (<see cref="ContentRewrite.ArtifactMcids"/>). Data with a filter stay as they are.
        /// </summary>
        private static (PdfDictionary Dict, byte[] Data) AsArtifactCopy(PdfDictionary dict, byte[] data, PdfDictionary? resources)
        {
            dict.Remove(StructParents);
            if (dict.ContainsKey(PdfName.Filter)) return (dict, data);
            var mcids = new HashSet<int>();
            var parser = new ContentParser(data);
            PdfInteger? Named(PdfName name) => resources?.GetDictionary(PdfName.Get("Properties"))?.GetDictionary(name)?.Get(PdfName.Get("MCID")) as PdfInteger;
            string? op;
            while ((op = parser.Next()) is not null)
                if (op == "BDC" && parser.Operands.Count == 2 &&
                    (parser.Operands[1].Object is PdfDictionary list ? list.Get(PdfName.Get("MCID")) as PdfInteger
                        : parser.Operands[1].Name is { } name ? Named(name) : null) is { Value: >= 0 and <= int.MaxValue } mcid)
                    mcids.Add((int)mcid.Value);
            return mcids.Count == 0 ? (dict, data) : (dict, Accessibility.MarkedContentIds.AsArtifacts(data, mcids, resources?.GetDictionary(PdfName.Get("Properties"))));
        }
    }
}
