using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Platform;
using PdfKnead.Core.Platform.Pkcs11;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The test matrix of the signing analysis (docs/PRUEFUNGEN.md, „Konformität der Signaturen“): writes signed files of every level into a folder
/// for the reference validator of the European Commission (DSS) — only when PDFKNEAD_KONFORMITAET names the folder; with PDFKNEAD_ZEITSTEMPEL
/// the levels with time stamps use that service and the real revocation status. Without the variables nothing happens (no network in tests).
/// </summary>
public sealed class SigningConformanceFiles
{
    private static InkElement Signature(double x, DigitalSigning? digital = null) => new()
    {
        Rect = new PdfRectangle(x, 100, x + 200, 180),
        Strokes = [[0, 20, 40, 70, 90, 10, 140, 60]],
        StrokeWidth = 3,
        Digital = digital ?? new DigitalSigning("Max Mustermann"),
    };

    private static void Write(string folder, string name, SigningCertificate certificate, ITimestampAuthority? timestamps, IRevocationSource? revocation,
        bool archive, byte[]? original = null, DigitalSigning? digital = null, double x = 50, bool append = false)
    {
        using var doc = PdfDocument.Open(original ?? TestPdf.Simple(1).Build());
        var pages = PageItem.From(doc);
        SignedSaving.Save(doc, [pages[0].WithElements([.. pages[0].Elements, Signature(x, digital)])], Path.Combine(folder, name), new PdfSaveOptions(),
            append, certificate, DateTimeOffset.Now, timestamps: timestamps, revocation: revocation, archive: archive);
    }

    [Fact]
    public void Matrix_WhenGiven()
    {
        if (Environment.GetEnvironmentVariable("PDFKNEAD_KONFORMITAET") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        using var own = SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(folder, "eigen.p12"));
        ITimestampAuthority? real = Environment.GetEnvironmentVariable("PDFKNEAD_ZEITSTEMPEL") is { Length: > 0 } server && HttpTimestampAuthority.TryParse(server, out var address)
            ? new HttpTimestampAuthority(address) : null;
        var online = new HttpRevocationSource();

        Write(folder, "01-eigen-B-B.pdf", own, null, null, false);
        if (real is not null)
        {
            Write(folder, "02-eigen-B-T.pdf", own, real, null, false);
            Write(folder, "03-eigen-B-LT.pdf", own, real, online, false);
            Write(folder, "04-eigen-B-LTA.pdf", own, real, online, true);
            Write(folder, "05-eigen-zertifiziert-B-LTA.pdf", own, real, online, true,
                digital: new DigitalSigning("Max Mustermann", Certify: CertificationLevel.FormsAndSigning, LockFields: true));
            // a second signature appended, then the archive renewed
            Write(folder, "06-zwei-signaturen.pdf", own, real, online, true, File.ReadAllBytes(Path.Combine(folder, "04-eigen-B-LTA.pdf")), x: 300, append: true);
            using (var two = PdfDocument.Open(File.ReadAllBytes(Path.Combine(folder, "06-zwei-signaturen.pdf"))))
                SignedSaving.AddArchiveTimestamp(two, Path.Combine(folder, "07-zwei-signaturen-erneuert.pdf"), real, online);
        }
        // a certification authority with OCSP and CRL (stand-ins): the structure of B-LT with an issued certificate
        using (var ca = new LongTermValidationTests.TestCa())
        using (var authority = new SignatureTimestampTests.TestAuthority())
            Write(folder, "08-ausgestellt-B-LTA.pdf", ca.Signer, authority, ca, true);
        // PDF/A-2b signed at every level (docs/ABLAUFPLAN.md, 9.3.4, step 5): for the PDF/A reference validator — does the file stay PDF/A
        // after signing, validation data and document time stamp?
        byte[] pdfa;
        using (var plain = PdfDocument.Open(TestPdf.Simple(1).Build()))
        using (var output = new MemoryStream())
        {
            PdfWriter.Write(plain, PageItem.From(plain), output, new PdfSaveOptions { PdfA = PdfAConformance.A2b });
            pdfa = output.ToArray();
        }
        File.WriteAllBytes(Path.Combine(folder, "10-pdfa2b-unsigniert.pdf"), pdfa);
        using (var ca = new LongTermValidationTests.TestCa())
        using (var authority = new SignatureTimestampTests.TestAuthority())
        {
            Write(folder, "11-pdfa2b-B-B.pdf", ca.Signer, null, null, false, pdfa, append: true);
            Write(folder, "12-pdfa2b-B-T.pdf", ca.Signer, authority, null, false, pdfa, append: true);
            Write(folder, "13-pdfa2b-B-LT.pdf", ca.Signer, authority, ca, false, pdfa, append: true);
            Write(folder, "14-pdfa2b-B-LTA.pdf", ca.Signer, authority, ca, true, pdfa, append: true);
        }
        // ECDSA from the test card (SoftHSM)
        if (SmartCardTests.Available is { } tokens)
        {
            using var card = SmartCards.Open(tokens.Module, SigningCertificate.FingerprintOf(tokens.Ecdsa), SmartCardTests.Pin);
            Write(folder, "09-karte-ecdsa-B-T.pdf", card, real, null, false);
        }
    }
}
