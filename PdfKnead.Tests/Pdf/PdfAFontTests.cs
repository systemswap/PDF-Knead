using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The fonts of a PDF/A copy, written anew from what the display draws (KONZEPT.md, section 5.21; docs/PDFA-REGELN.md, 6.2.11) — what the rule
/// cases (<see cref="PdfARuleCases"/>) cannot show by their look alone.
/// </summary>
public sealed class PdfAFontTests
{
    private static (PdfDocument Doc, PdfDictionary Font) WrittenFont(byte[] original, PdfAConformance part = PdfAConformance.A2b)
    {
        using var doc = PdfDocument.Open(original);
        var copy = PdfAExport.Write(doc, PageItem.From(doc), part);
        Assert.True(copy.Conforms, PdfATests.Describe(copy.Violations));
        var written = PdfDocument.Open(copy.Bytes);
        return (written, written.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("F1"))!);
    }

    [Fact]
    public void EmbeddedFontWithTheNameOfAStandardFont_KeepsItsOwnProgram()
    {
        // only a standard font that is not embedded becomes the Liberation font the display draws it with; whether it is embedded is
        // decided by the font as read (the rebuilt one already refers to its new program)
        var (doc, font) = WrittenFont(PdfARuleCases.WithTrueType(f => f.Replace("/BaseFont/LiberationSans", "/BaseFont/Arial", StringComparison.Ordinal))());
        using (doc) Assert.Equal("Arial", font.GetName(PdfName.Get("BaseFont"))?.Value);
    }

    [Fact]
    public void CidWidths_KeepAllTheirDecimals()
    {
        // widths such as 277.83203 add up along a line: rounded, glyphs move
        var a = PdfARuleCases.Gid('A');
        var (doc, font) = WrittenFont(PdfARuleCases.WithCidTrueType(cid: c => c.Replace("/DW 667", $"/DW 667/W[{a}[277.83203 277.83203]]", StringComparison.Ordinal))());
        using (doc)
        {
            var widths = ((PdfDictionary)font.GetArray(PdfName.Get("DescendantFonts"))!.Get(0)).GetArray(PdfName.Get("W"))!;
            var values = Enumerable.Range(0, widths.Count).Select(widths.Get).OfType<PdfArray>().SelectMany(run => Enumerable.Range(0, run.Count).Select(i => run.GetNumber(i)));
            Assert.Contains(277.83203, values);
        }
    }

    [Fact]
    public void CompositeFont_ShowingCidZero_GetsAFreeCid_AndAnEncodingOfItsOwn()
    {
        // CID 0 is .notdef (6.2.11.8): the code gets a CID of its own with the glyph the display drew, in a CMap written for the codes shown
        var (doc, font) = WrittenFont(PdfARuleCases.WithCidTrueType(shown: $"<0000{PdfARuleCases.Gid('B'):X4}>")());
        using (doc)
        {
            var encoding = Assert.IsType<PdfStream>(font.Get(PdfName.Get("Encoding")));
            var cmap = Core.Pdf.Fonts.CMap.Parse(doc.GetStreamData(encoding));
            Assert.NotEqual(0, cmap.ToCid(0, 2));
            Assert.Equal(PdfARuleCases.Gid('B'), cmap.ToCid((uint)PdfARuleCases.Gid('B'), 2));
            Assert.Equal(encoding.Dictionary.GetInteger(PdfName.Get("WMode")) ?? 0, cmap.WMode);
        }
    }

    [Theory]
    [InlineData(PdfAConformance.A2b)]
    [InlineData(PdfAConformance.A4)]
    public void ToUnicode_IsWrittenAnew_WithoutForbiddenValues(PdfAConformance part)
    {
        // the file reads „A“ as U+FEFF U+0041: the copy keeps the letter and drops the byte order mark, at every level
        var (doc, font) = WrittenFont(PdfARuleCases.WithTrueType(f => f.Replace("/FontDescriptor 6 0 R", "/FontDescriptor 6 0 R/ToUnicode 9 0 R", StringComparison.Ordinal),
            more: PdfARuleCases.ByteOrderMarkText())(), part);
        using (doc)
        {
            var map = Core.Pdf.Fonts.CMap.Parse(doc.GetStreamData(font.GetStream(PdfName.Get("ToUnicode"))!));
            Assert.Equal("A", map.ToUnicode(0x41, 1));
            Assert.Equal("B", map.ToUnicode(0x42, 1));
        }
    }
}
