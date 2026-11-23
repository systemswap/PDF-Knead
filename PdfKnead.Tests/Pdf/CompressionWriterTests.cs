using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>Compressing whole files: duplicates, images, Brotli.</summary>
public sealed class CompressionWriterTests
{
    private static readonly PdfName Im = PdfName.Get("Im0");

    /// <summary>Builds an A4 page with one image, drawn <paramref name="inches"/> inches wide (twice if <paramref name="second"/> is given).</summary>
    internal static byte[] WithImage(int width, int height, int components, Func<int, int, int, byte> sample, double inches, int bpc = 8,
        double? second = null, string? caption = null)
    {
        var pdf = TestPdf.Simple(1, null);
        var rowBytes = bpc == 1 ? (width + 7) / 8 : width * components;
        var data = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if (bpc == 1)
                {
                    if (sample(x, y, 0) != 0) data[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
                else
                {
                    for (var c = 0; c < components; c++) data[y * rowBytes + x * components + c] = sample(x, y, c);
                }
        var image = pdf.Add(TestPdf.FlateStream(data, TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(width)), ("Height", PdfInteger.Of(height)), ("BitsPerComponent", PdfInteger.Of(bpc)),
            ("ColorSpace", TestPdf.N(components == 1 ? "DeviceGray" : "DeviceRGB")))));
        var pt = inches * 72;
        var draw = FormattableString.Invariant($"q {pt} 0 0 {pt * height / width} 72 300 cm /Im0 Do Q");
        if (second is { } other) draw += FormattableString.Invariant($" q {other * 72} 0 0 {other * 72 * height / width} 300 100 cm /Im0 Do Q");
        if (caption is not null) draw += $" BT /F1 24 Tf 72 200 Td ({caption}) Tj ET";
        var content = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(draw)));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Contents, TestPdf.Ref(content));
        page.Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image)))), ("Font", TestPdf.Dict(("F1", TestPdf.Ref(3))))));
        return pdf.Build();
    }

    private static (PdfDocument Doc, PdfDictionary Image) Compress(byte[] file, CompressionSettings settings)
    {
        using var original = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(original, PageItem.From(original), output, new PdfSaveOptions { Compression = settings, ImageCodec = Rendering.SkiaImageCodec.Instance });
        var doc = PdfDocument.Open(output.ToArray());
        var image = (PdfStream)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(Im)!;
        return (doc, image.Dictionary);
    }

    internal static double Difference(PdfDocument a, PdfDocument b, double scale = 0.5)
    {
        using var pa = Rendering.RasterExport.Render(PageItem.From(a)[0], scale, new Rendering.ImageCache());
        using var pb = Rendering.RasterExport.Render(PageItem.From(b)[0], scale, new Rendering.ImageCache());
        long sum = 0;
        for (var y = 0; y < pa.Height; y++)
            for (var x = 0; x < pa.Width; x++)
            {
                var (p, q) = (pa.GetPixel(x, y), pb.GetPixel(x, y));
                sum += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
            }
        return sum / (3.0 * pa.Width * pa.Height);
    }

    [Fact]
    public void Photo_TooSharp_BecomesJpegAtTheTargetResolution()
    {
        // 2400 px on 2 inches = 1200 ppi; e-book: 150 dpi → 300 px
        var file = WithImage(2400, 1600, 3, (x, y, c) => (byte)((x * (c + 1) + y * 3 + (x * y) % 97) & 255), inches: 2);
        var (doc, image) = Compress(file, CompressionSettings.For(CompressionPreset.EBook));
        using (doc)
        {
            Assert.Equal(300, image.GetInteger(PdfName.Get("Width")));
            Assert.Equal(200, image.GetInteger(PdfName.Get("Height")));
            Assert.Equal("DCTDecode", image.GetName(PdfName.Filter)?.Value);
            Assert.True(doc.FileSize < file.Length / 10, $"{doc.FileSize} von {file.Length}");
        }
        // Lossless: image unchanged
        var (same, kept) = Compress(file, CompressionSettings.For(CompressionPreset.Lossless));
        using (same) Assert.Equal(2400, kept.GetInteger(PdfName.Get("Width")));
    }

    /// <summary>The largest use counts: drawn once small, once large → as many pixels as the large use needs.</summary>
    [Fact]
    public void LargestUse_Decides()
    {
        // 1200 px: 1 inch = 1200 ppi, 4 inches = 300 ppi → 300 ppi counts; target 150 dpi on 4 inches = 600 px
        var file = WithImage(1200, 1200, 3, (x, y, c) => (byte)((x * 7 + y * 13 + c * 50 + x * y) & 255), inches: 1, second: 4);
        var (doc, image) = Compress(file, CompressionSettings.For(CompressionPreset.EBook));
        using (doc) Assert.Equal(600, image.GetInteger(PdfName.Get("Width")));
    }

    [Fact]
    public void Graphic_FewColors_StaysLossless()
    {
        var file = WithImage(2000, 2000, 3, (x, y, c) => (byte)(((x / 100 + y / 100) % 4) * 60 + c * 10), inches: 1);
        var (doc, image) = Compress(file, CompressionSettings.For(CompressionPreset.Screen));
        using (doc)
        {
            Assert.Equal("FlateDecode", image.GetName(PdfName.Filter)?.Value);
            Assert.True(image.GetInteger(PdfName.Get("Width")) < 2000);
        }
    }

    /// <summary>
    /// Black and white as a greyscale image (scan): 1 bit, pixel-identical; as JBIG2 (the default) about a third smaller than G4; a 1-bit
    /// image with Flate likewise.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(1)]
    public void BlackAndWhite_BecomesOneBit_Lossless(int bpc)
    {
        var file = WithImage(1700, 2200, 1, (x, y, _) => (byte)(Ink(x, y) ? 0 : 255), inches: 5.67, bpc: bpc);
        var lossless = CompressionSettings.For(CompressionPreset.Lossless);
        Assert.Equal(BilevelMethod.Jbig2, lossless.BilevelCodec);
        var (jbig2, image) = Compress(file, lossless);
        var (g4, g4Image) = Compress(file, lossless with { BilevelCodec = BilevelMethod.G4 });
        using (jbig2)
        using (g4)
        using (var original = PdfDocument.Open(file))
        {
            Assert.Equal("JBIG2Decode", image.GetName(PdfName.Filter)?.Value);
            Assert.Equal("CCITTFaxDecode", g4Image.GetName(PdfName.Filter)?.Value);
            // ISO 32000-2, Table 11 (Bericht M2, Zeilen 21, 26): the own writer codes Group 4 — /K −1 — with 0 as black, /BlackIs1 left out
            // (false); pixel-identical below
            var parms = g4Image.GetDictionary(PdfName.DecodeParms)!;
            Assert.Equal(-1, parms.GetInteger(PdfName.Get("K")));
            Assert.True(parms.Get(PdfName.Get("BlackIs1")) is PdfNull or PdfBoolean { Value: false });
            Assert.Equal(1, image.GetInteger(PdfName.Get("BitsPerComponent")));
            Assert.Equal(0, Difference(original, jbig2));
            Assert.Equal(0, Difference(original, g4));
            Assert.True(g4.FileSize < file.Length, $"{g4.FileSize} von {file.Length}");
            Assert.True(jbig2.FileSize < g4.FileSize * 0.9, $"JBIG2 {jbig2.FileSize}, G4 {g4.FileSize}");
        }
    }

    /// <summary>
    /// More than 65 536 pixels wide (long plan, roll scan): stays as it is. Capping the width would rewrite the image with wrongly
    /// wrapped rows (1 bit as JBIG2, 8 bits with /Width 65536).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void VeryWideImage_StaysAsItIs(int bpc)
    {
        var file = WithImage(70_000, 16, 1, (x, y, _) => (byte)((x / 7 + y) % 3 == 0 ? 0 : 255), inches: 20, bpc: bpc);
        foreach (var preset in new[] { CompressionPreset.Lossless, CompressionPreset.EBook })
        {
            var (doc, image) = Compress(file, CompressionSettings.For(preset));
            using (doc)
            {
                Assert.Equal(70_000, image.GetInteger(PdfName.Get("Width")));
                Assert.Equal("FlateDecode", image.GetName(PdfName.Filter)?.Value);
            }
        }
    }

    /// <summary>G4 with /Columns wider than /Width (already shifted in other readers): stays as it is instead of being repacked askew.</summary>
    [Fact]
    public void G4WithOtherColumns_StaysAsItIs()
    {
        const int w = 400, h = 300, columns = 416;
        var rowBytes = (columns + 7) / 8;
        var bits = new byte[rowBytes * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < columns; x++)
                if ((x / 5 + y / 7) % 2 == 0) bits[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
        var pdf = TestPdf.Simple(1, null);
        var image = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(w)),
            ("Height", PdfInteger.Of(h)), ("ColorSpace", TestPdf.N("DeviceGray")), ("BitsPerComponent", PdfInteger.Of(1)), ("Filter", TestPdf.N("CCITTFaxDecode")),
            ("DecodeParms", TestPdf.Dict(("K", PdfInteger.Of(-1)), ("Columns", PdfInteger.Of(columns)), ("Rows", PdfInteger.Of(h))))),
            CcittEncoder.EncodeG4(bits, columns, h, rowBytes, blackIs1: false)));
        var content = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes("q 400 0 0 300 72 100 cm /Im0 Do Q")));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Contents, TestPdf.Ref(content));
        page.Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        var (doc, result) = Compress(pdf.Build(), CompressionSettings.For(CompressionPreset.Lossless));
        using (doc) Assert.Equal("CCITTFaxDecode", result.GetName(PdfName.Filter)?.Value);
    }

    /// <summary>Scanned text page with some edge noise: with lossy "nearly black and white" → 1 bit.</summary>
    [Fact]
    public void NearlyBlackAndWhite_Scan_BecomesOneBit()
    {
        var file = WithImage(1700, 2200, 1, (x, y, _) => Ink(x, y) ? (byte)0 : (x * 7 + y * 13) % 401 == 0 ? (byte)128 : (byte)255, inches: 5.67);
        var (doc, image) = Compress(file, CompressionSettings.For(CompressionPreset.EBook));
        using (doc)
            Assert.Equal("JBIG2Decode", image.GetName(PdfName.Filter)?.Value);
    }

    /// <summary>
    /// An existing G4 image (also as a stencil mask with /Decode) is repacked with JBIG2: the same bits, smaller; with G4 as the method
    /// it stays as it is. Afterwards the JBIG2 image does not come out larger when packed with JBIG2 again.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingG4_BecomesJbig2_SameBits(bool stencil)
    {
        const int w = 1700, h = 2200;
        var rowBytes = (w + 7) / 8;
        var bits = new byte[rowBytes * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (!Ink(x, y)) bits[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));
        var g4 = CcittEncoder.EncodeG4(bits, w, h, rowBytes, blackIs1: false);
        var pdf = TestPdf.Simple(1, null);
        var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(w)), ("Height", PdfInteger.Of(h)),
            ("Filter", TestPdf.N("CCITTFaxDecode")),
            ("DecodeParms", TestPdf.Dict(("K", PdfInteger.Of(-1)), ("Columns", PdfInteger.Of(w)), ("Rows", PdfInteger.Of(h)))));
        if (stencil)
        {
            dict.Set(PdfName.Get("ImageMask"), PdfBoolean.True);
            dict.Set(PdfName.Get("Decode"), new PdfArray([PdfInteger.Of(1), PdfInteger.Of(0)]));
        }
        else
        {
            dict.Set(PdfName.Get("ColorSpace"), TestPdf.N("DeviceGray"));
            dict.Set(PdfName.Get("BitsPerComponent"), PdfInteger.Of(1));
        }
        var image = pdf.Add(new PdfStream(dict, g4));
        var content = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes("0 0 1 rg q 408 0 0 528 72 100 cm /Im0 Do Q")));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Contents, TestPdf.Ref(content));
        page.Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        var file = pdf.Build();

        var lossless = CompressionSettings.For(CompressionPreset.Lossless);
        var (doc, result) = Compress(file, lossless);
        var (kept, keptImage) = Compress(file, lossless with { BilevelCodec = BilevelMethod.G4 });
        using (doc)
        using (kept)
        using (var original = PdfDocument.Open(file))
        {
            Assert.Equal("JBIG2Decode", result.GetName(PdfName.Filter)?.Value);
            Assert.False(result.ContainsKey(PdfName.DecodeParms));
            Assert.Equal(stencil, result.GetBoolean(PdfName.Get("ImageMask")) ?? false);
            Assert.Equal(0, Difference(original, doc, 1));
            Assert.True(doc.FileSize < file.Length * 0.85, $"{doc.FileSize} von {file.Length}");
            Assert.Equal("CCITTFaxDecode", keptImage.GetName(PdfName.Filter)?.Value);
            // Once more: JBIG2 stays JBIG2 (does not grow)
            using var again = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), again, new PdfSaveOptions { Compression = lossless, ImageCodec = Rendering.SkiaImageCodec.Instance });
            Assert.True(again.Length <= doc.FileSize + 64, $"{again.Length} gegen {doc.FileSize}");
        }
    }

    /// <summary>
    /// No black and white where there is none: a text page with a grey table area (the area would turn white) and an almost white image with
    /// a few soft spots without ink (background of a compact image PDF, which must not become 1 bit; found in the check run "Save as image, then compress").
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NotReallyBlackAndWhite_StaysGray(bool tintedTable)
    {
        var file = tintedTable
            ? WithImage(1700, 2200, 1, (x, y, _) => Ink(x, y) ? (byte)0 : y is > 300 and < 700 ? (byte)225 : (byte)255, inches: 5.67)
            : WithImage(1700, 2200, 1, (x, y, _) => x is > 200 and < 260 && y is > 200 and < 300 ? (byte)(140 + x - 200) : (byte)255, inches: 5.67);
        var (doc, image) = Compress(file, CompressionSettings.For(CompressionPreset.EBook));
        using (doc)
        using (var original = PdfDocument.Open(file))
        {
            Assert.NotEqual("CCITTFaxDecode", image.GetName(PdfName.Filter)?.Value);
            // At image resolution (4 × 72 dpi ≈ 300 dpi): with MRC the table becomes a text mask over a grey background; strongly
            // reduced, a 1-bit mask looks slightly different from a greyscale image although nothing is missing
            var difference = Difference(original, doc, 4);
            Assert.True(difference < 2, $"Abweichung {difference}");
        }
    }

    /// <summary>Like a scanned text page: lines of randomly shaped "letters" (stems, bars); nothing repeats exactly.</summary>
    private static bool Ink(int x, int y) => Scan.Value[y * 1700 + x];

    private static readonly Lazy<bool[]> Scan = new(() =>
    {
        const int w = 1700, h = 2200;
        var ink = new bool[w * h];
        var random = new Random(7);
        for (var line = 0; line < h / 40; line++)
            for (var x = 20; x < w - 30; x += random.Next(14, 24))
            {
                int top = line * 40 + 8 + random.Next(0, 4), height = random.Next(14, 22), width = random.Next(6, 14);
                for (var s = 0; s < 3; s++)
                {
                    int sx = x + random.Next(width), sy = top + random.Next(height / 2), sw = random.Next(2, 4), sh = random.Next(4, height);
                    for (var yy = sy; yy < sy + sh && yy < h; yy++)
                        for (var xx = sx; xx < sx + sw && xx < w; xx++) ink[yy * w + xx] = true;
                }
            }
        return ink;
    });

    /// <summary>Merged file with the same source twice: the image is stored only once in the new file.</summary>
    [Fact]
    public void Duplicates_FromMergedFiles_StoredOnce()
    {
        var file = WithImage(300, 300, 3, (x, y, c) => (byte)(x ^ y ^ (c * 40)), inches: 2);
        using var a = PdfDocument.Open(file);
        using var b = PdfDocument.Open(file);
        PageItem[] pages = [.. PageItem.From(a), .. PageItem.From(b)];
        using var plain = new MemoryStream();
        PdfWriter.Write(a, pages, plain);
        using var merged = new MemoryStream();
        PdfWriter.Write(a, pages, merged, new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.Lossless) });
        Assert.True(merged.Length < plain.Length * 0.65, $"{merged.Length} von {plain.Length}");
        using var doc = PdfDocument.Open(merged.ToArray());
        var first = doc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.GetRaw(Im);
        var second = doc.Pages[1].Resources!.GetDictionary(PdfName.Get("XObject"))!.GetRaw(Im);
        Assert.Equal(((PdfReference)first!).Id, ((PdfReference)second!).Id);
    }

    [Fact]
    public void Brotli_OnlyOnRequest_AndReadable()
    {
        var file = WithImage(400, 400, 3, (x, y, c) => (byte)((x + y + c) & 255), inches: 3);
        var (doc, image) = Compress(file, CompressionSettings.For(CompressionPreset.Lossless) with { Brotli = true });
        using (doc)
        using (var original = PdfDocument.Open(file))
        {
            Assert.Equal("BrotliDecode", image.GetName(PdfName.Filter)?.Value);
            Assert.Equal(0, Difference(original, doc));
        }
        var (plain, flate) = Compress(file, CompressionSettings.For(CompressionPreset.Lossless));
        using (plain) Assert.NotEqual("BrotliDecode", flate.GetName(PdfName.Filter)?.Value);
    }

    /// <summary>
    /// An image that Brotli does not make smaller (here: stored with Brotli already) is packed only once: the stream path takes the result
    /// of the image path, as with Flate — packing the same samples twice at quality 11 doubled the slowest step (docs/FEHLERGEDAECHTNIS.md,
    /// No. 710).
    /// </summary>
    [Fact]
    public void Brotli_ImageNotSmaller_PackedOnlyOnce()
    {
        const int size = 300;
        var noise = new Random(7);
        var samples = new byte[size * size * 3];
        for (var i = 0; i < samples.Length; i++) samples[i] = (byte)(noise.Next(32) * 8);
        var packed = new byte[System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(samples.Length)];
        Assert.True(System.IO.Compression.BrotliEncoder.TryCompress(samples, packed, out var length, quality: 11, window: 22));
        var pdf = TestPdf.Simple(1, null);
        var image = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(size)), ("Height", PdfInteger.Of(size)), ("BitsPerComponent", PdfInteger.Of(8)),
            ("ColorSpace", TestPdf.N("DeviceRGB")), ("Filter", TestPdf.N("BrotliDecode"))), packed[..length]));
        var content = pdf.Add(TestPdf.FlateStream("q 216 0 0 216 72 300 cm /Im0 Do Q"u8.ToArray()));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Contents, TestPdf.Ref(content));
        page.Set(PdfName.Resources, TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))));
        var before = Core.Pdf.Filters.Brotli.EncodedForTests;
        var (doc, written) = Compress(pdf.Build(), CompressionSettings.For(CompressionPreset.Lossless) with { Brotli = true });
        var encoded = Core.Pdf.Filters.Brotli.EncodedForTests - before;
        using (doc) Assert.Equal("BrotliDecode", written.GetName(PdfName.Filter)?.Value);
        Assert.InRange(encoded, samples.Length, samples.Length * 3 / 2);
    }

    /// <summary>
    /// Cancelling takes effect while a large image is packed with Brotli, not only after it (one encoder call blocked for well over ten
    /// seconds; docs/FEHLERGEDAECHTNIS.md, No. 710). Measured from the moment of cancelling, not from the start — a time test with the
    /// factor (No. 399): under the load of the full run the fixed limit from the start tore now and then (four times by 16.11.2026).
    /// </summary>
    [Fact]
    [Trait(TestKind.Name, TestKind.Time)]
    public void Brotli_LargeImage_CancelsWhilePacking()
    {
        var rng = new Random(11);
        var file = WithImage(1800, 1800, 3, (_, _, _) => (byte)(rng.Next(24) * 10), inches: 6);
        using var original = PdfDocument.Open(file);
        using var cancel = new CancellationTokenSource();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? cancelledAt = null;
        cancel.Token.Register(() => cancelledAt = watch.Elapsed);
        cancel.CancelAfter(TimeSpan.FromSeconds(1));
        Assert.ThrowsAny<OperationCanceledException>(() => PdfWriter.Write(original, PageItem.From(original), Stream.Null,
            new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.Lossless) with { Brotli = true }, ImageCodec = Rendering.SkiaImageCodec.Instance },
            cancel.Token));
        var after = watch.Elapsed - (cancelledAt ?? TimeSpan.Zero);
        Assert.True(after < TimeSpan.FromSeconds(2 * TestKind.Factor), $"{after.TotalSeconds:0.0} s nach dem Abbrechen");
    }

    /// <summary>
    /// Brotli in the file read: saved, compressed with Flate or as stored only if Brotli is chosen — it is not part of ISO 32000-2 and many
    /// readers cannot open it (docs/FEHLERGEDAECHTNIS.md, No. 370).
    /// </summary>
    [Fact]
    public void BrotliOfTheFile_PackedWithFlate_UnlessBrotliIsChosen()
    {
        var pdf = TestPdf.Simple(1, null);
        var content = "BT /F1 24 Tf 72 700 Td (Brotli im Inhalt der Seite) Tj ET"u8.ToArray();
        var packed = new byte[content.Length + 64];
        Assert.True(System.IO.Compression.BrotliEncoder.TryCompress(content, packed, out var length));
        var dict = new PdfDictionary();
        dict.Set(PdfName.Filter, PdfName.Get("BrotliDecode"));
        pdf.Replace(4, new PdfStream(dict, packed[..length]));
        using var doc = PdfDocument.Open(pdf.Build());
        var lossless = CompressionSettings.For(CompressionPreset.Lossless);
        foreach (var (options, filter) in (ReadOnlySpan<(PdfSaveOptions, string)>)[(new PdfSaveOptions(), "FlateDecode"),
                     (new PdfSaveOptions { Compression = lossless }, "FlateDecode"), (new PdfSaveOptions { Compression = lossless with { Brotli = true } }, "BrotliDecode")])
        {
            using var output = new MemoryStream();
            PdfWriter.Write(doc, PageItem.From(doc), output, options);
            using var saved = PdfDocument.Open(output.ToArray());
            var stream = (PdfStream)saved.Pages[0].Dictionary.Get(PdfName.Contents);
            Assert.Equal(filter, stream.Dictionary.GetName(PdfName.Filter)?.Value);
            Assert.Equal(content, saved.DecodeStream(stream).Data);
        }
    }

    /// <summary>
    /// Cache (trial runs in the window): across changing settings exactly the same file as without it, for a photo, a scan (G4),
    /// identical images, a font to subset, and JPEG.
    /// </summary>
    [Fact]
    public void Cache_GivesTheSameFileAsWithout()
    {
        var files = new[]
        {
            WithImage(2400, 1600, 3, Photo, inches: 2, caption: "Foto"),
            WithImage(1700, 2200, 1, (x, y, _) => Ink(x, y) ? (byte)0 : (x * 7 + y * 13) % 401 == 0 ? (byte)128 : (byte)255, inches: 5.67),
            Page("q 72 0 0 72 72 600 cm /Im0 Do Q q 288 0 0 288 200 100 cm /Im1 Do Q", pdf => TestPdf.Dict(
                ("Im0", TestPdf.Ref(pdf.Add(ImageStream(1200, 1200, 3, Photo)))), ("Im1", TestPdf.Ref(pdf.Add(ImageStream(1200, 1200, 3, Photo)))))),
            FontDocument(),
        };
        var settings = new[]
        {
            CompressionSettings.For(CompressionPreset.EBook), CompressionSettings.For(CompressionPreset.Lossless),
            CompressionSettings.For(CompressionPreset.Screen) with { JpegQuality = 40 }, CompressionSettings.For(CompressionPreset.EBook),
            CompressionSettings.For(CompressionPreset.EBook) with { Brotli = true }, CompressionSettings.For(CompressionPreset.Print) with { SubsetFonts = false },
        };
        foreach (var file in files)
        {
            using var doc = PdfDocument.Open(file);
            var items = PageItem.From(doc);
            var cache = new CompressionCache();
            foreach (var setting in settings)
            {
                // Without metadata: otherwise the modification date (packed in the object stream) changes the length when the second changes
                var options = new PdfSaveOptions { Compression = setting, ImageCodec = Rendering.SkiaImageCodec.Instance, Cleanup = CleanupItems.Metadata };
                using var with = new MemoryStream();
                using var without = new MemoryStream();
                PdfWriter.Write(doc, items, with, options with { CompressionCache = cache });
                PdfWriter.Write(doc, items, without, options);
                Assert.Equal(without.Length, with.Length);
                using var a = PdfDocument.Open(with.ToArray());
                using var b = PdfDocument.Open(without.ToArray());
                Assert.Equal(0, Difference(a, b));
            }
            Assert.True(cache.Bytes > 0);
        }
    }

    /// <summary>Builds an image stream (8 bits, Flate) with samples from <paramref name="sample"/>.</summary>
    private static PdfStream ImageStream(int width, int height, int components, Func<int, int, int, byte> sample, params (string Key, PdfObject Value)[] extra)
    {
        var data = new byte[width * height * components];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                for (var c = 0; c < components; c++) data[(y * width + x) * components + c] = sample(x, y, c);
        var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(width)),
            ("Height", PdfInteger.Of(height)), ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", TestPdf.N(components == 1 ? "DeviceGray" : "DeviceRGB")));
        foreach (var (key, value) in extra) dict.Set(TestPdf.N(key), value);
        return TestPdf.FlateStream(data, dict);
    }

    /// <summary>Builds a page with this content; <paramref name="xobjects"/> creates the images (name → object number).</summary>
    private static byte[] Page(string content, Func<TestPdf, PdfDictionary> xobjects, Func<TestPdf, PdfDictionary>? patterns = null)
    {
        var pdf = TestPdf.Simple(1, null);
        var resources = TestPdf.Dict(("XObject", xobjects(pdf)), ("Font", TestPdf.Dict(("F1", TestPdf.Ref(3)))));
        if (patterns is not null) resources.Set(TestPdf.N("Pattern"), patterns(pdf));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Contents, TestPdf.Ref(pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(content)))));
        page.Set(PdfName.Resources, resources);
        return pdf.Build();
    }

    private static PdfDictionary Written(PdfDocument doc, string name) =>
        ((PdfStream)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get(name))).Dictionary;

    private static byte Photo(int x, int y, int c) => (byte)((x * 7 + y * 13 + c * 50 + x * y) & 255);

    /// <summary>
    /// Two identical images (different objects), one drawn small, one large: they become one, and that one must suffice for the large
    /// use (if the one written first determined the size alone, the large use would become blurred).
    /// </summary>
    [Fact]
    public void DuplicateImages_TheLargestUseOfAllDecides()
    {
        var file = Page("q 72 0 0 72 72 600 cm /Im0 Do Q q 288 0 0 288 200 100 cm /Im1 Do Q", pdf => TestPdf.Dict(
            ("Im0", TestPdf.Ref(pdf.Add(ImageStream(1200, 1200, 3, Photo)))), ("Im1", TestPdf.Ref(pdf.Add(ImageStream(1200, 1200, 3, Photo))))));
        using var original = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(original, PageItem.From(original), output, new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.EBook), ImageCodec = Rendering.SkiaImageCodec.Instance });
        using var doc = PdfDocument.Open(output.ToArray());
        // 4 inches at 150 dpi = 600 px, for both (it is one object)
        Assert.Equal(600, Written(doc, "Im0").GetInteger(PdfName.Get("Width")));
        Assert.Equal(600, Written(doc, "Im1").GetInteger(PdfName.Get("Width")));
    }

    /// <summary>An image used only in a tiling pattern is measured as well, instead of always staying at full size.</summary>
    [Fact]
    public void PatternImage_IsMeasured()
    {
        var file = Page("/Pattern cs /P0 scn 72 300 288 288 re f", _ => new PdfDictionary(), pdf =>
        {
            var image = pdf.Add(ImageStream(1200, 1200, 3, Photo));
            var cell = TestPdf.FlateStream("q 144 0 0 144 0 0 cm /Im0 Do Q"u8.ToArray(), TestPdf.Dict(("Type", TestPdf.N("Pattern")),
                ("PatternType", PdfInteger.Of(1)), ("PaintType", PdfInteger.Of(1)), ("TilingType", PdfInteger.Of(1)), ("BBox", TestPdf.Box(0, 0, 144, 144)),
                ("XStep", PdfInteger.Of(144)), ("YStep", PdfInteger.Of(144)),
                ("Resources", TestPdf.Dict(("XObject", TestPdf.Dict(("Im0", TestPdf.Ref(image))))))));
            return TestPdf.Dict(("P0", TestPdf.Ref(pdf.Add(cell))));
        });
        using var original = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(original, PageItem.From(original), output, new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.EBook), ImageCodec = Rendering.SkiaImageCodec.Instance });
        using var doc = PdfDocument.Open(output.ToArray());
        var pattern = (PdfStream)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("Pattern"))!.Get(PdfName.Get("P0"));
        var image = ((PdfStream)pattern.Dictionary.GetDictionary(PdfName.Resources)!.GetDictionary(PdfName.Get("XObject"))!.Get(PdfName.Get("Im0"))).Dictionary;
        Assert.Equal(300, image.GetInteger(PdfName.Get("Width")));   // 2 inches at 150 dpi
    }

    /// <summary>Soft mask with /Matte: image and mask must keep the same size, so the image is not scaled down.</summary>
    [Fact]
    public void MatteSoftMask_KeepsTheSameSize()
    {
        var file = Page("q 144 0 0 144 72 600 cm /Im0 Do Q", pdf =>
        {
            var mask = pdf.Add(ImageStream(1200, 1200, 1, (x, y, _) => (byte)((x + y) % 256), ("Matte", new PdfArray([new PdfReal(0), new PdfReal(0), new PdfReal(0)]))));
            return TestPdf.Dict(("Im0", TestPdf.Ref(pdf.Add(ImageStream(1200, 1200, 3, Photo, ("SMask", TestPdf.Ref(mask)))))));
        });
        var (doc, image) = Compress(file, CompressionSettings.For(CompressionPreset.EBook));
        using (doc)
        {
            var mask = ((PdfStream)image.Get(PdfName.Get("SMask"))).Dictionary;
            Assert.Equal(mask.GetInteger(PdfName.Get("Width")), image.GetInteger(PdfName.Get("Width")));
        }
    }

    /// <summary>
    /// Soft mask with /Matte and an image with few colours, lossless: no exact palette, because /Matte needs as many values as the
    /// colour space has components, and /Indexed has one (11.6.5.3).
    /// </summary>
    [Fact]
    public void MatteSoftMask_NoExactPalette()
    {
        var file = Page("q 144 0 0 144 72 600 cm /Im0 Do Q", pdf =>
        {
            var mask = pdf.Add(ImageStream(300, 300, 1, (x, y, _) => (byte)((x + y) % 256), ("Matte", new PdfArray([new PdfReal(0), new PdfReal(0), new PdfReal(0)]))));
            return TestPdf.Dict(("Im0", TestPdf.Ref(pdf.Add(ImageStream(300, 300, 3, (x, y, c) => (byte)((x / 50 + y / 50) % 4 * 60 + c * 5),
                ("SMask", TestPdf.Ref(mask)))))));
        });
        var (doc, image) = Compress(file, CompressionSettings.For(CompressionPreset.Lossless));
        using (doc) Assert.Equal("DeviceRGB", image.GetName(PdfName.Get("ColorSpace"))?.Value);
    }

    /// <summary>1-bit RGB (three bits per pixel): must not be written as 1-bit G4 (that would read the data as one bit per pixel).</summary>
    [Fact]
    public void OneBitRgb_StaysCorrect()
    {
        const int w = 200, h = 200, rowBytes = (w * 3 + 7) / 8;
        var data = new byte[rowBytes * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w * 3; x++)
                if ((x / 3 + y) % 40 < 20) data[y * rowBytes + (x >> 3)] |= (byte)(0x80 >> (x & 7));   // uncompressed: G4 would always be smaller
        var file = Page("q 144 0 0 144 72 600 cm /Im0 Do Q", pdf => TestPdf.Dict(("Im0", TestPdf.Ref(pdf.Add(new PdfStream(
            TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(w)), ("Height", PdfInteger.Of(h)),
                ("BitsPerComponent", PdfInteger.Of(1)), ("ColorSpace", TestPdf.N("DeviceRGB"))), data))))));
        var (doc, image) = Compress(file, CompressionSettings.For(CompressionPreset.Lossless));
        using (doc)
        using (var original = PdfDocument.Open(file))
        {
            Assert.NotEqual("CCITTFaxDecode", image.GetName(PdfName.Filter)?.Value);
            Assert.Equal(0, Difference(original, doc));
        }
    }

    /// <summary>
    /// JPEG photo: lossless keeps it byte for byte (it is not even decoded); e-book decodes it already scaled down (libjpeg)
    /// and reaches the target size without visible deviation.
    /// </summary>
    [Fact]
    public void JpegPhoto_KeptOrDecodedSmaller()
    {
        const int w = 2400, h = 1600;
        var rgb = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 3; c++) rgb[(y * w + x) * 3 + c] = (byte)(128 + 100 * Math.Sin(x / (37.0 + c * 9)) * Math.Cos(y / 23.0));
        var jpeg = Rendering.SkiaImageCodec.Instance.EncodeJpeg(w, h, 3, rgb, 90)!;
        var file = Page("q 144 0 0 96 72 600 cm /Im0 Do Q", pdf => TestPdf.Dict(("Im0", TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(
            ("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(w)), ("Height", PdfInteger.Of(h)),
            ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("Filter", TestPdf.N("DCTDecode"))), jpeg))))));
        var (same, _) = Compress(file, CompressionSettings.For(CompressionPreset.Lossless));
        using (same) Assert.Equal(jpeg, same.GetRawStreamData((PdfStream)same.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(Im)));
        var (doc, image) = Compress(file, CompressionSettings.For(CompressionPreset.EBook));
        using (doc)
        using (var original = PdfDocument.Open(file))
        {
            Assert.Equal((300, 200), ((int)image.GetInteger(PdfName.Get("Width"))!, (int)image.GetInteger(PdfName.Get("Height"))!));
            Assert.True(Difference(original, doc) < 3, $"{Difference(original, doc):0.00}");
        }
    }

    /// <summary>
    /// JPEG with a placeholder height in the header (65 280 rows, as some scanners write it; issue10880/issue10989): fully decoded this would
    /// be gigabytes, and saving would abort with an OverflowException. It stays unchanged, both as a photo and as a text scan (MRC, black and white).
    /// </summary>
    [Fact]
    public void JpegWithPlaceholderHeight_KeptUnchanged()
    {
        const int w = 8400, h = 400;   // 8 400 × 65 280 × 4 bytes: more than one array can hold
        var rgb = new byte[w * h * 3];
        Array.Fill(rgb, (byte)250);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (y % 40 < 12 && x % 17 < 9) rgb[(y * w + x) * 3] = rgb[(y * w + x) * 3 + 1] = rgb[(y * w + x) * 3 + 2] = 20;
        var jpeg = Rendering.SkiaImageCodec.Instance.EncodeJpeg(w, h, 3, rgb, 90)!;
        for (var at = 2; ; at += 2 + (jpeg[at + 2] << 8 | jpeg[at + 3]))
            if (jpeg[at + 1] is 0xC0 or 0xC1 or 0xC2)
            {
                (jpeg[at + 5], jpeg[at + 6]) = (0xFF, 0x00);
                break;
            }
        var file = Page("q 500 0 0 24 50 400 cm /Im0 Do Q", pdf => TestPdf.Dict(("Im0", TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(
            ("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(w)), ("Height", PdfInteger.Of(h)),
            ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", TestPdf.N("DeviceRGB")), ("Filter", TestPdf.N("DCTDecode"))), jpeg))))));
        foreach (var settings in new[] { CompressionSettings.Default, CompressionSettings.Default with { Scans = ScanMethod.AsPhotos },
                     CompressionSettings.For(CompressionPreset.Screen), CompressionSettings.For(CompressionPreset.Lossless) })
        {
            var (doc, image) = Compress(file, settings);
            using (doc) Assert.Equal(jpeg, doc.GetRawStreamData((PdfStream)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(Im)));
        }
    }

    /// <summary>Builds a page with many lines and text (a large content stream as in a plan).</summary>
    private static byte[] Drawing()
    {
        var pdf = TestPdf.Simple(1, null);
        var random = new Random(3);
        var draw = new System.Text.StringBuilder();
        for (var i = 0; i < 8000; i++)
            draw.Append(FormattableString.Invariant($"{random.Next(40, 560)}.{random.Next(10)} {random.Next(40, 800)}.{random.Next(10)} m {random.Next(40, 560)} {random.Next(40, 800)} l S\n"));
        for (var i = 0; i < 300; i++) draw.Append(FormattableString.Invariant($"BT /F1 6 Tf {40 + i % 20 * 26} {60 + i / 20 * 48} Td (Raum {i}) Tj ET\n"));
        var content = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(draw.ToString())));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Contents, TestPdf.Ref(content));
        page.Set(PdfName.Resources, TestPdf.Dict(("Font", TestPdf.Dict(("F1", TestPdf.Ref(3))))));
        return pdf.Build();
    }

    private static byte[] Save(byte[] file, PdfSaveOptions options)
    {
        using var doc = PdfDocument.Open(file);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, options);
        return output.ToArray();
    }

    /// <summary>"Flate – strong": smaller than the strongest .NET level, exactly the same image.</summary>
    [Fact]
    public void StrongFlate_SmallerAndIdentical()
    {
        var file = Drawing();
        var lossless = CompressionSettings.For(CompressionPreset.Lossless);
        var normal = Save(file, new PdfSaveOptions { Compression = lossless });
        var strong = Save(file, new PdfSaveOptions { Compression = lossless with { StrongFlate = true } });
        Assert.True(strong.Length < normal.Length * 0.97, $"{normal.Length} → {strong.Length}");
        using var a = PdfDocument.Open(normal);
        using var b = PdfDocument.Open(strong);
        Assert.Equal(0, Difference(a, b, 1));
        Assert.Equal(a.DecodeStream((PdfStream)a.Pages[0].Dictionary.Get(PdfName.Contents)!).Data,
            b.DecodeStream((PdfStream)b.Pages[0].Dictionary.Get(PdfName.Contents)!).Data);
    }

    /// <summary>
    /// Fixed modification date: the same size no matter when the file is written; otherwise the digits in the packed object stream change
    /// the size by one byte and the compressor's prediction is off (full test run: 7 510 instead of 7 509 bytes).
    /// </summary>
    [Fact]
    public void ModificationTime_FixesSize()
    {
        var file = Drawing();
        var stamp = new DateTimeOffset(2026, 10, 20, 17, 5, 9, TimeSpan.FromHours(2));
        var sizes = new HashSet<int>();
        var compression = CompressionSettings.For(CompressionPreset.Lossless);
        for (var i = 0; i < 3; i++) sizes.Add(Save(file, new PdfSaveOptions { Compression = compression, ModificationTime = stamp }).Length);
        Assert.Single(sizes);
        using var saved = PdfDocument.Open(Save(file, new PdfSaveOptions { Compression = compression, ModificationTime = stamp }));
        Assert.Equal("D:20261020170509+02'00'", System.Text.Encoding.ASCII.GetString(saved.Trailer.GetDictionary(PdfName.Info)!.GetString(PdfName.Get("ModDate"))!.Bytes));
    }

    /// <summary>
    /// Font subsetting with a non-ascending loca (issue11131, issue17671): the reader reads such glyphs like common font engines up to the next
    /// start or the end of glyf; if subsetting treats them as empty, most letters are missing after lossless compression
    /// ("Operating Account …" → "p r ti g ou t …"). The result must be pixel-identical.
    /// </summary>
    [TestPdfsTheory]
    [InlineData("issue11131_reduced.pdf")]
    [InlineData("issue17671.pdf")]
    public void SubsetFonts_UnsortedLoca_KeepsAllGlyphs(string name)
    {
        var file = File.ReadAllBytes(Path.Combine(Repository(), "TestPDFs", "pdfjs", name));
        var saved = Save(file, new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.Lossless) });
        using var before = PdfDocument.Open(file);
        using var after = PdfDocument.Open(saved);
        Assert.Equal(0, Difference(before, after, 1));
    }

    private static string Repository([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }

    /// <summary>A grey photo becomes JPEG, although grey cannot have more than 256 levels, the colour count that otherwise marks a photo.</summary>
    [Fact]
    public void GrayPhoto_BecomesJpeg()
    {
        var file = WithImage(2400, 1600, 1, (x, y, _) => (byte)(128 + 100 * Math.Sin(x / 37.0) * Math.Cos(y / 23.0)), inches: 2);
        var (doc, image) = Compress(file, CompressionSettings.For(CompressionPreset.EBook));
        using (doc)
        {
            Assert.Equal("DCTDecode", image.GetName(PdfName.Filter)?.Value);
            Assert.Equal(300, image.GetInteger(PdfName.Get("Width")));
        }
    }

    /// <summary>
    /// What the compressor writes as JPEG (ISO 32000-2, 7.4.8; ITU-T T.81, clause 6; T.871, 5 and 6.1; Adobe TN 5116, sections 18 and 20;
    /// docs/normabgleich/M2-filter.md, rows 32, 35, 59, 141): the baseline process (SOF0) with 8-bit samples and 1 or 3 components, the
    /// JFIF marker directly after SOI, no Adobe marker (the default /ColorTransform of three components, 1, fits YCbCr) — and the own decoder
    /// reads it at the size of the image dictionary with the components it declares.
    /// </summary>
    [Fact]
    public void JpegOfTheCompressor_BaselineWithJfif_OneOrThreeComponents()
    {
        foreach (var components in new[] { 1, 3 })
        {
            var file = WithImage(2400, 1600, components, (x, y, c) => (byte)(128 + 100 * Math.Sin(x / (37.0 + c * 5)) * Math.Cos(y / 23.0)), inches: 2);
            var (doc, image) = Compress(file, CompressionSettings.For(CompressionPreset.EBook));
            using (doc)
            {
                Assert.Equal("DCTDecode", image.GetName(PdfName.Filter)?.Value);
                var stream = (PdfStream)doc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!.Get(Im)!;
                var jpeg = doc.GetRawStreamData(stream);
                Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0], jpeg[..4]);
                Assert.Equal("JFIF\0"u8.ToArray(), jpeg[6..11]);
                int? frame = null;
                for (var at = 2; at + 4 <= jpeg.Length && jpeg[at] == 0xFF && jpeg[at + 1] != 0xDA; at += 2 + (jpeg[at + 2] << 8 | jpeg[at + 3]))
                {
                    var marker = jpeg[at + 1];
                    Assert.False(marker == 0xEE, "Adobe-Marke geschrieben");
                    if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                    {
                        Assert.Null(frame);
                        frame = at;
                        Assert.Equal(0xC0, marker);                  // baseline
                        Assert.Equal(8, jpeg[at + 4]);                // sample precision
                        Assert.Equal(components, jpeg[at + 9]);       // components in the frame
                    }
                }
                Assert.NotNull(frame);
                var decoded = Core.Pdf.Filters.Jpeg.JpegDecoder.Decode(jpeg);
                Assert.Equal((image.GetInteger(PdfName.Get("Width")), image.GetInteger(PdfName.Get("Height")), components),
                    ((long?)decoded.Width, (long?)decoded.Height, decoded.Channels));
                Assert.False(decoded.Damaged);
            }
        }
    }

    /// <summary>
    /// Font subsetting: the font belongs to the graphics state. After Q the previous one applies again, and a form XObject inherits the font of
    /// the caller; in both cases the characters must stay in the subset font.
    /// </summary>
    [Fact]
    public void SubsetFonts_FollowsQAndFormInheritance()
    {
        var ttf = Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var original = FontDocument();

        using var before = PdfDocument.Open(original);
        using var output = new MemoryStream();
        PdfWriter.Write(before, PageItem.From(before), output, new PdfSaveOptions { Compression = CompressionSettings.For(CompressionPreset.Lossless) });
        using var after = PdfDocument.Open(output.ToArray());
        var subset = after.Pages[0].Resources!.GetDictionary(PdfName.Get("Font"))!.GetDictionary(PdfName.Get("TT"))!
            .GetDictionary(PdfName.Get("FontDescriptor"))!.GetStream(PdfName.Get("FontFile2"))!;
        Assert.True(after.GetStreamData(subset).Length < ttf.Length * 0.8, "Schrift wurde nicht gekürzt");
        Assert.True(Difference(before, after) < 0.01, $"{Difference(before, after):0.000}");
    }

    /// <summary>Embedded TrueType font (not subset), text after q/Q and in a form XObject without its own Tf.</summary>
    private static byte[] FontDocument()
    {
        var ttf = Core.Fonts.EmbeddedFonts.LoadStandard("LiberationSans-Regular.ttf");
        var pdf = TestPdf.Simple(1, null);
        var file = pdf.Add(TestPdf.FlateStream(ttf, TestPdf.Dict(("Length1", PdfInteger.Of(ttf.Length)))));
        var font = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Font")), ("Subtype", TestPdf.N("TrueType")), ("BaseFont", TestPdf.N("LiberationSans")),
            ("Encoding", TestPdf.N("WinAnsiEncoding")),
            ("FontDescriptor", TestPdf.Dict(("Type", TestPdf.N("FontDescriptor")), ("FontName", TestPdf.N("LiberationSans")), ("Flags", PdfInteger.Of(32)),
                ("FontFile2", TestPdf.Ref(file))))));
        var fonts = TestPdf.Dict(("F1", TestPdf.Ref(3)), ("TT", TestPdf.Ref(font)));
        var form = pdf.Add(TestPdf.FlateStream("BT 72 400 Td (QW) Tj ET"u8.ToArray(), TestPdf.Dict(("Type", TestPdf.N("XObject")),
            ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 595, 842)), ("Resources", TestPdf.Dict(("Font", fonts))))));
        var content = pdf.Add(TestPdf.FlateStream(System.Text.Encoding.ASCII.GetBytes(
            "BT /TT 40 Tf 72 700 Td (AB) Tj ET q BT /F1 12 Tf 72 650 Td (zz) Tj ET Q BT 72 600 Td (XYZ) Tj ET /Fm Do")));
        var page = (PdfDictionary)pdf.Get(5);
        page.Set(PdfName.Contents, TestPdf.Ref(content));
        page.Set(PdfName.Resources, TestPdf.Dict(("Font", fonts), ("XObject", TestPdf.Dict(("Fm", TestPdf.Ref(form))))));
        return pdf.Build();
    }
}
