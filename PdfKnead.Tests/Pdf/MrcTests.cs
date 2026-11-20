using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Text;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Text scans as MRC: text as masks per colour, small paper layer, OCR text kept; photos stay photos. See KONZEPT.md, section 5.12.</summary>
public sealed class MrcTests
{
    private const int W = 1700, H = 1100;   // just under 1.9 megapixels, drawn on 8.5 inches = 200 dpi

    /// <summary>Scanned letter: slightly yellowish paper with noise, black lines of "letters", a blue ballpoint stroke, a red stamp.</summary>
    private static byte[] Letter()
    {
        var rgb = new byte[W * H * 3];
        var random = new Random(7);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var o = (y * W + x) * 3;
                int noise = random.Next(-6, 7);
                (int r, int g, int b) = (246 + noise, 242 + noise, 230 + noise);
                // Lines: letters as small rectangles with gaps
                var line = (y - 120) % 60;
                if (y is > 120 and < 800 && line is > 10 and < 34 && x is > 120 and < 1500 && x % 22 < 13 && (x / 22 + y / 60) % 7 != 0) (r, g, b) = (25, 25, 30);
                // Signature: blue ballpoint
                var sy = 900 + (int)(25 * Math.Sin(x / 30.0));
                if (x is > 200 and < 700 && Math.Abs(y - sy) < 3) (r, g, b) = (40, 60, 170);
                // Stamp: red ring
                var d = Math.Sqrt((x - 1300) * (x - 1300) + (y - 920) * (y - 920));
                if (d is > 80 and < 92) (r, g, b) = (200, 40, 45);
                (rgb[o], rgb[o + 1], rgb[o + 2]) = ((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255));
            }
        return rgb;
    }

    private static byte[] Photo()
    {
        var rgb = new byte[W * H * 3];
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
                for (var c = 0; c < 3; c++)
                    rgb[(y * W + x) * 3 + c] = (byte)(128 + 110 * Math.Sin(x / (23.0 + c * 7)) * Math.Cos(y / 31.0));
        return rgb;
    }

    /// <summary>Builds a page with the image as JPEG (as a scanner stores it) and, as after OCR, invisible text above it.</summary>
    private static byte[] ScanPdf(byte[] rgb)
    {
        var rgba = new byte[W * H * 4];
        for (var i = 0; i < W * H; i++) (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]) = (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2], 255);
        var jpeg = Rendering.SkiaImageCodec.Instance.EncodeJpeg(W, H, 4, rgba, 90)!;
        var pdf = TestPdf.Simple(1, null);
        var image = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(W)),
            ("Height", PdfInteger.Of(H)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)), ("Filter", TestPdf.N("DCTDecode"))), jpeg));
        var content = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(
            "q 612 0 0 396 0 300 cm /Im0 Do Q BT 3 Tr /F1 12 Tf 60 650 Td (Max Mustermann Zeugnis) Tj ET")));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Contents, TestPdf.Ref(content));
        page.Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image)))), ("Font", TestPdf.Dict(("F1", TestPdf.Ref(3))))));
        return pdf.Build();
    }

    private static (PdfDocument Doc, PdfStream XObject, long Size) Compress(byte[] file, CompressionSettings settings)
    {
        using var original = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(original, PageItem.From(original), output, new PdfSaveOptions { Compression = settings, ImageCodec = Rendering.SkiaImageCodec.Instance });
        var doc = PdfDocument.Open(output.ToArray());
        return (doc, (PdfStream)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Im0"))!, output.Length);
    }

    [Fact]
    public void Segmenter_FindsInkColours_KeepsPaperOut()
    {
        var layers = ScanSegmenter.Split(Letter(), W, H, 3, new ScanOptions())!;
        Assert.False(layers.Gray);
        Assert.InRange(layers.InkShare, 0.01, 0.2);
        // Black, blue and red each as a separate colour
        Assert.Equal(3, layers.Masks.Count);
        Assert.Contains(layers.Masks, m => m.R < 70 && m.G < 70 && m.B < 70);
        Assert.Contains(layers.Masks, m => m.B > m.R + 60);
        Assert.Contains(layers.Masks, m => m.R > m.B + 80);
        // One colour: everything in one mask
        Assert.Single(ScanSegmenter.Split(Letter(), W, H, 3, new ScanOptions(MaxColors: 1))!.Masks);
        // Not a text scan
        Assert.Null(ScanSegmenter.Split(Photo(), W, H, 3, new ScanOptions()));
    }

    /// <summary>
    /// Small print as a scanner sees it: strokes three pixels wide, blurred over their neighbours, so only the middle of a stroke is as dark
    /// as the ink. The text colour carries the ink of the scan — mask pixels × (paper − colour) within 5 % of what the strokes had, at single
    /// and double resolution — and is not darker than the darkest pixels. The mean colour of the darker pixels alone gave about a fifth
    /// less (docs/FEHLERGEDAECHTNIS.md, No. 876).
    /// </summary>
    [Fact]
    public void BlurredSmallPrint_TextColourKeepsTheInk()
    {
        const int w = 1000, h = 600, paper = 245, inkLevel = 20;
        var sharp = new byte[w * h];
        Array.Fill(sharp, (byte)paper);
        // letters: cells of 20 × 28 pixels with two upright strokes and one across, three pixels wide, in lines
        for (var top = 40; top + 28 < h - 40; top += 44)
            for (var left = 40; left + 20 < w - 40; left += 26)
                for (var y = top; y < top + 28; y++)
                    for (var x = left; x < left + 20; x++)
                        if (x - left < 3 || x - left >= 17 || Math.Abs(y - top - 14) <= 1) sharp[y * w + x] = inkLevel;
        var random = new Random(11);
        var rgb = new byte[w * h * 3];
        long scanInk = 0;
        var darkest = 255;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                // the scanner's blur: the mean over 3 × 3 pixels; then a little noise
                var total = 0;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                        total += sharp[Math.Clamp(y + dy, 0, h - 1) * w + Math.Clamp(x + dx, 0, w - 1)];
                var blurred = (total + 4) / 9;
                scanInk += paper - blurred;
                var v = (byte)Math.Clamp(blurred + random.Next(-3, 4), 0, 255);
                darkest = Math.Min(darkest, v);
                rgb[(y * w + x) * 3] = rgb[(y * w + x) * 3 + 1] = rgb[(y * w + x) * 3 + 2] = v;
            }
        foreach (var scale in new[] { 1, 2 })
        {
            var layers = ScanSegmenter.Split(rgb, w, h, 3, new ScanOptions(MaxColors: 1, MaskScale: scale))!;
            Assert.True(layers.Gray);
            var mask = Assert.Single(layers.Masks);
            long pixels = mask.Bits.Sum(b => 8 - System.Numerics.BitOperations.PopCount(b));
            var carried = pixels / (double)(scale * scale) * (paper - mask.R);
            Assert.InRange(carried / scanInk, 0.95, 1.05);
            Assert.True(mask.R >= darkest, $"Farbe {mask.R}, dunkelstes Pixel {darkest}");
        }
    }

    /// <summary>
    /// Black text on slightly bluish paper: the background keeps its colour; computing the page as black and white would make it grey,
    /// including the paper. Fine masks (double resolution) have twice the size in each direction; the text does not become bolder.
    /// </summary>
    [Fact]
    public void TintedPaper_KeepsItsTint_FineMasksKeepTheWeight()
    {
        var rgb = Letter();
        for (var i = 0; i < W * H; i++)
            if (rgb[i * 3] > 150) (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (238, 241, 250);   // paper only; pen and stamp become black like the text
            else if (rgb[i * 3 + 2] > rgb[i * 3] + 40 || rgb[i * 3] > rgb[i * 3 + 2] + 40) (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (25, 25, 30);
        var layers = ScanSegmenter.Split(rgb, W, H, 3, new ScanOptions())!;
        Assert.False(layers.Gray);
        Assert.True(layers.Background[2] > layers.Background[0] + 6, $"Papier {layers.Background[0]} {layers.Background[1]} {layers.Background[2]}");
        var fine = ScanSegmenter.Split(rgb, W, H, 3, new ScanOptions(MaskScale: 2))!;
        Assert.Equal(2, fine.MaskScale);
        Assert.Equal((W * 2 + 7) / 8 * H * 2, fine.Masks[0].Bits.Length);
        static long Ink(ScanLayers l) => l.Masks.Sum(m => m.Bits.Sum(b => 8 - System.Numerics.BitOperations.PopCount(b)));
        // Same area: double resolution has four times as many pixels; at most 10 % deviation (the edges become more precise)
        var ratio = Ink(fine) / (4.0 * Ink(layers));
        Assert.InRange(ratio, 0.9, 1.1);
    }

    /// <summary>
    /// Wide scan (5000 px, 300 dpi) with free-standing thin lines (horizontal signature line, vertical frame) and small dots
    /// (3 × 3, decimal points): all of it in the text mask. Lines must not count as "bars" (background, blurred), and the minimum size
    /// must not grow with the image width (at 5000 px it would be 16 pixels and the dots would be lost). A thick bar stays in the background.
    /// </summary>
    [Fact]
    public void ThinLinesAndSmallDots_StayInTheMask()
    {
        const int w = 5000, h = 1500;
        var rgb = new byte[w * h * 3];
        Array.Fill(rgb, (byte)245);
        void Ink(int x0, int y0, int x1, int y1)
        {
            for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++) (rgb[(y * w + x) * 3], rgb[(y * w + x) * 3 + 1], rgb[(y * w + x) * 3 + 2]) = (20, 20, 25);
        }
        for (var line = 0; line < 12; line++)
            for (var x = 200; x < 4700; x += 22)
                if ((x / 22 + line) % 6 != 0) Ink(x, 100 + line * 60, x + 12, 100 + line * 60 + 24);   // "letters"
        Ink(500, 1000, 1500, 1003);   // signature line
        Ink(4800, 100, 4803, 900);    // vertical frame
        for (var d = 0; d < 20; d++) Ink(2000 + d * 40, 1100, 2003 + d * 40, 1103);   // decimal points 3 × 3
        Ink(3000, 1200, 4500, 1400);  // bar, 1.7 cm thick
        var layers = ScanSegmenter.Split(rgb, w, h, 3, new ScanOptions(Ppi: 300))!;
        bool Black(int x, int y) => layers.Masks.Any(m => (m.Bits[y * ((w + 7) / 8) + (x >> 3)] & (0x80 >> (x & 7))) == 0);
        Assert.True(Black(1000, 1001), "Unterschriftslinie");
        Assert.True(Black(4801, 500), "Rahmen");
        Assert.Equal(20, Enumerable.Range(0, 20).Count(d => Black(2001 + d * 40, 1101)));
        Assert.False(Black(3700, 1300), "Balken gehört in den Hintergrund");
    }

    /// <summary>
    /// Flatten paper (off by default in <see cref="ScanOptions"/>; the compression settings switch it on by default): the noise of the
    /// paper disappears in the background, while the red stamp (clearly away from the paper tone) stays; without the option the noise stays.
    /// </summary>
    [Fact]
    public void FlattenPaper_RemovesNoise_KeepsColour()
    {
        static double Spread(ScanLayers l)
        {
            // Spread of the luminance in a pure paper area at the top left (background at full size)
            var values = new List<int>();
            for (var y = 20; y < 100; y++)
                for (var x = 20; x < 100; x++) values.Add(l.Background[(y * W + x) * 3 + 1]);
            var mean = values.Average();
            return Math.Sqrt(values.Average(v => (v - mean) * (v - mean)));
        }
        var plain = ScanSegmenter.Split(Letter(), W, H, 3, new ScanOptions())!;
        var flat = ScanSegmenter.Split(Letter(), W, H, 3, new ScanOptions(FlattenPaper: true))!;
        Assert.True(Spread(plain) > 2, $"ohne: {Spread(plain):0.0}");
        Assert.True(Spread(flat) < 1, $"mit: {Spread(flat):0.0}");
        Assert.Equal(plain.Masks.Count, flat.Masks.Count);
    }

    /// <summary>
    /// Crafted dot screen (every second pixel of every second row black): passes the paper checks but has millions of specks.
    /// Without an early abort this costs 27 bytes per pixel (4000² → 439 MB, 8000² → 1.8 GB); the segmenter aborts as soon as there
    /// are too many specks.
    /// </summary>
    [Fact]
    public void DotRaster_IsRejected_WithoutGigabytes()
    {
        const int size = 4000;
        var gray = new byte[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                gray[y * size + x] = (byte)(x % 2 == 0 && y % 2 == 0 ? 0 : 245);
        var before = GC.GetAllocatedBytesForCurrentThread();   // this thread only: parallel tests allocate at the same time
        Assert.Null(ScanSegmenter.Split(gray, size, size, 1, new ScanOptions()));
        var used = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(used < 150_000_000, $"{used / 1_000_000} MB");
    }

    /// <summary>E-book: the letter becomes a form XObject of background and masks, much smaller than "as photos"; text and stamp match, the OCR text stays.</summary>
    [Fact]
    public void LetterScan_BecomesLayers_TextKept()
    {
        var file = ScanPdf(Letter());
        var ebook = CompressionSettings.For(CompressionPreset.EBook);
        Assert.Equal(ScanMethod.Mrc, ebook.Scans);
        var (mrc, form, mrcSize) = Compress(file, ebook);
        var (photos, image, photoSize) = Compress(file, ebook with { Scans = ScanMethod.AsPhotos });
        using (mrc)
        using (photos)
        using (var original = PdfDocument.Open(file))
        {
            Assert.Equal("Form", form.Dictionary.GetName(PdfName.Subtype)?.Value);
            var xobjects = form.Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!;
            Assert.Equal(4, xobjects.Count);   // background + 3 text colours
            Assert.Equal("JBIG2Decode", ((PdfStream)xobjects.Get(PdfName.Get("M0"))!).Dictionary.GetName(PdfName.Filter)?.Value);   // masks encoded with JBIG2
            Assert.Equal("Image", image.Dictionary.GetName(PdfName.Subtype)?.Value);
            Assert.True(mrcSize < photoSize, $"MRC {mrcSize} gegen {photoSize}");
            Assert.Equal(PageText.Create(Core.Pdf.Content.PageInterpreter.Build(original, original.Pages[0]).Text).Text,
                PageText.Create(Core.Pdf.Content.PageInterpreter.Build(mrc, mrc.Pages[0]).Text).Text);
            var difference = CompressionWriterTests.Difference(original, mrc);
            Assert.True(difference < 3, $"mittlere Abweichung {difference:0.00}");
        }
    }

    /// <summary>
    /// A soft mask that looks like a text scan stays an image (an /SMask must be an image): scans are found via the drawn
    /// images, not via all objects of the file.
    /// </summary>
    [Fact]
    public void SoftMaskLikeAScan_StaysAnImage()
    {
        var rgb = Letter();
        var gray = new byte[W * H];
        for (var i = 0; i < W * H; i++) gray[i] = (byte)((rgb[i * 3] * 77 + rgb[i * 3 + 1] * 150 + rgb[i * 3 + 2] * 29) >> 8);
        var pdf = TestPdf.Simple(1, null);
        var mask = pdf.Add(TestPdf.FlateStream(gray, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(W)),
            ("Height", PdfInteger.Of(H)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(8)))));
        var color = new byte[W * H * 3];
        Array.Fill(color, (byte)90);
        var image = pdf.Add(TestPdf.FlateStream(color, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(W)),
            ("Height", PdfInteger.Of(H)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8)), ("SMask", TestPdf.Ref(mask)))));
        var content = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes("q 612 0 0 396 0 300 cm /Im0 Do Q")));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Contents, TestPdf.Ref(content));
        page.Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        var (doc, xobject, _) = Compress(pdf.Build(), CompressionSettings.For(CompressionPreset.EBook));
        using (doc)
        {
            var smask = (PdfStream)xobject.Dictionary.Get(PdfName.Get("SMask"))!;
            Assert.Equal("Image", smask.Dictionary.GetName(PdfName.Subtype)?.Value);
        }
    }

    /// <summary>
    /// Scan in a calibrated colour space (here CalRGB; ICC likewise): the background keeps it, and the text colours are set in it
    /// (cs/sc); with DeviceRGB the colours would shift. /StructParent stays.
    /// </summary>
    [Fact]
    public void CalibratedScan_KeepsItsColourSpace()
    {
        var file = ScanPdf(Letter());
        using (var original = PdfDocument.Open(file))
        {
            var image = (PdfStream)original.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Im0"))!;
            var cal = new PdfDictionary();
            cal.Set(PdfName.Get("WhitePoint"), new PdfArray([new PdfReal(0.9505), PdfInteger.Of(1), new PdfReal(1.089)]));
            cal.Set(PdfName.Get("Gamma"), new PdfArray([new PdfReal(2.2), new PdfReal(2.2), new PdfReal(2.2)]));
            image.Dictionary.Set(PdfName.Get("ColorSpace"), new PdfArray([PdfName.Get("CalRGB"), cal]));
            image.Dictionary.Set(PdfName.Get("StructParent"), PdfInteger.Of(7));
            using var saved = new MemoryStream();
            PdfWriter.Write(original, PageItem.From(original), saved, new PdfSaveOptions());
            file = saved.ToArray();
        }
        var (mrc, form, _) = Compress(file, CompressionSettings.For(CompressionPreset.EBook));
        using (mrc)
        using (var original = PdfDocument.Open(file))
        {
            Assert.Equal("Form", form.Dictionary.GetName(PdfName.Subtype)?.Value);
            Assert.Equal(7, form.Dictionary.GetInteger(PdfName.Get("StructParent")));
            var xobjects = form.Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!;
            var background = (PdfStream)xobjects.Get(PdfName.Get("Bg"))!;
            Assert.Equal("CalRGB", ((background.Dictionary.Get(PdfName.Get("ColorSpace")) as PdfArray)?.Get(0) as PdfName)?.Value);
            Assert.Contains("/Cs0 cs", System.Text.Encoding.ASCII.GetString(mrc.DecodeStream(form).Data));
            var difference = CompressionWriterTests.Difference(original, mrc);
            Assert.True(difference < 3, $"mittlere Abweichung {difference:0.00}");
        }
    }

    /// <summary>
    /// Photo with a light sky (phase 9.1, found with a magazine page of the public PDF/UA references): light, even "paper" over most of the
    /// image, few intermediate tones, the dark objects (a jacket, a tree, a rendered body) each too small to count as a large solid area —
    /// as text masks they were posterised into four flat colours. Most of their ink is surface, not stroke: not a text scan.
    /// </summary>
    private static byte[] PhotoWithSky()
    {
        var rgb = new byte[W * H * 3];
        var random = new Random(11);
        for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var o = (y * W + x) * 3;
                // Sky from light blue to paler towards the horizon, below it a light grey pavement
                (int r, int g, int b) = y < 700 ? (190 + y / 20, 215 + y / 40, 240) : (205, 205, 200);
                var noise = random.Next(-3, 4);
                // Dark objects: ellipses with shading (light from the left) and texture, each below the size of a "blob" (2 % of the image)
                for (var k = 0; k < 10; k++)
                {
                    double cx = 160 + k * 150, cy = 350 + (k % 3) * 250, dx = (x - cx) / 70, dy = (y - cy) / 95;
                    if (dx * dx + dy * dy > 1) continue;
                    var shade = 30 + (int)(40 * (dx + 1)) + random.Next(-12, 13);
                    (r, g, b) = k % 2 == 0 ? (shade / 2, shade, shade / 3) : (shade / 2, shade / 2, shade + 30);
                    break;
                }
                (rgb[o], rgb[o + 1], rgb[o + 2]) = ((byte)Math.Clamp(r + noise, 0, 255), (byte)Math.Clamp(g + noise, 0, 255), (byte)Math.Clamp(b + noise, 0, 255));
            }
        return rgb;
    }

    [Fact]
    public void PhotoWithLightSky_IsNoTextScan()
    {
        Assert.Null(ScanSegmenter.Split(PhotoWithSky(), W, H, 3, new ScanOptions(Ppi: 200)));
        var screen = CompressionSettings.For(CompressionPreset.Screen);
        Assert.Equal(ScanMethod.Mrc, screen.Scans);
        var (doc, image, _) = Compress(ScanPdf(PhotoWithSky()), screen);
        using (doc) Assert.Equal("Image", image.Dictionary.GetName(PdfName.Subtype)?.Value);
        // A letter with solid letters and a stamp still becomes layers
        Assert.NotNull(ScanSegmenter.Split(Letter(), W, H, 3, new ScanOptions(Ppi: 200)));
    }

    /// <summary>A photo is not a text scan and stays an image; "Print" and "Lossless" never produce MRC.</summary>
    [Fact]
    public void Photo_AndPrintPreset_StayImages()
    {
        var (photo, image, _) = Compress(ScanPdf(Photo()), CompressionSettings.For(CompressionPreset.EBook));
        using (photo) Assert.Equal("Image", image.Dictionary.GetName(PdfName.Subtype)?.Value);
        Assert.Equal(ScanMethod.AsPhotos, CompressionSettings.For(CompressionPreset.Print).Scans);
        var (print, kept, _) = Compress(ScanPdf(Letter()), CompressionSettings.For(CompressionPreset.Print));
        using (print) Assert.Equal("Image", kept.Dictionary.GetName(PdfName.Subtype)?.Value);
    }
}
