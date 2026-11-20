using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Rendering;

/// <summary>
/// Mapping from page space (PDF: origin lower left, y up, points) to device space (pixels, origin top left, y down), including
/// /Rotate (clockwise) and /UserUnit.
/// </summary>
public static class PageGeometry
{
    /// <summary>Matrix from page space (crop box) to device pixels.</summary>
    /// <param name="page">The page.</param>
    /// <param name="pixelsPerPoint">1 = 72 dpi; 96/72 = screen at 100 %.</param>
    /// <returns>The transformation matrix.</returns>
    public static Matrix PageToDevice(PdfPage page, double pixelsPerPoint)
    {
        var box = page.CropBox;
        var s = pixelsPerPoint * page.UserUnit;
        var w = box.Width * s;
        var h = box.Height * s;
        // Unrotated: translate, flip y
        var m = Matrix.Translate(-box.Left, -box.Bottom) * Matrix.Scale(s, -s) * Matrix.Translate(0, h);
        return page.Rotate switch
        {
            90 => m * new Matrix(0, 1, -1, 0, h, 0),
            180 => m * new Matrix(-1, 0, 0, -1, w, h),
            270 => m * new Matrix(0, -1, 1, 0, 0, w),
            _ => m,
        };
    }

    /// <summary>Size of the page in pixels (after rotation), rounded up.</summary>
    /// <param name="page">The page.</param>
    /// <param name="pixelsPerPoint">1 = 72 dpi.</param>
    /// <returns>Width and height in pixels.</returns>
    public static (int Width, int Height) DeviceSize(PdfPage page, double pixelsPerPoint) =>
        ((int)Math.Ceiling(page.Width * pixelsPerPoint), (int)Math.Ceiling(page.Height * pixelsPerPoint));
}
