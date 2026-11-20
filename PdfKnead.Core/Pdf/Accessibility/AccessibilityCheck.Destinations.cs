using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Accessibility;

public static partial class AccessibilityCheck
{
    private sealed partial class Run
    {
        private static readonly PdfName DestKey = PdfName.Get("Dest"), DKey = PdfName.Get("D"), SdKey = PdfName.Get("SD"), NextKey = PdfName.Get("Next");

        /// <summary>
        /// PDF/UA-2, 8.8 (docs/ABLAUFPLAN.md, 6.7; docs/FEHLERGEDAECHTNIS.md, No. 668): destinations within the document are structure
        /// destinations — a destination (/Dest of links and outline items, an array as /OpenAction; named ones resolved) begins with a structure
        /// element (ISO 32000-2, 12.3.2.3), a GoTo action (of annotations, outline items, /OpenAction, and the actions chained by /Next) has /SD.
        /// </summary>
        public void Destinations()
        {
            if (part != 2 || Catalog is null) return;
            var named = NamedDestinations();
            var actions = new List<PdfDictionary>();
            void Destination(PdfObject? value, int? page)
            {
                if (value is null or PdfNull) return;
                var resolved = value is PdfString or PdfName ? named(value) : value;
                if (resolved is PdfDictionary withD) resolved = withD.Get(DKey);
                // a page number (a destination in another file) or nothing usable: not a destination within the document
                if (resolved is PdfArray { Count: > 0 } array && array.Get(0) is not PdfDictionary) return;
                if (resolved is PdfArray { Count: > 0 } target && !IsStructureElement(target.Get(0) as PdfDictionary))
                    Add(AccessibilityRules.StructureDestination, Tr.T("Ein Sprungziel in der Datei zeigt auf eine Seite statt auf ein Strukturelement."), page);
            }
            void Action(PdfObject? value)
            {
                if (value is PdfDictionary action) actions.Add(action);
                else if (value is PdfArray list)
                    for (var i = 0; i < list.Count && i < 100; i++)
                        if (list.Get(i) is PdfDictionary chained) actions.Add(chained);
            }

            var open = Catalog.Get(PdfName.Get("OpenAction"));
            if (open is PdfArray) Destination(open, null);
            else Action(open);
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<PdfDictionary>();
            if (Catalog.GetDictionary(PdfName.Get("Outlines"))?.Get(PdfName.Get("First")) is PdfDictionary outline) stack.Push(outline);
            while (stack.Count > 0 && seen.Count < 100_000)
            {
                var item = stack.Pop();
                if (!seen.Add(item)) continue;
                Destination(item.Get(DestKey), null);
                Action(item.Get(PdfName.Get("A")));
                if (item.Get(NextKey) is PdfDictionary next) stack.Push(next);
                if (item.Get(PdfName.Get("First")) is PdfDictionary first) stack.Push(first);
            }
            foreach (var page in doc.Pages)
                if (page.Dictionary.Get(PdfName.Get("Annots")) is PdfArray annots)
                    for (var i = 0; i < annots.Count && i < 10_000; i++)
                        if (annots.Get(i) is PdfDictionary annot)
                        {
                            Destination(annot.Get(DestKey), page.Index + 1);
                            Action(annot.Get(PdfName.Get("A")));
                        }

            var done = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < actions.Count && done.Count < 100_000; i++)
            {
                var action = actions[i];
                if (!done.Add(action)) continue;
                if (action.GetName(PdfName.Get("S"))?.Value == "GoTo" && action.Get(SdKey) is null or PdfNull)
                    Add(AccessibilityRules.GoToStructureDestination, Tr.T("Eine Sprung-Aktion (GoTo) in der Datei nennt kein Strukturziel (/SD)."));
                Action(action.Get(NextKey));
            }
        }

        /// <summary>A structure element (/Type /StructElem or an entry /S naming its type).</summary>
        private static bool IsStructureElement(PdfDictionary? element) =>
            element is not null && (element.GetName(PdfName.Type)?.Value == "StructElem" || element.Get(SKey) is PdfName && element.ContainsKey(PKey));

        /// <summary>The named destinations: the catalog's /Dests (PDF 1.1) and the name tree /Names /Dests; a lookup by name or string.</summary>
        private Func<PdfObject?, PdfObject?> NamedDestinations()
        {
            var byString = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
            if (Catalog?.GetDictionary(PdfName.Get("Names"))?.GetDictionary(PdfName.Get("Dests")) is { } tree)
                foreach (var (key, value) in NameTree.Pairs(tree, budget: 200_000))
                    byString.TryAdd(Convert.ToHexString(key), value is PdfReference r ? doc.Resolve(r.Id) ?? value : value);
            var old = Catalog?.GetDictionary(PdfName.Get("Dests"));
            return name => name switch
            {
                PdfName n => old?.Get(n),
                PdfString s => byString.GetValueOrDefault(Convert.ToHexString(s.Bytes)),
                _ => null,
            };
        }
    }
}
