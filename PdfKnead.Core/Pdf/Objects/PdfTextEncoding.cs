using System.Text;

namespace PdfKnead.Core.Pdf.Objects;

/// <summary>
/// Text strings (title, author, bookmarks …) according to ISO 32000-2, 7.9.2.2: UTF-16BE with the byte order mark
/// FE FF, UTF-8 with EF BB BF (PDF 2.0), otherwise PDFDocEncoding.
/// </summary>
public static class PdfTextEncoding
{
    /// <summary>PDFDocEncoding → Unicode. Bytes without an entry are read as Latin-1.</summary>
    private static readonly Dictionary<byte, char> DocEncoding = new()
    {
        [0x18] = '˘', [0x19] = 'ˇ', [0x1A] = 'ˆ', [0x1B] = '˙',
        [0x1C] = '˝', [0x1D] = '˛', [0x1E] = '˚', [0x1F] = '˜',
        [0x80] = '•', [0x81] = '†', [0x82] = '‡', [0x83] = '…',
        [0x84] = '—', [0x85] = '–', [0x86] = 'ƒ', [0x87] = '⁄',
        [0x88] = '‹', [0x89] = '›', [0x8A] = '−', [0x8B] = '‰',
        [0x8C] = '„', [0x8D] = '“', [0x8E] = '”', [0x8F] = '‘',
        [0x90] = '’', [0x91] = '‚', [0x92] = '™', [0x93] = 'ﬁ',
        [0x94] = 'ﬂ', [0x95] = 'Ł', [0x96] = 'Œ', [0x97] = 'Š',
        [0x98] = 'Ÿ', [0x99] = 'Ž', [0x9A] = 'ı', [0x9B] = 'ł',
        [0x9C] = 'œ', [0x9D] = 'š', [0x9E] = 'ž', [0xA0] = '€',
    };

    private static readonly Dictionary<char, byte> ToDocEncoding = DocEncoding.ToDictionary(p => p.Value, p => p.Key);

    /// <summary>Decodes the bytes of a text string.</summary>
    /// <param name="bytes">The string bytes.</param>
    /// <returns>The text, without language markers (ESC … ESC) and trailing null characters.</returns>
    /// <remarks>A UTF-16 little-endian byte order mark (FF FE) is also accepted, because such files occur; a trailing
    /// odd byte of UTF-16 text is ignored.</remarks>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Clean(Encoding.BigEndianUnicode.GetString(bytes[2..(2 + (bytes.Length - 2) / 2 * 2)]));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)   // wrong byte order, occurs in practice
            return Clean(Encoding.Unicode.GetString(bytes[2..(2 + (bytes.Length - 2) / 2 * 2)]));
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Clean(Encoding.UTF8.GetString(bytes[3..]));

        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
            sb.Append(DocEncoding.TryGetValue(b, out var c) ? c : (char)b);
        return Clean(sb.ToString());
    }

    /// <summary>Encodes text in PDFDocEncoding (passwords up to revision 4, ISO 32000-2, 7.6.4.3.2).</summary>
    /// <param name="text">The text.</param>
    /// <returns>One byte per character, or null if a character cannot be represented in PDFDocEncoding.</returns>
    public static byte[]? EncodeDocEncoding(string text)
    {
        var doc = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (ToDocEncoding.TryGetValue(c, out var b)) doc[i] = b;
            else if (c <= 0xFF && !DocEncoding.ContainsKey((byte)c) && c is not ('\u009F' or '­')) doc[i] = (byte)c;
            else return null;
        }
        return doc;
    }

    /// <summary>
    /// Encodes text as PDF string bytes: PDFDocEncoding if every character can be represented in it, otherwise
    /// UTF-16BE with a byte order mark (readable by all viewers, including older ones).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The encoded bytes.</returns>
    public static byte[] Encode(string text)
    {
        var doc = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (ToDocEncoding.TryGetValue(c, out var b)) doc[i] = b;
            else if (c <= 0xFF && !DocEncoding.ContainsKey((byte)c) && c is not ('\u009F' or '­')) doc[i] = (byte)c;
            else return [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)];
        }
        return doc;
    }

    /// <summary>
    /// Removes language markers (ESC … ESC in UTF-16, ISO 32000-2, 7.9.2.2.1) and trailing null characters; neither
    /// belongs in the displayed text.
    /// </summary>
    private static string Clean(string text)
    {
        if (text.Contains('\u001B'))
        {
            var sb = new StringBuilder(text.Length);
            var inMarker = false;
            foreach (var c in text)
            {
                if (c == '\u001B') inMarker = !inMarker;
                else if (!inMarker) sb.Append(c);
            }
            text = sb.ToString();
        }
        return text.TrimEnd('\0');
    }
}
