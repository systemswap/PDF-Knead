using System.Security.Cryptography.X509Certificates;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Signatures;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Core.Platform;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// The validation report (docs/ABLAUFPLAN.md, 9.3.5, step 4; ETSI EN 319 102-1, 5.1.3; docs/FEHLERGEDAECHTNIS.md, No. 717): per signature verdict,
/// main indication with sub-indication, process, time of the check and „applies to“ with its source; the implicit constraints; the footer
/// „technical check, no legal advice“; the HTML page self-contained and escaped; the command line the same.
/// </summary>
public sealed class ValidationReportTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 11, 9, 30, 0, TimeSpan.Zero);

    private static InkElement Signature() => new()
    {
        Rect = new PdfRectangle(50, 100, 250, 180), Strokes = [[0, 20, 40, 70, 90, 10]], StrokeWidth = 3,
        Digital = new DigitalSigning("Max Mustermann"),
    };

    /// <summary>Signed by the test authority with its revocation status (valid), checked against its root.</summary>
    private static (byte[] File, X509Certificate2 Root) Valid(LongTermValidationTests.TestCa ca)
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var pages = PageItem.From(doc);
        var path = Path.Combine(Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pdf-knead-tests")).FullName, $"bericht-{Guid.NewGuid():N}.pdf");
        SignedSaving.Save(doc, [pages[0].WithElements([Signature()])], path, new PdfSaveOptions(), false, ca.Signer, DateTimeOffset.Now, revocation: ca);
        var file = File.ReadAllBytes(path);
        File.Delete(path);
        return (file, ca.Root);
    }

    private static IReadOnlyList<SignatureCheck> Checks(byte[] file, X509Certificate2 root)
    {
        using var doc = PdfDocument.Open(file);
        return SignatureVerifier.Verify(doc, null, [X509CertificateLoader.LoadCertificate(root.RawData)], CancellationToken.None);
    }

    /// <summary>
    /// The cases of the table, each from a file: valid — TOTAL-PASSED; changed after signing — TOTAL-FAILED/CUSTOM; a byte changed —
    /// TOTAL-FAILED/HASH_FAILURE; issued by an authority the check does not know — INDETERMINATE/NO_CERTIFICATE_CHAIN_FOUND. The report names
    /// each, with the verdict.
    /// </summary>
    [Fact]
    public void EachOutcome_MapsToMainAndSubIndication()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var (valid, root) = Valid(ca);
        byte[] changed;
        using (var doc = PdfDocument.Open(valid))
        {
            var update = new IncrementalUpdate(doc);
            update.Replace(((PdfReference)doc.Pages[0].Dictionary.GetRaw(PdfName.Contents)!).Id,
                TestPdf.FlateStream("BT /F1 24 Tf 72 700 Td (Erika Mustermann) Tj ET"u8.ToArray()));
            using var output = new MemoryStream();
            update.WriteTo(output);
            changed = output.ToArray();
        }
        var broken = (byte[])valid.Clone();
        broken[System.Text.Encoding.ASCII.GetString(broken).IndexOf("stream", StringComparison.Ordinal) + 12] ^= 0x01;
        using var unknownRoot = new LongTermValidationTests.TestCa();
        foreach (var (file, trusted, verdict, indication) in new (byte[], X509Certificate2, string, string)[]
                 {
                     (valid, root, Tr.T("Gültig"), "TOTAL-PASSED"),
                     (changed, root, Tr.T("Nach dem Unterschreiben geändert"), "TOTAL-FAILED/CUSTOM"),
                     (broken, root, Tr.T("Ungültig"), "TOTAL-FAILED/HASH_FAILURE"),
                     (valid, unknownRoot.Root, Tr.T("Nicht bestätigt"), "INDETERMINATE/NO_CERTIFICATE_CHAIN_FOUND"),
                 })
        {
            var report = ValidationReport.ToText(Checks(file, trusted), "muster.pdf", "1.0", At);
            Assert.Contains($"{Tr.T("Urteil")}: {verdict}", report);
            Assert.True(report.Contains($"{Tr.T("Ergebnis nach EN 319 102-1")}: {indication}", StringComparison.Ordinal), report);
        }
    }

    /// <summary>The text report: process, time of the check (UTC), „applies to“, the constraints and the footer; the signer's one line.</summary>
    [Fact]
    public void TextReport_ContainsProcessTimeConstraintsAndDisclaimer()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var (valid, root) = Valid(ca);
        var checks = Checks(valid, root);
        var report = ValidationReport.ToText(checks, "muster.pdf", "1.2.3", At);
        Assert.StartsWith(Tr.T("Prüfbericht digitaler Signaturen"), report);
        Assert.Contains($"{Tr.T("Datei")}: muster.pdf", report);
        Assert.Contains($"{Tr.T("Prüfer")}: {Tr.T("PDF-Knead {0}, lokal, ohne Netzverbindung", "1.2.3")}", report);
        Assert.Contains($"{Tr.T("Prozess")}: {Tr.T("Grundprüfung (EN 319 102-1, 5.3)")}", report);
        Assert.Contains($"{Tr.T("Prüfzeit")}: 2026-11-11 09:30:00 UTC", report);
        Assert.Contains($"{Tr.T("Gilt für")}: {Tr.T("{0} (Zeitpunkt der Prüfung)", "2026-11-11 09:30:00 UTC")}", report);
        Assert.Contains(Tr.T("Signatur {0} von {1}: {2}", 1, 1, SignatureTexts.OneLine(checks[0])), report);
        Assert.Contains(Tr.T("Prüfvorgaben"), report);
        Assert.Contains(Tr.T("Implizite Prüfvorgaben von PDF-Knead {0}; keine Signaturrichtlinie ausgewertet", "1.2.3"), report);
        Assert.EndsWith(Tr.T("Technische Prüfung, keine Rechtsberatung. Erstellt von PDF-Knead auf diesem Rechner; nichts wurde übertragen.") + Environment.NewLine, report);
        Assert.DoesNotContain("rechtsgültig", report, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The details a record needs (docs/FEHLERGEDAECHTNIS.md, No. 723): the signed byte ranges, the digest of the signed bytes with its method,
    /// the path link by link with validity and fingerprint, the root marked as the anchor.
    /// </summary>
    [Fact]
    public void TextReport_ByteRangesDigestAndChain()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var (valid, root) = Valid(ca);
        var check = Assert.Single(Checks(valid, root));
        var report = ValidationReport.ToText([check], "muster.pdf", "1.2.3", At);
        Assert.Equal(4, check.ByteRange.Count);
        Assert.Contains($"{Tr.T("Byte-Bereiche")}: [0 {check.ByteRange[1]} {check.ByteRange[2]} {check.ByteRange[3]}]", report);
        Assert.StartsWith("SHA-256: ", check.SignedDigest);
        Assert.Contains($"{Tr.T("Prüfsumme der unterschriebenen Bytes")}: {check.SignedDigest}", report);
        Assert.True(check.Chain.Count >= 2, string.Join(" | ", check.Chain.Select(l => l.Name)));
        Assert.Equal("Max Mustermann", check.Chain[0].Name);
        Assert.Contains(Tr.T("Glied {0}", check.Chain.Count) + ": ", report);
        Assert.Contains(" · " + Tr.T("Vertrauensanker") + Environment.NewLine, report);
    }

    /// <summary>
    /// The revocation status of each certificate of the signer's chain with the answer it rests on (ETSI TS 119 102-2; docs/FEHLERGEDAECHTNIS.md,
    /// No. 725): the signer „not revoked“ according to the OCSP response of the test authority, with its times; the root needs none.
    /// </summary>
    [Fact]
    public void TextReport_RevocationStatusPerLink()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var (valid, root) = Valid(ca);
        var check = Assert.Single(Checks(valid, root));
        var links = check.Revocation!.Links;
        Assert.Equal([RevocationInfo.LinkKind.Good, RevocationInfo.LinkKind.Root], links.Select(l => l.Kind));
        Assert.Equal(("Max Mustermann", "OCSP"), (links[0].Name, links[0].Source));
        Assert.NotNull(links[0].ThisUpdate);
        var report = ValidationReport.ToText([check], "muster.pdf", "1.2.3", At);
        Assert.Contains("– Max Mustermann: " + Tr.T("nicht gesperrt") + " ", report);
        Assert.Contains(Tr.T("OCSP-Antwort"), report);
        Assert.Contains(": " + Tr.T("Wurzel, braucht keine Sperrauskunft"), report);
    }

    /// <summary>The HTML page: escaped (a field name with markup stays text), no script, no outside resource.</summary>
    [Fact]
    public void HtmlReport_SelfContainedAndEscaped()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var (valid, root) = Valid(ca);
        var checks = Checks(valid, root).Select(c => c with { FieldName = "<b>Feld</b>" }).ToList();
        var html = ValidationReport.ToHtml(checks, "muster <1>.pdf", "1.2.3", At);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("&lt;b&gt;Feld&lt;/b&gt;", html);
        Assert.Contains("muster &lt;1&gt;.pdf", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>pdf-knead signaturen &lt;datei&gt; --bericht text</c>: the same report for a file.</summary>
    [Fact]
    public void Signaturen_BerichtText()
    {
        using var ca = new LongTermValidationTests.TestCa();
        var (valid, _) = Valid(ca);
        var path = TestFiles.Write(valid, "bericht-cli.pdf");
        var text = PdfKnead.Cli.SignatureList.Report(path, null, html: false);
        Assert.StartsWith(Tr.T("Prüfbericht digitaler Signaturen"), text);
        Assert.Contains($"{Tr.T("Datei")}: bericht-cli.pdf", text);
        Assert.StartsWith("<!doctype html>", PdfKnead.Cli.SignatureList.Report(path, null, html: true));
    }
}
