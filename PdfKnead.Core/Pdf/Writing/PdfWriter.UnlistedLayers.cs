using PdfKnead.Core.Localization;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>
        /// Per source: the layers outside /OCProperties /OCGs (or listed without /Type /OCG) that the copy enters as layers that are on
        /// (<see cref="PlanUnlistedLayers"/>), by object number in the source.
        /// </summary>
        private readonly Dictionary<Source, List<int>> _unlistedLayers = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Layers outside /OCGs (ISO 32000-2, 8.11.3.2 sentence 6): content marked with a layer that is not listed in /OCProperties /OCGs, or
        /// listed without /Type /OCG, is not optional — PDF-Knead shows it (<see cref="Content.OptionalContent"/>), and so does every reader of
        /// 2.0; a reader of 1.x may count such a layer and hide it under /BaseState /OFF or an /OFF list. The copy enters such a layer in /OCGs,
        /// with /Type /OCG (and a /Name if it has none, 8.11.2.1), on in the default configuration (in /ON under /BaseState /OFF, out of /OFF
        /// and of the application dictionaries of /AS), so that every reader shows it as before — as 2.0 and below, since entered and on it
        /// is visible under the rules of both (docs/FEHLERGEDAECHTNIS.md, No. 1132; before, saving as 2.0 left it outside, and
        /// the sanitising took along content no reader of 2.0 hides). Found where the pages written name layers: /Properties of the page and
        /// of the XObjects it draws (a /Properties entry only with /Type /OCG — without it, it may be the property list of other marked
        /// content), /OC of XObjects and annotations. A layer that a membership dictionary there names is left as it is: entered and on, it
        /// would count for the membership and change what it shows.
        /// </summary>
        private void PlanUnlistedLayers()
        {
            if (FlattensLayers) return;
            foreach (var source in _sourceOrder)
            {
                if (!source.IsMain && source.Items.Count == 0) continue;
                if (source.Doc.Catalog?.GetDictionary(OCProperties) is not { } properties || properties.GetDictionary(D) is null) continue;
                var listed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                var candidates = new Dictionary<int, PdfDictionary>();
                var inMemberships = new HashSet<int>();
                if (properties.GetArray(Ocgs) is { } groups)
                    for (var i = 0; i < groups.Count && i < Content.OptionalContent.MaxListed; i++)
                    {
                        if (groups.Get(i) is not PdfDictionary group) continue;
                        listed.Add(group);
                        // listed without /Type: not a layer that counts (8.11.2.1); one with another /Type (a membership) stays as it is
                        if (groups[i] is PdfReference reference && group.GetRaw(PdfName.Type) is null) candidates.TryAdd(reference.Id.Number, group);
                    }
                var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                var budget = 100_000;
                void Membership(PdfDictionary ocmd)
                {
                    if (!seen.Add(ocmd)) return;
                    if (ocmd.GetRaw(Ocgs) is PdfReference single) inMemberships.Add(single.Id.Number);
                    else if (ocmd.Get(Ocgs) is PdfArray members)
                        for (var i = 0; i < members.Count && i < 10_000; i++)
                            if (members[i] is PdfReference member) inMemberships.Add(member.Id.Number);
                    void Walk(PdfObject? raw, int depth)
                    {
                        if (depth > 32 || --budget < 0) return;
                        if (raw is PdfReference operand && source.Doc.Resolve(operand.Id) is PdfDictionary) inMemberships.Add(operand.Id.Number);
                        else if ((raw is PdfReference r ? source.Doc.Resolve(r.Id) : raw) is PdfArray expression)
                            for (var i = 1; i < expression.Count; i++) Walk(expression[i], depth + 1);
                    }
                    Walk(ocmd.GetRaw(PdfName.Get("VE")), 0);
                }
                void Marked(PdfObject? raw, bool property)
                {
                    if (--budget < 0) return;
                    var resolved = raw is PdfReference r ? source.Doc.Resolve(r.Id) : raw;
                    if (resolved is not PdfDictionary dict) return;
                    var type = dict.GetName(PdfName.Type)?.Value;
                    if (type == "OCMD") Membership(dict);
                    else if (raw is PdfReference reference && !listed.Contains(dict) && (type == "OCG" || type is null && !property))
                        candidates.TryAdd(reference.Id.Number, dict);
                }
                void InProperties(PdfDictionary? resources)
                {
                    if (resources?.GetDictionary(PdfName.Get("Properties")) is not { } named) return;
                    foreach (var (key, _) in named.Entries) Marked(named.GetRaw(key), property: true);
                }
                foreach (var item in source.Items)
                {
                    if (budget < 0) break;
                    var page = item.Page;
                    InProperties(page.Resources);
                    foreach (var x in DocumentCleanup.EmbeddedForms(page.Resources))
                    {
                        Marked(x.Dictionary.GetRaw(PdfName.Get("OC")), property: false);
                        InProperties(x.Dictionary.GetDictionary(PdfName.Resources));
                    }
                    if (page.Dictionary.Get(Annots) is PdfArray annots)
                        for (var i = 0; i < annots.Count && i < 10_000; i++)
                            if (annots.Get(i) is PdfDictionary annot) Marked(annot.GetRaw(PdfName.Get("OC")), property: false);
                }
                var entered = new List<int>();
                foreach (var (number, layer) in candidates)
                {
                    if (inMemberships.Contains(number) || source.Removed.Contains(number)) continue;
                    entered.Add(number);
                    var current = source.Overrides.TryGetValue(number, out var over) && over is PdfDictionary own ? own : layer;
                    if (current.GetName(PdfName.Type)?.Value == "OCG" && current.GetString(PdfName.Name) is not null) continue;
                    var copy = Clone(current);
                    copy.Set(PdfName.Type, PdfName.Get("OCG"));
                    if (copy.GetString(PdfName.Name) is null) copy.Set(PdfName.Name, new PdfString(PdfTextEncoding.Encode(Tr.T("Ebene"))));
                    source.Overrides[number] = copy;
                }
                if (entered.Count > 0) _unlistedLayers[source] = entered;
            }
        }

        /// <summary>
        /// The layers of the main file outside /OCGs into the copy's /OCProperties (<see cref="PlanUnlistedLayers"/>): appended to /OCGs, on in
        /// /D — in /ON under /BaseState /OFF, out of /OFF and of the /OCGs of the application dictionaries of /AS.
        /// </summary>
        /// <param name="source">The main file.</param>
        /// <param name="result">The copy's /OCProperties as built so far (its /OCGs and /D copies of their own).</param>
        /// <param name="sourceConfig">The default configuration as read.</param>
        private void EnterUnlistedLayers(Source source, PdfDictionary result, PdfDictionary? sourceConfig)
        {
            if (!_unlistedLayers.TryGetValue(source, out var entered)) return;
            var numbers = entered.ToHashSet();
            if (result.Get(Ocgs) is not PdfArray groups) result.Set(Ocgs, groups = new PdfArray());
            var present = new HashSet<int>();
            for (var i = 0; i < groups.Count; i++)
                if (groups[i] is OutputReference output) present.Add(output.Number);
            var mapped = new List<int>();
            foreach (var number in entered)
                if (Map(source, number) is > 0 and var newNumber)
                {
                    mapped.Add(newNumber);
                    if (present.Add(newNumber)) groups.Add(new OutputReference(newNumber));
                }
            if (sourceConfig is null || result.Get(D) is not PdfDictionary config) return;
            bool Entered(PdfObject? raw) => raw is PdfReference r && numbers.Contains(r.Id.Number);
            PdfArray Without(PdfArray list)
            {
                var kept = new PdfArray();
                for (var i = 0; i < list.Count; i++)
                    if (!Entered(list[i])) kept.Add(Rebase(list[i], source));
                return kept;
            }
            if (sourceConfig.GetArray(Off) is { } off) config.Set(Off, Without(off));
            if (sourceConfig.GetName(PdfName.Get("BaseState"))?.Value == "OFF")
            {
                var on = sourceConfig.GetArray(PdfName.Get("ON")) is { } before ? Without(before) : new PdfArray();
                foreach (var number in mapped) on.Add(new OutputReference(number));
                config.Set(PdfName.Get("ON"), on);
            }
            if (sourceConfig.GetArray(PdfName.Get("AS")) is { } usages)
            {
                var applications = new PdfArray();
                for (var i = 0; i < usages.Count && i < 1000; i++)
                {
                    if (usages.Get(i) is not PdfDictionary usage)
                    {
                        applications.Add(Rebase(usages[i], source));
                        continue;
                    }
                    var copy = (PdfDictionary)Rebase(usage, source);
                    if (Entered(usage.GetRaw(Ocgs))) copy.Remove(Ocgs);
                    else if (usage.Get(Ocgs) is PdfArray members) copy.Set(Ocgs, Without(members));
                    applications.Add(copy);
                }
                config.Set(PdfName.Get("AS"), applications);
            }
        }

        /// <summary>Whether a layer of an inserted file is one the copy enters on (<see cref="PlanUnlistedLayers"/>).</summary>
        private bool EntersUnlisted(Source source, PdfObject raw) =>
            raw is PdfReference r && _unlistedLayers.TryGetValue(source, out var entered) && entered.Contains(r.Id.Number);

        /// <summary>
        /// The layers of an inserted file outside its /OCGs into the merged /OCProperties: appended to /OCGs (where not listed already), in /ON
        /// of the merged configuration when it starts from OFF.
        /// </summary>
        private void EnterUnlistedLayers(Source source, PdfArray ocgs, PdfDictionary config)
        {
            if (!_unlistedLayers.TryGetValue(source, out var entered)) return;
            var present = new HashSet<int>();
            for (var i = 0; i < ocgs.Count; i++)
                if (ocgs[i] is OutputReference output) present.Add(output.Number);
            var baseOff = config.GetName(PdfName.Get("BaseState"))?.Value == "OFF";
            foreach (var number in entered)
            {
                if (Map(source, number) is not (> 0 and var newNumber)) continue;
                if (present.Add(newNumber)) ocgs.Add(new OutputReference(newNumber));
                if (!baseOff) continue;
                if (config.Get(PdfName.Get("ON")) is not PdfArray on) config.Set(PdfName.Get("ON"), on = new PdfArray());
                on.Add(new OutputReference(newNumber));
            }
        }
    }
}
