using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PdfKnead.Core.Fonts;

/// <summary>
/// Subset of a CFF font (Adobe TN 5176/5177) for embedding as CIDFontType0C: only the glyphs in use, as a CID-keyed font with
/// CID = original glyph index (the page content writes glyph indices, Identity-H). Subroutines are inlined into the glyphs
/// ("flattened"), so the subset has none; of Noto CJK (15 MB of CFF) only what the glyphs need remains. Works for CID-keyed and
/// ordinary CFF fonts (OpenType fonts with CFF outlines among the system fonts).
/// </summary>
internal static class CffSubsetter
{
    // Work limit per glyph as when drawing (CffFont): subroutines that call each other many times without producing output would
    // grow exponentially despite the depth limit (20 calls per level, 10 levels)
    private const int MaxDepth = 10, MaxGlyphBytes = 65536, MaxSteps = 200_000;

    /// <summary>Builds the subset as stand-alone CFF data; glyph 0 is always included.</summary>
    /// <param name="data">The source CFF data.</param>
    /// <param name="glyphs">Glyph indices to keep; indices out of range are ignored.</param>
    /// <returns>The CFF data of the subset.</returns>
    /// <exception cref="FontFormatException">The font has no Top DICT or CharStrings, or a glyph exceeds the limits or uses arithmetic operators.</exception>
    public static byte[] Subset(ArraySegment<byte> data, IEnumerable<int> glyphs)
    {
        var r = new FontReader(data);
        r.U8();
        r.U8();
        r.Position = r.U8();
        var names = CffFont.CffIndex.Read(data, ref r);
        var tops = CffFont.CffIndex.Read(data, ref r);
        CffFont.CffIndex.Read(data, ref r);   // Strings: written anew
        var globalSubrs = CffFont.CffIndex.Read(data, ref r);
        if (tops.Count == 0) throw new FontFormatException("CFF ohne Top DICT");
        var top = CffFont.Dict.Parse(tops.Item(data, 0));
        if (top.Int(17) is not { } charStringsOffset) throw new FontFormatException("CFF ohne CharStrings");
        r.Position = charStringsOffset;
        var charStrings = CffFont.CffIndex.Read(data, ref r);
        var count = charStrings.Count;

        // Font DICTs (Font DICT + Private DICT with its subroutines); without CIDs a single one from the Top DICT
        var isCid = top.Has(1230);
        var fonts = new List<(CffFont.Dict Font, CffFont.Dict Private, CffFont.CffIndex Subrs)>();
        if (isCid && top.Int(1236) is { } fdArrayOffset)
        {
            r.Position = fdArrayOffset;
            var fdArray = CffFont.CffIndex.Read(data, ref r);
            for (var i = 0; i < fdArray.Count; i++)
            {
                var fd = CffFont.Dict.Parse(fdArray.Item(data, i));
                var (priv, subrs) = ReadPrivate(data, fd);
                fonts.Add((fd, priv, subrs));
            }
        }
        if (fonts.Count == 0)
        {
            var (priv, subrs) = ReadPrivate(data, top);
            fonts.Add((new CffFont.Dict(), priv, subrs));
        }
        var fdSelect = isCid && top.Int(1237) is { } fdSelectOffset ? CffFont.ReadFdSelect(data, fdSelectOffset, count) : new byte[count];

        var keep = glyphs.Where(g => g > 0 && g < count).Append(0).Distinct().Order().ToList();
        // Font DICTs in use, renumbered in order of first use
        var fdMap = new Dictionary<int, int>();
        var newFd = new byte[keep.Count];
        for (var i = 0; i < keep.Count; i++)
        {
            var fd = fdSelect[keep[i]] < fonts.Count ? fdSelect[keep[i]] : 0;
            if (!fdMap.TryGetValue(fd, out var mapped)) fdMap[fd] = mapped = fdMap.Count;
            newFd[i] = (byte)mapped;
        }
        var flat = new List<byte[]>(keep.Count);
        foreach (var g in keep)
        {
            var output = new List<byte>(charStrings.Item(data, g).Length * 2);
            var state = new FlattenState(data, globalSubrs, fonts[fdSelect[g] < fonts.Count ? fdSelect[g] : 0].Subrs);
            Flatten(charStrings.Item(data, g), state, output, 0);
            flat.Add([.. output]);
        }

        // Layout: header, Name, Top DICT, Strings, (empty) global subroutines, charset, FDSelect, CharStrings, FDArray, Private DICTs.
        // The offsets in the Top DICT are written as 5-byte numbers, so its size is known in advance.
        var name = names.Count > 0 ? names.Item(data, 0).ToArray() : "PDFKnead"u8.ToArray();
        var strings = Index([Encoding.ASCII.GetBytes("Adobe"), Encoding.ASCII.GetBytes("Identity")]);   // SID 391, 392
        var charset = new List<byte> { 0 };
        foreach (var g in keep.Skip(1)) AddU16(charset, g);
        var fdSelectBytes = new List<byte> { 0 };
        fdSelectBytes.AddRange(newFd);
        var charStringsIndex = Index(flat);
        var privates = fdMap.OrderBy(m => m.Value).Select(m => Dict(fonts[m.Key].Private.Entries.Where(e => e.Key != 19), [])).ToList();

        byte[] TopDict(int charsetAt, int fdSelectAt, int charStringsAt, int fdArrayAt)
        {
            var d = new List<byte>();
            Operands(d, [391, 392, 0]);
            Op(d, 1230);   // ROS must come first
            foreach (var op in (ReadOnlySpan<int>)[1207, 5])   // FontMatrix, FontBBox
                if (top.Get(op) is { } values)
                {
                    Operands(d, values);
                    Op(d, op);
                }
            Operands(d, [keep[^1] + 1]);
            Op(d, 1234);   // CIDCount
            Fixed(d, charsetAt);
            Op(d, 15);
            Fixed(d, fdSelectAt);
            Op(d, 1237);
            Fixed(d, charStringsAt);
            Op(d, 17);
            Fixed(d, fdArrayAt);
            Op(d, 1236);
            return [.. d];
        }

        var header = new byte[] { 1, 0, 4, 4 };
        var nameIndex = Index([name]);
        var topSize = Index([TopDict(0, 0, 0, 0)]).Length;
        var globalIndex = Index([]);
        var charsetAt = header.Length + nameIndex.Length + topSize + strings.Length + globalIndex.Length;
        var fdSelectAt = charsetAt + charset.Count;
        var charStringsAt = fdSelectAt + fdSelectBytes.Count;
        var fdArrayAt = charStringsAt + charStringsIndex.Length;
        // Font DICTs with the position of their Private DICT: after the FDArray, whose size does not depend on the (fixed) 5-byte numbers
        List<byte[]> FontDicts(int privateStart)
        {
            var result = new List<byte[]>();
            var at = privateStart;
            foreach (var (fd, index) in fdMap.OrderBy(m => m.Value).Select(m => (fonts[m.Key].Font, m.Value)))
            {
                var d = new List<byte>();
                if (fd.Get(1207) is { } matrix)
                {
                    Operands(d, matrix);
                    Op(d, 1207);
                }
                Fixed(d, privates[index].Length);
                Fixed(d, at);
                Op(d, 18);
                result.Add([.. d]);
                at += privates[index].Length;
            }
            return result;
        }
        var fdArraySize = Index(FontDicts(0)).Length;
        var privateAt = fdArrayAt + fdArraySize;

        var cff = new List<byte>(privateAt + privates.Sum(p => p.Length));
        cff.AddRange(header);
        cff.AddRange(nameIndex);
        cff.AddRange(Index([TopDict(charsetAt, fdSelectAt, charStringsAt, fdArrayAt)]));
        cff.AddRange(strings);
        cff.AddRange(globalIndex);
        cff.AddRange(charset);
        cff.AddRange(fdSelectBytes);
        cff.AddRange(charStringsIndex);
        cff.AddRange(Index(FontDicts(privateAt)));
        foreach (var p in privates) cff.AddRange(p);
        return [.. cff];
    }

    internal static (CffFont.Dict, CffFont.CffIndex) ReadPrivate(ArraySegment<byte> data, CffFont.Dict owner)
    {
        if (owner.Get(18) is not { Length: 2 } location || location[1] < 0 || location[0] < 0 || location[1] > data.Count - location[0])
            return (new CffFont.Dict(), default);
        var offset = (int)location[1];
        var dict = CffFont.Dict.Parse(data.AsSpan(offset, (int)location[0]));
        var subrs = default(CffFont.CffIndex);
        if (dict.Int(19) is { } at)
        {
            var r = new FontReader(data, offset + at);
            subrs = CffFont.CffIndex.Read(data, ref r);
        }
        return (dict, subrs);
    }

    // ------------------------------------------------------------ Flattening glyphs

    internal sealed class FlattenState(ArraySegment<byte> data, CffFont.CffIndex global, CffFont.CffIndex local)
    {
        public readonly ArraySegment<byte> Data = data;
        public readonly CffFont.CffIndex Global = global, Local = local;
        public int Stack, Stems, Steps, LastOperand = -1;
        public double LastValue;
        public bool Ended;

        /// <summary>The numbers before the endchar that ended the glyph (four or five: an accented character built with seac).</summary>
        public double[] EndArguments = [];

        // the numbers since the last operator, for EndArguments (at most the five of seac with a width)
        internal readonly double[] Recent = new double[5];
        internal int RecentCount;
    }

    /// <summary>Writes the charstring without subroutines: callsubr/callgsubr are replaced by the subroutine body (without return); stems are counted so that hint masks keep their length.</summary>
    internal static void Flatten(ReadOnlySpan<byte> cs, FlattenState s, List<byte> output, int depth)
    {
        if (depth > MaxDepth) throw new FontFormatException("CFF: Unterprogramme zu tief");
        var i = 0;
        while (i < cs.Length && !s.Ended)
        {
            if (output.Count > MaxGlyphBytes) throw new FontFormatException("CFF: Glyphe zu groß");
            if (++s.Steps > MaxSteps) throw new FontFormatException("CFF: Glyphe zu aufwendig");
            int b0 = cs[i];
            // Number: copy it at once and remember its position and value (the last one may be a subroutine number)
            if (b0 >= 32 || b0 == 28)
            {
                var length = b0 == 28 ? 3 : b0 <= 246 ? 1 : b0 <= 254 ? 2 : 5;
                if (i + length > cs.Length) throw new FontFormatException("CFF: Zahl abgeschnitten");
                s.LastValue = b0 switch
                {
                    28 => (short)(cs[i + 1] << 8 | cs[i + 2]),
                    <= 246 => b0 - 139,
                    <= 250 => (b0 - 247) * 256 + cs[i + 1] + 108,
                    <= 254 => -(b0 - 251) * 256 - cs[i + 1] - 108,
                    _ => BinaryPrimitives.ReadInt32BigEndian(cs[(i + 1)..]) / 65536.0,
                };
                s.LastOperand = output.Count;
                for (var k = 0; k < length; k++) output.Add(cs[i + k]);
                s.Stack++;
                if (s.RecentCount == s.Recent.Length) Array.Copy(s.Recent, 1, s.Recent, 0, --s.RecentCount);
                s.Recent[s.RecentCount++] = s.LastValue;
                i += length;
                continue;
            }
            i++;
            switch (b0)
            {
                case 10 or 29:   // callsubr, callgsubr: drop the number and put the body in its place
                {
                    if (s.LastOperand < 0) throw new FontFormatException("CFF: Unterprogramm ohne Nummer");
                    output.RemoveRange(s.LastOperand, output.Count - s.LastOperand);
                    s.Stack--;
                    s.LastOperand = -1;
                    if (s.RecentCount > 0) s.RecentCount--;
                    var subrs = b0 == 10 ? s.Local : s.Global;
                    var index = (int)s.LastValue + CffFont.Bias(subrs.Count);
                    if (index < 0 || index >= subrs.Count) throw new FontFormatException("CFF: Unterprogramm fehlt");
                    Flatten(subrs.Item(s.Data, index), s, output, depth + 1);
                    continue;
                }
                case 11:   // return
                    return;
                case 14:   // endchar
                    output.Add(14);
                    s.Ended = true;
                    s.EndArguments = s.Recent.AsSpan(0, s.RecentCount).ToArray();
                    return;
                case 1 or 3 or 18 or 23:   // stems (with an odd operand count the first one is the width)
                    s.Stems += s.Stack / 2;
                    break;
                case 19 or 20:   // hintmask, cntrmask: preceding numbers are further vertical stems, followed by the mask
                {
                    s.Stems += s.Stack / 2;
                    var bytes = (s.Stems + 7) / 8;
                    if (i + bytes > cs.Length) throw new FontFormatException("CFF: Maske abgeschnitten");
                    output.Add((byte)b0);
                    for (var k = 0; k < bytes; k++) output.Add(cs[i + k]);
                    i += bytes;
                    s.Stack = s.RecentCount = 0;
                    s.LastOperand = -1;
                    continue;
                }
                case 12:
                {
                    if (i >= cs.Length) throw new FontFormatException("CFF: Operator abgeschnitten");
                    var b1 = cs[i++];
                    // Arithmetic operators change the stack differently; they practically never occur in fonts
                    if (b1 is not (34 or 35 or 36 or 37)) throw new FontFormatException("CFF: Rechenoperator im Charstring");
                    output.Add(12);
                    output.Add(b1);
                    s.Stack = s.RecentCount = 0;
                    s.LastOperand = -1;
                    continue;
                }
            }
            output.Add((byte)b0);
            s.Stack = s.RecentCount = 0;
            s.LastOperand = -1;
        }
    }

    // ------------------------------------------------------------ Writing

    internal static byte[] Index(IReadOnlyList<byte[]> items)
    {
        if (items.Count == 0) return [0, 0];
        var total = items.Sum(x => x.Length) + 1;
        var offSize = total < 0x100 ? 1 : total < 0x10000 ? 2 : total < 0x1000000 ? 3 : 4;
        var result = new List<byte>(3 + (items.Count + 1) * offSize + total);
        AddU16(result, items.Count);
        result.Add((byte)offSize);
        var offset = 1;
        for (var i = 0; i <= items.Count; i++)
        {
            for (var k = offSize - 1; k >= 0; k--) result.Add((byte)(offset >> (8 * k)));
            if (i < items.Count) offset += items[i].Length;
        }
        foreach (var item in items) result.AddRange(item);
        return [.. result];
    }

    private static byte[] Dict(IEnumerable<KeyValuePair<int, double[]>> entries, List<byte> d)
    {
        foreach (var (op, values) in entries)
        {
            Operands(d, values);
            Op(d, op);
        }
        return [.. d];
    }

    internal static void AddU16(List<byte> b, int v)
    {
        b.Add((byte)(v >> 8));
        b.Add((byte)v);
    }

    internal static void Op(List<byte> d, int op)
    {
        if (op >= 1200)
        {
            d.Add(12);
            d.Add((byte)(op - 1200));
        }
        else d.Add((byte)op);
    }

    /// <summary>Writes a number always as 5 bytes (offsets whose value is only known later).</summary>
    internal static void Fixed(List<byte> d, int v)
    {
        d.Add(29);
        d.Add((byte)(v >> 24));
        d.Add((byte)(v >> 16));
        d.Add((byte)(v >> 8));
        d.Add((byte)v);
    }

    internal static void Operands(List<byte> d, IEnumerable<double> values)
    {
        foreach (var v in values)
        {
            if (v == Math.Round(v) && v is >= int.MinValue and <= int.MaxValue)
            {
                var n = (int)v;
                switch (n)
                {
                    case >= -107 and <= 107:
                        d.Add((byte)(n + 139));
                        break;
                    case >= 108 and <= 1131:
                        d.Add((byte)((n - 108) / 256 + 247));
                        d.Add((byte)((n - 108) % 256));
                        break;
                    case >= -1131 and <= -108:
                        d.Add((byte)((-n - 108) / 256 + 251));
                        d.Add((byte)((-n - 108) % 256));
                        break;
                    case >= short.MinValue and <= short.MaxValue:
                        d.Add(28);
                        d.Add((byte)(n >> 8));
                        d.Add((byte)n);
                        break;
                    default:
                        Fixed(d, n);
                        break;
                }
                continue;
            }
            // Real number: nibbles (0–9, . = A, E = B, E- = C, - = E, end = F)
            var text = v.ToString("R", CultureInfo.InvariantCulture).Replace("E+", "E");
            var nibbles = new List<int>();
            for (var k = 0; k < text.Length; k++)
                switch (text[k])
                {
                    case '.': nibbles.Add(0xA); break;
                    case '-' when k == 0: nibbles.Add(0xE); break;
                    case 'E' when k + 1 < text.Length && text[k + 1] == '-':
                        nibbles.Add(0xC);
                        k++;
                        break;
                    case 'E': nibbles.Add(0xB); break;
                    default: nibbles.Add(text[k] - '0'); break;
                }
            nibbles.Add(0xF);
            if (nibbles.Count % 2 == 1) nibbles.Add(0xF);
            d.Add(30);
            for (var k = 0; k < nibbles.Count; k += 2) d.Add((byte)(nibbles[k] << 4 | nibbles[k + 1]));
        }
    }
}
