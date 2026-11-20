using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// A certificate chosen for one signature only (docs/ABLAUFPLAN.md, 9.4.0, step 2; docs/FEHLERGEDAECHTNIS.md, No. 722): two signatures saved at once,
/// one with the default, one naming its own — each is made with its certificate, both valid.
/// </summary>
public sealed class PerSignatureCertificateTests
{
    private static InkElement Signature(double x, SigningCertificateChoice? with) => new()
    {
        Rect = new PdfRectangle(x, 100, x + 200, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann", With: with),
    };

    [Fact]
    public void TwoSignatures_EachWithItsCertificate()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var ownFile = Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pdf-knead-tests")).FullName, $"eigen-{Guid.NewGuid():N}.p12");
        using var own = SigningCertificates.CreateOwn("Erika Mustermann", ownFile);
        var choice = new SigningCertificateChoice(SigningCertificateSource.Own);
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var pages = PageItem.From(doc);
        var path = Path.Combine(Path.GetDirectoryName(ownFile)!, $"zwei-{Guid.NewGuid():N}.pdf");
        SignedSaving.Save(doc, [pages[0].WithElements([Signature(50, null), Signature(300, choice)])], path, new PdfSaveOptions(), false, ca.Signer,
            DateTimeOffset.Now, certificateFor: d => d.With == choice ? own : null);
        using var saved = PdfDocument.Open(File.ReadAllBytes(path));
        File.Delete(path);
        var checks = SignatureVerifier.Verify(saved);
        Assert.Equal(2, checks.Count);
        Assert.All(checks, c => Assert.True(SignatureTexts.IsValid(c), c.Problem));
        Assert.Equal(["Erika Mustermann", "Max Mustermann"], checks.Select(c => c.SignerName).Order());
    }
}
