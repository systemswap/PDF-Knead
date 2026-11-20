namespace PdfKnead.Core.Fonts;

/// <summary>
/// Scrubs a CFF font (Adobe TN 5176, Type 2 charstrings TN 5177) down to the glyphs that are still shown, for redaction
/// (docs/FEHLERGEDAECHTNIS.md, No. 174): every other glyph keeps its number but becomes empty (endchar), loses its name or CID in the
/// charset and its code in the encoding; the custom strings only it used leave the font. The kept glyphs are written without
/// subroutines (inlined), so no shared subroutine carries a part of a removed glyph's outline. Glyph numbers, the names and CIDs of the
/// kept glyphs, their codes, the Font DICTs and Private DICTs (without subroutines) stay, so that the PDF finds every shown glyph as
/// before — plain and CID-keyed CFF alike.
/// </summary>
internal static class CffScrubber
{
    // Top DICT operators whose operand is a string ID (version, Notice, FullName, FamilyName, Weight, Copyright, PostScript,
    // BaseFontName, FontName); ROS has two (registry and ordering) before the supplement
    private static readonly int[] StringOperators = [0, 1, 2, 3, 4, 1200, 1221, 1222, 1238];

    // Operators written anew (offsets) or dropped (subroutines are inlined)
    private static readonly int[] OffsetOperators = [15, 16, 17, 18, 19, 1230, 1236, 1237];

    /// <summary>Scrubs the font.</summary>
    /// <param name="data">The CFF data.</param>
    /// <param name="glyphs">The glyphs still shown; glyph 0 and the parts of accented characters (seac) are kept as well.</param>
    /// <returns>The new CFF data and the glyphs kept.</returns>
    /// <exception cref="FontFormatException">The font cannot be read or rewritten (Type 1 charstrings, a glyph beyond the limits).</exception>
    public static (byte[] Data, HashSet<int> Keep) Scrub(ArraySegment<byte> data, IEnumerable<int> glyphs)
    {
        try
        {
            return ScrubCore(data, glyphs);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            // damaged offsets or counts that no check anticipated (found by fuzzing): like any other unreadable font
            throw new FontFormatException("CFF beschädigt: " + e.Message);
        }
    }

    private static (byte[] Data, HashSet<int> Keep) ScrubCore(ArraySegment<byte> data, IEnumerable<int> glyphs)
    {
        var r = new FontReader(data);
        r.U8();
        r.U8();
        r.Position = r.U8();
        var names = CffFont.CffIndex.Read(data, ref r);
        var tops = CffFont.CffIndex.Read(data, ref r);
        var strings = CffFont.CffIndex.Read(data, ref r);
        var globalSubrs = CffFont.CffIndex.Read(data, ref r);
        if (tops.Count == 0) throw new FontFormatException("CFF ohne Top DICT");
        var top = CffFont.Dict.Parse(tops.Item(data, 0));
        if ((top.Int(1206) ?? 2) != 2) throw new FontFormatException("CFF mit Type-1-Charstrings");
        if (top.Int(17) is not { } charStringsOffset) throw new FontFormatException("CFF ohne CharStrings");
        r.Position = charStringsOffset;
        var charStrings = CffFont.CffIndex.Read(data, ref r);
        var count = charStrings.Count;
        // glyph 0 is always kept: without glyphs the font is unreadable (a damaged file; before, the empty FDSelect was read past its end)
        if (count == 0) throw new FontFormatException("CFF ohne Glyphen");
        var isCid = top.Has(1230);
        var font = CffFont.Parse(data);
        var charset = CffFont.ReadCharset(data, top.Int(15) ?? 0, count, isCid);

        // Font DICTs with their Private DICT and subroutines; without CIDs the Top DICT's
        var fonts = new List<(CffFont.Dict Font, CffFont.Dict Private, CffFont.CffIndex Subrs)>();
        if (isCid && top.Int(1236) is { } fdArrayOffset)
        {
            r.Position = fdArrayOffset;
            var fdArray = CffFont.CffIndex.Read(data, ref r);
            for (var i = 0; i < fdArray.Count; i++)
            {
                var fd = CffFont.Dict.Parse(fdArray.Item(data, i));
                var (priv, subrs) = CffSubsetter.ReadPrivate(data, fd);
                fonts.Add((fd, priv, subrs));
            }
        }
        if (fonts.Count == 0)
        {
            var (priv, subrs) = CffSubsetter.ReadPrivate(data, top);
            fonts.Add((new CffFont.Dict(), priv, subrs));
        }
        var fdSelect = isCid && top.Int(1237) is { } fdSelectOffset ? CffFont.ReadFdSelect(data, fdSelectOffset, count) : new byte[count];
        int FdOf(int g) => fdSelect[g] < fonts.Count ? fdSelect[g] : 0;

        // The kept glyphs, flattened; an accented character (seac) keeps its base and accent, found by name through StandardEncoding
        var keep = new HashSet<int>();
        var flat = new Dictionary<int, byte[]>();
        var pending = new Stack<int>(glyphs.Where(g => g > 0 && g < count).Append(0));
        while (pending.Count > 0)
        {
            var g = pending.Pop();
            if (!keep.Add(g)) continue;
            var output = new List<byte>(charStrings.Item(data, g).Length * 2);
            var state = new CffSubsetter.FlattenState(data, globalSubrs, fonts[FdOf(g)].Subrs);
            CffSubsetter.Flatten(charStrings.Item(data, g), state, output, 0);
            flat[g] = [.. output];
            if (!isCid && state.EndArguments.Length >= 4)
                foreach (var code in state.EndArguments.AsSpan(state.EndArguments.Length - 2))
                    // (only within these CharStrings: the font read for the names may count others in a damaged file)
                    if (code is >= 0 and < 256 && FontTables.StandardEncoding[(int)code] is { Length: > 0 } part && font.GlyphByName(part) is > 0 and var component &&
                        component < count)
                        pending.Push(component);
        }

        // Strings: the standard ones stay, the custom ones only if a kept glyph or the font itself names them
        var custom = new List<byte[]>();
        var remap = new Dictionary<int, int>();
        int Sid(int sid)
        {
            var standard = FontTables.CffStandardStrings.Length;
            if (sid < standard) return sid;
            if (remap.TryGetValue(sid, out var mapped)) return mapped;
            if (sid - standard >= strings.Count) return 0;
            custom.Add(strings.Item(data, sid - standard).ToArray());
            return remap[sid] = standard + custom.Count - 1;
        }

        // charset (format 0): SID or CID of kept glyphs, 0 (.notdef) for the others
        var charsetBytes = new List<byte> { 0 };
        for (var g = 1; g < count; g++) CffSubsetter.AddU16(charsetBytes, keep.Contains(g) ? isCid ? charset[g] : Sid(charset[g]) : 0);

        // Encoding (plain fonts): the predefined ones stay (they name glyphs by name); a custom one becomes supplements code → name for
        // the kept glyphs only
        var encodingOffset = top.Int(16) ?? 0;
        byte[]? encodingBytes = null;
        if (!isCid && encodingOffset > 1)
        {
            var entries = new List<(int Code, int Sid)>();
            for (var c = 0; c < 256 && entries.Count < 255; c++)
                if (font.Encoding[c] is > 0 and var g && keep.Contains(g)) entries.Add((c, Sid(charset[g])));
            var e = new List<byte> { 0x80, 0, (byte)entries.Count };
            foreach (var (c, sid) in entries)
            {
                e.Add((byte)c);
                CffSubsetter.AddU16(e, sid);
            }
            encodingBytes = [.. e];
        }

        var charStringsIndex = CffSubsetter.Index([.. Enumerable.Range(0, count).Select(g => flat.TryGetValue(g, out var cs) ? cs : [14])]);
        var privates = fonts.Select(f => Dict(f.Private, [19], Sid)).ToList();
        byte[] fdSelectBytes = isCid ? [0, .. fdSelect] : [];

        byte[] TopDict(int charsetAt, int encodingAt, int charStringsAt, int privateAt, int fdSelectAt, int fdArrayAt)
        {
            var d = new List<byte>();
            if (top.Get(1230) is { Length: >= 3 } ros)
            {
                CffSubsetter.Operands(d, [Sid((int)ros[0]), Sid((int)ros[1]), ros[2]]);
                CffSubsetter.Op(d, 1230);   // ROS must come first
            }
            d.AddRange(Dict(top, OffsetOperators, Sid));
            CffSubsetter.Fixed(d, charsetAt);
            CffSubsetter.Op(d, 15);
            if (encodingBytes is not null)
            {
                CffSubsetter.Fixed(d, encodingAt);
                CffSubsetter.Op(d, 16);
            }
            else if (!isCid && top.Has(16))
            {
                CffSubsetter.Operands(d, [encodingOffset]);
                CffSubsetter.Op(d, 16);
            }
            CffSubsetter.Fixed(d, charStringsAt);
            CffSubsetter.Op(d, 17);
            if (isCid)
            {
                CffSubsetter.Fixed(d, fdSelectAt);
                CffSubsetter.Op(d, 1237);
                CffSubsetter.Fixed(d, fdArrayAt);
                CffSubsetter.Op(d, 1236);
            }
            else
            {
                CffSubsetter.Fixed(d, privates[0].Length);
                CffSubsetter.Fixed(d, privateAt);
                CffSubsetter.Op(d, 18);
            }
            return [.. d];
        }
        List<byte[]> FontDicts(int privateStart)
        {
            var result = new List<byte[]>();
            var at = privateStart;
            for (var i = 0; i < fonts.Count; i++)
            {
                var d = new List<byte>(Dict(fonts[i].Font, [18], Sid));
                CffSubsetter.Fixed(d, privates[i].Length);
                CffSubsetter.Fixed(d, at);
                CffSubsetter.Op(d, 18);
                result.Add([.. d]);
                at += privates[i].Length;
            }
            return result;
        }

        // Layout: header, Name, Top DICT, Strings, (empty) global subroutines, charset, encoding, FDSelect, CharStrings, FDArray,
        // Private DICTs. All offsets are 5-byte numbers, so the sizes are known before the positions; the strings are collected while
        // the dictionaries are written once (Sid adds them), so they are complete before the String INDEX is laid out
        var header = new byte[] { 1, 0, 4, 4 };
        var nameIndex = CffSubsetter.Index([names.Count > 0 ? names.Item(data, 0).ToArray() : "PDFKnead"u8.ToArray()]);
        var topSize = CffSubsetter.Index([TopDict(0, 0, 0, 0, 0, 0)]).Length;
        var fdArraySize = isCid ? CffSubsetter.Index(FontDicts(0)).Length : 0;
        var stringIndex = CffSubsetter.Index(custom);
        var globalIndex = CffSubsetter.Index([]);
        var charsetAt = header.Length + nameIndex.Length + topSize + stringIndex.Length + globalIndex.Length;
        var encodingAt = charsetAt + charsetBytes.Count;
        var fdSelectAt = encodingAt + (encodingBytes?.Length ?? 0);
        var charStringsAt = fdSelectAt + fdSelectBytes.Length;
        var fdArrayAt = charStringsAt + charStringsIndex.Length;
        var privateAt = fdArrayAt + fdArraySize;

        var cff = new List<byte>(privateAt + privates.Sum(p => p.Length));
        cff.AddRange(header);
        cff.AddRange(nameIndex);
        cff.AddRange(CffSubsetter.Index([TopDict(charsetAt, encodingAt, charStringsAt, privateAt, fdSelectAt, fdArrayAt)]));
        cff.AddRange(stringIndex);
        cff.AddRange(globalIndex);
        cff.AddRange(charsetBytes);
        if (encodingBytes is not null) cff.AddRange(encodingBytes);
        cff.AddRange(fdSelectBytes);
        cff.AddRange(charStringsIndex);
        if (isCid) cff.AddRange(CffSubsetter.Index(FontDicts(privateAt)));
        foreach (var p in privates) cff.AddRange(p);
        return ([.. cff], keep);
    }

    /// <summary>A DICT without the given operators, string operands through <paramref name="sid"/>.</summary>
    private static byte[] Dict(CffFont.Dict dict, int[] without, Func<int, int> sid)
    {
        var d = new List<byte>();
        foreach (var (op, values) in dict.Entries)
        {
            if (Array.IndexOf(without, op) >= 0) continue;
            CffSubsetter.Operands(d, Array.IndexOf(StringOperators, op) >= 0 && values.Length == 1 ? [sid((int)values[0])] : values);
            CffSubsetter.Op(d, op);
        }
        return [.. d];
    }
}
