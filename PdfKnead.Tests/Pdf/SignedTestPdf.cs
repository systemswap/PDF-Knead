using System.Text;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Builds a small PDF with one signature field whose /ByteRange fits the written file exactly (the gap is the hexadecimal
/// /Contents string), as a signing program leaves it. The signature bytes themselves are a placeholder: the structure is
/// real, the cryptography is not.
/// </summary>
internal static class SignedTestPdf
{
    private const int PlaceholderBytes = 64;
    private const byte Marker = 0xAB;

    /// <summary>Builds the file.</summary>
    /// <param name="byteRange">Replaces the fitting /ByteRange (prepared files); null = fitting.</param>
    /// <param name="xmp">XMP metadata for the catalogue (its own object); null = none.</param>
    /// <returns>The file bytes.</returns>
    public static byte[] Build(PdfObject[]? byteRange = null, string? xmp = null)
    {
        // Pass 1 writes placeholder numbers; the real offsets move the string by the change in digits, so repeat until stable
        long[] range = [0, 0, 0, 0];
        for (var pass = 0; pass < 6; pass++)
        {
            var bytes = Write(byteRange ?? [.. range.Select(v => (PdfObject)PdfInteger.Of(v))], xmp);
            if (byteRange is not null) return bytes;
            var (open, close) = Gap(bytes);
            long[] next = [0, open, close + 1, bytes.Length - (close + 1)];
            if (next.SequenceEqual(range)) return bytes;
            range = next;
        }
        throw new InvalidOperationException("ByteRange did not settle");
    }

    private static byte[] Write(PdfObject[] byteRange, string? xmp)
    {
        var pdf = new TestPdf();
        var stream = pdf.Add(TestPdf.FlateStream("BT /F1 12 Tf 72 700 Td (Vertrag) Tj ET"u8.ToArray()));
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("Type1")), ("BaseFont", TestPdf.N("Helvetica"))));
        var pages = pdf.Add(new PdfDictionary());
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("Contents", TestPdf.Ref(stream)),
            ("MediaBox", TestPdf.Box(0, 0, 595, 842)), ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(font))))))));
        var signature = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Sig")), ("Filter", TestPdf.N("Adobe.PPKLite")),
            ("SubFilter", TestPdf.N("ETSI.CAdES.detached")), ("ByteRange", new PdfArray(byteRange)),
            ("Contents", new PdfString(Enumerable.Repeat(Marker, PlaceholderBytes).ToArray(), isHex: true))));
        var field = pdf.Add(TestPdf.Dict(("FT", TestPdf.N("Sig")), ("T", new PdfString("Unterschrift"u8.ToArray())), ("V", TestPdf.Ref(signature)),
            ("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Widget")), ("Rect", TestPdf.Box(0, 0, 0, 0)), ("P", TestPdf.Ref(page))));
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray { TestPdf.Ref(page) }), ("Count", PdfInteger.Of(1))));
        pdf.Replace(page, TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("Contents", TestPdf.Ref(stream)),
            ("MediaBox", TestPdf.Box(0, 0, 595, 842)), ("Resources", TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(font)))))),
            ("Annots", new PdfArray { TestPdf.Ref(field) })));
        var catalog = TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pages)),
            ("AcroForm", TestPdf.Dict(("Fields", new PdfArray { TestPdf.Ref(field) }), ("SigFlags", PdfInteger.Of(3)))));
        if (xmp is not null)
            catalog.Set(PdfName.Get("Metadata"), TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("Metadata")), ("Subtype", TestPdf.N("XML"))),
                Encoding.UTF8.GetBytes(xmp)))));
        pdf.RootNumber = pdf.Add(catalog);
        return pdf.Build();
    }

    /// <summary>Positions of „&lt;“ and „&gt;“ around the placeholder.</summary>
    private static (long Open, long Close) Gap(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var hex = string.Concat(Enumerable.Repeat(Marker.ToString("X2"), PlaceholderBytes));
        var at = text.IndexOf(hex, StringComparison.OrdinalIgnoreCase);
        if (at < 1 || text[at - 1] != '<' || text[at + hex.Length] != '>') throw new InvalidOperationException("placeholder not found");
        return (at - 1, at + hex.Length);
    }
}
