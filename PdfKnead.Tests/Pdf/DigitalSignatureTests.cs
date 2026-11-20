using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Finding digital signatures and telling intact ones from those a rewrite already broke (KONZEPT.md, section 5.17, S0).</summary>
public sealed class DigitalSignatureTests
{
    [Fact]
    public void FittingByteRange_IsIntact()
    {
        using var doc = PdfDocument.Open(SignedTestPdf.Build());
        var signature = Assert.Single(DigitalSignatures.Find(doc));
        Assert.True(signature.Intact);
        Assert.Equal("Unterschrift", signature.FieldName);
        Assert.Equal(1, DigitalSignatures.CountIntact(doc));
    }

    [Fact]
    public void AfterFullRewrite_SignatureStaysButIsBroken()
    {
        using var original = PdfDocument.Open(SignedTestPdf.Build());
        using var output = new MemoryStream();
        PdfWriter.Write(original, output, new PdfSaveOptions());
        using var rewritten = PdfDocument.Open(output.ToArray());
        var signature = Assert.Single(DigitalSignatures.Find(rewritten));
        Assert.False(signature.Intact);
        Assert.Equal(0, DigitalSignatures.CountIntact(rewritten));
    }

    /// <summary>Prepared /ByteRange values never throw and never count as intact.</summary>
    [Theory]
    [InlineData(new long[] { 0, 10, 5, 10 })]                    // second range before the first ends
    [InlineData(new long[] { 1, 10, 20, 10 })]                   // does not start at 0
    [InlineData(new long[] { 0, 10, 20, 999_999_999 })]          // past the end of the file
    [InlineData(new long[] { 0, 10, 20, long.MaxValue })]        // overflow
    [InlineData(new long[] { 0, 0, 0, 0 })]
    public void PreparedByteRange_NotIntact(long[] values)
    {
        using var doc = PdfDocument.Open(SignedTestPdf.Build([.. values.Select(v => (PdfObject)PdfInteger.Of(v))]));
        Assert.Equal(0, DigitalSignatures.CountIntact(doc));
    }

    [Fact]
    public void ByteRangeWithWrongShape_IsIgnored()
    {
        using var three = PdfDocument.Open(SignedTestPdf.Build([PdfInteger.Of(0), PdfInteger.Of(10), PdfInteger.Of(20)]));
        Assert.Empty(DigitalSignatures.Find(three));
        using var negative = PdfDocument.Open(SignedTestPdf.Build([PdfInteger.Of(0), PdfInteger.Of(-10), PdfInteger.Of(20), PdfInteger.Of(5)]));
        Assert.Empty(DigitalSignatures.Find(negative));
        using var real = PdfDocument.Open(SignedTestPdf.Build([PdfInteger.Of(0), new PdfReal(10.5), PdfInteger.Of(20), PdfInteger.Of(5)]));
        Assert.Empty(DigitalSignatures.Find(real));
    }
}
