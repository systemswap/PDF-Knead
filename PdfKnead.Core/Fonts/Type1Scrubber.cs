using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Scrubs a Type 1 font program (Adobe Type 1 Font Format, FontFile in PDF: clear text, eexec part, trailer) down to the glyphs that are
/// still shown, for redaction (docs/FEHLERGEDAECHTNIS.md, No. 180): the charstrings of all other glyphs leave the font together with their
/// names, a custom encoding loses their codes, and every subroutine no kept glyph calls becomes an empty "return" (its number stays, the
/// numbering of the others with it; 0 … 3 stay as the flex and hint replacement conventions require). Everything else is copied as
/// written, the eexec part encrypted anew.
/// </summary>
internal static partial class Type1Scrubber
{
    /// <summary>
    /// The segments of a PFB file (0x80 0x01 clear text, 0x80 0x02 binary, 0x80 0x03 end; each with its length, least significant byte
    /// first) joined to the form of a PDF font file: clear text, eexec part, trailer — with the lengths of the first two.
    /// </summary>
    private static (byte[] Data, int Clear, int Encrypted) FromPfb(byte[] data)
    {
        using var clear = new MemoryStream();
        using var binary = new MemoryStream();
        using var trailer = new MemoryStream();
        var p = 0;
        while (p + 6 <= data.Length && data[p] == 0x80 && data[p + 1] != 3)
        {
            var type = data[p + 1];
            var length = (long)(data[p + 2] | data[p + 3] << 8 | data[p + 4] << 16) | (long)data[p + 5] << 24;
            p += 6;
            if (type is not (1 or 2) || length > data.Length - p) throw new FontFormatException("Type 1: PFB-Segment beschädigt");
            (type == 2 ? binary : binary.Length == 0 ? clear : trailer).Write(data, p, (int)length);
            p += (int)length;
        }
        if (binary.Length == 0) throw new FontFormatException("Type 1: PFB ohne eexec-Teil");
        return ([.. clear.ToArray(), .. binary.ToArray(), .. trailer.ToArray()], (int)clear.Length, (int)binary.Length);
    }

    private const ushort EexecKey = 55665, CharStringKey = 4330;
    private const int MaxDepth = 10, MaxSteps = 200_000;

    /// <summary>Scrubs the font.</summary>
    /// <param name="data">The font program (clear text, binary or hexadecimal eexec part, trailer).</param>
    /// <param name="keep">The glyph names still shown; .notdef and the parts of accented characters (seac) are kept as well.</param>
    /// <param name="clearLength">/Length1 of the font file, if given: the length of the clear text.</param>
    /// <param name="encryptedLength">/Length2, if given: the length of the eexec part.</param>
    /// <returns>The new program and the lengths of its clear text, eexec part and trailer (/Length1, /Length2, /Length3).</returns>
    /// <exception cref="FontFormatException">The program cannot be read or rewritten (no eexec part, broken PFB segments, a charstring cut off).</exception>
    public static (byte[] Data, int Clear, int Encrypted, int Trailer) Scrub(byte[] data, IReadOnlySet<string> keep, int? clearLength = null, int? encryptedLength = null)
    {
        // PFB segments (a font file of a program, embedded as it was): joined to the usual form, whose lengths are then known
        if (data.Length > 6 && data[0] == 0x80) (data, clearLength, encryptedLength) = FromPfb(data);
        var eexec = data.AsSpan().IndexOf("eexec"u8);
        if (eexec < 0) throw new FontFormatException("Type 1 ohne eexec");
        var start = eexec + 5;
        while (start < data.Length && data[start] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t') start++;
        // the lengths of the font file where they fit; otherwise the trailer from its end (512 zeros and cleartomark — the encrypted part
        // itself may end in bytes that read as "0")
        int end;
        if (clearLength is { } l1 && encryptedLength is { } l2 && l1 >= start - 2 && l1 <= start && l2 > 0 && (long)l1 + l2 <= data.Length)
            (start, end) = (l1, l1 + l2);
        else end = Trailer(data, start);
        var encrypted = data[start..end];
        if (encrypted.Length >= 4 && encrypted.AsSpan(0, 4).ToArray().All(b => Uri.IsHexDigit((char)b))) encrypted = HexDecode(encrypted);
        var priv = Decrypt(encrypted, EexecKey, 4);

        var text = Encoding.Latin1.GetString(priv);
        var lenIV = LenIVPattern().Match(text) is { Success: true } m && int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var iv) ? Math.Clamp(iv, -1, 64) : 4;
        var subrs = Entries(priv, "/Subrs", charStrings: false);
        var glyphs = Entries(priv, "/CharStrings", charStrings: true);
        if (glyphs.Count == 0) throw new FontFormatException("Type 1 ohne CharStrings");

        // the glyphs kept (with seac parts) and the subroutines they call, by running them
        // a name defined twice: the last one counts, as when reading the font (both go or stay together)
        var byName = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var g in glyphs) byName[g.Key!] = g;
        var code = new Dictionary<int, byte[]>();
        foreach (var subr in subrs) code[subr.Index] = Decrypt(priv.AsSpan(subr.Data, subr.Length), CharStringKey, lenIV);
        var keptGlyphs = new HashSet<string>(StringComparer.Ordinal) { ".notdef" };
        var usedSubrs = new HashSet<int> { 0, 1, 2, 3 };
        var pending = new Stack<string>(keep.Append(".notdef"));
        var allSubrs = false;
        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (!byName.TryGetValue(name, out var glyph) || !keptGlyphs.Add(name) && name != ".notdef") continue;
            var run = new Runner(code);
            run.Execute(Decrypt(priv.AsSpan(glyph.Data, glyph.Length), CharStringKey, lenIV), 0);
            usedSubrs.UnionWith(run.Subrs);
            allSubrs |= run.Unknown;
            foreach (var part in run.SeacCodes)
                if (part is >= 0 and < 256 && FontTables.StandardEncoding[part] is { Length: > 0 } partName && !keptGlyphs.Contains(partName)) pending.Push(partName);
        }

        // the private part anew: removed glyphs without a trace, unused subroutines as "return"
        var output = new MemoryStream(priv.Length);
        var at = 0;
        void Copy(int to)
        {
            output.Write(priv, at, to - at);
            at = to;
        }
        var empty = Encrypt([.. new byte[Math.Max(0, lenIV)], 11], CharStringKey, lenIV);
        foreach (var entry in subrs.Concat(glyphs).OrderBy(e => e.Start))
        {
            if (entry.Key is { } name)
            {
                if (keptGlyphs.Contains(name)) continue;
                Copy(entry.Start);
                at = entry.End;   // the whole entry: name, length, charstring, ND
                continue;
            }
            if (allSubrs || usedSubrs.Contains(entry.Index)) continue;
            Copy(entry.LengthStart);
            output.Write(Encoding.ASCII.GetBytes(empty.Length.ToString(CultureInfo.InvariantCulture)));
            at = entry.LengthEnd;
            Copy(entry.Data);
            output.Write(empty);
            at = entry.Data + entry.Length;
        }
        Copy(priv.Length);
        // the number of entries of the CharStrings dictionary
        var newPriv = output.ToArray();
        newPriv = Recount(newPriv, keptGlyphs.Count(byName.ContainsKey));

        // the custom encoding of the clear text without the codes of removed glyphs
        var clear = Encoding.Latin1.GetString(data, 0, start);
        clear = EncodingEntry().Replace(clear, e => keptGlyphs.Contains(e.Groups[1].Value) ? e.Value : "");
        var clearBytes = Encoding.Latin1.GetBytes(clear);
        var newEncrypted = Encrypt([0, 0, 0, 0, .. newPriv], EexecKey, 4);
        var trailer = data[end..];
        return ([.. clearBytes, .. newEncrypted, .. trailer], clearBytes.Length, newEncrypted.Length, trailer.Length);
    }

    [GeneratedRegex(@"/lenIV\s+(-?\d+)")]
    private static partial Regex LenIVPattern();

    [GeneratedRegex(@"dup\s+\d+\s*/([^\s/\[\]{}()<>]+)\s+put\s*")]
    private static partial Regex EncodingEntry();

    /// <summary>
    /// An entry of /Subrs ("dup i len RD &lt;bytes&gt; NP") or /CharStrings ("/name len RD &lt;bytes&gt; ND"): where it starts and ends, where its
    /// length is written and where its (encrypted) bytes lie.
    /// </summary>
    private readonly record struct Entry(string? Key, int Index, int Start, int LengthStart, int LengthEnd, int Data, int Length, int End);

    /// <summary>The entries after <paramref name="section"/> (the first one; a second /Subrs, the "hires" branch of some fonts, stays as it is).</summary>
    private static List<Entry> Entries(byte[] priv, string section, bool charStrings)
    {
        var result = new List<Entry>();
        var at = priv.AsSpan().IndexOf(Encoding.ASCII.GetBytes(section));
        if (at < 0) return result;
        var p = at + section.Length;
        while (true)
        {
            var (token, tokenStart, tokenEnd) = Token(priv, p);
            if (token is null) break;
            if (token is "/CharStrings" or "/Subrs" or "end" || !charStrings && token == "ND") break;
            p = tokenEnd;
            string? key = null;
            var index = -1;
            int start;
            if (charStrings && token.StartsWith('/'))
            {
                (key, start) = (token[1..], tokenStart);
            }
            else if (!charStrings && token == "dup")
            {
                start = tokenStart;
                var (number, _, numberEnd) = Token(priv, p);
                if (!int.TryParse(number, CultureInfo.InvariantCulture, out index)) continue;
                p = numberEnd;
            }
            else continue;
            var (lengthText, lengthStart, lengthEnd) = Token(priv, p);
            if (!int.TryParse(lengthText, CultureInfo.InvariantCulture, out var length) || length < 0) continue;
            var (_, _, rdEnd) = Token(priv, lengthEnd);   // RD or -|
            var data = rdEnd + 1;                          // one space, then the bytes
            if (data + length > priv.Length) throw new FontFormatException("Type 1: Charstring abgeschnitten");
            // ND, |- or "noaccess def" (and NP, | or "noaccess put")
            var (closing, _, closingEnd) = Token(priv, data + length);
            if (closing == "noaccess") (_, _, closingEnd) = Token(priv, closingEnd);
            result.Add(new Entry(key, index, start, lengthStart, lengthEnd, data, length, closingEnd));
            p = closingEnd;
        }
        return result;
    }

    /// <summary>The next token from <paramref name="p"/> on (white space and comments skipped): its text, start and end.</summary>
    private static (string? Text, int Start, int End) Token(byte[] data, int p)
    {
        while (p < data.Length)
        {
            if (data[p] is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0) p++;
            else if (data[p] == (byte)'%')
                while (p < data.Length && data[p] is not ((byte)'\r' or (byte)'\n')) p++;
            else break;
        }
        if (p >= data.Length) return (null, p, p);
        var start = p;
        if (data[p] is (byte)'{' or (byte)'}' or (byte)'[' or (byte)']') return (((char)data[p]).ToString(), p, p + 1);
        p++;
        while (p < data.Length && data[p] is not ((byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)'{' or (byte)'}' or (byte)'[' or (byte)']' or (byte)'/')) p++;
        return (Encoding.Latin1.GetString(data, start, p - start), start, p);
    }

    /// <summary>Writes <paramref name="count"/> as the size of the CharStrings dictionary ("/CharStrings n dict").</summary>
    private static byte[] Recount(byte[] priv, int count)
    {
        var at = priv.AsSpan().IndexOf("/CharStrings"u8);
        if (at < 0) return priv;
        var (number, start, end) = Token(priv, at + 12);
        if (!int.TryParse(number, CultureInfo.InvariantCulture, out _)) return priv;
        return [.. priv.AsSpan(0, start), .. Encoding.ASCII.GetBytes(count.ToString(CultureInfo.InvariantCulture)), .. priv.AsSpan(end)];
    }

    /// <summary>Where the trailer begins: the run of zeros (and line breaks) before cleartomark at the end; the end of the data without one.</summary>
    private static int Trailer(byte[] data, int start)
    {
        var mark = data.AsSpan(start).LastIndexOf("cleartomark"u8);
        if (mark < 0) return data.Length;
        var p = start + mark;
        while (p > start && data[p - 1] is (byte)'0' or (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t') p--;
        return p;
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

    /// <summary>eexec or charstring decryption (Adobe Type 1 Font Format, 7.1); the first <paramref name="skip"/> bytes are random.</summary>
    private static byte[] Decrypt(ReadOnlySpan<byte> data, ushort key, int skip)
    {
        if (skip < 0) return data.ToArray();   // lenIV -1: not encrypted
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

    /// <summary>Encryption, the inverse of <see cref="Decrypt"/>; <paramref name="plain"/> already begins with the random bytes.</summary>
    private static byte[] Encrypt(ReadOnlySpan<byte> plain, ushort key, int skip)
    {
        if (skip < 0) return plain.ToArray();   // lenIV -1: not encrypted
        var r = key;
        var result = new byte[plain.Length];
        for (var i = 0; i < plain.Length; i++)
        {
            var c = (byte)(plain[i] ^ (r >> 8));
            r = (ushort)((c + r) * 52845 + 22719);
            result[i] = c;
        }
        return result;
    }

    /// <summary>
    /// Runs a charstring only for its calls: which subroutines it reaches (the number on the stack at callsubr, also through the hint
    /// replacement "subr 1 3 callothersubr pop callsubr"), and the base and accent of seac. If a subroutine number cannot be told,
    /// <see cref="Unknown"/> keeps all of them.
    /// </summary>
    private sealed class Runner(Dictionary<int, byte[]> subrs)
    {
        private readonly List<double> _stack = [];
        private readonly Stack<double> _ps = new();
        private int _steps;
        public readonly HashSet<int> Subrs = [];
        public readonly List<int> SeacCodes = [];
        public bool Unknown;

        public void Execute(byte[] code, int depth)
        {
            if (depth > MaxDepth) return;
            for (var i = 0; i < code.Length;)
            {
                if (++_steps > MaxSteps)
                {
                    Unknown = true;
                    return;
                }
                int b = code[i++];
                if (b >= 32)
                {
                    double v;
                    if (b <= 246) v = b - 139;
                    else if (b <= 250) v = i < code.Length ? (b - 247) * 256 + code[i++] + 108 : 0;
                    else if (b <= 254) v = i < code.Length ? -(b - 251) * 256 - code[i++] - 108 : 0;
                    else
                    {
                        if (i + 4 > code.Length) return;
                        v = code[i] << 24 | code[i + 1] << 16 | code[i + 2] << 8 | code[i + 3];
                        i += 4;
                    }
                    if (_stack.Count < 64) _stack.Add(v);
                    continue;
                }
                switch (b)
                {
                    case 10:   // callsubr
                    {
                        if (_stack.Count == 0)
                        {
                            Unknown = true;
                            return;
                        }
                        var index = (int)_stack[^1];
                        _stack.RemoveAt(_stack.Count - 1);
                        Subrs.Add(index);
                        if (subrs.TryGetValue(index, out var subr)) Execute(subr, depth + 1);
                        continue;
                    }
                    case 11:   // return
                        return;
                    case 14:   // endchar
                        return;
                    case 12 when i < code.Length:
                    {
                        var op = code[i++];
                        switch (op)
                        {
                            case 6:   // seac: asb adx ady bchar achar
                                if (_stack.Count >= 5) SeacCodes.AddRange([(int)_stack[^2], (int)_stack[^1]]);
                                return;
                            case 12:   // div: two numbers become one
                                if (_stack.Count >= 2) _stack.RemoveAt(_stack.Count - 1);
                                continue;
                            case 16:   // callothersubr: its arguments go to the PostScript stack (in reverse for pop)
                            {
                                if (_stack.Count < 2)
                                {
                                    _stack.Clear();
                                    continue;
                                }
                                _stack.RemoveAt(_stack.Count - 1);   // number of the other subroutine
                                var count = (int)_stack[^1];
                                _stack.RemoveAt(_stack.Count - 1);
                                for (var k = 0; k < count && _stack.Count > 0; k++)
                                {
                                    _ps.Push(_stack[^1]);
                                    _stack.RemoveAt(_stack.Count - 1);
                                }
                                continue;
                            }
                            case 17:   // pop: a value back from the PostScript stack
                                _stack.Add(_ps.Count > 0 ? _ps.Pop() : 0);
                                continue;
                            case 33:   // setcurrentpoint
                                _stack.Clear();
                                continue;
                            default:
                                _stack.Clear();
                                continue;
                        }
                    }
                    default:
                        _stack.Clear();
                        continue;
                }
            }
        }
    }
}
