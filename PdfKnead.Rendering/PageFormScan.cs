using PdfKnead.Core.Forms.Recognition;
using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Rendering;

/// <summary>
/// What the models add to a page for „Felder erkennen“ (KONZEPT.md, section 5.9a): the page rendered like for the text recognition; on a
/// scan its text is read and its image kept in grey to find the lines; the field detector looks at every page.
/// </summary>
public static class PageFormScan
{
    /// <summary>The result for a page: the part for the recognition, and the lines read (page space) for a text layer.</summary>
    /// <param name="Scan">The part for <see cref="FormRecognizer"/>.</param>
    /// <param name="Lines">The lines read in a scan; empty on a page with real text.</param>
    /// <param name="PageBox">The page's area (for <see cref="OcrTextLayer.Form"/>).</param>
    public sealed record Result(PageScan Scan, IReadOnlyList<OcrLine> Lines, RectD PageBox);

    /// <summary>Looks at a page with the models there are.</summary>
    /// <param name="item">The page.</param>
    /// <param name="recognizer">The text recognition, or null.</param>
    /// <param name="detector">The field detector, or null.</param>
    /// <param name="images">Image cache.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The result; null if there is nothing to add (no image on the page and no detector).</returns>
    public static Result? Look(PageItem item, ITextRecognizer? recognizer, IFieldDetector? detector, ImageCache images, CancellationToken cancel = default)
    {
        var list = PageInterpreter.Build(item.Source, item.Page, cancel, withoutOwnElements: true);
        // a scan: the page shows an image and its text is not real text
        var scanned = OcrTextLayer.ShowsImages(list) && list.Text.Count(g => g.Visible) < 20;
        if (!scanned && detector is null) return null;
        // the page without fields lying on it: the detector should see the paper, not what is filled in already
        var (image, toPage) = PageRecognition.Render(item.WithElements([]), list, images, PageRecognition.Dpi, cancel);
        IReadOnlyList<OcrLine> lines = [];
        ScanImage? gray = null;
        if (scanned)
        {
            if (recognizer is not null) lines = OcrTextLayer.WithoutText(OcrTextLayer.ToPage(recognizer.Recognize(image, cancel), toPage), list.Text);
            gray = new ScanImage(image.Width, image.Height, Gray(image), toPage);
        }
        var fields = new List<PageDetection>();
        if (detector is not null)
            foreach (var d in detector.Detect(image, cancel))
                fields.Add(new PageDetection(toPage.TransformBounds(RectD.FromLtrb(d.Left, d.Top, d.Right, d.Bottom)), d.Kind, d.Score));
        return new Result(new PageScan(lines, gray, fields, detector is not null), lines, list.PageBox);
    }

    /// <summary>The image in grey (the mean of red, green and blue, as on paper).</summary>
    private static byte[] Gray(OcrImage image)
    {
        var gray = new byte[image.Width * image.Height];
        var rgba = image.Rgba;
        for (var i = 0; i < gray.Length; i++) gray[i] = (byte)((rgba[i * 4] + rgba[i * 4 + 1] + rgba[i * 4 + 2]) / 3);
        return gray;
    }
}
