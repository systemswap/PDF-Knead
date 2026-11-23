using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Inline images the display reads only with a tolerance, written as image XObjects when a copy is saved — without loss, the image data as
/// stored (decided on 20.11.2026; docs/normabgleich/sonderregeln.md, S-A A12, A13, A86, A110; docs/FEHLERGEDAECHTNIS.md, No. 993):
/// <list type="bullet">
/// <item>with /JPXDecode or /JBIG2Decode, which ISO 32000-2 does not allow in an inline image (7.4.9, 7.4.7);</item>
/// <item>ASCII85 data without its end „~>“ (7.4.3: the end of the data);</item>
/// <item>data that holds „EI“ between white-space — where an inline image ends is then a guess (8.9.7) — the display found the end, other readers
/// may not.</item>
/// </list>
/// The XObject gets the dictionary with the full keys (<see cref="InlineImage.AsXObjectDictionary"/>), a colour space named in the resources
/// replaced by the space itself, and the data without the ASCII filters in front (only there to keep inline data text), otherwise as the parser
/// reads them (filtered data without /L up to the white-space before „EI“, which a decoder passes over); in the content the
/// inline image becomes <c>/Name Do</c> — an image XObject is drawn in the same unit square of the same matrix (8.9.5, 8.9.7).
/// </summary>
internal static class InlineImageConformance
{
    private static readonly PdfName ColorSpaceKey = PdfName.Get("ColorSpace"), FilterKey = PdfName.Get("Filter"), ParmsKey = PdfName.Get("DecodeParms");

    /// <summary>
    /// Whether a content may hold an inline image at all (cheap test before parsing): „BI“ as a word of its own — the parser of the display
    /// reads an operator from a letter up to the next white-space or delimiter, so an inline image begins only there. A content in parts is
    /// joined with a line end between them (7.8.2), so the operator lies wholly in one part: testing the parts one by one finds it.
    /// </summary>
    public static bool MayHold(ReadOnlySpan<byte> content)
    {
        for (var from = 0; from + 2 <= content.Length;)
        {
            var at = content[from..].IndexOf("BI"u8);
            if (at < 0) return false;
            at += from;
            if ((at == 0 || !Parsing.PdfLexer.IsRegular(content[at - 1])) && (at + 2 == content.Length || !Parsing.PdfLexer.IsRegular(content[at + 2])))
                return true;
            from = at + 1;
        }
        return false;
    }

    /// <summary>Whether the inline image is one to write as an XObject (see the class).</summary>
    public static bool Breaks(InlineImage image)
    {
        var filters = Filters(image.AsXObjectDictionary());
        if (filters.Any(f => f is "JPXDecode" or "JBIG2Decode")) return true;
        var data = image.Data.AsSpan();
        if (filters is ["ASCII85Decode", ..] && !data.TrimEnd(" \t\r\n\f\0"u8).EndsWith("~>"u8)) return true;
        for (var i = 1; i + 2 < data.Length; i++)
            if (data[i] == 'E' && data[i + 1] == 'I' && White(data[i - 1]) && White(data[i + 2])) return true;
        return false;
    }

    private static bool White(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static List<string> Filters(PdfDictionary dict) => dict.Get(FilterKey) switch
    {
        PdfName name => [name.Value],
        PdfArray array => [.. Enumerable.Range(0, array.Count).Select(i => (array[i] as PdfName)?.Value ?? "")],
        _ => [],
    };

    /// <summary>
    /// The image XObject for an inline image drawn with <paramref name="resources"/>: the dictionary as for an XObject, a colour space named
    /// in the resources replaced by the space itself (an XObject reads no resource names there), the ASCII filters in front decoded.
    /// </summary>
    public static PdfStream AsXObject(InlineImage image, PdfDictionary? resources)
    {
        var dict = image.AsXObjectDictionary();
        if (dict.Get(ColorSpaceKey) is PdfName { Value: not ("DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Indexed" or "Pattern") } name &&
            resources?.GetDictionary(ColorSpaceKey)?.Get(name) is { } named and not PdfNull)
            dict.Set(ColorSpaceKey, named);
        var data = image.Data;
        while (Filters(dict) is [var first and ("ASCII85Decode" or "ASCIIHexDecode"), ..])
        {
            var truncated = false;
            data = first == "ASCII85Decode" ? Ascii85.Decode(data, ref truncated) : AsciiHex.Decode(data, ref truncated);
            RemoveFirst(dict, FilterKey);
            RemoveFirst(dict, ParmsKey);
        }
        return new PdfStream(dict, data);
    }

    private static void RemoveFirst(PdfDictionary dict, PdfName key)
    {
        switch (dict.Get(key))
        {
            case PdfArray { Count: > 1 } array:
                dict.Set(key, new PdfArray(array.Skip(1)));
                break;
            default:
                dict.Remove(key);
                break;
        }
    }

    /// <summary>
    /// The content with each inline image that <see cref="Breaks"/> replaced by <c>/Name Do</c>, the name from <paramref name="place"/> (which
    /// keeps the XObject); everything else byte for byte. Null if the content holds no such inline image.
    /// </summary>
    public static byte[]? Rewrite(byte[] content, int length, Func<InlineImage, PdfName> place)
    {
        length = Math.Clamp(length, 0, content.Length);
        if (!MayHold(content.AsSpan(0, length))) return null;
        var found = new List<(int Start, int End, PdfName Name)>();
        var parser = new ContentParser(content, length);
        var before = 0;
        while (parser.Next() is { } op)
        {
            if (op == "BI" && parser.InlineImage is { } image && Breaks(image))
            {
                var start = content.AsSpan(before, parser.Position - before).IndexOf("BI"u8);
                if (start >= 0) found.Add((before + start, parser.Position, place(image)));
            }
            before = parser.Position;
        }
        if (found.Count == 0) return null;
        var output = new MemoryStream(length + 16 * found.Count);
        var from = 0;
        foreach (var (start, end, name) in found)
        {
            output.Write(content, from, start - from);
            output.Write(" "u8);
            output.Write(PdfSerializer.ToBytes(name));
            output.Write(" Do "u8);
            from = end;
        }
        output.Write(content, from, length - from);
        return output.ToArray();
    }

    /// <summary>A name for an XObject not yet taken (<paramref name="taken"/>) in the resources.</summary>
    public static PdfName FreeName(Func<PdfName, bool> taken)
    {
        for (var k = 1; ; k++)
        {
            var candidate = PdfName.Get("PDFKneadIm" + k);
            if (!taken(candidate)) return candidate;
        }
    }
}
