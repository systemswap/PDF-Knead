using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        private static readonly PdfName StructureDestinationKey = PdfName.Get("SD"), StructureElementKey = PdfName.Get("SE");

        /// <summary>
        /// Whether the copy writes the structure element a structure destination names (ISO 32000-2, 12.3.2.3): written as PDF 2.0 (/SD came
        /// with it), the element's file keeps its tree in the copy (the main file, or a merged one whose tree comes along) and the element is
        /// not left out (pruned, taken out of the tree).
        /// </summary>
        private bool WritesStructureElement(Source source, PdfObject? element) => Pdf20 && WritesElement(source, element);

        /// <summary>Whether the copy writes a structure element of a source (its tree in the copy, the element not left out) — any version.</summary>
        private bool WritesElement(Source source, PdfObject? element) =>
            element is PdfReference reference && !source.Removed.Contains(reference.Id.Number) &&
            (source.IsMain ? WritesMainTree() : source.StructKeyOffset is not null) && Current(source, reference) is { } dict && IsElement(dict);

        /// <summary>
        /// Whether the copy writes the structure tree of the main file: tagged and kept (<see cref="KeepsStructure"/>), or — not tagged, without
        /// /MarkInfo /Marked — a tree it carries along as it is. A bookmark of such a file lost its /SE although the copy wrote the element
        /// (docs/FEHLERGEDAECHTNIS.md, No. 880).
        /// </summary>
        private bool WritesMainTree() =>
            KeepsStructure() || !Tagged() && !Cleanup.HasFlag(CleanupItems.Structure) && _main.Catalog?.GetRaw(StructTreeRoot) is not null;

        /// <summary>The /SD of a GoTo action as the copy writes it — its element written (<see cref="WritesStructureElement"/>) — else null.</summary>
        private PdfObject? StructureDestination(Source source, PdfDictionary action) =>
            action.Get(StructureDestinationKey) is PdfArray { Count: > 0 } destination && WritesStructureElement(source, destination[0])
                ? Rebase(destination, source)
                : null;

        /// <summary>A GoTo action to a page (/D) and, where given, a structure element (/SD; docs/ABLAUFPLAN.md, 6.5 step 15).</summary>
        private static PdfDictionary GoTo(PdfObject destination, PdfObject? structureDestination)
        {
            var action = new PdfDictionary();
            action.Set(S, PdfName.Get("GoTo"));
            action.Set(D, destination);
            if (structureDestination is not null) action.Set(StructureDestinationKey, structureDestination);
            return action;
        }

        /// <summary>
        /// A GoTo action — the object itself, or the /A of an annotation (a link) — whose /SD names an element the copy does not write, or any
        /// /SD where the copy is not PDF 2.0, loses its /SD; its /D stays (ISO 32000-2, 12.6.4.2, Table 202; docs/ABLAUFPLAN.md, 6.5 step 15).
        /// An action without /D keeps it (the structure destination is all it has). Before: /SD named null after pruning.
        /// </summary>
        private PdfObject WithLiveStructureDestinations(Source source, PdfObject obj)
        {
            if (obj is not PdfDictionary dict) return obj;
            // a bookmark's structure element (/SE, ISO 32000-2, Table 151, from PDF 1.3) the copy does not write goes — kept, it named null,
            // or (structure cleaned up) wrote the whole tree back through it, with its replacement texts (6.6 c, No. 650)
            if (dict.GetRaw(StructureElementKey) is { } se && !WritesElement(source, se))
            {
                var withoutElement = Clone(dict);
                withoutElement.Remove(StructureElementKey);
                dict = withoutElement;
            }
            PdfDictionary? Fixed(PdfDictionary action)
            {
                if (action.GetName(S)?.Value != "GoTo" || !action.ContainsKey(StructureDestinationKey) || !action.ContainsKey(D) ||
                    StructureDestination(source, action) is not null)
                    return null;
                var copy = Clone(action);
                copy.Remove(StructureDestinationKey);
                return copy;
            }
            if (Fixed(dict) is { } fixedAction) return fixedAction;
            if (dict.GetRaw(A) is PdfDictionary inline && Fixed(inline) is { } fixedInline)
            {
                var copy = Clone(dict);
                copy.Set(A, fixedInline);
                return copy;
            }
            return dict;
        }
    }
}
