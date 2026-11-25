using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>
        /// What becomes of the inline images of a glyph procedure of a Type 3 font (ISO 32000-2, 8.9.7; docs/FEHLERGEDAECHTNIS.md, No. 1131): the
        /// image XObjects go into the font's resources — a glyph draws with those (9.6.4; the display, too, never with resources of a procedure's
        /// own).
        /// </summary>
        /// <param name="Ordinals">PDF/A-4: by the number of the inline image in the procedure (from 0), the name it is called by.</param>
        /// <param name="Content">Saving as 2.0 without PDF/A: the procedure as written, the images called by their names.</param>
        private sealed record Type3GlyphPlan(Dictionary<int, PdfName> Ordinals, byte[]? Content);

        /// <summary>The plans of glyph procedures, by document and where their data lie (a copy of the dictionary is another object).</summary>
        private readonly Dictionary<(PdfDocument, long), Type3GlyphPlan> _glyphPlans = [];

        /// <summary>Every glyph procedure looked at (a planned one or not): each once.</summary>
        private readonly HashSet<(PdfDocument, long)> _glyphs = [];

        /// <summary>
        /// Type 3 fonts with the image XObjects their glyphs call, added to their resources when written: the dictionaries as read, and copies the
        /// PDF/A copy makes of them on the way (<see cref="PdfAFix"/>).
        /// </summary>
        private readonly Dictionary<PdfDictionary, List<(PdfName Name, PdfStream Image)>> _fontImages = new(ReferenceEqualityComparer.Instance);

        private static readonly PdfName CharProcsKey = PdfName.Get("CharProcs");

        private Type3GlyphPlan? GlyphPlanOf(PdfDocument doc, PdfStream stream) =>
            _glyphPlans.Count > 0 && stream.DataOffset >= 0 && _glyphPlans.TryGetValue((doc, stream.DataOffset), out var plan) ? plan : null;

        /// <summary>The images planned for the resources of a Type 3 font (a list that grows; names unique within it).</summary>
        private List<(PdfName Name, PdfStream Image)> FontImages(PdfDictionary font) =>
            _fontImages.TryGetValue(font, out var list) ? list : _fontImages[font] = [];

        /// <summary>The resources a Type 3 font's glyphs draw with, as read: its own, or those it is lent (PDF/A: the check's).</summary>
        private PdfDictionary? FontResourcesAsRead(PdfDictionary font) =>
            font.GetDictionary(PdfName.Resources) ??
            (_pdfA is not null ? _pdfA.Usage.Borrowed.GetValueOrDefault(font) : _borrowed.GetValueOrDefault(font));

        /// <summary>
        /// Saving as PDF 2.0 (PDF/A-4 too): the glyph procedures of the Type 3 fonts the pages show — through forms, patterns and other Type 3
        /// fonts, as far as their resources reach — with an inline image the copy writes as an image XObject (more than 4 096 bytes of data; as
        /// 2.0 without PDF/A also what <see cref="InlineImageConformance.Breaks"/> names), planned before anything is written: the images go into
        /// the font's resources, a copy (also of resources shared with others). Not for the copy compared with a signed file
        /// (<see cref="PdfSaveOptions.ContentAsStored"/>).
        /// </summary>
        private void PlanType3InlineImages()
        {
            if (!Pdf20 || _options.ContentAsStored || _pdfA is not null && _pdfA.Part.Part() != 4) return;
            var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            var fonts = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            foreach (var item in _pages)
            {
                if (item.Page.Resources is not { } top || !_sources.TryGetValue(item.Source, out var source)) continue;
                _cancel.ThrowIfCancellationRequested();
                var pending = new Stack<(PdfDictionary Resources, int Depth)>([(top, 0)]);
                while (pending.TryPop(out var next) && seen.Count < 10_000)
                {
                    if (!seen.Add(next.Resources)) continue;
                    void Inner(PdfDictionary? resources)
                    {
                        if (resources is not null && next.Depth < 16) pending.Push((resources, next.Depth + 1));
                    }
                    foreach (var kind in (string[])["XObject", "Pattern"])
                        if (next.Resources.GetDictionary(PdfName.Get(kind)) is { } entries)
                            foreach (var (name, _) in entries.Entries.Take(10_000).ToList())
                                if (entries.Get(name) is PdfStream inner) Inner(inner.Dictionary.GetDictionary(PdfName.Resources));
                    if (next.Resources.GetDictionary(PdfName.Get("Font")) is not { } fontDict) continue;
                    foreach (var (name, _) in fontDict.Entries.Take(10_000).ToList())
                    {
                        if (fontDict.Get(name) is not PdfDictionary font || font.GetName(PdfName.Subtype)?.Value != "Type3" || !fonts.Add(font)) continue;
                        Inner(font.GetDictionary(PdfName.Resources));
                        PlanFont(font, source);
                    }
                }
            }
        }

        /// <summary>The plans of one Type 3 font's glyph procedures (see <see cref="PlanType3InlineImages"/>).</summary>
        private void PlanFont(PdfDictionary font, Source source)
        {
            if (font.GetDictionary(CharProcsKey) is not { } procs) return;
            var doc = source.Doc;
            var fontResources = FontResourcesAsRead(font);
            var images = FontImages(font);
            PdfName Free() => InlineImageConformance.FreeName(n => fontResources?.GetDictionary(XObject)?.ContainsKey(n) == true || images.Any(x => x.Name == n));
            foreach (var (key, _) in procs.Entries.Take(4096).ToList())
            {
                if (procs.Get(key) is not PdfStream proc || proc.DataOffset < 0) continue;
                var at = (doc, proc.DataOffset);
                if (!_glyphs.Add(at)) continue;
                byte[] data;
                try
                {
                    data = doc.GetStreamData(proc);
                }
                catch (Exception e) when (e is PdfException or IOException or InvalidDataException)
                {
                    continue;
                }
                if (!InlineImageConformance.MayHold(data)) continue;
                if (_pdfA is null)
                {
                    var rewritten = InlineImageConformance.Rewrite(data, data.Length, image =>
                    {
                        var name = Free();
                        images.Add((name, InlineImageConformance.AsXObject(image, fontResources)));
                        return name;
                    }, pdf20: true);
                    if (rewritten is not null) _glyphPlans[at] = new Type3GlyphPlan([], ContentConformance.CanonicalSyntax(rewritten, true) ?? rewritten);
                    continue;
                }
                // as the PDF/A copy writes the procedure (PdfAStream): numbered as ContentConformance.Rewrite numbers its inline images, the length
                // as it writes them, in the colours of the resources it is drawn with there
                var colourSource = PdfADrawnWith(proc);
                var ordinals = new Dictionary<int, PdfName>();
                var parser = new ContentParser(data);
                var ordinal = 0;
                for (var operations = 0; operations < 1_000_000 && parser.Next() is { } op; operations++)
                {
                    if (op != "BI" || parser.InlineImage is not { } image) continue;
                    var number = ordinal++;
                    if (ContentConformance.WrittenInlineLength(image, name => ColourChangeOf(colourSource, doc, name)) is not > InlineImageConformance.MaxInlineData ||
                        PlacedImage(image, fontResources) is not { } xobject)
                        continue;
                    var placedName = Free();
                    images.Add((placedName, xobject));
                    ordinals[number] = placedName;
                }
                if (ordinals.Count > 0) _glyphPlans[at] = new Type3GlyphPlan(ordinals, null);
            }
        }

        /// <summary>The resources the PDF/A copy draws a stream with when it writes it anew (<see cref="PdfAStream"/>), as read.</summary>
        private PdfDictionary? PdfADrawnWith(PdfStream stream)
        {
            var usage = _pdfA!.Usage;
            return (usage.Borrowed.TryGetValue(stream.Dictionary, out var lent) ? lent : _pdfAUndrawn.GetValueOrDefault(stream.Dictionary)) ??
                   stream.Dictionary.GetDictionary(PdfName.Resources) ?? usage.PageResources.GetValueOrDefault(stream);
        }

        /// <summary>
        /// A Type 3 font about to be written with the image XObjects planned for it: its /Resources a copy of those it is written with — an
        /// object of their own read and copied, never changed for others — with the images added under their names. <paramref name="written"/>
        /// itself where nothing is planned.
        /// </summary>
        /// <param name="written">The dictionary as it is about to be written (references of the source or of the copy).</param>
        /// <param name="asRead">The font as read, or a copy the PDF/A copy made of it (the key of the plan).</param>
        /// <param name="number">The number it is written under (a copy of an owner may have resources of its own); 0 if written directly.</param>
        /// <param name="source">Its source.</param>
        private PdfObject WithFontImages(PdfObject written, PdfDictionary? asRead, int number, Source? source)
        {
            if (_fontImages.Count == 0 || asRead is null || source is null || written is not PdfDictionary font || written is PdfStream ||
                !_fontImages.TryGetValue(asRead, out var images) || images.Count == 0 || font.GetName(PdfName.Subtype)?.Value != "Type3")
                return written;
            var result = CloneWithResolver(font);
            result.Set(PdfName.Resources, WithImages(font.GetRaw(PdfName.Resources), number > 0 && _variantLent.TryGetValue(number, out var variant) ? variant
                : FontResourcesAsRead(asRead), images, source));
            return result;
        }

        /// <summary>
        /// Resources as written (<paramref name="raw"/>: a dictionary, a reference of the source, one of the copy, or none) with image XObjects
        /// added — a copy; where the written ones cannot be read (an object of the copy), <paramref name="asRead"/> is copied for the source.
        /// </summary>
        private PdfDictionary WithImages(PdfObject? raw, PdfDictionary? asRead, List<(PdfName Name, PdfStream Image)> images, Source source)
        {
            var copy = raw switch
            {
                PdfDictionary direct => CloneWithResolver(direct),
                PdfReference reference when source.Doc.Resolve(reference.Id) is PdfDictionary resolved => PdfAFixed(CloneWithResolver(resolved), source),
                _ => asRead is null ? new PdfDictionary() : (PdfDictionary)Rebase(asRead, source),
            };
            var xobjects = copy.Get(XObject) is PdfDictionary known ? CloneWithResolver(known) : new PdfDictionary();
            foreach (var (name, image) in images) xobjects.Set(name, Sourced(image, source));
            copy.Set(XObject, xobjects);
            return copy;
        }

        /// <summary>A dictionary of the source brought in line with PDF/A where the copy is one (<see cref="PdfAFix"/>).</summary>
        private PdfDictionary PdfAFixed(PdfDictionary dict, Source source) =>
            _pdfA is not null && PdfAFix(dict, source, 0) is PdfDictionary fixedDict ? fixedDict : dict;
    }
}
