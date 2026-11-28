using System.Diagnostics;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Tests.Fonts;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Konformitätsabgleich M7, OpenType 13 (EBDT) and 14 (EBLC): monochrome and grey glyph bitmaps as the fallback for an embedded font without
/// outlines (neither glyf with loca nor CFF/CFF2) — drawn from the strike nearest to the shown size in the fill colour, grey levels as opacity —
/// and the redaction of fonts whose glyphs exist only as images (EBDT, sbix, CBDT), which keeps no image of a removed glyph.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (EBLC/EBDT probes)

    /// <summary>One index subtable of a strike: its index and image format and the image records (for index formats 2 and 5 all of one size).</summary>
    private sealed record EbSub(int IndexFormat, int ImageFormat, (int Glyph, byte[] Data)[] Images, byte[]? BigMetrics = null);

    /// <summary>One strike: ppemY, bit depth, the index subtables; ppemX (0 = ppemY) and the flags of the BitmapSize record.</summary>
    private sealed record EbStrike(int Ppem, int BitDepth, EbSub[] Subs, int PpemX = 0, int Flags = 1);

    /// <summary>The probe shape: 5 × 3 pixels (a width that is no multiple of 8, so byte and bit aligned data differ).</summary>
    private static readonly int[][] EbPattern = [[1, 1, 1, 1, 1], [1, 0, 0, 0, 0], [1, 1, 1, 0, 0]];

    /// <summary>Pixels of rows, <paramref name="depth"/> bits each, the most significant first; each row padded to a byte unless bit aligned.</summary>
    private static byte[] EbBits(int[][] rows, int depth, bool bitAligned)
    {
        var bytes = new List<byte>();
        int acc = 0, n = 0;
        void Flush()
        {
            if (n == 0) return;
            bytes.Add((byte)(acc << (8 - n)));
            (acc, n) = (0, 0);
        }
        foreach (var row in rows)
        {
            foreach (var v in row)
                for (var b = depth - 1; b >= 0; b--)
                {
                    acc = (acc << 1) | ((v >> b) & 1);
                    if (++n == 8)
                    {
                        bytes.Add((byte)acc);
                        (acc, n) = (0, 0);
                    }
                }
            if (!bitAligned) Flush();
        }
        Flush();
        return [.. bytes];
    }

    private static byte[] EbSmall(int height, int width, int bearingX, int bearingY) => [(byte)height, (byte)width, (byte)bearingX, (byte)bearingY, (byte)(width + 1)];

    private static byte[] EbBig(int height, int width, int bearingX, int bearingY) =>
        [(byte)height, (byte)width, (byte)bearingX, (byte)bearingY, (byte)(width + 1), 0, 0, (byte)(height + 1)];

    /// <summary>An image record of <paramref name="rows"/> in image format 1, 2, 5 (data only), 6 or 7.</summary>
    private static byte[] EbImage(int imageFormat, int[][] rows, int depth = 1, int bearingX = 1, int bearingY = 5)
    {
        var (h, w) = (rows.Length, rows[0].Length);
        return imageFormat switch
        {
            1 => [.. EbSmall(h, w, bearingX, bearingY), .. EbBits(rows, depth, false)],
            2 => [.. EbSmall(h, w, bearingX, bearingY), .. EbBits(rows, depth, true)],
            5 => EbBits(rows, depth, true),
            6 => [.. EbBig(h, w, bearingX, bearingY), .. EbBits(rows, depth, false)],
            7 => [.. EbBig(h, w, bearingX, bearingY), .. EbBits(rows, depth, true)],
            _ => throw new ArgumentOutOfRangeException(nameof(imageFormat)),
        };
    }

    /// <summary>A composite image record (format 8 with small, 9 with big metrics) of the components (glyph, xOffset, yOffset).</summary>
    private static byte[] EbComposite(int imageFormat, int height, int width, int bearingX, int bearingY, params (int Glyph, int X, int Y)[] parts)
    {
        var w = new Ot18Writer();
        if (imageFormat == 8) w.Bytes(EbSmall(height, width, bearingX, bearingY)).U8(0);
        else w.Bytes(EbBig(height, width, bearingX, bearingY));
        w.U16(parts.Length);
        foreach (var (glyph, x, y) in parts) w.U16(glyph).U8(x & 0xFF).U8(y & 0xFF);
        return w.ToArray();
    }

    /// <summary>EBLC 2.0 and EBDT 2.0 with the strikes given (the index subtable list of each strike right after the BitmapSize records).</summary>
    private static (byte[] Eblc, byte[] Ebdt) EbTables(params EbStrike[] strikes)
    {
        var ebdt = new Ot18Writer().U16(2, 0);
        var lists = new List<byte[]>();
        foreach (var strike in strikes)
        {
            var array = new Ot18Writer();
            var subtables = new Ot18Writer();
            foreach (var sub in strike.Subs)
            {
                var images = sub.Images.OrderBy(i => i.Glyph).ToArray();
                int first = images[0].Glyph, last = images[^1].Glyph;
                array.U16(first, last).U32(strike.Subs.Length * 8 + subtables.Length);
                var imageDataOffset = ebdt.Length;
                var starts = new List<int>();
                foreach (var (_, data) in images)
                {
                    starts.Add(ebdt.Length - imageDataOffset);
                    ebdt.Bytes(data);
                }
                var end = ebdt.Length - imageDataOffset;
                int Offset(int g)   // a glyph without image: the offset of the next one, so its length is 0
                {
                    for (var k = 0; k < images.Length; k++)
                        if (images[k].Glyph >= g) return starts[k];
                    return end;
                }
                subtables.U16(sub.IndexFormat, sub.ImageFormat).U32(imageDataOffset);
                switch (sub.IndexFormat)
                {
                    case 1:
                        for (var g = first; g <= last + 1; g++) subtables.U32(g > last ? end : Offset(g));
                        break;
                    case 3:
                        for (var g = first; g <= last + 1; g++) subtables.U16(g > last ? end : Offset(g));
                        subtables.Pad(4);
                        break;
                    case 2:
                        subtables.U32(images[0].Data.Length).Bytes(sub.BigMetrics!);
                        break;
                    case 4:
                        subtables.U32(images.Length);
                        for (var k = 0; k < images.Length; k++) subtables.U16(images[k].Glyph, starts[k]);
                        subtables.U16(0, end);
                        break;
                    case 5:
                        subtables.U32(images[0].Data.Length).Bytes(sub.BigMetrics!).U32(images.Length);
                        foreach (var (glyph, _) in images) subtables.U16(glyph);
                        subtables.Pad(4);
                        break;
                }
            }
            lists.Add([.. array.ToArray(), .. subtables.ToArray()]);
        }
        var eblc = new Ot18Writer().U16(2, 0).U32(strikes.Length);
        var listAt = 8 + 48 * strikes.Length;
        for (var s = 0; s < strikes.Length; s++)
        {
            var strike = strikes[s];
            var glyphs = strike.Subs.SelectMany(sub => sub.Images.Select(i => i.Glyph)).ToArray();
            eblc.U32(listAt).U32(lists[s].Length).U32(strike.Subs.Length).U32(0);
            for (var m = 0; m < 2; m++) eblc.U8(strike.Ppem).U8(0xFE).U8(strike.Ppem).U8(1).U8(0).U8(0).U8(0).U8(1).U8(strike.Ppem).U8(0xFE).U8(0).U8(0);
            eblc.U16(glyphs.Min(), glyphs.Max()).U8(strike.PpemX == 0 ? strike.Ppem : strike.PpemX).U8(strike.Ppem).U8(strike.BitDepth).U8(strike.Flags);
            listAt += lists[s].Length;
        }
        foreach (var list in lists) eblc.Bytes(list);
        return (eblc.ToArray(), ebdt.ToArray());
    }

    /// <summary>Liberation Sans without glyf and loca (no outlines), with the EBLC and EBDT of the strikes given.</summary>
    private static byte[] EbFont(params EbStrike[] strikes)
    {
        var (eblc, ebdt) = EbTables(strikes);
        return Ot18With(Ot18Sans, ("glyf", null), ("loca", null), ("EBLC", eblc), ("EBDT", ebdt));
    }

    /// <summary>A font with one strike of 10 ppem holding „A“ in one index subtable.</summary>
    private static byte[] EbFontA(int imageFormat, int indexFormat, byte[] data, int depth = 1, int flags = 1) =>
        EbFont(new EbStrike(10, depth, [new EbSub(indexFormat, imageFormat, [(Ot18G('A'), data)], indexFormat is 2 or 5 ? EbBig(3, 5, 1, 5) : null)], Flags: flags));

    /// <summary>A simple TrueType font (WinAnsiEncoding) with <paramref name="program"/> embedded as FontFile2.</summary>
    private static PdfDictionary EbSimpleFont(TestPdf pdf, byte[] program, params (string Key, PdfObject Value)[] extra)
    {
        var file = pdf.Add(TestPdf.FlateStream(program, TestPdf.Dict(("Length1", PdfInteger.Of(program.Length)))));
        var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ProbeBitmap")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(0, -200, 1000, 800)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(800)),
            ("Descent", PdfInteger.Of(-200)), ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file)));
        var font = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("ProbeBitmap")),
            ("Encoding", TestPdf.N("WinAnsiEncoding")), ("FontDescriptor", descriptor));
        foreach (var (key, value) in extra) font.Set(PdfName.Get(key), value);
        return font;
    }

    /// <summary>Renders <paramref name="content"/> on a 200 × 200 page whose /F1 embeds <paramref name="program"/>; the text read and the bitmap.</summary>
    private static (string Text, SKBitmap Bitmap) EbRender(byte[] program, string content, float scale = 1)
    {
        var (doc, list, _) = Iso03PageFont(content, pdf => EbSimpleFont(pdf, program));
        using (doc) return (TextIso01(list), RenderIso01(doc, list, scale));
    }

    private static int EbGrey(SKBitmap b, double x, double y, float scale = 1) => PixelIso01(b, x, y, scale) is var p ? Math.Min(p.Red, Math.Min(p.Green, p.Blue)) : 255;

    private static int EbInk(SKBitmap b)
    {
        var ink = 0;
        for (var y = 0; y < b.Height; y++)
            for (var x = 0; x < b.Width; x++)
                if (b.GetPixel(x, y) is var p && Math.Min(p.Red, Math.Min(p.Green, p.Blue)) < 200) ink++;
        return ink;
    }

    /// <summary>
    /// The probe shape drawn at „BT /F1 60 Tf 20 100 Td“ from a strike of 10 ppem (one bitmap pixel 6 pt, bearingX 1, bearingY 5): every pixel
    /// of <paramref name="rows"/> dark where set and light where not, and nothing left of and above the shape.
    /// </summary>
    private static void EbAssertPattern(SKBitmap b, int[][] rows, double originX = 20, double baseline = 100)
    {
        for (var row = 0; row < rows.Length; row++)
            for (var col = 0; col < rows[row].Length; col++)
            {
                var (x, y) = (originX + (1 + col + 0.5) * 6, baseline + (5 - row - 0.5) * 6);
                if (rows[row][col] != 0) Assert.True(EbGrey(b, x, y) < 60, $"Pixel {col},{row} sollte gefüllt sein ({EbGrey(b, x, y)})");
                else Assert.True(EbGrey(b, x, y) > 200, $"Pixel {col},{row} sollte leer sein ({EbGrey(b, x, y)})");
            }
        Assert.True(EbGrey(b, originX + 0.5 * 6, baseline + 4.5 * 6) > 200, "links daneben");
        Assert.True(EbGrey(b, originX + 1.5 * 6, baseline + 5.5 * 6) > 200, "darüber");
    }

    // ------------------------------------------------------------ drawing

    /// <summary>
    /// OT 13.2.1, 13.2.2, 13.2.5, 13.2.6, 13.2.7, 13.2.8, 13.2.9 (image formats) with OT 14.3 (index formats 1–5) — reader, fallback without
    /// outlines (FG 1169): the same 5 × 3 shape as byte aligned data with small metrics (1), bit aligned (2), bit aligned with the big metrics
    /// of the index subtable (5), with big metrics byte and bit aligned (6, 7), and as composites of two components with small and big metrics
    /// (8, 9) — each drawn at the same place, pixel for pixel, at bearingX 1 and bearingY 5 from the origin.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 3)]
    [InlineData(2, 1)]
    [InlineData(2, 4)]
    [InlineData(5, 2)]
    [InlineData(5, 5)]
    [InlineData(6, 4)]
    [InlineData(6, 1)]
    [InlineData(7, 3)]
    [InlineData(8, 1)]
    [InlineData(9, 1)]
    public void Ot17_EbdtImageFormats_BitmapOnlyFont_DrawTheSameShape(int imageFormat, int indexFormat)
    {
        var a = Ot18G('A');
        byte[] font;
        if (imageFormat is 8 or 9)
        {
            // „A“ = the top row (glyph of „O“) and the two lower rows (glyph of „I“) at yOffset 1
            int top = Ot18G('O'), bottom = Ot18G('I');
            font = EbFont(new EbStrike(10, 1, [new EbSub(1, imageFormat, [(a, EbComposite(imageFormat, 3, 5, 1, 5, (top, 0, 0), (bottom, 0, 1)))]),
                new EbSub(1, 1, [(top, EbImage(1, [EbPattern[0]], bearingX: 7, bearingY: -3))]), new EbSub(1, 2, [(bottom, EbImage(2, EbPattern[1..]))])]));
        }
        else font = EbFontA(imageFormat, indexFormat, EbImage(imageFormat, EbPattern));
        var (text, bitmap) = EbRender(font, "BT /F1 60 Tf 20 100 Td (A) Tj ET");
        using (bitmap)
        {
            Assert.Equal("A", text);
            EbAssertPattern(bitmap, EbPattern);
        }
    }

    /// <summary>
    /// OT 14.1 sentences 57–64 and OT 13.2 sentences 1–5 (reader, FG 1169): bit depths 2, 4 and 8 are grey levels — a pixel at the highest
    /// level is filled fully, one in between with its share of the opacity in the fill colour (black on white: 255 · (1 − level / highest), bit
    /// depth 8 within 16 levels).
    /// </summary>
    [Theory]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 8)]
    [InlineData(8, 128)]
    [InlineData(8, 40)]
    public void Ot17_EbdtBitDepth_GreyLevelsAsOpacity(int depth, int level)
    {
        var max = (1 << depth) - 1;
        var (_, bitmap) = EbRender(EbFontA(1, 1, EbImage(1, [[max, level, 0]], depth, bearingX: 1, bearingY: 5), depth), "BT /F1 60 Tf 20 100 Td (A) Tj ET");
        using (bitmap)
        {
            Assert.True(EbGrey(bitmap, 20 + 1.5 * 6, 100 + 4.5 * 6) < 30, "volle Stufe");
            var expected = 255 * (1 - (double)level / max);
            var grey = EbGrey(bitmap, 20 + 2.5 * 6, 100 + 4.5 * 6);
            Assert.True(Math.Abs(grey - expected) <= 12, $"Stufe {level}/{max}: {grey}, erwartet {expected:0}");
            Assert.True(EbGrey(bitmap, 20 + 3.5 * 6, 100 + 4.5 * 6) > 245, "Stufe 0");
        }
    }

    /// <summary>
    /// OT 14.1 sentences 51–55 (ppem = the point size at 72 dpi) and OT 14 sentences 1–8 (reader, FG 1169): of two strikes (10 and 20 ppem, each
    /// „A“ a square of 2 × 2 pixels at the origin) the one nearest to the shown size draws — at 10 pt and at 20 pt both squares are 2 pt wide
    /// (from the other strike they would be 1 and 4 pt), at 12 pt from 10 ppem (2.4 pt), at 18 pt from 20 ppem (1.8 pt); „B“, only in the
    /// 20-ppem strike, is drawn from it at 10 pt as well (1 pt).
    /// </summary>
    [Theory]
    [InlineData(10, 'A', 2.0)]
    [InlineData(20, 'A', 2.0)]
    [InlineData(12, 'A', 2.4)]
    [InlineData(18, 'A', 1.8)]
    [InlineData(10, 'B', 1.0)]
    public void Ot18_EblcStrikes_TheOneNearestToTheShownSizeDraws(int size, char letter, double expectedWidth)
    {
        int a = Ot18G('A'), b = Ot18G('B');
        int[][] square = [[1, 1], [1, 1]];
        var font = EbFont(
            new EbStrike(20, 1, [new EbSub(1, 1, [(a, EbImage(1, square, bearingX: 0, bearingY: 2)), (b, EbImage(1, square, bearingX: 0, bearingY: 2))])]),
            new EbStrike(10, 1, [new EbSub(1, 1, [(a, EbImage(1, square, bearingX: 0, bearingY: 2))])]));
        const float scale = 10;
        var (_, bitmap) = EbRender(font, $"BT /F1 {size} Tf 50 50 Td ({letter}) Tj ET", scale);
        using (bitmap)
        {
            var row = bitmap.Height - 1 - (int)((50 + expectedWidth / 2) * scale);
            var dark = Enumerable.Range(0, bitmap.Width).Count(x => bitmap.GetPixel(x, row) is var p && p.Red < 128);
            Assert.True(Math.Abs(dark / scale - expectedWidth) <= 0.25, $"Breite {dark / scale} pt, erwartet {expectedWidth}");
        }
    }

    /// <summary>
    /// OT 13 sentences 1–8 (reader, FG 1169): a bitmap glyph is drawn like an outline in the text state — in the fill colour (1 0 0 rg: red),
    /// nothing in mode 3 (invisible) while its text is read, and through a CIDFontType2 (Identity) as through a simple font, in vertical writing
    /// moved by the position vector as any glyph.
    /// </summary>
    [Fact]
    public void Ot17_BitmapGlyph_FillColourInvisibleModeAndCidFont()
    {
        var font = EbFontA(1, 1, EbImage(1, EbPattern));
        var (_, red) = EbRender(font, "BT /F1 60 Tf 1 0 0 rg 20 100 Td (A) Tj ET");
        using (red)
        {
            var p = PixelIso01(red, 20 + 1.5 * 6, 100 + 4.5 * 6);
            Assert.True(p.Red > 200 && p.Green < 60 && p.Blue < 60, p.ToString());
        }
        var (text, hidden) = EbRender(font, "BT /F1 60 Tf 3 Tr 20 100 Td (A) Tj ET");
        using (hidden)
        {
            Assert.Equal("A", text);
            Assert.Equal(0, EbInk(hidden));
        }
        // composite font, horizontal at 20 100 and vertical (Identity-V, /DW 1000, default /DW2 [880 −1000]) at 100 150: there the origin moves
        // by the position vector (−w0/2, −0.88 em) = (−30, −52.8)
        var a = Ot18G('A');
        foreach (var (encoding, x, y, originX, baseline) in new[] { ("Identity-H", 20, 100, 20.0, 100.0), ("Identity-V", 100, 150, 70.0, 97.2) })
        {
            var (doc, list, _) = Iso03PageFont($"BT /F1 60 Tf {x} {y} Td <{a:X4}> Tj ET", pdf =>
            {
                var program = pdf.Add(TestPdf.FlateStream(font, TestPdf.Dict(("Length1", PdfInteger.Of(font.Length)))));
                var descriptor = TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("ProbeBitmap")), ("Flags", PdfInteger.Of(32)),
                    ("FontBBox", TestPdf.Box(0, -200, 1000, 800)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(800)), ("Descent", PdfInteger.Of(-200)),
                    ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(program)));
                var cid = TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType2")), ("BaseFont", TestPdf.N("ProbeBitmap")),
                    ("CIDSystemInfo", Iso03Collection("Adobe", "Identity", 0)), ("FontDescriptor", descriptor), ("CIDToGIDMap", TestPdf.N("Identity")),
                    ("DW", PdfInteger.Of(1000)));
                return TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("ProbeBitmap")),
                    ("Encoding", TestPdf.N(encoding)), ("DescendantFonts", new PdfArray([cid])));
            });
            using (doc)
            using (var bitmap = RenderIso01(doc, list))
                EbAssertPattern(bitmap, EbPattern, originX, baseline);
        }
    }

    /// <summary>
    /// OT 14.1 sentences 65–72, 86 (reader, FG 1169): small metrics of a strike whose flags name them vertical only (0x02) give no place in
    /// horizontal text — no image from it, as in the colour bitmaps; with flags 0x01 the same image is drawn.
    /// </summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void Ot18_EblcVerticalSmallMetrics_NoImageInHorizontalText(int flags, bool drawn)
    {
        var (_, bitmap) = EbRender(EbFontA(1, 1, EbImage(1, EbPattern), flags: flags), "BT /F1 60 Tf 20 100 Td (A) Tj ET");
        using (bitmap) Assert.Equal(drawn, EbInk(bitmap) > 0);
    }

    /// <summary>
    /// OT 13.1 sentences 33–43, OT 13.2.8, 13.2.9 and OT 14.1 sentences 10–24 (security, FG 1169): prepared sizes stay bounded — metrics of
    /// 255 × 255 with ten bytes of data, a composite naming itself, composites fanning out 255 × 255 × 255 × 255, numSizes and
    /// numberOfIndexSubtables of 2³² − 1, offsets beyond the tables: no exception, the page within seconds, nothing drawn from what is damaged
    /// (the self-referring composite draws its other component once, as without the loop).
    /// </summary>
    [Theory]
    [InlineData("truncated")]
    [InlineData("self")]
    [InlineData("fan")]
    [InlineData("counts")]
    [InlineData("outside")]
    public void Ot17_EbdtPreparedSizes_StayBounded(string damage)
    {
        int a = Ot18G('A'), b = Ot18G('B'), c = Ot18G('C'), d = Ot18G('D'), o = Ot18G('O');
        var font = damage switch
        {
            "truncated" => EbFontA(1, 1, [255, 255, 0, 5, 9, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10]),
            "self" => EbFont(new EbStrike(10, 1, [new EbSub(1, 8, [(a, EbComposite(8, 3, 5, 1, 5, (a, 0, 0), (o, 0, 0)))]),
                new EbSub(1, 1, [(o, EbImage(1, EbPattern))])])),
            "fan" => EbFont(new EbStrike(10, 1, [
                new EbSub(1, 9, [(a, EbComposite(9, 3, 5, 1, 5, [.. Enumerable.Repeat((b, 0, 0), 255)]))]),
                new EbSub(1, 9, [(b, EbComposite(9, 3, 5, 1, 5, [.. Enumerable.Repeat((c, 0, 0), 255)]))]),
                new EbSub(1, 9, [(c, EbComposite(9, 3, 5, 1, 5, [.. Enumerable.Repeat((d, 0, 0), 255)]))]),
                new EbSub(1, 9, [(d, EbComposite(9, 3, 5, 1, 5, [.. Enumerable.Repeat((o, 0, 0), 255)]))]),
                new EbSub(1, 1, [(o, EbImage(1, EbPattern))])])),
            _ => EbFontA(1, 1, EbImage(1, EbPattern)),
        };
        if (damage is "counts" or "outside")
        {
            var parsed = OpenTypeFont.Parse(font);
            var (offset, _) = parsed.Table("EBLC")!.Value;
            if (damage == "counts")
            {
                font[offset + 4] = font[offset + 5] = font[offset + 6] = font[offset + 7] = 0xFF;          // numSizes
                font[offset + 16] = font[offset + 17] = font[offset + 18] = font[offset + 19] = 0xFF;      // numberOfIndexSubtables
            }
            else
            {
                font[offset + 56 + 4] = 0x7F;   // indexSubtableOffset of the only record far beyond the table
            }
        }
        var watch = Stopwatch.StartNew();
        var (text, bitmap) = EbRender(font, "BT /F1 60 Tf 20 100 Td (A) Tj ET");
        using (bitmap)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
            Assert.Equal("A", text);
            // a composite naming itself: the component drawn down to the depth limit, the shape as without the loop; the fan-out ends at the
            // component budget, and the glyph draws nothing
            if (damage == "self") EbAssertPattern(bitmap, EbPattern);
            else Assert.Equal(0, EbInk(bitmap));
        }
    }

    // ------------------------------------------------------------ scrubbing and redaction

    /// <summary>
    /// OT 14.3 (index formats 1–5) and OT 13.2.8 (composites) — writer, FG 1169: scrubbing a font without outlines (EBDT only) for „A“ keeps the
    /// image of „A“ (and the components it is made of) byte for byte, and the image of „B“ is gone from the program; the scrubbed font still
    /// draws „A“ as before.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    public void Ot18_BitmapOnlyFont_Scrub_KeepsTheImagesOfKeptGlyphsOnly(int indexFormat)
    {
        int a = Ot18G('A'), b = Ot18G('B'), o = Ot18G('O'), i = Ot18G('I');
        int[][] other = [[0, 1, 1, 1, 0], [1, 1, 0, 1, 1], [0, 1, 1, 1, 0]];   // the image of „B“
        byte[] font;
        if (indexFormat == 8)
        {
            // „A“ made of „O“ (top row) and „I“ (two lower rows); „B“ made of „Z“ alone, which only „B“ needs
            var z = Ot18G('Z');
            font = EbFont(new EbStrike(10, 1, [new EbSub(1, 8, [(a, EbComposite(8, 3, 5, 1, 5, (o, 0, 0), (i, 0, 1))), (b, EbComposite(8, 3, 5, 1, 5, (z, 0, 0)))]),
                new EbSub(1, 1, [(o, EbImage(1, [EbPattern[0]]))]), new EbSub(1, 1, [(i, EbImage(1, EbPattern[1..]))]), new EbSub(1, 6, [(z, EbImage(6, other))])]));
        }
        else
        {
            var format = indexFormat is 2 or 5 ? 5 : 1;
            font = EbFont(new EbStrike(10, 1, [new EbSub(indexFormat, format, [(a, EbImage(format, EbPattern)), (b, EbImage(format, other))],
                indexFormat is 2 or 5 ? EbBig(3, 5, 1, 5) : null)]));
        }
        var scrubbed = TrueTypeSubsetter.Scrub(font, [a]);
        var data = OpenTypeFont.Parse(scrubbed).TableData("EBDT").ToArray();
        var removed = indexFormat == 8 ? EbImage(6, other) : EbImage(indexFormat is 2 or 5 ? 5 : 1, other);
        Assert.False(Contains(data, removed), "Bild von B");
        Assert.True(Contains(data, indexFormat == 8 ? EbImage(1, EbPattern[1..]) : EbImage(indexFormat is 2 or 5 ? 5 : 1, EbPattern)), "Bild von A");
        var (_, bitmap) = EbRender(scrubbed, "BT /F1 60 Tf 20 100 Td (A) Tj ET");
        using (bitmap) EbAssertPattern(bitmap, EbPattern);
    }

    private static bool Contains(byte[] data, byte[] part) => data.AsSpan().IndexOf(part) >= 0;

    /// <summary>
    /// OT 14.2 sentence 13, OT 14.3 sentences 9, 64–65 and 70–72 (writer, FG 1169): the EBLC the scrubber writes for a font without outlines —
    /// index subtable records sorted by firstGlyphIndex without overlaps, every index subtable at a multiple of 4 bytes from the table start,
    /// the format chosen to keep the table small: the dense run „A“–„E“ without „D“ in format 1, „a“ and „z“ alone over a wide range in format
    /// 4, the digits of one size and metrics (index format 2, without „3“) in format 5; the images of „D“ and „3“ gone.
    /// </summary>
    [Fact]
    public void Ot18_ScrubbedEblc_SortedAlignedAndCompactFormats()
    {
        int G(char c) => Ot18G(c);
        int[][] Shape(int k) => [[k & 1, (k >> 1) & 1, 1, (k >> 2) & 1, (k >> 3) & 1], [1, 0, 1, 0, 1], [(k >> 4) & 1, 1, 0, 1, 1]];
        var digits = "0123".Select((c, k) => (G(c), EbImage(5, Shape(k + 1)))).ToArray();
        var letters = "ABCDE".Select((c, k) => (G(c), EbImage(1, Shape(k + 8)))).ToArray();
        var sparse = "az".Select((c, k) => (G(c), EbImage(1, Shape(k + 20)))).ToArray();
        var font = EbFont(new EbStrike(10, 1, [new EbSub(2, 5, digits, EbBig(3, 5, 1, 5)), new EbSub(1, 1, letters), new EbSub(3, 1, sparse)]));
        var scrubbed = OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(font, "012ABCEaz".Select(G)));
        var eblc = scrubbed.TableData("EBLC").ToArray();
        var ebdt = scrubbed.TableData("EBDT").ToArray();
        Assert.Equal(1u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(eblc.AsSpan(4)));
        var list = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(eblc.AsSpan(8));
        var count = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(eblc.AsSpan(16));
        Assert.Equal(3, count);
        int U16(int at) => (eblc[at] << 8) | eblc[at + 1];
        var formats = new List<int>();
        var previousLast = -1;
        for (var r = 0; r < count; r++)
        {
            int first = U16(list + r * 8), last = U16(list + r * 8 + 2);
            Assert.True(first > previousLast, $"Eintrag {r}: {first} nach {previousLast}");
            previousLast = last;
            var at = list + (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(eblc.AsSpan(list + r * 8 + 4));
            Assert.Equal(0, at % 4);
            formats.Add(U16(at));
        }
        Assert.Equal([5, 1, 4], formats);
        Assert.False(Contains(ebdt, letters[3].Item2), "Bild von D");
        Assert.False(Contains(ebdt, digits[3].Item2), "Bild von 3");
        foreach (var kept in (byte[][])[letters[0].Item2, letters[4].Item2, sparse[1].Item2, digits[2].Item2]) Assert.True(Contains(ebdt, kept));
    }

    /// <summary>
    /// Redaction of fonts without outlines (FG 1169, principle of M6: nothing stays behind): an embedded TrueType font whose glyphs exist only as
    /// EBDT bitmaps, sbix images or CBDT images shows „A“ and „B“; „B“ is redacted — the text goes, and the program holds no image of „B“ any more,
    /// while that of „A“ stays (before, the program stayed as it was, with the image of the redacted letter).
    /// </summary>
    [Theory]
    [InlineData("Ebdt")]
    [InlineData("Sbix")]
    [InlineData("Cbdt")]
    public void Ot18_ImagesOnlyFont_RedactedLetter_LeavesNoImageInProgram(string kind)
    {
        byte[] program, imageA, imageB;
        if (kind == "Ebdt")
        {
            int[][] other = [[0, 1, 1, 1, 0], [1, 1, 0, 1, 1], [0, 1, 1, 1, 0]];
            program = EbFont(new EbStrike(10, 1, [new EbSub(1, 1, [(Ot18G('A'), EbImage(1, EbPattern)), (Ot18G('B'), EbImage(1, other))])]));
            (imageA, imageB) = (EbImage(1, EbPattern), EbImage(1, other));
        }
        else
        {
            var source = ColorFontBuilder.Build(kind == "Sbix" ? ColorFontBuilder.Kind.Sbix : ColorFontBuilder.Kind.Cbdt, 32);
            program = Ot18With(source, ("glyf", null), ("loca", null));
            var font = OpenTypeFont.Parse(program);
            var images = ColorFontBuilder.Images(OpenTypeFont.Parse(EmbeddedFonts.LoadHandwriting("PatrickHand-Regular.ttf")), 32);
            imageA = images.Single(i => i.Glyph == font.GlyphForUnicode('A')).Png;
            imageB = images.Single(i => i.Glyph == font.GlyphForUnicode('B')).Png;
        }
        Assert.True(Contains(program, imageA) && Contains(program, imageB));
        var pdf = TestPdf.Simple(1);
        var file = pdf.Add(TestPdf.FlateStream(program, TestPdf.Dict(("Length1", PdfInteger.Of(program.Length)))));
        var descriptor = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("M7Probe")), ("Flags", PdfInteger.Of(32)),
            ("FontBBox", TestPdf.Box(0, -200, 1000, 900)), ("ItalicAngle", PdfInteger.Of(0)), ("Ascent", PdfInteger.Of(900)), ("Descent", PdfInteger.Of(-200)),
            ("CapHeight", PdfInteger.Of(700)), ("StemV", PdfInteger.Of(80)), ("FontFile2", TestPdf.Ref(file))));
        var fontRef = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("M7Probe")),
            ("FirstChar", PdfInteger.Of(65)), ("LastChar", PdfInteger.Of(66)), ("Widths", new PdfArray([PdfInteger.Of(600), PdfInteger.Of(600)])),
            ("Encoding", TestPdf.N("WinAnsiEncoding")), ("FontDescriptor", TestPdf.Ref(descriptor))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("Font"))!.Set(PdfName.Get("F1"), TestPdf.Ref(fontRef));
        pdf.Replace(4, TestPdf.FlateStream("BT /F1 24 Tf 72 600 Td (A) Tj 200 0 Td (B) Tj ET"u8.ToArray()));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Equal("AB", RedactionTests.Read(doc, doc.Pages[0]).Text);
        using var after = PdfDocument.Open(RedactionTests.WithBars(doc, (0, new PdfRectangle(265, 590, 300, 630))));
        Assert.Equal("A", RedactionTests.Read(after, after.Pages[0]).Text);
        var stream = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!
            .GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile2"))!;
        var data = after.GetStreamData(stream);
        Assert.False(Contains(data, imageB), "Bild von B");
        Assert.True(Contains(data, imageA), "Bild von A");
    }
}
