using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private static readonly PdfName NeedAppearances = PdfName.Get("NeedAppearances");

    private sealed partial class Session
    {
        /// <summary>The form's /NeedAppearances is dropped because code fields are written (see <see cref="SettleAppearances"/>).</summary>
        private bool _settled;

        /// <summary>
        /// /NeedAppearances (ISO 32000-2, 12.7.3, Table 224) makes other readers draw every field anew from its value — a code field would show
        /// its content as text instead of its code (KONZEPT.md, section 5.19, step 4). When code fields are written into such a form, the
        /// writer settles the appearances itself and drops the flag: text and choice fields get the appearance the own display draws from
        /// their value, as when filled in — the stored one updated (only its /Tx section), else built from /MK and /DA (12.7.4.3;
        /// docs/FEHLERGEDAECHTNIS.md, No. 890; before, only fields whose appearance showed no text); check boxes, radio buttons and push
        /// buttons keep theirs (without one, No. 889). Other readers then show what PDF Knead shows. Flattening needs none of this (there is no form afterwards).
        /// </summary>
        /// <remarks>
        /// The same for pages of other files (merged, inserted) whose form has /NeedAppearances while the main file's has not: the written
        /// form keeps the main file's entries, so their fields would show no value anywhere (Fehlergedächtnis 238). They are settled the same
        /// way; the main file's form stays as it is.
        /// </remarks>
        private IReadOnlyList<PageItem> SettleAppearances(IReadOnlyList<PageItem> pages)
        {
            if (_options.Flatten) return pages;
            // PDF/A and PDF 2.0: every widget needs an appearance (ISO 19005-2, 6.3.3; ISO 32000-2, 12.5.5); fields without one get the one the
            // display draws from their value
            if (AppearancesRequired && !NeedsAppearances(_main) && !pages.Any(p => NeedsAppearances(p.Source)))
                return [.. pages.Select(p => Settled(p, onlyMissing: true))];
            if (NeedsAppearances(_main))
            {
                // PDF/A does not allow the flag at all (ISO 19005-2, 6.4.1), PDF 2.0 deprecates it (ISO 32000-2, 12.7.3, Table 224): every
                // form is settled
                if (!AppearancesRequired && !pages.Any(p => p.Elements.Any(IsCodeField))) return pages;
                _settled = true;
                return [.. pages.Select(Settled)];
            }
            return pages.Any(p => !ReferenceEquals(p.Source, _main) && NeedsAppearances(p.Source))
                ? [.. pages.Select(p => !ReferenceEquals(p.Source, _main) && NeedsAppearances(p.Source) ? Settled(p) : !AppearancesRequired ? p : Settled(p, onlyMissing: true))]
                : pages;
        }

        private static bool NeedsAppearances(PdfDocument doc) => doc.Catalog?.GetDictionary(AcroForm)?.GetBoolean(NeedAppearances) == true;

        private static bool IsCodeField(PageElement element) => element switch
        {
            FieldElement field => field.Spec.Code is not null,
            ForeignAnnotation { Spec: { } spec } => spec.Code is not null,
            ForeignAnnotation { Kind: "Widget" } foreign => FormField.Of(foreign) is { } field && FieldSpec.IsCodeField(field),
            _ => false,
        };

        private PageItem Settled(PageItem item) => Settled(item, onlyMissing: false);

        /// <summary>
        /// Check boxes and radio buttons settled with the state they show (<see cref="FieldAppearance.MissingFieldValue"/>): they get the
        /// appearance, the value of the field stays as read (<see cref="Fill"/>). Before, the state shown was also written as /V — a radio
        /// group whose value named another button, or a check box whose /V and /AS disagreed, changed its value in a PDF 2.0 or PDF/A copy
        /// (docs/FEHLERGEDAECHTNIS.md, No. 916).
        /// </summary>
        private readonly HashSet<PdfDictionary> _settledStates = new(ReferenceEqualityComparer.Instance);

        /// <param name="item">The page.</param>
        /// <param name="onlyMissing">Only fields without any appearance (the file does not ask for new ones, so existing ones stay).</param>
        private PageItem Settled(PageItem item, bool onlyMissing)
        {
            List<PageElement>? elements = null;
            for (var i = 0; i < item.Elements.Count; i++)
            {
                if (item.Elements[i] is not ForeignAnnotation { Kind: "Widget", Value: null, Spec: null } foreign ||
                    onlyMissing && FieldAppearance.HasNormal(foreign.Original) || FormField.Of(foreign) is not { } field)
                    continue;
                string? value;
                // the form asks for every field's appearance to be constructed: text and choice fields from their value, the stored
                // appearance updated (No. 890)
                if (!onlyMissing && FieldAppearance.ConstructedValue(foreign.Original, foreign.Document.Catalog?.GetDictionary(AcroForm)) is { } constructed)
                    value = constructed;
                else if (field.Kind is FormFieldKind.Text or FormFieldKind.ComboBox or FormFieldKind.ListBox && !FieldSpec.IsCodeField(field) &&
                    FieldAppearance.Replacement(foreign.Document, foreign.Original, foreign.Document.Catalog?.GetDictionary(AcroForm), needAppearances: true) is not null)
                    value = field.StoredValue;
                // without an appearance: an empty field with a frame from /MK (No. 888), a check box, radio button or push button (No. 889)
                else if (FieldAppearance.MissingFieldValue(foreign.Original) is { } missing)
                {
                    value = missing;
                    if (field.Kind is FormFieldKind.CheckBox or FormFieldKind.Radio) _settledStates.Add(foreign.Original);
                }
                else continue;
                elements ??= [.. item.Elements];
                elements[i] = foreign with { Value = value };   // filled with its own value: written with an appearance
            }
            return elements is null ? item : item.WithElements(elements);
        }
    }
}
