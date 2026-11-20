using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Pdf.Fonts;

/// <summary>
/// CMap (ISO 32000-2, 9.7.5; Adobe TN 5014): maps character codes of any length (1 to 4 bytes) to CIDs or to
/// Unicode (ToUnicode, ISO 32000-2, 9.10.3). Supports embedded CMaps, "usecmap" and the predefined
/// Identity-H/V.
/// </summary>
public sealed class CMap
{
    private readonly List<(int Bytes, uint Low, uint High)> _codespace = [];
    private readonly List<(int Bytes, uint Low, uint High, int Cid)> _cidRanges = [];
    private readonly Dictionary<(int Bytes, uint Code), int> _cidSingles = [];
    private readonly Dictionary<(int Bytes, uint Code), string> _unicodeSingles = [];

    /// <summary>Codes whose target, as written, holds the value 0, U+FEFF or U+FFFE (the text drops them; PDF/A asks: docs/FEHLERGEDAECHTNIS.md, No. 501).</summary>
    private HashSet<(int Bytes, uint Code)>? _forbiddenTargets;
    private readonly List<(int Bytes, uint Low, uint High, byte[]? Start, string[]? Array)> _unicodeRanges = [];
    private RangeIndex? _cidIndex;       // built on the first lookup after parsing
    // the CMap included by "usecmap": its code → CID mappings come after the own ones — a vertical CMap includes its horizontal one and
    // overrides the codes of the vertical forms (docs/FEHLERGEDAECHTNIS.md, No. 513)
    private CMap? _base;
    private RangeIndex? _unicodeIndex;
    private bool _identity;

    private const int MaxCodespaceRanges = 64;

    /// <summary>
    /// Longest Unicode target of a bfchar/bfrange entry (Adobe Technical Note #5411, ToUnicode Mapping File Tutorial: at most 512 bytes).
    /// Longer targets are dropped: an unterminated hex string in a damaged file otherwise turns every character into thousands of letters.
    /// </summary>
    private const int MaxTargetBytes = 512;

    /// <summary>The /CMapName of the CMap; empty if it has none.</summary>
    public string Name { get; private set; } = "";

    /// <summary>Writing mode: 0 = horizontal, 1 = vertical.</summary>
    public int WMode { get; private set; }

    /// <summary>True for Identity-H/V and CMaps that use them without their own CID mappings.</summary>
    public bool IsIdentity => _identity;

    /// <summary>True if the CMap contains Unicode mappings (bfchar/bfrange).</summary>
    public bool HasUnicode => _unicodeSingles.Count > 0 || _unicodeRanges.Count > 0;

    /// <summary>Creates Identity-H or Identity-V: two-byte codes, CID = code.</summary>
    /// <param name="vertical">True for Identity-V (writing mode 1).</param>
    /// <returns>A new CMap.</returns>
    public static CMap Identity(bool vertical)
    {
        var cmap = new CMap { Name = vertical ? "Identity-V" : "Identity-H", WMode = vertical ? 1 : 0, _identity = true };
        cmap._codespace.Add((2, 0, 0xFFFF));
        return cmap;
    }

    /// <summary>Parses a CMap. Lenient: unknown or malformed parts are skipped.</summary>
    /// <param name="data">The CMap program (decoded stream content).</param>
    /// <param name="useCMap">Resolves CMaps referenced by "usecmap" by name; null ignores such references.</param>
    /// <returns>The parsed CMap; empty if nothing could be read.</returns>
    public static CMap Parse(byte[] data, Func<string, CMap?>? useCMap = null)
    {
        var cmap = new CMap();
        var tokens = new Tokenizer(data);
        var operands = new List<Token>();
        while (tokens.Next() is { } token)
        {
            if (token.Kind != TokenKind.Keyword)
            {
                operands.Add(token);
                if (operands.Count > 64) operands.RemoveAt(0);
                continue;
            }
            switch (token.Text)
            {
                case "begincodespacerange":
                    cmap.ReadPairs(ref tokens, "endcodespacerange", (lo, hi) =>
                    {
                        // real CMaps have a handful of codespace ranges; they are searched for every character
                        if (cmap._codespace.Count < MaxCodespaceRanges) cmap._codespace.Add((lo.Length, ToCode(lo), ToCode(hi)));
                    });
                    break;
                case "begincidrange":
                    cmap.ReadTriples(ref tokens, "endcidrange", (lo, hi, dst) =>
                    {
                        if (dst.Kind == TokenKind.Number) cmap._cidRanges.Add((lo.Length, ToCode(lo), ToCode(hi), (int)dst.Number));
                    });
                    break;
                case "begincidchar":
                    cmap.ReadPairs(ref tokens, "endcidchar", (code, dst) =>
                    {
                        if (dst.Kind == TokenKind.Number) cmap._cidSingles[(code.Length, ToCode(code))] = (int)dst.Number;
                    }, secondAnyToken: true);
                    break;
                case "beginbfchar":
                    cmap.ReadPairs(ref tokens, "endbfchar", (code, dst) =>
                    {
                        if (UnicodeOf(dst) is { } text) cmap._unicodeSingles[(code.Length, ToCode(code))] = text;
                        if (dst.Kind == TokenKind.Hex && Forbidden(dst.Bytes)) (cmap._forbiddenTargets ??= []).Add((code.Length, ToCode(code)));
                    }, secondAnyToken: true);
                    break;
                case "beginbfrange":
                    cmap.ReadBfRanges(ref tokens);
                    break;
                case "usecmap" when operands.Count > 0 && operands[^1].Kind == TokenKind.Name:
                    if (useCMap?.Invoke(operands[^1].Text) is { } parent) cmap.Include(parent);
                    break;
                case "def" when operands.Count >= 2 && operands[^2].Kind == TokenKind.Name:
                    if (operands[^2].Text == "WMode" && operands[^1].Kind == TokenKind.Number) cmap.WMode = (int)operands[^1].Number;
                    if (operands[^2].Text == "CMapName" && operands[^1].Kind == TokenKind.Name) cmap.Name = operands[^1].Text;
                    break;
            }
            operands.Clear();
        }
        return cmap;
    }

    /// <summary>All mappings code → CID (single entries and ranges expanded, at most 65,536 per range), for inverting the Uni… CMaps.</summary>
    internal IEnumerable<(uint Code, int Bytes, int Cid)> CidMappings()
    {
        foreach (var ((bytes, code), cid) in _cidSingles) yield return (code, bytes, cid);
        foreach (var (bytes, low, high, cid) in _cidRanges)
            for (var c = low; c <= high && c - low < 65536; c++) yield return (c, bytes, cid + (int)(c - low));
        if (_base is not null)
            foreach (var (code, bytes, cid) in _base.CidMappings())
                if (ToCid(code, bytes) == cid) yield return (code, bytes, cid);   // (only where the own mappings do not override it)
    }

    /// <summary>All codes with a Unicode mapping (ranges expanded, at most 65,536 per range), for inverting the mapping (text editing; see KONZEPT.md, section 5.14).</summary>
    internal IEnumerable<(uint Code, int Bytes)> UnicodeCodes()
    {
        foreach (var (bytes, code) in _unicodeSingles.Keys) yield return (code, bytes);
        foreach (var (bytes, low, high, _, _) in _unicodeRanges)
            for (var c = low; c <= high && c - low < 65536; c++) yield return (c, bytes);
    }

    internal void Include(CMap parent)
    {
        _identity |= parent._identity && _cidRanges.Count == 0 && _cidSingles.Count == 0;
        if (_codespace.Count == 0) _codespace.AddRange(parent._codespace.Take(MaxCodespaceRanges));
        _base = parent;
        foreach (var (k, v) in parent._unicodeSingles) _unicodeSingles.TryAdd(k, v);
        _unicodeRanges.AddRange(parent._unicodeRanges);
        if (WMode == 0) WMode = parent.WMode;
    }

    // ------------------------------------------------------------ Lookups

    /// <summary>
    /// Reads the next code starting at <paramref name="pos"/> (ISO 32000-2, 9.7.6.2): as many bytes as needed until the code lies in
    /// a codespace range of that length.
    /// </summary>
    /// <param name="s">The string bytes.</param>
    /// <param name="pos">Start position; must be inside <paramref name="s"/>.</param>
    /// <param name="code">The code read.</param>
    /// <returns>The number of bytes consumed (at least 1). If no range matches, as many bytes as the shortest range, so that
    /// reading always advances.</returns>
    public int ReadCode(ReadOnlySpan<byte> s, int pos, out uint code)
    {
        code = 0;
        if (_codespace.Count == 0)
        {
            code = s[pos];
            return 1;
        }
        for (var n = 1; n <= 4 && pos + n <= s.Length; n++)
        {
            code = code << 8 | s[pos + n - 1];
            foreach (var range in _codespace)
                if (range.Bytes == n && Prefix(range, s, pos, n) == n) return n;
        }
        // no range matches (9.7.6.3): the length of the range with the longest partial match — the most leading bytes within its bounds,
        // of equal ones the shortest codes; if the first byte starts no range, the shortest range. Before, always the shortest range: a
        // broken two-byte code was split, and its second byte read as a code of its own (docs/FEHLERGEDAECHTNIS.md, No. 771)
        int length = 0, matched = 0;
        foreach (var range in _codespace)
        {
            var k = Prefix(range, s, pos, range.Bytes);
            if (k > matched || k == matched && k > 0 && range.Bytes < length) (matched, length) = (k, range.Bytes);
        }
        if (matched == 0) length = _codespace.Min(c => c.Bytes);
        length = Math.Clamp(length, 1, s.Length - pos);
        code = 0;
        for (var i = 0; i < length; i++) code = code << 8 | s[pos + i];
        return length;
    }

    /// <summary>
    /// How many of the first <paramref name="count"/> bytes from <paramref name="pos"/> lie within the bounds of <paramref name="range"/>, byte
    /// by byte (9.7.6.2: each byte between the corresponding bytes of the lower and upper bound — not the code as a number).
    /// </summary>
    private static int Prefix((int Bytes, uint Low, uint High) range, ReadOnlySpan<byte> s, int pos, int count)
    {
        var k = 0;
        for (; k < Math.Min(count, range.Bytes) && pos + k < s.Length; k++)
        {
            var shift = 8 * (range.Bytes - 1 - k);
            var b = s[pos + k];
            if (b < ((range.Low >> shift) & 0xFF) || b > ((range.High >> shift) & 0xFF)) break;
        }
        return k;
    }

    /// <summary>
    /// Whether a code <see cref="ReadCode"/> read lies in a code space range of its length: not so for one no range has, or one a string ended
    /// in the middle of — readers take such a code differently (ISO 32000-2, 9.7.6.3 leaves it open).
    /// </summary>
    /// <param name="code">The code.</param>
    /// <param name="length">Its length in bytes.</param>
    public bool InCodespace(uint code, int length)
    {
        if (_codespace.Count == 0) return length == 1;
        if (length is < 1 or > 4) return false;
        Span<byte> bytes = stackalloc byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)(code >> (8 * (length - 1 - i)));
        foreach (var range in _codespace)
            if (range.Bytes == length && Prefix(range, bytes, 0, length) == length) return true;
        return false;
    }

    /// <summary>Maps a code to its CID.</summary>
    /// <param name="code">The code.</param>
    /// <param name="length">Length of the code in bytes.</param>
    /// <returns>The CID; 0 if the code is not mapped.</returns>
    public int ToCid(uint code, int length)
    {
        if (_cidSingles.TryGetValue((length, code), out var cid)) return cid;
        _cidIndex ??= RangeIndex.Build(_cidRanges.Select(r => (r.Bytes, r.Low, r.High)).ToList());
        if (_cidIndex.Find(length, code) is var i and >= 0) return _cidRanges[i].Cid + (int)(code - _cidRanges[i].Low);
        if (_base?.ToCid(code, length) is > 0 and var inherited) return inherited;
        if (_identity) return (int)code;
        // encoding CMap written in ToUnicode notation (bfchar/bfrange instead of cidchar/cidrange, bug920426): target taken as the CID, as in other readers
        if (_base is null && _cidSingles.Count == 0 && _cidRanges.Count == 0 && ToUnicode(code, length) is { Length: 1 } bf) return bf[0];
        return 0;
    }

    /// <summary>
    /// Whether the target of a code, as written in the CMap, holds the value 0, U+FEFF or U+FFFE — which <see cref="ToUnicode"/> leaves out of
    /// the text (ISO 19005-4, 6.2.10.7; ISO 19005-2, 6.2.11.7.2; docs/FEHLERGEDAECHTNIS.md, No. 501).
    /// </summary>
    internal bool ForbiddenTarget(uint code, int length)
    {
        if (_forbiddenTargets?.Contains((length, code)) == true) return true;
        if (_unicodeSingles.ContainsKey((length, code))) return false;
        _unicodeIndex ??= RangeIndex.Build(_unicodeRanges.Select(r => (r.Bytes, r.Low, r.High)).ToList());
        if (_unicodeIndex.Find(length, code) is not (var found and >= 0)) return false;
        var (_, low, _, start, array) = _unicodeRanges[found];
        if (array is not null || start is null) return false;   // (arrays: their entries are in the set)
        var dst = (byte[])start.Clone();
        var carry = (int)(code - low);
        for (var i = dst.Length - 1; i >= 0 && carry > 0; i--)
        {
            var v = dst[i] + carry;
            dst[i] = (byte)v;
            carry = v >> 8;
        }
        return Forbidden(dst);
    }

    /// <summary>A target holding 0, U+FEFF or U+FFFE: one byte 0, or a UTF-16 unit of these values.</summary>
    private static bool Forbidden(byte[] bytes)
    {
        if (bytes.Length == 1) return bytes[0] == 0;
        for (var i = 0; i + 1 < bytes.Length; i += 2)
            if ((bytes[i], bytes[i + 1]) is (0, 0) or (0xFE, 0xFF) or (0xFF, 0xFE)) return true;
        return false;
    }

    /// <summary>Maps a code to its Unicode text.</summary>
    /// <param name="code">The code.</param>
    /// <param name="length">Length of the code in bytes; single entries with another length are also accepted.</param>
    /// <returns>The text (may be more than one character, for example for ligatures); null if the code is not mapped.</returns>
    public string? ToUnicode(uint code, int length)
    {
        if (_unicodeSingles.TryGetValue((length, code), out var text)) return text;
        _unicodeIndex ??= RangeIndex.Build(_unicodeRanges.Select(r => (r.Bytes, r.Low, r.High)).ToList());
        if (_unicodeIndex.Find(length, code) is var found and >= 0)
        {
            var (_, low, _, start, array) = _unicodeRanges[found];
            var offset = (int)(code - low);
            if (array is not null) return offset < array.Length ? array[offset] : null;
            // increment the last byte of the target
            var dst = (byte[])start!.Clone();
            var carry = offset;
            for (var i = dst.Length - 1; i >= 0 && carry > 0; i--)
            {
                var v = dst[i] + carry;
                dst[i] = (byte)v;
                carry = v >> 8;
            }
            return Utf16(dst);
        }
        // some ToUnicode CMaps give codes with a different length than the font uses: search leniently
        // (look up directly per length instead of iterating over all entries)
        for (var bytes = 1; bytes <= 4; bytes++)
            if (bytes != length && _unicodeSingles.TryGetValue((bytes, code), out text)) return text;
        return null;
    }

    /// <summary>
    /// Ranges per code length, sorted by their start: search in log n instead of through all of them (predefined and crafted CMaps
    /// have tens of thousands). On overlap the first range in the file wins, as with a linear search; overlapping ranges are followed
    /// back at most <see cref="MaxOverlapSteps"/> steps (real CMaps do not overlap).
    /// </summary>
    private sealed class RangeIndex
    {
        private const int MaxOverlapSteps = 256;
        private readonly (uint Low, uint High, uint MaxHigh, int Index)[]?[] _byLength = new (uint, uint, uint, int)[]?[5];

        public static RangeIndex Build(List<(int Bytes, uint Low, uint High)> ranges)
        {
            var index = new RangeIndex();
            for (var length = 1; length <= 4; length++)
            {
                var list = new List<(uint Low, uint High, uint MaxHigh, int Index)>();
                for (var i = 0; i < ranges.Count; i++)
                    if (ranges[i].Bytes == length) list.Add((ranges[i].Low, ranges[i].High, 0, i));
                if (list.Count == 0) continue;
                // stable sort by start, then for each position the largest end so far (tells how far back a match can still lie)
                var sorted = list.OrderBy(r => r.Low).ToArray();
                uint max = 0;
                for (var i = 0; i < sorted.Length; i++)
                {
                    max = Math.Max(max, sorted[i].High);
                    sorted[i].MaxHigh = max;
                }
                index._byLength[length] = sorted;
            }
            return index;
        }

        /// <summary>Position (in file order) of the first range containing <paramref name="code"/>; -1 = none.</summary>
        public int Find(int length, uint code)
        {
            if ((uint)length >= 5 || _byLength[length] is not { } ranges) return -1;
            int lo = 0, hi = ranges.Length - 1, at = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) >>> 1;
                if (ranges[mid].Low <= code) { at = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            var best = -1;
            for (int i = at, steps = 0; i >= 0 && ranges[i].MaxHigh >= code && steps < MaxOverlapSteps; i--, steps++)
                if (ranges[i].High >= code && (best < 0 || ranges[i].Index < best)) best = ranges[i].Index;
            return best;
        }
    }

    // ------------------------------------------------------------ Parsing

    private void ReadPairs(ref Tokenizer tokens, string end, Action<byte[], Token> handle, bool secondAnyToken = false)
    {
        while (tokens.Next() is { } first)
        {
            if (first.Kind == TokenKind.Keyword && first.Text == end) return;
            if (first.Kind != TokenKind.Hex) continue;
            if (tokens.Next() is not { } second) return;
            if (second.Kind == TokenKind.Keyword && second.Text == end) return;
            if (second.Kind == TokenKind.Hex || secondAnyToken) handle(first.Bytes, second);
        }
    }

    private void ReadPairs(ref Tokenizer tokens, string end, Action<byte[], byte[]> handle) =>
        ReadPairs(ref tokens, end, (a, b) => { if (b.Kind == TokenKind.Hex) handle(a, b.Bytes); });

    private void ReadTriples(ref Tokenizer tokens, string end, Action<byte[], byte[], Token> handle)
    {
        while (tokens.Next() is { } first)
        {
            if (first.Kind == TokenKind.Keyword && first.Text == end) return;
            if (first.Kind != TokenKind.Hex) continue;
            if (tokens.Next() is not { Kind: TokenKind.Hex } second) continue;
            if (tokens.Next() is not { } third) return;
            handle(first.Bytes, second.Bytes, third);
        }
    }

    private void ReadBfRanges(ref Tokenizer tokens)
    {
        while (tokens.Next() is { } first)
        {
            if (first.Kind == TokenKind.Keyword && first.Text == "endbfrange") return;
            if (first.Kind != TokenKind.Hex) continue;
            if (tokens.Next() is not { Kind: TokenKind.Hex } second) continue;
            if (tokens.Next() is not { } third) return;
            var low = ToCode(first.Bytes);
            var high = ToCode(second.Bytes);
            if (high < low || high - low > 0xFFFF) continue;
            if (third.Kind == TokenKind.Hex)
            {
                if (third.Bytes.Length > MaxTargetBytes) continue;
                _unicodeRanges.Add((first.Bytes.Length, low, high, third.Bytes, null));
            }
            else if (third.Kind == TokenKind.ArrayStart)
            {
                var items = new List<string>();
                while (tokens.Next() is { } item && item.Kind != TokenKind.ArrayEnd)
                {
                    if (item.Kind == TokenKind.Hex && Forbidden(item.Bytes) && low + (uint)items.Count <= high)
                        (_forbiddenTargets ??= []).Add((first.Bytes.Length, low + (uint)items.Count));
                    items.Add(UnicodeOf(item) ?? "");
                }
                _unicodeRanges.Add((first.Bytes.Length, low, high, null, [.. items]));
            }
        }
    }

    private static uint ToCode(byte[] bytes)
    {
        uint code = 0;
        foreach (var b in bytes.AsSpan(0, Math.Min(bytes.Length, 4))) code = code << 8 | b;
        return code;
    }

    private static string? UnicodeOf(Token token) => token.Kind switch
    {
        TokenKind.Hex => token.Bytes.Length > MaxTargetBytes ? null : Utf16(token.Bytes),
        TokenKind.Name => Core.Fonts.GlyphNames.ToUnicode(token.Text),
        _ => null,
    };

    private static string Utf16(byte[] bytes)
    {
        if (bytes.Length == 1) return ((char)bytes[0]).ToString();
        var text = Encoding.BigEndianUnicode.GetString(bytes, 0, bytes.Length & ~1);
        return text.Replace("\0", "");
    }

    // ------------------------------------------------------------ Tokenizer

    private enum TokenKind { Number, Hex, Name, Keyword, ArrayStart, ArrayEnd, Other }

    private readonly record struct Token(TokenKind Kind, string Text, byte[] Bytes, double Number);

    private struct Tokenizer(byte[] data)
    {
        private int _p;

        public Token? Next()
        {
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
            var first = data[_p];
            switch (first)
            {
                case (byte)'[': _p++; return new Token(TokenKind.ArrayStart, "[", [], 0);
                case (byte)']': _p++; return new Token(TokenKind.ArrayEnd, "]", [], 0);
                case (byte)'<' when _p + 1 < data.Length && data[_p + 1] == '<':
                    _p += 2;
                    return new Token(TokenKind.Other, "<<", [], 0);
                case (byte)'>' when _p + 1 < data.Length && data[_p + 1] == '>':
                    _p += 2;
                    return new Token(TokenKind.Other, ">>", [], 0);
                case (byte)'<':
                {
                    _p++;
                    var bytes = new List<byte>();
                    var high = -1;
                    while (_p < data.Length && data[_p] != '>')
                    {
                        var ch = (char)data[_p++];
                        if (!Uri.IsHexDigit(ch)) continue;
                        var v = Uri.FromHex(ch);
                        if (high < 0) high = v;
                        else { bytes.Add((byte)(high << 4 | v)); high = -1; }
                    }
                    if (high >= 0) bytes.Add((byte)(high << 4));
                    _p++;
                    return new Token(TokenKind.Hex, "", [.. bytes], 0);
                }
                case (byte)'(':
                {
                    // literal string (rare in ToUnicode): as bytes
                    _p++;
                    var bytes = new List<byte>();
                    var depth = 1;
                    while (_p < data.Length)
                    {
                        var ch = data[_p++];
                        if (ch == '\\' && _p < data.Length) { bytes.Add(data[_p++]); continue; }
                        if (ch == '(') depth++;
                        else if (ch == ')' && --depth == 0) break;
                        bytes.Add(ch);
                    }
                    return new Token(TokenKind.Hex, "", [.. bytes], 0);
                }
                case (byte)'/':
                {
                    var start = ++_p;
                    while (_p < data.Length && !IsDelimiter(data[_p])) _p++;
                    return new Token(TokenKind.Name, Encoding.Latin1.GetString(data, start, _p - start), [], 0);
                }
            }
            var s = _p;
            _p++;
            while (_p < data.Length && !IsDelimiter(data[_p])) _p++;
            var text = Encoding.Latin1.GetString(data, s, _p - s);
            // "NaN" and "Infinity" are names in PostScript, not numbers (docs/FEHLERGEDAECHTNIS.md, No. 929)
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                return new Token(TokenKind.Number, text, [], number);
            return new Token(TokenKind.Keyword, text, [], 0);
        }

        private static bool IsDelimiter(byte b) =>
            b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0 or (byte)'[' or (byte)']'
                or (byte)'<' or (byte)'>' or (byte)'(' or (byte)')' or (byte)'/' or (byte)'%' or (byte)'{' or (byte)'}';
    }
}
