using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Empty names ("/" is a valid name in PDF): issue13610 references its page image as "/ 7 0 R" and draws it with "/ Do". If the writer
/// emits "/6 0 R", a reader parses the name "6", and after every save the header and footer images of the invoice are missing.
/// </summary>
public sealed class EmptyNameTests
{
    [Fact]
    public void Serializer_KeepsEmptyNameApart()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get(""), new PdfReference(new PdfObjectId(6, 0)));
        dict.Set(PdfName.Get("A"), PdfName.Get(""));
        dict.Set(PdfName.Get("B"), new PdfArray([PdfName.Get(""), PdfName.Get(""), PdfName.Get("X"), PdfInteger.Of(5)]));
        var text = System.Text.Encoding.ASCII.GetString(PdfSerializer.ToBytes(dict));
        Assert.Contains("/ 6 0 R", text);
        Assert.DoesNotContain("//", text);
        Assert.DoesNotContain("/6", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImageUnderEmptyName_SurvivesSaving(bool compress)
    {
        var pdf = TestPdf.Simple(1, null);
        var pixels = new byte[40 * 40 * 3];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
        var image = pdf.Add(TestPdf.FlateStream(pixels, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(40)), ("Height", PdfInteger.Of(40)), ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", TestPdf.N("DeviceRGB")))));
        var content = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes("q 300 0 0 300 100 300 cm / Do Q")));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Contents, TestPdf.Ref(content));
        var xobjects = new PdfDictionary();
        xobjects.Set(PdfName.Get(""), TestPdf.Ref(image));
        page.Set(PdfName.Resources, TestPdf.Dict(("XObject", xobjects)));
        var file = pdf.Build();

        using var original = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(original, PageItem.From(original), output,
            compress ? new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.Lossless) } : new PdfSaveOptions());
        using var saved = PdfDocument.Open(output.ToArray());
        Assert.IsType<PdfStream>(saved.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("")));
        Assert.Equal(0, CompressionWriterTests.Difference(original, saved, 1));
    }
}
