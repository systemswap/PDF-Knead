using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The name a signer gives and the holder of the certificate (KONZEPT.md, section 5.17): where they differ, the signature shows the holder
/// as well — „Zertifikatsinhaber: …“ in its origin line —, so that nobody reads a name the certificate does not vouch for as the signer's.
/// </summary>
public sealed class CertificateHolderTests
{
    private static readonly Lazy<SigningCertificate> Erika = new(() =>
        SigningCertificates.CreateOwn("Erika Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"inhaber-{Guid.NewGuid():N}.p12")));

    private static readonly DateTimeOffset When = new(2026, 11, 1, 10, 0, 0, TimeSpan.FromHours(1));

    private static string SignedText(string? signer, string? origin)
    {
        var signature = new InkElement
        {
            Rect = new PdfRectangle(100, 100, 300, 180),
            Strokes = [[0, 20, 40, 70, 90, 10, 140, 60, 190, 30]],
            StrokeWidth = 3,
            Color = 0x1B3A8C,
            Digital = new DigitalSigning(origin, Signer: signer),
        };
        using var doc = PdfDocument.Open(TestPdf.Simple(pages: 1).Build());
        using var signed = PdfDocument.Open(PdfSigner.Sign(doc, 0, signature, Erika.Value, When));
        return Core.Pdf.Text.PageText.Create(PageInterpreter.BuildText(signed, signed.Pages[0]), 0).Text;
    }

    [Theory]
    [InlineData("Max Mustermann", "Max Mustermann · 01.11.26 10:00", true)]    // another name: the holder beside it
    [InlineData("Max Mustermann", null, true)]                                   // without an origin line: a line with the holder
    [InlineData("Erika Mustermann", "Erika Mustermann · 01.11.26 10:00", false)]
    [InlineData("mustermann, erika", null, false)]                               // the same words in another order and case
    [InlineData(null, null, false)]                                              // no name given: nothing to tell apart
    public void NameOtherThanTheCertificates_HolderShownBesideIt(string? signer, string? origin, bool shown)
    {
        var text = SignedText(signer, origin);
        Assert.Equal(shown, text.Contains("Zertifikatsinhaber: Erika Mustermann"));
        if (origin is not null) Assert.Contains(origin, text);
    }

    [Theory]
    [InlineData("Max Mustermann", "Max  Mustermann", true)]
    [InlineData("Mustermann, Max", "Max Mustermann", true)]
    [InlineData("Max Mustermann", "Erika Mustermann", false)]
    [InlineData("Max", "Max Mustermann", false)]
    public void SameName_ByItsWords(string a, string b, bool same) => Assert.Equal(same, PdfSigner.SameName(a, b));
}
