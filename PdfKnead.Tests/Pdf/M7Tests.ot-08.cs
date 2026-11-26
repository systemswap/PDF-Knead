using System.Buffers.Binary;
using System.Globalization;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Konformitätsabgleich M7, package ot-08 (OpenType, CFF2 table: Private DICT 8.9 with 8.9.1–8.9.5, CharString 8.10.1 and 8.10.2): the tests the
/// sentence-by-sentence drafts named as missing. PDF-Knead reads the CFF2 table in the default instance (question 1, docs/FEHLERGEDAECHTNIS.md,
/// No. 1158): each glyph draws what its charstring says, blends resolved to their default values, the tables beside the outlines are read, and
/// the redaction writes a static CFF2 table. The CFF2 builder and the assertions of this file serve the packages ot-06, ot-07, ot-09, ot-10 and
/// ot-11 as well.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package ot-08; also used by ot-09, ot-10, ot-11)

    /// <summary>The variation data of a CFF2 table: regions of one axis (start, peak, end) and the region indices of each ItemVariationData.</summary>
    internal sealed record Cff2VariationsOt08((double Start, double Peak, double End)[] Regions, int[][] DataSets, int ItemCount = 0);

    /// <summary>One Font DICT with its Private DICT (null: an empty one, size and offset 0) and the local subroutines of that Private DICT.</summary>
    internal sealed record Cff2PrivateOt08(byte[]? Dict, byte[][]? Subrs = null);

    /// <summary>A CFF2 table to build: charstrings, global subroutines, Font/Private DICT pairs, FDSelect (one Font DICT per glyph) and variations.</summary>
    internal sealed record Cff2SpecOt08(byte[][] CharStrings, byte[][]? GlobalSubrs = null, Cff2PrivateOt08[]? Privates = null, byte[]? FdSelect = null,
        Cff2VariationsOt08? Variations = null, bool NoFontDicts = false);

    /// <summary>One variation region on one axis from −1 to 0 peaking at −1 … and a second one — the shapes of the example table (k = 2).</summary>
    internal static readonly Cff2VariationsOt08 TwoRegionsOt08 = new([(-1, -0.5, 0), (-1, -1, -0.5)], [[0, 1]]);

    /// <summary>One region (0 … 1 peaking at 1), one ItemVariationData with it (k = 1).</summary>
    internal static readonly Cff2VariationsOt08 OneRegionOt08 = new([(0, 1, 1)], [[0]]);

    /// <summary>Two regions and two ItemVariationData: number 0 with one region (k = 1), number 1 with both (k = 2).</summary>
    internal static readonly Cff2VariationsOt08 TwoDataSetsOt08 = new([(0, 1, 1), (-1, -1, 0)], [[0], [0, 1]]);

    /// <summary>
    /// Assembles a Type 2 / CFF2 charstring from tokens: integers and decimals (16.16 fixed, byte 255), operator names, and raw bytes as
    /// „0x..“ (mask bytes of hintmask and cntrmask).
    /// </summary>
    internal static byte[] CsOt08(string tokens)
    {
        var ops = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["hstem"] = [1], ["vstem"] = [3], ["vmoveto"] = [4], ["rlineto"] = [5], ["hlineto"] = [6], ["vlineto"] = [7], ["rrcurveto"] = [8],
            ["callsubr"] = [10], ["return"] = [11], ["endchar"] = [14], ["vsindex"] = [15], ["blend"] = [16], ["hstemhm"] = [18], ["hintmask"] = [19],
            ["cntrmask"] = [20], ["rmoveto"] = [21], ["hmoveto"] = [22], ["vstemhm"] = [23], ["rcurveline"] = [24], ["rlinecurve"] = [25],
            ["vvcurveto"] = [26], ["hhcurveto"] = [27], ["callgsubr"] = [29], ["vhcurveto"] = [30], ["hvcurveto"] = [31],
            ["hflex"] = [12, 34], ["flex"] = [12, 35], ["hflex1"] = [12, 36], ["flex1"] = [12, 37],
        };
        var result = new List<byte>();
        foreach (var token in tokens.Split((char[])[' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("0x", StringComparison.Ordinal)) result.Add(byte.Parse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            else if (ops.TryGetValue(token, out var op)) result.AddRange(op);
            else if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            {
                if (v is >= -107 and <= 107) result.Add((byte)(v + 139));
                else if (v is >= 108 and <= 1131) result.AddRange([(byte)((v - 108 >> 8) + 247), (byte)(v - 108)]);
                else if (v is >= -1131 and <= -108) result.AddRange([(byte)((-v - 108 >> 8) + 251), (byte)(-v - 108)]);
                else result.AddRange([28, (byte)(v >> 8), (byte)v]);
            }
            else
            {
                var f = (int)Math.Round(double.Parse(token, CultureInfo.InvariantCulture) * 65536);
                result.AddRange([255, (byte)(f >> 24), (byte)(f >> 16), (byte)(f >> 8), (byte)f]);
            }
        }
        return [.. result];
    }

    /// <summary>Assembles a DICT (Private DICT of CFF2) from tokens: integers, decimals (BCD, operator 30), operator names, raw bytes as „0x..“.</summary>
    internal static byte[] DictOt08(string tokens)
    {
        var ops = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["BlueValues"] = [6], ["OtherBlues"] = [7], ["FamilyBlues"] = [8], ["FamilyOtherBlues"] = [9], ["StdHW"] = [10], ["StdVW"] = [11],
            ["Subrs"] = [19], ["vsindex"] = [22], ["blend"] = [23], ["BlueScale"] = [12, 9], ["BlueShift"] = [12, 10], ["BlueFuzz"] = [12, 11],
            ["StemSnapH"] = [12, 12], ["StemSnapV"] = [12, 13], ["LanguageGroup"] = [12, 17], ["ExpansionFactor"] = [12, 18],
        };
        var result = new List<byte>();
        foreach (var token in tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("0x", StringComparison.Ordinal)) result.Add(byte.Parse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            else if (ops.TryGetValue(token, out var op)) result.AddRange(op);
            else if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) result.AddRange(DictIntOt08(v));
            else
            {
                // BCD: digits, a = '.', e = '-', f = end
                var nibbles = new List<int>();
                foreach (var c in token)
                    nibbles.Add(c switch { '.' => 0xA, '-' => 0xE, _ => c - '0' });
                nibbles.Add(0xF);
                if (nibbles.Count % 2 == 1) nibbles.Add(0xF);
                result.Add(30);
                for (var i = 0; i < nibbles.Count; i += 2) result.Add((byte)(nibbles[i] << 4 | nibbles[i + 1]));
            }
        }
        return [.. result];
    }

    private static byte[] DictIntOt08(int v) =>
        v switch
        {
            >= -107 and <= 107 => [(byte)(v + 139)],
            >= 108 and <= 1131 => [(byte)((v - 108 >> 8) + 247), (byte)(v - 108)],
            >= -1131 and <= -108 => [(byte)((-v - 108 >> 8) + 251), (byte)(-v - 108)],
            >= -32768 and <= 32767 => [28, (byte)(v >> 8), (byte)v],
            _ => DictFiveOt08(v),
        };

    /// <summary>An integer as the 5-byte DICT form (operator 29), so that sizes are known before offsets.</summary>
    private static byte[] DictFiveOt08(int v) => [29, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    /// <summary>A CFF2 INDEX: count (4 bytes), offSize 4, offsets from 1, data; an empty one is the count alone.</summary>
    internal static byte[] Index2Ot08(IReadOnlyList<byte[]> items)
    {
        var result = new List<byte>();
        void U32(int v) => result.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        U32(items.Count);
        if (items.Count == 0) return [.. result];
        result.Add(4);
        var offset = 1;
        U32(offset);
        foreach (var item in items) U32(offset += item.Length);
        foreach (var item in items) result.AddRange(item);
        return [.. result];
    }

    /// <summary>An ItemVariationStore (format 1) with the regions of one axis and ItemVariationData that list only region indices.</summary>
    internal static byte[] VariationStoreOt08(Cff2VariationsOt08 v)
    {
        var regions = new List<byte>();
        void U16(List<byte> l, int x) => l.AddRange([(byte)(x >> 8), (byte)x]);
        U16(regions, 1);
        U16(regions, v.Regions.Length);
        foreach (var (start, peak, end) in v.Regions)
            foreach (var x in (double[])[start, peak, end]) U16(regions, (short)Math.Round(x * 16384));
        var sets = new List<byte[]>();
        foreach (var indices in v.DataSets)
        {
            var set = new List<byte>();
            U16(set, v.ItemCount);
            U16(set, 0);
            U16(set, indices.Length);
            foreach (var i in indices) U16(set, i);
            for (var item = 0; item < v.ItemCount; item++) set.AddRange(new byte[indices.Length]);   // one byte delta per region
            sets.Add([.. set]);
        }
        var store = new List<byte>();
        var headerLength = 2 + 4 + 2 + 4 * sets.Count;
        U16(store, 1);
        store.AddRange([0, 0, (byte)(headerLength >> 8), (byte)headerLength]);
        U16(store, sets.Count);
        var at = headerLength + regions.Count;
        foreach (var set in sets)
        {
            store.AddRange([(byte)(at >> 24), (byte)(at >> 16), (byte)(at >> 8), (byte)at]);
            at += set.Length;
        }
        store.AddRange(regions);
        foreach (var set in sets) store.AddRange(set);
        return [.. store];
    }

    /// <summary>
    /// Builds a CFF2 table: header (version 2.0, header size 5), Top DICT (CharStringINDEXOffset, FontDICTINDEXOffset and, if given,
    /// VariationStoreOffset and FontDICTSelectOffset, all as 5-byte numbers), GlobalSubrINDEX, VariationStore, CharStringINDEX, FontDICTSelect
    /// (format 0), FontDICTINDEX and the Private DICTs, each followed by its LocalSubrINDEX (named by LocalSubrINDEXOffset, operator 19).
    /// </summary>
    internal static byte[] Cff2Ot08(Cff2SpecOt08 spec)
    {
        var privates = spec.Privates ?? [new Cff2PrivateOt08([])];
        var topLength = 6 + 7 + (spec.Variations is null ? 0 : 6) + (spec.FdSelect is null ? 0 : 7);
        var global = Index2Ot08(spec.GlobalSubrs ?? []);
        var store = spec.Variations is null ? [] : VariationStoreOt08(spec.Variations);
        var at = 5 + topLength + global.Length;
        var storeAt = at;
        if (store.Length > 0) at += 2 + store.Length;
        var charStringsAt = at;
        var charStrings = Index2Ot08(spec.CharStrings);
        at += charStrings.Length;
        var fdSelectAt = at;
        byte[] fdSelect = spec.FdSelect is null ? [] : [0, .. spec.FdSelect];
        at += fdSelect.Length;
        var fontDictsAt = at;
        var fontDictsLength = Index2Ot08(spec.NoFontDicts ? [] : privates.Select(_ => new byte[11]).ToArray()).Length;
        at += fontDictsLength;
        var fontDicts = new List<byte[]>();
        var tail = new List<byte>();
        foreach (var p in privates)
        {
            if (p.Dict is null)
            {
                fontDicts.Add([.. DictFiveOt08(0), .. DictFiveOt08(0), 18]);
                continue;
            }
            var dict = p.Subrs is null ? p.Dict : [.. p.Dict, .. DictFiveOt08(p.Dict.Length + 6), 19];
            fontDicts.Add([.. DictFiveOt08(dict.Length), .. DictFiveOt08(at), 18]);
            tail.AddRange(dict);
            at += dict.Length;
            if (p.Subrs is not null)
            {
                var subrs = Index2Ot08(p.Subrs);
                tail.AddRange(subrs);
                at += subrs.Length;
            }
        }
        var top = new List<byte>();
        top.AddRange(DictFiveOt08(charStringsAt));
        top.Add(17);
        top.AddRange(DictFiveOt08(fontDictsAt));
        top.AddRange([12, 36]);
        if (spec.Variations is not null)
        {
            top.AddRange(DictFiveOt08(storeAt));
            top.Add(24);
        }
        if (spec.FdSelect is not null)
        {
            top.AddRange(DictFiveOt08(fdSelectAt));
            top.AddRange([12, 37]);
        }
        var result = new List<byte> { 2, 0, 5, (byte)(topLength >> 8), (byte)topLength };
        result.AddRange(top);
        result.AddRange(global);
        if (store.Length > 0) result.AddRange([(byte)(store.Length >> 8), (byte)store.Length, .. store]);
        result.AddRange(charStrings);
        result.AddRange(fdSelect);
        result.AddRange(Index2Ot08(spec.NoFontDicts ? [] : fontDicts));
        result.AddRange(tail);
        return [.. result];
    }

    /// <summary>A CFF2 table with one empty Private DICT and the given charstrings (glyph 0 an empty .notdef, then one glyph per charstring).</summary>
    internal static byte[] Cff2GlyphsOt08(params string[] glyphs) => Cff2Ot08(new([[], .. glyphs.Select(CsOt08)]));

    /// <summary>fvar with one axis „wght“ 100 … 900, default 400, no named instances.</summary>
    internal static byte[] FvarOt08()
    {
        var f = new byte[16 + 20];
        BinaryPrimitives.WriteUInt32BigEndian(f, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(4), 16);    // axes array offset
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(6), 2);     // reserved
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(8), 1);     // axis count
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(10), 20);   // axis size
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(12), 0);    // instances
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(14), 8);    // instance size
        "wght"u8.CopyTo(f.AsSpan(16));
        BinaryPrimitives.WriteInt32BigEndian(f.AsSpan(20), 100 << 16);
        BinaryPrimitives.WriteInt32BigEndian(f.AsSpan(24), 400 << 16);
        BinaryPrimitives.WriteInt32BigEndian(f.AsSpan(28), 900 << 16);
        BinaryPrimitives.WriteUInt16BigEndian(f.AsSpan(34), 256);  // axis name ID
        return f;
    }

    /// <summary>
    /// An OpenType font „OTTO“ around a CFF2 table: head (units per em, indexToLocFormat), maxp 0.5 (numGlyphs, by default the glyphs),
    /// hhea, hmtx (every glyph 600 wide), cmap (3,1) format 4 with „A“ → glyph 1 and „B“ → glyph 2, and further tables; no „CFF “, glyf or loca.
    /// </summary>
    internal static byte[] Cff2FontOt08(byte[] cff2, int glyphs, int unitsPerEm = 1000, int? numGlyphs = null, short indexToLocFormat = 0,
        IReadOnlyDictionary<string, byte[]>? extra = null)
    {
        var head = new byte[54];
        BinaryPrimitives.WriteUInt32BigEndian(head, 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12), 0x5F0F3CF5);
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), (ushort)unitsPerEm);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), indexToLocFormat);
        var maxp = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(maxp, 0x00005000);
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), (ushort)(numGlyphs ?? glyphs));
        var hhea = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(hhea, 0x00010000);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(4), 800);
        BinaryPrimitives.WriteInt16BigEndian(hhea.AsSpan(6), -200);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(10), 600);
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)glyphs);
        var hmtx = new byte[glyphs * 4];
        for (var g = 0; g < glyphs; g++) BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(g * 4), 600);
        var cmap = new byte[12 + 32];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), 12);
        var f = cmap.AsSpan(12);
        foreach (var (at, value) in (ReadOnlySpan<(int, int)>)[(0, 4), (2, 32), (6, 4), (8, 4), (10, 1), (12, 0), (14, 'B'), (16, 0xFFFF), (20, 'A'),
                     (22, 0xFFFF), (24, (1 - 'A') & 0xFFFF), (26, 1)])
            BinaryPrimitives.WriteUInt16BigEndian(f[at..], (ushort)value);
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = head, ["maxp"] = maxp, ["hhea"] = hhea, ["hmtx"] = hmtx, ["cmap"] = cmap, ["CFF2"] = cff2,
        };
        foreach (var (tag, data) in extra ?? new Dictionary<string, byte[]>()) tables[tag] = data;
        return TrueTypeSubsetter.Assemble(tables);
    }

    /// <summary>What a probe of the CFF2 theories draws: the font, per glyph after .notdef a Type 2 charstring (null: empty), and glyph 0.</summary>
    internal sealed record Cff2ProbeOt08(byte[] Font, string?[] Glyphs, string? Notdef = null);

    /// <summary>
    /// The outline of a Type 2 charstring in a CFF (version 1) font — the reference for what a CFF2 glyph draws in the default instance; empty
    /// for null.
    /// </summary>
    internal static GlyphOutline ReferenceOt08(string? charString) =>
        charString is null ? GlyphOutline.Empty : CffFont.Parse(OneGlyphCffOt11(CsOt08(charString + " endchar"))).Outline(1);

    /// <summary>Two outlines are the same: verbs and points.</summary>
    internal static void SameOutlineOt08(GlyphOutline expected, GlyphOutline actual, string what)
    {
        Assert.True(expected.Verbs.SequenceEqual(actual.Verbs), what + ": Verben");
        Assert.True(expected.Points.SequenceEqual(actual.Points), what + ": Punkte");
    }

    /// <summary>The operators of a DICT (0–31, 12 x as 1200 + x, 255) with their operands, numbers decoded as in CFF/CFF2.</summary>
    internal static List<(int Op, List<double> Operands)> DictEntriesOt08(ReadOnlySpan<byte> d)
    {
        var result = new List<(int, List<double>)>();
        var operands = new List<double>();
        for (var i = 0; i < d.Length;)
        {
            int b = d[i++];
            if (b <= 27 || b is 31 or 255)
            {
                result.Add((b == 12 && i < d.Length ? 1200 + d[i++] : b, operands));
                operands = [];
            }
            else if (b == 28) { operands.Add((short)(d[i] << 8 | d[i + 1])); i += 2; }
            else if (b == 29) { operands.Add(BinaryPrimitives.ReadInt32BigEndian(d[i..])); i += 4; }
            else if (b == 30)
            {
                while (i < d.Length)
                {
                    var x = d[i++];
                    if ((x & 0xF0) == 0xF0 || (x & 0x0F) == 0x0F) break;
                }
                operands.Add(double.NaN);
            }
            else if (b <= 246) operands.Add(b - 139);
            else if (b <= 250) operands.Add((b - 247) * 256 + d[i++] + 108);
            else operands.Add(-(b - 251) * 256 - d[i++] - 108);
        }
        return result;
    }

    /// <summary>The items of a CFF2 INDEX (uint32 count) at <paramref name="at"/>.</summary>
    internal static byte[][] Index2ItemsOt08(byte[] table, int at)
    {
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(at));
        if (count == 0) return [];
        int offSize = table[at + 4];
        int Offset(int i)
        {
            var v = 0;
            for (var k = 0; k < offSize; k++) v = v << 8 | table[at + 5 + i * offSize + k];
            return v;
        }
        var data = at + 5 + (count + 1) * offSize - 1;
        return [.. Enumerable.Range(0, count).Select(i => table[(data + Offset(i))..(data + Offset(i + 1))])];
    }

    /// <summary>
    /// A CFF2 table written by the redaction is static: no VariationStore in the Top DICT, no global subroutines, no charstring calls a subroutine
    /// or uses blend, vsindex, return or endchar, no Private DICT names local subroutines, vsindex or blend — and the font has none of the
    /// tables of the variations.
    /// </summary>
    internal static void AssertStaticCff2Ot08(OpenTypeFont scrubbed)
    {
        var table = scrubbed.TableData("CFF2").ToArray();
        Assert.Equal(2, table[0]);
        int headerSize = table[2], topSize = table[3] << 8 | table[4];
        var top = DictEntriesOt08(table.AsSpan(headerSize, topSize));
        Assert.DoesNotContain(top, e => e.Op == 24);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(headerSize + topSize)));
        foreach (var cs in Index2ItemsOt08(table, (int)top.Single(e => e.Op == 17).Operands[^1]))
            foreach (var op in (int[])[10, 29, 11, 14, 15, 16]) Assert.DoesNotContain(op, OperatorsOt11(cs));
        foreach (var fd in Index2ItemsOt08(table, (int)top.Single(e => e.Op == 1236).Operands[^1]))
        {
            var location = DictEntriesOt08(fd).Single(e => e.Op == 18).Operands;
            var ops = DictEntriesOt08(table.AsSpan((int)location[1], (int)location[0])).Select(e => e.Op).ToList();
            foreach (var op in (int[])[19, 22, 23]) Assert.DoesNotContain(op, ops);
        }
        foreach (var tag in (string[])["fvar", "avar", "STAT", "HVAR", "VVAR", "MVAR", "gvar"]) Assert.False(scrubbed.HasTable(tag), tag);
    }

    /// <summary>
    /// What every reading of a font „OTTO“ with only a CFF2 table gives now (question 1, docs/FEHLERGEDAECHTNIS.md, No. 1158): it parses, is not
    /// broken, has no 'CFF ' and is not intact (a strict PDF reader knows no CFF2), and each glyph draws in the default instance what the probe
    /// says (the same charstring read as Type 2, blends resolved). The redaction keeping every glyph draws them all as before in a static CFF2
    /// table; keeping only glyph 0, every other glyph is empty. Compressing (subsetting) still refuses such a font: it stays as it is.
    /// </summary>
    internal static void AssertCff2ReadOt08(Cff2ProbeOt08 probe)
    {
        var font = probe.Font;
        var parsed = OpenTypeFont.Parse(font);
        Assert.False(parsed.Broken);
        Assert.True(parsed.HasTable("CFF2"));
        Assert.Null(parsed.Cff);
        Assert.False(parsed.Intact);
        Assert.True(parsed.GlyphCount > probe.Glyphs.Length);
        SameOutlineOt08(ReferenceOt08(probe.Notdef), parsed.Outline(0), "Glyphe 0");
        for (var i = 0; i < probe.Glyphs.Length; i++) SameOutlineOt08(ReferenceOt08(probe.Glyphs[i]), parsed.Outline(i + 1), $"Glyphe {i + 1}");

        var all = Enumerable.Range(0, parsed.GlyphCount).ToArray();
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, all));
        foreach (var g in all) SameOutlineOt08(parsed.Outline(g), scrubbed.Outline(g), $"bereinigt, Glyphe {g}");
        AssertStaticCff2Ot08(scrubbed);
        var notdefOnly = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, [0]));
        SameOutlineOt08(parsed.Outline(0), notdefOnly.Outline(0), "nur Glyphe 0");
        for (var g = 1; g < parsed.GlyphCount; g++) Assert.True(notdefOnly.Outline(g).IsEmpty, $"entfernte Glyphe {g}");
        Assert.Throws<FontFormatException>(() => TrueTypeSubsetter.Subset(font, [0, 1]));
    }

    private const string RectOt08 = "100 0 rmoveto 200 0 rlineto 0 200 rlineto";

    /// <summary>The probes of ot-08, each a font „OTTO“ with only a CFF2 table, and what each glyph draws.</summary>
    private static Cff2ProbeOt08 ProbeOt08(string name)
    {
        Cff2ProbeOt08 Glyphs(params string[] g) => new(Cff2FontOt08(Cff2GlyphsOt08(g), g.Length + 1), g);
        // the Private DICT changes no outline; with a variation store fvar beside it
        Cff2ProbeOt08 WithPrivate(string dict, Cff2VariationsOt08? variations = null, string glyph = RectOt08, string? drawn = null) =>
            new(Cff2FontOt08(Cff2Ot08(new([[], CsOt08(glyph)], Privates: [new(DictOt08(dict))], Variations: variations)), 2,
                extra: variations is null ? null : new Dictionary<string, byte[]> { ["fvar"] = FvarOt08() }), [drawn ?? glyph]);
        return name switch
        {
            "Rect" => Glyphs(RectOt08),
            // glyph 1 takes the subroutine of Font DICT 0, glyph 2 that of Font DICT 1 (FontDICTSelect format 0: 0 0 1)
            "TwoFontDicts" => new(Cff2FontOt08(Cff2Ot08(new([[], CsOt08("-107 callsubr"), CsOt08("-107 callsubr")],
                Privates: [new([], [CsOt08("0 0 rmoveto 100 0 rlineto 0 100 rlineto")]), new([], [CsOt08("0 0 rmoveto 0 300 rlineto 300 0 rlineto")])],
                FdSelect: [0, 0, 1])), 3), ["0 0 rmoveto 100 0 rlineto 0 100 rlineto", "0 0 rmoveto 0 300 rlineto 300 0 rlineto"]),
            "EmptyPrivate" => new(Cff2FontOt08(Cff2Ot08(new([[], CsOt08(RectOt08)], Privates: [new(null)])), 2), [RectOt08]),
            "PrivateBlendBlueValues" => WithPrivate("-15 0 5 5 2 blend BlueValues", OneRegionOt08),
            "BlueValuesDelta" => WithPrivate("-15 15 700 15 -168 12 BlueValues"),
            "BlueScaleBcd" => WithPrivate("0x1E 0x0A 0x03 0x96 0x25 0xFF BlueScale"),
            // no Font DICT at all: tolerated, the glyph draws without local subroutines
            "EmptyFontDictIndex" => new(Cff2FontOt08(Cff2Ot08(new([[], CsOt08(RectOt08)], NoFontDicts: true)), 2), [RectOt08]),
            "AllPrivateOperators" => new(Cff2FontOt08(Cff2Ot08(new([[], CsOt08("-107 callsubr")], Privates: [new(DictOt08(
                    "0 vsindex -15 0 5 5 2 blend BlueValues -250 10 OtherBlues -15 15 FamilyBlues -250 10 FamilyOtherBlues 0.039625 BlueScale 7 BlueShift " +
                    "1 BlueFuzz 50 StdHW 80 StdVW 50 10 StemSnapH 80 12 StemSnapV 0 LanguageGroup 0.06 ExpansionFactor"), [CsOt08(RectOt08)])],
                Variations: OneRegionOt08)), 2, extra: new Dictionary<string, byte[]> { ["fvar"] = FvarOt08() }), [RectOt08]),
            "LocalSubr" => new(Cff2FontOt08(Cff2Ot08(new([[], CsOt08("-107 callsubr")], Privates: [new([], [CsOt08(RectOt08)])])), 2), [RectOt08]),
            // LocalSubrINDEXOffset beyond the table: no subroutines, the call draws nothing
            "LocalSubrOutside" => new(Cff2FontOt08(Cff2Ot08(new([[], CsOt08("-107 callsubr")], Privates: [new(DictOt08("0x1D 0x00 0x0F 0x42 0x40 Subrs"))])), 2), [null]),
            // vsindex 1 in the Private DICT: k = 2, so „100 5 6 1 blend“ leaves 100 (with k = 1 it would leave 100 5)
            "PrivateVsindex" => WithPrivate("1 vsindex", TwoDataSetsOt08, "100 5 6 1 blend 0 rmoveto 200 0 rlineto 0 200 rlineto", RectOt08),
            "BlueValues" => WithPrivate("-15 15 700 15 BlueValues"),
            "BlueValuesInvalid" => WithPrivate("700 -10 3 1 3 1 3 1 3 1 3 1 3 1 3 1 BlueValues"),
            "OtherBluesInvalid" => WithPrivate("-250 10 20 10 20 10 20 10 20 10 20 -5 OtherBlues"),
            "FamilyBlues" => WithPrivate("-15 15 547 12 FamilyBlues"),
            "FamilyBluesMissing" => WithPrivate("-15 15 547 12 BlueValues"),
            "FamilyOtherBlues" => WithPrivate("-250 10 FamilyOtherBlues"),
            "BlueScaleHalf" => WithPrivate("0.5 BlueScale"),
            "BlueScale11pt" => WithPrivate("0.04379 BlueScale"),
            "FlexDepth8BlueShift7" => WithPrivate("7 BlueShift", glyph: "0 0 rmoveto 100 4 100 4 100 0 100 0 100 -4 100 -4 50 flex"),
            "BlueFuzz0" => WithPrivate("0 BlueFuzz"),
            "BlueFuzz3" => WithPrivate("3 BlueFuzz"),
            "StdHwVw" => WithPrivate("50 StdHW 80 StdVW"),
            "StemSnap" => WithPrivate("50 10 StemSnapH 80 12 StemSnapV"),
            "StdHwNegative" => WithPrivate("-5 StdHW"),
            "StemSnapDescending" => WithPrivate("80 -10 -10 StemSnapH"),
            "LanguageGroup1" => WithPrivate("1 LanguageGroup"),
            "LanguageGroup7" => WithPrivate("7 LanguageGroup"),
            "ExpansionFactor" => WithPrivate("1 LanguageGroup 0.5 ExpansionFactor"),
            "OtherBluesBeforeBlueValues" => WithPrivate("-250 10 OtherBlues -15 15 700 15 BlueValues"),
            // the rectangle, then moves that change nothing (the dangling ones drop out of the outline)
            "LongCharString" => new(Cff2FontOt08(Cff2GlyphsOt08(LongCharStringOt08()), 2), [RectOt08]),
            "GlobalSubr" => new(Cff2FontOt08(Cff2Ot08(new([[], CsOt08("-107 callgsubr")], GlobalSubrs: [CsOt08(RectOt08)])), 2), [RectOt08]),
            "EmptyCharString" => new(Cff2FontOt08(Cff2Ot08(new([[], [], CsOt08(RectOt08)])), 3), [null, RectOt08]),
            _ => throw new ArgumentException(name),
        };
    }

    /// <summary>A rectangle, then pairs of zeros moved to (a no-op rmoveto) up to 65 535 bytes, the largest charstring allowed.</summary>
    private static string LongCharStringOt08()
    {
        var text = new System.Text.StringBuilder(RectOt08);
        var length = CsOt08(RectOt08).Length;
        while (length + 3 <= 65_535)
        {
            text.Append(" 0 0 rmoveto");
            length += 3;
        }
        while (length++ < 65_535) text.Append(" 0");
        return text.ToString();
    }

    // ------------------------------------------------------------ 8.9 to 8.10.2

    /// <summary>
    /// OT 8.9 sentences 1–21 and 8.9.1 to 8.9.5, 8.10.1 sentences 1–14, 8.10.2 sentences 7–10 (reader and redaction, question 1): PDF-Knead reads
    /// the CFF2 table in the default instance — for each Private DICT, Font DICT and charstring arrangement of these sentences (one or two Font
    /// DICTs with FontDICTSelect, an empty Private DICT, blend and vsindex in the Private DICT, delta arrays, BCD numbers, every Private DICT
    /// operator, local and global subroutines, hint values breaking the rules for font makers, a charstring of 65 535 bytes, an empty charstring)
    /// each glyph draws what its charstring says (hints and Private DICT values change no outline; a LocalSubrINDEX beyond the table leaves the
    /// call without effect), and the redaction writes a static CFF2 table (8.9 sentences 22–26: no VariationStore, no subroutines).
    /// </summary>
    [Theory]
    [InlineData("Rect")]                       // 8.9 1–2
    [InlineData("TwoFontDicts")]               // 8.9 3–7
    [InlineData("EmptyPrivate")]               // 8.9 8–10
    [InlineData("PrivateBlendBlueValues")]     // 8.9 11–12, 8.9.3 17–28
    [InlineData("BlueValuesDelta")]            // 8.9 13–17
    [InlineData("BlueScaleBcd")]               // 8.9 18–19
    [InlineData("EmptyFontDictIndex")]         // 8.9 20–21
    [InlineData("AllPrivateOperators")]        // 8.9.1
    [InlineData("LocalSubr")]                  // 8.9.2
    [InlineData("LocalSubrOutside")]           // 8.9.2
    [InlineData("PrivateVsindex")]             // 8.9.3 4–16
    [InlineData("BlueValues")]                 // 8.9.4 1–8
    [InlineData("BlueValuesInvalid")]          // 8.9.4 9–21
    [InlineData("OtherBluesInvalid")]          // 8.9.4 30–43
    [InlineData("FamilyBlues")]                // 8.9.4 44–58, 59–67
    [InlineData("FamilyBluesMissing")]         // 8.9.4 59–67
    [InlineData("FamilyOtherBlues")]           // 8.9.4 68–75
    [InlineData("BlueScaleHalf")]              // 8.9.4 76–92
    [InlineData("BlueScale11pt")]              // 8.9.4 93–99
    [InlineData("FlexDepth8BlueShift7")]       // 8.9.4 100–117
    [InlineData("BlueFuzz0")]                  // 8.9.4 118–133
    [InlineData("BlueFuzz3")]                  // 8.9.4 118–133
    [InlineData("StdHwVw")]                    // 8.9.4 134–147
    [InlineData("StemSnap")]                   // 8.9.4 148–161
    [InlineData("StdHwNegative")]              // 8.9.4 162–174
    [InlineData("StemSnapDescending")]         // 8.9.4 175–185
    [InlineData("LanguageGroup1")]             // 8.9.4 186–203
    [InlineData("LanguageGroup7")]             // 8.9.4 186–203
    [InlineData("ExpansionFactor")]            // 8.9.4 204–215
    [InlineData("OtherBluesBeforeBlueValues")] // 8.9.5
    [InlineData("LongCharString")]             // 8.10.1 1–9
    [InlineData("GlobalSubr")]                 // 8.10.1 10–14
    [InlineData("EmptyCharString")]            // 8.10.2 7–10
    public void Cff2PrivateDictAndCharStrings_AreRead_DefaultInstanceDrawn(string probe) => AssertCff2ReadOt08(ProbeOt08(probe));

    /// <summary>
    /// OT 8.9 sentences 1–2 (reader): a font „OTTO“ with only a CFF2 table is taken as an embedded program (not broken) whose glyphs are found as
    /// in a TrueType font (CFF2 has no charset, names or encoding: cmap and post), and the display draws its outline from the CFF2 table.
    /// </summary>
    [Fact]
    public void Cff2Font_IsTakenAsEmbeddedTrueTypeProgram_AndDrawn()
    {
        var program = FontProgram.FromBytes(ProbeOt08("Rect").Font);
        Assert.NotNull(program);
        Assert.True(program.IsEmbedded);
        Assert.True(program.IsTrueType);
        Assert.Null(program.Cff);
        SameOutlineOt08(ReferenceOt08(RectOt08), program.OpenType!.Outline(1), "Glyphe 1");
    }

    /// <summary>
    /// OT 8.10.1 sentences 15–17 (reader): the advance widths of a CFF2 font are in hmtx, not in the charstrings — glyph 1 is 600 wide (hmtx) and
    /// draws its rectangle.
    /// </summary>
    [Fact]
    public void Cff2Font_AdvanceWidthComesFromHmtx()
    {
        var font = OpenTypeFont.Parse(ProbeOt08("Rect").Font);
        Assert.Equal(600, font.AdvanceWidth(1));
        Assert.False(font.Outline(1).IsEmpty);
    }

    /// <summary>
    /// OT 8.10.2 sentences 1–6 (reader, tolerant): the CharStringINDEX holds three charstrings, maxp says five glyphs — the glyph count comes from
    /// maxp (5), glyphs 1 and 2 draw, 3 and 4 beyond the charstrings are empty, nothing fails.
    /// </summary>
    [Fact]
    public void Cff2Font_MaxpCountsMoreGlyphsThanCharStrings_NothingFails()
    {
        var font = OpenTypeFont.Parse(Cff2FontOt08(Cff2GlyphsOt08(RectOt08, RectOt08), 3, numGlyphs: 5));
        Assert.Equal(5, font.GlyphCount);
        Assert.False(font.Outline(1).IsEmpty || font.Outline(2).IsEmpty);
        Assert.True(font.Outline(0).IsEmpty && font.Outline(3).IsEmpty && font.Outline(4).IsEmpty);
    }

    /// <summary>
    /// OT 8.10.2 sentences 1–6 and 8.10.2 sentences 7–10 (redaction, question 1): text „AB“ in a TrueType font whose program has only CFF2
    /// outlines; redacting the „A“ scrubs the program — „A“ (glyph 1) becomes an empty charstring and leaves the cmap, „B“ draws as before, the
    /// CFF2 table is static (finding S9 c; before, the program stayed byte for byte with the glyph of the redacted text).
    /// </summary>
    [Fact]
    public void Cff2Font_RedactedText_ProgramScrubbedStatic()
    {
        var program = Cff2FontOt08(Cff2GlyphsOt08(RectOt08, "0 0 rmoveto 0 300 rlineto 300 0 rlineto"), 3);
        using var doc = PdfDocument.Open(PdfARuleCases.WithTrueType(program: () => program)());
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(70, 690, 87, 730))));
        var font = (PdfDictionary)after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F1"))!;
        var file = font.GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile2"))!;
        var data = after.GetStreamData(file);
        Assert.NotEqual(program, data);
        var before = OpenTypeFont.Parse(program);
        var scrubbed = OpenTypeFont.Parse(data);
        Assert.True(scrubbed.Outline(1).IsEmpty);
        Assert.Equal(0, scrubbed.GlyphForUnicode('A'));
        SameOutlineOt08(before.Outline(2), scrubbed.Outline(2), "B");
        Assert.Equal(2, scrubbed.GlyphForUnicode('B'));
        AssertStaticCff2Ot08(scrubbed);
    }

    /// <summary>
    /// OT 8.4.1 sentences 1–19, 8.5.2 and 8.11.3 (reader, security, docs/FEHLERGEDAECHTNIS.md, No. 1158): sizes from a CFF2 table are bounded — a
    /// CharStringINDEX counting 0xFFFFFFFF charstrings in a table of some bytes is refused before its offsets are allocated (the font has no
    /// outlines, nothing fails); 512 numbers before one rlineto draw 256 lines, 514 overflow the stack of 513 and make the glyph empty; a blend
    /// asking for more operands than the stack holds, and a vsindex after a blend, make the glyph empty; the glyphs beside them still draw.
    /// </summary>
    [Fact]
    public void Cff2_DamagedSizes_BoundedAndHarmless()
    {
        var table = Cff2GlyphsOt08(RectOt08);
        var top = DictEntriesOt08(table.AsSpan(table[2], table[3] << 8 | table[4]));
        var at = (int)top.Single(e => e.Op == 17).Operands[^1];
        table[at] = table[at + 1] = table[at + 2] = table[at + 3] = 0xFF;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var huge = OpenTypeFont.Parse(Cff2FontOt08(table, 2));
        Assert.True(huge.Outline(1).IsEmpty);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));

        string Lines(int pairs) => "0 0 rmoveto " + string.Concat(Enumerable.Repeat("1 0 ", pairs)) + "rlineto";
        var font = OpenTypeFont.Parse(Cff2FontOt08(Cff2Ot08(new([[], CsOt08(Lines(256)), CsOt08(Lines(257)), CsOt08("0 0 rmoveto 10 5 30000 blend 0 rlineto"),
            CsOt08("0 0 rmoveto 10 5 1 blend 0 rlineto 0 vsindex 0 10 rlineto"), CsOt08(RectOt08)], Variations: OneRegionOt08)), 6,
            extra: new Dictionary<string, byte[]> { ["fvar"] = FvarOt08() }));
        Assert.Equal(256, font.Outline(1).Verbs.Count(v => v == PdfKnead.Core.Pdf.Graphics.PathVerb.Line));
        Assert.True(font.Outline(2).IsEmpty);
        Assert.True(font.Outline(3).IsEmpty);
        Assert.True(font.Outline(4).IsEmpty);
        SameOutlineOt08(ReferenceOt08(RectOt08), font.Outline(5), "Glyphe 5");
    }
}
