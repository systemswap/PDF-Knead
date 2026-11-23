using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Filters.Jbig2;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Files for the counter-check of the writers changed in M2 from outside (not part of the normal test run; PDFKNEAD_GEGENPROBE=&lt;folder&gt;;
/// PLAN §6 point 8 b, finding B4 of the probe of 20.11.2026): TIFF pages in grey and RGB with their profile (FG 980), a PDF whose images the
/// command line saves — 16-bit RGB with a 16-bit mask (FG 983), a JPX file (.jpf, FG 988) —, a PDF/A-2b copy with a bare CMYK codestream the
/// repair wraps as JPX (FG 981, 987), and a copy saved from a page with inline images the display reads only with a tolerance (FG 993). The
/// reference reader, the structure checker, the PDF/A validator and the model checker of ISO 32000-2 then read them (docs/normabgleich/M2-filter.md, Wächter).
/// </summary>
public sealed class WriterCounterCheckFiles
{
    [Fact]
    public void Write()
    {
        var folder = Environment.GetEnvironmentVariable("PDFKNEAD_GEGENPROBE");
        if (string.IsNullOrEmpty(folder)) return;
        Directory.CreateDirectory(folder);

        foreach (var (kind, name) in new[] { (TiffPageKind.Gray, "grau"), (TiffPageKind.Rgb, "rgb") })
        {
            using var tiff = File.Create(Path.Combine(folder, $"seite-{name}.tif"));
            TiffEncoder.EncodePages(1, _ => new TiffPage(64, 32, kind, 72, (y, row) =>
            {
                for (var i = 0; i < row.Length; i++) row[i] = (byte)(i * 4 + y * 3);
            }), tiff);
        }

        // images for „Bild speichern“: 16-bit RGB with a 16-bit soft mask; a JPX file (brand „jpx “)
        var pdf = new TestPdf();
        var rgb = new byte[32 * 16 * 3 * 2];
        for (var i = 0; i < rgb.Length / 2; i++) BinaryPrimitives.WriteUInt16BigEndian(rgb.AsSpan(i * 2), (ushort)(i * 4099 + 7));
        var alpha = new byte[32 * 16 * 2];
        for (var i = 0; i < alpha.Length / 2; i++) BinaryPrimitives.WriteUInt16BigEndian(alpha.AsSpan(i * 2), (ushort)(i * 211 + 1));
        PdfDictionary Image(int w, int h, int bpc, string space) => TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(w)), ("Height", PdfInteger.Of(h)), ("BitsPerComponent", PdfInteger.Of(bpc)), ("ColorSpace", TestPdf.N(space)));
        var mask = pdf.Add(TestPdf.FlateStream(alpha, Image(32, 16, 16, "DeviceGray")));
        var deep = TestPdf.FlateStream(rgb, Image(32, 16, 16, "DeviceRGB"));
        deep.Dictionary.Set(PdfName.Get("SMask"), TestPdf.Ref(mask));
        var cmyk = JpxRobustTests.Codestream(0, 64, 64, 4, 1, 0, 1, 0);
        var jpx = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(64)),
            ("Height", PdfInteger.Of(1)), ("Filter", TestPdf.N("JPXDecode"))), PdfAJpeg2000.Wrap(cmyk)!);
        File.WriteAllBytes(Path.Combine(folder, "bilder.pdf"), Page(pdf, "q 64 0 0 32 20 20 cm /Deep Do Q q 64 0 0 8 20 100 cm /Jpx Do Q",
            ("Deep", deep), ("Jpx", jpx)));

        // PDF/A-2b with a bare CMYK codestream: the repair wraps it as a JPX file
        var bare = new TestPdf();
        var bareImage = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(64)),
            ("Height", PdfInteger.Of(1)), ("Filter", TestPdf.N("JPXDecode"))), cmyk);
        using (var doc = PdfDocument.Open(Page(bare, "q 64 0 0 8 20 100 cm /Jpx Do Q", ("Jpx", bareImage))))
            File.WriteAllBytes(Path.Combine(folder, "pdfa-cmyk-jpx.pdf"), PdfAExport.Write(doc, PageItem.From(doc), PdfAConformance.A2b).Bytes);

        // a copy saved from a page with inline images that break the standard (JBIG2 inline, ASCII85 without its end, „EI“ in the data)
        var bits = new byte[5 * 24];
        for (var y = 0; y < 24; y++)
            for (var x = 0; x < 40; x++)
                if ((x / 4 + y / 4) % 2 == 0) bits[y * 5 + x / 8] |= (byte)(0x80 >> (x % 8));
        var jbig2 = Jbig2Encoder.Generic(bits, 40, 24, 5);
        var inline = new TestPdf();
        var content = new List<byte>();
        content.AddRange("q 80 0 0 48 20 200 cm BI /W 40 /H 24 /BPC 1 /CS /G /F /JBIG2Decode ID "u8.ToArray());
        content.AddRange(jbig2);
        content.AddRange(" EI Q\nq 80 0 0 10 20 150 cm BI /W 8 /H 1 /BPC 8 /CS /G ID "u8.ToArray());
        content.AddRange(new byte[] { 0x40, 0x20, 0x45, 0x49, 0x20, 0x80, 0xC0, 0xFF });
        content.AddRange(" EI Q\n"u8.ToArray());
        using (var doc = PdfDocument.Open(Page(inline, [.. content])))
        {
            using var output = File.Create(Path.Combine(folder, "gespeichert-inline.pdf"));
            PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions());
        }
    }

    private static byte[] Page(TestPdf pdf, string content, params (string Name, PdfStream XObject)[] xobjects) =>
        Page(pdf, Encoding.ASCII.GetBytes(content), xobjects);

    private static byte[] Page(TestPdf pdf, byte[] content, params (string Name, PdfStream XObject)[] xobjects)
    {
        var resources = new PdfDictionary();
        var dict = new PdfDictionary();
        foreach (var (name, x) in xobjects) dict.Set(PdfName.Get(name), TestPdf.Ref(pdf.Add(x)));
        if (xobjects.Length > 0) resources.Set(PdfName.Get("XObject"), dict);
        var contents = pdf.Add(new PdfStream(new PdfDictionary(), content));
        var pages = pdf.Add(new PdfDictionary());
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("MediaBox", TestPdf.Box(0, 0, 300, 300)),
            ("Resources", resources), ("Contents", TestPdf.Ref(contents))));
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pages))));
        return pdf.Build();
    }
}
