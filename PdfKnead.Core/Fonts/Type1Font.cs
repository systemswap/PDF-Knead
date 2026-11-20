using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using PdfKnead.Core.Pdf.Graphics;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Type 1 font (Adobe "Type 1 Font Format"): PFB, PFA or PDF FontFile (clear-text part +
/// eexec-encrypted part). Reads FontMatrix, encoding, Subrs and CharStrings and executes
/// the Type 1 charstrings (including flex, hint replacement and seac).
/// </summary>
/// <remarks>Glyph outlines are built on first use and cached up to 64 MB per font.</remarks>
public sealed class Type1Font
{
    private const int MaxSubrDepth = 10;

    private readonly Dictionary<string, byte[]> _charStrings;
    private readonly List<byte[]> _subrs;
    private readonly string[] _order;
    /// <summary>Outlines per glyph, at most 64 MB (<see cref="BoundedCache{TKey, TValue}"/>).</summary>
    private readonly BoundedCache<string, (GlyphOutline Outline, double Width)> _cache =
        new(64L << 20, g => g.Outline.EstimatedBytes, StringComparer.Ordinal);

    private Type1Font(string name, Matrix fontMatrix, string[] encoding, Dictionary<string, byte[]> charStrings, List<byte[]> subrs, string[] order)
    {
        _order = order;
        Name = name;
        FontMatrix = fontMatrix;
        Encoding = encoding;
        _charStrings = charStrings;
        _subrs = subrs;
    }

    /// <summary>The /FontName from the clear-text part; empty if missing.</summary>
    public string Name { get; }

    /// <summary>Maps font units to text space (1 = one em); a scale of 0.001 if the font gives no usable matrix.</summary>
    public Matrix FontMatrix { get; }

    /// <summary>Built-in encoding: code 0–255 → glyph name ("" = none).</summary>
    public string[] Encoding { get; }

    /// <summary>Names of all glyphs in the CharStrings dictionary.</summary>
    public IEnumerable<string> GlyphNames => _charStrings.Keys;

    /// <summary>Whether the font has a glyph of that name.</summary>
    /// <param name="name">Glyph name.</param>
    /// <returns>True if the CharStrings dictionary contains the name.</returns>
    public bool HasGlyph(string name) => _charStrings.ContainsKey(name);

    /// <summary>Returns the name of glyph number <paramref name="index"/> (order of the CharStrings, .notdef first), for Type 1 used as a CIDFont.</summary>
    /// <param name="index">Glyph number.</param>
    /// <returns>The glyph name, or null if the index is out of range.</returns>
    public string? GlyphNameAt(int index) => index >= 0 && index < _order.Length ? _order[index] : null;

    /// <summary>Returns the outline of a glyph in font units.</summary>
    /// <param name="name">Glyph name.</param>
    /// <returns>The outline; <see cref="GlyphOutline.Empty"/> for unknown names and charstrings that cannot be executed.</returns>
    public GlyphOutline Outline(string name) => Glyph(name).Outline;

    /// <summary>Returns the advance width of a glyph in font units (from hsbw/sbw).</summary>
    /// <param name="name">Glyph name.</param>
    /// <returns>The advance width; 0 for unknown names and charstrings that cannot be executed.</returns>
    public double AdvanceWidth(string name) => Glyph(name).Width;

    private (GlyphOutline Outline, double Width) Glyph(string name)
    {
        if (!_charStrings.TryGetValue(name, out var code)) return (GlyphOutline.Empty, 0);
        if (_cache.TryGetValue(name, out var cached)) return cached;
        return _cache.GetOrAdd(name, _ =>
        {
            try
            {
                return new CharStringInterpreter(this).Run(code);
            }
            catch (Exception e) when (e is FontFormatException or IndexOutOfRangeException or InvalidOperationException)
            {
                return (GlyphOutline.Empty, 0);
            }
        });
    }

    // ------------------------------------------------------------ Parsing

    /// <summary>Parses PFB, PFA or the data of a PDF FontFile stream.</summary>
    /// <param name="data">The font data.</param>
    /// <returns>The parsed font.</returns>
    /// <exception cref="FontFormatException">The data has no eexec part (PFA/FontFile) or no CharStrings, or a charstring lies outside the data.</exception>
    public static Type1Font Parse(byte[] data)
    {
        byte[] clear, encrypted;
        if (data.Length > 6 && data[0] == 0x80 && data[1] is 1 or 2)
        {
            (clear, encrypted) = SplitPfb(data);
        }
        else
        {
            var eexec = IndexOf(data, "eexec"u8);
            if (eexec < 0) throw new FontFormatException("Type 1 ohne eexec");
            var start = eexec + 5;
            while (start < data.Length && data[start] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') start++;
            clear = data[..eexec];
            encrypted = data[start..];
        }
        if (LooksHex(encrypted)) encrypted = HexDecode(encrypted);
        var priv = Decrypt(encrypted, 55665, 4);

        var header = new Scanner(clear);
        var name = "";
        var fontMatrix = new Matrix(0.001, 0, 0, 0.001, 0, 0);
        var encoding = new string[256];
        Array.Fill(encoding, "");
        while (header.Next() is { } token)
        {
            switch (token)
            {
                case "/FontName" when header.Next() is { } n && n.StartsWith('/'):
                    name = n[1..];
                    break;
                case "/FontMatrix":
                    if (header.ReadArray() is { Count: 6 } m && Math.Abs(m[0] * m[3] - m[1] * m[2]) > 1e-12)
                        fontMatrix = new Matrix(m[0], m[1], m[2], m[3], m[4], m[5]);
                    break;
                case "/Encoding":
                    ReadEncoding(ref header, encoding);
                    break;
            }
        }

        var (charStrings, subrs, order) = ReadPrivate(priv);
        if (charStrings.Count == 0) throw new FontFormatException("Type 1 ohne CharStrings");
        return new Type1Font(name, fontMatrix, encoding, charStrings, subrs, order);
    }

    private static (byte[] Clear, byte[] Encrypted) SplitPfb(byte[] data)
    {
        var clear = new MemoryStream();
        var binary = new MemoryStream();
        var p = 0;
        while (p + 6 <= data.Length && data[p] == 0x80)
        {
            var type = data[p + 1];
            if (type == 3) break;
            var length = data[p + 2] | data[p + 3] << 8 | data[p + 4] << 16 | data[p + 5] << 24;
            p += 6;
            if (length < 0 || p + length > data.Length) length = data.Length - p;
            (type == 1 && binary.Length == 0 ? clear : binary).Write(data, p, length);
            p += length;
        }
        return (clear.ToArray(), binary.ToArray());
    }

    private static void ReadEncoding(ref Scanner s, string[] encoding)
    {
        var first = s.Next();
        if (first == "StandardEncoding")
        {
            Array.Copy(FontTables.StandardEncoding, encoding, 256);
            return;
        }
        // "256 array 0 1 255 {1 index exch /.notdef put} for dup 32 /space put … readonly def"
        while (s.Next() is { } token)
        {
            if (token is "def" or "readonly") return;
            if (token != "dup") continue;
            if (s.Next() is not { } codeText || !int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)) continue;
            if (s.Next() is { } glyph && glyph.StartsWith('/') && code is >= 0 and < 256) encoding[code] = glyph[1..];
        }
    }

    private static (Dictionary<string, byte[]>, List<byte[]>, string[]) ReadPrivate(byte[] priv)
    {
        var charStrings = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var order = new List<string>();
        var subrs = new List<byte[]>();
        var lenIV = 4;
        var s = new Scanner(priv);
        while (s.Next() is { } token)
        {
            if (token == "/lenIV" && s.Next() is { } liv && int.TryParse(liv, CultureInfo.InvariantCulture, out var l)) lenIV = l;
            else if (token == "/Subrs")
            {
                // "/Subrs n array dup i len RD <bytes> NP …" up to the CharStrings. A second /Subrs (the "hires" branch of some
                // fonts, e.g. the test file issue18548: only empty subroutines) does not overwrite the first, as in common PDF readers
                var target = subrs.Count == 0 ? subrs : [];
                while (s.Next() is { } t)
                {
                    if (t is "/CharStrings" or "/Subrs")
                    {
                        s.Back(t);
                        break;
                    }
                    if (t != "dup") continue;
                    if (!int.TryParse(s.Next(), CultureInfo.InvariantCulture, out var index)) continue;
                    if (!int.TryParse(s.Next(), CultureInfo.InvariantCulture, out var length)) continue;
                    s.Next();                                       // RD or -|
                    var bytes = s.Binary(length);
                    while (target.Count <= index && index < 65536) target.Add([]);
                    if (index >= 0 && index < target.Count) target[index] = Decrypt(bytes, 4330, lenIV);
                }
            }
            else if (token == "/CharStrings")
            {
                // "/CharStrings n dict dup begin /name len RD <bytes> ND … end"
                while (s.Next() is { } t)
                {
                    if (t == "end") break;
                    if (!t.StartsWith('/')) continue;
                    if (!int.TryParse(s.Next(), CultureInfo.InvariantCulture, out var length)) continue;
                    s.Next();                                       // RD or -|
                    var bytes = s.Binary(length);
                    if (!charStrings.ContainsKey(t[1..])) order.Add(t[1..]);
                    charStrings[t[1..]] = Decrypt(bytes, 4330, lenIV);
                }
            }
        }
        // .notdef first, otherwise the order in the file (common PDF readers number them the same way when converting to CFF)
        if (order.IndexOf(".notdef") is > 0 and var notdef)
        {
            order.RemoveAt(notdef);
            order.Insert(0, ".notdef");
        }
        return (charStrings, subrs, [.. order]);
    }

    /// <summary>eexec or charstring decryption; the first <paramref name="skip"/> bytes are random.</summary>
    private static byte[] Decrypt(ReadOnlySpan<byte> data, ushort key, int skip)
    {
        if (skip < 0) return data.ToArray();                          // lenIV -1: not encrypted
        var r = key;
        var result = new byte[Math.Max(0, data.Length - skip)];
        for (var i = 0; i < data.Length; i++)
        {
            var c = data[i];
            var plain = (byte)(c ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
            if (i >= skip) result[i - skip] = plain;
        }
        return result;
    }

    private static bool LooksHex(byte[] data)
    {
        if (data.Length < 4) return false;
        for (var i = 0; i < 4; i++)
            if (!Uri.IsHexDigit((char)data[i])) return false;
        return true;
    }

    private static byte[] HexDecode(byte[] data)
    {
        var result = new List<byte>(data.Length / 2);
        var high = -1;
        foreach (var b in data)
        {
            if (!Uri.IsHexDigit((char)b)) continue;
            var v = Uri.FromHex((char)b);
            if (high < 0) high = v;
            else
            {
                result.Add((byte)(high << 4 | v));
                high = -1;
            }
        }
        return [.. result];
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> what) => data.AsSpan().IndexOf(what);

    /// <summary>Simple PostScript tokenizer for the parts a Type 1 font needs.</summary>
    private struct Scanner(byte[] data)
    {
        private int _p;
        private string? _pushedBack;

        public void Back(string token) => _pushedBack = token;

        public string? Next()
        {
            if (_pushedBack is { } back)
            {
                _pushedBack = null;
                return back;
            }
            while (_p < data.Length)
            {
                var c = data[_p];
                if (c == '%')
                {
                    while (_p < data.Length && data[_p] is not ((byte)'\n' or (byte)'\r')) _p++;
                    continue;
                }
                if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0) { _p++; continue; }
                break;
            }
            if (_p >= data.Length) return null;
            var start = _p;
            var first = data[_p];
            if (first is (byte)'[' or (byte)']' or (byte)'{' or (byte)'}')
            {
                _p++;
                return ((char)first).ToString();
            }
            if (first == '(')
            {
                // Skip a string (with nested parentheses)
                var depth = 0;
                while (_p < data.Length)
                {
                    var c = data[_p++];
                    if (c == '\\') { _p++; continue; }
                    if (c == '(') depth++;
                    else if (c == ')' && --depth == 0) break;
                }
                return "()";
            }
            _p++;
            while (_p < data.Length && data[_p] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0
                       or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'(' or (byte)'/' or (byte)'%'))
                _p++;
            return System.Text.Encoding.Latin1.GetString(data, start, _p - start);
        }

        /// <summary>Binary data after "RD": exactly one space, then <paramref name="length"/> bytes.</summary>
        public byte[] Binary(int length)
        {
            _p++;
            if (length < 0 || _p > data.Length - length) throw new FontFormatException("Charstring außerhalb der Daten");
            var bytes = data.AsSpan(_p, length).ToArray();
            _p += length;
            return bytes;
        }

        public List<double>? ReadArray()
        {
            var open = Next();
            if (open is not ("[" or "{")) return null;
            var values = new List<double>();
            while (Next() is { } t && t is not ("]" or "}"))
                // "NaN" and "Infinity" are names in PostScript, not numbers (docs/FEHLERGEDAECHTNIS.md, No. 929, 933)
                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)) values.Add(v);
            return values;
        }
    }

    // ------------------------------------------------------------ Type 1 charstrings

    private sealed class CharStringInterpreter(Type1Font font)
    {
        private readonly GlyphOutline _outline = new();
        // Work limit per glyph: subroutines that call each other many times grow exponentially despite the depth limit
        private const int MaxSteps = 200_000;
        private int _steps;

        /// <summary>Stack for "othersubr" and "pop", limited to 256 entries so that it cannot grow without bound with every call.</summary>
        private void PsPush(double v)
        {
            if (_psStack.Count < 256) _psStack.Push(v);
        }
        private readonly double[] _stack = new double[64];
        private readonly Stack<double> _psStack = new();
        private readonly List<(double X, double Y)> _flex = [];
        private int _sp;
        private double _x, _y;
        private double _width;
        private bool _flexing;
        private bool _ended;

        public (GlyphOutline, double) Run(byte[] code)
        {
            Execute(code, 0);
            return (_outline.Finish(), _width);
        }

        private void Push(double v)
        {
            if (_sp < _stack.Length) _stack[_sp++] = v;
        }

        private double Arg(int k) => k < _sp ? _stack[k] : 0;

        private void Execute(byte[] code, int depth)
        {
            if (depth > MaxSubrDepth) throw new FontFormatException("Subroutinen zu tief");
            var i = 0;
            while (i < code.Length && !_ended)
            {
                int v = code[i++];
                if (++_steps > MaxSteps) throw new FontFormatException("Glyphe zu aufwendig");
                if (v >= 32)
                {
                    if (v <= 246) Push(v - 139);
                    else if (v <= 250) Push((v - 247) * 256 + At(code, i++) + 108);
                    else if (v <= 254) Push(-(v - 251) * 256 - At(code, i++) - 108);
                    else
                    {
                        Push(At(code, i) << 24 | At(code, i + 1) << 16 | At(code, i + 2) << 8 | At(code, i + 3));
                        i += 4;
                    }
                    continue;
                }
                switch (v)
                {
                    case 13:                    // hsbw: sbx wx
                        _x = Arg(0);
                        _y = 0;
                        _width = Arg(1);
                        break;
                    case 9:                     // closepath
                        _outline.Close();
                        break;
                    case 21:                    // rmoveto
                        MoveBy(Arg(0), Arg(1));
                        break;
                    case 22:                    // hmoveto
                        MoveBy(Arg(0), 0);
                        break;
                    case 4:                     // vmoveto
                        MoveBy(0, Arg(0));
                        break;
                    case 5:                     // rlineto
                        LineBy(Arg(0), Arg(1));
                        break;
                    case 6:                     // hlineto
                        LineBy(Arg(0), 0);
                        break;
                    case 7:                     // vlineto
                        LineBy(0, Arg(0));
                        break;
                    case 8:                     // rrcurveto
                        CurveBy(Arg(0), Arg(1), Arg(2), Arg(3), Arg(4), Arg(5));
                        break;
                    case 30:                    // vhcurveto: dy1 dx2 dy2 dx3
                        CurveBy(0, Arg(0), Arg(1), Arg(2), Arg(3), 0);
                        break;
                    case 31:                    // hvcurveto: dx1 dx2 dy2 dy3
                        CurveBy(Arg(0), 0, Arg(1), Arg(2), 0, Arg(3));
                        break;
                    case 10:                    // callsubr
                    {
                        if (_sp == 0) break;
                        var index = (int)_stack[--_sp];
                        if (index >= 0 && index < font._subrs.Count) Execute(font._subrs[index], depth + 1);
                        continue;               // the stack belongs to the subroutine
                    }
                    case 11:                    // return
                        return;
                    case 14:                    // endchar
                        _outline.Close();
                        _ended = true;
                        return;
                    case 1 or 3:                // hstem, vstem
                        break;
                    case 12:
                        if (Escape(At(code, i++), depth)) continue;
                        break;
                }
                _sp = 0;
            }
        }

        /// <summary>Executes an escape operator; returns true if the stack is to be kept.</summary>
        private bool Escape(int op, int depth)
        {
            switch (op)
            {
                case 7:                         // sbw: sbx sby wx wy
                    _x = Arg(0);
                    _y = Arg(1);
                    _width = Arg(2);
                    return false;
                case 6:                         // seac: asb adx ady bchar achar
                    Seac(Arg(0), Arg(1), Arg(2), (int)Arg(3), (int)Arg(4));
                    _ended = true;
                    return false;
                case 12:                        // div
                    if (_sp >= 2)
                    {
                        _stack[_sp - 2] = _stack[_sp - 1] == 0 ? 0 : _stack[_sp - 2] / _stack[_sp - 1];
                        _sp--;
                    }
                    return true;
                case 16:                        // callothersubr: args… n othersubr#
                {
                    if (_sp < 2) return false;
                    var number = (int)_stack[--_sp];
                    var n = Math.Clamp((int)_stack[--_sp], 0, _sp);
                    var args = new double[n];
                    for (var k = n - 1; k >= 0; k--) args[k] = _stack[--_sp];
                    OtherSubr(number, args);
                    return true;
                }
                case 17:                        // pop: value from the PostScript stack
                    Push(_psStack.Count > 0 ? _psStack.Pop() : 0);
                    return true;
                case 33:                        // setcurrentpoint
                    _x = Arg(0);
                    _y = Arg(1);
                    return false;
                default:                        // dotsection, vstem3, hstem3 …
                    return false;
            }
        }

        private void OtherSubr(int number, double[] args)
        {
            switch (number)
            {
                case 1:                         // flex starts
                    _flexing = true;
                    _flex.Clear();
                    break;
                case 2:                         // flex point
                    if (_flexing) _flex.Add((_x, _y));
                    break;
                case 0:                         // flex ends: 7 points (reference point + two curves)
                    _flexing = false;
                    if (_flex.Count >= 7)
                    {
                        CurveTo(_flex[1], _flex[2], _flex[3]);
                        CurveTo(_flex[4], _flex[5], _flex[6]);
                    }
                    // End point for "pop pop setcurrentpoint"
                    if (args.Length >= 3)
                    {
                        PsPush(args[2]);
                        PsPush(args[1]);
                    }
                    break;
                case 3:                         // hint replacement: "pop" returns 3 → callsubr 3
                    PsPush(3);
                    break;
                default:                        // unknown: push the arguments back so that "pop" finds them
                    for (var k = args.Length - 1; k >= 0; k--) PsPush(args[k]);
                    break;
            }
        }

        private void Seac(double asb, double adx, double ady, int baseCode, int accentCode)
        {
            if (baseCode is < 0 or > 255 || accentCode is < 0 or > 255 || _seacDepth > 2) return;
            _seacDepth++;
            try
            {
                var baseName = FontTables.StandardEncoding[baseCode];
                var accentName = FontTables.StandardEncoding[accentCode];
                if (baseName.Length > 0) _outline.Append(font.Outline(baseName), Matrix.Identity);
                if (accentName.Length > 0) _outline.Append(font.Outline(accentName), Matrix.Translate(adx - asb, ady));
            }
            finally
            {
                _seacDepth--;
            }
        }

        [ThreadStatic]
        private static int _seacDepth;

        private static int At(byte[] code, int i) => i < code.Length ? code[i] : 0;

        private void MoveBy(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            if (!_flexing) _outline.MoveTo((float)_x, (float)_y);
        }

        private void LineBy(double dx, double dy)
        {
            _x += dx;
            _y += dy;
            _outline.LineTo((float)_x, (float)_y);
        }

        private void CurveBy(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
        {
            var x1 = _x + dx1;
            var y1 = _y + dy1;
            var x2 = x1 + dx2;
            var y2 = y1 + dy2;
            _x = x2 + dx3;
            _y = y2 + dy3;
            _outline.CubicTo((float)x1, (float)y1, (float)x2, (float)y2, (float)_x, (float)_y);
        }

        private void CurveTo((double X, double Y) a, (double X, double Y) b, (double X, double Y) c)
        {
            _outline.CubicTo((float)a.X, (float)a.Y, (float)b.X, (float)b.Y, (float)c.X, (float)c.Y);
            _x = c.X;
            _y = c.Y;
        }
    }
}
