using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Core.Pdf.Accessibility;

public static partial class AccessibilityCheck
{
    private sealed partial class Run
    {
        /// <summary>
        /// Stage B, fonts (ISO 14289-1, 7.21; -2, 8.4.5): PDF/UA asks of the fonts what PDF/A-2 level u asks (ISO 19005-2, 6.2.11) — the check
        /// of <see cref="PdfACheck"/> runs once with that level, and its findings on fonts are told under the numbers of PDF/UA (the building
        /// block is shared, not copied; docs/ABLAUFPLAN.md, 6.7 risks).
        /// </summary>
        public void Fonts()
        {
            IReadOnlyList<PdfAViolation> violations;
            try
            {
                violations = PdfACheck.Check(doc, PdfAConformance.A2u, cancel);
            }
            catch (PdfException)
            {
                return;
            }
            foreach (var violation in violations)
                if (AccessibilityRules.Fonts.TryGetValue(violation.Id, out var rule))
                    Add(rule, violation.Text, violation.Page);
        }

        /// <summary>
        /// Stage B, PDF/UA-2 only: /ActualText and /Alt of the structure elements (8.4.3) and the text strings meant to be read (8.6) without
        /// characters of the private use areas — they say nothing without the font. Text strings looked at: the outline titles, the /Contents,
        /// /TU and /T of annotations and fields, /E and /T of structure elements, the catalogue's /Lang, the entries of the information dictionary.
        /// </summary>
        public void PrivateUse()
        {
            if (part != 2) return;
            foreach (var element in Elements())
            {
                if (Text(element.Get(PdfName.Get("ActualText"))) is { } actual && PdfACheck.HasPrivateUse(actual))
                    Add(AccessibilityRules.ActualTextNoPua, Tr.T("Ein Ersatztext (/ActualText) enthält Zeichen aus dem Bereich für private Nutzung."));
                if (Text(element.Get(PdfName.Get("Alt"))) is { } alt && PdfACheck.HasPrivateUse(alt))
                    Add(AccessibilityRules.AltNoPua, Tr.T("Ein Alternativtext (/Alt) enthält Zeichen aus dem Bereich für private Nutzung."));
                foreach (var key in (ReadOnlySpan<string>)["E", "T"])
                    if (Text(element.Get(PdfName.Get(key))) is { } text && PdfACheck.HasPrivateUse(text)) PrivateUseString();
            }
            if (Text(Catalog?.Get(PdfName.Get("Lang"))) is { } lang && PdfACheck.HasPrivateUse(lang)) PrivateUseString();
            if (doc.Trailer.Get(PdfName.Info) is PdfDictionary info)
                foreach (var (_, value) in info.Entries)
                    if (Text(value) is { } text && PdfACheck.HasPrivateUse(text)) PrivateUseString();
            var outline = Catalog?.GetDictionary(PdfName.Get("Outlines"))?.Get(PdfName.Get("First")) as PdfDictionary;
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<PdfDictionary>();
            if (outline is not null) stack.Push(outline);
            while (stack.Count > 0 && seen.Count < 100_000)
            {
                var item = stack.Pop();
                if (!seen.Add(item)) continue;
                if (Text(item.Get(PdfName.Get("Title"))) is { } title && PdfACheck.HasPrivateUse(title)) PrivateUseString();
                if (item.Get(PdfName.Get("Next")) is PdfDictionary next) stack.Push(next);
                if (item.Get(PdfName.Get("First")) is PdfDictionary first) stack.Push(first);
            }
            foreach (var page in doc.Pages)
                if (page.Dictionary.Get(Annots) is PdfArray annots)
                    for (var i = 0; i < annots.Count && i < 10_000; i++)
                        if (annots.Get(i) is PdfDictionary annot)
                            foreach (var key in (ReadOnlySpan<string>)["Contents", "TU", "T"])
                                if (Text(annot.Get(PdfName.Get(key))) is { } text && PdfACheck.HasPrivateUse(text)) PrivateUseString(page.Index + 1);

            void PrivateUseString(int? page = null) =>
                Add(AccessibilityRules.TextStringNoPua, Tr.T("Ein lesbarer Text der Datei enthält Zeichen aus dem Bereich für private Nutzung."), page);
        }
    }
}
