using System.Globalization;
using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;
using PdfKnead.Core.Imaging;
using PdfKnead.Tests.Fonts;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M7, package ot-33 (OpenType: prep, sbix, SVG). The colour tables are built by hand in Liberation Sans (or taken from
/// <see cref="ColorFontBuilder"/>); PDF-Knead reads sbix and SVG only for colour glyphs of signature and element fonts.
/// </summary>
public sealed partial class M7NormTests
{
    // ------------------------------------------------------------ helpers (package ot-33)

    private static readonly byte[] Ot33Liberation = EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");

    private static readonly Lazy<OpenTypeFont> Ot33LiberationFont = new(() => OpenTypeFont.Parse(Ot33Liberation));

    private static int Ot33Gid(char c) => Ot33LiberationFont.Value.GlyphForUnicode(c);

    /// <summary>Font units per pixel of the SVG images built here: their resolution comes out at 128 px per em.</summary>
    private static double Ot33UnitsPerPx => Ot33LiberationFont.Value.UnitsPerEm / 128.0;

    private sealed class Ot33Bytes
    {
        private readonly List<byte> _bytes = [];

        public int Length => _bytes.Count;

        public Ot33Bytes U16(int v)
        {
            _bytes.Add((byte)(v >> 8));
            _bytes.Add((byte)v);
            return this;
        }

        public Ot33Bytes U32(long v) => U16((int)(v >> 16)).U16((int)v);

        public Ot33Bytes Tag(string tag) => Add(Encoding.ASCII.GetBytes(tag));

        public Ot33Bytes Add(ReadOnlySpan<byte> data)
        {
            _bytes.AddRange(data.ToArray());
            return this;
        }

        public byte[] ToArray() => [.. _bytes];
    }

    /// <summary>An opaque blue PNG of the size given.</summary>
    private static byte[] Ot33Png(int width, int height)
    {
        using var output = new MemoryStream();
        PngEncoder.Encode(width, height, 8, PngColorType.Rgba, (_, row) =>
        {
            for (var i = 0; i < row.Length; i += 4) (row[i], row[i + 1], row[i + 2], row[i + 3]) = (0x10, 0x20, 0x90, 0xFF);
        }, output);
        return output.ToArray();
    }

    /// <summary>One strike of an sbix table: ppem, ppi and the glyph data records (graphicType and data; origin offsets 0).</summary>
    private sealed record Ot33Strike(int Ppem, int Ppi, Dictionary<int, (string Type, byte[] Data)> Glyphs);

    /// <summary>An sbix table (version 1) with the flags and strikes given, for the glyph count of Liberation Sans.</summary>
    private static byte[] Ot33Sbix(int flags, params Ot33Strike[] strikes)
    {
        var glyphCount = Ot33LiberationFont.Value.GlyphCount;
        var bodies = new List<byte[]>();
        foreach (var s in strikes)
        {
            var data = new Ot33Bytes();
            var offsets = new List<long>();
            var header = 4 + 4L * (glyphCount + 1);
            for (var g = 0; g < glyphCount; g++)
            {
                offsets.Add(header + data.Length);
                if (s.Glyphs.TryGetValue(g, out var r)) data.U16(0).U16(0).Tag(r.Type).Add(r.Data);
            }
            offsets.Add(header + data.Length);
            var strike = new Ot33Bytes().U16(s.Ppem).U16(s.Ppi);
            foreach (var o in offsets) strike.U32(o);
            bodies.Add(strike.Add(data.ToArray()).ToArray());
        }
        var t = new Ot33Bytes().U16(1).U16(flags).U32(strikes.Length);
        long at = 8 + 4L * strikes.Length;
        foreach (var b in bodies)
        {
            t.U32(at);
            at += b.Length;
        }
        foreach (var b in bodies) t.Add(b);
        return t.ToArray();
    }

    private static OpenTypeFont Ot33WithTable(string tag, byte[] table, byte[]? font = null) =>
        OpenTypeFont.Parse(ColorFontBuilder.WithTables(font ?? Ot33Liberation, new Dictionary<string, byte[]?> { [tag] = table }));

    private static Dictionary<int, (string, byte[])> Ot33Png(params (int Glyph, int Size)[] glyphs) =>
        glyphs.ToDictionary(g => g.Glyph, g => ("png ", Ot33Png(g.Size, g.Size)));

    /// <summary>
    /// An SVG table (version 0, list right after the header) with one record per entry; entries naming the same document array share it.
    /// </summary>
    private static byte[] Ot33Svg(params (int First, int Last, byte[] Doc)[] entries)
    {
        var t = new Ot33Bytes().U16(0).U32(10).U32(0).U16(entries.Length);
        var offsets = new Dictionary<byte[], long>(ReferenceEqualityComparer.Instance);
        var documents = new List<byte[]>();
        long at = 2 + 12L * entries.Length;
        foreach (var (first, last, doc) in entries)
        {
            if (!offsets.TryGetValue(doc, out var offset))
            {
                offsets[doc] = offset = at;
                documents.Add(doc);
                at += doc.Length;
            }
            t.U16(first).U16(last).U32(offset).U32(doc.Length);
        }
        foreach (var doc in documents) t.Add(doc);
        return t.ToArray();
    }

    private const string Ot33Open = """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" version="1.1">""";

    private static string Ot33N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>An image element with a square PNG of <paramref name="px"/> pixels, its top left corner at x 0, one image height above the baseline.</summary>
    private static string Ot33Image(int px, string extra = "", string href = "xlink:href", int? pxHigh = null, string close = "/>")
    {
        var w = px * Ot33UnitsPerPx;
        var h = (pxHigh ?? px) * Ot33UnitsPerPx;
        return $"""<image x="0" y="{Ot33N(-h)}" width="{Ot33N(w)}" height="{Ot33N(h)}"{extra} {href}="data:image/png;base64,{Convert.ToBase64String(Ot33Png(px, pxHigh ?? px))}"{close}""";
    }

    private static byte[] Ot33Doc(string inner, string open = Ot33Open) => Encoding.UTF8.GetBytes(open + inner + "</svg>");

    /// <summary>SVG font with one document per glyph (inner content of the svg element).</summary>
    private static OpenTypeFont Ot33SvgFont(params (int Glyph, string Inner)[] glyphs) =>
        Ot33WithTable("SVG ", Ot33Svg([.. glyphs.OrderBy(g => g.Glyph).Select(g => (g.Glyph, g.Glyph, Ot33Doc(g.Inner)))]));

    private static void Ot33SameImage((int X, int Y, RgbaImage Image)? expected, (int X, int Y, RgbaImage Image)? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected.Value.X, actual.Value.X);
        Assert.Equal(expected.Value.Y, actual.Value.Y);
        Assert.Equal(expected.Value.Image.Width, actual.Value.Image.Width);
        Assert.Equal(expected.Value.Image.Height, actual.Value.Image.Height);
        Assert.Equal(expected.Value.Image.Pixels, actual.Value.Image.Pixels);
    }

    private static void Ot33SameOutline(GlyphOutline expected, GlyphOutline actual)
    {
        Assert.Equal(expected.Verbs, actual.Verbs);
        Assert.Equal(expected.Points, actual.Points);
    }

    // ------------------------------------------------------------ OT 33 (prep)

    /// <summary>
    /// OT 33 sentences 1–5: prep is the font-wide control value program of a hinting rasterizer. PDF-Knead never runs it and hands it on
    /// unchanged — the subset and the scrubbed font of Liberation Sans carry prep byte for byte, and the outlines of glyphs read the same with
    /// and without prep.
    /// </summary>
    [Fact]
    public void Ot33_Prep_SubsetAndScrubPassItOnByteForByte_OutlinesIgnoreIt()
    {
        var prep = Ot33LiberationFont.Value.TableData("prep").ToArray();
        Assert.NotEmpty(prep);
        int[] glyphs = [Ot33Gid('A'), Ot33Gid('b')];
        Assert.Equal(prep, OpenTypeFont.Parse(TrueTypeSubsetter.Subset(Ot33Liberation, glyphs)).TableData("prep").ToArray());
        Assert.Equal(prep, OpenTypeFont.Parse(TrueTypeSubsetter.Scrub(Ot33Liberation, glyphs)).TableData("prep").ToArray());
        var without = OpenTypeFont.Parse(ColorFontBuilder.WithTables(Ot33Liberation, new Dictionary<string, byte[]?> { ["prep"] = null }));
        Assert.False(without.HasTable("prep"));
        foreach (var g in glyphs) Ot33SameOutline(Ot33LiberationFont.Value.Outline(g), without.Outline(g));
    }

    /// <summary>
    /// OT 33 sentences 6–8: the table is n bytes of instructions, n as many as its length holds — a prep of 7 bytes (not a multiple of 4)
    /// goes into the subset as exactly these 7 bytes; a font without prep gives a subset without prep.
    /// </summary>
    [Fact]
    public void Ot33_PrepOfSevenBytes_SubsetTakesExactlyThose_WithoutPrepNone()
    {
        byte[] seven = [0xB0, 0x01, 0x2C, 0xB1, 0x02, 0x03, 0x1D];
        var with = ColorFontBuilder.WithTables(Ot33Liberation, new Dictionary<string, byte[]?> { ["prep"] = seven });
        int[] glyphs = [Ot33Gid('A')];
        Assert.Equal(seven, OpenTypeFont.Parse(TrueTypeSubsetter.Subset(with, glyphs)).TableData("prep").ToArray());
        var without = ColorFontBuilder.WithTables(Ot33Liberation, new Dictionary<string, byte[]?> { ["prep"] = null });
        Assert.False(OpenTypeFont.Parse(TrueTypeSubsetter.Subset(without, glyphs)).HasTable("prep"));
    }

    // ------------------------------------------------------------ OT 34 (sbix)

    /// <summary>
    /// OT 34 sentence 9 and OT 34.2 sentences 15–16, 18: strikes need not cover every glyph, and a glyph that has an image in any strike must
    /// be drawn with one — strikes of 64 and 128 ppem, „A“ only in the 64 strike: <c>ColorGlyphs.Glyph(A)</c> must give an image.
    /// </summary>
    [Fact]
    public void Ot33_SbixGlyphOnlyInTheSmallerStrike_GetsAnImage()
    {
        var font = Ot33WithTable("sbix", Ot33Sbix(1,
            new Ot33Strike(64, 72, Ot33Png((Ot33Gid('A'), 8))),
            new Ot33Strike(128, 72, Ot33Png((Ot33Gid('B'), 16)))));
        var color = ColorGlyphs.For(font);
        Assert.NotNull(color);
        Assert.NotNull(color.Glyph(Ot33Gid('B')));
        Assert.NotNull(color.Glyph(Ot33Gid('A')));
    }

    /// <summary>
    /// OT 34.1 sentences 5–7, 10 and 11–12, 14–15: the flags are not read — bit 1 („draw the outline too“) is optional and not supported, bit 0
    /// is historical; with flags 0 or 3 the glyph images and the composed name are the same as with flags 1.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Ot33_SbixFlags_DoNotChangeTheImagesOrTheComposition(int flags)
    {
        OpenTypeFont Font(int f) => Ot33WithTable("sbix", Ot33Sbix(f, new Ot33Strike(128, 72, Ot33Png((Ot33Gid('A'), 12), (Ot33Gid('B'), 16)))));
        var one = Font(1);
        var other = Font(flags);
        var c1 = ColorGlyphs.For(one)!;
        var c2 = ColorGlyphs.For(other)!;
        foreach (var c in "AB") Ot33SameImage(c1.Glyph(Ot33Gid(c)), c2.Glyph(Ot33Gid(c)));
        var a = c1.Compose(TextShaper.For(one).Shape("AB"), one.UnitsPerEm)!;
        var b = c2.Compose(TextShaper.For(other).Shape("AB"), other.UnitsPerEm)!;
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        Assert.Equal(a.Pixels, b.Pixels);
    }

    /// <summary>
    /// OT 34.1 sentence 13 and OT 35.6 sentence 6: a glyph without a bitmap or SVG description is drawn with its outline — „M€x“ in a colour
    /// font without an image for „€“: in the composed image something must stand in the advance of „€“ (its outline); the place stays empty.
    /// </summary>
    [Theory]
    [InlineData(ColorFontBuilder.Kind.Sbix, 128)]
    [InlineData(ColorFontBuilder.Kind.Svg, 200)]
    public void Ot33_GlyphWithoutColourImage_ShowsItsOutline(ColorFontBuilder.Kind kind, int ppem)
    {
        var font = OpenTypeFont.Parse(ColorFontBuilder.Build(kind, ppem));
        var color = ColorGlyphs.For(font)!;
        var euro = font.GlyphForUnicode('€');
        Assert.True(euro > 0 && !font.Outline(euro).IsEmpty);
        Assert.Null(color.Glyph(euro));
        var shaped = TextShaper.For(font).Shape("M€x");
        var image = color.Compose(shaped, font.UnitsPerEm)!;
        var k = (double)color.PixelsPerEm / font.UnitsPerEm;
        var m = color.Glyph(shaped[0].Glyph)!.Value;
        var left = Math.Min(m.X, shaped[0].XAdvance * k + shaped[1].XAdvance * k + color.Glyph(shaped[2].Glyph)!.Value.X);
        // the middle half of the advance of „€“, in pixels of the composed image
        var from = (int)Math.Ceiling(shaped[0].XAdvance * k + shaped[1].XAdvance * k / 4 - left);
        var to = (int)Math.Floor(shaped[0].XAdvance * k + shaped[1].XAdvance * k * 3 / 4 - left);
        var inked = false;
        for (var y = 0; y < image.Height && !inked; y++)
            for (var x = from; x < to && !inked; x++)
                inked = image.Pixels[(y * image.Width + x) * 4 + 3] > 0;
        Assert.True(inked, "an der Stelle von „€“ steht nichts");
    }

    /// <summary>
    /// OT 34.2 sentence 14: non-printing glyphs should have no data — a rule for the font; PDF-Knead follows the data: an image given for the
    /// space is used, without data the space has none.
    /// </summary>
    [Fact]
    public void Ot33_SbixImageForTheSpace_IsUsed_WithoutDataNone()
    {
        var space = Ot33Gid(' ');
        var with = ColorGlyphs.For(Ot33WithTable("sbix", Ot33Sbix(1, new Ot33Strike(128, 72, Ot33Png((Ot33Gid('A'), 8), (space, 8))))))!;
        Assert.NotNull(with.Glyph(space));
        var without = ColorGlyphs.For(Ot33WithTable("sbix", Ot33Sbix(1, new Ot33Strike(128, 72, Ot33Png((Ot33Gid('A'), 8))))))!;
        Assert.Null(without.Glyph(space));
    }

    /// <summary>
    /// OT 34.2 sentence 17: without the exact size another basis may be chosen — PDF-Knead takes the strike of the largest ppem (the sharpest
    /// image): strikes 64, 160 and 128 give 160 px per em.
    /// </summary>
    [Fact]
    public void Ot33_SbixStrikes_TheLargestIsUsed()
    {
        var a = Ot33Gid('A');
        var color = ColorGlyphs.For(Ot33WithTable("sbix", Ot33Sbix(1,
            new Ot33Strike(64, 72, Ot33Png((a, 8))), new Ot33Strike(160, 72, Ot33Png((a, 20))), new Ot33Strike(128, 72, Ot33Png((a, 16))))))!;
        Assert.Equal(160, color.PixelsPerEm);
        Assert.Equal(20, color.Glyph(a)!.Value.Image.Width);
    }

    /// <summary>
    /// OT 34.2 sentences 19–21: two strikes may share a ppem with different ppi, and choosing by pixel density is not required — of two
    /// strikes of 128 ppem (72 and 144 ppi) the first is taken.
    /// </summary>
    [Fact]
    public void Ot33_SbixStrikesOfTheSamePpem_TheFirstIsUsed()
    {
        var a = Ot33Gid('A');
        var color = ColorGlyphs.For(Ot33WithTable("sbix", Ot33Sbix(1,
            new Ot33Strike(128, 72, Ot33Png((a, 8))), new Ot33Strike(128, 144, Ot33Png((a, 16))))))!;
        Assert.Equal(128, color.PixelsPerEm);
        Assert.Equal(8, color.Glyph(a)!.Value.Image.Width);
    }

    /// <summary>
    /// OT 34.3 sentence 13 (FG 1156): for a glyph with contours the image origin lies at the lower left corner of its box (xMin, yMin) — the
    /// hyphen of Liberation Sans (a rectangle above the baseline) with originOffset 0 at 128 ppem: the image starts at xMin and yMin in pixels.
    /// </summary>
    [Fact]
    public void Ot33_SbixGlyphWithContour_ImageStartsAtTheBoxCorner()
    {
        var hyphen = Ot33Gid('-');
        var box = Ot33LiberationFont.Value.Outline(hyphen).Bounds;
        Assert.True(box.Top > 100);
        var font = Ot33WithTable("sbix", Ot33Sbix(1, new Ot33Strike(128, 72, Ot33Png((hyphen, 8)))));
        var k = 128.0 / font.UnitsPerEm;
        var glyph = ColorGlyphs.For(font)!.Glyph(hyphen)!.Value;
        Assert.Equal(((int)Math.Round(box.Left * k), (int)Math.Round(box.Top * k)), (glyph.X, glyph.Y));
    }

    /// <summary>
    /// OT 34.3 sentences 10–12: for a glyph without contours the image origin lies at the pen, the lsb of hmtx does not apply — the space of
    /// Liberation Sans (empty) with lsb 200 and originOffsetX 0: its image starts at the pen, two such glyphs compose to one advance plus one
    /// image width.
    /// </summary>
    [Fact]
    public void Ot33_SbixGlyphWithoutContour_ImageStartsAtThePen()
    {
        var space = Ot33Gid(' ');
        Assert.True(Ot33LiberationFont.Value.Outline(space).IsEmpty);
        var hhea = Ot33LiberationFont.Value.TableData("hhea");
        var hmtx = Ot33LiberationFont.Value.TableData("hmtx").ToArray();
        Assert.True(space < ((hhea[34] << 8) | hhea[35]));
        (hmtx[space * 4 + 2], hmtx[space * 4 + 3]) = (0, 200);
        var source = ColorFontBuilder.WithTables(Ot33Liberation, new Dictionary<string, byte[]?> { ["hmtx"] = hmtx });
        var font = Ot33WithTable("sbix", Ot33Sbix(1, new Ot33Strike(128, 72, Ot33Png((space, 8)))), source);
        var color = ColorGlyphs.For(font)!;
        Assert.Equal(0, color.Glyph(space)!.Value.X);
        var advance = font.AdvanceWidth(space);
        var k = 128.0 / font.UnitsPerEm;
        var image = color.Compose([new ShapedGlyph { Glyph = space, XAdvance = advance }, new ShapedGlyph { Glyph = space, XAdvance = advance }], font.UnitsPerEm)!;
        Assert.Equal((int)Math.Ceiling(advance * k + 8), image.Width);
    }

    /// <summary>
    /// OT 34.3 sentences 14–15: the standard graphic types are JPEG, PNG and TIFF — a glyph whose data are a JPEG („jpg “) must give an image
    /// (a JPEG decoder is part of the program).
    /// </summary>
    [Fact]
    public void Ot33_SbixJpegImage_IsDecoded()
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.Blue);
        using var jpeg = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        var a = Ot33Gid('A');
        var glyphs = Ot33Png((Ot33Gid('B'), 8));
        glyphs[a] = ("jpg ", jpeg.ToArray());
        var color = ColorGlyphs.For(Ot33WithTable("sbix", Ot33Sbix(1, new Ot33Strike(128, 72, glyphs))))!;
        Assert.NotNull(color.Glyph(a));
    }

    /// <summary>
    /// OT 34.3 sentences 16–17: „dupe“ data are a glyph ID whose image is used — B as dupe of A gives A's image (placed at B's own box corner,
    /// 34.3 S13; FG 1156); a dupe of itself and a chain of six dupes end without an image (no endless loop).
    /// </summary>
    [Fact]
    public void Ot33_SbixDupe_UsesTheOtherImage_LoopsEndWithoutImage()
    {
        static byte[] Id(int g) => [(byte)(g >> 8), (byte)g];
        var (a, b, c) = (Ot33Gid('A'), Ot33Gid('B'), Ot33Gid('C'));
        var glyphs = Ot33Png((a, 12));
        glyphs[b] = ("dupe", Id(a));
        glyphs[c] = ("dupe", Id(c));
        // D → E → F → G → H → I → A: six dupes
        var chain = "DEFGHI".Select(Ot33Gid).ToArray();
        for (var i = 0; i < chain.Length; i++) glyphs[chain[i]] = ("dupe", Id(i + 1 < chain.Length ? chain[i + 1] : a));
        var color = ColorGlyphs.For(Ot33WithTable("sbix", Ot33Sbix(1, new Ot33Strike(128, 72, glyphs))))!;
        var (ia, ib) = (color.Glyph(a)!.Value, color.Glyph(b)!.Value);
        var k = 128.0 / Ot33LiberationFont.Value.UnitsPerEm;
        var (ca, cb) = (Ot33LiberationFont.Value.ContourCorner(a)!.Value, Ot33LiberationFont.Value.ContourCorner(b)!.Value);
        Assert.Equal((ia.X - (int)Math.Round(ca.XMin * k) + (int)Math.Round(cb.XMin * k), ia.Y - (int)Math.Round(ca.YMin * k) + (int)Math.Round(cb.YMin * k)),
            (ib.X, ib.Y));
        Assert.Equal(ia.Image.Pixels, ib.Image.Pixels);
        Assert.Null(color.Glyph(c));
        Assert.Null(color.Glyph(chain[0]));
    }

    /// <summary>OT 34.3 sentences 18–19: the graphic types „pdf “ and „mask“ do not exist in OpenType — such glyphs have no image, nothing throws.</summary>
    [Fact]
    public void Ot33_SbixPdfAndMaskTypes_GiveNoImage()
    {
        var glyphs = Ot33Png((Ot33Gid('A'), 8));
        glyphs[Ot33Gid('B')] = ("pdf ", Ot33Png(8, 8));
        glyphs[Ot33Gid('C')] = ("mask", Ot33Png(8, 8));
        var color = ColorGlyphs.For(Ot33WithTable("sbix", Ot33Sbix(1, new Ot33Strike(128, 72, glyphs))))!;
        Assert.NotNull(color.Glyph(Ot33Gid('A')));
        Assert.Null(color.Glyph(Ot33Gid('B')));
        Assert.Null(color.Glyph(Ot33Gid('C')));
    }

    // ------------------------------------------------------------ OT 35 (SVG)

    /// <summary>
    /// OT 35.1 sentences 16–17: every SVG description needs an outline glyph — a record for glyph ID glyph count + 5 gives no image, and a
    /// colour font with SVG but without glyf and loca is not accepted as a signature font.
    /// </summary>
    [Fact]
    public void Ot33_SvgEntryBeyondTheGlyphCount_HasNoImage_SvgFontWithoutOutlines_IsNotImported()
    {
        var a = Ot33Gid('A');
        var beyond = Ot33LiberationFont.Value.GlyphCount + 5;
        var color = ColorGlyphs.For(Ot33WithTable("SVG ", Ot33Svg((a, a, Ot33Doc(Ot33Image(16))), (beyond, beyond, Ot33Doc(Ot33Image(16))))))!;
        Assert.NotNull(color.Glyph(a));
        Assert.Null(color.Glyph(beyond));

        var bare = ColorFontBuilder.WithTables(ColorFontBuilder.Build(ColorFontBuilder.Kind.Svg, 200),
            new Dictionary<string, byte[]?> { ["glyf"] = null, ["loca"] = null });
        var folder = Directory.CreateTempSubdirectory("pdfk-ot33-");
        try
        {
            var file = Path.Combine(folder.FullName, "Farbschrift ohne Umrisse.ttf");
            File.WriteAllBytes(file, bare);
            var result = SignatureFonts.Import(file, [Path.Combine(folder.FullName, "ziel")]);
            Assert.Null(result.File);
            Assert.NotNull(result.Error);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>
    /// OT 35.2 sentence 7: svgDocumentListOffset must not be 0 — with 0 the font has no image source (version 0: the list would read no
    /// entries); with version 1 neither (an unknown version is not read since FG 1149).
    /// </summary>
    [Fact]
    public void Ot33_SvgListOffsetZero_GivesNoImageSource()
    {
        var a = Ot33Gid('A');
        var table = Ot33Svg((a, a, Ot33Doc(Ot33Image(16))));
        Assert.NotNull(ColorGlyphs.For(Ot33WithTable("SVG ", table)));
        var zero = table.ToArray();
        zero[2] = zero[3] = zero[4] = zero[5] = 0;
        Assert.Null(ColorGlyphs.For(Ot33WithTable("SVG ", zero)));
        var version1 = zero.ToArray();
        version1[1] = 1;
        Assert.Null(ColorGlyphs.For(Ot33WithTable("SVG ", version1)));
    }

    /// <summary>
    /// OT 35.2 sentence 13: numEntries must not be 0 — 0 entries give no image source; numEntries 1 000 with records for three glyphs still
    /// finds the images of the three.
    /// </summary>
    [Fact]
    public void Ot33_SvgNumEntries_ZeroGivesNoSource_TooManyStillFindTheRecords()
    {
        var (a, b, c) = (Ot33Gid('A'), Ot33Gid('B'), Ot33Gid('C'));
        var table = Ot33Svg((a, a, Ot33Doc(Ot33Image(16))), (b, b, Ot33Doc(Ot33Image(12))), (c, c, Ot33Doc(Ot33Image(8))));
        var none = table.ToArray();
        none[10] = none[11] = 0;
        Assert.Null(ColorGlyphs.For(Ot33WithTable("SVG ", none)));
        var many = table.ToArray();
        (many[10], many[11]) = (1000 >> 8, 1000 & 0xFF);
        var color = ColorGlyphs.For(Ot33WithTable("SVG ", many))!;
        Assert.Equal(16, color.Glyph(a)!.Value.Image.Width);
        Assert.Equal(12, color.Glyph(b)!.Value.Image.Width);
        Assert.Equal(8, color.Glyph(c)!.Value.Image.Width);
    }

    /// <summary>
    /// OT 35.2 sentence 21: svgDocOffset must not be 0 — a record with offset 0 (its length up to the end of the table) gives that glyph no
    /// image and throws nothing; the other glyph keeps its image.
    /// </summary>
    [Fact]
    public void Ot33_SvgDocOffsetZero_GivesNoImage()
    {
        var (a, b) = (Ot33Gid('A'), Ot33Gid('B'));
        var table = Ot33Svg((a, a, Ot33Doc(Ot33Image(16))), (b, b, Ot33Doc(Ot33Image(12))));
        // record of B (the second, at 12 + 12): offset 0, length everything from the start of the list
        var length = table.Length - 10;
        for (var i = 0; i < 4; i++)
        {
            table[24 + 4 + i] = 0;
            table[24 + 8 + i] = (byte)(length >> (24 - 8 * i));
        }
        var color = ColorGlyphs.For(Ot33WithTable("SVG ", table))!;
        Assert.Equal(16, color.Glyph(a)!.Value.Image.Width);
        Assert.Null(color.Glyph(b));
    }

    /// <summary>OT 35.2 sentence 23: svgDocLength must not be 0 — that glyph has no image, the others are as before.</summary>
    [Fact]
    public void Ot33_SvgDocLengthZero_OnlyThatGlyphHasNoImage()
    {
        var (a, b, c) = (Ot33Gid('A'), Ot33Gid('B'), Ot33Gid('C'));
        var table = Ot33Svg((a, a, Ot33Doc(Ot33Image(16))), (b, b, Ot33Doc(Ot33Image(12))), (c, c, Ot33Doc(Ot33Image(8))));
        var before = ColorGlyphs.For(Ot33WithTable("SVG ", table))!;
        var zero = table.ToArray();
        zero[24 + 8] = zero[24 + 9] = zero[24 + 10] = zero[24 + 11] = 0;
        var after = ColorGlyphs.For(Ot33WithTable("SVG ", zero))!;
        Assert.NotNull(before.Glyph(b));
        Assert.Null(after.Glyph(b));
        Ot33SameImage(before.Glyph(a), after.Glyph(a));
        Ot33SameImage(before.Glyph(c), after.Glyph(c));
    }

    /// <summary>
    /// OT 35.2 sentences 26–28: several records may name one document, which then describes glyphs that are not contiguous (example 1) — one
    /// document for glyphs 2, 13, 14 (the image of glyph13 first), records [2,2] and [13,14]: glyph 2 must get the image of the element
    /// glyph2 (8 px), not that of glyph13 (32 px).
    /// </summary>
    [Fact]
    public void Ot33_SvgDocumentForSeveralGlyphs_SingleRecordFindsItsOwnElement()
    {
        var doc = Ot33Doc($"""<g id="glyph13">{Ot33Image(32)}</g><g id="glyph2">{Ot33Image(8)}</g><g id="glyph14">{Ot33Image(16)}</g>""");
        var color = ColorGlyphs.For(Ot33WithTable("SVG ", Ot33Svg((2, 2, doc), (13, 14, doc))))!;
        Assert.Equal(32, color.Glyph(13)!.Value.Image.Width);
        Assert.Equal(16, color.Glyph(14)!.Value.Image.Width);
        Assert.Equal(8, color.Glyph(2)!.Value.Image.Width);
    }

    /// <summary>
    /// OT 35.3.2 sentences 7–12: documents are XML and the application must handle the XML form — an image element inside a comment is no
    /// element; the real image after it (8 px) must be used, not the commented one (32 px).
    /// </summary>
    [Fact]
    public void Ot33_SvgCommentedImageElement_IsNotTheImage()
    {
        var a = Ot33Gid('A');
        var color = ColorGlyphs.For(Ot33SvgFont((a, $"<!-- {Ot33Image(32)} -->{Ot33Image(8)}")))!;
        Assert.Equal(8, color.Glyph(a)!.Value.Image.Width);
    }

    /// <summary>OT 35.3.2 sentences 7–12: XML names are case-sensitive — an element „&lt;IMAGE“ is no image element, the glyph has no image.</summary>
    [Fact]
    public void Ot33_SvgUpperCaseImageElement_IsNoImage()
    {
        var (a, b) = (Ot33Gid('A'), Ot33Gid('B'));
        var color = ColorGlyphs.For(Ot33SvgFont((a, Ot33Image(16)), (b, Ot33Image(16).Replace("<image", "<IMAGE", StringComparison.Ordinal))))!;
        Assert.NotNull(color.Glyph(a));
        Assert.Null(color.Glyph(b));
    }

    /// <summary>
    /// OT 35.3.2 sentences 13–18: the namespace declarations are rules for the font, the reader does not check them — a document without
    /// xmlns, and one with href instead of xlink:href, give the same image as one with both declarations.
    /// </summary>
    [Fact]
    public void Ot33_SvgWithoutXmlnsOrWithPlainHref_SameImage()
    {
        var a = Ot33Gid('A');
        var expected = ColorGlyphs.For(Ot33SvgFont((a, Ot33Image(16))))!.Glyph(a);
        var bare = Ot33WithTable("SVG ", Ot33Svg((a, a, Ot33Doc(Ot33Image(16), "<svg>"))));
        Ot33SameImage(expected, ColorGlyphs.For(bare)!.Glyph(a));
        Ot33SameImage(expected, ColorGlyphs.For(Ot33SvgFont((a, Ot33Image(16, href: "href"))))!.Glyph(a));
    }

    /// <summary>
    /// OT 35.3.3 sentences 3–17: restricted elements are not rendered — a document with text is not used at all; script is never run (the
    /// image as without it); an image of width 1em and an image with SVG data give no image.
    /// </summary>
    [Fact]
    public void Ot33_SvgRestrictedContent_TextRejected_ScriptNeverRun_EmAndSvgDataNoImage()
    {
        var (a, b, c) = (Ot33Gid('A'), Ot33Gid('B'), Ot33Gid('C'));
        Assert.Null(ColorGlyphs.For(Ot33SvgFont((a, $"<text>Ab</text>{Ot33Image(16)}"))));
        var expected = ColorGlyphs.For(Ot33SvgFont((a, Ot33Image(16))))!.Glyph(a);
        Ot33SameImage(expected, ColorGlyphs.For(Ot33SvgFont((a, $"<script>document.title = 'x';</script>{Ot33Image(16)}")))!.Glyph(a));
        var em = System.Text.RegularExpressions.Regex.Replace(Ot33Image(16), "width=\"[^\"]*\"", "width=\"1em\"");
        var svgData = Ot33Image(16).Replace("data:image/png;base64,", "data:image/svg+xml;base64,", StringComparison.Ordinal);
        var color = ColorGlyphs.For(Ot33SvgFont((a, Ot33Image(16)), (b, em), (c, svgData)))!;
        Assert.NotNull(color.Glyph(a));
        Assert.Null(color.Glyph(b));
        Assert.Null(color.Glyph(c));
    }

    /// <summary>
    /// OT 35.3.3 sentences 3–17: elements inside a and switch are restricted and to be ignored — an image inside a, and one inside switch,
    /// must give no image.
    /// </summary>
    [Fact]
    public void Ot33_SvgImageInsideAOrSwitch_IsIgnored()
    {
        var (a, b, c) = (Ot33Gid('A'), Ot33Gid('B'), Ot33Gid('C'));
        var color = ColorGlyphs.For(Ot33SvgFont((a, Ot33Image(16)), (b, $"""<a href="#x">{Ot33Image(16)}</a>"""), (c, $"<switch>{Ot33Image(16)}</switch>")))!;
        Assert.NotNull(color.Glyph(a));
        Assert.Null(color.Glyph(b));
        Assert.Null(color.Glyph(c));
    }

    /// <summary>OT 35.3.3 sentence 18: title, desc and metadata may appear and are ignored — the same image as without them.</summary>
    [Fact]
    public void Ot33_SvgTitleDescMetadata_AreIgnored()
    {
        var a = Ot33Gid('A');
        var expected = ColorGlyphs.For(Ot33SvgFont((a, Ot33Image(16))))!.Glyph(a);
        var color = ColorGlyphs.For(Ot33SvgFont((a, $"<title>Glyphe A</title><desc>Erfundene Probe</desc><metadata><x>1</x></metadata>{Ot33Image(16)}")))!;
        Ot33SameImage(expected, color.Glyph(a));
    }

    /// <summary>
    /// OT 35.3.3 sentences 19–35 (optional capabilities, not supported): a document with filter, pattern or mask around the image is not
    /// used; style="opacity:0" on the image is not evaluated — today the image is fully visible.
    /// </summary>
    [Fact]
    public void Ot33_SvgOptionalCapabilities_FilterPatternMaskRejected_OpacityNotEvaluated()
    {
        var a = Ot33Gid('A');
        Assert.Null(ColorGlyphs.For(Ot33SvgFont((a, $"""<filter id="f"/><g filter="url(#f)">{Ot33Image(16)}</g>"""))));
        Assert.Null(ColorGlyphs.For(Ot33SvgFont((a, $"""<pattern id="p">{Ot33Image(16)}</pattern>"""))));
        Assert.Null(ColorGlyphs.For(Ot33SvgFont((a, $"""<mask id="m">{Ot33Image(16)}</mask>"""))));
        var image = ColorGlyphs.For(Ot33SvgFont((a, Ot33Image(16, extra: " style=\"opacity:0\""))))!.Glyph(a)!.Value.Image;
        Assert.All(Enumerable.Range(0, image.Width * image.Height), i => Assert.Equal(255, image.Pixels[i * 4 + 3]));
    }

    /// <summary>
    /// OT 35.3.3 sentences 36–39: presentation attributes rather than CSS, no media queries — CSS is not read: a style element with a media
    /// query hiding images gives the same image as without it.
    /// </summary>
    [Fact]
    public void Ot33_SvgStyleWithMediaQuery_SameImage()
    {
        var a = Ot33Gid('A');
        var expected = ColorGlyphs.For(Ot33SvgFont((a, Ot33Image(16))))!.Glyph(a);
        var color = ColorGlyphs.For(Ot33SvgFont((a, $"<style>@media (prefers-color-scheme: dark) {{ image {{ display: none; }} }}</style>{Ot33Image(16)}")))!;
        Ot33SameImage(expected, color.Glyph(a));
    }

    /// <summary>
    /// OT 35.5 sentences 9–10, 14–15: the element with the glyph id is rendered as if referenced by use, with its own content only — an
    /// empty &lt;g id="glyph5"/&gt; followed by &lt;g id="glyph6"&gt; with an image, record [5,6]: glyph 5 must have no image (it gets glyph 6's).
    /// </summary>
    [Fact]
    public void Ot33_SvgEmptyElementWithTheId_HasNoImage()
    {
        var doc = Ot33Doc($"""<g id="glyph5"/><g id="glyph6">{Ot33Image(16)}</g>""");
        var color = ColorGlyphs.For(Ot33WithTable("SVG ", Ot33Svg((5, 6, doc), (7, 7, Ot33Doc(Ot33Image(8))))))!;
        Assert.NotNull(color.Glyph(6));
        Assert.Null(color.Glyph(5));
    }

    /// <summary>
    /// OT 35.6 sentences 1–2: the description must show the same abstract glyph as the outline — a rule for the font, not checked: an image of
    /// 32 × 8 px for the narrow „I“ is used unchanged.
    /// </summary>
    [Fact]
    public void Ot33_SvgImageNotMatchingTheOutline_IsUsedUnchanged()
    {
        var i = Ot33Gid('I');
        var image = ColorGlyphs.For(Ot33SvgFont((i, Ot33Image(32, pxHigh: 8))))!.Glyph(i)!.Value.Image;
        Assert.Equal((32, 8), (image.Width, image.Height));
    }

    /// <summary>
    /// OT 35.7 sentences 4–5 (today's behaviour for viewBox): a viewBox acts as a scaling — documents with a viewBox are not used, the font has
    /// no image source.
    /// </summary>
    [Fact]
    public void Ot33_SvgWithViewBox_NotUsed()
    {
        var a = Ot33Gid('A');
        var upm = Ot33LiberationFont.Value.UnitsPerEm;
        var open = $"""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 {-upm} {upm} {upm}">""";
        Assert.Null(ColorGlyphs.For(Ot33WithTable("SVG ", Ot33Svg((a, a, Ot33Doc(Ot33Image(16), open))))));
    }

    /// <summary>
    /// OT 35.7 sentences 4–5: width and height on the svg element act as a scaling — width and height of twice the em: the image must come out
    /// half as large as without them.
    /// </summary>
    [Fact]
    public void Ot33_SvgRootWidthAndHeight_ScaleTheImage()
    {
        var (a, b) = (Ot33Gid('A'), Ot33Gid('B'));
        var twice = 2 * Ot33LiberationFont.Value.UnitsPerEm;
        var open = $"""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="{twice}" height="{twice}">""";
        var color = ColorGlyphs.For(Ot33WithTable("SVG ", Ot33Svg((a, a, Ot33Doc(Ot33Image(16))), (b, b, Ot33Doc(Ot33Image(16), open)))))!;
        Assert.Equal(16, color.Glyph(a)!.Value.Image.Width);
        Assert.Equal(8, color.Glyph(b)!.Value.Image.Width);
    }

    /// <summary>
    /// OT 35.7 sentences 6–9: the viewport is not clipped and other clip/overflow values are ignored — with overflow="hidden" on the svg element
    /// an image twice the em wide, starting left of the origin, comes out whole.
    /// </summary>
    [Fact]
    public void Ot33_SvgOverflowHidden_WholeImageKept()
    {
        var a = Ot33Gid('A');
        var upm = Ot33LiberationFont.Value.UnitsPerEm;
        var open = """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" overflow="hidden">""";
        var image = $"""<image x="{-upm / 4}" y="{-upm}" width="{2 * upm}" height="{upm}" xlink:href="data:image/png;base64,{Convert.ToBase64String(Ot33Png(64, 32))}"/>""";
        var color = ColorGlyphs.For(Ot33WithTable("SVG ", Ot33Svg((a, a, Ot33Doc(image, open)))))!;
        Assert.Equal(32, color.PixelsPerEm);
        var g = color.Glyph(a)!.Value;
        Assert.Equal((64, 32), (g.Image.Width, g.Image.Height));
        Assert.Equal(-8, g.X);
    }

    /// <summary>
    /// OT 35.7 sentences 20–22: GPOS offsets count y upwards and the application converts — a shaped glyph with a YOffset of 20 pixels' worth
    /// (at 128 px per em) stands 20 pixels higher in the composed image than the same glyph without offset.
    /// </summary>
    [Fact]
    public void Ot33_ComposedGlyphWithYOffset_StandsHigher()
    {
        var a = Ot33Gid('A');
        var font = Ot33SvgFont((a, Ot33Image(16)));
        var color = ColorGlyphs.For(font)!;
        Assert.Equal(128, color.PixelsPerEm);
        var offset = 20.0 * font.UnitsPerEm / 128;
        var image = color.Compose([new ShapedGlyph { Glyph = a, XAdvance = 0 }, new ShapedGlyph { Glyph = a, YOffset = offset }], font.UnitsPerEm)!;
        Assert.Equal(36, image.Height);
        // the raised one at the top (rows 0–15), the other at the bottom (rows 20–35); both at the same x
        Assert.Equal(255, image.Pixels[(0 * image.Width) * 4 + 3]);
        Assert.Equal(255, image.Pixels[(35 * image.Width) * 4 + 3]);
        Assert.Equal(0, image.Pixels[(18 * image.Width) * 4 + 3]);
    }

    /// <summary>
    /// OT 35.8 sentences 1–7: animations are optional; a static image comes from not running them — an animate element inside the image (width
    /// from 100 to 200) gives the same image as without it.
    /// </summary>
    [Fact]
    public void Ot33_SvgAnimation_NotRun_StaticImage()
    {
        var a = Ot33Gid('A');
        var expected = ColorGlyphs.For(Ot33SvgFont((a, Ot33Image(16))))!.Glyph(a);
        var animated = Ot33Image(16, close: "><animate attributeName=\"width\" from=\"100\" to=\"200\" dur=\"2s\"/></image>");
        Ot33SameImage(expected, ColorGlyphs.For(Ot33SvgFont((a, animated)))!.Glyph(a));
    }
}
