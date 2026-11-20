using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>Per page of the main file (object number): marked content left empty whose text a block now carries (<see cref="PlanRefills"/>).</summary>
        private readonly Dictionary<int, HashSet<int>> _refilledEmptied = [];

        /// <summary>
        /// Edited text in a tagged copy (docs/ABLAUFPLAN.md, 6.6 i; docs/FEHLERGEDAECHTNIS.md, No. 656), before the tree is pruned: marked
        /// content left empty because a block took its characters (<see cref="RichTextElement.ReplacesMcids"/>) is no emptied content whose
        /// element would go — the block gives its element new content (<see cref="EditedTextIntoContent"/>). Its empty bracket becomes an
        /// artifact (as with the brackets of elements that went, 6.6 b), its place in the element the block's new marked content.
        /// </summary>
        private void PlanRefills()
        {
            if (!TaggedCopy()) return;
            var main = _sources[_main];
            foreach (var item in _pages)
            {
                if (!ReferenceEquals(item.Source, _main) || item.Page.Id is not { } id || NumberTree.KeyOf(item.Page.Dictionary.Get(StructParents)) is null ||
                    !main.EmptiedMcids.TryGetValue(id.Number, out var emptied))
                    continue;
                var refilled = OwnElements(item).OfType<RichTextElement>().Where(Printed).SelectMany(r => r.ReplacesMcids).ToHashSet();
                refilled.IntersectWith(emptied);
                if (refilled.Count == 0) continue;
                emptied.ExceptWith(refilled);
                if (emptied.Count == 0) main.EmptiedMcids.Remove(id.Number);
                _refilledEmptied[id.Number] = refilled;
                ArtifactBrackets(item, refilled);
            }
        }

        /// <summary>
        /// The text of an edited block of a tagged page in the element whose characters it took (6.6 i): new marked content of the page in the
        /// element of its first replaced MCID that still has one — at that MCID's place where its bracket was left empty, else right after it;
        /// further brackets it left empty leave their elements. Null if none of them has an element (the block then gets a paragraph of its own).
        /// </summary>
        private ContentMark? EditedTextIntoContent(RichTextElement rich, PageItem item, int page, StructurePlan plan)
        {
            var gone = item.Page.Id is { } id && _refilledEmptied.TryGetValue(id.Number, out var emptied) ? emptied : null;
            foreach (var first in rich.ReplacesMcids)
            {
                if (plan.ElementOfMcid(page, first) is not { } element) continue;
                var mcid = plan.NextMcid(page);
                if (!plan.PutMarkedContent(element, page, mcid, first, replace: gone?.Contains(first) == true)) plan.AddMarkedContent(element, page, mcid);
                foreach (var other in rich.ReplacesMcids)
                    if (other != first && gone?.Contains(other) == true && plan.ElementOfMcid(page, other) is { } holder) plan.RemoveMarkedContent(holder, page, other);
                return new ContentMark(plan.View.Element(element)?.GetName(PdfName.Get("S")) ?? PdfName.Get("P"), mcid);
            }
            return null;
        }
    }
}
