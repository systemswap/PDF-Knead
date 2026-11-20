using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>An area of a page as pixels: RGB with 8 bits per channel on white paper (no alpha), row by row from the top.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Rgb">Three bytes per pixel.</param>
public sealed record RasterArea(int Width, int Height, byte[] Rgb);

/// <summary>
/// Draws an area of a page as the display shows it (KONZEPT.md, section 5.21, PDF/A-1: transparency flattened). Implemented by the
/// renderer; the core library only chooses what to draw (<see cref="PdfAFlattening"/>).
/// </summary>
public interface IAreaRasterizer
{
    /// <summary>Draws the area of the page.</summary>
    /// <param name="list">Display list of the page.</param>
    /// <param name="doc">The document the list belongs to.</param>
    /// <param name="page">The page.</param>
    /// <param name="area">The area in the default space of the page (y upwards, <see cref="RectD.Top"/> the smaller y).</param>
    /// <param name="pixelsPerPoint">Resolution (1 = 72 dpi).</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The pixels, the area's width and height in points times <paramref name="pixelsPerPoint"/> rounded up; null if the area is
    /// too large to draw.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/> was triggered.</exception>
    RasterArea? Render(DisplayList list, PdfDocument doc, PdfPage page, RectD area, double pixelsPerPoint, CancellationToken cancel);
}

/// <summary>
/// Transparency flattened for PDF/A-1 (ISO 19005-1, 6.4, which allows none; decision of 31.10.2026: „exakt, sonst Bild“): where
/// something on a page is drawn translucent — an opacity below 1, a blend mode other than Normal, a soft mask, a translucent group, an image
/// with partial opacity — the page gets, over its content, that area as the display shows it, as an image of 300 dpi; underneath, the content
/// stays as it is (its text can still be searched and copied there), drawn opaque (<see cref="PdfSaveOptions.PdfAFlattened"/>). An annotation
/// that draws something translucent keeps its entry with an empty appearance: what it showed is in the image.
/// </summary>
public static class PdfAFlattening
{
    /// <summary>Draws the areas; null: transparency is not flattened (the check names it). Set by the program, the command line and the tests.</summary>
    public static IAreaRasterizer? Rasterizer { get; set; }

    /// <summary>Pixels per point of the images (300 dpi; less on very large pages, <see cref="MaxPixels"/>).</summary>
    public const double PixelsPerPoint = 300 / 72.0;

    /// <summary>Most pixels per page; beyond it the resolution drops (a whole A0 page at 300 dpi would be 139 million).</summary>
    private const double MaxPixels = 50_000_000;

    /// <summary>At most this many areas per page; beyond it they become one around all.</summary>
    private const int MaxAreas = 32;

    /// <summary>Areas covering more than this share of the page become one around all (fewer edges, about the same pixels).</summary>
    private const double JoinShare = 0.5;

    /// <summary>What a flattened copy gets: per page (index) the form with its images, and the annotations whose look is in them.</summary>
    /// <param name="Overlays">Per page the form drawn over its content (<see cref="PdfSaveOptions.Overlays"/>).</param>
    /// <param name="Annotations">The annotations (as read from the copy) written with an empty appearance.</param>
    internal sealed record Plan(IReadOnlyDictionary<int, IReadOnlyList<PdfStream>> Overlays, IReadOnlySet<PdfDictionary> Annotations);

    /// <summary>A plan without images: nothing on any page is translucent.</summary>
    internal static readonly Plan Nothing = new(new Dictionary<int, IReadOnlyList<PdfStream>>(), new HashSet<PdfDictionary>());

    /// <summary>The areas to flatten on each page of a PDF/A-1 copy, drawn; null if nothing on any page is translucent.</summary>
    /// <param name="copy">The copy as written without flattening (it looks as the original is shown).</param>
    /// <param name="rasterizer">Draws the areas.</param>
    /// <param name="cancel">Cancels the work.</param>
    internal static Plan? For(PdfDocument copy, IAreaRasterizer rasterizer, CancellationToken cancel)
    {
        var overlays = new Dictionary<int, IReadOnlyList<PdfStream>>();
        var annotations = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        PdfStream? srgb = null;
        for (var i = 0; i < copy.PageCount; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var page = copy.Pages[i];
            var list = PageInterpreter.Build(copy, page, cancel);
            var areas = Areas(TranslucentBounds(list, copy), page);
            if (areas.Count == 0) continue;
            if (page.Dictionary.GetArray(PdfName.Get("Annots")) is { } annots)
                for (var k = 0; k < annots.Count && k < 100_000; k++)
                    if (annots.Get(k) is PdfDictionary annot && Translucent(copy, page, annot)) annotations.Add(annot);
            srgb ??= new PdfStream(Dict(("N", PdfInteger.Of(3))), CalibratedProfile.Srgb());
            if (Overlay(list, copy, page, areas, rasterizer, srgb, cancel) is { } form) overlays[i] = [form];
        }
        return overlays.Count == 0 ? null : new Plan(overlays, annotations);
    }

    /// <summary>
    /// The bounds of what a display list draws translucent (page space): entries with an opacity below 1 or a blend mode other than Normal,
    /// images whose soft mask has values between 0 and 255 (an opaque or only showing-and-hiding mask is written without loss), groups with
    /// an opacity, blend mode or soft mask of their own. Content that cannot be seen is left out.
    /// </summary>
    internal static List<RectD> TranslucentBounds(DisplayList list, PdfDocument doc)
    {
        var bounds = new List<RectD>();
        var items = list.Items.Span;
        var masks = new Dictionary<PdfStream, bool>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < items.Length; i++)
        {
            if (list.IsHidden(i)) continue;
            ref readonly var item = ref items[i];
            var paint = list.Paints[item.Paint];
            var translucent = paint.BlendMode != 0 || item.Kind switch
            {
                DisplayItemKind.Fill => paint.Fill >> 24 < 255,
                DisplayItemKind.Stroke => paint.Stroke >> 24 < 255,
                DisplayItemKind.FillStroke => paint.Fill >> 24 < 255 || paint.Stroke >> 24 < 255,
                DisplayItemKind.Image => TranslucentImage(list.Images[item.Image], doc, masks),
                _ => false,
            };
            if (translucent) bounds.Add(RectD.FromLtrb(item.Left, item.Top, item.Right, item.Bottom));
        }
        foreach (var group in list.Groups)
        {
            if (group.Alpha == 255 && group.BlendMode == 0 && group.Mask < 0) continue;
            var area = group.Bounds;
            if (area.IsEmpty)
                for (var k = group.Start; k < group.End && k < items.Length; k++)
                    area = area.Union(RectD.FromLtrb(items[k].Left, items[k].Top, items[k].Right, items[k].Bottom));
            bounds.Add(area);
        }
        return bounds;
    }

    /// <summary>An image drawn with partial opacity: a stencil mask in a translucent colour, a soft mask with values between 0 and 255, alpha in the JPEG 2000 data.</summary>
    private static bool TranslucentImage(ImageSource image, PdfDocument doc, Dictionary<PdfStream, bool> masks)
    {
        if (image.IsStencil) return image.StencilColor >> 24 < 255;
        var dict = image.Dictionary;
        if (dict.GetInteger(PdfName.Get("SMaskInData")) is > 0) return true;
        if (dict.Get(PdfName.Get("SMask")) is not PdfStream mask) return false;
        if (masks.TryGetValue(mask, out var known)) return known;
        // a mask that cannot be read draws as the display does: counted as translucent, so that the pixels show it
        return masks[mask] = PdfImageDecoder.MaskOpacity(mask, doc) is not var (_, _, alpha) || alpha.Any(a => a is not (0 or 255));
    }

    /// <summary>
    /// An annotation that draws something translucent in its appearance. (Its /CA the display, like widely used readers, does not apply
    /// to an appearance of its own; PDF/A-1 writes it without, PdfAAnnotation.)
    /// </summary>
    private static bool Translucent(PdfDocument doc, PdfPage page, PdfDictionary annot)
    {
        var list = PageInterpreter.BuildAnnotations(doc, page, [annot]);
        return TranslucentBounds(list, doc).Count > 0;
    }

    /// <summary>
    /// The areas to draw as pixels: the bounds a little larger (anti-aliasing at their edges), overlapping ones joined, within the crop box;
    /// many, or most of the page, become one.
    /// </summary>
    internal static List<RectD> Areas(List<RectD> bounds, PdfPage page)
    {
        var crop = page.CropBox;
        var box = RectD.FromLtrb(crop.Left, crop.Bottom, crop.Right, crop.Top);
        var areas = new List<RectD>();
        foreach (var b in bounds)
        {
            var r = b.Inflate(1).Intersect(box);
            if (!r.IsEmpty && double.IsFinite(r.Width) && double.IsFinite(r.Height)) areas.Add(r);
        }
        // join what overlaps, until nothing does
        for (var joined = true; joined;)
        {
            joined = false;
            for (var i = 0; i < areas.Count && !joined; i++)
                for (var k = i + 1; k < areas.Count; k++)
                    if (areas[i].Intersects(areas[k]))
                    {
                        areas[i] = areas[i].Union(areas[k]);
                        areas.RemoveAt(k);
                        joined = true;
                        break;
                    }
        }
        if (areas.Count == 0) return areas;
        var total = areas.Sum(a => a.Width * a.Height);
        if (areas.Count > MaxAreas || total > JoinShare * box.Width * box.Height)
        {
            var all = areas.Aggregate((a, b) => a.Union(b));
            areas = [all];
        }
        return areas;
    }

    /// <summary>
    /// The form drawn over the page's content (in its default space): each area as an image of what the display shows there, placed exactly
    /// on its area. Null if no area could be drawn.
    /// </summary>
    private static PdfStream? Overlay(DisplayList list, PdfDocument doc, PdfPage page, List<RectD> areas, IAreaRasterizer rasterizer, PdfStream srgb,
        CancellationToken cancel)
    {
        var images = new PdfDictionary();
        var content = new StringBuilder();
        // 300 dpi, less where the areas together would exceed the pixel budget (a large plan)
        var total = areas.Sum(a => a.Width * a.Height);
        var scale = Math.Min(PixelsPerPoint, Math.Sqrt(MaxPixels / Math.Max(total, 1)));
        foreach (var area in areas)
        {
            if (rasterizer.Render(list, doc, page, area, scale, cancel) is not { } pixels || pixels.Width <= 0 || pixels.Height <= 0) continue;
            var image = new PdfDictionary();
            image.Set(PdfName.Type, PdfName.Get("XObject"));
            image.Set(PdfName.Subtype, PdfName.Get("Image"));
            image.Set(PdfName.Get("Width"), PdfInteger.Of(pixels.Width));
            image.Set(PdfName.Get("Height"), PdfInteger.Of(pixels.Height));
            image.Set(PdfName.Get("ColorSpace"), new PdfArray([PdfName.Get("ICCBased"), srgb]));
            image.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(8));
            image.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
            var name = $"PDFKneadFlat{images.Count}";
            images.Set(PdfName.Get(name), new PdfStream(image, Flate.Encode(pixels.Rgb)));
            // the image as large as its pixels at the resolution: its top left corner on that of the area
            var width = pixels.Width / scale;
            var height = pixels.Height / scale;
            content.Append(CultureInfo.InvariantCulture,
                $"q {Number(width)} 0 0 {Number(height)} {Number(area.Left)} {Number(area.Bottom - height)} cm /{name} Do Q\n");
        }
        if (images.Count == 0) return null;
        var crop = page.CropBox;
        var form = new PdfDictionary();
        form.Set(PdfName.Type, PdfName.Get("XObject"));
        form.Set(PdfName.Subtype, PdfName.Get("Form"));
        form.Set(PdfName.Get("BBox"), new PdfArray([new PdfReal(crop.Left), new PdfReal(crop.Bottom), new PdfReal(crop.Right), new PdfReal(crop.Top)]));
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("XObject"), images);
        form.Set(PdfName.Resources, resources);
        return new PdfStream(form, Encoding.ASCII.GetBytes(content.ToString()));
    }

    private static string Number(double value) => PdfSerializer.FormatReal(value);

    private static PdfDictionary Dict(params (string Key, PdfObject Value)[] entries)
    {
        var dict = new PdfDictionary();
        foreach (var (key, value) in entries) dict.Set(PdfName.Get(key), value);
        return dict;
    }
}
