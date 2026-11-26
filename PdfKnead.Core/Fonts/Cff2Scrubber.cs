namespace PdfKnead.Core.Fonts;

/// <summary>
/// Scrubs the "CFF2" table of an OpenType font (OpenType 8) down to the glyphs still shown, for redaction (docs/FEHLERGEDAECHTNIS.md,
/// No. 1158): it is written as a static CFF2 table — the default instance the display draws, without VariationStore, blend and vsindex,
/// without subroutines (the kept glyphs written out, <see cref="CffFont.StaticCharString"/>), every other glyph an empty charstring at its
/// number (8.10.2) and with the first Font DICT. The Private DICTs keep their hint values in the default instance. So no delta, subroutine
/// or Font DICT choice tells of a removed glyph.
/// </summary>
/// <remarks>
/// It stays CFF2 rather than becoming 'CFF ': CFF2 has no charset, glyph names or encoding, so the PDF finds its glyphs through cmap and post
/// like a TrueType font — a 'CFF ' table would make a simple font choose them through the names and the built-in encoding of the CFF
/// (ISO 32000-2, 9.9, the process of 9.6.5.2), and the display before and after the redaction could differ. The tables of the variations
/// (<see cref="VariationTables"/>) leave the font with the VariationStore.
/// </remarks>
internal static class Cff2Scrubber
{
    /// <summary>
    /// The tables of a variable font: without the VariationStore they would describe variations the outlines no longer have, and HVAR, VVAR
    /// and gvar hold data per glyph (OpenType 8.12.6, 8.12.7).
    /// </summary>
    internal static readonly string[] VariationTables = ["fvar", "avar", "STAT", "HVAR", "VVAR", "MVAR", "gvar", "cvar"];

    /// <summary>Scrubs the CFF2 data of a font.</summary>
    /// <param name="font">The CFF2 data as read (<see cref="CffFont.ParseCff2"/>).</param>
    /// <param name="glyphs">The glyphs still shown; glyph 0 is kept as well.</param>
    /// <returns>The new CFF2 table and the glyphs kept.</returns>
    /// <exception cref="FontFormatException">The data are not CFF2.</exception>
    public static (byte[] Data, HashSet<int> Keep) Scrub(CffFont font, IEnumerable<int> glyphs)
    {
        if (!font.IsCff2) throw new FontFormatException("kein CFF2");
        var count = font.GlyphCount;
        var keep = new HashSet<int>(glyphs.Where(g => g > 0 && g < count)) { 0 };
        if (count == 0) keep.Clear();

        // Font DICTs in use, renumbered in the order of the glyphs; a removed glyph takes the Font DICT of glyph 0 (its own would tell which
        // kind of glyph it was — a Font DICT for kana, one for ideographs)
        var fdMap = new Dictionary<int, int>();
        int Mapped(int fd) => fdMap.TryGetValue(fd, out var m) ? m : fdMap[fd] = fdMap.Count;
        var notdefFd = count > 0 ? Mapped(font.FontDictOf(0)) : 0;
        var fdSelect = new byte[count];
        var charStrings = new byte[count][];
        for (var g = 0; g < count; g++)
        {
            if (keep.Contains(g))
            {
                fdSelect[g] = (byte)Mapped(font.FontDictOf(g));
                charStrings[g] = font.StaticCharString(g);
            }
            else
            {
                fdSelect[g] = (byte)notdefFd;
                charStrings[g] = [];
            }
        }
        if (fdMap.Count == 0) fdMap[0] = 0;
        var privates = fdMap.OrderBy(m => m.Value).Select(m => PrivateDict(font.PrivateSource(m.Key))).ToList();

        // Layout: header, Top DICT, (empty) GlobalSubrINDEX, CharStringINDEX, FontDICTSelect (only with more than one Font DICT: 8.7.2),
        // FontDICTINDEX, Private DICTs. Offsets as 5-byte numbers, so the size of the Top DICT is known before the positions
        var withSelect = privates.Count > 1;
        byte[] TopDict(int charStringsAt, int fontDictsAt, int fdSelectAt)
        {
            var d = new List<byte>();
            if (font.TopMatrixOperands is { } matrix)
            {
                CffSubsetter.Operands(d, matrix);
                CffSubsetter.Op(d, 1207);
            }
            CffSubsetter.Fixed(d, charStringsAt);
            CffSubsetter.Op(d, 17);
            CffSubsetter.Fixed(d, fontDictsAt);
            CffSubsetter.Op(d, 1236);
            if (withSelect)
            {
                CffSubsetter.Fixed(d, fdSelectAt);
                CffSubsetter.Op(d, 1237);
            }
            return [.. d];
        }
        List<byte[]> FontDicts(int privateStart)
        {
            var result = new List<byte[]>();
            var at = privateStart;
            foreach (var p in privates)
            {
                var d = new List<byte>();
                CffSubsetter.Fixed(d, p.Length);
                CffSubsetter.Fixed(d, p.Length == 0 ? 0 : at);   // an empty Private DICT: size and offset 0 (8.8.4)
                CffSubsetter.Op(d, 18);
                result.Add([.. d]);
                at += p.Length;
            }
            return result;
        }

        var topSize = TopDict(0, 0, 0).Length;
        if (topSize > ushort.MaxValue) throw new FontFormatException("CFF2: TopDICT zu groß");
        const int headerSize = 5;
        var globalIndex = Index([]);
        var charStringIndex = Index(charStrings);
        byte[] fdSelectBytes = withSelect ? [0, .. fdSelect] : [];
        var charStringsAt = headerSize + topSize + globalIndex.Length;
        var fdSelectAt = charStringsAt + charStringIndex.Length;
        var fontDictsAt = fdSelectAt + fdSelectBytes.Length;
        var privateAt = fontDictsAt + Index(FontDicts(0)).Length;

        var cff2 = new List<byte>(privateAt + privates.Sum(p => p.Length)) { 2, 0, headerSize, (byte)(topSize >> 8), (byte)topSize };
        cff2.AddRange(TopDict(charStringsAt, fontDictsAt, fdSelectAt));
        cff2.AddRange(globalIndex);
        cff2.AddRange(charStringIndex);
        cff2.AddRange(fdSelectBytes);
        cff2.AddRange(Index(FontDicts(privateAt)));
        foreach (var p in privates) cff2.AddRange(p);
        return ([.. cff2], keep);
    }

    /// <summary>
    /// A Private DICT in the default instance: its values as read (a blended one with its default value), without LocalSubrINDEXOffset (no
    /// subroutines are left) and vsindex (no VariationStore); empty for an empty or missing one.
    /// </summary>
    private static byte[] PrivateDict(CffFont.Dict? source)
    {
        if (source is null) return [];
        var d = new List<byte>();
        foreach (var (op, values) in source.Entries)
        {
            if (op is 19 or 22) continue;
            CffSubsetter.Operands(d, values);
            CffSubsetter.Op(d, op);
        }
        return [.. d];
    }

    /// <summary>An INDEX of CFF2 (OpenType 8.5.2): a uint32 count, then the smallest offset size; an empty one is the count alone.</summary>
    internal static byte[] Index(IReadOnlyList<byte[]> items)
    {
        if (items.Count == 0) return [0, 0, 0, 0];
        var total = items.Sum(x => (long)x.Length) + 1;
        var offSize = total < 0x100 ? 1 : total < 0x10000 ? 2 : total < 0x1000000 ? 3 : 4;
        var result = new List<byte>((int)Math.Min(int.MaxValue, 5 + (items.Count + 1L) * offSize + total));
        result.AddRange([(byte)(items.Count >> 24), (byte)(items.Count >> 16), (byte)(items.Count >> 8), (byte)items.Count, (byte)offSize]);
        var offset = 1;
        for (var i = 0; i <= items.Count; i++)
        {
            for (var k = offSize - 1; k >= 0; k--) result.Add((byte)(offset >> (8 * k)));
            if (i < items.Count) offset += items[i].Length;
        }
        foreach (var item in items) result.AddRange(item);
        return [.. result];
    }
}
