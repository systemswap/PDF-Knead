using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Pdf.Editing;

/// <summary>
/// Elements at the edge of the page (decided on 8.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 622): readers show and print only what lies
/// in the visible area of a page (its crop box, ISO 32000-2, 14.11.2), so the view shows elements cut there too. The part of an image beyond
/// the edge is invisible but stays in the file, where anyone can take it out — <see cref="CropToPage"/> cuts it off for real.
/// </summary>
public static class PageEdge
{
    /// <summary>Closer than this (points) an element counts as inside (its rectangle is written to a thousandth of a point).</summary>
    private const double Tolerance = 0.01;

    /// <summary>Whether an element reaches beyond the visible area of its page.</summary>
    /// <param name="element">The element (its rectangle in page space).</param>
    /// <param name="visible">The visible area of the page (<see cref="PdfPage.CropBox"/>).</param>
    public static bool Overhangs(PageElement element, PdfRectangle visible)
    {
        var r = element.Rect;
        return r.Left < visible.Left - Tolerance || r.Bottom < visible.Bottom - Tolerance || r.Right > visible.Right + Tolerance || r.Top > visible.Top + Tolerance;
    }

    /// <summary>
    /// The image with the part beyond the visible area of the page cut off: its pixels outside that part leave it, its rectangle shrinks to what
    /// shows — on the page it looks as before. Rotations of 90°, 180°, 270° and the margin of an image with a locked aspect ratio are respected;
    /// the cut runs along whole pixels on the inner side, so nothing is left beyond the edge. Null if nothing reaches beyond the edge, nothing of
    /// the image is on the page, or it carries a line of origin or a digital signature (its appearance belongs to them).
    /// </summary>
    /// <param name="image">The image element.</param>
    /// <param name="visible">The visible area of the page.</param>
    public static ImageElement? CropToPage(ImageElement image, PdfRectangle visible)
    {
        if (!Overhangs(image, visible) || image.Seal is not null || image.Digital is not null) return null;
        var r = image.Rect;
        var shown = new PdfRectangle(Math.Max(r.Left, visible.Left), Math.Max(r.Bottom, visible.Bottom), Math.Min(r.Right, visible.Right), Math.Min(r.Top, visible.Top));
        if (shown.IsEmpty) return null;
        // upright space of the element (the box of its appearance, 0…w × 0…h) and the page: rotated, then moved onto the rectangle
        var (w, h) = image.UprightSize;
        var turn = ElementAppearance.Rotation(image.Rotation);
        var box = turn.TransformBounds(RectD.FromLtrb(0, 0, w, h));
        var toPage = turn * Matrix.Translate(r.Left - box.Left, r.Bottom - box.Top);
        var toUpright = toPage.Invert();
        var u = toUpright.TransformBounds(RectD.FromLtrb(shown.Left, shown.Bottom, shown.Right, shown.Top));
        // where the pixels lie in upright space: the whole box, or fitted and centred with the aspect ratio locked
        var (sw, sh) = ElementAppearance.ShownSize(image);
        double ox = (w - sw) / 2, oy = (h - sh) / 2;
        int width = image.Image.Width, height = image.Image.Height;
        // the pixels inside (rows from the top; y upwards in upright space), whole pixels on the inner side
        var x0 = Math.Clamp((int)Math.Ceiling((u.Left - ox) / sw * width - 1e-6), 0, width);
        var x1 = Math.Clamp((int)Math.Floor((u.Right - ox) / sw * width + 1e-6), 0, width);
        var y0 = Math.Clamp((int)Math.Ceiling((1 - (Math.Max(u.Top, u.Bottom) - oy) / sh) * height - 1e-6), 0, height);
        var y1 = Math.Clamp((int)Math.Floor((1 - (Math.Min(u.Top, u.Bottom) - oy) / sh) * height + 1e-6), 0, height);
        if (x1 - x0 < 1 || y1 - y0 < 1) return null;
        var pixels = new byte[(x1 - x0) * (y1 - y0) * 4];
        for (var y = y0; y < y1; y++)
            Array.Copy(image.Image.Pixels, (y * width + x0) * 4, pixels, (y - y0) * (x1 - x0) * 4, (x1 - x0) * 4);
        // the rectangle of exactly these pixels, in upright space and on the page
        double left = ox + (double)x0 / width * sw, right = ox + (double)x1 / width * sw;
        double top = oy + (1 - (double)y0 / height) * sh, bottom = oy + (1 - (double)y1 / height) * sh;
        var page = toPage.TransformBounds(RectD.FromLtrb(left, bottom, right, top));
        return image with
        {
            Image = new Imaging.RgbaImage(x1 - x0, y1 - y0, pixels),
            Rect = new PdfRectangle(page.Left, Math.Min(page.Top, page.Bottom), page.Right, Math.Max(page.Top, page.Bottom)),
        };
    }
}
