using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using SkiaSharp;

namespace PdfKnead.Rendering;

/// <summary>
/// Text recognition of a page (KONZEPT.md, section 5.9): the page rendered as displayed on white paper, its text recognised, the lines put
/// into the page's default space — without the words that lie on real text of the page (that is searchable already).
/// </summary>
public static class PageRecognition
{
    /// <summary>The resolution pages are recognised at: small print stays readable, and lines are about as tall as the network was trained on.</summary>
    public const int Dpi = 200;

    /// <summary>Renders a page for the recognition.</summary>
    /// <param name="item">The page.</param>
    /// <param name="list">Its display list (without own elements).</param>
    /// <param name="images">Image cache.</param>
    /// <param name="dpi">Resolution; very large pages get less (<see cref="RasterExport.Scale"/>).</param>
    /// <param name="cancel">Cancels rendering.</param>
    /// <returns>The image, and the matrix from its pixels to the page's default space.</returns>
    public static (OcrImage Image, Matrix ImageToPage) Render(PageItem item, DisplayList list, ImageCache images, int dpi = Dpi, CancellationToken cancel = default)
    {
        var scale = RasterExport.Scale(item.Page, dpi);
        using var bitmap = SkiaRenderer.RenderPage(list, item.Source, item.Page, scale, images, cancel);
        if (item.Elements.Count > 0)
        {
            foreach (var element in item.Elements) ElementAppearance.Final(element);
            using var canvas = new SKCanvas(bitmap);
            ElementOverlay.Draw(canvas, item.Page, item.Elements, scale, images: images);
        }
        // the page lies on opaque paper: premultiplied samples are the colours themselves
        return (new OcrImage(bitmap.Width, bitmap.Height, bitmap.Bytes), PageGeometry.PageToDevice(item.Page, scale).Invert());
    }

    /// <summary>Recognises the text of a page.</summary>
    /// <param name="item">The page.</param>
    /// <param name="recognizer">The recognition.</param>
    /// <param name="images">Image cache.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <param name="dpi">Resolution of the page image.</param>
    /// <param name="reading">Called on the working thread with the lines being read, in the page's default space.</param>
    /// <returns>The lines in page space and the page's area (for <see cref="OcrTextLayer.Form"/>); null if the page shows no image (there is
    /// nothing to read that is not text already).</returns>
    public static (IReadOnlyList<OcrLine> Lines, RectD PageBox)? Recognize(PageItem item, ITextRecognizer recognizer, ImageCache images,
        CancellationToken cancel = default, int dpi = Dpi, Action<IReadOnlyList<RectD>>? reading = null)
    {
        var list = PageInterpreter.Build(item.Source, item.Page, cancel, withoutOwnElements: true);
        if (!OcrTextLayer.ShowsImages(list) && !item.Elements.Any(e => e is ImageElement)) return null;
        var (image, toPage) = Render(item, list, images, dpi, cancel);
        var lines = recognizer.Recognize(image, reading is null ? null : p => reading(Reading(p, toPage)), cancel);
        return (OcrTextLayer.WithoutText(OcrTextLayer.ToPage(lines, toPage), list.Text), list.PageBox);
    }

    private static List<RectD> Reading(OcrProgress progress, Matrix toPage)
    {
        var areas = new List<RectD>(progress.Reading);
        foreach (var corners in progress.Lines.Skip(progress.Done).Take(progress.Reading))
        {
            double left = corners.Min(c => c.X), right = corners.Max(c => c.X), top = corners.Min(c => c.Y), bottom = corners.Max(c => c.Y);
            areas.Add(toPage.TransformBounds(RectD.FromLtrb(left, top, right, bottom)));
        }
        return areas;
    }
}
