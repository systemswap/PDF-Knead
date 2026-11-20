using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>File → New and blank pages in the chosen format.</summary>
public sealed class NewDocumentTests
{
    [Theory]
    [InlineData("A0", false, 841, 1189)]
    [InlineData("A3", true, 420, 297)]
    [InlineData("A4", false, 210, 297)]
    [InlineData("A6", true, 148, 105)]
    [InlineData("Letter", false, 215.9, 279.4)]
    [InlineData("Legal", true, 355.6, 215.9)]
    public void Formats_HaveTheirSize(string format, bool landscape, double width, double height)
    {
        var setup = new PageSetup(format, landscape);
        Assert.Equal((width, height), setup.Millimeters);
        var (w, h) = setup.Points;
        Assert.Equal((format, landscape), PageFormat.Detect(w, h));
    }

    [Fact]
    public void CustomSize_StaysAsEnteredWithinLimits()
    {
        Assert.Equal((300, 200), new PageSetup(PageSetup.Custom, CustomWidth: 300, CustomHeight: 200).Millimeters);
        Assert.Equal((10, 5000), new PageSetup(PageSetup.Custom, CustomWidth: 0, CustomHeight: 1e9).Millimeters);
        Assert.Equal((210, 210), new PageSetup(PageSetup.Custom, CustomWidth: double.NaN, CustomHeight: double.PositiveInfinity).Millimeters);
        Assert.Equal(1, new PageSetup(Count: -5).Pages);
        Assert.Equal(BlankPage.MaxCount, new PageSetup(Count: int.MaxValue).Pages);
        // Old or corrupted settings
        Assert.Equal("A4", new PageSetup("B7").Checked().Format);
    }

    [Fact]
    public void MenuShortForms_Parse()
    {
        Assert.Equal(new PageSetup("A4"), PageSetup.Parse("A4"));
        Assert.Equal(new PageSetup("A3", true), PageSetup.Parse("A3 quer"));
        Assert.Null(PageSetup.Parse("A4 hoch"));
        Assert.Null(PageSetup.Parse("B7"));
        Assert.Null(PageSetup.Parse(""));
        Assert.Null(PageSetup.Parse(null));
    }

    [Fact]
    public void BlankDocument_HasThePagesAndSavesAsPdf17()
    {
        var (w, h) = new PageSetup("A3", true).Points;
        using var blank = BlankPage.CreateDocument(w, h, 3);
        Assert.Equal("1.7", blank.Version);
        Assert.Equal(3, blank.PageCount);
        Assert.False(blank.WasRepaired);
        Assert.All(blank.Pages, p => Assert.Equal(("A3", true), PageFormat.Detect(p.Width, p.Height)));

        using var output = new MemoryStream();
        PdfWriter.Write(blank, PageItem.From(blank), output);
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.False(saved.WasRepaired);
        Assert.Equal("1.7", saved.Version);
        Assert.Equal(3, saved.PageCount);
        Assert.All(saved.Pages, p => Assert.Equal(("A3", true), PageFormat.Detect(p.Width, p.Height)));
    }

    [Fact]
    public void BlankDocument_CountIsLimited()
    {
        using var many = BlankPage.CreateDocument(595.28, 841.89, 100_000);
        Assert.Equal(BlankPage.MaxCount, many.PageCount);
        Assert.Equal(BlankPage.MaxCount, many.Pages.Select(p => p.Dictionary).Distinct().Count());
        using var none = BlankPage.CreateDocument(595.28, 841.89, 0);
        Assert.Equal(1, none.PageCount);
    }
}
