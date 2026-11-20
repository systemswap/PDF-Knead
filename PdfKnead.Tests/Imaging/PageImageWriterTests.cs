using System.Buffers.Binary;
using System.IO.Compression;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Scanning;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Imaging;

/// <summary>
/// „Als Bild speichern“ (KONZEPT §5.25): pages as PNG, JPEG, TIFF (one file per page or all in one) and WebP — read back sample for sample
/// (TIFF with a small reader of its own here, Group 4 with the decoder of the PDF filters), the resolution in every format that holds one,
/// names „Name_001“, nothing replaced, nothing half written when cancelled.
/// </summary>
public sealed class PageImageWriterTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("pdf-knead-seitenbilder-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>Test pages: black and white with a pattern, greyscale with a gradient, colour with three gradients.</summary>
    private static ScannedImage BlackWhite(int w = 203, int h = 117, int dpi = 300)
    {
        var px = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) px[y * w + x] = (x * 7 + y * 3) % 11 < 4 || (x > 50 && x < 90) ? (byte)0 : (byte)255;
        return new ScannedImage(w, h, 1, dpi, px, BlackWhite: true);
    }

    private static ScannedImage Gray(int w = 150, int h = 90, int dpi = 200)
    {
        var px = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) px[y * w + x] = (byte)(x * 255 / (w - 1) ^ (y & 8));
        return new ScannedImage(w, h, 1, dpi, px);
    }

    private static ScannedImage Colour(int w = 120, int h = 80, int dpi = 150)
    {
        var px = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                (px[(y * w + x) * 3], px[(y * w + x) * 3 + 1], px[(y * w + x) * 3 + 2]) = ((byte)(x * 2), (byte)(y * 3), (byte)((x + y) % 256));
        return new ScannedImage(w, h, 3, dpi, px);
    }

    /// <summary>
    /// TIFF 6.0, section 3 (M2 row 211, decided on 19.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 964): no strip above 64 KB for old
    /// readers, black and white pages in several strips too, each its own Group 4 encoding; every page reads back exactly. Before, black and
    /// white pages were one strip and the others strips of 256 KB of raw data.
    /// </summary>
    [Fact]
    public void Tiff_StripsAtMost64Kilobytes_EachPageReadBackExactly()
    {
        ScannedImage[] pages = [BlackWhite(2480, 900), Gray(1200, 400), Colour(900, 300)];
        var file = Assert.Single(PageImageWriter.Write(pages.Length, i => pages[i], _folder, "Gross", new PageImageSettings(PageImageFormat.Tiff, OneFile: true)));
        var read = TiffReader.Read(File.ReadAllBytes(file));
        for (var i = 0; i < pages.Length; i++)
        {
            Assert.Equal(pages[i].Pixels, read[i].Pixels);
            Assert.True(read[i].Strips.Length > 1, $"Seite {i + 1}: ein Streifen");
            Assert.All(read[i].Strips, s => Assert.True(s <= 65536, $"Seite {i + 1}: Streifen mit {s} Bytes"));
        }
    }

    [Fact]
    public void Tiff_AllPagesInOneFile_EachPageReadBackExactly()
    {
        ScannedImage[] pages = [BlackWhite(), Gray(), Colour()];
        var files = PageImageWriter.Write(pages.Length, i => pages[i], _folder, "Akte", new PageImageSettings(PageImageFormat.Tiff, OneFile: true));
        var file = Assert.Single(files);
        Assert.Equal("Akte.tif", Path.GetFileName(file));
        var read = TiffReader.Read(File.ReadAllBytes(file));
        Assert.Equal(3, read.Count);
        for (var i = 0; i < pages.Length; i++)
        {
            Assert.Equal((pages[i].Width, pages[i].Height, (double)pages[i].Dpi), (read[i].Width, read[i].Height, read[i].Dpi));
            Assert.Equal(pages[i].Pixels, read[i].Pixels);
            Assert.Equal((i, 3), read[i].PageNumber);
        }
        Assert.Equal([4, 8, 8], read.Select(p => p.Compression));   // Group 4 for black and white, Deflate otherwise
    }

    [Fact]
    public void Tiff_OneFilePerPage_NamesNumbered_NothingReplaced()
    {
        ScannedImage[] pages = [Gray(), Colour()];
        File.WriteAllText(Path.Combine(_folder, "Scan_002.tif"), "vorhanden");
        var files = PageImageWriter.Write(pages.Length, i => pages[i], _folder, "Scan", new PageImageSettings(PageImageFormat.Tiff));
        Assert.Equal(["Scan_001.tif", "Scan_002 (2).tif"], files.Select(Path.GetFileName));
        Assert.Equal("vorhanden", File.ReadAllText(Path.Combine(_folder, "Scan_002.tif")));
        Assert.Equal(Colour().Pixels, Assert.Single(TiffReader.Read(File.ReadAllBytes(files[1]))).Pixels);
    }

    [Fact]
    public void Png_BlackWhiteOneBit_GreyOneChannel_ResolutionNoted()
    {
        ScannedImage[] pages = [BlackWhite(), Gray(), Colour()];
        var files = PageImageWriter.Write(pages.Length, i => pages[i], _folder, "Brief", new PageImageSettings(PageImageFormat.Png));
        Assert.Equal(["Brief_001.png", "Brief_002.png", "Brief_003.png"], files.Select(Path.GetFileName));
        (int Depth, int Type)[] expected = [(1, 0), (8, 0), (8, 2)];
        for (var i = 0; i < pages.Length; i++)
        {
            var data = File.ReadAllBytes(files[i]);
            Assert.Equal(expected[i], ((int)data[24], (int)data[25]));   // IHDR: bit depth, colour type
            var phys = data.AsSpan().IndexOf("pHYs"u8);
            Assert.True(phys > 0 && phys < data.AsSpan().IndexOf("IDAT"u8));
            Assert.Equal(Math.Round(pages[i].Dpi / 0.0254), BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(phys + 4)));
            var decoded = PngDecoder.Decode(data);
            for (var p = 0; p < pages[i].Width * pages[i].Height; p++)
                Assert.Equal(pages[i].Pixels[p * pages[i].Channels], decoded.Pixels[p * 4]);
        }
    }

    [Fact]
    public void Jpeg_And_WebP_QualityAndResolution()
    {
        var page = Colour(400, 300, 240);
        var jpeg = PageImageWriter.Write(1, _ => page, _folder, "Foto", new PageImageSettings(PageImageFormat.Jpeg, Quality: 95));
        var data = File.ReadAllBytes(Assert.Single(jpeg));
        Assert.Equal("Foto_001.jpg", Path.GetFileName(jpeg[0]));
        Assert.True(data.AsSpan(6, 5).SequenceEqual("JFIF\0"u8));
        Assert.Equal((1, 240, 240), (data[13], BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(14)), BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(16))));
        using (var bitmap = SKBitmap.Decode(data))
        {
            Assert.Equal((400, 300), (bitmap.Width, bitmap.Height));
            var c = bitmap.GetPixel(200, 150);
            Assert.InRange(c.Red, 144 - 6, 144 + 6);   // 2 · 200, as a byte
        }
        var small = PageImageWriter.Write(1, _ => page, _folder, "Klein", new PageImageSettings(PageImageFormat.Jpeg, Quality: 50));
        Assert.True(new FileInfo(small[0]).Length < data.Length);

        var webp = PageImageWriter.Write(1, _ => Gray(), _folder, "Netz", new PageImageSettings(PageImageFormat.WebP));
        var bytes = File.ReadAllBytes(Assert.Single(webp));
        Assert.True(bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8));
        using var decoded = SKBitmap.Decode(bytes);
        Assert.Equal((150, 90), (decoded.Width, decoded.Height));
    }

    [Fact]
    public void JpegDensity_InsertedWithoutJfif_NamesAndCleaning()
    {
        byte[] bare = [0xFF, 0xD8, 0xFF, 0xDB, 0, 4, 1, 2, 0xFF, 0xD9];
        var with = PageImageFiles.WithJpegDensity(bare, 600);
        Assert.Equal(bare.Length + 18, with.Length);
        Assert.True(with.AsSpan(6, 5).SequenceEqual("JFIF\0"u8));
        Assert.Equal(600, BinaryPrimitives.ReadUInt16BigEndian(with.AsSpan(14)));
        Assert.True(with.AsSpan(20).SequenceEqual(bare.AsSpan(2)));
        Assert.Same(with, PageImageFiles.WithJpegDensity(with, 300));   // changed in place
        Assert.Equal(300, BinaryPrimitives.ReadUInt16BigEndian(with.AsSpan(14)));

        Assert.Equal("Brief_007", PageImageFiles.Name("Brief", 7, 12));
        Assert.Equal("Brief_0007", PageImageFiles.Name("Brief", 7, 1200));
        Assert.Equal("a_b_c", PageImageFiles.CleanBaseName("a/b:c"));
        Assert.Equal("Scan", PageImageFiles.CleanBaseName(" .. "));
        Assert.Equal(new PageImageSettings(PageImageFormat.Png, 50, Dpi: 1200), new PageImageSettings((PageImageFormat)77, 3, Dpi: 99999).Normalized());
    }

    [Fact]
    public void Cancelled_NoHalfFileLeft()
    {
        using var cancel = new CancellationTokenSource();
        ScannedImage[] pages = [Gray(), Gray(), Gray()];
        Assert.ThrowsAny<OperationCanceledException>(() => PageImageWriter.Write(pages.Length, i =>
        {
            if (i == 1) cancel.Cancel();
            return pages[i];
        }, _folder, "Abbruch", new PageImageSettings(PageImageFormat.Tiff, OneFile: true), cancel: cancel.Token));
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public void DocumentPages_AllOrSome_AtTheChosenResolution()
    {
        // a document of two pages: 2 × 1 inch and 1 × 1 inch
        using var doc = ScanPdf.Build([Colour(200, 100, 100), Gray(100, 100, 100)]);
        var items = doc.Pages.Select(p => new PageItem(doc, p)).ToList();
        var files = RasterExport.WriteImages(items, _folder, "Dokument", new PageImageSettings(PageImageFormat.Tiff, OneFile: true, Dpi: 150));
        var read = TiffReader.Read(File.ReadAllBytes(Assert.Single(files)));
        Assert.Equal([(300, 150, 150.0), (150, 150, 150.0)], read.Select(p => (p.Width, p.Height, p.Dpi)));
        var one = RasterExport.WriteImages(items.Skip(1).ToList(), _folder, "Einzeln", new PageImageSettings(PageImageFormat.Png, Dpi: 72));
        Assert.Equal("Einzeln_001.png", Path.GetFileName(Assert.Single(one)));
    }

    /// <summary>Reads the TIFF files written here: big-endian, strips, Deflate with predictor 2 or Group 4.</summary>
    private static class TiffReader
    {
        public sealed record Page(int Width, int Height, double Dpi, int Compression, (int, int) PageNumber, byte[] Pixels, uint[] Strips);

        public static List<Page> Read(byte[] file)
        {
            Assert.True(file.AsSpan(0, 4).SequenceEqual((byte[])[(byte)'M', (byte)'M', 0, 42]));
            var pages = new List<Page>();
            for (var at = (int)U32(file, 4); at != 0;)
            {
                Assert.Equal(0, at % 2);   // a directory on a word boundary (TIFF 6.0, section 2)
                var count = U16(file, at);
                var tags = new Dictionary<int, uint[]>();
                for (var i = 0; i < count; i++)
                {
                    var e = at + 2 + i * 12;
                    var (tag, type, n) = (U16(file, e), U16(file, e + 2), (int)U32(file, e + 4));
                    Assert.True(tags.Keys.All(k => k < tag));   // ascending by tag
                    var size = type switch { 3 => 2, 4 => 4, 5 => 8, _ => 1 };
                    var valueAt = size * n <= 4 ? e + 8 : (int)U32(file, e + 8);
                    tags[tag] = type == 5
                        ? [U32(file, valueAt), U32(file, valueAt + 4)]
                        : [.. Enumerable.Range(0, n).Select(k => type == 3 ? U16(file, valueAt + 2 * k) : type == 4 ? U32(file, valueAt + 4 * k) : file[valueAt + k])];
                }
                var (w, h) = ((int)tags[256][0], (int)tags[257][0]);
                var compression = (int)tags[259][0];
                var channels = (int)tags[277][0];
                var data = tags[273].Zip(tags[279]).SelectMany(s => file.AsSpan((int)s.First, (int)s.Second).ToArray()).ToArray();
                byte[] pixels;
                if (compression == 4)
                {
                    Assert.Equal(0u, tags[262][0]);   // WhiteIsZero: 1 = black
                    Assert.Equal(1u, tags[266][0]);   // FillOrder stated (TIFF 6.0, section 11; No. 942)
                    // each strip its own Group 4 encoding (TIFF 6.0, section 11)
                    var rowBytes = (w + 7) / 8;
                    var stripRows = (int)tags[278][0];
                    var bits = new byte[rowBytes * h];
                    var top = 0;
                    foreach (var (offset, length) in tags[273].Zip(tags[279]))
                    {
                        var rows = Math.Min(stripRows, h - top);
                        CcittFax.DecodeMmr(file, (int)offset, (int)(offset + length), w, rows, out _).AsSpan(0, rows * rowBytes).CopyTo(bits.AsSpan(top * rowBytes));
                        top += rows;
                    }
                    Assert.Equal(h, top);
                    pixels = new byte[w * h];
                    for (var y = 0; y < h; y++)
                        for (var x = 0; x < w; x++) pixels[y * w + x] = (bits[y * rowBytes + (x >> 3)] >> (7 - (x & 7)) & 1) == 1 ? (byte)0 : (byte)255;
                }
                else
                {
                    Assert.Equal(2u, tags[317][0]);
                    var rowsPerStrip = (int)tags[278][0];
                    using var raw = new MemoryStream();
                    foreach (var (offset, length) in tags[273].Zip(tags[279]))
                    {
                        using var z = new ZLibStream(new MemoryStream(file, (int)offset, (int)length), CompressionMode.Decompress);
                        z.CopyTo(raw);
                    }
                    pixels = raw.ToArray();
                    Assert.Equal(w * h * channels, pixels.Length);
                    for (var y = 0; y < h; y++)
                        for (var i = channels; i < w * channels; i++) pixels[y * w * channels + i] += pixels[y * w * channels + i - channels];
                    Assert.True(rowsPerStrip > 0);
                }
                Assert.Equal(2u, tags[296][0]);
                var dpi = tags[282][0] / (double)tags[282][1];
                pages.Add(new Page(w, h, dpi, compression, ((int)tags[297][0], (int)tags[297][1]), pixels, tags[279]));
                at = (int)U32(file, at + 2 + count * 12);
            }
            return pages;
        }

        private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));

        private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
    }
}
