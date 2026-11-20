using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.IO;

namespace PdfKnead.Core.Pdf.Parsing;

/// <summary>Kind of a <see cref="PdfToken"/>.</summary>
public enum PdfTokenKind
{
    /// <summary>End of the data.</summary>
    EndOfFile,
    /// <summary>An integer; value in <see cref="PdfToken.Integer"/>.</summary>
    Integer,
    /// <summary>A real number; value in <see cref="PdfToken.Real"/>.</summary>
    Real,
    /// <summary>A literal string "(…)"; bytes in <see cref="PdfToken.Bytes"/>.</summary>
    String,
    /// <summary>A hexadecimal string "&lt;…&gt;"; bytes in <see cref="PdfToken.Bytes"/>.</summary>
    HexString,
    /// <summary>A name; in <see cref="PdfToken.Name"/>.</summary>
    Name,
    /// <summary>"[".</summary>
    ArrayStart,
    /// <summary>"]".</summary>
    ArrayEnd,
    /// <summary>"&lt;&lt;".</summary>
    DictionaryStart,
    /// <summary>"&gt;&gt;", or a single "&gt;".</summary>
    DictionaryEnd,
    /// <summary>Everything else without delimiters: obj, endobj, stream, R, true, xref, operators …</summary>
    Keyword,
}

/// <summary>A token from the file. <see cref="Bytes"/> carries strings and keywords, <see cref="Name"/> names.</summary>
/// <param name="Kind">The kind of token.</param>
/// <param name="Start">Position of the first byte of the token in the source.</param>
/// <param name="Integer">The value of an <see cref="PdfTokenKind.Integer"/> token.</param>
/// <param name="Real">The value of a <see cref="PdfTokenKind.Real"/> token.</param>
/// <param name="Bytes">The bytes of a string or keyword token.</param>
/// <param name="Name">The name of a <see cref="PdfTokenKind.Name"/> token.</param>
public readonly record struct PdfToken(PdfTokenKind Kind, long Start, long Integer = 0, double Real = 0, byte[]? Bytes = null, PdfName? Name = null)
{
    /// <summary>Checks whether the token is the given keyword.</summary>
    /// <param name="keyword">The keyword (ASCII).</param>
    /// <returns>True if the token is a keyword with exactly these bytes.</returns>
    public bool IsKeyword(string keyword)
    {
        if (Kind != PdfTokenKind.Keyword || Bytes is null || Bytes.Length != keyword.Length) return false;
        for (var i = 0; i < Bytes.Length; i++)
            if (Bytes[i] != keyword[i]) return false;
        return true;
    }

    /// <summary>The name value or the bytes as Latin-1 text; empty for other tokens.</summary>
    public string Text => Name?.Value ?? (Bytes is null ? "" : Encoding.Latin1.GetString(Bytes));
}

/// <summary>
/// Splits PDF bytes into tokens (ISO 32000-2, 7.2 and 7.3). Lenient like the major viewers: broken numbers ("--5",
/// "1.2.3"), missing parentheses, invalid hex digits and faulty names do not abort reading but are read as well as
/// possible.
/// </summary>
/// <remarks>A lexer has a position and is not meant for several threads; each reader uses its own.</remarks>
public sealed class PdfLexer
{
    private readonly PdfSource _source;

    /// <summary>Creates a lexer.</summary>
    /// <param name="source">The bytes to read.</param>
    /// <param name="position">Start position.</param>
    public PdfLexer(PdfSource source, long position = 0)
    {
        _source = source;
        Position = position;
    }

    /// <summary>The bytes being read.</summary>
    public PdfSource Source => _source;

    /// <summary>Current position in the source; the next token is read from here.</summary>
    public long Position { get; set; }

    /// <summary>Checks for a PDF white-space character (ISO 32000-2, 7.2.3).</summary>
    /// <param name="c">The byte value, or -1.</param>
    /// <returns>True for NUL, tab, line feed, form feed, carriage return and space.</returns>
    public static bool IsWhitespace(int c) => c is 0 or 9 or 10 or 12 or 13 or 32;

    /// <summary>Checks for a PDF delimiter character.</summary>
    /// <param name="c">The byte value, or -1.</param>
    /// <returns>True for ( ) &lt; &gt; [ ] { } / %.</returns>
    public static bool IsDelimiter(int c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    /// <summary>Checks for a regular character (neither white space nor delimiter).</summary>
    /// <param name="c">The byte value, or -1.</param>
    /// <returns>True for a regular character; false for -1 (end of data).</returns>
    public static bool IsRegular(int c) => c >= 0 && !IsWhitespace(c) && !IsDelimiter(c);

    /// <summary>Skips white space and comments.</summary>
    public void SkipWhitespace()
    {
        while (true)
        {
            var c = _source.At(Position);
            if (IsWhitespace(c))
            {
                Position++;
            }
            else if (c == '%')
            {
                while (_source.At(Position) is not (-1 or 10 or 13)) Position++;
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>Reads the next token and advances the position past it.</summary>
    /// <returns>The token; <see cref="PdfTokenKind.EndOfFile"/> at the end of the data.</returns>
    public PdfToken Next()
    {
        SkipWhitespace();
        // Skip unmatched ")" (a loop rather than recursion: thousands of them in a malicious file must not
        // overflow the stack)
        while (_source.At(Position) == ')')
        {
            Position++;
            SkipWhitespace();
        }
        var start = Position;
        var c = _source.At(Position);
        switch (c)
        {
            case -1:
                return new PdfToken(PdfTokenKind.EndOfFile, start);
            case '[':
                Position++;
                return new PdfToken(PdfTokenKind.ArrayStart, start);
            case ']':
                Position++;
                return new PdfToken(PdfTokenKind.ArrayEnd, start);
            case '{' or '}':
                // Only in PostScript functions; passed on as a keyword
                Position++;
                return new PdfToken(PdfTokenKind.Keyword, start, Bytes: [(byte)c]);
            case '/':
                Position++;
                return new PdfToken(PdfTokenKind.Name, start, Name: ReadName());
            case '(':
                Position++;
                return new PdfToken(PdfTokenKind.String, start, Bytes: ReadLiteralString());
            case '<':
                if (_source.At(Position + 1) == '<')
                {
                    Position += 2;
                    return new PdfToken(PdfTokenKind.DictionaryStart, start);
                }
                Position++;
                return new PdfToken(PdfTokenKind.HexString, start, Bytes: ReadHexString());
            case '>':
                // ">>"; a single ">" is an error: skip it and treat it as the end
                Position += _source.At(Position + 1) == '>' ? 2 : 1;
                return new PdfToken(PdfTokenKind.DictionaryEnd, start);
        }

        if (c is >= '0' and <= '9' or '+' or '-' or '.')
        {
            var number = ReadNumber(start);
            if (number is { } token) return token;
            Position = start;
        }

        // Keyword: all regular characters up to the next white space or delimiter
        while (IsRegular(_source.At(Position))) Position++;
        if (Position == start) Position++;   // unknown control character: advance one byte
        return new PdfToken(PdfTokenKind.Keyword, start, Bytes: KeywordBytes(_source.Span(start, Position - start)));
    }

    /// <summary>Reads the next token without changing the position.</summary>
    /// <returns>The token.</returns>
    public PdfToken Peek()
    {
        var position = Position;
        var token = Next();
        Position = position;
        return token;
    }

    // ------------------------------------------------------------ Numbers

    /// <summary>
    /// Reads a number; null if the token is not a number (it is then a keyword).
    /// As in common PDF readers: multiple signs ("--5") count as one (each "-" flips the sign), a second point ends the number,
    /// and "-" alone is 0.
    /// </summary>
    private PdfToken? ReadNumber(long start)
    {
        var negative = false;
        var c = _source.At(Position);
        while (c is '+' or '-')
        {
            if (c == '-') negative = !negative;
            Position++;
            c = _source.At(Position);
        }

        long integer = 0;
        var digits = 0;
        var overflow = false;
        var digitsStart = Position;
        while (c is >= '0' and <= '9')
        {
            if (integer > (long.MaxValue - 9) / 10) overflow = true;
            else integer = integer * 10 + (c - '0');
            digits++;
            Position++;
            c = _source.At(Position);
        }
        // Beyond long: only the digits before the point as a double (the fraction is added below, a second point is skipped)
        var whole = overflow ? ParseDigits(digitsStart, Position) : integer;

        if (c != '.')
        {
            if (IsRegular(c))
            {
                // "12abc", "10e5": not a number but a keyword
                return null;
            }
            if (overflow) return new PdfToken(PdfTokenKind.Real, start, Real: negative ? -whole : whole);
            return new PdfToken(PdfTokenKind.Integer, start, Integer: negative ? -integer : integer);
        }

        // Fractional digits
        Position++;
        c = _source.At(Position);
        double fraction = 0, scale = 1;
        var fractionDigits = 0;
        while (c is >= '0' and <= '9')
        {
            if (fractionDigits < 17)
            {
                fraction = fraction * 10 + (c - '0');
                scale *= 10;
            }
            fractionDigits++;
            Position++;
            c = _source.At(Position);
        }
        // Second point or letters directly after it: skip the rest, the number ends here
        if (IsRegular(c))
        {
            if (digits == 0 && fractionDigits == 0) return null;
            while (IsRegular(_source.At(Position))) Position++;
        }

        var value = whole + fraction / scale;
        return new PdfToken(PdfTokenKind.Real, start, Real: negative ? -value : value);
    }

    private double ParseDigits(long from, long to)
    {
        var text = Encoding.ASCII.GetString(_source.Span(from, to - from));
        // more than 309 digits read as infinity: the largest double instead, which the writer writes back as it was read (No. 933)
        return double.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? (double.IsFinite(v) ? v : double.MaxValue) : 0;
    }

    // ------------------------------------------------------------ Names and strings

    // Buffer for names and strings, shared per thread: a lexer is created for each of the thousands of objects in a
    // file, and a separate buffer per lexer would take more memory than the object itself.
    // Safe because a name or string is read without interruption and then copied out.
    [ThreadStatic] private static byte[]? t_scratch;
    private byte[] _scratch = [];
    private int _length;

    private void BeginScratch()
    {
        _scratch = t_scratch ??= new byte[256];
        _length = 0;
    }

    private void Add(int b)
    {
        if (_length == _scratch.Length)
        {
            Array.Resize(ref _scratch, _scratch.Length * 2);
            t_scratch = _scratch;
        }
        _scratch[_length++] = (byte)b;
    }

    // Common keywords as shared arrays ("2 0 R" occurs tens of thousands of times in a file)
    private static readonly byte[][] CommonKeywords =
        [.. new[] { "R", "obj", "endobj", "stream", "endstream", "true", "false", "null", "xref", "trailer", "startxref", "f", "n", "ID", "BI", "EI" }
            .Select(System.Text.Encoding.ASCII.GetBytes)];

    private static byte[] KeywordBytes(ReadOnlySpan<byte> word)
    {
        foreach (var known in CommonKeywords)
            if (word.SequenceEqual(known)) return known;
        return word.ToArray();
    }

    /// <summary>Reads a name as a finished <see cref="PdfName"/> (equal names are the same object; no intermediate strings).</summary>
    private PdfName ReadName()
    {
        BeginScratch();
        while (true)
        {
            var c = _source.At(Position);
            if (!IsRegular(c)) break;
            Position++;
            if (c == '#' && HexValue(_source.At(Position)) is var hi and >= 0 && HexValue(_source.At(Position + 1)) is var lo and >= 0)
            {
                Add(hi * 16 + lo);
                Position += 2;
            }
            else
            {
                Add(c);
            }
        }
        return PdfName.Get(_scratch.AsSpan(0, _length));
    }

    private byte[] ReadLiteralString()
    {
        BeginScratch();
        var depth = 1;
        while (true)
        {
            var c = _source.At(Position);
            if (c == -1) break;   // not closed: up to the end of the file
            Position++;
            switch (c)
            {
                case '(':
                    depth++;
                    Add(c);
                    break;
                case ')':
                    if (--depth == 0) return _scratch.AsSpan(0, _length).ToArray();
                    Add(c);
                    break;
                case '\\':
                    ReadEscape();
                    break;
                case '\r':
                    // An end of line in a string is always \n (ISO 32000-2, 7.3.4.2)
                    if (_source.At(Position) == '\n') Position++;
                    Add('\n');
                    break;
                default:
                    Add(c);
                    break;
            }
        }
        return _scratch.AsSpan(0, _length).ToArray();
    }

    private void ReadEscape()
    {
        var c = _source.At(Position);
        if (c == -1) return;
        Position++;
        switch (c)
        {
            case 'n': Add('\n'); break;
            case 'r': Add('\r'); break;
            case 't': Add('\t'); break;
            case 'b': Add(8); break;
            case 'f': Add(12); break;
            case '\r':
                // Line continuation
                if (_source.At(Position) == '\n') Position++;
                break;
            case '\n':
                break;
            case >= '0' and <= '7':
                var value = c - '0';
                for (var i = 0; i < 2 && _source.At(Position) is >= '0' and <= '7'; i++)
                {
                    value = value * 8 + (_source.At(Position) - '0');
                    Position++;
                }
                Add(value & 0xFF);   // above 255: the high-order bit is ignored (ISO 32000-2, 7.3.4.2)
                break;
            default:
                // \( \) \\ and unknown escapes: the character itself
                Add(c);
                break;
        }
    }

    private byte[] ReadHexString()
    {
        BeginScratch();
        var high = -1;
        while (true)
        {
            var c = _source.At(Position);
            if (c == -1) break;
            Position++;
            if (c == '>') break;
            var v = HexValue(c);
            if (v < 0) continue;   // skip white space and invalid characters
            if (high < 0)
            {
                high = v;
            }
            else
            {
                Add(high * 16 + v);
                high = -1;
            }
        }
        if (high >= 0) Add(high * 16);   // odd number of digits: pad the last one with 0
        return _scratch.AsSpan(0, _length).ToArray();
    }

    private static int HexValue(int c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    // ------------------------------------------------------------ Helpers for the parser and repair

    /// <summary>Checks whether the keyword <paramref name="keyword"/> stands at a position, followed by white space,
    /// a delimiter or the end of the data.</summary>
    /// <param name="position">The position to check.</param>
    /// <param name="keyword">The keyword (ASCII).</param>
    /// <returns>True if it matches.</returns>
    public bool IsKeywordAt(long position, string keyword)
    {
        for (var i = 0; i < keyword.Length; i++)
            if (_source.At(position + i) != keyword[i]) return false;
        return !IsRegular(_source.At(position + keyword.Length));
    }
}
