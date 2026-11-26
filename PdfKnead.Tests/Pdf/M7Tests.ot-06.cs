using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-06 (OpenType specification, CBDT, CBLC, CFF and the first sections of CFF2): the tests the drafts of the
/// sentence-by-sentence comparison asked for — colour bitmaps of the signature fonts, CFF in OpenType fonts, and CFF2, which is not read.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (ot-06): CBLC/CBDT

    private static int Ot06M => Ot05Sans.Value.GlyphForUnicode('M');

    /// <summary>
    /// One strike for <see cref="Ot06ColorFont"/>: its BitmapSize values, one index subtable for glyph „M“ and the glyph's record in CBDT
    /// (metrics and data as the image format has them); <paramref name="IndexMetrics"/> are the bigGlyphMetrics of index format 2.
    /// </summary>
    private sealed record Ot06Strike(int PpemX, int PpemY, int BitDepth, int Flags, int IndexFormat, int ImageFormat, byte[] Record, byte[]? IndexMetrics = null);

    /// <summary>Liberation Sans with CBLC/CBDT of the strikes given (glyph „M“ only).</summary>
    private static OpenTypeFont Ot06ColorFont(Ot06Strike[] strikes, int cblcMajor = 3, int cbdtMajor = 3, uint? numSizes = null)
    {
        var cbdt = new List<byte> { 0, (byte)cbdtMajor, 0, 0 };
        var imageAt = new int[strikes.Length];
        for (var i = 0; i < strikes.Length; i++)
        {
            imageAt[i] = cbdt.Count;
            cbdt.AddRange(strikes[i].Record);
        }
        var cblc = new List<byte>();
        void U8(int v) => cblc.Add((byte)v);
        void U16(int v) { U8(v >> 8); U8(v); }
        void U32(long v) { U16((int)(v >> 16)); U16((int)v); }
        U16(cblcMajor);
        U16(0);
        U32(numSizes ?? (uint)strikes.Length);
        var arrays = new List<byte[]>();
        foreach (var (s, i) in strikes.Select((s, i) => (s, i)))
        {
            var a = new List<byte>();
            void A16(int v) => a.AddRange([(byte)(v >> 8), (byte)v]);
            void A32(long v) { A16((int)(v >> 16)); A16((int)v); }
            A16(Ot06M); A16(Ot06M); A32(8);                       // the array: glyph M, its subtable 8 bytes on
            A16(s.IndexFormat); A16(s.ImageFormat); A32(imageAt[i]);
            if (s.IndexFormat == 2)
            {
                A32(s.Record.Length);
                a.AddRange(s.IndexMetrics ?? new byte[8]);
            }
            else
            {
                A32(0);
                A32(s.Record.Length);
            }
            arrays.Add([.. a]);
        }
        var at = 8 + 48 * strikes.Length;
        for (var i = 0; i < strikes.Length; i++)
        {
            var s = strikes[i];
            U32(at);
            U32(arrays[i].Length);
            U32(1);
            U32(0);
            for (var m = 0; m < 2; m++)
            {
                U8(s.PpemY * 8 / 10);
                U8(-s.PpemY * 2 / 10);
                cblc.AddRange(new byte[10]);
            }
            U16(Ot06M); U16(Ot06M);
            U8(s.PpemX); U8(s.PpemY); U8(s.BitDepth); U8(s.Flags);
            at += arrays[i].Length;
        }
        foreach (var a in arrays) cblc.AddRange(a);
        return OpenTypeFont.Parse(Ot05SansWith(("CBLC", [.. cblc]), ("CBDT", [.. cbdt])));
    }

    /// <summary>A PNG of the size given, every pixel the colour given (RGBA).</summary>
    private static byte[] Ot06Png(int width, int height, byte r = 0x10, byte g = 0x20, byte b = 0x90, byte a = 0xFF)
    {
        using var output = new MemoryStream();
        PngEncoder.Encode(width, height, 8, PngColorType.Rgba, (_, row) =>
        {
            for (var x = 0; x + 3 < row.Length; x += 4) (row[x], row[x + 1], row[x + 2], row[x + 3]) = (r, g, b, a);
        }, output);
        return output.ToArray();
    }

    /// <summary>A CBDT record of image format 17: small metrics and the PNG.</summary>
    private static byte[] Ot06Format17(byte[] png, int height, int width, int bearingX, int bearingY) =>
        [(byte)height, (byte)width, (byte)(sbyte)bearingX, (byte)(sbyte)bearingY, (byte)width, .. Ot06Length(png.Length), .. png];

    /// <summary>A CBDT record of image format 18: big metrics (with the vertical ones) and the PNG.</summary>
    private static byte[] Ot06Format18(byte[] png, int height, int width, int bearingX, int bearingY, int vertBearingX, int vertBearingY) =>
        [(byte)height, (byte)width, (byte)(sbyte)bearingX, (byte)(sbyte)bearingY, (byte)width, (byte)(sbyte)vertBearingX, (byte)(sbyte)vertBearingY,
            (byte)height, .. Ot06Length(png.Length), .. png];

    private static byte[] Ot06Length(int length) => [(byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length];

    /// <summary>A PNG with chunks inserted after IHDR (type and data; the CRC is computed).</summary>
    private static byte[] Ot06PngWithChunks(byte[] png, params (string Type, byte[] Data)[] chunks)
    {
        var result = new List<byte>(png.AsSpan(0, 8 + 25).ToArray());   // signature and IHDR
        foreach (var (type, data) in chunks)
        {
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            result.AddRange(Ot06Length(data.Length));
            result.AddRange(body);
            result.AddRange(Ot06Length((int)Ot06Crc(body)));
        }
        result.AddRange(png.AsSpan(8 + 25).ToArray());
        return [.. result];
    }

    private static uint Ot06Crc(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private static Ot06Strike Ot06PngStrike(int ppem, int bitDepth = 32, int flags = 1) =>
        new(ppem, ppem, bitDepth, flags, 1, 17, Ot06Format17(Ot06Png(8, 8), 8, 8, 1, 6));

    // ------------------------------------------------------------ helpers (ot-06): CFF

    /// <summary>A CFF of the letters given (outlines and widths of Liberation Sans, names „g1“, „g2“ …), glyph 0 empty.</summary>
    private static byte[] Ot06Cff(string letters, string name = "M7Probe")
    {
        var sans = Ot05Sans.Value;
        return CffBuilder.Build(name, new Matrix(1.0 / sans.UnitsPerEm, 0, 0, 1.0 / sans.UnitsPerEm, 0, 0),
            [.. letters.Select((c, i) => ($"g{i + 1}", sans.Outline(sans.GlyphForUnicode(c)), sans.AdvanceWidth(sans.GlyphForUnicode(c))))]);
    }

    /// <summary>An OpenType font with the CFF given in the tables of Liberation Sans (without glyf and loca).</summary>
    private static byte[] Ot06OpenTypeCff(byte[] cff, params (string Tag, byte[]? Data)[] more) =>
        Ot05SansWith([("CFF ", cff), ("glyf", null), ("loca", null), .. more]);

    /// <summary>Reads one INDEX (CFF 1: count u16) at a position: its items and the position after it.</summary>
    private static (List<byte[]> Items, int End) Ot06Index(byte[] cff, int at)
    {
        var count = Ot05U16(cff, at);
        if (count == 0) return ([], at + 2);
        var size = cff[at + 2];
        long Offset(int i) { long v = 0; for (var k = 0; k < size; k++) v = (v << 8) | cff[at + 3 + i * size + k]; return v; }
        var data = at + 3 + (count + 1) * size - 1;
        var items = Enumerable.Range(0, count).Select(i => cff.AsSpan((int)(data + Offset(i)), (int)(Offset(i + 1) - Offset(i))).ToArray()).ToList();
        return (items, (int)(data + Offset(count)));
    }

    /// <summary>
    /// A DICT with the offsets of operators 15, 16, 17, 18 (second operand), 12 36 and 12 37 moved by <paramref name="delta"/>; they must be
    /// written as five-byte numbers (as <see cref="CffBuilder"/> writes them), so the DICT keeps its size.
    /// </summary>
    private static byte[] Ot06MoveOffsets(byte[] dict, int delta)
    {
        var result = (byte[])dict.Clone();
        var operands = new List<int>();   // positions of the operands
        for (var i = 0; i < result.Length;)
        {
            int b0 = result[i];
            if (b0 <= 21)
            {
                var op = b0 == 12 ? 1200 + result[i + 1] : b0;
                var moved = op switch { 15 or 16 or 17 or 1236 or 1237 => operands.Count >= 1 ? operands[^1] : -1, 18 => operands.Count >= 2 ? operands[^1] : -1, _ => -1 };
                if (moved >= 0)
                {
                    Assert.Equal(29, result[moved]);
                    BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(moved + 1), BinaryPrimitives.ReadInt32BigEndian(result.AsSpan(moved + 1)) + delta);
                }
                operands.Clear();
                i += b0 == 12 ? 2 : 1;
                continue;
            }
            operands.Add(i);
            if (b0 == 28) i += 3;
            else if (b0 == 29) i += 5;
            else if (b0 == 30)
            {
                i++;
                while ((result[i] & 0x0F) != 0x0F && (result[i] & 0xF0) != 0xF0) i++;
                i++;
            }
            else if (b0 is >= 247 and <= 254) i += 2;
            else i++;
        }
        return result;
    }

    /// <summary>
    /// A CFF rewritten with other names and Top DICTs: <paramref name="topDicts"/> gets the Top DICT of the source and gives the new ones;
    /// their offsets are moved so that they still point to the parts after the Top DICT INDEX.
    /// </summary>
    private static byte[] Ot06Recompose(byte[] cff, IReadOnlyList<string> names, Func<byte[], IReadOnlyList<byte[]>> topDicts)
    {
        var (_, afterNames) = Ot06Index(cff, cff[2]);
        var (tops, rest) = Ot06Index(cff, afterNames);
        var newTops = topDicts(tops[0]);
        var nameIndex = CffSubsetter.Index([.. names.Select(Encoding.ASCII.GetBytes)]);
        var topIndex = CffSubsetter.Index(newTops);
        var delta = cff[2] + nameIndex.Length + topIndex.Length - rest;
        topIndex = CffSubsetter.Index([.. newTops.Select(t => Ot06MoveOffsets(t, delta))]);
        return [.. cff.AsSpan(0, cff[2]), .. nameIndex, .. topIndex, .. cff.AsSpan(rest)];
    }

    // ------------------------------------------------------------ helpers (ot-06): CFF2

    /// <summary>What <see cref="Ot06Cff2"/> writes.</summary>
    internal sealed record Ot06Cff2Options
    {
        public int HeaderSize { get; init; } = 5;
        public int Glyphs { get; init; } = 2;
        public double[]? FontMatrix { get; init; }
        public bool VariationStore { get; init; }
        public bool FontDictIndexOffset { get; init; } = true;
        public int FontDicts { get; init; } = 1;
        public byte[]? FdSelect { get; init; }
        public bool EmptyPrivate { get; init; }
        public bool PrivateOutside { get; init; }
        public int Operands { get; init; }
        public int UnitsPerEm { get; init; } = 2048;
    }

    /// <summary>
    /// A CFF2 table: header, TopDICT (offsets as five-byte numbers), empty GlobalSubrINDEX, CharStringINDEX (glyph 0 empty, the others a
    /// square), FontDICTINDEX with one PrivateDICT per FontDICT (each with a local subroutine INDEX), and what the options add.
    /// </summary>
    internal static byte[] Ot06Cff2(Ot06Cff2Options o)
    {
        static void Fixed(List<byte> d, int v) => d.AddRange([29, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        static void Real(List<byte> d, double v)
        {
            var text = v.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture);
            var nibbles = text.Select(c => c switch { '.' => 0xA, '-' => 0xE, _ => c - '0' }).Append(0xF).ToList();
            if (nibbles.Count % 2 != 0) nibbles.Add(0xF);
            d.Add(30);
            for (var i = 0; i < nibbles.Count; i += 2) d.Add((byte)(nibbles[i] << 4 | nibbles[i + 1]));
        }
        static byte[] Index32(IReadOnlyList<byte[]> items)
        {
            var total = items.Sum(i => i.Length) + 1;
            var size = total < 256 ? 1 : total < 65536 ? 2 : 4;
            var d = new List<byte> { (byte)(items.Count >> 24), (byte)(items.Count >> 16), (byte)(items.Count >> 8), (byte)items.Count, (byte)size };
            var offset = 1;
            for (var i = 0; i <= items.Count; i++)
            {
                for (var k = size - 1; k >= 0; k--) d.Add((byte)(offset >> (8 * k)));
                if (i < items.Count) offset += items[i].Length;
            }
            foreach (var item in items) d.AddRange(item);
            return [.. d];
        }

        // charstrings: a square of 100 units (50 50 rmoveto 100 0 rlineto 0 100 rlineto), the first one with Operands numbers before it
        byte[] square = [189, 189, 21, 239, 139, 5, 139, 239, 5];
        var charStrings = new List<byte[]> { Array.Empty<byte>() };
        for (var g = 1; g < o.Glyphs; g++)
            charStrings.Add(g == 1 && o.Operands > 0 ? [.. Enumerable.Repeat((byte)140, o.Operands), 5, .. square] : square);
        var charStringIndex = Index32(charStrings);

        List<byte> TopDict(int charStringsAt, int variationAt, int fontDictsAt, int fdSelectAt)
        {
            var d = new List<byte>();
            if (o.FontMatrix is { } m)
            {
                foreach (var v in m) Real(d, v);
                d.AddRange([12, 7]);
            }
            Fixed(d, charStringsAt);
            d.Add(17);
            if (o.VariationStore)
            {
                Fixed(d, variationAt);
                d.Add(24);
            }
            if (o.FontDictIndexOffset)
            {
                Fixed(d, fontDictsAt);
                d.AddRange([12, 36]);
            }
            if (o.FdSelect is not null)
            {
                Fixed(d, fdSelectAt);
                d.AddRange([12, 37]);
            }
            return d;
        }
        var topSize = TopDict(0, 0, 0, 0).Count;
        var globalAt = o.HeaderSize + topSize;
        var charStringsAt = globalAt + 4;
        var fontDictSize = 11;   // size and offset as five-byte numbers, operator 18
        var fontDictsAt = charStringsAt + charStringIndex.Length;
        var fontDictIndexLength = o.FontDicts == 0 ? 4 : 4 + 1 + (o.FontDicts + 1) + o.FontDicts * fontDictSize;   // an empty INDEX is its count
        var fdSelectAt = fontDictsAt + fontDictIndexLength;
        var privatesAt = fdSelectAt + (o.FdSelect?.Length ?? 0);
        const int privateBlock = 16;   // a PrivateDICT of 6 bytes (Subrs at +6) and its INDEX of 10
        var variationAt = privatesAt + o.FontDicts * privateBlock;
        byte[] variationStore = o.VariationStore ? [0, 8, 0, 1, 0, 0, 0, 8, 0, 0] : [];
        var end = variationAt + variationStore.Length;

        var table = new List<byte> { 2, 0, (byte)o.HeaderSize, (byte)(topSize >> 8), (byte)topSize };
        table.AddRange(new byte[o.HeaderSize - 5]);
        table.AddRange(TopDict(charStringsAt, variationAt, fontDictsAt, fdSelectAt));
        table.AddRange(new byte[4]);   // GlobalSubrINDEX: count 0
        table.AddRange(charStringIndex);
        var fontDicts = new List<byte[]>();
        for (var i = 0; i < o.FontDicts; i++)
        {
            var d = new List<byte>();
            var (size, offset) = o.EmptyPrivate ? (0, 0) : o.PrivateOutside ? (100, end - 10) : (6, privatesAt + i * privateBlock);
            Fixed(d, size);
            Fixed(d, offset);
            d.Add(18);
            fontDicts.Add([.. d]);
        }
        var fontDictIndex = new List<byte> { (byte)(o.FontDicts >> 24), (byte)(o.FontDicts >> 16), (byte)(o.FontDicts >> 8), (byte)o.FontDicts };
        if (o.FontDicts > 0) fontDictIndex.Add(1);
        for (var i = 0; o.FontDicts > 0 && i <= o.FontDicts; i++) fontDictIndex.Add((byte)(1 + i * fontDictSize));
        foreach (var d in fontDicts) fontDictIndex.AddRange(d);
        table.AddRange(fontDictIndex);
        table.AddRange(o.FdSelect ?? []);
        for (var i = 0; i < o.FontDicts; i++)
        {
            table.AddRange([29, 0, 0, 0, 6, 19]);                                       // Subrs at +6
            table.AddRange([0, 0, 0, 1, 1, 1, 4, .. i == 0 ? new byte[] { 139, 239, 5 } : [239, 139, 5]]);
        }
        table.AddRange(variationStore);
        Assert.Equal(end, table.Count);
        return [.. table];
    }

    /// <summary>
    /// An OpenType font („OTTO“) with the CFF2 table given and the tables a reader needs (head, hhea, maxp, hmtx, cmap, name, OS/2, post of
    /// Liberation Sans, maxp and hmtx for <paramref name="glyphs"/> glyphs), and further tables.
    /// </summary>
    internal static byte[] Ot06Cff2Font(byte[] cff2, int glyphs, int unitsPerEm = 2048, params (string Tag, byte[] Data)[] more)
    {
        var sans = Ot05SansFile.Value;
        var head = Ot05Table(sans, "head");
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), (ushort)unitsPerEm);
        var hhea = Ot05Table(sans, "hhea");
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), (ushort)glyphs);
        var hmtx = new byte[glyphs * 4];
        for (var g = 0; g < glyphs; g++) BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(g * 4), 500);
        var post = Ot05Table(sans, "post")[..32];
        BinaryPrimitives.WriteUInt32BigEndian(post, 0x00030000);
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = head, ["hhea"] = hhea, ["maxp"] = [0, 0, 0x50, 0, (byte)(glyphs >> 8), (byte)glyphs], ["hmtx"] = hmtx,
            ["cmap"] = Ot05Table(sans, "cmap"), ["name"] = Ot05Table(sans, "name"), ["OS/2"] = Ot05Table(sans, "OS/2"), ["post"] = post, ["CFF2"] = cff2,
        };
        foreach (var (tag, data) in more) tables[tag] = data;
        return TrueTypeSubsetter.Assemble(tables);
    }

    /// <summary>What every reading of an OpenType font with only CFF2 gives today: no CFF, not intact, no outlines, the matrix from head.</summary>
    internal static void Ot06AssertCff2NotRead(byte[] file, int unitsPerEm = 2048)
    {
        Assert.Equal("OTTO"u8.ToArray(), file[..4]);
        var font = OpenTypeFont.Parse(file);
        Assert.True(font.HasTable("CFF2"));
        Assert.Null(font.Cff);
        Assert.False(font.Intact);
        Assert.False(font.Broken);
        for (var g = 0; g < Math.Max(2, font.GlyphCount); g++) Assert.True(font.Outline(g).IsEmpty);
        Assert.Equal(Matrix.Scale(1.0 / unitsPerEm, 1.0 / unitsPerEm), font.FontMatrix);
    }

    // ------------------------------------------------------------ OT 5 CBDT

    /// <summary>
    /// OT 5.1 sentences 1–6 (reader, tolerant): the version of the CBDT header is not checked — a colour font with CBDT version 2.0 or 4.0
    /// still gives its glyph images.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void CbdtVersion_NotChecked(int major)
    {
        var color = ColorGlyphs.For(Ot06ColorFont([Ot06PngStrike(96)], cbdtMajor: major));
        Assert.NotNull(color);
        Assert.NotNull(color.Glyph(Ot06M));
    }

    /// <summary>
    /// OT 5.2 sentences 1–4 (reader): bit depth 32 means BGRA, premultiplied, and the image formats of EBDT hold in CBDT too — a strike of
    /// image format 1 (small metrics, one row of one pixel 00 80 00 80, half transparent green) gives that pixel.
    /// </summary>
    [Fact(Skip = "M7-Befund: unkomprimierte BGRA-Bilder (Bildformat 1) werden nicht gelesen, nur PNG (CbdtImages.Image)")]
    public void Cbdt_UncompressedBgraImage_IsRead()
    {
        byte[] record = [1, 1, 0, 1, 1, 0x00, 0x80, 0x00, 0x80];
        var color = ColorGlyphs.For(Ot06ColorFont([new Ot06Strike(96, 96, 32, 1, 1, 1, record)]));
        Assert.NotNull(color);
        var glyph = color.Glyph(Ot06M);
        Assert.NotNull(glyph);
        var p = glyph.Value.Image.Pixels;
        Assert.Equal((1, 1), (glyph.Value.Image.Width, glyph.Value.Image.Height));
        Assert.Equal(0, p[0]);
        Assert.True(p[1] >= 0x80);
        Assert.Equal(0, p[2]);
        Assert.Equal(0x80, p[3]);
    }

    /// <summary>
    /// OT 5.2 sentences 1–4 and 5.6 sentences 1–3 (reader): a 32-bit strike of 96 ppem with only image format 1 and one of 48 ppem with PNG
    /// images — the glyph still has an image (from either strike), not none.
    /// </summary>
    [Fact(Skip = "M7-Befund: ein 32-Bit-Strike nur mit Bildformat 1 verdrängt den kleineren PNG-Strike, die Glyphe hat kein Bild (CbdtImages.TryCreate)")]
    public void Cbdt_StrikeWithoutPngImages_DoesNotHideASmallerPngStrike()
    {
        byte[] record = [1, 1, 0, 1, 1, 0x00, 0x80, 0x00, 0x80];
        var color = ColorGlyphs.For(Ot06ColorFont([new Ot06Strike(96, 96, 32, 1, 1, 1, record), Ot06PngStrike(48)]));
        Assert.NotNull(color);
        Assert.NotNull(color.Glyph(Ot06M));
    }

    /// <summary>
    /// OT 5.3 sentence 4 (reader): the image data are sRGB whatever other chunks say — a format 17 glyph whose PNG also has gAMA (gamma 1.0)
    /// and iCCP gives the same pixels as without them.
    /// </summary>
    [Fact]
    public void CbdtPng_GammaAndIccChunksIgnored()
    {
        var png = Ot06Png(8, 8, 0x40, 0x80, 0xC0, 0xFF);
        byte[] profile;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(new byte[128]);
            profile = [.. "Probe"u8, 0, 0, .. output.ToArray()];
        }
        var tagged = Ot06PngWithChunks(png, ("gAMA", Ot06Length(100000)), ("iCCP", profile));
        var plain = ColorGlyphs.For(Ot06ColorFont([new Ot06Strike(96, 96, 32, 1, 1, 17, Ot06Format17(png, 8, 8, 1, 6))]))!.Glyph(Ot06M);
        var other = ColorGlyphs.For(Ot06ColorFont([new Ot06Strike(96, 96, 32, 1, 1, 17, Ot06Format17(tagged, 8, 8, 1, 6))]))!.Glyph(Ot06M);
        Assert.NotNull(plain);
        Assert.NotNull(other);
        Assert.Equal(plain.Value.Image.Pixels, other.Value.Image.Pixels);
        Assert.Equal((plain.Value.X, plain.Value.Y), (other.Value.X, other.Value.Y));
    }

    /// <summary>
    /// OT 5.3 sentence 5 (reader): each image has the size its metrics give — a format 17 glyph with metrics 10 × 10 and a PNG of 20 × 20 is
    /// left out or drawn at 10 × 10, not at 20 × 20.
    /// </summary>
    [Fact(Skip = "M7-Befund: die Bildgröße wird nicht mit der Metrik verglichen, das PNG von 20 × 20 wird in seiner Größe gesetzt (CbdtImages.Image)")]
    public void CbdtPng_LargerThanItsMetrics_NotDrawnAtItsOwnSize()
    {
        var color = ColorGlyphs.For(Ot06ColorFont([new Ot06Strike(96, 96, 32, 1, 1, 17, Ot06Format17(Ot06Png(20, 20), 10, 10, 0, 10))]));
        Assert.NotNull(color);
        var glyph = color.Glyph(Ot06M);
        Assert.True(glyph is null || glyph.Value.Image is { Width: 10, Height: 10 });
    }

    /// <summary>
    /// OT 5.4 sentences 1–5 (reader): colour glyphs are set horizontally only — a strike flagged vertical (0x02) reads its format 17 metrics as
    /// the horizontal ones, and the vertical bearings of format 18 change nothing.
    /// </summary>
    [Fact]
    public void Cbdt_VerticalMetricsAndFlagsNotUsed()
    {
        var png = Ot06Png(8, 8);
        var horizontal = ColorGlyphs.For(Ot06ColorFont([new Ot06Strike(96, 96, 32, 1, 1, 17, Ot06Format17(png, 8, 8, 2, 7))]))!.Glyph(Ot06M)!.Value;
        var vertical = ColorGlyphs.For(Ot06ColorFont([new Ot06Strike(96, 96, 32, 2, 1, 17, Ot06Format17(png, 8, 8, 2, 7))]))!.Glyph(Ot06M)!.Value;
        Assert.Equal((2, 7 - 8), (horizontal.X, horizontal.Y));
        Assert.Equal((horizontal.X, horizontal.Y), (vertical.X, vertical.Y));
        var plain = ColorGlyphs.For(Ot06ColorFont([new Ot06Strike(96, 96, 32, 1, 1, 18, Ot06Format18(png, 8, 8, 2, 7, 0, 0))]))!.Glyph(Ot06M)!.Value;
        var bearings = ColorGlyphs.For(Ot06ColorFont([new Ot06Strike(96, 96, 32, 1, 1, 18, Ot06Format18(png, 8, 8, 2, 7, 5, 9))]))!.Glyph(Ot06M)!.Value;
        Assert.Equal((plain.X, plain.Y), (bearings.X, bearings.Y));
        Assert.Equal((2, 7 - 8), (plain.X, plain.Y));
    }

    /// <summary>
    /// OT 5.5.3 sentences 1–4 (reader): image format 19 has its metrics in the CBLC — through index format 2 (big metrics with bearing x 3 and
    /// y 40) the image lies where they say; through index format 1, which has no metrics, the glyph has no image.
    /// </summary>
    [Fact]
    public void CbdtFormat19_MetricsFromTheIndexSubtable()
    {
        var png = Ot06Png(8, 8);
        byte[] record = [.. Ot06Length(png.Length), .. png];
        byte[] metrics = [8, 8, 3, 40, 8, 0, 0, 8];
        var withMetrics = ColorGlyphs.For(Ot06ColorFont([new Ot06Strike(96, 96, 32, 1, 2, 19, record, metrics)]))!.Glyph(Ot06M);
        Assert.NotNull(withMetrics);
        Assert.Equal((3, 40 - 8), (withMetrics.Value.X, withMetrics.Value.Y));
        Assert.Null(ColorGlyphs.For(Ot06ColorFont([new Ot06Strike(96, 96, 32, 1, 1, 19, record)]))!.Glyph(Ot06M));
    }

    /// <summary>
    /// OT 5.6 sentences 1–3 (reader): the largest strike is used and scaled down when drawn — two 32-bit strikes of 48 and 96 ppem: 96.
    /// </summary>
    [Fact]
    public void Cbdt_TwoStrikes_TheLargerIsUsed()
    {
        Assert.Equal(96, ColorGlyphs.For(Ot06ColorFont([Ot06PngStrike(48), Ot06PngStrike(96)]))!.PixelsPerEm);
        Assert.Equal(96, ColorGlyphs.For(Ot06ColorFont([Ot06PngStrike(96), Ot06PngStrike(48)]))!.PixelsPerEm);
    }

    // ------------------------------------------------------------ OT 6 CBLC

    /// <summary>
    /// OT 6 sentences 4–12 (reader): CBLC versions 3.0 and (tolerant) 2.0 are read, an unknown major version 4 is not; a numSizes of
    /// 0xFFFFFFFF with one BitmapSize record reads that one strike without error.
    /// </summary>
    [Fact]
    public void CblcHeader_VersionAndNumSizes()
    {
        Assert.Null(ColorGlyphs.For(Ot06ColorFont([Ot06PngStrike(96)], cblcMajor: 4)));
        Assert.NotNull(ColorGlyphs.For(Ot06ColorFont([Ot06PngStrike(96)], cblcMajor: 2))!.Glyph(Ot06M));
        var many = ColorGlyphs.For(Ot06ColorFont([Ot06PngStrike(96)], numSizes: 0xFFFFFFFF));
        Assert.NotNull(many);
        Assert.Equal(96, many.PixelsPerEm);
        Assert.NotNull(many.Glyph(Ot06M));
    }

    /// <summary>
    /// OT 6 sentences 22–27 (reader): only colour strikes (bit depth 32) are used, and ppemY gives the resolution — a strike of 8 bits at 200
    /// ppem beside one of 32 bits at 96 ppem: 96; a strike with ppemX 48 and ppemY 96: 96.
    /// </summary>
    [Fact]
    public void CblcBitmapSize_OnlyColourStrikes_PpemY()
    {
        Assert.Equal(96, ColorGlyphs.For(Ot06ColorFont([Ot06PngStrike(200, bitDepth: 8), Ot06PngStrike(96)]))!.PixelsPerEm);
        var uneven = Ot06PngStrike(96) with { PpemX = 48 };
        Assert.Equal(96, ColorGlyphs.For(Ot06ColorFont([uneven]))!.PixelsPerEm);
    }

    // ------------------------------------------------------------ OT 7 CFF

    /// <summary>
    /// OT 7 sentence 5 (reader): the advance comes from hmtx, which CFF doubles — an OpenType font whose hmtx gives glyph 1 a width of 700 while
    /// its charstring says 500: 700; the same font without hhea and hmtx: 500.
    /// </summary>
    [Fact]
    public void OpenTypeCff_AdvanceFromHmtx_CharstringWidthOnlyWithoutIt()
    {
        var sans = Ot05Sans.Value;
        var cff = CffBuilder.Build("M7Probe", new Matrix(1.0 / sans.UnitsPerEm, 0, 0, 1.0 / sans.UnitsPerEm, 0, 0),
            [("eins", sans.Outline(sans.GlyphForUnicode('A')), 500), ("zwei", sans.Outline(sans.GlyphForUnicode('B')), 500)]);
        var hmtx = Ot05Table(Ot05SansFile.Value, "hmtx");
        BinaryPrimitives.WriteUInt16BigEndian(hmtx.AsSpan(4), 700);
        var withHmtx = OpenTypeFont.Parse(Ot06OpenTypeCff(cff, ("hmtx", hmtx)));
        Assert.NotNull(withHmtx.Cff);
        Assert.Equal(500, withHmtx.Cff.AdvanceWidth(1));
        Assert.Equal(700, withHmtx.AdvanceWidth(1));
        var without = OpenTypeFont.Parse(Ot06OpenTypeCff(cff, ("hmtx", null), ("hhea", null)));
        Assert.Equal(500, without.AdvanceWidth(1));
    }

    /// <summary>
    /// OT 7 sentence 9 (reader and writers): the Name INDEX of a 'CFF ' table has one entry and the FontSet one font — a CFF with two names and
    /// two Top DICTs is read with the first name and the first Top DICT (the second has another FontMatrix); the CFF a subset or a scrub of
    /// Noto Sans CJK writes has one name.
    /// </summary>
    [Fact]
    public void Cff_OnlyTheFirstFontOfTheFontSet_WritersWriteOne()
    {
        var cff = Ot06Cff("AB");
        byte[] other = [30, 0xa0, 0x02, 0xff, 139, 139, 30, 0xa0, 0x02, 0xff, 139, 139, 12, 7];   // FontMatrix 0.002 0 0 0.002 0 0
        var two = Ot06Recompose(cff, ["Erste", "Zweite"], top => [top, [.. top, .. other]]);
        var original = OpenTypeFont.Parse(Ot06OpenTypeCff(cff));
        var font = OpenTypeFont.Parse(Ot06OpenTypeCff(two));
        Assert.NotNull(font.Cff);
        Assert.Equal("Erste", font.Cff.Name);
        Assert.Equal(original.Cff!.FontMatrix, font.Cff.FontMatrix);
        Assert.True(Ot05SameOutline(original.Outline(1), font.Outline(1)));
        Assert.False(font.Outline(1).IsEmpty);
        // the second Top DICT really differs: read alone, it gives the other matrix
        var second = Ot06Recompose(cff, ["Zweite"], top => [[.. top, .. other]]);
        Assert.Equal(0.002, CffFont.Parse(second).FontMatrix.A, 9);

        var notoCff = OpenTypeFont.Parse(Ot05NotoExcerpt.Value).TableData("CFF ").ToArray();
        var noto = OpenTypeFont.Parse(Ot05NotoExcerpt.Value);
        var subset = CffSubsetter.Subset(notoCff, "漢字A".Select(c => noto.GlyphForUnicode(c)));
        Assert.Equal(1, Ot05U16(subset, subset[2]));
        var scrubbed = OpenTypeFont.Parse(Ot05NotoScrubbed.Value).TableData("CFF ").ToArray();
        Assert.Equal(1, Ot05U16(scrubbed, scrubbed[2]));
    }

    /// <summary>
    /// OT 7 sentences 10–11 (writer): applications take the names of the name table, not of the Name INDEX — the Japanese and the Korean face of
    /// Noto Sans CJK share one 'CFF ' table, and typed text names each by its own name ID 6.
    /// </summary>
    [Fact]
    public void CjkFacesSharingOneCff_BaseNameFromTheNameTable()
    {
        var jp = OpenTypeFont.Parse(Ot05NotoCollection.Value, 0);
        var kr = OpenTypeFont.Parse(Ot05NotoCollection.Value, 1);
        Assert.Equal(jp.Table("CFF "), kr.Table("CFF "));
        var jpFace = ElementFace.For("M7-ot06-NotoSansCJK-JP", () => jp, false, false, false);
        var krFace = ElementFace.For("M7-ot06-NotoSansCJK-KR", () => kr, false, false, false);
        Assert.Equal(OpenTypeFont.PostScriptSafe(jp.PostScriptName), jpFace.BaseName);
        Assert.Equal(OpenTypeFont.PostScriptSafe(kr.PostScriptName), krFace.BaseName);
        Assert.NotEqual(jpFace.BaseName, krFace.BaseName);
        // one Name INDEX for both faces: at least one of them is not named by it
        Assert.Equal(jp.Cff!.Name, kr.Cff!.Name);
    }

    /// <summary>
    /// OT 7 sentence 12 (reader and writer): CharstringType must be 2 — a CFF whose Top DICT says 12 6 = 1: scrubbing refuses it
    /// (FontFormatException); the reader does not check the value and reads the charstrings as Type 2.
    /// </summary>
    [Fact]
    public void CffWithCharstringType1_ScrubRefuses_ReaderReadsAsType2()
    {
        var cff = Ot06Cff("AB");
        var type1 = Ot06Recompose(cff, ["M7Probe"], top => [[.. top, 140, 12, 6]]);
        Assert.Throws<FontFormatException>(() => CffScrubber.Scrub(type1, [1]));
        var read = CffFont.Parse(type1);
        Assert.True(Ot05SameOutline(CffFont.Parse(cff).Outline(1), read.Outline(1)));
        Assert.False(read.Outline(1).IsEmpty);
    }

    /// <summary>
    /// OT 7 sentence 13 (writer and reader): numGlyphs of maxp equals the charstrings — the scrubbed Noto Sans CJK has as many glyphs in maxp as
    /// its CFF has charstrings; an OpenType font with maxp 10 and 12 charstrings counts 10 glyphs and still draws glyph 11 from the CFF.
    /// </summary>
    [Fact]
    public void OpenTypeCff_MaxpGlyphCountAndCharstrings()
    {
        var scrubbed = OpenTypeFont.Parse(Ot05NotoScrubbed.Value);
        Assert.Equal(CffFont.Parse(scrubbed.TableData("CFF ").ToArray()).GlyphCount, Ot05U16(scrubbed.TableData("maxp"), 4));
        Assert.Equal(scrubbed.GlyphCount, Ot05U16(scrubbed.TableData("maxp"), 4));

        var maxp = Ot05Table(Ot05SansFile.Value, "maxp");
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4), 10);
        var font = OpenTypeFont.Parse(Ot06OpenTypeCff(Ot06Cff("ABCDEFGHIJK"), ("maxp", maxp)));
        Assert.Equal(12, font.Cff!.GlyphCount);
        Assert.Equal(10, font.GlyphCount);
        Assert.False(font.Outline(11).IsEmpty);
    }

    // ------------------------------------------------------------ OT 8 CFF2 (not read)

    /// <summary>
    /// OT 8.1 sentences 1–13, 8.2 sentences 1–14 and 29–53, 8.3.1, 8.3.2 sentences 1–37, 8.4, 8.4.1 sentences 20–30, 8.5.1 and 8.5.2 (reader,
    /// today's behaviour, question 1): CFF2 is not read — an OpenType font with a CFF2 table in place of 'CFF ' (header, TopDICT,
    /// GlobalSubrINDEX, CharStringINDEX, FontDICTINDEX, PrivateDICT): no CFF, not intact, every outline empty, the matrix from head. With a
    /// VariationStore (8.2 sentences 15–28) and with a charstring of 100 operands before one operator (8.4.1 sentences 1–19) the same.
    /// </summary>
    [Theory]
    [InlineData("einfach")]
    [InlineData("VariationStore")]
    [InlineData("100 Operanden")]
    public void Cff2Font_IsNotRead(string variant)
    {
        var options = variant switch
        {
            "VariationStore" => new Ot06Cff2Options { VariationStore = true },
            "100 Operanden" => new Ot06Cff2Options { Operands = 100 },
            _ => new Ot06Cff2Options(),
        };
        var (fvar, gvar) = Ot05VariationTables(2);
        var file = variant == "VariationStore"
            ? Ot06Cff2Font(Ot06Cff2(options), 2, 2048, ("fvar", fvar), ("gvar", gvar))
            : Ot06Cff2Font(Ot06Cff2(options), 2);
        Ot06AssertCff2NotRead(file);
    }

    /// <summary>
    /// OT 8.2 sentences 10–14 and 8.6 sentences 1–7 (reader, today's behaviour): a bare CFF2 program (header 02 00 05 …) as FontFile3 is not
    /// recognised — <see cref="FontProgram.FromBytes"/> gives null and the font is substituted.
    /// </summary>
    [Fact]
    public void Cff2Program_AloneAsFontFile3_IsNotRecognised()
    {
        var cff2 = Ot06Cff2(new Ot06Cff2Options());
        Assert.Equal(new byte[] { 2, 0, 5 }, cff2[..3]);
        Assert.Null(FontProgram.FromBytes(cff2));
    }

    /// <summary>
    /// OT 8.1 sentences 1–13 (writer, today's behaviour): a program with only CFF2 is not scrubbed — embedded as FontFile3 /OpenType in a
    /// TrueType font and one of its two letters redacted, the text goes and the program stays byte for byte.
    /// </summary>
    [Fact]
    public void Cff2Font_Embedded_RedactionLeavesTheProgramUnchanged()
    {
        var program = Ot06Cff2Font(Ot06Cff2(new Ot06Cff2Options { Glyphs = 3 }), 3);
        var pdf = TestPdf.Simple(1);
        var file = pdf.Add(new PdfStream(TestPdf.Dict(("Subtype", TestPdf.N("OpenType"))), program));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("M7Probe")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(0, -200, 1000, 900)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile3", TestPdf.Ref(file))));
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("M7Probe")),
            ("FirstChar", PdfInteger.Of(65)), ("LastChar", PdfInteger.Of(66)), ("Widths", new PdfArray([PdfInteger.Of(600), PdfInteger.Of(600)])),
            ("Encoding", TestPdf.N("WinAnsiEncoding")), ("FontDescriptor", TestPdf.Ref(descriptor))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("F1"), TestPdf.Ref(font));
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 24 Tf 72 600 Td (A) Tj 200 0 Td (B) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Equal("AB", RedactionTests.Read(doc, doc.Pages[0]).Text);
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(265, 590, 300, 630))));
        Assert.Equal("A", RedactionTests.Read(after, after.Pages[0]).Text);
        var stream = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!
            .GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile3"))!;
        Assert.Equal(program, after.GetStreamData(stream));
    }

    // ------------------------------------------------------------ OT 8.3.2 real numbers (CFF 1)

    /// <summary>
    /// OT 8.3.2 sentences 11–22 (reader, CFF 1): the nibbles of a real number — 3, c (E−), 5, f: 3E−5.
    /// </summary>
    [Fact]
    public void CffDictReal_NibbleCIsANegativeExponent()
    {
        Assert.Equal(3e-5, CffFont.Dict.Parse([30, 0x3c, 0x5f, 17]).Number(17)!.Value, 12);
    }

    /// <summary>
    /// OT 8.3.2 sentences 23–37 (reader, CFF 1): the two examples (−2.25 and 0.140541E−3) and the border cases: empty and „.“ read as 0,
    /// „.5“ as 0.5 and „2.“ as 2.
    /// </summary>
    [Fact]
    public void CffDictReal_ExamplesAndBorderCases()
    {
        static double Read(params byte[] real) => CffFont.Dict.Parse([.. real, 17]).Number(17)!.Value;
        Assert.Equal(-2.25, Read(30, 0xe2, 0xa2, 0x5f), 12);
        Assert.Equal(0.140541e-3, Read(30, 0x0a, 0x14, 0x05, 0x41, 0xc3, 0xff), 12);
        Assert.Equal(0, Read(30, 0xff));
        Assert.Equal(0, Read(30, 0xaf));
        Assert.Equal(0.5, Read(30, 0xa5, 0xff), 12);
        Assert.Equal(2, Read(30, 0x2a, 0xff), 12);
    }
}
