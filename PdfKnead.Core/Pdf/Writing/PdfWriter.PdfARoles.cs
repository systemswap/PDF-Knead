using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    /// <summary>
    /// One element of a structure tree as the role repair looks at it (docs/FEHLERGEDAECHTNIS.md, No. 507): its type, its parent's type
    /// („StructTreeRoot“ at the top), the types of its child elements, whether it holds content directly, and the types of its siblings.
    /// </summary>
    internal sealed record StructureOccurrence(string Type, string Parent, string[] Kids, bool HoldsContent, bool ParentHoldsContent, string[] Siblings);

    private sealed partial class Session
    {
        /// <summary>The elements of a structure tree with their surroundings (at most 500 000, as <see cref="StructureTypes"/>).</summary>
        internal static List<StructureOccurrence> StructureOccurrences(PdfDictionary root)
        {
            var result = new List<StructureOccurrence>();
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var budget = 500_000;
            var stack = new Stack<(PdfDictionary Element, string Parent, bool ParentHoldsContent, string[] Siblings)>();
            var (topKids, topContent) = Kids(root.Get(PdfName.Get("K")));
            var topTypes = Types(topKids);
            foreach (var kid in topKids) stack.Push((kid, "StructTreeRoot", topContent, topTypes));
            while (stack.TryPop(out var item) && --budget > 0)
            {
                if (!seen.Add(item.Element) || item.Element.GetName(PdfName.Get("S"))?.Value is not { } type) continue;
                var (kids, holds) = Kids(item.Element.Get(PdfName.Get("K")));
                var kidTypes = Types(kids);
                result.Add(new StructureOccurrence(type, item.Parent, kidTypes, holds, item.ParentHoldsContent, item.Siblings));
                foreach (var kid in kids) stack.Push((kid, type, holds, kidTypes));
            }
            return result;

            static string[] Types(List<PdfDictionary> elements) =>
                [.. elements.Select(e => e.GetName(PdfName.Get("S"))?.Value).OfType<string>()];
        }

        /// <summary>The child elements in /K and whether it holds content directly (a marked-content id, a marked-content or object reference).</summary>
        private static (List<PdfDictionary> Elements, bool Content) Kids(PdfObject? k)
        {
            var elements = new List<PdfDictionary>();
            var content = false;
            void One(PdfObject? o)
            {
                switch (o)
                {
                    case PdfInteger:
                        content = true;
                        break;
                    case PdfDictionary d when d.GetName(PdfName.Type)?.Value is "MCR" or "OBJR":
                        content = true;
                        break;
                    case PdfDictionary d when d.ContainsKey(PdfName.Get("S")):
                        elements.Add(d);
                        break;
                }
            }
            if (k is PdfArray array)
                for (var i = 0; i < array.Count && i < 100_000; i++) One(array.Get(i));
            else One(k);
            return (elements, content);
        }

        /// <summary>
        /// The standard types a type of its own may be mapped to, nearest first, by its name — row → TR, cell → TD, list item → LI, a heading →
        /// H (where only one fits, the containment rules leave P) …; empty where the name tells nothing. A type of the namespace for PDF 2.0
        /// first by PDF-Knead's own mapping (<see cref="StandardStructure.Pdf17Equivalent"/>; docs/ABLAUFPLAN.md, 6.5 f) — before, Title became
        /// H beside H1–H6, Aside or DocumentFragment fell to Div or NonStruct by chance.
        /// </summary>
        internal static List<string> NearestTypes(string type)
        {
            if (StandardStructure.Pdf17Equivalent(type) is { } equivalent) return [equivalent];
            var name = type.ToLowerInvariant();
            if (name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6') return ["H" + name[1]];
            bool Has(params string[] parts) => parts.Any(name.Contains);
            if (Has("tableofcontents", "inhaltsverzeichnis") || name is "toc") return ["TOC"];
            if (Has("tocitem", "tocentry", "toci")) return ["TOCI"];
            if (Has("row", "zeile")) return ["TR"];
            if (Has("cell", "zelle")) return ["TD", "TH"];
            if (Has("table", "tabelle") && Has("head", "kopf")) return ["TH", "THead", "TR"];
            if (Has("listitem", "listenpunkt") || name is "item") return ["LI"];
            if (Has("listbody", "lbody")) return ["LBody"];
            if (Has("label", "lbl")) return ["Lbl"];
            if (Has("figure", "image", "picture", "graphic", "bild")) return ["Figure"];
            if (Has("caption", "beschriftung")) return ["Caption"];
            if (Has("table", "tabelle")) return ["Table"];
            if (Has("head", "überschrift", "ueberschrift")) return ["H", "P"];
            if (Has("title", "titel")) return ["P"];
            if (Has("link")) return ["Link"];
            if (Has("note", "footnote", "fußnote")) return ["Note"];
            if (Has("list", "liste")) return ["L"];
            if (Has("body", "frame", "rahmen")) return ["Div"];
            if (Has("para", "text", "absatz") || name == "normal") return ["P"];
            if (Has("span", "char", "emph", "strong")) return ["Span"];
            if (Has("sect", "section", "abschnitt", "div", "story", "article")) return ["Sect", "Div"];
            return [];
        }

        /// <summary>
        /// The standard type for each of <paramref name="types"/> (own types that reach none through the role map): the first by its name
        /// that keeps every occurrence within the containment rules (ISO/TS 32005, Table 5) — as a child of its parent, as the parent of its
        /// children, holding content, not more often than allowed —; the one element at the top may be the document; else, where it holds
        /// content, P or Span; else Div; else NonStruct, which takes the rules of its parent (its children are checked against that). Types
        /// depend on each other (a table of own rows of own cells): decided in rounds (docs/FEHLERGEDAECHTNIS.md, No. 507).
        /// </summary>
        internal static Dictionary<string, string> ChooseRoles(IReadOnlyCollection<string> types, Func<string, string?> mapped, List<StructureOccurrence> occurrences,
            int part)
        {
            var byType = occurrences.GroupBy(o => o.Type).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            var numbered = byType.Keys.Any(k => (mapped(k) ?? StandardStructure.Pdf17Equivalent(k) ?? k) is ['H', >= '1' and <= '9', ..]);
            var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string> Candidates(string type)
            {
                var list = NearestTypes(type).Where(c => PdfACheck.StandardStructureType(c, part)).ToList();
                // a tree with numbered headings gets no H beside them (H and Hn do not mix, ISO 14289-1, 7.4; 6.5 f)
                if (numbered) list.Remove("H");
                byType.TryGetValue(type, out var all);
                // the one element at the top: the document
                if (all is [{ Parent: "StructTreeRoot", Siblings.Length: 1 }] && !list.Contains("Document")) list.Insert(0, "Document");
                var fallback = all is not null && all.Any(o => o.HoldsContent) ? new[] { "P", "Span", "Div" } : ["Div"];
                list.AddRange(fallback.Where(f => !list.Contains(f)));
                list.Add("NonStruct");
                return list;
            }
            foreach (var type in types) chosen[type] = Candidates(type)[0];
            string? Resolve(string t) => PdfACheck.StandardStructureType(t, part) ? t : chosen.TryGetValue(t, out var c) ? c : mapped(t);
            bool Fits(string type, string candidate)
            {
                if (!byType.TryGetValue(type, out var all)) return true;
                // siblings counted once per parent (its occurrences share one array), not once per child
                var counted = new Dictionary<string[], int>(ReferenceEqualityComparer.Instance);
                foreach (var o in all)
                {
                    // (an element „groups“ here where it holds no content directly — an approximation of the grouping use in ISO 32000-2,
                    // which concerns only Annot, Link, Form, Reference, notes, Figure, Caption, Code and Quote)
                    var parent = o.Parent == "StructTreeRoot" ? "StructTreeRoot" : Resolve(o.Parent);
                    var parentKnown = parent is not null && parent != "NonStruct" && Containment.Known(parent);
                    // NonStruct is transparent: its children answer to its parent
                    var holder = candidate == "NonStruct" ? parentKnown ? parent! : null : candidate;
                    if (candidate != "NonStruct")
                    {
                        if (o.HoldsContent && !Containment.MayHoldContent(candidate)) return false;
                        if (parentKnown)
                        {
                            if (!Containment.Allowed(parent!, candidate, !o.ParentHoldsContent)) return false;
                            if (Containment.Most(parent!, candidate, !o.ParentHoldsContent) is { } most)
                            {
                                if (!counted.TryGetValue(o.Siblings, out var same))
                                    counted[o.Siblings] = same = o.Siblings.Count(s => s == type || Resolve(s) == candidate);
                                if (same > most) return false;
                            }
                        }
                    }
                    if (holder is null) continue;
                    foreach (var group in o.Kids.Select(k => Resolve(k)).OfType<string>().Where(k => k != "NonStruct" && Containment.Known(k)).GroupBy(k => k))
                    {
                        if (!Containment.Allowed(holder, group.Key, !o.HoldsContent)) return false;
                        if (Containment.Most(holder, group.Key, !o.HoldsContent) is { } most && group.Count() > most) return false;
                    }
                }
                return true;
            }
            for (var round = 0; round < 4; round++)
            {
                var changed = false;
                foreach (var type in types)
                {
                    var pick = Candidates(type).FirstOrDefault(c => Fits(type, c)) ?? "NonStruct";
                    if (pick == chosen[type]) continue;
                    chosen[type] = pick;
                    changed = true;
                }
                if (!changed) break;
            }
            return chosen;
        }
    }
}
