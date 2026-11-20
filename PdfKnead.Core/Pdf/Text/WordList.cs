using System.Text;

namespace PdfKnead.Core.Pdf.Text;

/// <summary>
/// A list of words or names for „Suchen und schwärzen“ as a file (KONZEPT.md, section 5.6): plain text, one term per line, or a table saved
/// as CSV with semicolons or tabs (one term per cell; commas stay, „Mustermann, Max“ is one name). Read in UTF-8 (with or without BOM), UTF-16
/// (with BOM, or without one if every second byte is zero — „Unicode text“ of spreadsheets and editors), Latin-1 if the file is none of these;
/// empty lines, quotes around a cell, control characters and repeated terms are left out.
/// </summary>
public static class WordList
{
    /// <summary>Largest file read.</summary>
    public const int MaxBytes = 4 << 20;

    /// <summary>Most terms taken from a file.</summary>
    public const int MaxTerms = 20_000;

    /// <summary>The terms of a file.</summary>
    /// <param name="data">The file.</param>
    /// <returns>The terms in their order, each once; at most <see cref="MaxTerms"/>.</returns>
    /// <exception cref="InvalidDataException">The file is larger than <see cref="MaxBytes"/>.</exception>
    public static List<string> Read(byte[] data)
    {
        if (data.Length > MaxBytes) throw new InvalidDataException(Tr.T("Die Wortliste ist zu groß (höchstens {0} MB).", MaxBytes >> 20));
        return Parse(Decode(data).TrimStart('\ufeff'));
    }

    /// <summary>The text of a file in the encodings <see cref="Read"/> accepts.</summary>
    private static string Decode(byte[] data)
    {
        if (data is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(data, 2, data.Length - 2 & ~1);
        if (data is [0xFE, 0xFF, ..]) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2 & ~1);
        if (Utf16WithoutBom(data) is { } utf16) return utf16.GetString(data, 0, data.Length & ~1);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(data);
        }
    }

    /// <summary>
    /// UTF-16 without a BOM: in text of Latin letters every second byte is zero (the odd ones in little endian, the even ones in big endian),
    /// while the other half has none — at least nine in ten pairs, and at least four pairs.
    /// </summary>
    private static Encoding? Utf16WithoutBom(byte[] data)
    {
        var pairs = data.Length / 2;
        if (pairs < 4) return null;
        int zeroOdd = 0, zeroEven = 0;
        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            if (data[i] == 0) zeroEven++;
            if (data[i + 1] == 0) zeroOdd++;
        }
        if (zeroOdd * 10 >= pairs * 9 && zeroEven * 10 < pairs) return Encoding.Unicode;
        if (zeroEven * 10 >= pairs * 9 && zeroOdd * 10 < pairs) return Encoding.BigEndianUnicode;
        return null;
    }

    /// <summary>The terms of a text (see <see cref="Read"/>).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The terms.</returns>
    public static List<string> Parse(string text)
    {
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            foreach (var cell in line.Split(';', '\t'))
            {
                var term = (cell.Any(char.IsControl) ? new string([.. cell.Where(c => !char.IsControl(c))]) : cell).Trim();
                if (term.Length >= 2 && term[0] == '"' && term[^1] == '"') term = term[1..^1].Replace("\"\"", "\"").Trim();
                if (term.Length == 0 || !seen.Add(term)) continue;
                terms.Add(term);
                if (terms.Count == MaxTerms) return terms;
            }
        }
        return terms;
    }

    /// <summary>A list as a file: one term per line, UTF-8.</summary>
    /// <param name="terms">The terms.</param>
    /// <returns>The file.</returns>
    public static byte[] Write(IEnumerable<string> terms) =>
        Encoding.UTF8.GetBytes(string.Join("\n", terms.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal)) + "\n");
}
