using System.Text;
using PdfKnead.Core.Fonts;
using PdfKnead.Core.Fonts.Layout;

namespace PdfKnead.Tests.Fonts;

/// <summary>Tests for colour glyph images from 'sbix', 'CBDT'/'CBLC' and 'SVG ' tables (generated fonts, <see cref="ColorFontBuilder"/>).</summary>
public sealed class ColorGlyphsTests
{
    private static readonly Lazy<Dictionary<ColorFontBuilder.Kind, OpenTypeFont>> Fonts = new(() =>
        Enum.GetValues<ColorFontBuilder.Kind>().ToDictionary(k => k, k => OpenTypeFont.Parse(ColorFontBuilder.Build(k, Ppem(k)))));

    private static int Ppem(ColorFontBuilder.Kind kind) => kind switch
    {
        ColorFontBuilder.Kind.Sbix => 128,
        ColorFontBuilder.Kind.Cbdt or ColorFontBuilder.Kind.CbdtSparse => 96,
        _ => 200,
    };

    [Theory]
    [InlineData(ColorFontBuilder.Kind.Sbix)]
    [InlineData(ColorFontBuilder.Kind.Cbdt)]
    [InlineData(ColorFontBuilder.Kind.CbdtSparse)]
    [InlineData(ColorFontBuilder.Kind.Svg)]
    [InlineData(ColorFontBuilder.Kind.SvgShared)]
    public void EveryFormat_GivesTheImagesWhereTheOutlinesAre(ColorFontBuilder.Kind kind)
    {
        var font = Fonts.Value[kind];
        var color = ColorGlyphs.For(font);
        Assert.NotNull(color);
        Assert.Equal(Ppem(kind), color.PixelsPerEm);
        var expected = ColorFontBuilder.Images(font, color.PixelsPerEm).ToDictionary(i => i.Glyph);
        foreach (var c in "AMaxß7")
        {
            var glyph = font.GlyphForUnicode(c);
            var image = color.Glyph(glyph);
            Assert.True(image is not null, $"{kind}: kein Bild für {c}");
            var e = expected[glyph];
            // SVG positions pass through font units with two decimals: at most one pixel off
            Assert.InRange(image.Value.X, e.Left - 1, e.Left + 1);
            Assert.InRange(image.Value.Y, e.Bottom - 1, e.Bottom + 1);
            Assert.InRange(image.Value.Image.Width, e.Width - 1, e.Width + 1);
            Assert.InRange(image.Value.Image.Height, e.Height - 1, e.Height + 1);
            Assert.Contains(Enumerable.Range(0, image.Value.Image.Width * image.Value.Image.Height), i => image.Value.Image.Pixels[i * 4 + 3] == 255);
        }
        Assert.Null(color.Glyph(font.GlyphForUnicode('€')));   // a glyph without an image
        Assert.Null(color.Glyph(-1));
        Assert.Null(color.Glyph(font.GlyphCount));
        var name = color.Compose(TextShaper.For(font).Shape("Max Mustermann"), font.UnitsPerEm);
        Assert.NotNull(name);
        Assert.InRange(name.Width / (double)name.Height, 3, 12);
    }

    [Fact]
    public void SvgImages_AreScaledToAtMost256PixelsPerEm()
    {
        var font = OpenTypeFont.Parse(ColorFontBuilder.Build(ColorFontBuilder.Kind.Svg, 512));
        var color = ColorGlyphs.For(font)!;
        Assert.Equal(SvgPngImages.MaxPixelsPerEm, color.PixelsPerEm);
        var m = font.GlyphForUnicode('M');
        var expected = ColorFontBuilder.Images(font, SvgPngImages.MaxPixelsPerEm).Single(i => i.Glyph == m);
        Assert.InRange(color.Glyph(m)!.Value.Image.Height, expected.Height - 2, expected.Height + 2);
    }

    [Fact]
    public void SeveralTables_TheSharpestIsUsed()
    {
        var cbdt = ColorFontBuilder.Build(ColorFontBuilder.Kind.Cbdt, 96);
        var svg = OpenTypeFont.Parse(ColorFontBuilder.Build(ColorFontBuilder.Kind.Svg, 200));
        var both = ColorFontBuilder.WithTables(cbdt, new Dictionary<string, byte[]?> { ["SVG "] = svg.TableData("SVG ").ToArray() });
        Assert.Equal(200, ColorGlyphs.For(OpenTypeFont.Parse(both))!.PixelsPerEm);
    }

    [Fact]
    public void SvgWithVectorContent_IsNotUsedAsImages()
    {
        // Glyph documents that draw shapes besides (or instead of) one PNG cannot be shown as a single image
        var svg = OpenTypeFont.Parse(ColorFontBuilder.Build(ColorFontBuilder.Kind.SvgShared, 64)).TableData("SVG ").ToArray();
        var start = svg.AsSpan().IndexOf("<svg"u8);
        var document = Encoding.UTF8.GetString(svg, start, svg.Length - start).Replace("<g id=", "<path d=\"M0 0L10 10\"/><g id=", StringComparison.Ordinal);
        var withPath = svg[..start].Concat(Encoding.UTF8.GetBytes(document)).ToArray();
        // One document: its length in the record (bytes 20 to 23 of the table) grows with the inserted text
        var length = withPath.Length - start;
        (withPath[20], withPath[21], withPath[22], withPath[23]) = ((byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length);
        OpenTypeFont With(byte[] table) =>
            OpenTypeFont.Parse(ColorFontBuilder.WithTables(EmbeddedFonts.LoadHandwriting("PatrickHand-Regular.ttf"), new Dictionary<string, byte[]?> { ["SVG "] = table }));
        Assert.NotNull(ColorGlyphs.For(With(svg)));
        Assert.Null(ColorGlyphs.For(With(withPath)));
    }

    [Theory]
    [InlineData(ColorFontBuilder.Kind.Cbdt, "CBLC")]
    [InlineData(ColorFontBuilder.Kind.Cbdt, "CBDT")]
    [InlineData(ColorFontBuilder.Kind.CbdtSparse, "CBLC")]
    [InlineData(ColorFontBuilder.Kind.Svg, "SVG ")]
    [InlineData(ColorFontBuilder.Kind.SvgShared, "SVG ")]
    [InlineData(ColorFontBuilder.Kind.Sbix, "sbix")]
    public void DamagedTables_NeverThrow(ColorFontBuilder.Kind kind, string tag)
    {
        // Prepared fonts: random bytes in the bitmap tables; images may go missing, nothing may throw or read outside the file
        var original = ColorFontBuilder.Build(kind, 48);
        var table = OpenTypeFont.Parse(original).Table(tag)!.Value;
        var random = new Random(7);
        for (var round = 0; round < 300; round++)
        {
            var data = original.ToArray();
            var changes = random.Next(1, 12);
            for (var k = 0; k < changes; k++)
            {
                // Mostly the structures at the start (header, records, offsets), sometimes anywhere
                var span = random.Next(4) == 0 ? table.Length : Math.Min(table.Length, 256);
                data[table.Offset + random.Next(span)] = (byte)random.Next(256);
            }
            var font = OpenTypeFont.Parse(data);
            if (ColorGlyphs.For(font) is not { } color) continue;
            for (var g = 0; g < font.GlyphCount; g++) color.Glyph(g);
            color.Compose(TextShaper.For(font).Shape("Max"), font.UnitsPerEm);
        }
    }
}
