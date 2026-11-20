using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Accessibility;

public static partial class AccessibilityCheck
{
    private sealed partial class Run
    {
        /// <summary>
        /// Stage C, content (ISO 14289-1, 7.1, 7.2, 7.20; -2, 8.2.2, 8.4.3), through <see cref="TaggedContent"/>: every painting operator
        /// tagged or an artifact, the two never inside each other, the language of replacement texts in marked content, reference XObjects,
        /// forms with MCIDs drawn once; PDF/UA-2: characters of the private use areas shown with a replacement text. With the check of the
        /// tree from the same building block: every element has a parent (of stage E). Only for a file with a structure tree — without one the
        /// rules of stage A have said it.
        /// </summary>
        public void Content()
        {
            if (Catalog?.GetDictionary(PdfName.Get("StructTreeRoot")) is null) return;
            var tagging = TaggedContent.Analyse(doc, cancel);
            foreach (var page in tagging.Pages)
            {
                var number = page.Page + 1;
                if (page.Untagged > 0)
                    Add(AccessibilityRules.UntaggedContent, Tr.T("Inhalt der Seite ist weder getaggt noch als Artefakt gekennzeichnet ({0}-mal).", page.Untagged), number);
                if (page.ArtifactInTagged > 0)
                    Add(AccessibilityRules.ArtifactInTagged, Tr.T("Ein Artefakt steht in getaggtem Inhalt."), number);
                if (page.TaggedInArtifact > 0)
                    Add(AccessibilityRules.TaggedInArtifact, Tr.T("Getaggter Inhalt steht in einem Artefakt."), number);
                if (page.ActualTextWithoutLanguage > 0)
                    Add(AccessibilityRules.ActualTextLanguage, Tr.T("Die Sprache eines Ersatztexts (/ActualText) im Seiteninhalt ist nicht bestimmbar."), number);
                if (page.AltWithoutLanguage > 0)
                    Add(AccessibilityRules.AltLanguage, Tr.T("Die Sprache eines Alternativtexts (/Alt) im Seiteninhalt ist nicht bestimmbar."), number);
                if (page.ExpansionWithoutLanguage > 0)
                    Add(AccessibilityRules.ExpansionLanguage, Tr.T("Die Sprache einer Auflösung (/E) im Seiteninhalt ist nicht bestimmbar."), number);
                if (page.ReferenceXObjects > 0)
                    Add(AccessibilityRules.ReferenceXObject, Tr.T("Die Seite zeichnet ein Referenz-XObject."), number);
            }
            if (tagging.FormsWithMcidsDrawnTwice > 0)
                Add(AccessibilityRules.FormWithMcidsOnce, Tr.T("Eine Form mit markiertem Inhalt (MCID) wird mehr als einmal gezeichnet."));
            if (TaggedContent.ParentTree(doc, cancel).Any(i => i.Kind == StructureIssueKind.ParentMissing))
                Add(AccessibilityRules.ElementParent, Tr.T("Ein Strukturelement nennt kein Elternelement (/P)."));
            if (part == 2) PrivateUseReplaced();
        }

        /// <summary>
        /// PDF/UA-2, 8.4.3: characters of the private use areas in real content need /ActualText or /Alt — of the marked content around them or
        /// of their structure element or one above it (<see cref="PrivateUseText"/>, shared with the save after redacting).
        /// </summary>
        private void PrivateUseReplaced()
        {
            var tree = Catalog?.GetDictionary(PdfName.Get("StructTreeRoot"))?.GetDictionary(PdfName.Get("ParentTree"));
            foreach (var page in doc.Pages)
            {
                cancel.ThrowIfCancellationRequested();
                var array = tree is not null && NumberTree.KeyOf(page.Dictionary.Get(PdfName.Get("StructParents"))) is { } key ? NumberTree.Value(tree, key) as PdfArray : null;
                bool WithText(int mcid)
                {
                    PdfDictionary? element = array is not null && mcid < array.Count ? array.Get(mcid) as PdfDictionary : null;
                    for (var i = 0; i < MaxDepth && element?.ContainsKey(PdfName.Get("S")) == true; i++)
                    {
                        if (element.ContainsKey(PdfName.Get("ActualText")) || element.ContainsKey(PdfName.Get("Alt"))) return true;
                        element = element.Get(PdfName.Get("P")) as PdfDictionary;
                    }
                    return false;
                }
                using var content = PageInterpreter.PageContent(doc, page.Dictionary);
                if (PrivateUseText.Found(doc, content.Span.ToArray(), page.Resources, WithText) == true)
                {
                    Add(AccessibilityRules.PrivateUseReplaced, Tr.T("Zeichen aus dem Bereich für private Nutzung stehen ohne Ersatztext im Seiteninhalt."), page.Index + 1);
                    return;
                }
            }
        }
    }
}
