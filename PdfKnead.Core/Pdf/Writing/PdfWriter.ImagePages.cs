using PdfKnead.Core.Pdf.Accessibility;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>Per page of the main file replaced by its image (object number): the first place in the tree where its elements went.</summary>
        private Dictionary<int, (PdfReference Parent, int Index)> _imagePagePlaces = [];

        /// <summary>Per image page of the copy (its number): the MCID of its Figure around the whole content.</summary>
        private readonly Dictionary<int, int> _imagePageMarks = [];

        /// <summary>
        /// The pages of the main file another page stands for (<see cref="Editing.PageItem.Replaces"/>: a redacted page saved as its image) and
        /// that are not written themselves as well.
        /// </summary>
        private HashSet<int> ReplacedMainPages()
        {
            var replaced = new HashSet<int>();
            foreach (var item in _pages)
                if (item.Replaces is { } r && ReferenceEquals(r.Source, _main)) replaced.Add(r.Id.Number);
            if (replaced.Count > 0)
                foreach (var item in _pages)
                    if (ReferenceEquals(item.Source, _main) && item.Page.Id is { } id) replaced.Remove(id.Number);
            return replaced;
        }

        /// <summary>
        /// A page of a tagged copy replaced by its image („zusätzlich als Bild“; docs/ABLAUFPLAN.md, 6.6 g; docs/FEHLERGEDAECHTNIS.md, No. 654):
        /// the image becomes a Figure with /Alt „Seite {n} als Bild (geschwärzt)“ (in the document's language, else English) and a layout /BBox
        /// of the page, at the place of the first element of the old page that went (<see cref="TreePruner.Places"/>; not into a list or table
        /// structure, <see cref="StructurePlan.NewElementAt"/>), else beside the last element of the page before. The page gets a key into the
        /// parent tree and its content one marked-content sequence (<see cref="PageDictionary"/>). Before the image stood untagged in a tagged
        /// file (ISO 14289-1, 7.1). Not for PDF/A (its content is written anew; the export tags itself).
        /// </summary>
        private void ImagePageFigures()
        {
            if (_pdfA is not null || !_pages.Any(p => p.Replaces is { } r && ReferenceEquals(r.Source, _main)) || StructureEdits() is not { } plan) return;
            var language = DocumentLanguage;
            for (var i = 0; i < _pages.Count; i++)
            {
                if (_pages[i].Replaces is not { } replaced || !ReferenceEquals(replaced.Source, _main) || ReferenceEquals(_pages[i].Source, _main)) continue;
                var (alt, lang) = FixedTexts.PageAsImage(i + 1, language);
                var made = _imagePagePlaces.TryGetValue(replaced.Id.Number, out var place)
                    ? plan.NewElementAt("Figure", place.Parent, place.Index, i, alt, lang)
                    : plan.NewElement("Figure", i > 0 ? plan.LastElementOnPage(i - 1) : null, i, alt, lang, besideAnchor: true);
                if (made is null) continue;
                var box = _pages[i].Page.CropBox;
                var layout = new PdfDictionary();
                layout.Set(PdfName.Get("O"), PdfName.Get("Layout"));
                layout.Set(PdfName.Get("BBox"), new PdfArray([new PdfReal(Math.Round(box.Left, 3)), new PdfReal(Math.Round(box.Bottom, 3)),
                    new PdfReal(Math.Round(box.Right, 3)), new PdfReal(Math.Round(box.Top, 3))]));
                plan.AddAttributes(made, layout);
                var mcid = plan.NextMcid(i);
                plan.AddMarkedContent(made, i, mcid);
                _imagePageMarks[_pageNumbers[i]] = mcid;
            }
        }

        /// <summary>The content of an image page with a Figure (<see cref="ImagePageFigures"/>): its streams between a BDC with the MCID and an EMC.</summary>
        private void MarkImagePage(PdfDictionary dict, int self)
        {
            if (!_imagePageMarks.TryGetValue(self, out var mcid) || dict.GetRaw(PdfName.Contents) is not { } contents) return;
            var parts = new PdfArray();
            parts.Add(Fresh(new PdfStream(new PdfDictionary(), System.Text.Encoding.ASCII.GetBytes(
                "/Figure <</MCID " + mcid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ">> BDC\n"))));
            if (contents is PdfArray list)
                for (var i = 0; i < list.Count; i++) parts.Add(list[i]);
            else parts.Add(contents);
            parts.Add(Fresh(new PdfStream(new PdfDictionary(), "\nEMC\n"u8.ToArray())));
            dict.Set(PdfName.Contents, parts);
        }
    }
}
