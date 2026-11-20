using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Localization;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Edits of images and drawings (see KONZEPT.md, section 5.20): the page they apply to, and per object the transformation in page space.
/// Always applied to <see cref="Base"/>, so moving an image a hundred times does not nest a hundred blocks.
/// </summary>
/// <param name="Base">The page before the edits.</param>
/// <param name="Moves">Per object: the transformation in page space (moving, scaling, turning), applied after its own placement.</param>
public sealed record ContentObjectEdits(PdfPage Base, IReadOnlyDictionary<ContentKey, Matrix> Moves)
{
    /// <summary>Deleted objects: they leave the content; images leave the file if nothing else draws them.</summary>
    public IReadOnlySet<ContentKey> Deleted { get; init; } = new HashSet<ContentKey>();

    /// <summary>
    /// Objects edited together: they stay one picture even when turned freely, where the tiles no longer lie upright and
    /// <see cref="ContentObjects.GroupOf"/> would not find them again.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<ContentKey>> Groups { get; init; } = [];

    /// <summary>
    /// New images in place of deleted objects <c>Key</c> (they must be in <see cref="Deleted"/>): each drawn where that object's block began,
    /// so its clip does not cut the new one; <c>ToPage</c> places the unit square on the page; <c>Clip</c> (page space) cuts it to exactly
    /// that area if set (a cut image keeps whole pixels). <c>InPlace</c>: drawn at the object's own operator instead, under every clip it had
    /// (an image cut to what can be seen of it looks as before, also in a round frame).
    /// </summary>
    public IReadOnlyList<(ContentKey Key, PdfStream Image, Matrix ToPage, RectD? Clip, bool InPlace)> Replacements { get; init; } = [];

    /// <summary>
    /// The edited objects where the content of <see cref="Base"/> draws them, as far as known: applying the edits needs only these — a plan has
    /// hundreds of thousands of objects, and finding them all again for each step would take a second. Missing ones are found when applying.
    /// </summary>
    public IReadOnlyDictionary<ContentKey, ContentObject> Originals { get; init; } = new Dictionary<ContentKey, ContentObject>();

    /// <summary>
    /// For the forms whose content is edited (<see cref="ContentKey.Scope"/>, outer ones included): the objects in them that reach beyond their
    /// box. A move grows the box of the form (the moved content must not be cut by it); these then keep the old box as a clip of their own,
    /// so that nothing the box hid appears. Missing forms are looked at when applying.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ContentObject>> Overhanging { get; init; } = new Dictionary<string, IReadOnlyList<ContentObject>>();
}

/// <summary>
/// Edits images and drawings of a page — in its content and in the forms it invokes: moving, turning, scaling, deleting, replacing, cutting.
/// An object keeps its place in the drawing order — text that lay above it stays above —, a moved block takes its clip along. The changes are
/// planned per content stream and operator (<see cref="ContentEditPlan"/>) and written by the rewriter of the redaction, which copies a form
/// for the page where something inside it changes.
/// </summary>
public static partial class ContentObjectEditor
{
    /// <summary>
    /// Largest image cut (with its mask, at the finer size of the two): 64 megapixels, 256 MB in memory — a photo of a current camera; cut at
    /// its full size, it keeps its resolution.
    /// </summary>
    public const long MaxCropPixels = 64_000_000;

    /// <summary>
    /// Pixels cut on one page at most — of the images and their masks together, each part once: four images of the largest size. A damaged
    /// page drew a 2 × 2 image three thousand times, each time with its mask of 75 megapixels cut anew to another part, and cleaning it took
    /// minutes (docs/FEHLERGEDAECHTNIS.md, No. 388); the images beyond stay as they are and are counted.
    /// </summary>
    public const long MaxPageCropPixels = 4 * MaxCropPixels;

    /// <summary>What may still be cut on the page (<see cref="MaxPageCropPixels"/>).</summary>
    private sealed class CropBudget
    {
        private long _left = MaxPageCropPixels;

        /// <summary>Takes <paramref name="pixels"/> from the budget, or throws if they are no longer there (the image then stays).</summary>
        public void Take(long pixels)
        {
            if (pixels > _left)
                throw new PdfException(Tr.T("Auf dieser Seite sind zu viele Bildpunkte zuzuschneiden (möglich sind bis zu {0} Megapixel je Seite).",
                    MaxPageCropPixels / 1_000_000));
            _left -= pixels;
        }
    }

    /// <summary>Samples of an image XObject and of its masks (/SMask, a /Mask image).</summary>
    private static long SamplesOf(PdfStream image)
    {
        static long Of(PdfDictionary d) => Math.Clamp(d.GetInteger(PdfName.Get("Width")) ?? 0, 0, int.MaxValue) * Math.Clamp(d.GetInteger(PdfName.Get("Height")) ?? 0, 0, int.MaxValue);
        var total = Of(image.Dictionary);
        foreach (var key in (ReadOnlySpan<string>)["SMask", "Mask"])
            if (image.Dictionary.Get(PdfName.Get(key)) is PdfStream mask) total += Of(mask.Dictionary);
        return total;
    }

    /// <summary>
    /// The page the objects of a page entry are found in: its page before the edits (<see cref="ContentObjectEdits.Base"/>) — the objects found
    /// there serve every later state of the entry, with <see cref="PageObjects.WithEdits"/>.
    /// </summary>
    /// <param name="item">The page entry.</param>
    /// <returns>The page.</returns>
    public static Document.PdfPage BaseOf(PageItem item) => item.ObjectEdits?.Base ?? item.Page;

    /// <summary>The images and drawings of a page entry as they are now (after its edits), in drawing order.</summary>
    /// <param name="item">The page entry.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The objects, with their current place.</returns>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static PageObjects ObjectsOf(PageItem item, CancellationToken cancel = default) =>
        ContentObjects.Find(item.Source, BaseOf(item), cancel).WithEdits(item.ObjectEdits);

    /// <summary>The page entry with objects transformed further by <paramref name="transform"/> (page space).</summary>
    /// <param name="item">The page entry.</param>
    /// <param name="keys">The objects (from <see cref="ObjectsOf"/>).</param>
    /// <param name="transform">The transformation in page space, applied after the current one.</param>
    /// <param name="found">The objects of <paramref name="item"/> if already found (<see cref="ObjectsOf"/>): then the page need not be read again.</param>
    /// <returns>The new entry (new content, new identifier); the same entry if nothing changes.</returns>
    public static PageItem Transform(PageItem item, IEnumerable<ContentKey> keys, Matrix transform, PageObjects? found = null)
    {
        if (!transform.IsFinite || !transform.IsInvertible) return item;
        keys = keys.ToList();
        var edits = Remember(Edits(item), keys, found);
        var moves = new Dictionary<ContentKey, Matrix>(edits.Moves);
        var moved = keys.ToList();
        if (moved.Count == 0) return item;
        foreach (var key in moved) moves[key] = moves.TryGetValue(key, out var current) ? current * transform : transform;
        // edited together: one picture from now on (groups it touches join it)
        var groups = edits.Groups;
        if (moved.Count > 1)
        {
            var joined = new HashSet<ContentKey>(moved);
            var kept = new List<IReadOnlyList<ContentKey>>();
            foreach (var g in groups)
                if (g.Any(joined.Contains)) joined.UnionWith(g);
                else kept.Add(g);
            kept.Add([.. joined]);
            groups = kept;
        }
        return item.WithObjectEdits(edits with { Moves = moves, Groups = groups });
    }

    /// <summary>The page entry without the objects <paramref name="keys"/> (they leave the content).</summary>
    /// <param name="item">The page entry.</param>
    /// <param name="keys">The objects (from <see cref="ObjectsOf"/>).</param>
    /// <param name="found">The objects of <paramref name="item"/> if already found (<see cref="ObjectsOf"/>): then the page need not be read again.</param>
    /// <returns>The new entry; the same entry if nothing changes.</returns>
    public static PageItem Delete(PageItem item, IEnumerable<ContentKey> keys, PageObjects? found = null)
    {
        keys = keys.ToList();
        var edits = Remember(Edits(item), keys, found);
        var deleted = new HashSet<ContentKey>(edits.Deleted);
        var any = false;
        foreach (var key in keys) any |= deleted.Add(key);
        return any ? item.WithObjectEdits(edits with { Deleted = deleted }) : item;
    }

    /// <summary>
    /// The page entry with the objects <paramref name="keys"/> (one picture or graphic) replaced by <paramref name="image"/>, fitted into their
    /// box with its own aspect ratio and centred. The new image takes the place of the first of them in the drawing order; afterwards it is an
    /// ordinary image of the page (the edits start from the new state).
    /// </summary>
    /// <param name="item">The page entry.</param>
    /// <param name="keys">The objects (from <see cref="ObjectsOf"/>).</param>
    /// <param name="image">The new image XObject.</param>
    /// <param name="found">The objects of <paramref name="item"/> if already found (<see cref="ObjectsOf"/>); otherwise they are found here.</param>
    /// <returns>The new entry; the same entry if none of the objects is there.</returns>
    public static PageItem Replace(PageItem item, IReadOnlyList<ContentKey> keys, PdfStream image, PageObjects? found = null)
    {
        found ??= ObjectsOf(item);
        var objects = found.Of(keys);
        int width = (int)(image.Dictionary.GetInteger(PdfName.Get("Width")) ?? 0), height = (int)(image.Dictionary.GetInteger(PdfName.Get("Height")) ?? 0);
        if (objects.Count == 0 || width <= 0 || height <= 0) return item;
        var box = FittedBox(objects.Select(o => o.Visible).Aggregate((a, b) => a.Union(b)), width, height);
        var edits = Remember(Edits(item), keys, found);
        var deleted = new HashSet<ContentKey>(edits.Deleted);
        deleted.UnionWith(objects.Select(o => o.Key));
        var toPage = Matrix.Scale(box.Width, box.Height) * Matrix.Translate(box.Left, box.Top);
        return item.WithObjectEdits(edits with { Deleted = deleted, Replacements = [(objects[0].Key, image, toPage, null, false)] }).Rebased();
    }

    /// <summary>
    /// The page entry with the images <paramref name="keys"/> cut to <paramref name="keep"/> (page space): the pixels outside leave the file —
    /// an image is decoded, cut to the pixels that reach into the kept area and written anew (JPEG as JPEG at about the quality it had, everything
    /// else without loss); an image entirely outside is deleted, one entirely inside stays as it is. Afterwards the new images are ordinary
    /// images of the page. Drawings and stencil masks (their colour comes from the page) cannot be cut.
    /// </summary>
    /// <param name="item">The page entry.</param>
    /// <param name="keys">The images (from <see cref="ObjectsOf"/>).</param>
    /// <param name="keep">The area to keep.</param>
    /// <param name="codec">Codec for decoding and for writing JPEG; without one, JPEG images are written without loss.</param>
    /// <param name="found">The objects of <paramref name="item"/> if already found (<see cref="ObjectsOf"/>); otherwise they are found here.</param>
    /// <returns>The new entry and the new image streams; the same entry and none if nothing changes.</returns>
    /// <exception cref="PdfException">An object is a drawing or a stencil mask, or an image cannot be read.</exception>
    public static (PageItem Item, IReadOnlyList<PdfStream> Images) Crop(PageItem item, IReadOnlyList<ContentKey> keys, RectD keep, IImageCodec? codec,
        PageObjects? found = null)
    {
        found ??= ObjectsOf(item);
        return Cut(item, [.. keys.Select(k => (k, keep))], codec, found, null, inPlace: false);
    }

    /// <summary>
    /// „Dokument bereinigen“ → „Verdeckte Teile von Bildern“ (docs/RECHERCHE-VERSTECKEN.md, B-11): every image of the page cut to the part that can
    /// be seen — within its clips and the visible page —, so that what a frame or the edge of the page hides leaves the file. Looks the same
    /// afterwards. Images that cannot be cut (stencil masks, too large, unreadable) stay as they are and are counted.
    /// </summary>
    /// <param name="item">The page entry.</param>
    /// <param name="page">The visible area of the page (page space).</param>
    /// <param name="codec">Codec for decoding and for writing JPEG.</param>
    /// <param name="cancel">Cancels the work between images.</param>
    /// <param name="keep">Images to leave whole, by their visible part (the places the user keeps); null: none.</param>
    /// <returns>The new entry (the same if nothing is cut), the number of images cut and of those that had to stay.</returns>
    public static (PageItem Item, int Cut, int Skipped) CutToVisible(PageItem item, RectD page, IImageCodec? codec, CancellationToken cancel = default,
        Func<RectD, bool>? keep = null)
    {
        var found = ObjectsOf(item);
        var cuts = PartlyHidden(found, page).Select(i => (found.KeyOf(i), found[i].Visible.Intersect(page))).Where(c => keep?.Invoke(c.Item2) != true).ToList();
        if (cuts.Count == 0) return (item, 0, 0);
        var skipped = new List<ContentKey>();
        var (cut, images) = Cut(item, cuts, codec, found, skipped, inPlace: true, cancel);
        return (cut, images.Count, skipped.Count);
    }

    /// <summary>
    /// The images of <paramref name="found"/> whose pixels reach beyond what can be seen of them (their clips and <paramref name="page"/>) by at
    /// least one whole pixel: what <see cref="CutToVisible"/> cuts. Drawings and images that cannot be seen at all do not count (those go with
    /// „Was niemand sieht“).
    /// </summary>
    /// <param name="found">The objects of a page.</param>
    /// <param name="page">The visible area of the page (page space).</param>
    /// <returns>Their indices.</returns>
    public static List<int> PartlyHidden(PageObjects found, RectD page)
    {
        var list = new List<int>();
        for (var i = 0; i < found.Count; i++)
        {
            var image = found[i];
            if (image.IsDrawing || image.Source is null || image.PixelWidth <= 0 || image.PixelHeight <= 0 || !image.ImageToPage.IsInvertible) continue;
            var kept = image.Visible.Intersect(page);
            if (kept.IsEmpty || kept.Width < 1e-3 || kept.Height < 1e-3) continue;
            var (x0, y0, x1, y1) = Pixels(image.ImageToPage, kept, image.PixelWidth, image.PixelHeight);
            if (x0 > 0 || y0 > 0 || x1 < image.PixelWidth || y1 < image.PixelHeight) list.Add(i);
        }
        return list;
    }

    /// <summary>The pixels of an image of <paramref name="w"/> × <paramref name="h"/> that reach into <paramref name="kept"/> (page space): columns x0 … x1 and rows y0 … y1 (row 0 at the top), end exclusive.</summary>
    private static (int X0, int Y0, int X1, int Y1) Pixels(Matrix imageToPage, RectD kept, int w, int h)
    {
        var (left, top, right, bottom) = Unit(imageToPage, kept);
        return ImageCut.Range(w, h, left, top, right, bottom);
    }

    /// <summary>An area (page space) in the image's unit square: fractions of its width and height, row 0 at the top.</summary>
    private static (double Left, double Top, double Right, double Bottom) Unit(Matrix imageToPage, RectD kept)
    {
        var unit = imageToPage.Invert().TransformBounds(kept);
        return (unit.Left, 1 - unit.Bottom, unit.Right, 1 - unit.Top);
    }

    /// <summary>
    /// Cuts each image to its area (see <see cref="Crop"/>). With <paramref name="skipped"/>, an image that cannot be cut stays and is listed
    /// there; without it, it throws. <paramref name="inPlace"/>: the cut image is drawn under the clips of the old one, otherwise at the start of
    /// its block, cut to the area.
    /// </summary>
    private static (PageItem Item, IReadOnlyList<PdfStream> Images) Cut(PageItem item, IReadOnlyList<(ContentKey Key, RectD Keep)> cuts, IImageCodec? codec,
        PageObjects found, List<ContentKey>? skipped, bool inPlace, CancellationToken cancel = default)
    {
        var keepOf = cuts.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First().Keep);
        var objects = found.Of([.. keepOf.Keys]);
        var edits = Remember(Edits(item), keepOf.Keys, found);
        var deleted = new HashSet<ContentKey>(edits.Deleted);
        var replacements = new List<(ContentKey, PdfStream, Matrix, RectD?, bool)>();
        // one image drawn in many places, each cut to the same part of it: cut once (docs/FEHLERGEDAECHTNIS.md, No. 339)
        var samePart = new Dictionary<(PdfStream, string), PdfStream?>();
        var budget = new CropBudget();
        foreach (var image in objects)
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                if (CutOne(item, image, keepOf[image.Key], codec, deleted, inPlace, samePart, budget) is var (key, stream, toPage, kept))
                    replacements.Add((key, stream, toPage, inPlace ? null : kept, inPlace));
            }
            catch (PdfException) when (skipped is not null)
            {
                skipped.Add(image.Key);
            }
        }
        if (deleted.SetEquals(edits.Deleted)) return (item, []);
        var cropped = item.WithObjectEdits(edits with { Deleted = deleted, Replacements = replacements }).Rebased();
        return (cropped, [.. replacements.Select(r => r.Item2)]);
    }

    /// <summary>One image cut to <paramref name="keep"/>: null if it stays as it is (or is deleted, entirely outside), else its replacement.</summary>
    /// <param name="inPlace">The cut image is drawn where the old one was, in the same state: a stencil mask (its colour comes from there) can be cut too.</param>
    private static (ContentKey Key, PdfStream Image, Matrix ToPage, RectD Kept)? CutOne(PageItem item, ContentObject image, RectD keep, IImageCodec? codec,
        HashSet<ContentKey> deleted, bool inPlace, Dictionary<(PdfStream, string), PdfStream?> samePart, CropBudget budget)
    {
        var visible = image.Visible;
        var kept = visible.Intersect(keep);
        if (kept.IsEmpty || kept.Width < 1e-3 || kept.Height < 1e-3)
        {
            deleted.Add(image.Key);
            return null;
        }
        // entirely inside — also the part its clips hide: nothing of it leaves
        var bounds = image.Bounds;
        if (kept.Left <= bounds.Left + 1e-3 && kept.Top <= bounds.Top + 1e-3 && kept.Right >= bounds.Right - 1e-3 && kept.Bottom >= bounds.Bottom - 1e-3)
            return null;
        if (image.IsDrawing) throw new PdfException(Tr.T("Grafiken lassen sich nicht zuschneiden — nur Bilder."));
        if (image.Source is not { } source || !image.ImageToPage.IsInvertible)
            throw new PdfException(Tr.T("Dieses Bild lässt sich nicht zuschneiden."));
        if (source.IsStencil && !inPlace)
            throw new PdfException(Tr.T("Eine Schablone (einfarbige Maske) lässt sich nicht zuschneiden — ihre Farbe kommt aus der Seite."));
        var doc = item.Source;
        // in its own samples where the filters give them: exactly as it was, in its colour space and bit depth
        if (source.Stream is { } xobject && xobject.Dictionary.GetInteger(PdfName.Get("Width")) is > 0 and <= int.MaxValue and var sw &&
            xobject.Dictionary.GetInteger(PdfName.Get("Height")) is > 0 and <= int.MaxValue and var sh)
        {
            var (left, top, right, bottom) = Unit(image.ImageToPage, kept);
            // a part on whole samples of the image and of its masks: both are cut to exactly it
            (left, top, right, bottom) = ImageCut.Region(xobject, left, top, right, bottom);
            var (sx0, sy0, sx1, sy1) = ImageCut.Range((int)sw, (int)sh, left, top, right, bottom);
            if (sx0 == 0 && sy0 == 0 && sx1 == sw && sy1 == sh) return null;
            var part = (xobject, ImageCut.PartKey(xobject, left, top, right, bottom));
            if (!samePart.TryGetValue(part, out var known))
            {
                budget.Take(SamplesOf(xobject));
                samePart[part] = known = ImageCut.Cut(doc, xobject, source.Resources, left, top, right, bottom) ??
                    ImageCut.CutEncoded(doc, xobject, source.Resources, left, top, right, bottom, codec, ImageInspector.JpegQuality(doc, source) ?? 90);
            }
            if (known is { } samples)
            {
                deleted.Add(image.Key);
                var place = Matrix.Scale((double)(sx1 - sx0) / sw, (double)(sy1 - sy0) / sh) * Matrix.Translate((double)sx0 / sw, 1 - (double)sy1 / sh) * image.ImageToPage;
                return (image.Key, samples, place, kept);
            }
        }
        if (source.IsStencil)
            throw new PdfException(Tr.T("Eine Schablone (einfarbige Maske) lässt sich nicht zuschneiden — ihre Farbe kommt aus der Seite."));
        // at its full size: the part kept keeps its resolution (the display reduces large images); larger ones are refused, not reduced
        if (PdfImageDecoder.FullSize(source) is var size && size > MaxCropPixels)
            throw new PdfException(Tr.T("Dieses Bild ist zum Zuschneiden zu groß ({0} Megapixel, möglich sind bis zu {1}).",
                Math.Round(size / 1e6), MaxCropPixels / 1_000_000));
        budget.Take(size);
        var decoded = PdfImageDecoder.DecodeFull(source, doc, MaxCropPixels) ?? throw new PdfException(Tr.T("Das Bild lässt sich nicht lesen."));
        // the kept area in the image's unit square (row 0 at the top), widened to whole pixels of the decoded image
        int w = decoded.Width, h = decoded.Height;
        var (x0, y0, x1, y1) = Pixels(image.ImageToPage, kept, w, h);
        if (x0 == 0 && y0 == 0 && x1 == w && y1 == h) return null;
        var rgba = decoded.Rgba;
        int cw = x1 - x0, ch = y1 - y0;
        var cut = new byte[cw * ch * 4];
        for (var y = 0; y < ch; y++) Array.Copy(rgba, ((y0 + y) * w + x0) * 4, cut, y * cw * 4, cw * 4);
        var stream = CutStream(doc, source, new Imaging.RgbaImage(cw, ch, cut), codec);
        // the new unit square: the cut pixels where they were
        var toPage = Matrix.Scale((double)cw / w, (double)ch / h) * Matrix.Translate((double)x0 / w, 1 - (double)y1 / h) * image.ImageToPage;
        deleted.Add(image.Key);
        return (image.Key, stream, toPage, kept);
    }

    /// <summary>A cut image: a JPEG stays a JPEG at about the quality it had (90 if unknown) if it has no transparency, otherwise without loss.</summary>
    private static PdfStream CutStream(PdfDocument doc, ImageSource source, Imaging.RgbaImage pixels, IImageCodec? codec)
    {
        var filter = source.Dictionary.Get(PdfName.Filter) switch
        {
            PdfName n => n.Value,
            PdfArray { Count: > 0 } a => (a.Get(a.Count - 1) as PdfName)?.Value,
            _ => null,
        };
        if (filter is "DCTDecode" or "DCT" && codec is not null && !Imaging.ImageTools.HasTransparency(pixels) &&
            codec.EncodeJpeg(pixels.Width, pixels.Height, 4, pixels.Pixels, Math.Clamp(ImageInspector.JpegQuality(doc, source) ?? 90, 50, 100)) is { } jpeg)
        {
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, PdfName.Get("XObject"));
            dict.Set(PdfName.Subtype, PdfName.Get("Image"));
            dict.Set(PdfName.Get("Width"), PdfInteger.Of(pixels.Width));
            dict.Set(PdfName.Get("Height"), PdfInteger.Of(pixels.Height));
            dict.Set(PdfName.Get("ColorSpace"), PdfName.Get("DeviceRGB"));
            dict.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(8));
            dict.Set(PdfName.Filter, PdfName.Get("DCTDecode"));
            return new PdfStream(dict, jpeg);
        }
        return ElementAppearance.ImageStream(pixels);
    }

    /// <summary>The largest box of the aspect ratio <paramref name="width"/> : <paramref name="height"/> inside <paramref name="box"/>, centred.</summary>
    /// <param name="box">The box.</param>
    /// <param name="width">Width of the aspect ratio.</param>
    /// <param name="height">Height of the aspect ratio.</param>
    /// <returns>The fitted box.</returns>
    public static RectD FittedBox(RectD box, int width, int height)
    {
        var scale = Math.Min(box.Width / width, box.Height / height);
        double w = width * scale, h = height * scale;
        double left = box.Left + (box.Width - w) / 2, top = box.Top + (box.Height - h) / 2;
        return RectD.FromLtrb(left, top, left + w, top + h);
    }

    /// <summary>The transformation that turns a box by <paramref name="degrees"/> (anticlockwise on the page) about its centre.</summary>
    /// <param name="box">The box (page space).</param>
    /// <param name="degrees">Angle; negative turns clockwise.</param>
    /// <returns>The transformation in page space.</returns>
    public static Matrix TurnAbout(RectD box, double degrees)
    {
        double cx = (box.Left + box.Right) / 2, cy = (box.Top + box.Bottom) / 2;
        return Matrix.Translate(-cx, -cy) * Matrix.Rotate(degrees) * Matrix.Translate(cx, cy);
    }

    private static ContentObjectEdits Edits(PageItem item) => item.ObjectEdits ?? new ContentObjectEdits(item.Page, new Dictionary<ContentKey, Matrix>());

    /// <summary>
    /// The edits with the objects <paramref name="keys"/> where the content draws them (<see cref="ContentObjectEdits.Originals"/>) and the
    /// objects reaching beyond the boxes of their forms (<see cref="ContentObjectEdits.Overhanging"/>), from <paramref name="found"/>.
    /// </summary>
    private static ContentObjectEdits Remember(ContentObjectEdits edits, IEnumerable<ContentKey> keys, PageObjects? found)
    {
        // only objects found in the same page (another state of the entry would have other operators)
        if (found is null || !ReferenceEquals(found.Page, edits.Base.Dictionary)) return edits;
        var list = keys.ToList();
        var missing = list.Where(k => !edits.Originals.ContainsKey(k)).ToList();
        var forms = list.SelectMany(FormsOnTheWay).Distinct().Where(f => !edits.Overhanging.ContainsKey(f)).ToList();
        if (missing.Count == 0 && forms.Count == 0) return edits;
        var originals = new Dictionary<ContentKey, ContentObject>(edits.Originals);
        foreach (var o in found.Original.Of(missing)) originals[o.Key] = o;
        var overhanging = new Dictionary<string, IReadOnlyList<ContentObject>>(edits.Overhanging);
        foreach (var form in forms) overhanging[form] = OverhangingIn(found.Original, form);
        return edits with { Originals = originals, Overhanging = overhanging };
    }

    /// <summary>The scopes of the forms on the way to a key: "2/7" → "2", "2/7".</summary>
    private static IEnumerable<string> FormsOnTheWay(ContentKey key)
    {
        for (var i = 0; i < key.Scope.Length; i++)
            if (key.Scope[i] == '/') yield return key.Scope[..i];
        if (key.Scope.Length > 0) yield return key.Scope;
    }

    /// <summary>The objects in the content of a form (and the forms it invokes) that reach beyond its box: the box hides part of them.</summary>
    private static List<ContentObject> OverhangingIn(PageObjects original, string form)
    {
        var depth = form.Count(c => c == '/');
        var result = new List<ContentObject>();
        foreach (var i in original.InForm(form))
            if (original[i] is { } o && depth < o.FormBoxes.Count && o.FormBoxes[depth] is { } box && !Inside(o.Bounds, box))
                result.Add(o);
        return result;
    }

    /// <summary>
    /// Whether a rectangle lies inside a convex polygon (corners in order). What cannot be computed — a box that overflowed to infinity in a
    /// damaged file — counts as not inside: the object is treated as reaching beyond the box, the careful case (Fehlergedächtnis 338).
    /// </summary>
    private static bool Inside(RectD r, (double X, double Y)[] polygon)
    {
        foreach (var (x, y) in new[] { (r.Left, r.Top), (r.Right, r.Top), (r.Right, r.Bottom), (r.Left, r.Bottom) })
        {
            int sign = 0;
            for (var k = 0; k < polygon.Length; k++)
            {
                var (a, b) = (polygon[k], polygon[(k + 1) % polygon.Length]);
                var side = (b.X - a.X) * (y - a.Y) - (x - a.X) * (b.Y - a.Y);
                if (double.IsNaN(side)) return false;
                if (Math.Abs(side) < 1e-9) continue;
                var s = Math.Sign(side);
                if (sign == 0) sign = s;
                else if (s != sign) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The page with the edits applied, written by the rewriter of the redaction (<see cref="Redactor.ApplyContentEdits"/>): a copy of its
    /// dictionary with new content and resources.
    /// </summary>
    /// <param name="doc">The document of the page.</param>
    /// <param name="edits">The edits.</param>
    /// <param name="copyUnchanged">Copy what the edits do not touch as it is (false only for comparing in the tests: everything written anew).</param>
    /// <returns>The page dictionary, its resources, and the objects replaced or dropped (they leave the file unless something else draws them).</returns>
    internal static (PdfDictionary Page, PdfDictionary Resources, IReadOnlySet<int> Dropped) Apply(PdfDocument doc, ContentObjectEdits edits, bool copyUnchanged = true) =>
        Redactor.ApplyContentEdits(doc, edits.Base, PlanOf(doc, edits), copyUnchanged);

    /// <summary>
    /// The objects <paramref name="keys"/> as one form XObject (B7, copying as a vector): the content of the page with everything else taken
    /// out — the other images, drawings and shadings and all text —, the edits of the page applied, and only the resources it uses. Its box is
    /// the area of the objects; its space is the page's.
    /// </summary>
    /// <param name="item">The page entry.</param>
    /// <param name="keys">The objects (from <see cref="ObjectsOf"/>).</param>
    /// <param name="found">The objects of <paramref name="item"/> if already found (<see cref="ObjectsOf"/>); otherwise they are found here.</param>
    /// <returns>The form and its box (page space); null if none of the objects is there or they cover no area.</returns>
    public static (PdfStream Form, RectD Box)? Copy(PageItem item, IReadOnlyList<ContentKey> keys, PageObjects? found = null)
    {
        found ??= ObjectsOf(item);
        var chosen = found.Of(keys);
        if (chosen.Count == 0 || chosen.Select(o => o.Visible).Aggregate((a, b) => a.Union(b)) is not { IsEmpty: false } box) return null;
        var edits = Remember(Edits(item), keys, found);
        var root = PlanOf(item.Source, edits);
        foreach (var o in chosen)
        {
            // what to keep in each content on the way: the page's, and that of every form holding a chosen object
            var plan = root;
            plan.Keep ??= [];
            foreach (var form in o.Key.Forms)
            {
                if (!plan.Forms.TryGetValue(form, out var inner)) plan.Forms[form] = inner = new ContentEditPlan();
                plan = inner;
                plan.Keep ??= [];
            }
            plan.Keep.Add(o.Key.Op);
        }
        var (content, resources) = Redactor.CopyContent(item.Source, edits.Base, root);
        // the copy keeps no MCID (docs/ABLAUFPLAN.md, 6.6 d; docs/FEHLERGEDAECHTNIS.md, No. 651): in a form without keys of its own — or pasted
        // into another file — it would name no element or a stranger's; the marked content stays, without its number
        content = Accessibility.MarkedContentIds.WithPropertyLists(content, WithoutMcid);
        if (resources.GetDictionary(PdfName.Get("Properties")) is { } named)
        {
            PdfDictionary? lists = null;
            foreach (var (name, _) in named.Entries.Take(10_000))
                if (named.Get(name) is PdfDictionary list && WithoutMcid(list) is { } plain)
                {
                    lists ??= CopyOf(named);
                    lists.Set(name, plain);
                }
            if (lists is not null) resources.Set(PdfName.Get("Properties"), lists);
        }
        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Get("XObject"));
        dict.Set(PdfName.Subtype, PdfName.Get("Form"));
        dict.Set(PdfName.Get("BBox"), new PdfArray([new PdfReal(box.Left), new PdfReal(box.Top), new PdfReal(box.Right), new PdfReal(box.Bottom)]));
        dict.Set(PdfName.Resources, resources);
        return (new PdfStream(dict, content), box);
    }

    /// <summary>A property list without its MCID; null if it has none.</summary>
    private static PdfDictionary? WithoutMcid(PdfDictionary list)
    {
        if (!list.ContainsKey(PdfName.Get("MCID"))) return null;
        var copy = CopyOf(list);
        copy.Remove(PdfName.Get("MCID"));
        return copy;
    }

    /// <summary>A shallow copy of a dictionary (its entries as they are).</summary>
    private static PdfDictionary CopyOf(PdfDictionary dict)
    {
        var copy = new PdfDictionary(dict.Count) { Resolver = dict.Resolver };
        foreach (var (key, value) in dict.Entries) copy.Set(key, value);
        return copy;
    }

    /// <summary>A page showing only <paramref name="form"/>, its box as the page: a picture of copied objects for other programs.</summary>
    /// <param name="form">The form (from <see cref="Copy"/>).</param>
    /// <param name="box">Its box (page space).</param>
    /// <returns>The page (not part of a document; drawn with the document the form comes from).</returns>
    public static PdfPage PageOf(PdfStream form, RectD box)
    {
        var resources = form.Dictionary.GetDictionary(PdfName.Resources) ?? new PdfDictionary();
        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Get("Page"));
        dict.Set(PdfName.Get("MediaBox"), new PdfArray([new PdfReal(box.Left), new PdfReal(box.Top), new PdfReal(box.Right), new PdfReal(box.Bottom)]));
        dict.Set(PdfName.Contents, form);   // the content of a form is a content stream like a page's
        dict.Set(PdfName.Resources, resources);
        return new PdfPage(0, dict, null, resources, new PdfRectangle(box.Left, box.Top, box.Right, box.Bottom), null, 0);
    }

    /// <summary>
    /// The page entry with <paramref name="form"/> drawn on top of its content, placed by <paramref name="toPage"/> (pasting copied objects,
    /// B7). Afterwards its objects are ordinary objects of the page: the edits start from the new state. A form from another document must be
    /// standalone (<see cref="ObjectCopy.Standalone"/>).
    /// </summary>
    /// <param name="item">The page entry.</param>
    /// <param name="form">The form XObject (from <see cref="Copy"/>).</param>
    /// <param name="toPage">Its matrix on the page.</param>
    /// <returns>The new entry and the number of the operator drawing the form: its objects have it first in <see cref="ContentKey.Forms"/>.</returns>
    public static (PageItem Item, int Op) Insert(PageItem item, PdfStream form, Matrix toPage)
    {
        if (!toPage.IsFinite || !toPage.IsInvertible) throw new ArgumentException(Tr.T("Die Lage ist ungültig."), nameof(toPage));
        var page = item.Page;
        byte[] old;
        using (var content = Redactor.PageContent(item.Source, page.Dictionary)) old = content.Span.ToArray();
        var resources = Redactor.CopyResources(page.Resources);
        var xobjects = new PdfDictionary { Resolver = resources.Resolver };
        if (resources.GetDictionary(PdfName.Get("XObject")) is { } existing)
            foreach (var (key, value) in existing.Entries) xobjects.Set(key, value);
        var name = PdfName.Get("KnObj1");
        for (var i = 2; xobjects.GetRaw(name) is not null; i++) name = PdfName.Get("KnObj" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        xobjects.Set(name, form);
        resources.Set(PdfName.Get("XObject"), xobjects);
        // the old content in q … Q of its own: what it leaves set (a clip, a matrix) does not reach the new object — with as many Q as it
        // leaves open, and without a Q that closes more than it opened, which would end the own q early (docs/FEHLERGEDAECHTNIS.md, No. 148, 383)
        old = Content.StateBalance.WithoutStrayRestores([old])[0] ?? old;
        var (open, close) = Content.StateBalance.Of([old]);
        var count = 0;
        var parser = new Content.ContentParser(old);
        while (parser.Next() is not null) count++;
        var output = new MemoryStream(old.Length + 128);
        for (var i = 0; i < open; i++) output.Write("q\n"u8);
        output.Write(old);
        output.WriteByte((byte)'\n');
        for (var i = 0; i < close; i++) output.Write("Q\n"u8);
        output.Write("q "u8);
        foreach (var v in new[] { toPage.A, toPage.B, toPage.C, toPage.D, toPage.E, toPage.F })
        {
            Writing.PdfSerializer.Write(new PdfReal(v), output);
            output.WriteByte((byte)' ');
        }
        output.Write("cm "u8);
        Writing.PdfSerializer.Write(name, output);
        output.Write(" Do Q\n"u8);
        var dict = new PdfDictionary(page.Dictionary.Count) { Resolver = page.Dictionary.Resolver };
        foreach (var (key, value) in page.Dictionary.Entries)
            if (key != PdfName.Contents && key != PdfName.Resources) dict.Set(key, value);
        dict.Set(PdfName.Contents, new PdfStream(new PdfDictionary(), output.ToArray()));
        dict.Set(PdfName.Resources, resources);
        // the q, the old operators, the Q, q, cm — then the Do
        return (item.WithContent(dict, resources), open + count + close + 2);
    }

    /// <summary>The plan for the content of <see cref="ContentObjectEdits.Base"/> that carries out <paramref name="edits"/>.</summary>
    private static ContentEditPlan PlanOf(PdfDocument doc, ContentObjectEdits edits)
    {
        var root = new ContentEditPlan();
        // only the edited ones: a plan has hundreds of thousands of objects — those remembered, otherwise the page is read
        var keys = edits.Moves.Keys.Concat(edits.Deleted).Distinct().ToList();
        var forms = keys.Where(k => edits.Moves.ContainsKey(k) || edits.Replacements.Any(r => r.Key == k)).SelectMany(FormsOnTheWay).ToHashSet();
        var known = keys.All(edits.Originals.ContainsKey) && forms.All(edits.Overhanging.ContainsKey);
        var found = known ? null : ContentObjects.Find(doc, edits.Base);
        var edited = found is null ? keys.Select(k => edits.Originals[k]) : found.Of(keys);
        var grown = new Dictionary<string, ContentEditPlan>();   // form → its plan (whose Grow says where its box now reaches, form space)
        // a whole graphic moved by one matrix moves its block, clips included (ContentObject.MovedAsGroup)
        edited = edited.ToList();
        var together = new Dictionary<(string Group, Matrix Move), int>();
        foreach (var o in edited)
            if (o.Group is { } g && !edits.Deleted.Contains(o.Key) && edits.Moves.TryGetValue(o.Key, out var gm) && gm != Matrix.Identity && gm.IsFinite)
                together[(g, gm)] = together.GetValueOrDefault((g, gm)) + 1;
        var blocksMoved = new HashSet<(string Scope, int Op)>();
        foreach (var o in edited)
        {
            var deleted = edits.Deleted.Contains(o.Key);
            var moved = edits.Moves.TryGetValue(o.Key, out var d) && d != Matrix.Identity && d.IsFinite;
            if (!deleted && !moved) continue;
            var plan = PlanOf(root, o.Key);
            // where it lies afterwards (page space): each form on the way must hold it
            var lies = deleted
                ? edits.Replacements.Where(r => r.Key == o.Key).Select(r => r.ToPage.TransformBounds(RectD.FromLtrb(0, 0, 1, 1))).DefaultIfEmpty(RectD.Empty).Aggregate((a, b) => a.Union(b))
                : o.Moved(d).Bounds;
            if (!lies.IsEmpty) Hold(root, o, lies, grown);
            // the space a change is made in: at the block's "q" (the object with its clip), otherwise at the operator itself
            var at = o.Block >= 0 ? o.Block : o.Key.Op;
            var ctm = o.BlockMatrix;
            if (deleted)
            {
                if (o.Kind == ContentKind.Path) plan.RemovedPaths.Add(o.Key.Op);   // a clip the path sets stays
                else plan.RemovedOperators.Add(o.Key.Op);
                foreach (var r in edits.Replacements.Where(r => r.Key == o.Key))
                {
                    // in place: at the operator, in its space and under its clips
                    if (r.InPlace) (at, ctm) = (o.Key.Op, o.Ctm);
                    // a CTM that cannot be inverted draws nothing: nothing to put there
                    if (!ctm.IsInvertible || (r.ToPage * ctm.Invert()) is not { IsFinite: true } place) continue;
                    var back = ctm.Invert();
                    var clip = r.Clip is { } c
                        ? new[] { (c.Left, c.Top), (c.Right, c.Top), (c.Right, c.Bottom), (c.Left, c.Bottom) }.Select(p => back.Transform(p.Item1, p.Item2)).ToArray()
                        : null;
                    if (!plan.Inserts.TryGetValue(at, out var list)) plan.Inserts[at] = list = [];
                    list.Add((r.Image, place, clip));
                }
                continue;
            }
            // the whole graphic: its block moves once, with the clips it sets
            if (o.Group is { } group && ContentObject.MovesAsGroup(o, together.GetValueOrDefault((group, d))))
            {
                if (!blocksMoved.Add((o.Key.Scope, o.GroupBlock))) continue;
                (at, ctm) = (o.GroupBlock, o.GroupMatrix);
            }
            // a CTM that cannot be inverted (no area) is left as it is — Invert would give the identity and move it wrongly. (A path that also
            // clips is written twice by the rewriter: its clip stays, its painted shape moves.)
            if (!ctm.IsInvertible || (ctm * d * ctm.Invert()) is not { IsFinite: true } x) continue;
            plan.Transforms[at] = x;
            // a shading fills its clip: cut to the area it showed, so that this area moves (in the space at "sh"; the move comes before)
            if (o.Kind == ContentKind.Shading && o.Ctm.IsInvertible && o.Visible is { IsEmpty: false } shown)
            {
                var back = o.Ctm.Invert();
                if (!plan.ClipsBefore.TryGetValue(o.Key.Op, out var clips)) plan.ClipsBefore[o.Key.Op] = clips = [];
                clips.Add([.. new[] { (shown.Left, shown.Top), (shown.Right, shown.Top), (shown.Right, shown.Bottom), (shown.Left, shown.Bottom) }
                    .Select(p => back.Transform(p.Item1, p.Item2))]);
            }
            // patterns lie in the default space of the stream: the page's, or that of the form the object is in
            var space = o.FormsToPage.Count > 0 ? o.FormsToPage[^1] : Matrix.Identity;
            if (space.IsInvertible && (space * d * space.Invert()) is { IsFinite: true } inSpace) plan.PatternMoves[at] = inSpace;
        }
        // What reaches beyond the box of a form into where it grows keeps the old box as its own clip: the box hid it there (the moved and
        // deleted ones aside). Only there — a second clip on the same edge of a turned box would be drawn twice where nothing changes
        foreach (var (form, plan) in grown)
            foreach (var o in found is null ? edits.Overhanging.GetValueOrDefault(form) ?? [] : OverhangingIn(found, form))
                if (!edits.Moves.ContainsKey(o.Key) && !edits.Deleted.Contains(o.Key) && o.Ctm.IsInvertible && Revealed(o, form, plan.Grow))
                {
                    var back = o.Ctm.Invert();
                    var box = o.FormBoxes[form.Count(c => c == '/')]!;
                    var clips = PlanOf(root, o.Key).ClipsBefore;
                    if (!clips.TryGetValue(o.Key.Op, out var list)) clips[o.Key.Op] = list = [];
                    list.Add([.. box.Select(p => back.Transform(p.X, p.Y))]);
                }
        return root;
    }

    /// <summary>
    /// Whether part of <paramref name="o"/> the old box of <paramref name="form"/> hid lies inside its grown box (the box grows as a rectangle,
    /// so also beside the moved content). Compared in the space of the form, where the box is upright.
    /// </summary>
    private static bool Revealed(ContentObject o, string form, RectD? grow)
    {
        var depth = form.Count(c => c == '/');
        if (grow is not { } added || depth >= o.FormsToPage.Count || o.FormBoxes[depth] is not { } corners || !o.FormsToPage[depth].IsInvertible) return false;
        var back = o.FormsToPage[depth].Invert();
        var box = corners.Select(p => back.Transform(p.X, p.Y)).Aggregate(RectD.Empty,
            (r, p) => r == RectD.Empty ? RectD.FromLtrb(p.X, p.Y, p.X, p.Y) : RectD.FromLtrb(Math.Min(r.Left, p.X), Math.Min(r.Top, p.Y), Math.Max(r.Right, p.X), Math.Max(r.Bottom, p.Y)));
        var reach = back.TransformBounds(o.Bounds).Intersect(box.Union(added));
        const double Allowance = 1e-3;
        return reach.Left < box.Left - Allowance || reach.Top < box.Top - Allowance || reach.Right > box.Right + Allowance || reach.Bottom > box.Bottom + Allowance;
    }

    /// <summary>
    /// Lets the box of each form on the way to <paramref name="o"/> hold <paramref name="area"/> (page space); <paramref name="grown"/>
    /// collects the plans of the forms whose box grows.
    /// </summary>
    private static void Hold(ContentEditPlan root, ContentObject o, RectD area, Dictionary<string, ContentEditPlan> grown)
    {
        var plan = root;
        var i = 0;
        var scope = "";
        foreach (var form in o.Key.Forms)
        {
            if (!plan.Forms.TryGetValue(form, out var inner)) plan.Forms[form] = inner = new ContentEditPlan();
            plan = inner;
            scope = scope.Length == 0 ? form.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{scope}/{form.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            if (i < o.FormsToPage.Count && o.FormsToPage[i] is { IsInvertible: true } toPage && toPage.Invert().TransformBounds(area) is var inForm)
            {
                plan.Grow = plan.Grow is { } held ? held.Union(inForm) : inForm;
                grown[scope] = plan;
            }
            i++;
        }
    }

    /// <summary>The plan of the content stream of <paramref name="key"/>: the page's, or that of a form on the way (created as needed).</summary>
    private static ContentEditPlan PlanOf(ContentEditPlan root, ContentKey key)
    {
        var plan = root;
        foreach (var form in key.Forms)
        {
            if (!plan.Forms.TryGetValue(form, out var inner)) plan.Forms[form] = inner = new ContentEditPlan();
            plan = inner;
        }
        return plan;
    }
}
