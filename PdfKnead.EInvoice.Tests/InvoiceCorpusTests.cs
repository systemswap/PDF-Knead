using PdfKnead.EInvoice;
using PdfKnead.Tests;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// The free test collections of e-invoices in <c>TestPDFs/erechnung/</c> (not in the repository; tools/pruefer/LIESMICH.md says where they
/// come from): every PDF opens, every embedded XML and loose XML is read or refused with a reason — never with another exception — and lands in
/// the syntax and profile it declares. Only the one known truncated sample may be refused; the test suite of XRechnung is XRechnung throughout.
/// </summary>
public sealed class InvoiceCorpusTests
{
    /// <summary>Samples of the collections that are damaged on purpose or by accident (checked by hand, 13.11.2026).</summary>
    private static readonly HashSet<string> KnownDamaged = new(StringComparer.Ordinal)
    {
        "con-ritenuta-acconto-e-cassa-previdenziale.xml",   // FatturaPA sample, truncated in the middle of an element
    };

    private static IEnumerable<(string File, string Name, byte[] Data)> Xmls(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) yield return (file, Path.GetFileName(file), File.ReadAllBytes(file));
            else if (file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var found = new List<(string, string, byte[])>();
                using (var doc = PdfDocument.Open(file))
                    foreach (var f in InvoiceSources.Find(doc).Files)
                        if (InvoiceSources.Data(doc, f, InvoiceXmlLimits.Default.MaxBytes) is { } data && InvoiceXml.LooksLikeXml(data))
                            found.Add((file, f.DisplayName, data));
                foreach (var x in found) yield return x;
            }
        }
    }

    [TestPdfsCollectionFact("erechnung")]
    public void Collections_ReadOrRefusedWithReason()
    {
        var root = TestPdfs.File("erechnung", "");
        var bySyntax = new Dictionary<InvoiceSyntax, int>();
        var refused = new List<string>();
        foreach (var (file, name, data) in Xmls(root))
        {
            try
            {
                var kind = InvoiceKind.Of(InvoiceXml.Read(data));
                bySyntax[kind.Syntax] = bySyntax.GetValueOrDefault(kind.Syntax) + 1;
            }
            catch (InvoiceXmlException e)
            {
                refused.Add($"{name} ({e.Reason}) in {Path.GetFileName(file)}");
                Assert.Contains(name, KnownDamaged);
            }
        }
        Assert.True(bySyntax.GetValueOrDefault(InvoiceSyntax.Cii) >= 200, string.Join(", ", bySyntax));
        Assert.True(bySyntax.GetValueOrDefault(InvoiceSyntax.UblInvoice) >= 70, string.Join(", ", bySyntax));
        Assert.True(bySyntax.GetValueOrDefault(InvoiceSyntax.ZugferdV1) >= 20, string.Join(", ", bySyntax));
        Assert.True(bySyntax.GetValueOrDefault(InvoiceSyntax.FatturaPa) >= 10, string.Join(", ", bySyntax));
        Assert.True(refused.Count <= KnownDamaged.Count, string.Join("; ", refused));
    }

    [TestPdfsCollectionFact("erechnung")]
    public void Collections_EveryInvoiceReadable()
    {
        // stage 2: every CII and UBL invoice of the collections reads into the readable form, with its number
        var root = TestPdfs.File("erechnung", "");
        var read = 0;
        var withoutNumber = new List<string>();
        foreach (var (file, name, data) in Xmls(root))
        {
            InvoiceXmlDocument xml;
            try
            {
                xml = InvoiceXml.Read(data);
            }
            catch (InvoiceXmlException)
            {
                continue;   // refused ones are the business of Collections_ReadOrRefusedWithReason
            }
            var kind = InvoiceKind.Of(xml);
            if (kind.Syntax is not (InvoiceSyntax.Cii or InvoiceSyntax.UblInvoice or InvoiceSyntax.UblCreditNote or InvoiceSyntax.ZugferdV1)) continue;
            var invoice = InvoiceReader.Read(xml, kind);
            Assert.NotNull(invoice);
            read++;
            if (string.IsNullOrWhiteSpace(invoice.Text("BT-1"))) withoutNumber.Add($"{name} in {Path.GetFileName(file)}");
            // and every value can be shown (FG 824: a payment means without code stopped the reading)
            foreach (var field in invoice.Root.AllFields()) InvoiceFormat.Display(field, invoice);
        }
        Assert.True(read >= 280, read.ToString());
        Assert.True(withoutNumber.Count == 0, string.Join("; ", withoutNumber));
    }

    [TestPdfsCollectionFact("erechnung/zugferd-2.5.2-beispiele")]
    public void OfficialSamples_ValuesFoundInTheVisibleText()
    {
        // stage 3: the official examples show their invoice — of the values that can be checked, nearly all are found in the visible text
        // (14.11.2026: 4 490 found, 48 partly, 131 not; the rest are values the samples do not print, such as order and object numbers)
        var root = TestPdfs.File("erechnung", "zugferd-2.5.2-beispiele");
        int found = 0, checkable = 0, files = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var doc = PdfDocument.Open(file);
            var location = InvoiceLocator.Locate(doc);
            if (location is not { Xml: { } xml, Kind: { } kind } || InvoiceReader.Read(xml, kind) is not { } invoice) continue;
            var result = InvoiceComparison.Compare(doc, invoice);
            Assert.Equal(invoice.Root.AllFields().Count(), result.Matches.Count);
            found += result.Count(InvoiceMatchStatus.Found);
            checkable += result.Matches.Count(m => m.Status is not (InvoiceMatchStatus.NotCheckable or InvoiceMatchStatus.NotChecked));
            files++;
        }
        Assert.True(files >= 60, files.ToString());
        Assert.True(found >= 0.93 * checkable, $"{found} von {checkable}");
    }

    [TestPdfsCollectionFact("erechnung")]
    public void Collections_EveryPdfCompared()
    {
        // stage 3 over every PDF of the collections, the damaged and the deliberately wrong ones too: never an exception
        var compared = 0;
        foreach (var file in Directory.EnumerateFiles(TestPdfs.File("erechnung", ""), "*.pdf", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            using var doc = PdfDocument.Open(file);
            var location = InvoiceLocator.Locate(doc);
            if (location is not { Xml: { } xml, Kind: { } kind }) continue;
            InvoiceData? invoice;
            try
            {
                invoice = InvoiceReader.Read(xml, kind);
            }
            catch (InvoiceXmlException)
            {
                continue;
            }
            if (invoice is null) continue;
            _ = InvoiceComparison.Compare(doc, invoice);
            compared++;
        }
        Assert.True(compared >= 200, compared.ToString());
    }

    [TestPdfsCollectionFact("erechnung/xrechnung-testsuite")]
    public void XRechnungTestSuite_XRechnungThroughout()
    {
        var root = TestPdfs.File("erechnung", "xrechnung-testsuite");
        var count = 0;
        foreach (var (_, name, data) in Xmls(root))
        {
            var kind = InvoiceKind.Of(InvoiceXml.Read(data));
            Assert.True(kind.Profile == InvoiceProfile.XRechnung && kind.EInvoiceInGermany == true, $"{name}: {kind}");
            count++;
        }
        Assert.True(count >= 80, count.ToString());
    }
}
