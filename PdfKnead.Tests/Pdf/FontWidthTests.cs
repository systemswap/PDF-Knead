using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Fonts;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>Widths of CID fonts (W, DW, W2, DW2) stored as ranges instead of being expanded.</summary>
public sealed class FontWidthTests
{
    [Fact]
    public void RangeMap_FindsRanges_AndFirstWinsOnOverlap()
    {
        var map = new RangeMap<int>([(100, 199, 2), (0, 9, 1), (1000, 65535, 3)]);
        Assert.True(map.TryGet(0, out var v) && v == 1);
        Assert.True(map.TryGet(150, out v) && v == 2);
        Assert.True(map.TryGet(65535, out v) && v == 3);
        Assert.False(map.TryGet(10, out _));
        Assert.False(map.TryGet(999, out _));

        var overlapping = new RangeMap<int>([(10, 50, 1), (0, 100, 2)]);
        Assert.True(overlapping.TryGet(20, out v) && v == 1);   // as in the file: the first matching range wins
        Assert.True(overlapping.TryGet(70, out v) && v == 2);
    }

    private static PdfDictionary SimpleFont(string baseFont, string subtype = "Type1") => TestPdf.Dict(("Type", TestPdf.N("Font")),
        ("Subtype", TestPdf.N(subtype)), ("BaseFont", TestPdf.N(baseFont)), ("Encoding", TestPdf.N("WinAnsiEncoding")));

    /// <summary>
    /// A standard 14 font that is not embedded and has no /Widths is set with the standard metrics, in whole thousandths of the em, as other
    /// readers set it — not with the fractions of the bundled substitute (Liberation: 277.83 for 278). Those moved the glyphs along a line
    /// and, written into a copy as PDF 2.0, made other readers draw the copy differently from the original (docs/FEHLERGEDAECHTNIS.md, No. 884).
    /// </summary>
    [Theory]
    [InlineData("Helvetica", ' ', 278)]
    [InlineData("Helvetica", 'A', 667)]
    [InlineData("Helvetica", 'W', 944)]
    [InlineData("Helvetica-Bold", 'A', 722)]
    [InlineData("ABCDEF+Helvetica-Oblique", 'e', 556)]
    [InlineData("Times-Roman", 'a', 444)]
    [InlineData("Times-Roman", 'W', 944)]
    [InlineData("Times-Bold", 'A', 722)]
    [InlineData("Courier", 'i', 600)]
    public void NonEmbeddedStandardFont_SetsTheStandardWidths(string baseFont, char code, int width)
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        var font = PdfFont.Get(SimpleFont(baseFont), doc);
        Assert.Equal(width / 1000.0, font.Glyph(code, 1).Width, 12);
    }

    /// <summary>Arial is no standard font: its own widths have fractions, and the bundled substitute has them exactly (569/2048 for the space).</summary>
    [Fact]
    public void NonEmbeddedArial_KeepsItsOwnWidths()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        var font = PdfFont.Get(SimpleFont("Arial", "TrueType"), doc);
        Assert.Equal(569.0 / 2048, font.Glyph(' ', 1).Width, 12);
    }

    [Fact]
    public void CidFont_UsesListsRangesAndDefaults()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        var w = new PdfArray([
            PdfInteger.Of(10), new PdfArray([PdfInteger.Of(100), PdfInteger.Of(200)]),     // CID 10, 11
            PdfInteger.Of(1000), PdfInteger.Of(60000), PdfInteger.Of(500),                // large range
        ]);
        var w2 = new PdfArray([
            PdfInteger.Of(1), PdfInteger.Of(65535), PdfInteger.Of(-900), PdfInteger.Of(250), PdfInteger.Of(800),
        ]);
        var font = PdfFont.Get(Type0("Identity-V", w, w2), doc);
        Assert.True(font.Vertical);
        Assert.Equal(0.1, font.Glyph(10, 2).Width, 6);
        Assert.Equal(0.2, font.Glyph(11, 2).Width, 6);
        Assert.Equal(0.5, font.Glyph(40000, 2).Width, 6);
        Assert.Equal(0.3, font.Glyph(12, 2).Width, 6);                 // DW
        var g = font.Glyph(5000, 2);
        Assert.Equal(-0.9, g.VerticalAdvance, 6);
        Assert.Equal(0.25, g.OriginX, 6);
        Assert.Equal(0.8, g.OriginY, 6);
        var d = font.Glyph(0, 2);                                      // outside W2: DW2
        Assert.Equal(-1.0, d.VerticalAdvance, 6);
        Assert.Equal(0.88, d.OriginY, 6);
    }

    private static PdfDictionary Type0(string encoding, PdfArray w, PdfArray w2) =>
        TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type0")), ("BaseFont", TestPdf.N("Test")),
            ("Encoding", TestPdf.N(encoding)),
            ("DescendantFonts", new PdfArray([TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("CIDFontType0")),
                ("BaseFont", TestPdf.N("Test")), ("DW", PdfInteger.Of(300)), ("W", w), ("W2", w2))])));
}
