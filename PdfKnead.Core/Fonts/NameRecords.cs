using System.Buffers.Binary;
using System.Text;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Chooses the text of a name ID from a name table (OpenType 30), the one rule for the font reader and the index of system fonts: English
/// first (Windows language 0x0409, a language tag „en“ of a version 1 table, or Macintosh language 0), otherwise the first record in table
/// order — Windows and Unicode before Macintosh. Windows texts are UTF-16BE except the old code page encodings 2–9 (Shift-JIS, PRC, Big5,
/// Wansung, Johab, reserved), which are passed over; Macintosh texts are read in Mac OS Roman (encoding 0; the other scripts are passed over),
/// and a language ID beyond the language tags of a version 1 table names no known language and is passed over (docs/FEHLERGEDAECHTNIS.md,
/// No. 1160).
/// </summary>
internal static class NameRecords
{
    /// <summary>Reads the text of a name ID.</summary>
    /// <param name="data">The data holding the table (the font file, or the table alone).</param>
    /// <param name="table">Where the name table starts in <paramref name="data"/>.</param>
    /// <param name="nameId">The name ID.</param>
    /// <param name="systemIndex">For the index of system fonts: only the Windows encodings 0 and 1 and Macintosh Roman, as the index has always
    /// read (no Unicode platform, no full repertoire).</param>
    /// <returns>The text, or null if no usable record has the ID.</returns>
    public static string? Read(ReadOnlySpan<byte> data, int table, int nameId, bool systemIndex = false)
    {
        if (table < 0 || table + 6 > data.Length) return null;
        int version = U16(data, table), count = U16(data, table + 2), storage = table + U16(data, table + 4);
        // version 1: the language tags follow the records; a language ID of 0x8000 or above refers to one of them
        var tags = version >= 1 && table + 6 + 12L * count + 2 <= data.Length ? U16(data, table + 6 + 12 * count) : -1;
        var bestRank = int.MaxValue;
        string? best = null;
        for (var i = 0; i < count; i++)
        {
            var r = table + 6 + 12 * i;
            if (r + 12 > data.Length) break;
            int platform = U16(data, r), encoding = U16(data, r + 2), language = U16(data, r + 4), id = U16(data, r + 6);
            int length = U16(data, r + 8), at = storage + U16(data, r + 10);
            if (id != nameId || at + length > data.Length) continue;
            if (tags >= 0 && language >= 0x8000 && language - 0x8000 >= tags) continue;   // beyond the tags: an unknown language (30.1.2)
            var english = language == 0x0409 || language >= 0x8000 && tags > 0 && EnglishTag(data, table, storage, count, language - 0x8000);
            int rank;
            switch (platform)
            {
                case 3 when encoding is >= 2 and <= 9:                     // old code pages: not UTF-16 (30.5.4.1)
                case 3 when systemIndex && encoding is not (0 or 1):
                case 0 when systemIndex:
                    continue;
                case 3:
                    rank = english ? 0 : 3;
                    break;
                case 0:
                    // the Unicode platform has no language of its own (language 0); a tag can name one
                    rank = english ? 0 : 1;
                    break;
                case 1 when encoding == 0:
                    rank = language == 0 ? 2 : 4;
                    break;
                default:
                    continue;
            }
            if (rank >= bestRank) continue;
            bestRank = rank;
            var bytes = data.Slice(at, length);
            best = platform == 1 ? MacRoman(bytes) : Encoding.BigEndianUnicode.GetString(bytes);
            if (rank == 0) break;
        }
        return best;
    }

    /// <summary>Whether language tag <paramref name="index"/> of a version 1 table is English („en“ or „en-…“).</summary>
    private static bool EnglishTag(ReadOnlySpan<byte> data, int table, int storage, int count, int index)
    {
        var r = table + 6 + 12L * count + 2 + 4L * index;
        if (r + 4 > data.Length) return false;
        int length = U16(data, (int)r), at = storage + U16(data, (int)r + 2);
        if (length is < 4 or > 64 || at + length > data.Length) return false;
        var tag = Encoding.BigEndianUnicode.GetString(data.Slice(at, length));
        return tag.Equals("en", StringComparison.OrdinalIgnoreCase) || tag.StartsWith("en-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Decodes Mac OS Roman (OpenType 30.5.3.1, Macintosh encoding 0): ASCII and 128 further characters — before read as Latin-1.</summary>
    internal static string MacRoman(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++) chars[i] = bytes[i] < 0x80 ? (char)bytes[i] : (char)FontTables.MacOsRomanUnicode[bytes[i] - 0x80];
        return new string(chars);
    }

    private static int U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
}
