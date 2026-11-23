using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Printing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// A missing entry of a dictionary reads as the null object, never as null (docs/FEHLERGEDAECHTNIS.md, No. 1004): tests against null were never
/// true — on the printed sheet every stream was decoded and an image in JPEG lost its format, a name the colour spaces do not list shared one
/// entry of the cache with /Pattern; the hexadecimal copy of an inline image is tested in <see cref="PdfARepairTests"/>.
/// </summary>
public sealed class MissingEntryTests
{
    /// <summary>A JPEG of 64 × 64 pixels, the left half red, the right half blue.</summary>
    private static byte[] Jpeg()
    {
        using var bitmap = new SKBitmap(64, 64);
        using (var canvas = new SKCanvas(bitmap))
        using (var red = new SKPaint { Color = SKColors.Red })
        {
            canvas.Clear(SKColors.Blue);
            canvas.DrawRect(0, 0, 32, 64, red);
        }
        using var encoded = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Jpeg, 90);
        return encoded.ToArray();
    }

    /// <summary>A page of 400 × 300 drawing the image (its filter chain as given) and a Flate form of 20 KB of white space after a square.</summary>
    private static TestPdf JpegPage(byte[] jpeg, PdfObject filter, PdfObject? parms = null)
    {
        var pdf = new TestPdf();
        var catalog = TestPdf.Dict(("Type", TestPdf.N("Catalog")));
        var catalogNumber = pdf.Add(catalog);
        var pages = pdf.Add(new PdfDictionary());
        var imageDict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(64)),
            ("Height", PdfInteger.Of(64)), ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("Filter", filter));
        if (parms is not null) imageDict.Set(PdfName.DecodeParms, parms);
        var image = pdf.Add(new PdfStream(imageDict, jpeg));
        var form = pdf.Add(TestPdf.FlateStream(Encoding.ASCII.GetBytes("0 1 0 rg 0 0 50 50 re f\n" + new string(' ', 20000)),
            TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 50, 50)))));
        var content = pdf.Add(TestPdf.FlateStream("q 200 0 0 200 50 50 cm /Im Do Q q 1 0 0 1 300 50 cm /Fm Do Q"u8.ToArray()));
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("MediaBox", TestPdf.Box(0, 0, 400, 300)),
            ("Resources", TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(image)), ("Fm", TestPdf.Ref(form)))))), ("Contents", TestPdf.Ref(content))));
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        catalog.Set(PdfName.Pages, TestPdf.Ref(pages));
        pdf.RootNumber = catalogNumber;
        return pdf;
    }

    /// <summary>The printed sheet of the file: it names /DCTDecode, holds the JPEG data as they are and looks like the page (the image red on the left).</summary>
    private static byte[] SheetKeepsTheJpeg(byte[] file, byte[] jpeg)
    {
        using var original = PdfDocument.Open(file);
        var sheets = PrintLayout.Arrange([(original.Pages[0].Width, original.Pages[0].Height)],
            new PrintLayoutOptions { Scaling = PrintScaling.ActualSize, Margin = 0, PaperWidth = 400, PaperHeight = 300 });
        using var output = new MemoryStream();
        SheetPdf.Write(original, [0], sheets, output);
        var bytes = output.ToArray();
        Assert.Contains("/DCTDecode", Encoding.Latin1.GetString(bytes));
        Assert.True(bytes.AsSpan().IndexOf(jpeg) >= 0, "the JPEG data as they are");

        using var sheetDoc = PdfDocument.Open(bytes);
        var images = new ImageCache();
        using var before = RasterExport.Render(PageItem.From(original)[0], 0.5, images);
        using var after = RasterExport.Render(PageItem.From(sheetDoc)[0], 0.5, images);
        var inImage = after.GetPixel((int)(70 * 0.5), (int)((300 - 150) * 0.5));   // left half of the image: red
        Assert.True(inImage.Red > 180 && inImage.Blue < 90, $"rot erwartet: {inImage}");
        Assert.Equal(before.Width, after.Width);
        var differing = 0;
        for (var y = 0; y < before.Height; y++)
            for (var x = 0; x < before.Width; x++)
            {
                SKColor a = before.GetPixel(x, y), b = after.GetPixel(x, y);
                if (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) > 30) differing++;
            }
        Assert.True(differing < before.Width * before.Height / 100, $"{differing} Pixel anders");
        return bytes;
    }

    /// <summary>
    /// The printed sheet (the PDF the printer gets) copies streams as stored: a JPEG image with its /DCTDecode, a Flate form packed. Before, the
    /// JPEG data stood there without its filter — read as samples, the image came out as noise — and the form was written decoded.
    /// </summary>
    [Fact]
    public void Sheet_StreamsCopiedAsStored_JpegStaysJpeg()
    {
        var jpeg = Jpeg();
        var bytes = SheetKeepsTheJpeg(JpegPage(jpeg, TestPdf.N("DCTDecode")).Build(), jpeg);
        Assert.True(bytes.Length < jpeg.Length + 4000, $"{bytes.Length} bytes: the form packed as stored");
    }

    /// <summary>
    /// An encrypted file: the sheet is not encrypted and holds the decrypted data, still packed, with their filters — the JPEG with /DCTDecode.
    /// </summary>
    [Fact]
    public void Sheet_OfAnEncryptedFile_DecryptedAndStillPacked()
    {
        var jpeg = Jpeg();
        var bytes = SheetKeepsTheJpeg(JpegPage(jpeg, TestPdf.N("DCTDecode")).Build(cipher: Core.Pdf.Security.PdfCipher.Aes256), jpeg);
        Assert.DoesNotContain("/Encrypt", Encoding.Latin1.GetString(bytes));
        Assert.True(bytes.Length < jpeg.Length + 4000, $"{bytes.Length} bytes: the form packed as stored");
    }

    /// <summary>
    /// A stream with the Crypt filter in its chain (here Identity, before the JPEG): the sheet writes it decoded as far as the image format and
    /// keeps that — /DCTDecode with the data as they are. Before, the filters were all removed and the JPEG data were read as samples.
    /// </summary>
    [Fact]
    public void Sheet_CryptFilterBeforeTheImageFormat_ImageFormatKept()
    {
        var jpeg = Jpeg();
        var file = JpegPage(jpeg, new PdfArray([TestPdf.N("Crypt"), TestPdf.N("DCTDecode")]),
            new PdfArray([TestPdf.Dict(("Type", TestPdf.N("CryptFilterDecodeParms")), ("Name", TestPdf.N("Identity"))), PdfNull.Instance])).Build();
        var text = Encoding.Latin1.GetString(SheetKeepsTheJpeg(file, jpeg));
        Assert.DoesNotContain("/Crypt", text);
    }

    /// <summary>
    /// /Pattern, then a name the page's colour spaces do not list (it draws in grey): each in its own space. Before, both read as the null object
    /// from the resources and shared one entry of the cache — the grey fill was drawn in the pattern space, that is not at all.
    /// </summary>
    [Fact]
    public void NameNotListed_AfterPattern_DrawsInItsOwnSpace()
    {
        var (doc, list) = InterpreterTests.Page("/Pattern cs /Missing cs 0.5 sc 0 0 200 200 re f",
            _ => TestPdf.Dict(("ColorSpace", TestPdf.Dict(("Listed", TestPdf.N("DeviceRGB"))))));
        using (doc)
        {
            using var b = SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new ImageCache());
            var c = b.GetPixel(100, 100);
            Assert.True(c.Red is > 90 and < 170 && c.Red == c.Green && c.Green == c.Blue, $"grau erwartet: {c}");
        }
    }
}
