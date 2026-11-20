using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Security;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>Save as image.</summary>
public sealed class RasterExportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PagesBecomeImages_LookTheSame_NoTextLeft(bool lossless)
    {
        using var original = PdfDocument.Open(TestPdf.Simple(3).Build());
        var pages = PageItem.From(original);
        pages[0] = pages[0].WithElements([new TextElement { Rect = new PdfRectangle(100, 600, 400, 650), Text = "Max Mustermann", FontSize = 24 }]);
        using var raster = new MemoryStream();
        RasterExport.Write(pages, raster, new RasterSettings(Dpi: 100, Lossless: lossless));
        using var doc = PdfDocument.Open(raster.ToArray());

        Assert.Equal(3, doc.PageCount);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(original.Pages[i].Width, doc.Pages[i].Width, 1);   // including the rotated page 2: width as displayed
            Assert.Equal(original.Pages[i].Height, doc.Pages[i].Height, 1);
            Assert.Empty(PageInterpreter.Build(doc, doc.Pages[i]).Text);    // no text left
        }
        Assert.DoesNotContain("Mustermann", System.Text.Encoding.Latin1.GetString(raster.ToArray()));

        // Looks the same as before (page 1 with the element)
        var images = new ImageCache();
        using var before = RasterExport.Render(pages[0], 0.5, images);
        using var after = RasterExport.Render(PageItem.From(doc)[0], 0.5, images);
        Assert.Equal((before.Width, before.Height), (after.Width, after.Height));
        long difference = 0;
        for (var y = 0; y < before.Height; y++)
            for (var x = 0; x < before.Width; x++)
                difference += Math.Abs(before.GetPixel(x, y).Red - after.GetPixel(x, y).Red);
        Assert.True(difference / (double)(before.Width * before.Height) < 4, $"mittlere Abweichung {difference / (double)(before.Width * before.Height):0.0}");
    }

    [Fact]
    public void RasterFile_CanBeProtected()
    {
        using var original = PdfDocument.Open(TestPdf.Simple(2).Build());
        using var raster = new MemoryStream();
        RasterExport.Write(PageItem.From(original), raster, new RasterSettings(Dpi: 72));
        using var doc = PdfDocument.Open(raster.ToArray());
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Protection = new PdfProtection("", "Besitzer-1", PdfPermissions.Print) });
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.Equal(PdfPermissions.Print | PdfPermissions.Accessibility, saved.Security!.Permissions & PdfPermissions.All);
        Assert.Equal(2, saved.PageCount);
    }

    [Fact]
    public void HugePages_AreLimited()
    {
        var a0 = BlankPage.Create(2384, 3370).Page;   // A0 in points
        var scale = RasterExport.Scale(a0, 300);
        Assert.True(a0.Width * scale * a0.Height * scale <= RasterExport.MaxPixels * 1.001);
        Assert.Equal(300 / 72.0, RasterExport.Scale(BlankPage.Create(595, 842).Page, 300), 6);   // A4 gets the full 300 dpi
    }

    /// <summary>
    /// In-memory intermediate file: spans several 4 MB chunks (one image of 6 MB), opens as a document like a file,
    /// and nothing of it ends up in the temp folder.
    /// </summary>
    [Fact]
    public void MemoryPdf_SpansChunks_OpensLikeAFile_NoTempFile()
    {
        var pdf = TestPdf.Simple(1);
        var pixels = new byte[1000 * 2000 * 3];
        new Random(7).NextBytes(pixels);   // incompressible, so the stream stays this large
        var image = pdf.Add(new Core.Pdf.Objects.PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", Core.Pdf.Objects.PdfInteger.Of(1000)), ("Height", Core.Pdf.Objects.PdfInteger.Of(2000)),
            ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", Core.Pdf.Objects.PdfInteger.Of(8)),
            ("Length", Core.Pdf.Objects.PdfInteger.Of(pixels.Length))), pixels));
        ((Core.Pdf.Objects.PdfDictionary)pdf.Get(5)).Set(Core.Pdf.Objects.PdfName.Resources,
            TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        pdf.Replace(4, TestPdf.FlateStream("q 500 0 0 700 50 50 cm /Im0 Do Q"u8.ToArray()));
        using var original = PdfDocument.Open(pdf.Build());
        var tempBefore = Directory.GetFiles(Path.GetTempPath(), "pdf-knead-*").Length;

        using var buffer = new Core.Pdf.IO.MemoryPdf();
        PdfWriter.Write(original, buffer, new PdfSaveOptions { CompressStreams = false });
        Assert.True(buffer.Length > 6_000_000, $"{buffer.Length}");
        using var copy = buffer.OpenDocument();
        Assert.Equal(0, buffer.Length);   // chunks released
        Assert.Equal(1, copy.PageCount);
        var stream = (Core.Pdf.Objects.PdfStream)copy.Pages[0].Resources!.GetDictionary(Core.Pdf.Objects.PdfName.Get("XObject"))!.Get(Core.Pdf.Objects.PdfName.Get("Im0"));
        Assert.Equal(pixels, copy.GetStreamData(stream));
        Assert.Equal(tempBefore, Directory.GetFiles(Path.GetTempPath(), "pdf-knead-*").Length);
    }
}
