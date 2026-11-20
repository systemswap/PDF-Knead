using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Fonts;

/// <summary>Whether a glyph procedure of a Type 3 font paints anything (docs/FEHLERGEDAECHTNIS.md, No. 489).</summary>
internal sealed partial class Type3Font
{
    /// <summary>Procedures larger than this are taken as painting without reading them.</summary>
    private const int BlankProbeLimit = 64 * 1024;

    private readonly bool?[] _paints = new bool?[256];

    /// <summary>
    /// Whether the glyph procedure of a code paints anything (ISO 32000-2, 9.6.4): a path filled or stroked, a shading, an XObject, text, or
    /// an inline image — except an image mask whose every sample leaves the page as it is (a word processor ends lines with a space glyph
    /// that „draws“ one such pixel). d0/d1, colours, the graphics state and paths without painting do not count. A procedure that cannot be
    /// read, or is too large, paints (the safe answer: it keeps a text block locked). Read once per code.
    /// </summary>
    /// <param name="code">The character code.</param>
    /// <returns>False only if the procedure surely paints nothing (or there is none).</returns>
    internal bool ProcedurePaints(uint code)
    {
        var c = (int)(code & 0xFF);
        return _paints[c] ??= Paints(ProcOf(code));
    }

    private bool Paints(PdfStream? proc)
    {
        if (proc is null) return false;
        try
        {
            var data = _doc.GetStreamData(proc);
            if (data.Length > BlankProbeLimit) return true;
            var parser = new ContentParser(data);
            while (parser.Next() is { } op)
            {
                switch (op)
                {
                    case "f" or "F" or "f*" or "B" or "B*" or "b" or "b*" or "S" or "s" or "sh" or "Do" or "Tj" or "TJ" or "'" or "\"":
                        return true;
                    case "BI" when parser.InlineImage is not { } image || !MasksNothing(image):
                        return true;
                }
            }
            return false;
        }
        catch (Exception e) when (e is PdfException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// An unfiltered image mask (ISO 32000-2, 8.9.6.2) whose samples all leave the page untouched: with the default /Decode [0 1] a sample of
    /// 1 masks, with [1 0] a sample of 0. Rows are padded to whole bytes; the bits beyond the width do not count.
    /// </summary>
    private static bool MasksNothing(InlineImage image)
    {
        var dict = image.Dictionary;
        if (dict.GetBoolean(PdfName.Get("ImageMask")) != true || dict.ContainsKey(PdfName.Get("Filter"))) return false;
        if (dict.GetNumber(PdfName.Get("Width")) is not { } w || dict.GetNumber(PdfName.Get("Height")) is not { } h) return false;
        if (w is < 1 or > 4096 || h is < 1 or > 4096 || dict.GetNumber(PdfName.Get("BitsPerComponent")) is { } bpc && bpc != 1) return false;
        var inverted = dict.GetArray(PdfName.Get("Decode")) is { Count: 2 } decode && decode.GetNumber(0) == 1 && decode.GetNumber(1) == 0;
        var untouched = inverted ? 0 : 1;
        int width = (int)w, height = (int)h, rowBytes = (width + 7) / 8;
        var data = image.Data;
        if (data.Length < rowBytes * height) return false;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (((data[y * rowBytes + x / 8] >> (7 - x % 8)) & 1) != untouched) return false;
        return true;
    }
}
