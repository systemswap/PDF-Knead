namespace PdfKnead.Core.Ocr;

/// <summary>An image to recognise text in: 8-bit samples, RGBA row by row without padding (a rendered page).</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Rgba">The samples, 4 bytes per pixel.</param>
public sealed record OcrImage(int Width, int Height, byte[] Rgba);

/// <summary>A point in the pixels of the <see cref="OcrImage"/> (y downwards).</summary>
/// <param name="X">X.</param>
/// <param name="Y">Y.</param>
public readonly record struct OcrPoint(double X, double Y);

/// <summary>
/// A recognised word: its text and its box as a quadrilateral in reading order (top left, top right, bottom right, bottom left), in the pixels
/// of the image — turned with the line it belongs to.
/// </summary>
/// <param name="Text">The text (no spaces).</param>
/// <param name="Corners">The four corners.</param>
public sealed record OcrWord(string Text, OcrPoint[] Corners);

/// <summary>A recognised line: its words in reading order, and how sure the recognition is (0 … 1, mean of its characters).</summary>
/// <param name="Words">The words.</param>
/// <param name="Confidence">The confidence.</param>
public sealed record OcrLine(IReadOnlyList<OcrWord> Words, double Confidence)
{
    /// <summary>The words joined by spaces.</summary>
    public string Text => string.Join(' ', Words.Select(w => w.Text));
}

/// <summary>How far the recognition of an image has come, to show it.</summary>
/// <param name="Lines">The lines found, as quadrilaterals in the pixels of the image, in reading order.</param>
/// <param name="Done">How many of them are read.</param>
/// <param name="Reading">How many are being read now.</param>
public sealed record OcrProgress(IReadOnlyList<OcrPoint[]> Lines, int Done, int Reading);

/// <summary>
/// Recognises text in an image (KONZEPT.md, section 5.9): where text lies, and what it says, word by word with its box. Implemented with
/// PP-OCRv6 in PdfKnead.Ai; the core only knows this interface.
/// </summary>
public interface ITextRecognizer : IDisposable
{
    /// <summary>Recognises the text of an image.</summary>
    /// <param name="image">The image (a page rendered at about 200–300 dpi).</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The lines, top to bottom; empty if there is no text.</returns>
    IReadOnlyList<OcrLine> Recognize(OcrImage image, CancellationToken cancel = default);

    /// <summary>Recognises the text of an image and says how far it has come.</summary>
    /// <param name="image">The image.</param>
    /// <param name="progress">Called on the working thread; a recognition that cannot say simply does not call it.</param>
    /// <param name="cancel">Cancels the work.</param>
    /// <returns>The lines, top to bottom; empty if there is no text.</returns>
    IReadOnlyList<OcrLine> Recognize(OcrImage image, Action<OcrProgress>? progress, CancellationToken cancel = default) => Recognize(image, cancel);
}

/// <summary>The text recognition could not be started: its models are damaged or incomplete, or its runtime library is missing.</summary>
/// <param name="message">What went wrong (not translated; for the log).</param>
/// <param name="inner">The cause.</param>
public sealed class TextRecognitionException(string message, Exception? inner = null) : Exception(message, inner);
