using PdfKnead.Core.Barcodes;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using SkiaSharp;

namespace PdfKnead.Rendering;

/// <summary>
/// Self-check of an inserted code (KONZEPT.md, section 5.19): the element is drawn exactly as on the page — through its appearance, the
/// interpreter and the renderer — on white paper, at about four pixels per module, and read back with the own reader. Catches whatever
/// would make it unreadable in print: colours too light, a background too dark, a missing quiet zone, a rectangle too small.
/// </summary>
public static class CodeCheck
{
    /// <summary>Largest image drawn for the check.</summary>
    private const long MaxPixels = 6_000_000;

    /// <summary>Draws the element and reads it.</summary>
    /// <param name="code">The element.</param>
    /// <param name="cancel">Cancels the check.</param>
    /// <returns>What was read; null if nothing could be read (or the content gives no symbol).</returns>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public static FoundBarcode? Read(CodeElement code, CancellationToken cancel = default)
    {
        if (ElementAppearance.CodeSymbolOf(code).Symbol is null) return null;
        var modulePoints = ElementAppearance.CodeModuleMm(code) * 72 / 25.4;
        if (modulePoints <= 0) return null;
        var rect = code.Rect;
        var scale = Math.Clamp(4 / modulePoints, 0.5, 16);
        if ((rect.Width * scale + 40) * (rect.Height * scale + 40) > MaxPixels)
            scale = Math.Sqrt(MaxPixels / ((rect.Width + 40 / scale) * (rect.Height + 40 / scale)));
        // white paper around the element, as on a page without anything beside it
        const int Margin = 20;
        int width = (int)Math.Ceiling(rect.Width * scale) + 2 * Margin, height = (int)Math.Ceiling(rect.Height * scale) + 2 * Margin;
        cancel.ThrowIfCancellationRequested();
        using var bitmap = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            var toCanvas = new Matrix(scale, 0, 0, -scale, -rect.Left * scale + Margin, rect.Top * scale + Margin);
            ElementOverlay.Draw(canvas, ElementAppearance.Document.Pages[0], [code], toCanvas);
        }
        cancel.ThrowIfCancellationRequested();
        var pixels = bitmap.GetPixelSpan().ToArray();
        // Gray8 rows may be padded
        var rowBytes = bitmap.RowBytes;
        if (rowBytes != width)
        {
            var packed = new byte[width * height];
            for (var y = 0; y < height; y++) Array.Copy(pixels, y * rowBytes, packed, y * width, width);
            pixels = packed;
        }
        return BarcodeScanner.ReadAt(new GrayImage(width, height, pixels), width / 2.0, height / 2.0);
    }
}
