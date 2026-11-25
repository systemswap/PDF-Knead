using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

public static partial class PdfWriter
{
    private sealed partial class Session
    {
        /// <summary>
        /// The contents of a page written anew where they hold an inline image that <see cref="InlineImageConformance.Breaks"/> (JPEG 2000 or
        /// JBIG2 inline, ASCII85 without its end, „EI“ in the data): such an image becomes an image XObject in the page's resources, the rest of
        /// the content stays byte for byte; the parts are joined as the display joins them (a line end between them). The resources: those of
        /// the page as read — with only what the content uses, where that is known (7.8.3; No. 788) — and the new XObjects. Null where the page
        /// has no such inline image (decided on 20.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 993).
        /// </summary>
        private (PdfObject Contents, PdfObject Resources)? PageInlineImages(PageItem item, PdfDictionary page, Source source)
        {
            if (PageInlineImageContent(page, source, Pdf20) is not var (content, images)) return null;
            var resources = page.Get(PdfName.Resources) as PdfDictionary ?? InheritedValue(page, PdfName.Resources) as PdfDictionary;
            var kept = resources is null ? new PdfDictionary() : UsedOnly(item, resources);
            var written = new PdfDictionary();
            foreach (var (key, value) in kept.Entries)
                if (key != XObject) written.Set(key, value);
            var xobjects = new PdfDictionary();
            if (kept.GetDictionary(XObject) is { } before)
                foreach (var (key, value) in before.Entries) xobjects.Set(key, value);
            foreach (var (name, image) in images) xobjects.Set(name, image);
            written.Set(XObject, xobjects);
            var stream = new PdfDictionary();
            stream.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
            return (Fresh(new PdfStream(stream, Flate.Encode(content))), Rebase(written, source));
        }

        /// <summary>
        /// The joined contents of a page with each inline image that <see cref="InlineImageConformance.Breaks"/> replaced by <c>/Name Do</c>, and
        /// the XObjects under those names (names free in the page's resources); null where the page holds no such image or a part cannot be read.
        /// As PDF 2.0 (<paramref name="pdf20"/>) also an image of more than 4 096 bytes, and the images left inline get /L (No. 1131).
        /// </summary>
        private static (byte[] Content, List<(PdfName Name, PdfStream Image)> Images)? PageInlineImageContent(PdfDictionary page, Source source, bool pdf20)
        {
            var parts = page.Get(PdfName.Contents) switch
            {
                PdfArray array => [.. Enumerable.Range(0, Math.Min(array.Count, 10_000)).Select(i => array.Get(i)).OfType<PdfStream>()],
                PdfStream single => new List<PdfStream> { single },
                _ => [],
            };
            if (parts.Count == 0) return null;
            // each part decoded into a rented buffer and tested on its own; joined only where one may hold an inline image — every page passes
            // here, and joining every content in memory cost a fifth more memory over the test collection (No. 936, 997)
            var decoded = new List<PooledBytes>(parts.Count);
            byte[] content;
            try
            {
                var holds = false;
                long count = 0;
                foreach (var part in parts)
                {
                    decoded.Add(source.Doc.DecodeStreamPooled(part));
                    holds |= InlineImageConformance.MayHold(decoded[^1].Span);
                    // beyond the bound of all parts together the page stays as it is, as with a part the display cannot read (No. 1002)
                    count += decoded[^1].Length + 1L;
                    if (count > PdfKnead.Core.Pdf.Content.PageInterpreter.ContentPartsLimit(parts.Count)) return null;
                }
                if (!holds) return null;
                var total = decoded.Sum(d => (long)d.Length + 1);
                if (total > Array.MaxLength) return null;
                content = new byte[total];
                var at = 0;
                foreach (var d in decoded)
                {
                    d.Span.CopyTo(content.AsSpan(at));
                    at += d.Length;
                    content[at++] = (byte)'\n';
                }
            }
            catch (PdfException)
            {
                return null;   // a part the display cannot read: the page stays as it is
            }
            finally
            {
                foreach (var d in decoded) d.Dispose();
            }
            var resources = page.Get(PdfName.Resources) as PdfDictionary ?? InheritedValue(page, PdfName.Resources) as PdfDictionary;
            var images = new List<(PdfName Name, PdfStream Image)>();
            var known = resources?.GetDictionary(XObject);
            var rewritten = InlineImageConformance.Rewrite(content, content.Length, image =>
            {
                var name = InlineImageConformance.FreeName(n => known?.ContainsKey(n) == true || images.Any(x => x.Name == n));
                images.Add((name, InlineImageConformance.AsXObject(image, resources)));
                return name;
            }, pdf20);
            return rewritten is null ? null : (ContentConformance.CanonicalSyntax(rewritten, pdf20) ?? rewritten, images);
        }

        /// <summary>
        /// The resources of a page with only the names its content uses (as <see cref="UsedResources"/> keeps them, not written); the
        /// resources as read where the use is not known for certain.
        /// </summary>
        private PdfDictionary UsedOnly(PageItem item, PdfDictionary resources)
        {
            if (ResourceUsage.UsedNames(item.Source, item.Page) is not { } used) return resources;
            var kept = CloneWithResolver(resources);
            foreach (var category in NamedCategories)
            {
                if (resources.Get(category) is not PdfDictionary entries) continue;
                var names = used.GetValueOrDefault(category);
                var only = new PdfDictionary();
                foreach (var (name, value) in entries.Entries)
                    if (names?.Contains(name) == true || category.Value == "ColorSpace" && name.Value is "DefaultGray" or "DefaultRGB" or "DefaultCMYK")
                        only.Set(name, value);
                if (only.Count > 0) kept.Set(category, only);
                else kept.Remove(category);
            }
            return kept;
        }

        /// <summary>
        /// A form, tiling pattern, soft-mask group or appearance whose content holds an inline image that
        /// <see cref="InlineImageConformance.Breaks"/>: the content with such images as XObjects (see <see cref="PageInlineImages"/>), and the
        /// resources it draws with — its own, or those of what draws it — with the new XObjects, as its own. Null where it holds none, or for a
        /// stream that is no such form (page contents are rewritten with their page; Type 3 glyphs draw with the font's resources and stay).
        /// </summary>
        private (byte[] Content, PdfObject Resources)? FormInlineImages(PdfStream stream, PdfDocument doc, Source source)
        {
            if (!_contentStreams.FormResources(doc, stream, out var resources)) return null;
            // decoded into a rented buffer: every form, pattern and appearance passes here (as in PageInlineImageContent; No. 936, 997)
            PooledBytes decoded;
            try
            {
                decoded = StreamFilters.DecodePooled(doc.GetRawStreamData(stream), stream.Dictionary, out var truncated);
                if (truncated)
                {
                    decoded.Dispose();
                    return null;
                }
            }
            catch (Exception e) when (e is PdfException or InvalidDataException)
            {
                return null;
            }
            var images = new List<(PdfName Name, PdfStream Image)>();
            byte[]? rewritten;
            using (decoded)
            {
                if (!InlineImageConformance.MayHold(decoded.Span)) return null;
                var known = resources?.GetDictionary(XObject);
                rewritten = InlineImageConformance.Rewrite(decoded.Array, decoded.Length, image =>
                {
                    var name = InlineImageConformance.FreeName(n => known?.ContainsKey(n) == true || images.Any(x => x.Name == n));
                    images.Add((name, InlineImageConformance.AsXObject(image, resources)));
                    return name;
                }, Pdf20);
            }
            if (rewritten is null) return null;
            var written = resources is null ? new PdfDictionary() : CloneWithResolver(resources);
            var xobjects = new PdfDictionary();
            if (resources?.GetDictionary(XObject) is { } before)
                foreach (var (key, value) in before.Entries) xobjects.Set(key, value);
            foreach (var (name, image) in images) xobjects.Set(name, image);
            written.Set(XObject, xobjects);
            return (ContentConformance.CanonicalSyntax(rewritten, Pdf20) ?? rewritten, Rebase(written, source));
        }
    }
}
