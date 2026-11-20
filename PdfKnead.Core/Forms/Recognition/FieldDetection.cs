using PdfKnead.Core.Ocr;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Forms.Recognition;

/// <summary>What the field detector saw: a field to write in, a box to tick or a field to sign.</summary>
public enum DetectedFieldKind
{
    /// <summary>A text field.</summary>
    Text,

    /// <summary>A check box or option button.</summary>
    Choice,

    /// <summary>A signature field.</summary>
    Signature,
}

/// <summary>A field the detector found in an image.</summary>
/// <param name="Left">Left edge in the pixels of the image.</param>
/// <param name="Top">Upper edge (y downwards).</param>
/// <param name="Right">Right edge.</param>
/// <param name="Bottom">Lower edge.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Score">How sure the detector is (0 … 1).</param>
public sealed record DetectedField(double Left, double Top, double Right, double Bottom, DetectedFieldKind Kind, double Score);

/// <summary>
/// Finds form fields in the image of a page (KONZEPT.md, section 5.9a, step 4): implemented with FFDetr in PdfKnead.Ai; the core only knows
/// this interface, the recognition works without it.
/// </summary>
public interface IFieldDetector : IDisposable
{
    /// <summary>Finds the fields of a page image.</summary>
    /// <param name="image">The page rendered at about 200 dpi.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The fields; empty if there are none.</returns>
    IReadOnlyList<DetectedField> Detect(OcrImage image, CancellationToken cancel = default);
}

/// <summary>A page image in grey for finding lines and boxes in scans: one byte per pixel, rows without padding (y downwards).</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Gray">The samples (0 black … 255 white).</param>
/// <param name="ImageToPage">From the pixels to the page's default space.</param>
public sealed record ScanImage(int Width, int Height, byte[] Gray, Matrix ImageToPage);

/// <summary>A field of the detector in the page's default space.</summary>
/// <param name="Box">Where it lies.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Score">How sure the detector is.</param>
public sealed record PageDetection(RectD Box, DetectedFieldKind Kind, double Score);

/// <summary>
/// What the models and the page image add to a page (KONZEPT.md, section 5.9a): the text recognised on a scan, the scan itself to find its
/// lines and boxes, and the fields of the detector — each may be missing.
/// </summary>
/// <param name="Lines">The lines read in the scan, in page space (without words on real text of the page).</param>
/// <param name="Image">The page image of a scan; null for a page with real lines (they are taken from its content).</param>
/// <param name="Fields">The fields of the detector in page space.</param>
/// <param name="Detected">Whether the detector looked at the page (then what it did not see is less certain).</param>
public sealed record PageScan(IReadOnlyList<OcrLine> Lines, ScanImage? Image, IReadOnlyList<PageDetection> Fields, bool Detected);
