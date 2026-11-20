using System.Text;
using PdfKnead.Core.Pdf.IO;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Parsing;

namespace PdfKnead.Core.Pdf.Content;

/// <summary>An operand of a content stream operator: a number (stored without allocating an object, because content consists almost entirely of numbers) or an object.</summary>
public readonly struct Operand
{
    /// <summary>Creates a numeric operand.</summary>
    /// <param name="number">The value.</param>
    public Operand(double number)
    {
        Number = number;
        Object = null;
    }

    /// <summary>Creates an operand from an object.</summary>
    /// <param name="obj">The object; its numeric value, if any, is also available as <see cref="Number"/>.</param>
    public Operand(PdfObject obj)
    {
        Number = obj.AsNumber() ?? 0;
        Object = obj;
    }

    /// <summary>The numeric value; 0 if the operand is not a number.</summary>
    public double Number { get; }

    /// <summary>Name, string, array, dictionary …; null for numbers created with <see cref="Operand(double)"/>.</summary>
    public PdfObject? Object { get; }

    /// <summary>True if the operand is a number (integer or real).</summary>
    public bool IsNumber => Object is null || Object is PdfInteger or PdfReal;

    /// <summary>The operand as a name; null if it is not a name.</summary>
    public PdfName? Name => Object as PdfName;
}

/// <summary>
/// Inline image "BI … ID data EI" (ISO 32000-2, 8.9.7). Two instances are equal if their data and dictionary are equal: inline images are
/// created anew on every pass (for example in every Type 3 glyph of a bitmap font), and value equality lets them still be
/// decoded only once.
/// </summary>
public sealed class InlineImage(PdfDictionary dictionary, byte[] data) : IEquatable<InlineImage>
{
    private string? _signature;
    private int _hash;

    /// <summary>The image dictionary; abbreviated keys are also stored under their full names.</summary>
    public PdfDictionary Dictionary { get; } = dictionary;

    /// <summary>
    /// The values the content gave under a full key that its abbreviation overrode (both present and different, test file issue14256):
    /// PDF-Knead reads the abbreviation like most readers, other readers take the full name — whatever such a value names must stay
    /// (docs/FEHLERGEDAECHTNIS.md, No. 885). Null when there is none.
    /// </summary>
    public PdfDictionary? Overridden { get; init; }

    /// <summary>
    /// A full key the parser added for an abbreviation that is present (<see cref="Dictionary"/>): written again, an inline image has the
    /// abbreviation only — redacting wrote both, /W and /Width, /L and /Length (docs/FEHLERGEDAECHTNIS.md, No. 913).
    /// </summary>
    public static bool ExpandedKey(PdfDictionary dict, PdfName key) => key.Value switch
    {
        "BitsPerComponent" => Given(dict, PdfName.Get("BPC")),
        "ColorSpace" => Given(dict, PdfName.Get("CS")),
        "Decode" => Given(dict, PdfName.Get("D")),
        "DecodeParms" => Given(dict, PdfName.Get("DP")),
        "Filter" => Given(dict, PdfName.Get("F")),
        "Height" => Given(dict, PdfName.Get("H")),
        "ImageMask" => Given(dict, PdfName.Get("IM")),
        "Interpolate" => Given(dict, PdfName.Get("I")),
        "Width" => Given(dict, PdfName.Get("W")),
        "Length" => Given(dict, PdfName.Get("L")),
        _ => false,
    };

    // the parser copies only an abbreviation with a value (not null) to the full key
    private static bool Given(PdfDictionary dict, PdfName abbreviation) => dict.Get(abbreviation) is { } value and not PdfNull;

    /// <summary>
    /// The dictionary as that of an image XObject (ISO 32000-2, 8.9.7, Tables 91 and 92): only the full keys, abbreviated colour spaces
    /// (/G, /RGB, /CMYK, /I — also as the base of an indexed space) and filters (/Fl, /DCT …) written out, /Type and /Subtype added. An
    /// abbreviation kept in an XObject is not read there — a /DP with a predictor for the old width garbled new data, and a colour space
    /// /CMYK is unknown outside inline images (docs/FEHLERGEDAECHTNIS.md, No. 289).
    /// </summary>
    /// <returns>A new dictionary; the inline image stays as it is.</returns>
    public PdfDictionary AsXObjectDictionary()
    {
        var result = new PdfDictionary();
        result.Set(PdfName.Type, PdfName.Get("XObject"));
        result.Set(PdfName.Subtype, PdfName.Get("Image"));
        foreach (var (key, value) in Dictionary.Entries)
        {
            if (key.Value is "BPC" or "CS" or "D" or "DP" or "F" or "H" or "IM" or "I" or "W" or "L" or "Length") continue;
            result.Set(key, key.Value switch
            {
                "ColorSpace" => FullSpace(value),
                "Filter" => FullFilters(value),
                _ => value,
            });
        }
        // an image mask has no colour space (ISO 32000-2, 8.9.6.2): one named inline as well is ignored by readers, in an XObject it is an
        // error (docs/FEHLERGEDAECHTNIS.md, No. 374)
        if (result.GetBoolean(PdfName.Get("ImageMask")) == true) result.Remove(PdfName.Get("ColorSpace"));
        return result;

        static PdfObject FullSpace(PdfObject space) => space switch
        {
            PdfName name => FullSpaceName(name),
            PdfArray { Count: > 0 } array when array.Get(0) is PdfName { Value: "I" or "Indexed" } => IndexedFull(array),
            _ => space,
        };
        static PdfName FullSpaceName(PdfName name) => name.Value switch
        {
            "G" => PdfName.Get("DeviceGray"),
            "RGB" => PdfName.Get("DeviceRGB"),
            "CMYK" => PdfName.Get("DeviceCMYK"),
            "I" => PdfName.Get("Indexed"),
            _ => name,
        };
        static PdfArray IndexedFull(PdfArray array)
        {
            var copy = new PdfArray();
            for (var i = 0; i < array.Count; i++)
                copy.Add(i == 0 ? PdfName.Get("Indexed") : i == 1 && array[i] is PdfName baseName ? FullSpaceName(baseName) : array[i]);
            return copy;
        }
        static PdfObject FullFilters(PdfObject filter) => filter switch
        {
            PdfName name => FullFilterName(name),
            PdfArray array => new PdfArray([.. Enumerable.Range(0, array.Count).Select(i => array[i] is PdfName n ? FullFilterName(n) : array[i])]),
            _ => filter,
        };
        static PdfName FullFilterName(PdfName name) => name.Value switch
        {
            "AHx" => PdfName.Get("ASCIIHexDecode"),
            "A85" => PdfName.Get("ASCII85Decode"),
            "LZW" => PdfName.Get("LZWDecode"),
            "Fl" => PdfName.Get("FlateDecode"),
            "RL" => PdfName.Get("RunLengthDecode"),
            "CCF" => PdfName.Get("CCITTFaxDecode"),
            "DCT" => PdfName.Get("DCTDecode"),
            _ => name,
        };
    }

    /// <summary>The raw (still encoded) image data between "ID" and "EI".</summary>
    public byte[] Data { get; } = data;

    private string Signature => _signature ??= Dictionary.ToString();

    /// <summary>Compares data and dictionary.</summary>
    /// <param name="other">The other inline image.</param>
    /// <returns>True if both have the same data and the same dictionary.</returns>
    public bool Equals(InlineImage? other) =>
        other is not null && (ReferenceEquals(this, other)
            || Data.Length == other.Data.Length && GetHashCode() == other.GetHashCode()
               && Data.AsSpan().SequenceEqual(other.Data) && Signature == other.Signature);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as InlineImage);

    /// <summary>Hash over data and dictionary; computed once and cached.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        if (_hash != 0) return _hash;
        var h = new HashCode();
        h.AddBytes(Data);
        h.Add(Signature);
        var value = h.ToHashCode();
        return _hash = value == 0 ? 1 : value;
    }
}

/// <summary>
/// Splits a content stream into operators with their operands (ISO 32000-2, 7.8.2).
/// The operand list is reused, so the caller reads it before calling <see cref="Next"/>
/// again. Lenient: garbage is skipped, never thrown.
/// </summary>
public sealed class ContentParser
{
    private readonly PdfLexer _lexer;
    private readonly List<Operand> _operands = new(16);

    // an array or dictionary operand ended by an operator before its closing bracket (docs/FEHLERGEDAECHTNIS.md, No. 931)
    private bool _unfinished;
    private List<(int Start, int End)>? _discarded;
    // Fast path directly on the array: content consists almost entirely of whitespace, numbers and
    // operators. The lexer reads every byte through PdfSource.At (bounds check, array or pointer,
    // 64-bit position); here a local int position is used instead. Everything else (names, strings, arrays,
    // dictionaries, malformed numbers) is still read by the lexer, starting at the same position.
    private readonly byte[] _data;
    private readonly int _length;
    private int _position;

    /// <summary>Creates a parser over the whole of <paramref name="content"/>.</summary>
    /// <param name="content">The decoded content stream.</param>
    public ContentParser(byte[] content) : this(content, content.Length) { }

    /// <summary>Creates a parser over only the first <paramref name="length"/> bytes (for a rented buffer).</summary>
    /// <param name="content">The buffer holding the decoded content stream.</param>
    /// <param name="length">Number of valid bytes; clamped to the buffer length.</param>
    public ContentParser(byte[] content, int length)
    {
        _data = content;
        _length = Math.Clamp(length, 0, content.Length);
        _lexer = new PdfLexer(PdfSource.FromBytes(content, _length));
    }

    /// <summary>
    /// The operands of the operator last returned by <see cref="Next"/>. Valid only until the next call; at most the last 64 operands
    /// are kept, so garbage without an operator does not accumulate.
    /// </summary>
    public IReadOnlyList<Operand> Operands => _operands;

    /// <summary>Byte position just after the operator last returned by <see cref="Next"/> (after "EI" for an inline image).</summary>
    public int Position => _position;

    /// <summary>For the operator "BI": the whole inline image; otherwise null.</summary>
    public InlineImage? InlineImage { get; private set; }

    /// <summary>
    /// Where the operands of the operator last returned held an array or dictionary that an operator ended before its closing bracket („[(a)
    /// TJ“, „&lt;&lt; /ActualText (x) BDC“, S-A A02): the bytes from its opening bracket to that operator, discarded as a broken operand —
    /// the operator is read with the operands before it. Null where there was none (docs/FEHLERGEDAECHTNIS.md, No. 931).
    /// </summary>
    internal IReadOnlyList<(int Start, int End)>? Discarded => _discarded;

    /// <summary>Reads on from <paramref name="position"/> (a <see cref="Position"/> returned earlier), to read operators again.</summary>
    /// <param name="position">Byte position in the content; clamped to it.</param>
    internal void Rewind(int position)
    {
        _position = Math.Clamp(position, 0, _length);
        _operands.Clear();
        InlineImage = null;
    }

    /// <summary>Reads the next operator together with its operands.</summary>
    /// <returns>The operator (for example "re", "Tj"); null at the end of the stream.</returns>
    public string? Next()
    {
        _operands.Clear();
        InlineImage = null;
        _discarded = null;
        var data = _data;
        var length = _length;
        var pos = _position;
        while (true)
        {
            // whitespace and comments
            while (pos < length)
            {
                var w = data[pos];
                if (w is 0 or 9 or 10 or 12 or 13 or 32) pos++;
                else if (w == '%') { while (pos < length && data[pos] is not (10 or 13)) pos++; }
                else break;
            }
            if (pos >= length)
            {
                _position = pos;
                return null;
            }

            var first = data[pos];
            if (first is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z') or 0x27 or (byte)'"')
            {
                // operator (or true/false/null): letters up to the next delimiter, without allocating a string
                var end = pos + 1;
                while (end < length && PdfLexer.IsRegular(data[end])) end++;
                var word = data.AsSpan(pos, end - pos);
                pos = end;
                if (word.SequenceEqual("true"u8)) { _operands.Add(new Operand(PdfBoolean.True)); continue; }
                if (word.SequenceEqual("false"u8)) { _operands.Add(new Operand(PdfBoolean.False)); continue; }
                if (word.SequenceEqual("null"u8)) { _operands.Add(new Operand(PdfNull.Instance)); continue; }
                var op = Operator(word);
                _position = pos;
                if (op == "BI") InlineImage = ReadInlineImage();
                // operands left over before an operator of fixed arity: the ones directly before it count (ISO 32000-2, 7.8.2: the operands
                // precede their operator; two of the three reference readers take them so). One rule for every reader of the content — the
                // display, redaction, text, rewriting —, before the first ones were taken and a rectangle drawn elsewhere
                // (docs/FEHLERGEDAECHTNIS.md, No. 912)
                if (_operands.Count > 1 && PageInterpreter.Arity(op) is { } arity && _operands.Count > arity)
                    _operands.RemoveRange(0, _operands.Count - arity);
                return op;
            }

            if (first is (>= (byte)'0' and <= (byte)'9') or (byte)'-' or (byte)'+' or (byte)'.' && TryNumber(data, length, ref pos, out var number))
            {
                _operands.Add(new Operand(number));
                if (_operands.Count > 64) _operands.RemoveAt(0);
                continue;
            }

            // everything else goes through the lexer
            _lexer.Position = pos;
            var token = _lexer.Next();
            switch (token.Kind)
            {
                case PdfTokenKind.EndOfFile:
                    _position = (int)_lexer.Position;
                    return null;
                case PdfTokenKind.Integer:
                    _operands.Add(new Operand(token.Integer));
                    break;
                case PdfTokenKind.Real:
                    _operands.Add(new Operand(token.Real));
                    break;
                case PdfTokenKind.Keyword:
                    if (token.IsKeyword("true")) _operands.Add(new Operand(PdfBoolean.True));
                    else if (token.IsKeyword("false")) _operands.Add(new Operand(PdfBoolean.False));
                    else if (token.IsKeyword("null")) _operands.Add(new Operand(PdfNull.Instance));
                    else
                    {
                        var op = Operator(token.Bytes);
                        _position = (int)_lexer.Position;
                        if (op == "BI") InlineImage = ReadInlineImage();
                        return op;
                    }
                    break;
                case PdfTokenKind.ArrayEnd or PdfTokenKind.DictionaryEnd:
                    break;   // without a matching opening bracket: skip
                default:
                    var parsed = ParseObject(token, 0);
                    if (_unfinished)
                    {
                        // an operator before the closing bracket: the operand is broken and goes, the operator is read next — before, the
                        // array or dictionary took in the rest of the stream, and what followed was not drawn, while other readers show it
                        // (No. 931)
                        _unfinished = false;
                        (_discarded ??= []).Add(((int)token.Start, (int)_lexer.Position));
                        break;
                    }
                    _operands.Add(new Operand(parsed));
                    break;
            }
            pos = (int)_lexer.Position;
            if (_operands.Count > 64) _operands.RemoveAt(0);   // do not collect garbage without an operator indefinitely
        }
    }

    /// <summary>
    /// Reads a well-formed number ("12", "-3.5", ".25") directly, with exactly the same arithmetic as
    /// the lexer so that the values stay bit-identical. Anything unusual (several signs, very many
    /// digits, letters directly after it) returns false, and the lexer reads it instead.
    /// </summary>
    private static bool TryNumber(byte[] data, int length, ref int position, out double value)
    {
        value = 0;
        var pos = position;
        var negative = false;
        int c = data[pos];
        if (c is '-' or '+')
        {
            negative = c == '-';
            if (++pos >= length) return false;
            c = data[pos];
        }
        long integer = 0;
        var digits = 0;
        while (c is >= '0' and <= '9')
        {
            if (++digits > 18) return false;
            integer = integer * 10 + (c - '0');
            c = ++pos < length ? data[pos] : -1;
        }
        if (c != '.')
        {
            if (digits == 0 || PdfLexer.IsRegular(c)) return false;
            value = negative ? -integer : integer;
            position = pos;
            return true;
        }
        c = ++pos < length ? data[pos] : -1;
        double fraction = 0, scale = 1;
        var fractionDigits = 0;
        while (c is >= '0' and <= '9')
        {
            if (++fractionDigits > 17) return false;
            fraction = fraction * 10 + (c - '0');
            scale *= 10;
            c = ++pos < length ? data[pos] : -1;
        }
        if (digits == 0 && fractionDigits == 0 || PdfLexer.IsRegular(c)) return false;
        var v = integer + fraction / scale;
        value = negative ? -v : v;
        position = pos;
        return true;
    }

    // All operators from ISO 32000-2, Annex A, as a fixed table: operators are short and
    // repeat millions of times, so no allocation and no lock per operator.
    private static readonly Dictionary<int, string> Known = new[]
    {
        "b", "B", "b*", "B*", "BDC", "BI", "BMC", "BT", "BX", "c", "cm", "CS", "cs", "d", "d0", "d1", "Do", "DP", "EI",
        "EMC", "ET", "EX", "f", "F", "f*", "G", "g", "gs", "h", "i", "ID", "j", "J", "K", "k", "l", "m", "M", "MP", "n",
        "q", "Q", "re", "RG", "rg", "ri", "s", "S", "SC", "sc", "SCN", "scn", "sh", "T*", "Tc", "Td", "TD", "Tf", "Tj",
        "TJ", "TL", "Tm", "Tr", "Ts", "Tw", "Tz", "v", "w", "W", "W*", "y", "'", "\"",
    }.ToDictionary(op => Key(System.Text.Encoding.Latin1.GetBytes(op)), op => op);

    /// <summary>The operators of ISO 32000-2, Annex A (for checks that no others are used outside BX … EX).</summary>
    internal static readonly IReadOnlySet<string> Operators = Known.Values.ToHashSet(StringComparer.Ordinal);

    private static int Key(ReadOnlySpan<byte> b) =>
        b.Length | (b.Length > 0 ? b[0] << 8 : 0) | (b.Length > 1 ? b[1] << 16 : 0) | (b.Length > 2 ? b[2] << 24 : 0);

    private static string Operator(ReadOnlySpan<byte> bytes) =>
        bytes.Length <= 3 && Known.TryGetValue(Key(bytes), out var op) ? op : Encoding.Latin1.GetString(bytes);

    /// <summary>
    /// An operator inside an array or dictionary of a content stream: no operand has one, so the array or dictionary was not closed (ISO
    /// 32000-2, 7.3.6, 7.3.7, 7.8.2) — braces, which only PostScript functions know, and true, false, null are no such end.
    /// </summary>
    private static bool EndsOperand(PdfToken token) =>
        token.Kind == PdfTokenKind.Keyword && !token.IsKeyword("true") && !token.IsKeyword("false") && !token.IsKeyword("null") &&
        !token.IsKeyword("{") && !token.IsKeyword("}");

    /// <summary>Ends an unfinished array or dictionary at the operator, which is read next (No. 931).</summary>
    private PdfNull Unfinished(PdfToken op)
    {
        _lexer.Position = op.Start;
        _unfinished = true;
        return PdfNull.Instance;
    }

    private PdfObject ParseObject(PdfToken token, int depth)
    {
        if (depth > PdfLimits.MaxNesting) return PdfNull.Instance;
        switch (token.Kind)
        {
            case PdfTokenKind.Integer: return PdfInteger.Of(token.Integer);
            case PdfTokenKind.Real: return new PdfReal(token.Real);
            case PdfTokenKind.String: return new PdfString(token.Bytes!);
            case PdfTokenKind.HexString: return new PdfString(token.Bytes!, isHex: true);
            case PdfTokenKind.Name: return token.Name!;
            case PdfTokenKind.ArrayStart:
            {
                // with exactly the right size (TJ arrays occur thousands of times)
                var items = ParseScratch.RentObjects();
                try
                {
                    while (true)
                    {
                        var t = _lexer.Next();
                        if (t.Kind is PdfTokenKind.ArrayEnd or PdfTokenKind.EndOfFile) return ParseScratch.ToArray(items);
                        if (EndsOperand(t)) return Unfinished(t);
                        items.Add(ParseObject(t, depth + 1));
                        if (_unfinished) return PdfNull.Instance;
                    }
                }
                finally
                {
                    ParseScratch.Return(items);
                }
            }
            case PdfTokenKind.DictionaryStart:
            {
                var entries = ParseScratch.RentEntries();
                try
                {
                    while (true)
                    {
                        var t = _lexer.Next();
                        if (t.Kind is PdfTokenKind.DictionaryEnd or PdfTokenKind.EndOfFile) return ParseScratch.ToDictionary(entries);
                        if (EndsOperand(t)) return Unfinished(t);
                        if (t.Kind != PdfTokenKind.Name) continue;
                        var key = t.Name!;
                        var value = _lexer.Next();
                        if (value.Kind is PdfTokenKind.DictionaryEnd or PdfTokenKind.EndOfFile) return ParseScratch.ToDictionary(entries);
                        if (EndsOperand(value)) return Unfinished(value);
                        entries.Add((key, ParseObject(value, depth + 1)));
                        if (_unfinished) return PdfNull.Instance;
                    }
                }
                finally
                {
                    ParseScratch.Return(entries);
                }
            }
            case PdfTokenKind.Keyword:
                return token.IsKeyword("true") ? PdfBoolean.True : token.IsKeyword("false") ? PdfBoolean.False : PdfNull.Instance;
            default:
                return PdfNull.Instance;
        }
    }

    // ------------------------------------------------------------ Inline images

    private InlineImage ReadInlineImage()
    {
        _lexer.Position = _position;
        try
        {
            return ReadInlineImageCore();
        }
        finally
        {
            _position = (int)Math.Min(_lexer.Position, _length);
        }
    }

    private InlineImage ReadInlineImageCore()
    {
        var dict = new PdfDictionary();
        while (true)
        {
            var token = _lexer.Next();
            if (token.Kind == PdfTokenKind.EndOfFile) return new InlineImage(dict, []);
            if (token.IsKeyword("ID")) break;
            if (token.Kind != PdfTokenKind.Name) continue;
            var key = token.Name!;
            dict.Set(key, ParseObject(_lexer.Next(), 1));
            // an array of the image dictionary not closed before "ID": it goes, "ID" is read next (No. 931)
            _unfinished = false;
        }
        // If both the abbreviated and the full key are present, the abbreviated one wins (as in common PDF readers; test file
        // issue14256). The full key receives its value so that every caller reads the same, whatever order it looks the keys up in.
        PdfDictionary? overridden = null;
        foreach (var (abbreviation, full) in Abbreviations)
            if (dict.Get(abbreviation) is { } value and not PdfNull)
            {
                if (dict.Get(full) is { } before and not PdfNull && !ReferenceEquals(before, value)) (overridden ??= new PdfDictionary()).Set(full, before);
                dict.Set(full, value);
            }

        // after "ID" exactly one whitespace character, then the data
        var source = _lexer.Source;
        var start = _lexer.Position;
        if (PdfLexer.IsWhitespace(source.At(start))) start++;

        var length = ExpectedLength(dict);
        long end;
        // /L (or /Length), which PDF 2.0 asks for: the length of the data — where an "EI" follows it, it ends the image; before, filtered
        // data was searched for "EI" and could end early at one inside it (ISO 32000-2, 8.9.7; docs/FEHLERGEDAECHTNIS.md, No. 765)
        if (dict.GetInteger(PdfName.Get("Length")) is { } declared && declared >= 0 && start + declared <= source.Length && IsEiAt(source, start + declared))
            end = start + declared;
        else if (length is { } n && start + n <= source.Length && IsEiAt(source, start + n))
            end = start + n;
        else if (TextFilterEnd(dict, source, start) is { } marked && IsEiAt(source, marked))
            end = marked;   // ASCII85/ASCIIHex: after the end marker, because "EI" can also occur in the middle of such data (issue10614)
        else
            end = FindEi(source, start);

        var data = source.ReadBytes(start, end - start);
        // past the data and the "EI"
        var p = end;
        while (PdfLexer.IsWhitespace(source.At(p))) p++;
        _lexer.Position = Math.Min(source.Length, p + 2);
        return new InlineImage(dict, data) { Overridden = overridden };
    }

    /// <summary>Abbreviated keys of inline images (ISO 32000-2, Table 91) and their full names.</summary>
    private static readonly (PdfName Abbreviation, PdfName Full)[] Abbreviations =
    [
        (PdfName.Get("BPC"), PdfName.Get("BitsPerComponent")), (PdfName.Get("CS"), PdfName.Get("ColorSpace")),
        (PdfName.Get("D"), PdfName.Get("Decode")), (PdfName.Get("DP"), PdfName.Get("DecodeParms")), (PdfName.Get("F"), PdfName.Filter),
        (PdfName.Get("H"), PdfName.Get("Height")), (PdfName.Get("IM"), PdfName.Get("ImageMask")), (PdfName.Get("I"), PdfName.Get("Interpolate")),
        (PdfName.Get("W"), PdfName.Get("Width")), (PdfName.Get("L"), PdfName.Get("Length")),
    ];

    /// <summary>Data length of an unfiltered image from width, height, bits and components; null with filters or implausible values.</summary>
    private static long? ExpectedLength(PdfDictionary dict)
    {
        if (dict.ContainsKey(PdfName.Get("F")) || dict.ContainsKey(PdfName.Filter)) return null;
        var w = dict.GetInteger(PdfName.Get("W")) ?? dict.GetInteger(PdfName.Get("Width"));
        var h = dict.GetInteger(PdfName.Get("H")) ?? dict.GetInteger(PdfName.Get("Height"));
        if (w is not (> 0 and <= 1 << 24) || h is not (> 0 and <= 1 << 24)) return null;
        var mask = dict.GetBoolean(PdfName.Get("IM")) ?? dict.GetBoolean(PdfName.Get("ImageMask")) ?? false;
        var bpc = mask ? 1 : dict.GetInteger(PdfName.Get("BPC")) ?? dict.GetInteger(PdfName.Get("BitsPerComponent")) ?? 8;
        // Nonsensical bit depth (a negative one gives a length below zero and a crash when copying): search for "EI" instead
        if (bpc is not (> 0 and <= 32)) return null;
        var cs = (dict.Get(PdfName.Get("CS")) as PdfName ?? dict.Get(PdfName.Get("ColorSpace")) as PdfName)?.Value;
        var components = mask ? 1 : cs switch
        {
            "G" or "DeviceGray" or "CalGray" => 1,
            "RGB" or "DeviceRGB" or "CalRGB" => 3,
            "CMYK" or "DeviceCMYK" => 4,
            _ => 1,   // Indexed and others: 1 component
        };
        return (w.Value * components * bpc + 7) / 8 * h.Value;
    }

    /// <summary>
    /// If the first filter is ASCII85 or ASCIIHex: the position after its end marker ("~&gt;" or "&gt;"), otherwise null. Such data is
    /// readable text and can contain "EI" surrounded by whitespace; a search for "EI" alone would stop there and lose the rest of the image.
    /// </summary>
    private static long? TextFilterEnd(PdfDictionary dict, PdfSource source, long start)
    {
        var filter = dict.Get(PdfName.Filter) switch
        {
            PdfName name => name.Value,
            PdfArray { Count: > 0 } chain => (chain.Get(0) as PdfName)?.Value,
            _ => null,
        };
        var ascii85 = filter is "A85" or "ASCII85Decode";
        if (!ascii85 && filter is not ("AHx" or "ASCIIHexDecode")) return null;
        // Read by the character set instead of searching for the end marker: some files write only "~" (issue11385), and a search
        // for "~>" would run into the next image and swallow the content in between. End: the end marker or the first foreign character.
        for (var p = start; p < source.Length; p++)
        {
            var c = source.At(p);
            if (PdfLexer.IsWhitespace(c)) continue;
            if (ascii85)
            {
                if (c == '~') return source.At(p + 1) == '>' ? p + 2 : p + 1;
                if (c is >= '!' and <= 'u' or 'z') continue;
            }
            else
            {
                if (c == '>') return p + 1;
                if (char.IsAsciiHexDigit((char)c)) continue;
            }
            return p;
        }
        return null;
    }

    private static bool IsEiAt(PdfSource s, long p)
    {
        while (PdfLexer.IsWhitespace(s.At(p))) p++;
        return s.At(p) == 'E' && s.At(p + 1) == 'I' && !PdfLexer.IsRegular(s.At(p + 2));
    }

    /// <summary>
    /// Finds "EI" with whitespace before and after it, followed by readable operators (image data can contain "EI"). The standard does not ask
    /// for whitespace before "EI" (ISO 32000-2, 8.9.7): where no such "EI" follows, the first one right after the data — followed by whitespace
    /// or the end and readable operators — ends the image (otherwise the rest of the content, "EI" included, would be taken as its data).
    /// </summary>
    private static long FindEi(PdfSource s, long from)
    {
        for (var p = from; p + 1 < s.Length; p++)
        {
            if (s.At(p) != 'E' || s.At(p + 1) != 'I' || (p > from && !PdfLexer.IsWhitespace(s.At(p - 1))) || PdfLexer.IsRegular(s.At(p + 2)))
                continue;
            if (LooksLikeContent(s, p + 2)) return p > from && PdfLexer.IsWhitespace(s.At(p - 1)) ? p - 1 : p;
        }
        for (var p = from; p + 1 < s.Length; p++)
            if (s.At(p) == 'E' && s.At(p + 1) == 'I' && !PdfLexer.IsRegular(s.At(p + 2)) && LooksLikeContent(s, p + 2)) return p;
        return s.Length;
    }

    private static bool LooksLikeContent(PdfSource s, long p)
    {
        // the next bytes must be printable ASCII (operators, numbers), as in widely used readers
        for (var i = 0; i < 32; i++)
        {
            var c = s.At(p + i);
            if (c == -1) return true;
            if (c is < 9 or > 127 || c is > 13 and < 32) return false;
        }
        return true;
    }
}
