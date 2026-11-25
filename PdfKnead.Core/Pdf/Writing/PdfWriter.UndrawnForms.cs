using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>
        /// Forms without resources of their own that no content draws and no page names, whose content names resources (dictionaries as read):
        /// they are written without, nothing tells which resources they would be drawn with (<see cref="PlanUndrawnForms"/>).
        /// </summary>
        private readonly HashSet<PdfDictionary> _unresourced = new(ReferenceEqualityComparer.Instance);

        /// <summary>Those of <see cref="_unresourced"/> written (counted once each; <see cref="PdfSaveResult.FormsWithoutResources"/>).</summary>
        private readonly HashSet<PdfDictionary> _unresourcedWritten = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Forms without resources of their own that no content draws (ISO 32000-2, 8.10.2 sentences 30–32, 7.8.3; V10): one that the /XObject
        /// of a page names gets the resources of the first page that names it — added to <paramref name="givens"/>, so that it is written with
        /// the names its content uses of them, as a drawn one (<see cref="PlanResourceVariants"/>). One that no page names — only the /XObject
        /// of a form, which never draws it — stays without; where its content names resources it is noted (<see cref="_unresourced"/>) and
        /// reported (docs/FEHLERGEDAECHTNIS.md, No. 1131). Before, every form never drawn went without (No. 549, 782 gave only drawn ones theirs).
        /// </summary>
        /// <param name="givens">The owners drawn, with the resources they are drawn with (<see cref="ResourceUsage.Borrowed"/>).</param>
        /// <param name="ownerSource">The document of each owner.</param>
        private void PlanUndrawnForms(Dictionary<PdfDictionary, List<PdfDictionary>> givens, Dictionary<PdfDictionary, PdfDocument> ownerSource) =>
            PlanUndrawnForms(form => givens.ContainsKey(form.Dictionary), (form, resources, doc) =>
            {
                givens[form.Dictionary] = [resources];
                ownerSource.TryAdd(form.Dictionary, doc);
            });

        /// <summary>
        /// The same for PDF/A, which gives forms their resources itself (<see cref="PdfAStream"/>): a form the check saw drawn is written anew
        /// with those it is drawn with (<see cref="PdfACheck.Usage.Rewrites"/>, <see cref="PdfACheck.Usage.Borrowed"/>); one never drawn that a
        /// page names gets that page's — the names its content uses — in <see cref="_pdfAUndrawn"/> (docs/FEHLERGEDAECHTNIS.md, No. 1131).
        /// </summary>
        private void PlanUndrawnFormsPdfA()
        {
            var usage = _pdfA!.Usage;
            PlanUndrawnForms(form => usage.Rewrites.Contains(form) || usage.Borrowed.ContainsKey(form.Dictionary) || _pdfAUndrawn.ContainsKey(form.Dictionary),
                (form, resources, doc) => _pdfAUndrawn[form.Dictionary] = OnlyUsed(resources, UsedNames(doc, form, resources)));
        }

        /// <summary>Forms of PDF/A never drawn, without resources of their own, with those of the first page that names them (dictionaries as read).</summary>
        private readonly Dictionary<PdfDictionary, PdfDictionary> _pdfAUndrawn = new(ReferenceEqualityComparer.Instance);

        /// <param name="handled">Whether a form is drawn, or has been given resources already.</param>
        /// <param name="give">Gives a form named by a page the resources of that page.</param>
        private void PlanUndrawnForms(Func<PdfStream, bool> handled, Action<PdfStream, PdfDictionary, PdfDocument> give)
        {
            var pending = new Stack<(PdfDictionary Resources, PdfDocument Doc, int Depth)>();
            foreach (var item in _pages)
            {
                if (item.Page.Resources is not { } resources) continue;
                _cancel.ThrowIfCancellationRequested();
                pending.Push((resources, item.Source, 0));
                if (resources.GetDictionary(XObject) is not { } xobjects) continue;
                foreach (var (name, _) in xobjects.Entries.Take(10_000).ToList())
                    if (xobjects.Get(name) is PdfStream form && BareForm(form) && !handled(form)) give(form, resources, item.Source);
            }
            // forms named only in the resources of forms: walked like the resources of the pages (bounded as there)
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            while (pending.TryPop(out var next) && seen.Count < 10_000)
            {
                if (!seen.Add(next.Resources) || next.Resources.GetDictionary(XObject) is not { } xobjects) continue;
                foreach (var (name, _) in xobjects.Entries.Take(10_000).ToList())
                {
                    if (xobjects.Get(name) is not PdfStream form || form.Dictionary.GetName(PdfName.Subtype)?.Value != "Form") continue;
                    if (form.Dictionary.GetDictionary(PdfName.Resources) is { } inner)
                    {
                        if (next.Depth < 16) pending.Push((inner, next.Doc, next.Depth + 1));
                    }
                    else if (!handled(form) && NamesResources(next.Doc, form)) _unresourced.Add(form.Dictionary);
                }
            }
        }

        /// <summary>A form without resources of its own (resources naming nothing count as none, as the display reads them: S-A A67).</summary>
        private static bool BareForm(PdfStream stream) =>
            stream.Dictionary.GetName(PdfName.Subtype)?.Value == "Form" && stream.Dictionary.GetDictionary(PdfName.Resources) is null;

        /// <summary>Whether the content of a form names a resource (a font, an XObject, a colour space …); false where it cannot be read.</summary>
        private static bool NamesResources(PdfDocument doc, PdfStream form)
        {
            byte[] data;
            try
            {
                data = doc.GetStreamData(form);
            }
            catch (Exception e) when (e is PdfException or IOException or InvalidDataException)
            {
                return false;
            }
            var parser = new ContentParser(data);
            for (var operations = 0; operations < 1_000_000 && parser.Next() is { } op; operations++)
                if (ContentConformance.Named(op, parser.Operands) is not null) return true;
            return false;
        }

        /// <summary>A form of <see cref="_unresourced"/> is written without resources: counted for the result.</summary>
        private void NoteUnresourced(PdfDictionary asRead, PdfDictionary written)
        {
            if (_unresourced.Count > 0 && written.GetDictionary(PdfName.Resources) is null && _unresourced.Contains(asRead)) _unresourcedWritten.Add(asRead);
        }
    }
}
