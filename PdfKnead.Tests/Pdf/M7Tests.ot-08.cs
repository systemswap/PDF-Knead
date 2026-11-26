using System.Buffers.Binary;
using System.Globalization;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Konformitätsabgleich M7, package ot-08 (OpenType, CFF2 table: Private DICT 8.9 with 8.9.1–8.9.5, CharString 8.10.1 and 8.10.2): the tests the
/// sentence-by-sentence drafts named as missing. PDF-Knead does not read the CFF2 table (open question to the user); the tests pin the
/// behaviour of today — the glyphs of such a font have no outline, the tables beside the outlines are read, nothing writes CFF2. The CFF2 builder
/// of this file serves the packages ot-09, ot-10 and ot-11 as well.
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

    /// <summary>
    /// The behaviour of today for a font whose outlines are in CFF2: it parses without error and is not broken, but CFF2 is not read (no CFF, not
    /// intact), every glyph has an empty outline, and neither scrubbing nor subsetting writes it (both refuse it as without outlines).
    /// </summary>
    internal static void AssertCff2NotReadOt08(byte[] font)
    {
        var parsed = OpenTypeFont.Parse(font);
        Assert.False(parsed.Broken);
        Assert.True(parsed.HasTable("CFF2"));
        Assert.Null(parsed.Cff);
        Assert.False(parsed.Intact);
        Assert.True(parsed.GlyphCount > 0);
        for (var g = 0; g < Math.Min(parsed.GlyphCount, 32); g++) Assert.True(parsed.Outline(g).IsEmpty, $"Umriss von Glyphe {g}");
        Assert.Throws<FontFormatException>(() => TrueTypeSubsetter.Scrub(font, [0, 1]));
        Assert.Throws<FontFormatException>(() => TrueTypeSubsetter.Subset(font, [0, 1]));
    }

    private const string RectOt08 = "100 0 rmoveto 200 0 rlineto 0 200 rlineto";

    /// <summary>The probes of ot-08, each a font „OTTO“ with only a CFF2 table.</summary>
    private static byte[] ProbeOt08(string name)
    {
        byte[] Glyphs(params string[] g) => Cff2FontOt08(Cff2GlyphsOt08(g), g.Length + 1);
        byte[] WithPrivate(string dict, Cff2VariationsOt08? variations = null, string glyph = RectOt08) =>
            Cff2FontOt08(Cff2Ot08(new([[], CsOt08(glyph)], Privates: [new(DictOt08(dict))], Variations: variations)), 2,
                extra: variations is null ? null : new Dictionary<string, byte[]> { ["fvar"] = FvarOt08() });
        return name switch
        {
            "Rect" => Glyphs(RectOt08),
            "TwoFontDicts" => Cff2FontOt08(Cff2Ot08(new([[], CsOt08("-107 callsubr"), CsOt08("-107 callsubr")],
                Privates: [new([], [CsOt08("0 0 rmoveto 100 0 rlineto 0 100 rlineto")]), new([], [CsOt08("0 0 rmoveto 0 300 rlineto 300 0 rlineto")])],
                FdSelect: [0, 0, 1])), 3),
            "EmptyPrivate" => Cff2FontOt08(Cff2Ot08(new([[], CsOt08(RectOt08)], Privates: [new(null)])), 2),
            "PrivateBlendBlueValues" => WithPrivate("-15 0 5 5 2 blend BlueValues", OneRegionOt08),
            "BlueValuesDelta" => WithPrivate("-15 15 700 15 -168 12 BlueValues"),
            "BlueScaleBcd" => WithPrivate("0x1E 0x0A 0x03 0x96 0x25 0xFF BlueScale"),
            "EmptyFontDictIndex" => Cff2FontOt08(Cff2Ot08(new([[], CsOt08(RectOt08)], NoFontDicts: true)), 2),
            "AllPrivateOperators" => Cff2FontOt08(Cff2Ot08(new([[], CsOt08("-107 callsubr")], Privates: [new(DictOt08(
                    "0 vsindex -15 0 5 5 2 blend BlueValues -250 10 OtherBlues -15 15 FamilyBlues -250 10 FamilyOtherBlues 0.039625 BlueScale 7 BlueShift " +
                    "1 BlueFuzz 50 StdHW 80 StdVW 50 10 StemSnapH 80 12 StemSnapV 0 LanguageGroup 0.06 ExpansionFactor"), [CsOt08(RectOt08)])],
                Variations: OneRegionOt08)), 2, extra: new Dictionary<string, byte[]> { ["fvar"] = FvarOt08() }),
            "LocalSubr" => Cff2FontOt08(Cff2Ot08(new([[], CsOt08("-107 callsubr")], Privates: [new([], [CsOt08(RectOt08)])])), 2),
            "LocalSubrOutside" => Cff2FontOt08(Cff2Ot08(new([[], CsOt08("-107 callsubr")], Privates: [new(DictOt08("0x1D 0x00 0x0F 0x42 0x40 Subrs"))])), 2),
            "PrivateVsindex" => WithPrivate("1 vsindex", TwoDataSetsOt08, "100 5 6 1 blend 0 rmoveto 200 0 rlineto 0 200 rlineto"),
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
            "LongCharString" => Glyphs(LongCharStringOt08()),
            "GlobalSubr" => Cff2FontOt08(Cff2Ot08(new([[], CsOt08("-107 callgsubr")], GlobalSubrs: [CsOt08(RectOt08)])), 2),
            "EmptyCharString" => Cff2FontOt08(Cff2Ot08(new([[], [], CsOt08(RectOt08)])), 3),
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
    /// OT 8.9 sentences 1–21 and 8.9.1 to 8.9.5, 8.10.1 sentences 1–14, 8.10.2 sentences 7–10 (reader, today): PDF-Knead does not read the CFF2
    /// table — for each Private DICT, Font DICT and charstring arrangement of these sentences (one or two Font DICTs with FontDICTSelect, an
    /// empty Private DICT, blend and vsindex in the Private DICT, delta arrays, BCD numbers, every Private DICT operator, local and global
    /// subroutines, hint values breaking the rules for font makers, a charstring of 65 535 bytes, an empty charstring) the font parses without
    /// error, has no CFF and is not intact, every glyph has an empty outline, and scrubbing and subsetting refuse it (8.9 sentences 22–26: no
    /// CFF2 is written). Once CFF2 is read (question 1), the expected outlines replace these assertions.
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
    public void Cff2PrivateDictAndCharStrings_AreNotRead_GlyphsStayEmpty(string probe) => AssertCff2NotReadOt08(ProbeOt08(probe));

    /// <summary>
    /// OT 8.9 sentences 1–2 (reader, today): a font „OTTO“ with only a CFF2 table is taken as an embedded program (not broken), and as one
    /// with TrueType outlines, because it has no CFF — so the display finds no outline and draws nothing; no substitute is chosen.
    /// </summary>
    [Fact]
    public void Cff2Font_IsTakenAsEmbeddedTrueTypeProgram()
    {
        var program = FontProgram.FromBytes(ProbeOt08("Rect"));
        Assert.NotNull(program);
        Assert.True(program.IsEmbedded);
        Assert.True(program.IsTrueType);
        Assert.Null(program.Cff);
        Assert.True(program.OpenType!.Outline(1).IsEmpty);
    }

    /// <summary>
    /// OT 8.10.1 sentences 15–17 (reader): the advance widths of a CFF2 font are in hmtx, not in the charstrings — PDF-Knead reads hmtx also
    /// beside a CFF2 table: glyph 1 is 600 wide, while its outline stays empty (CFF2 not read).
    /// </summary>
    [Fact]
    public void Cff2Font_AdvanceWidthComesFromHmtx()
    {
        var font = OpenTypeFont.Parse(ProbeOt08("Rect"));
        Assert.Equal(600, font.AdvanceWidth(1));
        Assert.True(font.Outline(1).IsEmpty);
    }

    /// <summary>
    /// OT 8.10.2 sentences 1–6 (reader, today): the CharStringINDEX holds three charstrings, maxp says five glyphs — the glyph count comes from
    /// maxp (5), every outline (0 … 4) is empty, nothing fails.
    /// </summary>
    [Fact]
    public void Cff2Font_MaxpCountsMoreGlyphsThanCharStrings_NothingFails()
    {
        var font = OpenTypeFont.Parse(Cff2FontOt08(Cff2GlyphsOt08(RectOt08, RectOt08), 3, numGlyphs: 5));
        Assert.Equal(5, font.GlyphCount);
        for (var g = 0; g < 5; g++) Assert.True(font.Outline(g).IsEmpty);
    }

    /// <summary>
    /// OT 8.10.2 sentences 1–6 (redaction, today): text in a TrueType font whose program has only CFF2 outlines; redacting the „A“ leaves the
    /// program as it was, byte for byte — scrubbing handles only glyf and „CFF “ (FontScrub.ScrubProgram), so all charstrings stay (finding S9 c,
    /// question 1).
    /// </summary>
    [Fact]
    public void Cff2Font_RedactedText_ProgramStaysWhole()
    {
        var program = Cff2FontOt08(Cff2GlyphsOt08(RectOt08, "0 0 rmoveto 0 300 rlineto 300 0 rlineto"), 3);
        using var doc = PdfDocument.Open(PdfARuleCases.WithTrueType(program: () => program)());
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(70, 690, 87, 730))));
        var font = (PdfDictionary)after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.Get(PdfName.Get("F1"))!;
        var file = font.GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile2"))!;
        Assert.Equal(program, after.GetStreamData(file));
    }
}
