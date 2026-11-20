using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>The largest page box PDF/A allows, in units of user space (ISO 19005-2, 6.1.13; ISO 32000-1, C.2).</summary>
        private const double LargestBox = 14400;

        /// <summary>Pages (as read) larger than <see cref="LargestBox"/> and the factor their unit grows by.</summary>
        private readonly Dictionary<PdfDictionary, int> _pageUnits = new(ReferenceEqualityComparer.Instance);

        /// <summary>Annotations of such pages (source and number as read), with the factor.</summary>
        private readonly Dictionary<(Source Source, int Number), int> _annotationUnits = [];

        /// <summary>Annotations of such pages that exist only in memory (by their number in the new file), with the factor.</summary>
        private readonly Dictionary<int, int> _writtenAnnotationUnits = [];

        /// <summary>
        /// Pages larger than PDF/A allows (more than 14 400 units, 200 inches at the default unit): from PDF 1.6 on, the unit of user space can
        /// grow instead (/UserUnit, ISO 32000-1, 14.11.2) — the page keeps its size on paper and on screen, its boxes and everything placed in
        /// default user space shrink by the same factor (a whole number, so that nothing is rounded beyond the last digit). PDF/A-1 sets no
        /// such limit (and PDF 1.4 has no /UserUnit): its pages stay as they are. Noted here before anything is written, as destinations and
        /// annotations refer to the pages.
        /// </summary>
        private void NoteLargePages()
        {
            if (_pdfA!.Part.Part() == 1) return;
            foreach (var item in _pages)
            {
                var page = item.Page;
                var largest = Math.Max(Math.Max(Math.Abs(page.MediaBox.Width), Math.Abs(page.MediaBox.Height)),
                    Math.Max(Math.Abs(page.CropBox.Width), Math.Abs(page.CropBox.Height)));
                if (!(largest > LargestBox) || !double.IsFinite(largest) || largest > LargestBox * 1000) continue;
                var factor = (int)Math.Ceiling(largest / LargestBox);
                // the page must not become smaller than PDF/A allows either
                if (Math.Min(Math.Abs(page.MediaBox.Width), Math.Abs(page.MediaBox.Height)) / factor < 3) continue;
                _pageUnits[page.Dictionary] = factor;
                if (page.Dictionary.GetRaw(Annots) is { } raw && Look(raw, _sources[item.Source]) is (PdfArray annots, var from) && from is not null)
                    for (var i = 0; i < annots.Count && i < 100_000; i++)
                        if (annots[i] is PdfReference r) _annotationUnits[(from, r.Id.Number)] = factor;
            }
        }

        /// <summary>
        /// A page noted by <see cref="NoteLargePages"/> as written: /UserUnit grown by the factor, the boxes shrunk by it, the content drawn
        /// smaller by it (a scaling before it, in a stream of its own), annotations that exist only in memory noted to shrink alike.
        /// </summary>
        private void ShrinkLargePage(PdfDictionary dict, PdfPage page, Source source)
        {
            if (!_pageUnits.TryGetValue(page.Dictionary, out var factor)) return;
            dict.Set(PdfName.UserUnit, new PdfReal(page.UserUnit * factor));
            static PdfArray Box(double[] v, int factor) => new([.. v.Select(n => (PdfObject)new PdfReal(n / factor))]);
            dict.Set(PdfName.MediaBox, Box([page.MediaBox.Left, page.MediaBox.Bottom, page.MediaBox.Right, page.MediaBox.Top], factor));
            if (dict.ContainsKey(PdfName.CropBox))
                dict.Set(PdfName.CropBox, Box([page.CropBox.Left, page.CropBox.Bottom, page.CropBox.Right, page.CropBox.Top], factor));
            foreach (var key in (ReadOnlySpan<string>)["BleedBox", "TrimBox", "ArtBox"])
                if (dict.GetRaw(PdfName.Get(key)) is { } raw && PdfAResolve(raw, source) is PdfArray { Count: 4 } box &&
                    Enumerable.Range(0, 4).Select(i => PdfAResolve(box[i], source)?.AsNumber()).ToArray() is var numbers && numbers.All(n => n is { } v && double.IsFinite(v)))
                    dict.Set(PdfName.Get(key), Box([.. numbers.Select(n => n!.Value)], factor));
            // viewports (12.9): their boxes and the origins of their measures lie in default user space, the measures convert from it — with
            // a larger unit, one new unit is factor old ones (docs/FEHLERGEDAECHTNIS.md, No. 579)
            if (dict.GetRaw(PdfName.Get("VP")) is { } vpRaw && Look(vpRaw, source) is (PdfArray viewports, var vpFrom))
            {
                var shrunk = new PdfArray();
                for (var i = 0; i < Math.Min(viewports.Count, 10_000); i++)
                {
                    var (entry, from) = Look(viewports[i], vpFrom);
                    if (entry is not PdfDictionary viewport)
                    {
                        shrunk.Add(Portable(viewports[i], vpFrom, null));
                        continue;
                    }
                    var copy = new PdfDictionary();
                    foreach (var (key, value) in viewport.Entries) copy.Set(key, Portable(value, from, null));
                    if (viewport.Get(PdfName.Get("BBox")) is PdfArray bbox) copy.Set(PdfName.Get("BBox"), Shrunk(bbox, factor));
                    if (viewport.Get(PdfName.Get("Measure")) is PdfDictionary measure)
                        copy.Set(PdfName.Get("Measure"), ShrunkMeasure(measure, factor, v => Portable(v, from, null)));
                    shrunk.Add(copy);
                }
                dict.Set(PdfName.Get("VP"), shrunk);
            }
            var scale = PdfSerializer.FormatReal(1.0 / factor);
            var before = Fresh(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes($"{scale} 0 0 {scale} 0 0 cm\n")));
            var contents = new PdfArray([before]);
            switch (dict.GetRaw(PdfName.Contents))
            {
                case PdfArray parts:
                    for (var i = 0; i < parts.Count; i++) contents.Add(parts[i]);
                    break;
                case { } single:
                    contents.Add(single);
                    break;
            }
            dict.Set(PdfName.Contents, contents);
            if (dict.GetRaw(Annots) is PdfArray annots)
                for (var i = 0; i < annots.Count; i++)
                    if (annots[i] is OutputReference o && !_pdfA!.Origin.ContainsKey(o.Number)) _writtenAnnotationUnits[o.Number] = factor;
        }

        /// <summary>The factor an annotation shrinks by (it lies on a page noted by <see cref="NoteLargePages"/>); 0 if it does not.</summary>
        private int AnnotationUnit(int number) =>
            number <= 0 ? 0
            : _writtenAnnotationUnits.TryGetValue(number, out var written) ? written
            : _pdfA!.Origin.TryGetValue(number, out var at) && _annotationUnits.TryGetValue((at.Source, at.Old), out var factor) ? factor
            : 0;

        /// <summary>
        /// An annotation of a page whose unit grew (see <see cref="NoteLargePages"/>): its places in default user space shrink by the factor —
        /// /Rect (its appearance follows, being fitted to it), /QuadPoints, /Vertices, /L, /CL, /InkList, /RD, /Path, the leader lines /LL, /LLE,
        /// /LLO of a dimension line — and its measure converts from the larger unit (docs/FEHLERGEDAECHTNIS.md, No. 579).
        /// </summary>
        private static PdfDictionary ShrunkAnnotation(PdfDictionary annotation, int factor)
        {
            var copy = new PdfDictionary(annotation.Count) { Resolver = annotation.Resolver };
            foreach (var (key, value) in annotation.Entries) copy.Set(key, value);
            foreach (var key in (ReadOnlySpan<string>)["Rect", "QuadPoints", "Vertices", "L", "CL", "RD"])
                if (annotation.Get(PdfName.Get(key)) is PdfArray numbers) copy.Set(PdfName.Get(key), Shrunk(numbers, factor));
            foreach (var key in (ReadOnlySpan<string>)["InkList", "Path"])
                if (annotation.Get(PdfName.Get(key)) is PdfArray paths)
                    copy.Set(PdfName.Get(key), new PdfArray([.. Enumerable.Range(0, paths.Count).Select(i => paths.Get(i) is PdfArray path ? Shrunk(path, factor) : paths[i])]));
            foreach (var key in (ReadOnlySpan<string>)["LL", "LLE", "LLO"])
                if (annotation.Get(PdfName.Get(key)).AsNumber() is { } length && double.IsFinite(length)) copy.Set(PdfName.Get(key), new PdfReal(length / factor));
            if (annotation.Get(PdfName.Get("Measure")) is PdfDictionary measure) copy.Set(PdfName.Get("Measure"), ShrunkMeasure(measure, factor, v => v));
            // an own dimension keeps its points in user space units (relative to /Rect): reopened, it would be drawn anew at twice the size. It
            // stays a dimension with its shrunk appearance and measure — no longer an element of PDF Knead (docs/FEHLERGEDAECHTNIS.md, No. 597)
            if (Editing.ElementStore.DataOf(annotation)?.GetName(PdfName.Get("Art"))?.Value == "Bemassung")
            {
                copy.Remove(Editing.ElementStore.Key);
                copy.Remove(Editing.ElementStore.LegacyKey);
            }
            return copy;
        }

        /// <summary>
        /// A rectilinear measure for a unit grown by <paramref name="factor"/>: the first factors of /X and /Y convert from default user space and
        /// grow by it, /O lies in it and shrinks (ISO 32000-2, 12.9, Tables 267 and 268). Other subtypes (GEO) as they are.
        /// </summary>
        private static PdfDictionary ShrunkMeasure(PdfDictionary measure, int factor, Func<PdfObject, PdfObject> portable)
        {
            if (measure.GetName(PdfName.Subtype)?.Value is { } subtype && subtype != "RL") return measure;
            var copy = new PdfDictionary();
            foreach (var (key, value) in measure.Entries) copy.Set(key, portable(value));
            foreach (var axis in (ReadOnlySpan<string>)["X", "Y"])
            {
                if (measure.GetArray(PdfName.Get(axis)) is not { Count: > 0 } units || units.Get(0) is not PdfDictionary first || first.GetNumber(PdfName.Get("C")) is not { } c)
                    continue;
                var unit = new PdfDictionary();
                foreach (var (key, value) in first.Entries) unit.Set(key, portable(value));
                unit.Set(PdfName.Get("C"), new PdfReal(c * factor));
                var array = new PdfArray([unit]);
                for (var i = 1; i < units.Count; i++) array.Add(portable(units[i]));
                copy.Set(PdfName.Get(axis), array);
            }
            if (measure.Get(PdfName.Get("O")) is PdfArray origin) copy.Set(PdfName.Get("O"), Shrunk(origin, factor));
            return copy;
        }

        private static PdfArray Shrunk(PdfArray numbers, int factor) =>
            new([.. Enumerable.Range(0, numbers.Count).Select(i => numbers.Get(i).AsNumber() is { } v ? new PdfReal(v / factor) : numbers[i])]);

        /// <summary>
        /// A destination to a page whose unit grew (see <see cref="NoteLargePages"/>): its coordinates shrink by the factor (ISO 32000-1, 12.3.2.2:
        /// left, top, right, bottom in default user space; the zoom stays). Null for any other array.
        /// </summary>
        private PdfArray? ShrunkDestination(PdfArray array, Source? source)
        {
            if (_pageUnits.Count == 0 || array.Count < 2 || array.Get(1) is not PdfName { Value: "XYZ" or "FitH" or "FitV" or "FitR" or "FitBH" or "FitBV" } kind ||
                Look(array[0], source).Obj is not PdfDictionary target || !_pageUnits.TryGetValue(target, out var factor))
                return null;
            var copy = new PdfArray { Resolver = array.Resolver };
            for (var i = 0; i < array.Count; i++)
            {
                // XYZ: left, top, zoom — the zoom is no coordinate
                var coordinate = i >= 2 && !(kind.Value == "XYZ" && i == 4);
                copy.Add(coordinate && array.Get(i).AsNumber() is { } v ? new PdfReal(v / factor) : array[i]);
            }
            return copy;
        }
    }
}
