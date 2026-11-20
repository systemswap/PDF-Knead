using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;
using static PdfKnead.Tests.Pdf.ContentImageTests;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// „Dokument bereinigen“ → „Verdeckte Teile von Bildern“ (docs/RECHERCHE-VERSTECKEN.md, B-11): what a frame (clip) or the edge of the page hides
/// of an image leaves the file, and the page looks the same — also in a round frame. Images that cannot be cut (a stencil mask) stay and are
/// reported.
/// </summary>
public sealed class HiddenImagePartsTests
{
    /// <summary>An image of 10 × 10 pixels: blue where <paramref name="blue"/> says so, red elsewhere.</summary>
    private static PdfStream Image(Func<int, int, bool> blue)
    {
        var pixels = new byte[10 * 10 * 3];
        for (var y = 0; y < 10; y++)
            for (var x = 0; x < 10; x++)
                (pixels[(y * 10 + x) * 3], pixels[(y * 10 + x) * 3 + 1], pixels[(y * 10 + x) * 3 + 2]) = blue(x, y) ? ((byte)30, (byte)30, (byte)220) : ((byte)220, (byte)30, (byte)30);
        return TestPdf.FlateStream(pixels, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(10)), ("Height", PdfInteger.Of(10)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8))));
    }

    // a circle of radius 40 about (350, 550), as four Bézier curves
    private const string Circle = "390 550 m 390 572.09 372.09 590 350 590 c 327.91 590 310 572.09 310 550 c 310 527.91 327.91 510 350 510 c " +
                                  "372.09 510 390 527.91 390 550 c h W n ";

    /// <summary>
    /// The page: an image in a square frame (its blue rim hidden), one in a round frame (its blue rim hidden), one half beside the page (its blue
    /// half there), and one wholly visible with a blue half (stays as it is).
    /// </summary>
    private static PdfDocument Page(string extra = "", int? stencil = null) => PdfDocument.Open(File(extra, stencil));

    /// <summary>The page of <see cref="Page"/> as a file (also for the test of the window).</summary>
    internal static byte[] File(string extra = "", int? stencil = null)
    {
        var pdf = TestPdf.Simple(1);
        static bool Rim(int x, int y) => x == 0 || y == 0 || x == 9 || y == 9;
        var framed = pdf.Add(Image(Rim));
        var round = pdf.Add(Image(Rim));
        var beside = pdf.Add(Image((x, _) => x < 5));
        var whole = pdf.Add(Image((x, _) => x < 5));
        var xobjects = TestPdf.Dict(("A", TestPdf.Ref(framed)), ("B", TestPdf.Ref(round)), ("C", TestPdf.Ref(beside)), ("D", TestPdf.Ref(whole)));
        if (stencil is not null)
        {
            // a stencil mask that paints everywhere (bits 0 paint), and a JPEG whose data are broken
            var mask = pdf.Add(TestPdf.FlateStream(new byte[20], TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
                ("Width", PdfInteger.Of(10)), ("Height", PdfInteger.Of(10)), ("ImageMask", PdfBoolean.True), ("BitsPerComponent", PdfInteger.Of(1)))));
            xobjects.Set(PdfName.Get("M"), TestPdf.Ref(mask));
            var broken = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(10)),
                ("Height", PdfInteger.Of(10)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)), ("Filter", TestPdf.N("DCTDecode"))),
                "not a JPEG"u8.ToArray()));
            xobjects.Set(PdfName.Get("J"), TestPdf.Ref(broken));
        }
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes(
            "q 110 510 80 80 re W n q 100 0 0 100 100 500 cm /A Do Q Q\n" +
            "q " + Circle + "q 100 0 0 100 300 500 cm /B Do Q Q\n" +
            "q 100 0 0 100 -50 700 cm /C Do Q\n" +
            "q 100 0 0 100 400 200 cm /D Do Q\n" + extra)));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", xobjects)));
        return pdf.Build();
    }

    private static (byte[] File, PdfSaveResult Result) Clean(PdfDocument doc)
    {
        using var output = new MemoryStream();
        var result = PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions { Cleanup = CleanupItems.HiddenImageParts, ImageCodec = SkiaImageCodec.Instance });
        return (output.ToArray(), result);
    }

    /// <summary>Blue pixels in the images of the file (the hidden parts were blue).</summary>
    private static int BlueInImages(PdfDocument doc)
    {
        var blue = 0;
        for (var n = 1; n < doc.ObjectCount; n++)
            if (doc.GetObject(n) is PdfStream s && s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image" &&
                PdfImageDecoder.Decode(new ImageSource(s, null, null, false, 0), doc) is { Format: ImagePixels.Rgba } decoded)
                for (var k = 0; k < decoded.Width * decoded.Height; k++)
                    if (decoded.Pixels[k * 4 + 2] > 180 && decoded.Pixels[k * 4] < 80) blue++;
        return blue;
    }

    [Fact]
    public void HiddenParts_LeaveTheFile_PageLooksTheSame()
    {
        using var doc = Page();
        var finding = Assert.Single(DocumentCleanup.Inspect(doc, PageItem.From(doc)), f => f.Item == CleanupItems.HiddenImageParts);
        Assert.Equal(3, finding.Count);   // the wholly visible one does not count
        var (file, result) = Clean(doc);
        Assert.Empty(result.ImagePartsKept);
        using var cleaned = PdfDocument.Open(file);
        // only the wholly visible image still has blue: 5 × 10 pixels
        Assert.Equal(50, BlueInImages(cleaned));
        // looks the same: every pixel of the page (edges of the frames within a level of smoothing)
        using var before = Render(doc, doc.Pages[0]);
        using var after = Render(cleaned, cleaned.Pages[0]);
        var differ = 0;
        for (var y = 0; y < before.Height; y++)
            for (var x = 0; x < before.Width; x++)
            {
                SKColor a = before.GetPixel(x, y), b = after.GetPixel(x, y);
                if (Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue))) > 40) differ++;
            }
        Assert.True(differ <= 8, $"{differ} Pixel anders");
        Assert.Equal(SKColors.White, At(after, cleaned.Pages[0], 314, 514));   // outside the round frame, inside its square: still white
        Assert.True(Red(At(after, cleaned.Pages[0], 350, 550)));
        Assert.DoesNotContain(DocumentCleanup.Inspect(cleaned, PageItem.From(cleaned)), f => f.Item == CleanupItems.HiddenImageParts);
    }

    /// <summary>
    /// An image of few samples whose shape lies in a finer soft mask (a presentation program draws arrows like this: 2 × 2 samples, the arrow
    /// in the mask): the image is cut to whole samples, so the mask must be cut to exactly that part too — cut to the frame alone, it was
    /// stretched over the larger part and the shape slid out of its frame (the arrows vanished).
    /// </summary>
    [Fact]
    public void FineSoftMask_CutToTheSamePartAsTheImage_ShapeStays()
    {
        var pdf = TestPdf.Simple(1);
        // the mask: 40 × 40, opaque in a bar from column 4 to 35, rows 24 to 31 (in the lower half, where the frame is)
        var alpha = new byte[40 * 40];
        for (var y = 24; y < 32; y++)
            for (var x = 4; x < 36; x++) alpha[y * 40 + x] = 255;
        var mask = pdf.Add(TestPdf.FlateStream(alpha, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(40)), ("Height", PdfInteger.Of(40)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)))));
        var image = pdf.Add(TestPdf.FlateStream([200, 30, 30, 200, 30, 30, 200, 30, 30, 200, 30, 30], TestPdf.Dict(("Type", TestPdf.N("XObject")),
            ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(2)), ("ColorSpace", TestPdf.N("DeviceRGB")),
            ("BitsPerComponent", PdfInteger.Of(8)), ("SMask", TestPdf.Ref(mask)))));
        // drawn 200 × 200 at (100, 400): the bar lies at y 440 … 480, the frame shows y 420 … 460 (the lower half of the bar)
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes("q 100 420 200 40 re W n q 200 0 0 200 100 400 cm /P Do Q Q\n")));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("P", TestPdf.Ref(image))))));
        using var doc = PdfDocument.Open(pdf.Build());
        Assert.Single(DocumentCleanup.Inspect(doc, PageItem.From(doc)), f => f.Item == CleanupItems.HiddenImageParts);
        var (file, result) = Clean(doc);
        Assert.Empty(result.ImagePartsKept);
        using var cleaned = PdfDocument.Open(file);
        using var before = Render(doc, doc.Pages[0]);
        using var after = Render(cleaned, cleaned.Pages[0]);
        var differ = 0;
        for (var y = 0; y < before.Height; y++)
            for (var x = 0; x < before.Width; x++)
            {
                SKColor a = before.GetPixel(x, y), b = after.GetPixel(x, y);
                if (Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue))) > 40) differ++;
            }
        Assert.True(differ <= 8, $"{differ} Pixel anders");
        Assert.True(Red(At(after, cleaned.Pages[0], 200, 450)), "the bar is still there");
    }

    /// <summary>
    /// A grey JPEG with a finer soft mask, partly hidden by its frame: the colour stays a grey JPEG at its own size, the mask is cut in its own
    /// samples to the same part (docs/FEHLERGEDAECHTNIS.md, No. 291) — before, both were decoded into RGBA at the mask's size and written
    /// without loss. The page looks the same.
    /// </summary>
    [Fact]
    public void JpegWithFinerMask_StaysAGreyJpeg_MaskCutToo()
    {
        var gray = new byte[40 * 40];
        for (var y = 0; y < 40; y++)
            for (var x = 0; x < 40; x++) gray[y * 40 + x] = (byte)(x < 20 ? 40 : 200);
        var jpeg = SkiaImageCodec.Instance.EncodeJpeg(40, 40, 1, gray, 95)!;
        var alpha = new byte[80 * 80];
        for (var y = 0; y < 80; y++)
            for (var x = 0; x < 80; x++) alpha[y * 80 + x] = (byte)((x - 40) * (x - 40) + (y - 40) * (y - 40) < 36 * 36 ? 255 : 0);
        var pdf = TestPdf.Simple(1);
        var mask = pdf.Add(TestPdf.FlateStream(alpha, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(80)), ("Height", PdfInteger.Of(80)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)))));
        var image = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(40)),
            ("Height", PdfInteger.Of(40)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)), ("Filter", TestPdf.N("DCTDecode")),
            ("SMask", TestPdf.Ref(mask))), jpeg));
        // drawn 200 × 200 at (100, 400); the frame keeps the right half
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes("q 200 400 100 200 re W n q 200 0 0 200 100 400 cm /P Do Q Q\n")));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("P", TestPdf.Ref(image))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var (file, result) = Clean(doc);
        Assert.Empty(result.ImagePartsKept);
        using var cleaned = PdfDocument.Open(file);
        var written = Enumerable.Range(1, cleaned.ObjectCount + 8).Select(cleaned.GetObject).OfType<PdfStream>()
            .Single(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image" && s.Dictionary.ContainsKey(PdfName.Get("SMask")));
        Assert.Equal("DCTDecode", written.Dictionary.GetName(PdfName.Filter)?.Value);
        Assert.Equal("DeviceGray", written.Dictionary.GetName(PdfName.Get("ColorSpace"))?.Value);
        Assert.Equal(20, written.Dictionary.GetInteger(PdfName.Get("Width")));
        Assert.Equal(40, written.Dictionary.GetStream(PdfName.Get("SMask"))!.Dictionary.GetInteger(PdfName.Get("Width")));
        using var before = Render(doc, doc.Pages[0]);
        using var after = Render(cleaned, cleaned.Pages[0]);
        var differ = 0;
        for (var y = 0; y < before.Height; y++)
            for (var x = 0; x < before.Width; x++)
            {
                SKColor a = before.GetPixel(x, y), b = after.GetPixel(x, y);
                if (Math.Max(Math.Abs(a.Red - b.Red), Math.Max(Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue))) > 40) differ++;
            }
        Assert.True(differ <= 8, $"{differ} Pixel anders");
    }

    /// <summary>
    /// A stencil mask is cut in its own bits (G4) and keeps the colour of where it is drawn; an image that cannot be read stays as it is, and its
    /// page is reported.
    /// </summary>
    [Fact]
    public void StencilMaskCut_UnreadableImageStays_PageReported()
    {
        using var doc = Page("q 300 100 50 50 re W n 0 0 1 rg q 100 0 0 100 300 100 cm /M Do Q Q\n" +
                             "q 100 300 50 50 re W n q 100 0 0 100 100 300 cm /J Do Q Q\n", stencil: 1);
        var (file, result) = Clean(doc);
        Assert.Equal([0], result.ImagePartsKept);
        using var cleaned = PdfDocument.Open(file);
        var masks = Enumerable.Range(1, cleaned.ObjectCount - 1).Select(n => cleaned.GetObject(n)).OfType<PdfStream>()
            .Where(s => s.Dictionary.Get(PdfName.Get("ImageMask")) is PdfBoolean { Value: true }).ToList();
        var mask = Assert.Single(masks);
        Assert.Equal(5, mask.Dictionary.GetInteger(PdfName.Get("Width")));   // 50 of 100 pt: 5 of 10 bits
        Assert.Equal("CCITTFaxDecode", mask.Dictionary.GetName(PdfName.Filter)?.Value);
        using var bitmap = Render(cleaned, cleaned.Pages[0]);
        Assert.True(At(bitmap, cleaned.Pages[0], 325, 125) is { Blue: > 180, Red: < 80 }, "die Schablone malt in ihrer Farbe");
        Assert.Equal(SKColors.White, At(bitmap, cleaned.Pages[0], 375, 125));
    }

    /// <summary>Cut in its own samples at a column inside a byte (4 and 2 bits per sample, 3 components): every sample kept is the one it was.</summary>
    [Theory]
    [InlineData(4, 1)]
    [InlineData(2, 3)]
    [InlineData(16, 1)]
    public void OwnSamples_CutInsideAByte_Exact(int bpc, int components)
    {
        const int w = 13, h = 3;
        var rowBits = w * bpc * components;
        var rowBytes = (rowBits + 7) / 8;
        var data = new byte[rowBytes * h];
        int Sample(int x, int y, int c) => (x * 7 + y * 3 + c * 5) % (1 << Math.Min(bpc, 8));
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < components; c++)
                {
                    var bit = (long)(x * components + c) * bpc;
                    var v = Sample(x, y, c);
                    if (bpc == 16) { data[y * rowBytes + bit / 8] = 0; data[y * rowBytes + bit / 8 + 1] = (byte)v; continue; }
                    data[y * rowBytes + bit / 8] |= (byte)(v << (8 - bpc - (int)(bit % 8)));
                }
        var pdf = TestPdf.Simple(1);
        var stream = TestPdf.FlateStream(data, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(w)),
            ("Height", PdfInteger.Of(h)), ("ColorSpace", TestPdf.N(components == 3 ? "DeviceRGB" : "DeviceGray")), ("BitsPerComponent", PdfInteger.Of(bpc))));
        var number = pdf.Add(stream);
        using var doc = PdfDocument.Open(pdf.Build());
        var image = (PdfStream)doc.GetObject(number)!;
        // columns 3 … 9 (of 13), rows 1 … 2
        var cut = ImageCut.Cut(doc, image, null, 3.0 / w, 1.0 / h, 10.0 / w, 1.0)!;
        Assert.Equal(7, cut.Dictionary.GetInteger(PdfName.Get("Width")));
        Assert.Equal(2, cut.Dictionary.GetInteger(PdfName.Get("Height")));
        var cutBytes = (7 * bpc * components + 7) / 8;
        var samples = cut.RawData!;
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 7; x++)
                for (var c = 0; c < components; c++)
                {
                    var bit = (long)(x * components + c) * bpc;
                    var v = bpc == 16 ? samples[y * cutBytes + bit / 8 + 1] : (samples[y * cutBytes + bit / 8] >> (8 - bpc - (int)(bit % 8))) & ((1 << bpc) - 1);
                    Assert.Equal(Sample(x + 3, y + 1, c), v);
                }
    }

    /// <summary>
    /// One image drawn in many places, each in the same frame relative to it: cut once — every place draws the same cut image, which is in the
    /// file once (docs/FEHLERGEDAECHTNIS.md, No. 339: a page drawing one image with a large mask 40 times took 100 s; cut once, 9 s).
    /// </summary>
    [Fact]
    public void OneImageInManyPlaces_SameFrame_CutOnce()
    {
        var pdf = TestPdf.Simple(1);
        var image = pdf.Add(Image((x, y) => x == 0 || y == 0 || x == 9 || y == 9));
        var content = new StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            var (x, y) = (20 + i % 5 * 110, 100 + i / 5 * 110);
            content.Append(System.FormattableString.Invariant($"q {x + 10} {y + 10} 80 80 re W n q 100 0 0 100 {x} {y} cm /A Do Q Q\n"));
        }
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes(content.ToString())));
        ((PdfDictionary)pdf.Get(5)).Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("A", TestPdf.Ref(image))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var item = PageItem.From(doc)[0];
        var box = item.Page.CropBox;
        var (cut, done, skipped) = ContentObjectEditor.CutToVisible(item, RectD.FromLtrb(box.Left, box.Bottom, box.Right, box.Top), SkiaImageCodec.Instance);
        Assert.Equal(20, done);
        Assert.Equal(0, skipped);
        var (file, _) = Clean(doc);
        using var cleaned = PdfDocument.Open(file);
        Assert.Equal(0, BlueInImages(cleaned));
        var size = cleaned.Trailer.GetInteger(PdfName.Size) ?? 0;
        Assert.Equal(1, Enumerable.Range(1, (int)size - 1).Count(n => cleaned.GetObject(n) is PdfStream s && s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image"));
    }

    /// <summary>
    /// A large image drawn twenty times in a frame, each time a different part of it visible: the page cuts at most
    /// <see cref="ContentObjectEditor.MaxPageCropPixels"/> pixels, the images beyond stay whole and are counted. Each drawing was cut anew in
    /// full, and a damaged page with three thousand drawings of an image with a mask of 75 megapixels took minutes (found by the redaction focus
    /// of the fuzzer; docs/FEHLERGEDAECHTNIS.md, No. 388).
    /// </summary>
    [Fact]
    public void ManyDrawingsOfALargeImage_CutWithinABudgetPerPage()
    {
        var pdf = TestPdf.Simple(1);
        var image = pdf.Add(TestPdf.FlateStream(new byte[4000 * 4000], TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(4000)), ("Height", PdfInteger.Of(4000)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)))));
        var content = new StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            var (x, y) = (50 + i % 5 * 100, 100 + i / 5 * 150);
            content.Append(FormattableString.Invariant($"q {x} {y} {40 + i} 60 re W n 80 0 0 80 {x} {y} cm /Im1 Do Q "));
        }
        pdf.Replace(4, TestPdf.FlateStream(Encoding.ASCII.GetBytes(content.ToString())));
        pdf.Replace(5, TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(2)), ("Contents", TestPdf.Ref(4)),
            ("Resources", TestPdf.Dict(("XObject", TestPdf.Dict(("Im1", TestPdf.Ref(image))))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var (_, cut, skipped) = ContentObjectEditor.CutToVisible(PageItem.From(doc)[0], RectD.FromLtrb(0, 0, 595, 842), SkiaImageCodec.Instance);
        Assert.Equal(20, cut + skipped);
        Assert.True(cut <= ContentObjectEditor.MaxPageCropPixels / (4000 * 4000), $"{cut} zugeschnitten");
        Assert.True(skipped > 0);
    }

    /// <summary>
    /// The samples a page may black out (<see cref="ImageBlanker.PageSamples"/>): an image beyond them goes, as one that cannot be read — nothing of
    /// it stays under a bar (docs/FEHLERGEDAECHTNIS.md, No. 388).
    /// </summary>
    [Fact]
    public void BlackingOut_BeyondTheBudgetOfThePage_ImageGoes()
    {
        var pdf = TestPdf.Simple(1);
        var number = pdf.Add(Image((x, _) => x < 5));
        using var doc = PdfDocument.Open(pdf.Build());
        var image = (PdfStream)doc.GetObject(number)!;
        var toPage = Matrix.Scale(100, 100) * Matrix.Translate(100, 100);
        RectD[] bar = [RectD.FromLtrb(90, 90, 140, 210)];
        Assert.NotNull(ImageBlanker.TryBlank(doc, image, null, toPage, bar, out var handled, new ImageBlanker.PageBudget(100)));
        Assert.True(handled);
        Assert.Null(ImageBlanker.TryBlank(doc, image, null, toPage, bar, out handled, new ImageBlanker.PageBudget(99)));
        Assert.True(handled);   // handled: the image goes
    }

    /// <summary>
    /// An image whose soft mask is the image itself (a damaged file): cut, its mask is cut once and keeps no mask of its own — followed, it cut
    /// its mask without end until the stack ran out and the program ended (found by the redaction focus of the fuzzer;
    /// docs/FEHLERGEDAECHTNIS.md, No. 380).
    /// </summary>
    [Fact]
    public void ImageThatIsItsOwnMask_CutOnce()
    {
        var pdf = TestPdf.Simple(1);
        var number = pdf.Add(new PdfDictionary());
        var stream = TestPdf.FlateStream(Enumerable.Range(0, 16).Select(i => (byte)(i * 16)).ToArray(), TestPdf.Dict(("Type", TestPdf.N("XObject")),
            ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(4)), ("Height", PdfInteger.Of(4)), ("ColorSpace", TestPdf.N("DeviceGray")),
            ("BitsPerComponent", PdfInteger.Of(8)), ("SMask", TestPdf.Ref(number))));
        pdf.Replace(number, stream);
        using var doc = PdfDocument.Open(pdf.Build());
        var image = (PdfStream)doc.GetObject(number)!;
        var cut = ImageCut.Cut(doc, image, null, 0, 0, 0.5, 0.5);
        Assert.NotNull(cut);
        var mask = Assert.IsType<PdfStream>(cut.Dictionary.Get(PdfName.Get("SMask")));
        Assert.False(mask.Dictionary.ContainsKey(PdfName.Get("SMask")));
        Assert.Equal(2, mask.Dictionary.GetInteger(PdfName.Get("Width")));
    }
}
