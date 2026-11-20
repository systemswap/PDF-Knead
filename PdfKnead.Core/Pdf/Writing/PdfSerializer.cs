using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Writing;

/// <summary>
/// Reference to an object of the <em>new</em> file (when saving). Ordinary <see cref="PdfReference"/>s point into a
/// source file and are renumbered when written.
/// </summary>
internal sealed class OutputReference(int number) : PdfObject
{
    public int Number { get; } = number;

    internal override void Write(StringBuilder sb, int indent) => sb.Append(Number).Append(" 0 R");
}

/// <summary>
/// Writes objects in PDF syntax (ISO 32000-2, 7.3), compactly: spaces only where two characters would otherwise merge
/// into one token. The caller writes the stream data; only the stream dictionary is written here.
/// </summary>
public static class PdfSerializer
{
    /// <summary>New number of a reference in the output; 0 = write "null" (the target is dropped).</summary>
    internal delegate int ReferenceMap(PdfObjectId id);

    /// <summary>Writes an object to a stream; references are written unchanged.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="output">The output stream.</param>
    /// <param name="encryptString">Encrypts strings (only when saving encrypted files); encrypted strings are written in hexadecimal form.</param>
    /// <exception cref="PdfLimitException">The object is nested deeper than <see cref="PdfLimits.MaxNesting"/>.</exception>
    public static void Write(PdfObject obj, Stream output, Func<byte[], byte[]>? encryptString = null)
    {
        var buffer = new ByteBuffer(256);
        Write(buffer, obj, null, encryptString);
        buffer.WriteTo(output);
    }

    /// <summary>Writes an object to a new byte array; references are written unchanged.</summary>
    /// <param name="obj">The object.</param>
    /// <param name="encryptString">Encrypts strings; null for none.</param>
    /// <returns>The PDF syntax of the object.</returns>
    /// <exception cref="PdfLimitException">The object is nested deeper than <see cref="PdfLimits.MaxNesting"/>.</exception>
    public static byte[] ToBytes(PdfObject obj, Func<byte[], byte[]>? encryptString = null)
    {
        var buffer = new ByteBuffer(256);
        Write(buffer, obj, null, encryptString);
        return buffer.Written.ToArray();
    }

    /// <param name="map">Renumbers references; null = write them as they are.</param>
    internal static void Write(ByteBuffer b, PdfObject obj, ReferenceMap? map, Func<byte[], byte[]>? encrypt, int depth = 0)
    {
        if (depth > PdfLimits.MaxNesting) throw new PdfLimitException(Tr.T("Objekte sind zu tief verschachtelt."));
        switch (obj)
        {
            case PdfNull:
                Word(b, "null");
                break;
            case PdfBoolean v:
                Word(b, v.Value ? "true" : "false");
                break;
            case PdfInteger i:
                Separate(b);
                b.Int(i.Value);
                break;
            case PdfReal r:
                Separate(b);
                b.Real(r.Value);
                break;
            case PdfName n:
                WriteName(b, n);
                break;
            case PdfString s:
                var bytes = encrypt is null ? s.Bytes : encrypt(s.Bytes);
                if (encrypt is not null || s.IsHex) WriteHex(b, bytes);
                else WriteLiteral(b, bytes);
                break;
            case PdfReference reference:
                var number = map is null ? reference.Id.Number : map(reference.Id);
                if (number <= 0)
                {
                    Word(b, "null");
                    break;
                }
                Separate(b);
                b.Int(number);
                b.Ascii(map is null ? $" {reference.Id.Generation} R" : " 0 R");
                break;
            case OutputReference output:
                Separate(b);
                b.Int(output.Number);
                b.Ascii(" 0 R");
                break;
            case PdfArray a:
                b.Add((byte)'[');
                for (var k = 0; k < a.Count; k++) Write(b, a[k], map, encrypt, depth + 1);
                b.Add((byte)']');
                break;
            case PdfDictionary d:
                b.Add((byte)'<');
                b.Add((byte)'<');
                // the signature value stays plain in an encrypted file (ISO 32000-2, 7.6.2; docs/FEHLERGEDAECHTNIS.md, No. 542)
                var plainContents = encrypt is not null && IsSignatureDictionary(d);
                foreach (var (key, value) in d.Entries)
                {
                    WriteName(b, key);
                    Write(b, value, map, plainContents && key == PdfName.Contents && value is PdfString ? null : encrypt, depth + 1);
                }
                b.Add((byte)'>');
                b.Add((byte)'>');
                break;
            case PdfStream stream:
                Write(b, stream.Dictionary, map, encrypt, depth);
                break;
        }
    }

    /// <summary>
    /// Whether a dictionary is a signature dictionary, whose /Contents is not encrypted in an encrypted file (ISO 32000-2, 7.6.2): /Type /Sig or
    /// /DocTimeStamp; as /Type is optional, also one with a /ByteRange array, a /Filter name and a /Contents string (Table 255).
    /// </summary>
    /// <param name="dict">The dictionary.</param>
    internal static bool IsSignatureDictionary(PdfDictionary dict) =>
        dict.GetRaw(PdfName.Type) is PdfName { Value: "Sig" or "DocTimeStamp" }
        || dict.GetRaw(PdfName.Get("ByteRange")) is PdfArray && dict.GetRaw(PdfName.Filter) is PdfName && dict.GetRaw(PdfName.Contents) is PdfString;

    /// <summary>True for regular characters, false for characters that end a token (white space and delimiters, ISO 32000-2, 7.2.3).</summary>
    private static bool IsRegular(int c) =>
        c >= 0 && c is not (' ' or '\n' or '\r' or '\t' or '\f' or 0 or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%');

    /// <summary>
    /// Before a token (number, true, null …): a space if the previous character would merge with the next one, including
    /// after an empty name "/". Otherwise "/ 7 0 R" in the source is written as "/6 0 R" (renumbered), that is, the name "6"
    /// (test file issue13610: the page image was missing after saving). See docs/FEHLERGEDAECHTNIS.md, No. 74.
    /// </summary>
    private static void Separate(ByteBuffer b)
    {
        if (IsRegular(b.Last) || b.Last == (byte)'/') b.Add((byte)' ');
    }

    private static void Word(ByteBuffer b, string word)
    {
        Separate(b);
        b.Ascii(word);
    }

    private static void WriteName(ByteBuffer b, PdfName name)
    {
        // After an empty name, not "//" (some readers read that as one name)
        if (b.Last == (byte)'/') b.Add((byte)' ');
        b.Add((byte)'/');
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in name.Value.EnumerateRunes())
        {
            var c = rune.Value;
            if (c > 0xFF)
            {
                // Names read from files are bytes (one per character); new characters beyond that (such as "€" in an export
                // value) are written as UTF-8, as in PDF 2.0; truncating them to one byte would turn "€" into a different
                // character. See docs/FEHLERGEDAECHTNIS.md, No. 48.
                var n = rune.EncodeToUtf8(utf8);
                for (var i = 0; i < n; i++)
                {
                    b.Add((byte)'#');
                    b.Add(Hex(utf8[i] >> 4));
                    b.Add(Hex(utf8[i] & 0xF));
                }
            }
            else if (c is < '!' or > '~' or '#' or '/' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%')
            {
                b.Add((byte)'#');
                b.Add(Hex((c >> 4) & 0xF));
                b.Add(Hex(c & 0xF));
            }
            else
            {
                b.Add((byte)c);
            }
        }
    }

    private static byte Hex(int v) => (byte)(v < 10 ? '0' + v : 'A' + v - 10);

    private static void WriteHex(ByteBuffer b, byte[] bytes)
    {
        b.Add((byte)'<');
        foreach (var x in bytes)
        {
            b.Add(Hex(x >> 4));
            b.Add(Hex(x & 0xF));
        }
        b.Add((byte)'>');
    }

    private static void WriteLiteral(ByteBuffer b, byte[] bytes)
    {
        b.Add((byte)'(');
        foreach (var x in bytes)
        {
            switch (x)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    b.Add((byte)'\\');
                    b.Add(x);
                    break;
                case (byte)'\r':
                    b.Add((byte)'\\');
                    b.Add((byte)'r');
                    break;
                case (byte)'\n':
                    b.Add((byte)'\\');
                    b.Add((byte)'n');
                    break;
                default:
                    b.Add(x);
                    break;
            }
        }
        b.Add((byte)')');
    }

    /// <summary>
    /// Formats a real number in the shortest form that gives exactly the same value, so that no number changes when
    /// saving (rounded matrices shift fine graphics by pixels). See docs/FEHLERGEDAECHTNIS.md, No. 19.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The text, culture-invariant and without an exponent, which PDF does not know; values that would need an
    /// exponent are written with at most 17 decimal places. Non-finite values and -0 give "0".</returns>
    public static string FormatReal(double value)
    {
        if (!double.IsFinite(value)) return "0";
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        if (text.Contains('E')) text = value.ToString("0.#################", CultureInfo.InvariantCulture);
        return text is "-0" ? "0" : text;
    }
}
