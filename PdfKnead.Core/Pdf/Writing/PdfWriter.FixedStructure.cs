using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>
        /// The structure of an own element fixed into the content of a page of the main file in a tagged copy (docs/ABLAUFPLAN.md, 6.4 step 4;
        /// docs/FEHLERGEDAECHTNIS.md, No. 634), through the <see cref="StructurePlan"/>: an image, a signature (image or name in a handwriting
        /// font), a freehand drawing, a code, a dimension or a note become a Figure with /Alt and a layout /BBox — a signature says
        /// „Unterschrift“ without the name (ISO 14289-1, 7.13; the name would stand in plain text in the tree); text and a text field a P; a
        /// flattened check box, radio button, list or push button a Form with PrintField; a shape an artifact of the layout. The element goes next
        /// to the content the element sits on (<see cref="MarkedContentGeometry"/>), else beside the last element of the page. Edited text goes
        /// into the element whose characters it took (<see cref="EditedTextIntoContent"/>; 6.6 i, No. 656); a new block, or one whose element
        /// is gone, becomes a P beside the content it lies on — never inside a paragraph.
        /// </summary>
        /// <returns>The marked content around the element's form; null where the copy keeps no structure.</returns>
        private ContentMark? FixedIntoContent(ElementAppearance appearance, PageItem item, int pageNumber)
        {
            // an untagged file PDF-Knead makes itself with its structure given (the redaction protocol, 6.6 i, No. 658)
            if (_options.ElementStructure?.TryGetValue(appearance.Element, out var role) == true) return OwnStructureIntoContent(role, pageNumber);
            if (!ReferenceEquals(item.Source, _main) || StructureEdits() is not { } plan || PageIndexOf(pageNumber) is var page && page < 0) return null;
            if (appearance.Element is RichTextElement rich && EditedTextIntoContent(rich, item, page, plan) is { } refilled) return refilled;
            var language = DocumentLanguage;
            (string Type, string? Alt, FieldElement? Field) kind = appearance.Element switch
            {
                ShapeElement => ("Artifact", null, null),
                ImageElement { IsSignature: true } or SignatureElement => ("Figure", FixedTexts.Signature(language), null),
                ImageElement image => ("Figure", FixedTexts.Image(image, language), null),
                InkElement => ("Figure", FixedTexts.Ink(language), null),
                CodeElement code => ("Figure", ElementAppearance.CodeAltText(code), null),
                DimensionElement dimension => ("Figure", dimension.Text, null),
                NoteElement note => ("Figure", note.Text, null),
                FieldElement { Spec.Kind: not (FormFieldKind.Text or FormFieldKind.ComboBox) } own => ("Form", null, own),
                _ => ("P", null, null),
            };
            var (type, alt, field) = kind;
            if (type == "Artifact") return new ContentMark(PdfName.Get("Artifact"), null);
            var rect = appearance.Element.Rect;
            if (!_markedGeometry.TryGetValue(page, out var geometry))
                _markedGeometry[page] = geometry = MarkedContentGeometry.Of(item.Source, item.Page, _cancel);
            var near = MarkedContentGeometry.At(geometry, (rect.Left + rect.Right) / 2, (rect.Bottom + rect.Top) / 2) is { } mcid ? plan.ElementOfMcid(page, mcid) : null;
            if (plan.NewElement(type, near ?? plan.LastElementOnPage(page), page, alt: string.IsNullOrWhiteSpace(alt) ? null : alt.Trim(),
                    besideAnchor: near is null) is not { } made)
                return null;
            if (type == "Figure")
            {
                // where the figure lies (ISO 32000-2, 14.8.5.4.3; ISO 14289-1, 7.3 asks it for figures with content)
                var layout = new PdfDictionary();
                layout.Set(PdfName.Get("O"), PdfName.Get("Layout"));
                layout.Set(PdfName.Get("BBox"), new PdfArray([new PdfReal(Math.Round(rect.Left, 3)), new PdfReal(Math.Round(rect.Bottom, 3)),
                    new PdfReal(Math.Round(rect.Right, 3)), new PdfReal(Math.Round(rect.Top, 3))]));
                plan.AddAttributes(made, layout);
            }
            if (field is not null) plan.AddAttributes(made, PrintField(field));
            var marked = plan.NextMcid(page);
            plan.AddMarkedContent(made, page, marked);
            return new ContentMark(PdfName.Get(type), marked);
        }

        /// <summary>The attribute PrintField of an own field fixed into the page (as <see cref="PrintField(PdfDictionary)"/> for a flattened widget).</summary>
        private PdfDictionary PrintField(FieldElement field)
        {
            var spec = field.Spec;
            var role = spec.Kind switch
            {
                FormFieldKind.Radio => "rb",
                FormFieldKind.CheckBox => "cb",
                FormFieldKind.PushButton => "pb",
                FormFieldKind.ListBox => "lb",
                _ => "tv",
            };
            var attributes = new PdfDictionary();
            attributes.Set(PdfName.Get("O"), PdfName.Get("PrintField"));
            attributes.Set(PdfName.Get("Role"), PdfName.Get(role));
            if (role is "rb" or "cb")
                attributes.Set(PdfName.Get(Pdf20 ? "Checked" : "checked"), PdfName.Get(field.Value == ElementAppearance.OnStateOf(spec) ? "on" : "off"));
            if (spec.Tooltip.Trim() is { Length: > 0 } tooltip) attributes.Set(PdfName.Get("Desc"), new PdfString(PdfTextEncoding.Encode(tooltip)));
            return attributes;
        }
    }
}
