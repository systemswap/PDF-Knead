using System.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Annex F (linearized files) as the PDF/A check reads them.</summary>
public sealed partial class PdfARuleTests
{
    /// <summary>
    /// ISO 32000-2, F.3.3, Table F.1 (/L, sentences 8 to 12; ISO 32000-1, F.2.2): /L is exactly the length of the file, and a file whose /L
    /// differs is no linearized file but an ordinary one — the check of PDF/A-1 does not compare the /ID of the first page's trailer with the
    /// last one there. The file of the case „FileIdLinearized“ with /L one byte too large (as many digits, so that nothing moves) has no such
    /// finding; with the right /L it has.
    /// </summary>
    [Fact]
    public void LinearizationLengthNotTheFileLength_NotReadAsLinearized()
    {
        var id = Catalogue.Single(c => c.Name == "FileIdLinearized").Rule.Id(PdfAConformance.A1b);
        var file = PdfARuleCases.LinearizedAfter("");
        var text = Encoding.Latin1.GetString(file);
        var at = text.IndexOf($"/L {file.Length:D10}", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var wrong = (byte[])file.Clone();
        Encoding.ASCII.GetBytes($"{file.Length + 1:D10}").CopyTo(wrong, at + 3);
        using (var right = Core.Pdf.Document.PdfDocument.Open(file))
            Assert.Contains(PdfACheck.Check(right, PdfAConformance.A1b), v => v.Id == id);
        using var ordinary = Core.Pdf.Document.PdfDocument.Open(wrong);
        Assert.DoesNotContain(PdfACheck.Check(ordinary, PdfAConformance.A1b), v => v.Id == id);
    }
}
