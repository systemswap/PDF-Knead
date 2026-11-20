using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.EInvoice;

/// <summary>
/// Whether saving keeps an embedded e-invoice as it was (KONZEPT.md, section 5.24; decided on 15.11.2026: PDF-Knead never changes an
/// e-invoice): the PDF saved in the ways that keep attachments — unchanged, as PDF 2.0, with lossless compression, the first page turned, as
/// PDF/A-3b — read back and compared: the XML byte for byte, name, type and relationship of the file, where it is found, the invoice data
/// of the XMP with their extension schemas, and no rule of the PDF side broken anew. For „pdf-knead erechnung --speichertest“ over the
/// collections; the unit tests (InvoiceSaveTests) check the same with invented files and more ways.
/// </summary>
public static class InvoiceSaveCheck
{
    /// <summary>The ways of saving checked.</summary>
    private static readonly (string Way, Func<PdfSaveOptions> Options, bool Turn)[] Ways =
    [
        ("unverändert", () => new PdfSaveOptions(), false),
        ("PDF 2.0", () => new PdfSaveOptions { Version = "2.0" }, false),
        ("verlustfrei", () => new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.Lossless) }, false),
        ("gedreht", () => new PdfSaveOptions(), true),
        ("PDF/A-3b", () => new PdfSaveOptions { PdfA = PdfAConformance.A3b }, false),
    ];

    /// <summary>What a PDF says of its e-invoice.</summary>
    private sealed record Picture(byte[]? Data, string File, string Places, string Xmp, string Extensions, IReadOnlySet<string> Rules)
    {
        public static Picture Of(PdfDocument doc, InvoiceLocation location)
        {
            var file = location.File;
            var xmp = location.Xmp;
            var data = location is { Xml: { } xml, Kind: { } kind } ? InvoiceReader.Read(xml, kind) : null;
            return new Picture(location.Data, file is null ? "" : $"{file.DisplayName} | {file.Relationship} | {file.MimeType}",
                file is null ? "" : string.Join(", ", file.Places.Select(p => p.Place).Distinct().Order()),
                xmp is null ? "" : $"{xmp.Namespace} | {xmp.DocumentType} | {xmp.DocumentFileName} | {xmp.Version} | {xmp.ConformanceLevel}",
                xmp is null ? "" : string.Join("; ", xmp.Extensions.Select(e => $"{e.NamespaceUri} {e.Prefix} {string.Join(",", e.Properties)}")),
                data is null ? new HashSet<string>() : InvoiceHybridRules.Check(doc, location, data).Select(v => v.Rule.Id).ToHashSet());
        }

        /// <summary>What differs in <paramref name="after"/>, in short words.</summary>
        public IEnumerable<string> Differences(Picture after)
        {
            if (!(Data ?? []).AsSpan().SequenceEqual(after.Data ?? [])) yield return after.Data is null ? "Rechnungs-XML fehlt" : "Rechnungs-XML verändert";
            if (File != after.File) yield return $"Datei „{File}“ → „{after.File}“";
            if (Places != after.Places) yield return $"Fundorte „{Places}“ → „{after.Places}“";
            if (Xmp != after.Xmp) yield return $"XMP „{Xmp}“ → „{after.Xmp}“";
            if (Extensions != after.Extensions) yield return "Erweiterungsschema anders";
            foreach (var rule in after.Rules.Except(Rules).Order(StringComparer.Ordinal)) yield return "neu " + rule;
        }
    }

    /// <summary>Saves the PDF in each way and compares; per way null if all stayed, otherwise each difference.</summary>
    /// <param name="doc">The PDF.</param>
    /// <param name="location">Its invoice.</param>
    /// <param name="keepPdfA">Where to keep the PDF/A-3b copy (for an independent PDF/A check); null: not kept.</param>
    public static IEnumerable<(string Way, string? Difference)> Run(PdfDocument doc, InvoiceLocation location, string? keepPdfA = null)
    {
        var before = Picture.Of(doc, location);
        foreach (var (way, options, turn) in Ways)
        {
            List<string> differences;
            try
            {
                var pages = PageItem.From(doc);
                IReadOnlyList<PageItem> kept = turn && pages.Length > 0 ? [pages[0].Rotated(90), .. pages.Skip(1)] : pages;
                using var output = new MemoryStream();
                PdfWriter.Write(doc, kept, output, options());
                if (keepPdfA is not null && way == "PDF/A-3b") File.WriteAllBytes(keepPdfA, output.ToArray());
                using var saved = PdfDocument.Open(output.ToArray());
                differences = [.. before.Differences(Picture.Of(saved, InvoiceLocator.Locate(saved)))];
            }
            catch (Exception e) when (e is PdfException or IOException or InvalidDataException or InvalidOperationException or NotSupportedException
                                          or UnauthorizedAccessException)
            {
                differences = ["nicht gespeichert: " + e.GetType().Name];
            }
            if (differences.Count == 0) yield return (way, null);
            foreach (var d in differences) yield return (way, d);
        }
    }
}
