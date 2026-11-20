using PdfKnead.Core.Pdf.IO;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Parsing;

/// <summary>
/// Reads objects from the tokens of a <see cref="PdfLexer"/> (ISO 32000-2, 7.3). Lenient: missing "&gt;&gt;" or "]",
/// keys without a value and a wrong /Length of a stream are repaired instead of rejected. Nesting is limited
/// (<see cref="PdfLimits.MaxNesting"/>).
/// </summary>
/// <remarks>Not thread-safe; uses the position of its lexer.</remarks>
public sealed class PdfParser
{
    private static readonly byte[] EndStream = "endstream"u8.ToArray();
    private static readonly byte[] EndObj = "endobj"u8.ToArray();

    private readonly PdfLexer _lexer;
    private readonly IPdfResolver? _resolver;
    private readonly Func<PdfObjectId, byte[], byte[]>? _decryptString;
    private PdfObjectId? _current;

    /// <summary>Creates a parser.</summary>
    /// <param name="lexer">The lexer that supplies the tokens.</param>
    /// <param name="resolver">For /Length given as a reference and for resolving references in the objects read.</param>
    /// <param name="decryptString">Decrypts the strings of an indirect object (only for encrypted files).</param>
    public PdfParser(PdfLexer lexer, IPdfResolver? resolver = null, Func<PdfObjectId, byte[], byte[]>? decryptString = null)
    {
        _lexer = lexer;
        _resolver = resolver;
        _decryptString = decryptString;
    }

    /// <summary>The lexer that supplies the tokens.</summary>
    public PdfLexer Lexer => _lexer;

    /// <summary>Reads one object from the current position.</summary>
    /// <returns>The object; <see cref="PdfNull"/> for "null", unknown keywords and the end of the data.</returns>
    /// <exception cref="PdfLimitException">The object is nested deeper than <see cref="PdfLimits.MaxNesting"/>.</exception>
    /// <remarks>Strings are decrypted only inside <see cref="ParseIndirectObject"/>.</remarks>
    public PdfObject ParseObject() => Parse(_lexer.Next(), 0);

    /// <summary>
    /// Reads "n g obj … endobj" at <paramref name="offset"/>, including a following stream.
    /// </summary>
    /// <param name="offset">Position of the object number in the source.</param>
    /// <returns>The identifier found there and the object, or null if no indirect object starts there.</returns>
    /// <exception cref="PdfLimitException">The object is nested deeper than <see cref="PdfLimits.MaxNesting"/>.</exception>
    public (PdfObjectId Id, PdfObject Value)? ParseIndirectObject(long offset)
    {
        _lexer.Position = offset;
        var number = _lexer.Next();
        var generation = _lexer.Next();
        var keyword = _lexer.Next();
        if (number.Kind != PdfTokenKind.Integer || generation.Kind != PdfTokenKind.Integer || !keyword.IsKeyword("obj"))
            return null;
        if (number.Integer is < 0 or > int.MaxValue || generation.Integer is < 0 or > 65535) return null;

        var id = new PdfObjectId((int)number.Integer, (int)generation.Integer);
        _current = id;
        try
        {
            var value = ParseObject();
            if (value is PdfDictionary dict)
            {
                var afterDict = _lexer.Position;
                if (_lexer.Next().IsKeyword("stream"))
                    return (id, ReadStream(dict, id));
                _lexer.Position = afterDict;
            }
            return (id, value);
        }
        finally
        {
            _current = null;
        }
    }

    private PdfObject Parse(PdfToken token, int depth)
    {
        if (depth > PdfLimits.MaxNesting)
            throw new PdfLimitException(Tr.T("Objekte zu tief verschachtelt (über {0} Ebenen).", PdfLimits.MaxNesting));

        switch (token.Kind)
        {
            case PdfTokenKind.Integer:
                return ParseIntegerOrReference(token);
            case PdfTokenKind.Real:
                return new PdfReal(token.Real);
            case PdfTokenKind.String:
                return new PdfString(Decrypt(token.Bytes!));
            case PdfTokenKind.HexString:
                return new PdfString(Decrypt(token.Bytes!), isHex: true);
            case PdfTokenKind.Name:
                return token.Name!;
            case PdfTokenKind.ArrayStart:
                return ParseArray(depth);
            case PdfTokenKind.DictionaryStart:
                return ParseDictionary(depth);
            case PdfTokenKind.Keyword:
                if (token.IsKeyword("true")) return PdfBoolean.True;
                if (token.IsKeyword("false")) return PdfBoolean.False;
                return PdfNull.Instance;   // "null" and unknown words
            default:
                return PdfNull.Instance;
        }
    }

    /// <summary>"12" or "12 0 R": looks ahead two tokens to decide.</summary>
    private PdfObject ParseIntegerOrReference(PdfToken first)
    {
        if (first.Integer is >= 0 and <= int.MaxValue)
        {
            var position = _lexer.Position;
            var second = _lexer.Next();
            if (second.Kind == PdfTokenKind.Integer && second.Integer is >= 0 and <= 65535)
            {
                var third = _lexer.Next();
                if (third.IsKeyword("R"))
                    return new PdfReference(new PdfObjectId((int)first.Integer, (int)second.Integer));
            }
            _lexer.Position = position;
        }
        return PdfInteger.Of(first.Integer);
    }

    private PdfArray ParseArray(int depth)
    {
        var items = ParseScratch.RentObjects();
        try
        {
            while (true)
            {
                var position = _lexer.Position;
                var token = _lexer.Next();
                if (token.Kind is PdfTokenKind.ArrayEnd or PdfTokenKind.EndOfFile) break;
                if (IsObjectEnd(token) || token.Kind == PdfTokenKind.DictionaryEnd)
                {
                    // "]" missing: stop; the token belongs to the caller
                    _lexer.Position = position;
                    break;
                }
                items.Add(Parse(token, depth + 1));
            }
            var array = ParseScratch.ToArray(items);
            array.Resolver = _resolver;
            return array;
        }
        finally
        {
            ParseScratch.Return(items);
        }
    }

    private PdfDictionary ParseDictionary(int depth)
    {
        var entries = ParseScratch.RentEntries();
        try
        {
            var rawContents = ReadEntries(entries, depth);
            // the signature value of an encrypted file is not encrypted (ISO 32000-2, 7.6.2): its bytes as they stand (No. 542)
            if (rawContents is not null)
            {
                var probe = ParseScratch.ToDictionary(entries);
                if (Writing.PdfSerializer.IsSignatureDictionary(probe))
                    for (var i = 0; i < entries.Count; i++)
                        if (entries[i].Item1 == PdfName.Contents) entries[i] = (PdfName.Contents, rawContents);
            }
            var dict = ParseScratch.ToDictionary(entries);
            dict.Resolver = _resolver;
            return dict;
        }
        finally
        {
            ParseScratch.Return(entries);
        }
    }

    /// <returns>The undecrypted /Contents string, if the file is encrypted and the dictionary has one; otherwise null.</returns>
    private PdfString? ReadEntries(List<(PdfName, PdfObject)> entries, int depth)
    {
        PdfString? rawContents = null;
        while (true)
        {
            var position = _lexer.Position;
            var token = _lexer.Next();
            if (token.Kind is PdfTokenKind.DictionaryEnd or PdfTokenKind.EndOfFile) return rawContents;
            if (IsObjectEnd(token))
            {
                _lexer.Position = position;   // ">>" missing
                return rawContents;
            }
            if (token.Kind != PdfTokenKind.Name) continue;   // garbage instead of a key: skip it

            var key = token.Name!;
            var valuePosition = _lexer.Position;
            var valueToken = _lexer.Next();
            if (valueToken.Kind is PdfTokenKind.DictionaryEnd or PdfTokenKind.EndOfFile || IsObjectEnd(valueToken))
            {
                // Key without a value at the end
                _lexer.Position = valuePosition;
                continue;
            }
            if (_decryptString is not null && _current is not null && key == PdfName.Contents && valueToken.Kind is PdfTokenKind.String or PdfTokenKind.HexString)
                rawContents = new PdfString(valueToken.Bytes!, isHex: valueToken.Kind == PdfTokenKind.HexString);
            var value = Parse(valueToken, depth + 1);
            // By the standard, a null value means the key is absent
            if (value is not PdfNull) entries.Add((key, value));
        }
    }

    private static bool IsObjectEnd(PdfToken token) =>
        token.IsKeyword("endobj") || token.IsKeyword("stream") || token.IsKeyword("endstream") || token.IsKeyword("obj");

    private byte[] Decrypt(byte[] bytes) =>
        _decryptString is not null && _current is { } id ? _decryptString(id, bytes) : bytes;

    // ------------------------------------------------------------ Streams

    private PdfStream ReadStream(PdfDictionary dict, PdfObjectId id)
    {
        var source = _lexer.Source;
        // "stream" is followed by CRLF or LF; a lone CR or spaces before it are wrong but common
        var p = _lexer.Position;
        while (source.At(p) == ' ') p++;
        if (source.At(p) == '\r') p++;
        if (source.At(p) == '\n') p++;
        var start = p;

        long length = -1;
        try
        {
            if (dict.Get(PdfName.Length) is PdfInteger { Value: >= 0 } l) length = l.Value;
        }
        catch (PdfException)
        {
            // /Length points nowhere or into a loop: search for the end instead
        }

        if (length < 0 || !EndsWithEndStream(source, start + length))
            length = FindLength(source, start);

        _lexer.Position = start + length;
        // Skip "endstream" if it is there
        var after = _lexer.Position;
        if (_lexer.Next().IsKeyword("endstream")) after = _lexer.Position;
        _lexer.Position = after;

        return new PdfStream(dict, start, length, id);
    }

    private static bool EndsWithEndStream(PdfSource source, long end)
    {
        if (end > source.Length) return false;
        var p = end;
        while (PdfLexer.IsWhitespace(source.At(p))) p++;
        return source.Span(p, EndStream.Length).SequenceEqual(EndStream);
    }

    /// <summary>/Length is missing or wrong: searches up to "endstream" (or, failing that, "endobj").</summary>
    private static long FindLength(PdfSource source, long start)
    {
        // With a memory: without "endstream", every stream would otherwise search the rest of the file (quadratic, 13 s for 3 MB)
        var end = source.IndexOfRemembered(EndStream, start, 0);
        if (end < 0) end = source.IndexOfRemembered(EndObj, start, 1);
        if (end < 0) end = source.Length;
        // The end of line before "endstream" is not part of the data
        if (end > start && source.At(end - 1) == '\n') end--;
        if (end > start && source.At(end - 1) == '\r') end--;
        return end - start;
    }
}
