using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Tests.Fonts;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Konformitätsabgleich M7, package ot-18 (OpenType: EBLC — embedded bitmap location, fpgm — font program, fvar — font variations): the
/// tests the sentence-by-sentence drafts named as missing. Monochrome and grey bitmaps (EBLC/EBDT/EBSC) are not read; the colour bitmap
/// location table CBLC has the same structure and is read, so its fields are tested there. Variable fonts are drawn in their default
/// instance (user question 2 is open: these tests hold today's behaviour).
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package ot-18; the writer and the variation tables are also used by ot-19)

    /// <summary>Big-endian bytes, written in order.</summary>
    internal sealed class Ot18Writer
    {
        private readonly List<byte> _bytes = [];

        public int Length => _bytes.Count;

        public Ot18Writer U8(int v)
        {
            _bytes.Add((byte)v);
            return this;
        }

        public Ot18Writer U16(params int[] values)
        {
            foreach (var v in values)
            {
                _bytes.Add((byte)(v >> 8));
                _bytes.Add((byte)v);
            }
            return this;
        }

        public Ot18Writer U32(long v) => U16((int)(v >> 16) & 0xFFFF, (int)v & 0xFFFF);

        public Ot18Writer Fixed(double v) => U32((long)Math.Round(v * 65536) & 0xFFFFFFFF);

        public Ot18Writer F2Dot14(double v) => U16((int)Math.Round(v * 16384) & 0xFFFF);

        public Ot18Writer Tag(string tag)
        {
            foreach (var c in tag) _bytes.Add((byte)c);
            return this;
        }

        public Ot18Writer Bytes(byte[] data)
        {
            _bytes.AddRange(data);
            return this;
        }

        public Ot18Writer Pad(int multiple)
        {
            while (_bytes.Count % multiple != 0) _bytes.Add(0);
            return this;
        }

        public byte[] ToArray() => [.. _bytes];
    }

    private static readonly byte[] Ot18Sans = ShaperLimitTests.Liberation;

    private static readonly OpenTypeFont Ot18SansFont = OpenTypeFont.Parse(Ot18Sans);

    private static int Ot18G(char c) => Ot18SansFont.GlyphForUnicode(c);

    internal static byte[] Ot18With(byte[] font, params (string Tag, byte[]? Data)[] tables) =>
        ColorFontBuilder.WithTables(font, tables.ToDictionary(t => t.Tag, t => t.Data));

    internal static void Ot18SameOutline(GlyphOutline expected, GlyphOutline actual)
    {
        Assert.False(expected.IsEmpty);
        Assert.Equal(expected.Verbs, actual.Verbs);
        Assert.Equal(expected.Points, actual.Points);
    }

    /// <summary>Outline and advance of „A“, „M“ and „x“ as in Liberation Sans itself.</summary>
    internal static void Ot18SameGlyphsAsLiberation(OpenTypeFont font)
    {
        foreach (var c in "AMx")
        {
            var g = Ot18G(c);
            Assert.Equal(g, font.GlyphForUnicode(c));
            Ot18SameOutline(Ot18SansFont.Outline(g), font.Outline(g));
            Assert.Equal(Ot18SansFont.AdvanceWidth(g), font.AdvanceWidth(g));
        }
    }

    /// <summary>
    /// EBLC 2.0 with <paramref name="strikes"/> BitmapSize records, all naming one index subtable list with one index subtable (format 1, image
    /// format 1) for <paramref name="glyph"/>; EBDT 2.0 with that 8 × 8 image; EBSC 2.0 with one scaled size.
    /// </summary>
    private static (byte[] Eblc, byte[] Ebdt, byte[] Ebsc) Ot18Bitmaps(int glyph, int strikes, int bitDepth, int flags, int colorRef)
    {
        var ebdt = new Ot18Writer().U16(2, 0);
        var imageStart = ebdt.Length;
        ebdt.U8(8).U8(8).U8(0).U8(8).U8(9);                  // smallGlyphMetrics: height, width, bearingX, bearingY, advance
        var rowBytes = (8 * bitDepth + 7) / 8;
        for (var row = 0; row < 8; row++)
            for (var k = 0; k < rowBytes; k++) ebdt.U8(row % 2 == 0 ? 0xAA : 0x55);
        var imageLength = ebdt.Length - imageStart;

        var list = 8 + 48 * strikes;
        var eblc = new Ot18Writer().U16(2, 0).U32(strikes);
        for (var s = 0; s < strikes; s++)
        {
            eblc.U32(list).U32(8 + 16).U32(1).U32(colorRef);
            for (var m = 0; m < 2; m++)                            // SbitLineMetrics, horizontal and vertical
                eblc.U8(10).U8(-2 & 0xFF).U8(9).U8(1).U8(0).U8(0).U8(0).U8(1).U8(8).U8(-2 & 0xFF).U8(0).U8(0);
            eblc.U16(glyph, glyph).U8(12 + 4 * s).U8(12 + 4 * s).U8(bitDepth).U8(flags);
        }
        eblc.U16(glyph, glyph).U32(8);                              // IndexSubtableRecord: the subtable right after it
        eblc.U16(1, 1).U32(imageStart).U32(0).U32(imageLength);     // format 1, image format 1, imageDataOffset, two offsets

        var ebsc = new Ot18Writer().U16(2, 0).U32(1);
        for (var m = 0; m < 2; m++) ebsc.U8(10).U8(-2 & 0xFF).U8(9).U8(1).U8(0).U8(0).U8(0).U8(1).U8(8).U8(-2 & 0xFF).U8(0).U8(0);
        ebsc.U8(20).U8(20).U8(12).U8(12);                           // ppemX, ppemY, substitutePpemX, substitutePpemY
        return (eblc.ToArray(), ebdt.ToArray(), ebsc.ToArray());
    }

    private static readonly Lazy<byte[]> Ot18ColorFont = new(() => ColorFontBuilder.Build(ColorFontBuilder.Kind.Cbdt, 96));

    private static readonly Lazy<(byte[] Font, List<ColorFontBuilder.Image> Images)> Ot18Hand = new(() =>
    {
        var source = EmbeddedFonts.LoadHandwriting("PatrickHand-Regular.ttf");
        return (source, ColorFontBuilder.Images(OpenTypeFont.Parse(source), 96));
    });

    private static byte[] Ot18Cblc(byte[] font) => OpenTypeFont.Parse(font).TableData("CBLC").ToArray();

    /// <summary>The colour font with its CBLC changed by <paramref name="change"/> (the BitmapSize record starts at byte 8).</summary>
    private static byte[] Ot18ColorFontWith(Action<byte[]> change)
    {
        var cblc = Ot18Cblc(Ot18ColorFont.Value);
        change(cblc);
        return Ot18With(Ot18ColorFont.Value, ("CBLC", cblc));
    }

    private const string Ot18Letters = "AMaxß7";

    /// <summary>The images of <paramref name="text"/> in both fonts: same pixels per em, positions, sizes and pixels.</summary>
    private static void Ot18SameImages(byte[] expectedFont, byte[] actualFont, string text = Ot18Letters)
    {
        var expected = OpenTypeFont.Parse(expectedFont);
        var actual = OpenTypeFont.Parse(actualFont);
        var e = ColorGlyphs.For(expected);
        var a = ColorGlyphs.For(actual);
        Assert.NotNull(e);
        Assert.NotNull(a);
        Assert.Equal(e.PixelsPerEm, a.PixelsPerEm);
        foreach (var c in text)
        {
            var g = expected.GlyphForUnicode(c);
            var x = e.Glyph(g);
            var y = a.Glyph(g);
            Assert.NotNull(x);
            Assert.NotNull(y);
            Assert.Equal((x.Value.X, x.Value.Y, x.Value.Image.Width, x.Value.Image.Height), (y.Value.X, y.Value.Y, y.Value.Image.Width, y.Value.Image.Height));
            Assert.Equal(x.Value.Image.Pixels, y.Value.Image.Pixels);
        }
    }

    /// <summary>CBDT 3.0 with the images in format 17 (small metrics) or 19 (PNG only, each padded to <paramref name="padTo"/> bytes).</summary>
    private static (byte[] Data, Dictionary<int, (int Offset, int Length)> At) Ot18CbdtData(IEnumerable<ColorFontBuilder.Image> images, int format, int padTo = 0)
    {
        var w = new Ot18Writer().U16(3, 0);
        var at = new Dictionary<int, (int, int)>();
        foreach (var i in images)
        {
            var start = w.Length;
            if (format == 17) w.U8(i.Height).U8(i.Width).U8(i.Left & 0xFF).U8((i.Bottom + i.Height) & 0xFF).U8(48);
            w.U32(i.Png.Length).Bytes(i.Png);
            while (w.Length - start < padTo) w.U8(0);
            at[i.Glyph] = (start, w.Length - start);
        }
        return (w.ToArray(), at);
    }

    /// <summary>
    /// CBLC 3.0 with one 32-bit strike at 96 ppem and the index subtables given, placed one after another behind the IndexSubtableRecords —
    /// each aligned to four bytes unless <paramref name="aligned"/> is false.
    /// </summary>
    private static byte[] Ot18CblcTable(IReadOnlyList<(int First, int Last, byte[] Sub)> subtables, bool aligned = true)
    {
        var body = new Ot18Writer();
        var offsets = new List<int>();
        foreach (var s in subtables)
        {
            offsets.Add(8 * subtables.Count + body.Length);
            body.Bytes(s.Sub);
            if (aligned) body.Pad(4);
        }
        var array = new Ot18Writer();
        for (var i = 0; i < subtables.Count; i++) array.U16(subtables[i].First, subtables[i].Last).U32(offsets[i]);
        array.Bytes(body.ToArray());
        var w = new Ot18Writer().U16(3, 0).U32(1).U32(56).U32(array.Length).U32(subtables.Count).U32(0);
        for (var m = 0; m < 2; m++) w.U8(77).U8(-19 & 0xFF).Bytes(new byte[10]);
        w.U16(subtables.Min(s => s.First), subtables.Max(s => s.Last)).U8(96).U8(96).U8(32).U8(1);
        return w.Bytes(array.ToArray()).ToArray();
    }

    private static byte[] Ot18Index1(int imageFormat, int dataOffset, IEnumerable<int> offsets)
    {
        var w = new Ot18Writer().U16(1, imageFormat).U32(dataOffset);
        foreach (var o in offsets) w.U32(o);
        return w.ToArray();
    }

    private static byte[] Ot18Index3(int imageFormat, int dataOffset, IEnumerable<int> offsets)
    {
        var w = new Ot18Writer().U16(3, imageFormat).U32(dataOffset);
        foreach (var o in offsets)
        {
            Assert.InRange(o, 0, 0xFFFF);
            w.U16(o);
        }
        return w.ToArray();
    }

    /// <summary>bigGlyphMetrics: height, width, horiBearingX, horiBearingY, horiAdvance and the vertical values (zero).</summary>
    private static Ot18Writer Ot18BigMetrics(Ot18Writer w, int bearingX, int bearingY) =>
        w.U8(60).U8(60).U8(bearingX & 0xFF).U8(bearingY & 0xFF).U8(48).U8(0).U8(0).U8(0);

    private static byte[] Ot18Index2(int dataOffset, int imageSize, int bearingX, int bearingY) =>
        Ot18BigMetrics(new Ot18Writer().U16(2, 19).U32(dataOffset).U32(imageSize), bearingX, bearingY).ToArray();

    private static byte[] Ot18Index5(int dataOffset, int imageSize, int bearingX, int bearingY, int[] glyphs)
    {
        var w = Ot18BigMetrics(new Ot18Writer().U16(5, 19).U32(dataOffset).U32(imageSize), bearingX, bearingY).U32(glyphs.Length);
        foreach (var g in glyphs) w.U16(g);
        return w.Pad(4).ToArray();
    }

    private static byte[] Ot18HandWith(byte[] cbdt, byte[] cblc) => Ot18With(Ot18Hand.Value.Font, ("CBDT", cbdt), ("CBLC", cblc));

    /// <summary>Images of glyphs <paramref name="first"/>, <paramref name="first"/> + 1, … in a row (the test letters have consecutive glyphs).</summary>
    private static List<ColorFontBuilder.Image> Ot18Run(int length)
    {
        var images = Ot18Hand.Value.Images;
        for (var i = 0; i + length <= images.Count; i++)
            if (images[i + length - 1].Glyph == images[i].Glyph + length - 1)
                return images.GetRange(i, length);
        throw new InvalidOperationException("keine Folge von Glyphen mit Bildern");
    }

    private static ColorFontBuilder.Image Ot18HandImage(char c)
    {
        var g = OpenTypeFont.Parse(Ot18Hand.Value.Font).GlyphForUnicode(c);
        return Ot18Hand.Value.Images.Single(i => i.Glyph == g);
    }

    // ---- variation tables

    /// <summary>fvar 1.0: the axes and instances given; the record sizes as given (larger than needed: padded with zeros).</summary>
    internal static byte[] Ot18Fvar(IReadOnlyList<(string Tag, double Min, double Default, double Max, int Flags, int NameId)> axes,
        IReadOnlyList<(int Subfamily, double[] Coordinates, int PostScript)> instances, bool postScriptNames = false, int axisSize = 20,
        int instanceSize = 0)
    {
        var size = instanceSize > 0 ? instanceSize : axes.Count * 4 + (postScriptNames ? 6 : 4);
        var w = new Ot18Writer().U16(1, 0, 16, 2, axes.Count, axisSize, instances.Count, size);
        foreach (var a in axes)
        {
            var start = w.Length;
            w.Tag(a.Tag).Fixed(a.Min).Fixed(a.Default).Fixed(a.Max).U16(a.Flags, a.NameId);
            while (w.Length - start < axisSize) w.U8(0);
        }
        foreach (var i in instances)
        {
            var start = w.Length;
            w.U16(i.Subfamily, 0);
            foreach (var c in i.Coordinates) w.Fixed(c);
            if (postScriptNames) w.U16(i.PostScript);
            while (w.Length - start < size) w.U8(0);
        }
        return w.ToArray();
    }

    /// <summary>One axis „wght“ 100…400…900 without instances.</summary>
    internal static byte[] Ot18WeightFvar() => Ot18Fvar([("wght", 100, 400, 900, 0, 256)], []);

    /// <summary>Number of points of a simple glyph in the glyf table.</summary>
    private static int Ot18PointCount(OpenTypeFont font, int glyph)
    {
        var head = font.TableData("head");
        var loca = font.TableData("loca");
        var glyf = font.TableData("glyf");
        var start = BinaryPrimitives.ReadInt16BigEndian(head[50..]) == 1
            ? (int)BinaryPrimitives.ReadUInt32BigEndian(loca[(glyph * 4)..])
            : BinaryPrimitives.ReadUInt16BigEndian(loca[(glyph * 2)..]) * 2;
        var contours = BinaryPrimitives.ReadInt16BigEndian(glyf[start..]);
        Assert.True(contours > 0);
        return BinaryPrimitives.ReadUInt16BigEndian(glyf[(start + 10 + (contours - 1) * 2)..]) + 1;
    }

    private static void Ot18Deltas(Ot18Writer w, int count, int value)
    {
        while (count > 0)
        {
            var run = Math.Min(64, count);
            if (value == 0) w.U8(0x80 | (run - 1));                // DELTAS_ARE_ZERO
            else
            {
                w.U8(0x40 | (run - 1));                             // DELTAS_ARE_WORDS
                for (var k = 0; k < run; k++) w.U16(value);
            }
            count -= run;
        }
    }

    /// <summary>
    /// gvar 1.0 (long offsets) with one tuple variation for <paramref name="glyph"/>: peak 1 on every axis, all points (with the phantom
    /// points) moved by <paramref name="dx"/> in x.
    /// </summary>
    internal static byte[] Ot18Gvar(OpenTypeFont font, int axisCount, int glyph, int dx)
    {
        var points = Ot18PointCount(font, glyph) + 4;
        var data = new Ot18Writer().U8(0);                          // private point numbers: all points
        Ot18Deltas(data, points, dx);
        Ot18Deltas(data, points, 0);
        var serialized = data.ToArray();
        var record = new Ot18Writer().U16(1, 4 + 4 + 2 * axisCount).U16(serialized.Length, 0x8000 | 0x2000);
        for (var a = 0; a < axisCount; a++) record.F2Dot14(1);
        var variation = record.Bytes(serialized).Pad(2).ToArray();
        var count = font.GlyphCount;
        var array = 20 + 4 * (count + 1);
        var w = new Ot18Writer().U16(1, 0, axisCount, 0).U32(array).U16(count, 1).U32(array);
        for (var g = 0; g <= count; g++) w.U32(g <= glyph ? 0 : variation.Length);
        return w.Bytes(variation).ToArray();
    }

    /// <summary>ItemVariationStore: one region (peak 1 on each axis) and one ItemVariationData with 8-bit deltas per item.</summary>
    internal static byte[] Ot18Ivs(int axisCount, int itemCount, Func<int, int> delta)
    {
        var regions = new Ot18Writer().U16(axisCount, 1);
        for (var a = 0; a < axisCount; a++) regions.F2Dot14(0).F2Dot14(1).F2Dot14(1);
        var data = new Ot18Writer().U16(itemCount, 0, 1, 0);
        for (var i = 0; i < itemCount; i++) data.U8(delta(i) & 0xFF);
        var r = regions.ToArray();
        return new Ot18Writer().U16(1).U32(12).U16(1).U32(12 + r.Length).Bytes(r).Bytes(data.ToArray()).ToArray();
    }

    /// <summary>HVAR 1.0 without mappings (glyph index = item), the advance of <paramref name="glyph"/> varied by <paramref name="delta"/>.</summary>
    internal static byte[] Ot18Hvar(OpenTypeFont font, int glyph, int delta) =>
        new Ot18Writer().U16(1, 0).U32(20).U32(0).U32(0).U32(0).Bytes(Ot18Ivs(1, font.GlyphCount, g => g == glyph ? delta : 0)).ToArray();

    /// <summary>STAT 1.1 with one design axis „wght“ and no axis values.</summary>
    internal static byte[] Ot18Stat() => new Ot18Writer().U16(1, 1, 8, 1).U32(20).U16(0).U32(0).U16(2).Tag("wght").U16(256, 0).ToArray();

    /// <summary>The name table with one more Windows Unicode record (English, United States) appended.</summary>
    private static byte[] Ot18NameWith(byte[] name, int id, string text)
    {
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(name));
        int count = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(2)), storage = BinaryPrimitives.ReadUInt16BigEndian(name.AsSpan(4));
        var value = Encoding.BigEndianUnicode.GetBytes(text);
        return new Ot18Writer().U16(0, count + 1, storage + 12).Bytes(name.AsSpan(6, count * 12).ToArray())
            .U16(3, 1, 0x409, id, value.Length, name.Length - storage).Bytes(name.AsSpan(storage).ToArray()).Bytes(value).ToArray();
    }

    // ------------------------------------------------------------ OT 14, 14.1: EBLC, EBDT, EBSC

    /// <summary>
    /// OT 14 sentences 1–8, OT 14.1 sentences 1–9, 10–24, 57–64 (EBLC part) and 73–74: Liberation Sans with EBLC 2.0 (one or two strikes,
    /// bit depth 1 or 8, colorRef 0 or 5), EBDT and EBSC for „A“ — the monochrome and grey bitmaps are not read: no glyph images, the outline
    /// and advance of „A“ as without the tables; a subset and a scrubbed font carry none of the three tables.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 1, 0)]
    [InlineData(2, 1, 1, 0)]
    [InlineData(1, 8, 1, 0)]
    [InlineData(1, 1, 1, 5)]
    public void Ot18_EblcEbdtEbsc_NotReadAndLeftOutOfSubsetAndScrub(int strikes, int bitDepth, int flags, int colorRef)
    {
        var a = Ot18G('A');
        var (eblc, ebdt, ebsc) = Ot18Bitmaps(a, strikes, bitDepth, flags, colorRef);
        var file = Ot18With(Ot18Sans, ("EBLC", eblc), ("EBDT", ebdt), ("EBSC", ebsc));
        var font = OpenTypeFont.Parse(file);
        Assert.True(font.HasTable("EBLC"));
        Assert.Null(ColorGlyphs.For(font));
        Ot18SameGlyphsAsLiberation(font);
        foreach (var result in (byte[][])[TrueTypeSubsetter.Subset(file, [0, a]), TrueTypeSubsetter.Scrub(file, [0, a])])
        {
            var parsed = OpenTypeFont.Parse(result);
            foreach (var tag in (string[])["EBLC", "EBDT", "EBSC"]) Assert.False(parsed.HasTable(tag), tag);
            Ot18SameOutline(Ot18SansFont.Outline(a), parsed.Outline(a));
        }
    }

    /// <summary>
    /// OT 14.1 sentences 1–9 (comparison in the CBLC, which has the same header): a CBLC of major version 2 (the first version) is read like
    /// one of version 3 — the same images.
    /// </summary>
    [Fact]
    public void Ot18_CblcVersion2Header_IsRead()
    {
        var version2 = Ot18ColorFontWith(cblc => BinaryPrimitives.WriteUInt16BigEndian(cblc, 2));
        Ot18SameImages(Ot18ColorFont.Value, version2);
    }

    /// <summary>
    /// OT 14.1 sentences 29–44 and 45–50: the line metrics of a strike (horizontal and vertical SbitLineMetrics, bytes 16–39 of the BitmapSize
    /// record) are not used — all zero, all 0x7F, or caretSlopeNumerator 1, caretOffset −5, minAfterBL −128: the same images at the same places.
    /// </summary>
    [Theory]
    [InlineData("zero")]
    [InlineData("7F")]
    [InlineData("caret")]
    public void Ot18_CblcLineMetrics_DoNotChangeImages(string metrics)
    {
        var changed = Ot18ColorFontWith(cblc =>
        {
            for (var m = 0; m < 2; m++)
            {
                var at = 8 + 16 + 12 * m;
                switch (metrics)
                {
                    case "zero": cblc.AsSpan(at, 12).Clear(); break;
                    case "7F": cblc.AsSpan(at, 12).Fill(0x7F); break;
                    default:
                        cblc[at + 3] = 1;                          // caretSlopeNumerator
                        cblc[at + 5] = unchecked((byte)-5);        // caretOffset
                        cblc[at + 9] = 0x80;                       // minAfterBL −128
                        break;
                }
            }
        });
        Ot18SameImages(Ot18ColorFont.Value, changed);
    }

    /// <summary>
    /// OT 14.1 sentence 56: ppemX and ppemY may differ (pixels not square) — ppemX 48 and ppemY 96: 96 pixels per em, and each image is twice as
    /// wide as with square pixels (a pixel is 1/48 em wide), its left edge twice as far from the origin, its height and bottom the same (FG 1160;
    /// before ppemY was used for both).
    /// </summary>
    [Fact]
    public void Ot18_CblcUnequalPpemXAndY_ImagesToSquarePixels()
    {
        var changed = Ot18ColorFontWith(cblc => cblc[8 + 44] = 48);
        var square = ColorGlyphs.For(OpenTypeFont.Parse(Ot18ColorFont.Value))!;
        var wide = ColorGlyphs.For(OpenTypeFont.Parse(changed))!;
        Assert.Equal(96, wide.PixelsPerEm);
        foreach (var c in Ot18Letters)
        {
            var g = OpenTypeFont.Parse(Ot18ColorFont.Value).GlyphForUnicode(c);
            var (x, y, image) = square.Glyph(g)!.Value;
            var stretched = wide.Glyph(g)!.Value;
            Assert.Equal((2 * image.Width, image.Height, 2 * x, y), (stretched.Image.Width, stretched.Image.Height, stretched.X, stretched.Y));
        }
    }

    /// <summary>OT 14.1 sentences 57–64: a strike of bit depth 1, 2, 4 or 8 (monochrome or grey) is not used — the font has no colour glyphs.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Ot18_CblcBitDepthBelow32_NotUsed(int bitDepth)
    {
        var changed = Ot18ColorFontWith(cblc => cblc[8 + 46] = (byte)bitDepth);
        Assert.NotNull(ColorGlyphs.For(OpenTypeFont.Parse(Ot18ColorFont.Value)));
        Assert.Null(ColorGlyphs.For(OpenTypeFont.Parse(changed)));
    }

    /// <summary>
    /// OT 14.1 sentences 65–72 and 86: the flags say which direction small glyph metrics describe — flags 2 (vertical only) instead of 1: the
    /// images in format 17 (small metrics) give no place in horizontal text, no glyph has an image (each shows its outline); flags 3 (both) and
    /// 0 (none named): the same images at the same places as with flags 1 (FG 1160; before flags 2 read as horizontal).
    /// </summary>
    [Fact]
    public void Ot18_CblcVerticalOnlySmallMetrics_NoImagesForHorizontalText()
    {
        var font = OpenTypeFont.Parse(Ot18ColorFontWith(cblc => cblc[8 + 47] = 2));
        var colours = ColorGlyphs.For(font);
        Assert.NotNull(colours);
        Assert.All(Ot18Letters, c => Assert.Null(colours.Glyph(font.GlyphForUnicode(c))));
        Ot18SameImages(Ot18ColorFont.Value, Ot18ColorFontWith(cblc => cblc[8 + 47] = 3));
        Ot18SameImages(Ot18ColorFont.Value, Ot18ColorFontWith(cblc => cblc[8 + 47] = 0));
    }

    /// <summary>
    /// OT 14.2 sentence 13 (reader): the IndexSubtableRecords need no order — reversed, the same images; with an additional first record whose range
    /// (the glyph of „A“ alone) overlaps a later one and names the subtable of another run, „A“ gets the image of that first record.
    /// </summary>
    [Fact]
    public void Ot18_CblcIndexSubtableRecords_NoOrderNeeded_FirstOverlapWins()
    {
        var source = Ot18Cblc(Ot18ColorFont.Value);
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(8 + 8));
        Assert.True(count >= 2);
        const int array = 56;
        var records = Enumerable.Range(0, count).Select(i => source.AsSpan(array + 8 * i, 8).ToArray()).ToList();

        var reversed = source.ToArray();
        for (var i = 0; i < count; i++) records[count - 1 - i].CopyTo(reversed, array + 8 * i);
        Ot18SameImages(Ot18ColorFont.Value, Ot18With(Ot18ColorFont.Value, ("CBLC", reversed)));

        // an extra first record for „A“ pointing to the subtable of the run that holds „x“: that subtable's first image
        var font = OpenTypeFont.Parse(Ot18ColorFont.Value);
        int a = font.GlyphForUnicode('A'), x = font.GlyphForUnicode('x');
        var other = records.Single(r => BinaryPrimitives.ReadUInt16BigEndian(r) <= x && x <= BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(2)));
        var otherFirst = BinaryPrimitives.ReadUInt16BigEndian(other);
        var w = new Ot18Writer().Bytes(source[..8]);
        var size = source.AsSpan(8, 48).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(size.AsSpan(4), BinaryPrimitives.ReadUInt32BigEndian(size.AsSpan(4)) + 8);
        BinaryPrimitives.WriteUInt32BigEndian(size.AsSpan(8), (uint)count + 1);
        w.Bytes(size).U16(a, a).U32(BinaryPrimitives.ReadUInt32BigEndian(other.AsSpan(4)) + 8);
        foreach (var r in records) w.U16(BinaryPrimitives.ReadUInt16BigEndian(r), BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(2))).U32(BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(4)) + 8);
        w.Bytes(source[(array + 8 * count)..]);
        var overlapping = OpenTypeFont.Parse(Ot18With(Ot18ColorFont.Value, ("CBLC", w.ToArray())));
        var expected = ColorGlyphs.For(font)!.Glyph(otherFirst)!.Value;
        var actual = ColorGlyphs.For(overlapping)!.Glyph(a)!.Value;
        Assert.Equal(expected.Image.Pixels, actual.Image.Pixels);
        Assert.NotEqual(ColorGlyphs.For(font)!.Glyph(a)!.Value.Image.Pixels, actual.Image.Pixels);
        Assert.NotNull(ColorGlyphs.For(overlapping)!.Glyph(x));
    }

    // ------------------------------------------------------------ OT 14.3: index subtables

    /// <summary>
    /// OT 14.3 sentence 9 and sentences 64–65 (writer): PDF-Knead writes no bitmap location tables — a scrubbed colour bitmap font (index
    /// subtables format 1 and format 4) carries neither CBLC nor CBDT.
    /// </summary>
    [Theory]
    [InlineData(ColorFontBuilder.Kind.Cbdt)]
    [InlineData(ColorFontBuilder.Kind.CbdtSparse)]
    public void Ot18_ScrubbedColorBitmapFont_HasNoCblcOrCbdt(ColorFontBuilder.Kind kind)
    {
        var file = ColorFontBuilder.Build(kind, 96);
        var font = OpenTypeFont.Parse(file);
        Assert.True(font.HasTable("CBLC"));
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(file, [0, font.GlyphForUnicode('A')]));
        Assert.False(scrubbed.HasTable("CBLC"));
        Assert.False(scrubbed.HasTable("CBDT"));
    }

    /// <summary>
    /// OT 14.3 sentences 23–29: index format 2 — one imageSize for three consecutive glyphs and one bigGlyphMetrics (horiBearingX 2,
    /// horiBearingY 40, 60 × 60) for all, image format 19 — gives the three images in the size of the metrics (5.3: each image has the size its
    /// metrics give; FG 1152) with their lower left corner at x 2 and y 40 − 60.
    /// </summary>
    [Fact]
    public void Ot18_IndexFormat2_SameSizeImagesShareTheBigMetrics()
    {
        var run = Ot18Run(3);
        var size = 4 + run.Max(i => i.Png.Length);
        var (cbdt, at) = Ot18CbdtData(run, 19, size);
        var cblc = Ot18CblcTable([(run[0].Glyph, run[2].Glyph, Ot18Index2(at[run[0].Glyph].Offset, size, 2, 40))]);
        var color = ColorGlyphs.For(OpenTypeFont.Parse(Ot18HandWith(cbdt, cblc)));
        Assert.NotNull(color);
        foreach (var image in run)
        {
            var glyph = color.Glyph(image.Glyph);
            Assert.NotNull(glyph);
            Assert.Equal((60, 60), (glyph.Value.Image.Width, glyph.Value.Image.Height));
            Assert.Equal((2, 40 - 60), (glyph.Value.X, glyph.Value.Y));
        }
        Assert.Null(color.Glyph(run[2].Glyph + 1));
    }

    /// <summary>
    /// OT 14.3 sentences 30–38: index format 3 (16-bit offsets, one more than glyphs) instead of format 1 with the same offsets — every glyph gets the
    /// same image at the same place.
    /// </summary>
    [Fact]
    public void Ot18_IndexFormat3_SameImagesAsFormat1()
    {
        var images = Ot18Hand.Value.Images;
        var (cbdt, at) = Ot18CbdtData(images, 17);
        var runs = new List<List<ColorFontBuilder.Image>>();
        foreach (var image in images)
            if (runs.Count > 0 && runs[^1][^1].Glyph == image.Glyph - 1) runs[^1].Add(image);
            else runs.Add([image]);
        byte[] Cblc(bool format3) => Ot18CblcTable([.. runs.Select(run =>
        {
            var first = at[run[0].Glyph].Offset;
            var offsets = run.Select(i => at[i.Glyph].Offset - first).Append(at[run[^1].Glyph].Offset + at[run[^1].Glyph].Length - first);
            return (run[0].Glyph, run[^1].Glyph, format3 ? Ot18Index3(17, first, offsets) : Ot18Index1(17, first, offsets));
        })]);
        var format1 = Ot18HandWith(cbdt, Cblc(false));
        var format3 = Ot18HandWith(cbdt, Cblc(true));
        Ot18SameImages(format1, format3, ColorFontBuilder.Letters.Replace(" ", ""));
    }

    /// <summary>
    /// OT 14.3 sentences 51–58: index format 5 (one imageSize, one bigGlyphMetrics, a sorted list of the glyphs of „A“, „M“ and „x“), image format
    /// 19 — the three images at the place and in the size (60 × 60, 5.3; FG 1152) the metrics give; „a“, not in the list, has none.
    /// </summary>
    [Fact]
    public void Ot18_IndexFormat5_SparseGlyphsShareTheBigMetrics()
    {
        var images = "AMx".Select(Ot18HandImage).OrderBy(i => i.Glyph).ToList();
        var size = 4 + images.Max(i => i.Png.Length);
        var (cbdt, at) = Ot18CbdtData(images, 19, size);
        int[] glyphs = [.. images.Select(i => i.Glyph)];
        var cblc = Ot18CblcTable([(glyphs[0], glyphs[^1], Ot18Index5(at[glyphs[0]].Offset, size, -3, 50, glyphs))]);
        var font = OpenTypeFont.Parse(Ot18HandWith(cbdt, cblc));
        var color = ColorGlyphs.For(font);
        Assert.NotNull(color);
        foreach (var image in images)
        {
            var glyph = color.Glyph(image.Glyph);
            Assert.NotNull(glyph);
            Assert.Equal((60, 60), (glyph.Value.Image.Width, glyph.Value.Image.Height));
            Assert.Equal((-3, 50 - 60), (glyph.Value.X, glyph.Value.Y));
        }
        Assert.Null(color.Glyph(font.GlyphForUnicode('a')));
    }

    /// <summary>
    /// OT 14.3 sentences 66–69: in index format 1 the glyph whose offset equals the next one has no image (length 0) while its neighbours have theirs;
    /// two format-3 subtables with different imageDataOffsets (the offsets of each are added to its own) are both read.
    /// </summary>
    [Fact]
    public void Ot18_ZeroLengthEntryIsMissing_EachSubtableAddsItsOwnDataOffset()
    {
        var run = Ot18Run(3);
        var (cbdt, at) = Ot18CbdtData([run[0], run[2]], 17);
        int first = at[run[0].Glyph].Offset, middle = at[run[2].Glyph].Offset - first;
        var cblc = Ot18CblcTable([(run[0].Glyph, run[2].Glyph, Ot18Index1(17, first, [0, middle, middle, middle + at[run[2].Glyph].Length]))]);
        var color = ColorGlyphs.For(OpenTypeFont.Parse(Ot18HandWith(cbdt, cblc)));
        Assert.NotNull(color);
        Assert.NotNull(color.Glyph(run[0].Glyph));
        Assert.Null(color.Glyph(run[1].Glyph));
        Assert.NotNull(color.Glyph(run[2].Glyph));
        Assert.Equal(run[2].Height, color.Glyph(run[2].Glyph)!.Value.Image.Height);

        ColorFontBuilder.Image a = Ot18HandImage('A'), x = Ot18HandImage('x');
        var (data, places) = Ot18CbdtData([a, x], 17);
        var two = Ot18CblcTable([(a.Glyph, a.Glyph, Ot18Index3(17, places[a.Glyph].Offset, [0, places[a.Glyph].Length])),
            (x.Glyph, x.Glyph, Ot18Index3(17, places[x.Glyph].Offset, [0, places[x.Glyph].Length]))]);
        var both = ColorGlyphs.For(OpenTypeFont.Parse(Ot18HandWith(data, two)));
        Assert.NotNull(both);
        Assert.Equal((a.Width, a.Height), (both.Glyph(a.Glyph)!.Value.Image.Width, both.Glyph(a.Glyph)!.Value.Image.Height));
        Assert.Equal((x.Width, x.Height), (both.Glyph(x.Glyph)!.Value.Image.Width, both.Glyph(x.Glyph)!.Value.Image.Height));
    }

    /// <summary>
    /// OT 14.3 sentences 70–72 (reader): an index subtable of format 3 with an odd number of 16-bit offsets (two glyphs, three offsets) without the
    /// padding element, the next subtable right behind it (not aligned to 32 bits) — both are read.
    /// </summary>
    [Fact]
    public void Ot18_UnpaddedFormat3Subtable_NextSubtableStillRead()
    {
        var pair = Ot18Run(2);
        var x = Ot18HandImage('x');
        Assert.DoesNotContain(pair, i => i.Glyph == x.Glyph);
        var (cbdt, at) = Ot18CbdtData([.. pair, x], 17);
        var first = at[pair[0].Glyph].Offset;
        var sub1 = Ot18Index3(17, first, [0, at[pair[1].Glyph].Offset - first, at[pair[1].Glyph].Offset + at[pair[1].Glyph].Length - first]);
        Assert.Equal(14, sub1.Length);
        var sub2 = Ot18Index3(17, at[x.Glyph].Offset, [0, at[x.Glyph].Length]);
        var cblc = Ot18CblcTable([(pair[0].Glyph, pair[1].Glyph, sub1), (x.Glyph, x.Glyph, sub2)], aligned: false);
        var color = ColorGlyphs.For(OpenTypeFont.Parse(Ot18HandWith(cbdt, cblc)));
        Assert.NotNull(color);
        foreach (var image in pair.Append(x))
            Assert.Equal((image.Width, image.Height), (color.Glyph(image.Glyph)!.Value.Image.Width, color.Glyph(image.Glyph)!.Value.Image.Height));
    }

    // ------------------------------------------------------------ OT 15: fpgm

    /// <summary>
    /// OT 15 sentences 1–4: the font program is not run (no instruction interpreter) and goes on unchanged — a subset and a scrubbed Liberation Sans
    /// carry fpgm byte for byte as in the source; the outline of „A“ is the same with and without fpgm.
    /// </summary>
    [Fact]
    public void Ot18_Fpgm_KeptByteForByte_OutlinesDoNotDependOnIt()
    {
        var a = Ot18G('A');
        var fpgm = Ot18SansFont.TableData("fpgm").ToArray();
        Assert.NotEmpty(fpgm);
        Assert.Equal(fpgm, OpenTypeFont.Parse(TrueTypeSubsetter.Subset(Ot18Sans, [0, a])).TableData("fpgm").ToArray());
        Assert.Equal(fpgm, OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(Ot18Sans, [0, a])).TableData("fpgm").ToArray());
        var without = OpenTypeFont.Parse(Ot18With(Ot18Sans, ("fpgm", null)));
        Ot18SameOutline(Ot18SansFont.Outline(a), without.Outline(a));
    }

    /// <summary>
    /// OT 15 sentences 5–7: the table is optional — Liberation Sans without fpgm is intact, and its subset has no fpgm (with fpgm: see
    /// <see cref="Ot18_Fpgm_KeptByteForByte_OutlinesDoNotDependOnIt"/>).
    /// </summary>
    [Fact]
    public void Ot18_FontWithoutFpgm_IsIntact_SubsetHasNone()
    {
        var file = Ot18With(Ot18Sans, ("fpgm", null));
        var font = OpenTypeFont.Parse(file);
        Assert.True(font.Intact);
        Assert.False(font.Broken);
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(file, [0, Ot18G('A')])).HasTable("fpgm"));
        Assert.True(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(Ot18Sans, [0, Ot18G('A')])).HasTable("fpgm"));
    }

    // ------------------------------------------------------------ OT 16: fvar

    /// <summary>
    /// OT 16 sentences 1–13, OT 16.1.1 sentences 22–25, OT 16.1.2 sentences 23–24, OT 16.2 sentences 1 and 3: Liberation Sans with fvar (axis wght
    /// 100–400–900, or no axis at all) and gvar deltas for „A“ (and an HVAR that widens „A“) is drawn in its default instance — the outline of glyf
    /// and the advance of hmtx, as without the tables; with HVAR also in the subset.
    /// </summary>
    [Theory]
    [InlineData("wght")]
    [InlineData("noAxes")]
    [InlineData("hvar")]
    public void Ot18_VariableFont_DefaultInstanceOutlinesAndAdvances(string variant)
    {
        var a = Ot18G('A');
        var axes = variant == "noAxes" ? 0 : 1;
        byte[] fvar = axes == 0 ? Ot18Fvar([], []) : Ot18WeightFvar();
        var file = Ot18With(Ot18Sans, ("fvar", fvar), ("gvar", Ot18Gvar(Ot18SansFont, axes, a, 80)),
            ("HVAR", variant == "hvar" ? Ot18Hvar(Ot18SansFont, a, 60) : null));
        var font = OpenTypeFont.Parse(file);
        Assert.True(font.HasTable("gvar"));
        Ot18SameGlyphsAsLiberation(font);
        if (variant != "hvar") return;
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(file, [0, a]));
        Ot18SameOutline(Ot18SansFont.Outline(a), subset.Outline(a));
        Assert.Equal(Ot18SansFont.AdvanceWidth(a), subset.AdvanceWidth(a));
    }

    /// <summary>
    /// OT 16.1.1 sentences 1–17 and 18–21, OT 16.1.2 sentences 1–18, 19–22 and 36–38, OT 16.1.3 sentences 1–15, 16–25 and 26–28: fvar is not read —
    /// two axes and four instances, records larger than needed (axisSize 24, instanceSize 18), an axis wght 300–400–700, an invalid axis tag,
    /// an axis name ID 300 with its name record, instances with PostScript name IDs, two instances with the same coordinates and subfamily
    /// name ID 5, no instances: each parses without error, with the family name and the default instance's outlines and advances.
    /// </summary>
    [Theory]
    [InlineData("twoAxesFourInstances")]
    [InlineData("largerRecords")]
    [InlineData("wght300to700")]
    [InlineData("invalidTag")]
    [InlineData("axisName300")]
    [InlineData("postScriptNames")]
    [InlineData("duplicateInstances")]
    [InlineData("noInstances")]
    public void Ot18_FvarVariants_ParseAsTheDefaultInstance(string variant)
    {
        var a = Ot18G('A');
        (string, double, double, double, int, int) wght = ("wght", 100, 400, 900, 0, 256);
        var fvar = variant switch
        {
            "twoAxesFourInstances" => Ot18Fvar([wght, ("wdth", 75, 100, 125, 0, 257)],
                [(258, [100, 100], 0), (259, [700, 100], 0), (260, [400, 75], 0), (261, [900, 125], 0)]),
            "largerRecords" => Ot18Fvar([wght], [(258, [100], 0), (259, [700], 0)], axisSize: 24, instanceSize: 18),
            "wght300to700" => Ot18Fvar([("wght", 300, 400, 700, 0, 256)], [(258, [300], 0)]),
            "invalidTag" => Ot18Fvar([(" wgh", 100, 400, 900, 0, 256)], []),
            "axisName300" => Ot18Fvar([("wght", 100, 400, 900, 0, 300)], []),
            "postScriptNames" => Ot18Fvar([wght], [(2, [400], 6), (258, [100], 262), (259, [700], 0xFFFF), (260, [900], 263)], postScriptNames: true),
            "duplicateInstances" => Ot18Fvar([wght], [(5, [700], 0), (5, [700], 0)]),
            _ => Ot18Fvar([wght], []),
        };
        var name = variant == "axisName300" ? Ot18NameWith(Ot18SansFont.TableData("name").ToArray(), 300, "Strichstaerke") : null;
        var file = Ot18With(Ot18Sans, ("fvar", fvar), ("gvar", Ot18Gvar(Ot18SansFont, variant == "twoAxesFourInstances" ? 2 : 1, a, 80)),
            ("name", name ?? Ot18SansFont.TableData("name").ToArray()));
        var font = OpenTypeFont.Parse(file);
        Assert.False(font.Broken);
        Assert.Equal(Ot18SansFont.FamilyName, font.FamilyName);
        Assert.Equal(Ot18SansFont.PostScriptName, font.PostScriptName);
        Ot18SameGlyphsAsLiberation(font);
    }

    /// <summary>
    /// OT 16 sentences 14–16, OT 16.1.3 sentences 16–25 (writer part) and 29–30 (today's behaviour, user question 2): PDF-Knead builds no variable
    /// fonts — a subset of a font with fvar, gvar, HVAR and STAT is static (none of them); a scrubbed one keeps all four unchanged.
    /// </summary>
    [Fact]
    public void Ot18_SubsetDropsVariationTables_ScrubKeepsThem()
    {
        var a = Ot18G('A');
        (string Tag, byte[] Data)[] tables = [("fvar", Ot18Fvar([("wght", 100, 400, 900, 0, 256)], [(5, [700], 0), (5, [700], 0)])),
            ("gvar", Ot18Gvar(Ot18SansFont, 1, a, 80)), ("HVAR", Ot18Hvar(Ot18SansFont, a, 60)), ("STAT", Ot18Stat())];
        var file = Ot18With(Ot18Sans, [.. tables.Select(t => (t.Tag, (byte[]?)t.Data))]);
        var subset = OpenTypeFont.Parse(TrueTypeSubsetter.Subset(file, [0, a]));
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(file, [0, a]));
        foreach (var (tag, data) in tables)
        {
            Assert.False(subset.HasTable(tag), tag);
            Assert.Equal(data, scrubbed.TableData(tag).ToArray());
        }
    }

    private const string Ot18Bahnschrift = @"C:\Windows\Fonts\bahnschrift.ttf";

    /// <summary>
    /// OT 16.1.2 sentences 25–33 and 34–35, OT 16.1.3 sentences 26–28 (installed font; today's behaviour, user question 2): an installed variable font
    /// (Bahnschrift on Windows, fvar with named instances) is offered once, as its family in the default instance — no axes and no named instances
    /// in the font list.
    /// </summary>
    [Fact]
    public void Ot18_InstalledVariableFont_OneFamilyEntry()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Ot18Bahnschrift)) return;
        var installed = OpenTypeFont.Parse(File.ReadAllBytes(Ot18Bahnschrift));
        var fvar = installed.TableData("fvar");
        Assert.True(fvar.Length > 16);
        Assert.True(BinaryPrimitives.ReadUInt16BigEndian(fvar[12..]) >= 4, "benannte Instanzen");
        var entries = SystemFonts.Families().Where(f => f.StartsWith("Bahnschrift", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(["Bahnschrift"], entries);
    }

    /// <summary>
    /// OT 16.2 sentence 2 (today's behaviour, user question 2): a font that is not embedded, „Bahnschrift-Bold“, finds the file of the installed
    /// variable Bahnschrift — the same font as „Bahnschrift“, its default instance (weight 400), not the named instance „Bold“.
    /// </summary>
    [Fact]
    public void Ot18_InstalledVariableFont_NamedInstanceFindsTheDefaultInstance()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Ot18Bahnschrift)) return;
        var bold = SystemFonts.Find("Bahnschrift-Bold", false, false);
        var plain = SystemFonts.Find("Bahnschrift", false, false);
        Assert.NotNull(bold);
        Assert.Same(plain, bold);
        Assert.Equal(400, bold.WeightClass);
        Assert.True(bold.HasTable("fvar"));
    }

    /// <summary>
    /// OT 14 sentences 1–8 and 14.1 sentences 1–24, 57–64 (reader, special rule, measured): an embedded TrueType font whose glyphs exist only as
    /// EBDT bitmaps (bit depth 1) — without glyf and loca, or with all glyphs empty — draws nothing, as two of three reference readers do (the
    /// third draws the bitmaps only without glyf; probes e1–e4, FG 1160); its text is read all the same.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ot18_BitmapOnlyEmbeddedFont_DrawsNothing_TextRead(bool emptyGlyf)
    {
        var a = Ot18G('A');
        var (eblc, ebdt, _) = Ot18Bitmaps(a, 1, 1, 1, 0);
        var emptyLoca = new byte[(Ot18SansFont.GlyphCount + 1) * (Ot18SansFont.LongLoca ? 4 : 2)];
        var file = Ot18With(Ot18Sans, ("glyf", emptyGlyf ? new byte[4] : null), ("loca", emptyGlyf ? emptyLoca : null), ("EBLC", eblc), ("EBDT", ebdt));
        var (doc, list, _) = Iso03PageFont("BT /F1 72 Tf 10 20 Td (AAA) Tj ET", pdf =>
        {
            var program = pdf.Add(TestPdf.FlateStream(file, TestPdf.Dict(("Length1", Core.Pdf.Objects.PdfInteger.Of(file.Length)))));
            var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ProbeBitmap")), ("Flags", Core.Pdf.Objects.PdfInteger.Of(32)),
                ("FontBBox", TestPdf.Box(0, -200, 1000, 800)), ("ItalicAngle", Core.Pdf.Objects.PdfInteger.Of(0)), ("Ascent", Core.Pdf.Objects.PdfInteger.Of(800)),
                ("Descent", Core.Pdf.Objects.PdfInteger.Of(-200)), ("CapHeight", Core.Pdf.Objects.PdfInteger.Of(700)), ("StemV", Core.Pdf.Objects.PdfInteger.Of(80)),
                ("FontFile2", TestPdf.Ref(program)));
            return TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("ProbeBitmap")),
                ("Encoding", TestPdf.N("WinAnsiEncoding")), ("FontDescriptor", descriptor));
        });
        using (doc)
        {
            Assert.Equal("AAA", TextIso01(list));
            using var bitmap = RenderIso01(doc, list);
            var ink = 0;
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                    if (bitmap.GetPixel(x, y) is var p && Math.Min(p.Red, Math.Min(p.Green, p.Blue)) < 200) ink++;
            Assert.Equal(0, ink);
        }
    }
}
