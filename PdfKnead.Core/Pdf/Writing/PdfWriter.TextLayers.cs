using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>The forms of the text recognition put over pages (<see cref="PdfSaveOptions.TextLayers"/>).</summary>
        private readonly HashSet<PdfStream> _textLayerForms = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// The text layer of the text recognition put over a page of a tagged copy (docs/ABLAUFPLAN.md, 6.6 i, item 2; docs/FEHLERGEDAECHTNIS.md,
        /// No. 657): a paragraph of its own — beside the element the middle of the page lies in (the page's image, mostly a Figure, which keeps
        /// its description), else beside the last element of the page. Recognised text is real content and is tagged (Matterhorn 08-002);
        /// before it stood untagged in the tagged file. Null for any other form or where the copy keeps no structure of the page.
        /// </summary>
        private ContentMark? TextLayerIntoContent(PdfStream overlay, PageItem item, int pageNumber)
        {
            if (!_textLayerForms.Contains(overlay) || !ReferenceEquals(item.Source, _main) || StructureEdits() is not { } plan ||
                PageIndexOf(pageNumber) is var page && page < 0)
                return null;
            if (!_markedGeometry.TryGetValue(page, out var geometry))
                _markedGeometry[page] = geometry = MarkedContentGeometry.Of(item.Source, item.Page, _cancel);
            var box = item.Page.CropBox;
            var near = MarkedContentGeometry.At(geometry, (box.Left + box.Right) / 2, (box.Bottom + box.Top) / 2) is { } mcid ? plan.ElementOfMcid(page, mcid) : null;
            if (plan.NewElement("P", near ?? plan.LastElementOnPage(page), page, besideAnchor: true) is not { } made) return null;
            var marked = plan.NextMcid(page);
            plan.AddMarkedContent(made, page, marked);
            return new ContentMark(PdfName.Get("P"), marked);
        }
    }
}
