using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Tests;

namespace PdfKnead.EInvoice.Tests;

/// <summary>
/// Saving a PDF with an embedded e-invoice (KONZEPT.md, section 5.24; decided on 15.11.2026: PDF-Knead never changes an e-invoice):
/// whatever the way of saving that keeps attachments — unchanged, another version, the stages of compression, pages turned or left out,
/// forms flattened, PDF/A-3b, cleaned up without „Anhänge“ and „Metadaten“ — the invoice XML stays byte for byte, the file keeps its name,
/// type and relationship, it stays in the /AF of the catalog and in the name tree, the invoice data of the XMP stay, the embedded file is
/// packed only with a filter every reader knows, and no rule of the PDF side is broken that was not broken before. Only invented invoices.
/// </summary>
public sealed class InvoiceSaveTests
{
    /// <summary>What a PDF says of its e-invoice, for comparing before and after saving.</summary>
    private sealed record Picture(byte[] Data, string Kind, string File, string Places, string Xmp, string Extensions, IReadOnlySet<string> Rules, string Filter);

    private static Picture Look(PdfDocument doc)
    {
        var location = InvoiceLocator.Locate(doc);
        Assert.True(location.HasInvoice);
        var file = location.File!;
        var data = InvoiceReader.Read(location.Xml!, location.Kind!)!;
        var xmp = location.Xmp;
        var filter = doc.Resolve(file.StreamId) is PdfStream stream ? stream.Dictionary.GetRaw(PdfName.Filter)?.ToString() ?? "" : "?";
        return new Picture(location.Data!, location.Kind!.ToString(), $"{file.DisplayName} | {file.Relationship} | {file.MimeType}",
            string.Join(", ", file.Places.Select(p => p.Place).Distinct().Order()),
            xmp is null ? "" : $"{xmp.Namespace} | {xmp.DocumentType} | {xmp.DocumentFileName} | {xmp.Version} | {xmp.ConformanceLevel}",
            xmp is null ? "" : string.Join("; ", xmp.Extensions.Select(e => $"{e.NamespaceUri} {e.Prefix} {string.Join(",", e.Properties)}")),
            InvoiceHybridRules.Check(doc, location, data).Select(v => v.Rule.Id).ToHashSet(), filter);
    }

    /// <summary>The PDF of the rule tests with a second page (to leave one out).</summary>
    private static byte[] TwoPages()
    {
        var objects = InvoiceHybridRulesTests.Pdf();
        objects[2] = "<</Type/Pages/Kids[3 0 R 12 0 R]/Count 2/MediaBox[0 0 595 842]>>";
        objects[12] = "<</Type/Page/Parent 2 0 R/Contents 4 0 R/Resources<<>>>>";
        return TestFiles.Build(objects);
    }

    public static TheoryData<string> Ways => new()
    {
        "unverändert", "PDF 2.0", "verlustfrei", "verlustfrei mit Brotli", "Bildschirm", "gedreht und eine Seite weniger", "flach", "PDF/A-3b",
        "PDF/A-4f", "bereinigt ohne Anhänge und Metadaten", "bereinigt mit Anhängen und Metadaten, E-Rechnung behalten",
    };

    private static PdfSaveOptions Options(string way, PdfDocument? doc = null) => way switch
    {
        // decision (4) of 15.11.2026: the e-invoice's row not ticked — it stays whole, also with „Metadaten“ cleaned up
        "bereinigt mit Anhängen und Metadaten, E-Rechnung behalten" => new PdfSaveOptions
        {
            Cleanup = CleanupItems.Attachments | CleanupItems.Metadata | CleanupItems.Scripts | CleanupItems.Thumbnails | CleanupItems.PrivateData,
            KeptAttachments = new HashSet<PdfKnead.Core.Pdf.Objects.PdfObjectId> { InvoiceLocator.Locate(doc!).File!.StreamId },
            AttachedFileMetadataStays = true,
        },
        "PDF 2.0" => new PdfSaveOptions { Version = "2.0" },
        "verlustfrei" => new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.Lossless) },
        "verlustfrei mit Brotli" => new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.Lossless) with { Brotli = true } },
        "Bildschirm" => new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.Screen) },
        "flach" => new PdfSaveOptions { Flatten = true },
        "PDF/A-3b" => new PdfSaveOptions { PdfA = PdfAConformance.A3b },
        "PDF/A-4f" => new PdfSaveOptions { PdfA = PdfAConformance.A4f, Version = "2.0" },
        "bereinigt ohne Anhänge und Metadaten" => new PdfSaveOptions
        {
            Cleanup = CleanupItems.Scripts | CleanupItems.Thumbnails | CleanupItems.PrivateData | CleanupItems.Comments | CleanupItems.Bookmarks
                      | CleanupItems.HiddenLayers,
        },
        _ => new PdfSaveOptions(),
    };

    [Theory]
    [MemberData(nameof(Ways))]
    public void Saved_TheInvoiceStaysAsItWas(string way)
    {
        using var original = PdfDocument.Open(TwoPages());
        var before = Look(original);
        var pages = PageItem.From(original);
        IReadOnlyList<PageItem> kept = way == "gedreht und eine Seite weniger" ? [pages[0].Rotated(90)] : pages;
        var output = new MemoryStream();
        InvoiceSources.RegisterXmpData();   // as when the module is docked
        PdfWriter.Write(original, kept, output, Options(way, original));
        using var saved = PdfDocument.Open(output.ToArray());
        var after = Look(saved);

        Assert.Equal(before.Data, after.Data);   // byte for byte
        Assert.Equal(before.Kind, after.Kind);
        Assert.Equal(before.File, after.File);
        Assert.Equal(before.Places, after.Places);
        Assert.Equal(before.Xmp, after.Xmp);
        Assert.Equal(before.Extensions, after.Extensions);
        // packed, if at all, as every reader can unpack it — never with Brotli (not in ISO 32000; the receiver's software must read the XML)
        Assert.DoesNotContain("Brotli", after.Filter, StringComparison.Ordinal);
        // no rule of the PDF side broken that was not broken before
        Assert.True(after.Rules.IsSubsetOf(before.Rules), $"neu nach dem Speichern: {string.Join(", ", after.Rules.Except(before.Rules))}");
        // made PDF/A: the invoice data of the XMP kept with their extension schema, as PDF/A asks (6.6.2.3; Fehlergedächtnis 863)
        // the identification of PDF/A stays wherever the copy can still be PDF/A-3 (cleaning up the metadata included, decision (4))
        if (way is not ("PDF 2.0" or "PDF/A-4f" or "verlustfrei mit Brotli")) Assert.Equal(PdfACheck.Declared(original), PdfACheck.Declared(saved));
        if (way.StartsWith("PDF/A-", StringComparison.Ordinal)) Assert.Empty(PdfACheck.Check(saved, Options(way).PdfA).Where(v => v.Id.StartsWith("6.6", StringComparison.Ordinal)).Select(v => v.ToString()));
    }

    /// <summary>
    /// Where the copy keeps no attachments — PDF/A-2b and PDF/A-4 allow only PDF/A files, so the invoice XML goes by the norm; „Anhänge“ cleaned
    /// up; a part without the attachments of the whole — the e-invoice goes whole: no file left, and no invoice data in the XMP naming a file
    /// that is gone (Fehlergedächtnis 863 and 865; decided on 15.11.2026).
    /// </summary>
    [Theory]
    [InlineData("PDF/A-2b")]
    [InlineData("PDF/A-4")]
    [InlineData("bereinigt mit Anhängen")]
    [InlineData("Teil ohne Anhänge")]
    public void Dropped_TheInvoiceGoesWhole(string way)
    {
        InvoiceSources.RegisterXmpData();   // as when the module is docked
        using var original = PdfDocument.Open(TwoPages());
        var output = new MemoryStream();
        PdfWriter.Write(original, PageItem.From(original), output, way switch
        {
            "PDF/A-4" => new PdfSaveOptions { PdfA = PdfAConformance.A4, Version = "2.0" },
            "PDF/A-2b" => new PdfSaveOptions { PdfA = PdfAConformance.A2b },
            "bereinigt mit Anhängen" => new PdfSaveOptions { Cleanup = CleanupItems.Attachments },
            _ => new PdfSaveOptions { PartOfDocument = true },
        });
        using var saved = PdfDocument.Open(output.ToArray());
        var location = InvoiceLocator.Locate(saved);
        Assert.False(location.HasInvoice);
        // a part keeps what hangs on its pages (the delivery note as an annotation); only the attachments of the whole go
        if (way != "Teil ohne Anhänge") Assert.Empty(location.Attachments);
        Assert.True(location.Xmp is null || location.Xmp.Namespace.Length == 0, $"XMP nennt noch eine Rechnung: {location.Xmp?.DocumentFileName}");
    }

    private static readonly Lazy<Core.Platform.SigningCertificate> Own = new(() =>
        Core.Platform.SigningCertificates.CreateOwn("Max Mustermann", Path.Combine(Path.GetTempPath(), "pdf-knead-tests", $"erechnung-speichern-{Guid.NewGuid():N}.p12")));

    /// <summary>
    /// Signed (KONZEPT.md, section 5.24): the signature is appended to the file — the invoice stays byte for byte, with its place, its XMP
    /// data and the PDF/A identification, and no rule of the PDF side is broken anew (a signature is allowed in PDF/A-3).
    /// </summary>
    [Fact]
    public void Signed_TheInvoiceStaysAsItWas()
    {
        using var original = PdfDocument.Open(TwoPages());
        var before = Look(original);
        var ink = new InkElement
        {
            Rect = new Core.Pdf.Document.PdfRectangle(350, 100, 530, 160),
            Strokes = [[0, 20, 40, 50, 90, 10, 140, 45, 175, 25]],
            StrokeWidth = 3,
            Color = 0x1B3A8C,
            Digital = new DigitalSigning("Max Mustermann · 15.11.2026"),
        };
        var signed = Core.Pdf.Signatures.PdfSigner.Sign(original, 0, ink, Own.Value, new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.FromHours(1)));
        using var saved = PdfDocument.Open(signed);
        var after = Look(saved);
        Assert.Equal(before.Data, after.Data);
        Assert.Equal(before.File, after.File);
        Assert.Equal(before.Places, after.Places);
        Assert.Equal(before.Xmp, after.Xmp);
        Assert.Equal(before.Extensions, after.Extensions);
        Assert.Equal(PdfACheck.Declared(original), PdfACheck.Declared(saved));
        Assert.True(after.Rules.IsSubsetOf(before.Rules), $"neu nach dem Signieren: {string.Join(", ", after.Rules.Except(before.Rules))}");
    }
}
