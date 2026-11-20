using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>Image cache shared across pages.</summary>
public sealed class ImageSharingTests
{
    [Fact]
    public void SameImageOnPagesWithOwnResources_IsDecodedOnce()
    {
        // Logo on every page, each page with its own resource dictionary: decoded once. A page's own /DefaultRGB changes the
        // colours and therefore gets its own entry.
        var pdf = new TestPdf();
        var catalog = pdf.Add(Dict(("Type", N("Catalog"))));
        var pagesNode = pdf.Add(new PdfDictionary());
        var pixels = new byte[64 * 64 * 3];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 13);
        var image = pdf.Add(FlateStream(pixels, Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(64)),
            ("Height", PdfInteger.Of(64)), ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", N("DeviceRGB")))));
        var content = pdf.Add(FlateStream(Encoding.ASCII.GetBytes("q 100 0 0 100 50 50 cm /Logo Do Q")));
        var calRgb = new PdfArray([N("CalRGB"), Dict(("WhitePoint", Box(0.9505, 1, 1.089)), ("Gamma", Box(2.2, 2.2, 2.2)))]);
        var kids = new PdfArray();
        for (var i = 0; i < 4; i++)
        {
            var resources = Dict(("XObject", Dict(("Logo", Ref(image)))));
            if (i == 3) resources.Set(N("ColorSpace"), Dict(("DefaultRGB", calRgb)));
            kids.Add(Ref(pdf.Add(Dict(("Type", N("Page")), ("Parent", Ref(pagesNode)), ("MediaBox", Box(0, 0, 200, 200)),
                ("Resources", resources), ("Contents", Ref(content))))));
        }
        pdf.Replace(pagesNode, Dict(("Type", N("Pages")), ("Kids", kids), ("Count", PdfInteger.Of(4))));
        ((PdfDictionary)pdf.Get(catalog)).Set(PdfName.Pages, Ref(pagesNode));
        pdf.RootNumber = catalog;

        using var doc = PdfDocument.Open(pdf.Build());
        var cache = new ImageCache();
        var colors = new List<SkiaSharp.SKColor>();
        foreach (var page in doc.Pages)
        {
            using var bitmap = SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page), doc, page, 1, cache);
            colors.Add(bitmap.GetPixel(60, 140));
        }
        Assert.Equal(2 * 64 * 64 * 4, cache.Bytes);   // pages 1–3 share one image, page 4 has its own
        Assert.Equal(colors[0], colors[1]);
        Assert.NotEqual(colors[0], colors[3]);
    }

    /// <summary>
    /// An image larger than the whole budget, drawn three times: decoded once. The image just requested was evicted by its own request,
    /// and every drawing decoded it anew — a page with forty partly blacked-out copies of a large image took 18 s in the visibility check
    /// (docs/FEHLERGEDAECHTNIS.md, No. 409). It now stays until the next image needs the room.
    /// </summary>
    [Fact]
    public void ImageLargerThanTheBudget_DecodedOnceForRepeatedDrawings()
    {
        var pdf = TestPdf.Simple(1);
        var pixels = new byte[64 * 64 * 3];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
        var image = pdf.Add(FlateStream(pixels, Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(64)),
            ("Height", PdfInteger.Of(64)), ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", N("DeviceRGB")))));
        ((PdfDictionary)pdf.Get(2)).GetDictionary(PdfName.Resources)!.Set(N("XObject"), Dict(("I", Ref(image))));
        pdf.Replace(4, FlateStream(Encoding.ASCII.GetBytes("q 50 0 0 50 10 10 cm /I Do Q q 50 0 0 50 100 10 cm /I Do Q q 50 0 0 50 200 10 cm /I Do Q")));
        using var doc = PdfDocument.Open(pdf.Build());
        var cache = new ImageCache(1);
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        using (SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, cache)) { }
        using (SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, cache)) { }
        Assert.Equal(64 * 64, cache.DecodedPixels);
    }

    [Fact]
    public void EvictedImages_AreFreedOnlyAfterDrawing()
    {
        // Evicted images are released immediately, but never while another drawing pass is still painting them. Tiny
        // budget (every image evicts the others), eight pages with their own images in parallel: no crash, pixels as with a large budget
        var pdf = new TestPdf();
        var catalog = pdf.Add(Dict(("Type", N("Catalog"))));
        var pagesNode = pdf.Add(new PdfDictionary());
        var content = pdf.Add(FlateStream(Encoding.ASCII.GetBytes("q 180 0 0 180 10 10 cm /I Do Q")));
        var kids = new PdfArray();
        for (var p = 0; p < 8; p++)
        {
            var pixels = new byte[200 * 200 * 3];
            for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * (p + 3) + p * 40);
            var image = pdf.Add(FlateStream(pixels, Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(200)),
                ("Height", PdfInteger.Of(200)), ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", N("DeviceRGB")))));
            kids.Add(Ref(pdf.Add(Dict(("Type", N("Page")), ("Parent", Ref(pagesNode)), ("MediaBox", Box(0, 0, 200, 200)),
                ("Resources", Dict(("XObject", Dict(("I", Ref(image)))))), ("Contents", Ref(content))))));
        }
        pdf.Replace(pagesNode, Dict(("Type", N("Pages")), ("Kids", kids), ("Count", PdfInteger.Of(8))));
        ((PdfDictionary)pdf.Get(catalog)).Set(PdfName.Pages, Ref(pagesNode));
        pdf.RootNumber = catalog;

        using var doc = PdfDocument.Open(pdf.Build());
        var lists = doc.Pages.Select(page => PageInterpreter.Build(doc, page)).ToArray();
        string Sum(SkiaSharp.SKBitmap b) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b.Bytes));
        var expected = lists.Select((list, p) =>
        {
            using var b = SkiaRenderer.RenderPage(list, doc, doc.Pages[p], 1, new ImageCache());
            return Sum(b);
        }).ToArray();
        var tiny = new ImageCache(1);
        Parallel.For(0, 400, new ParallelOptions { MaxDegreeOfParallelism = 8 }, k =>
        {
            var p = k % 8;
            using var b = SkiaRenderer.RenderPage(lists[p], doc, doc.Pages[p], 1, tiny);
            Assert.Equal(expected[p], Sum(b));
        });
    }
}
