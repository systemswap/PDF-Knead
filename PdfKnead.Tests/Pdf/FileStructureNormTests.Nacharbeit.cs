using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Findings of the sentence-by-sentence assignment of M3 and M5 (23.11.2026), fixed and tested here.</summary>
public sealed partial class FileStructureNormTests
{
    /// <summary>
    /// ISO 32000-2, I.2 (and 7.12.5 for BaseVersion): a version is two integers, major and minor — „1.10“ comes after „1.9“ and is no newer
    /// version than 2.0; before both were compared as decimal fractions (M3, Annex I, 23.11.2026). The header, the catalogue's /Version, the
    /// newest of merged files and the warning about versions above 2.0 all use this comparison.
    /// </summary>
    [Theory]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("2.0", "1.10", 1)]
    [InlineData("1.7", "1.7", 0)]
    [InlineData("1.4", "1.10", -1)]
    [InlineData("10.0", "9.9", 1)]
    public void Versions_TwoIntegers_ComparedAsSuch(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(PdfWriter.CompareVersions(a, b)));
        Assert.Equal(-sign, Math.Sign(PdfWriter.CompareVersions(b, a)));
    }

    /// <summary>I.2: the newest of several versions, and which are newer than 2.0 — by major and minor.</summary>
    [Fact]
    public void Versions_NewestAndNewerThanKnown_ByMajorAndMinor()
    {
        Assert.Equal("1.10", PdfWriter.NewestVersion(["1.9", "1.10", "1.4"]));
        Assert.True(PdfDocument.NewerThanKnown("2.1"));
        Assert.True(PdfDocument.NewerThanKnown("10.0"));
        Assert.False(PdfDocument.NewerThanKnown("2.0"));
        Assert.False(PdfDocument.NewerThanKnown("1.10"));
        Assert.False(PdfDocument.NewerThanKnown("zwei"));
    }

    /// <summary>
    /// I.2 sentence 12 and I.3: a reader tells when it meets a feature it cannot handle — a page whose content stream names an unknown filter is
    /// shown empty and the document's problems say so; before it stayed empty without a word (M3, Annex I, 23.11.2026). A readable page notes
    /// nothing.
    /// </summary>
    [Fact]
    public void ContentWithUnknownFilter_ShownEmpty_ProblemNoted()
    {
        static byte[] File(string filter) => Encoding.ASCII.GetBytes(
            "%PDF-1.7\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n" +
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>\nendobj\n" +
            $"4 0 obj\n<< {filter}/Length 18 >>\nstream\n0 0 50 50 re f\n   \nendstream\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");
        using var unknown = PdfDocument.Open(File("/Filter /Unbekannt "));
        Assert.Equal(0, Core.Pdf.Content.PageInterpreter.Build(unknown, unknown.Pages[0]).Count);
        Assert.Contains(unknown.Problems, p => p.Contains("lässt sich nicht lesen", StringComparison.Ordinal));
        using var plain = PdfDocument.Open(File(""));
        Assert.NotEqual(0, Core.Pdf.Content.PageInterpreter.Build(plain, plain.Pages[0]).Count);
        Assert.DoesNotContain(plain.Problems, p => p.Contains("lässt sich nicht lesen", StringComparison.Ordinal));
    }
}
