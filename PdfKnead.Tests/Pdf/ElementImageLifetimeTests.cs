using System.Runtime.CompilerServices;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>Images of overlaid elements do not stay in memory beyond the lifetime of the document.</summary>
public sealed class ElementImageLifetimeTests
{
    /// <summary>Builds a page with a foreign stamp annotation whose appearance stream draws an image.</summary>
    private static byte[] StampWithImage()
    {
        var pdf = new TestPdf();
        var catalog = pdf.Add(Dict(("Type", N("Catalog"))));
        var pagesNode = pdf.Add(new PdfDictionary());
        var pixels = new byte[32 * 32 * 3];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
        var image = pdf.Add(FlateStream(pixels, Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(32)),
            ("Height", PdfInteger.Of(32)), ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", N("DeviceRGB")))));
        var form = pdf.Add(FlateStream(Encoding.ASCII.GetBytes("q 100 0 0 100 0 0 cm /Im Do Q"), Dict(("Type", N("XObject")),
            ("Subtype", N("Form")), ("BBox", Box(0, 0, 100, 100)), ("Resources", Dict(("XObject", Dict(("Im", Ref(image)))))))));
        var stamp = pdf.Add(Dict(("Type", N("Annot")), ("Subtype", N("Stamp")), ("Rect", Box(20, 20, 120, 120)),
            ("AP", Dict(("N", Ref(form))))));
        var content = pdf.Add(FlateStream(Encoding.ASCII.GetBytes("0 0 1 rg 0 0 10 10 re f")));
        var page = pdf.Add(Dict(("Type", N("Page")), ("Parent", Ref(pagesNode)), ("MediaBox", Box(0, 0, 200, 200)),
            ("Contents", Ref(content)), ("Annots", new PdfArray([Ref(stamp)]))));
        pdf.Replace(pagesNode, Dict(("Type", N("Pages")), ("Kids", new PdfArray([Ref(page)])), ("Count", PdfInteger.Of(1))));
        ((PdfDictionary)pdf.Get(catalog)).Set(PdfName.Pages, Ref(pagesNode));
        pdf.RootNumber = catalog;
        return pdf.Build();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ExportAndClose(byte[] data)
    {
        var doc = PdfDocument.Open(data);
        var item = PageItem.From(doc)[0];
        Assert.Contains(item.Elements, e => e is ForeignAnnotation);
        var images = new ImageCache();
        using (RasterExport.Render(item, 1, images))
        {
        }
        var weak = new WeakReference(doc);
        images.Detach();
        doc.Dispose();
        return weak;
    }

    [Fact]
    public void ExportedElementImages_DoNotKeepTheDocument()
    {
        var weak = ExportAndClose(StampWithImage());
        for (var i = 0; i < 3 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive, "geschlossenes Dokument hängt noch im Bildspeicher der Elemente");
    }
}
