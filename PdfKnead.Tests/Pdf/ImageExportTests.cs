using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;
using Xunit;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// „Bild speichern“ in the image info: JPEG and JPEG 2000 byte for byte, everything else lossless in its own bit depth (PNG, CMYK as
/// TIFF) with profile and transparency; the files are read back with an independent reader written here for the purpose.
/// </summary>
public class ImageExportTests
{
    [Fact]
    public void Jpeg_ByteForByte_MaskBeside()
    {
        var jpeg = ImageInfoTests.Jpeg(40, 20, 80);
        var pdf = new TestPdf();
        var alpha = Ramp(40 * 20);
        var mask = pdf.Add(FlateStream(alpha, Image(40, 20, 8, N("DeviceGray"))));
        var image = ImageInfoTests.JpegImage(jpeg, 40, 20);
        image.Dictionary.Set(N("SMask"), Ref(mask));
        var result = Export(ImageInfoTests.OnePage("q 40 0 0 20 100 100 cm /Im Do Q", [("Im", image)], pdf));

        Assert.Equal(ImageExportFormat.Jpeg, result.Format);
        Assert.Equal(".jpg", result.Extension);
        Assert.Equal(jpeg, result.Data);
        var png = Png.Read(result.MaskPng!);
        Assert.Equal((40, 20, 8, 0), (png.Width, png.Height, png.Depth, png.Type));
        Assert.Equal(alpha, png.Raw);
    }

    [Fact]
    public void Gray16_KeepsAllSixteenBits()
    {
        var raw = new byte[64 * 2 * 8];
        for (var i = 0; i < raw.Length / 2; i++) BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(i * 2), (ushort)(i * 997 + 13));
        var result = Export(ImageInfoTests.OnePage("q 64 0 0 8 100 100 cm /Im Do Q", ("Im", FlateStream(raw, Image(64, 8, 16, N("DeviceGray"))))));

        Assert.Equal(ImageExportFormat.Png, result.Format);
        var png = Png.Read(result.Data);
        Assert.Equal((64, 8, 16, 0), (png.Width, png.Height, png.Depth, png.Type));
        Assert.Equal(raw, png.Raw);
        Assert.False(result.ColorsConverted);
    }

    [Fact]
    public void OneBit_InvertedDecode_StaysOneBit_WithTheVisibleValues()
    {
        // 12 pixels per row = 2 bytes; /Decode [1 0]: bit 1 is black
        byte[] raw = [0b1010_1010, 0b1111_0000, 0b0000_1111, 0b0101_0000];
        var dict = Image(12, 2, 1, N("DeviceGray"));
        dict.Set(N("Decode"), Box(1, 0));
        var png = Png.Read(Export(ImageInfoTests.OnePage("q 12 0 0 2 100 100 cm /Im Do Q", ("Im", new PdfStream(dict, raw)))).Data);

        Assert.Equal((12, 2, 1, 0), (png.Width, png.Height, png.Depth, png.Type));
        // the visible pixels: what was 1 (black) is now 0
        for (var x = 0; x < 12; x++)
        {
            Assert.Equal(1 - Bit(raw, x), Bit(png.Raw, x));
            Assert.Equal(1 - Bit(raw, 16 + x), Bit(png.Raw, 16 + x));
        }
    }

    [Fact]
    public void Palette_FromTheLookup_ColorKeyBecomesTransparentEntry()
    {
        // 4 bits per index, RGB base with four colours, colour key: index 2 transparent
        byte[] lookup = [255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30];
        var space = new PdfArray([N("Indexed"), N("DeviceRGB"), PdfInteger.Of(3), new PdfString(lookup)]);
        var dict = Image(4, 2, 4, space);
        dict.Set(N("Mask"), Box(2, 2));
        byte[] raw = [0x01, 0x23, 0x32, 0x10];
        var result = Export(ImageInfoTests.OnePage("q 4 0 0 2 100 100 cm /Im Do Q", ("Im", new PdfStream(dict, raw))));
        var png = Png.Read(result.Data);

        Assert.Equal((4, 2, 4, 3), (png.Width, png.Height, png.Depth, png.Type));
        Assert.Equal(lookup, png.Chunks["PLTE"]);
        Assert.Equal(new byte[] { 255, 255, 0 }, png.Chunks["tRNS"]);
        Assert.Equal(raw, png.Raw);
        Assert.False(result.ColorsConverted);
    }

    /// <summary>
    /// A colour space /Indexed given as a bare name (no base, no lookup — a crafted file): saving the image ended in an InvalidCastException
    /// (docs/FEHLERGEDAECHTNIS.md, No. 284); now it is saved as the display shows it, or refused with a message.
    /// </summary>
    [Fact]
    public void IndexedAsABareName_NoCastError()
    {
        var dict = Image(2, 2, 8, N("Indexed"));
        try
        {
            var result = Export(ImageInfoTests.OnePage("q 2 0 0 2 100 100 cm /Im Do Q", ("Im", new PdfStream(dict, [0, 1, 2, 3]))));
            Assert.NotEmpty(result.Data);
        }
        catch (Core.Pdf.PdfException)
        {
            // refused with a message: fine
        }
    }

    [Fact]
    public void Rgb_WithSoftMaskOfTheSameSize_AlphaChannel()
    {
        var pdf = new TestPdf();
        var rgb = Ramp(16 * 4 * 3);
        var alpha = Ramp(16 * 4, 7);
        var mask = pdf.Add(FlateStream(alpha, Image(16, 4, 8, N("DeviceGray"))));
        var image = FlateStream(rgb, Image(16, 4, 8, N("DeviceRGB")));
        image.Dictionary.Set(N("SMask"), Ref(mask));
        var result = Export(ImageInfoTests.OnePage("q 16 0 0 4 100 100 cm /Im Do Q", [("Im", image)], pdf));
        var png = Png.Read(result.Data);

        Assert.Null(result.MaskPng);
        Assert.Equal((16, 4, 8, 6), (png.Width, png.Height, png.Depth, png.Type));
        for (var i = 0; i < 16 * 4; i++)
        {
            Assert.Equal(rgb.AsSpan(i * 3, 3).ToArray(), png.Raw.AsSpan(i * 4, 3).ToArray());
            Assert.Equal(alpha[i], png.Raw[i * 4 + 3]);
        }
        // and the program's own reader sees the same
        var decoded = PngDecoder.Decode(result.Data)!;
        Assert.Equal(alpha[5], decoded.Pixels[5 * 4 + 3]);
    }

    [Fact]
    public void Rgb_MaskOfAnotherSize_Beside()
    {
        var pdf = new TestPdf();
        var mask = pdf.Add(FlateStream(Ramp(32 * 8), Image(32, 8, 8, N("DeviceGray"))));
        var image = FlateStream(Ramp(16 * 4 * 3), Image(16, 4, 8, N("DeviceRGB")));
        image.Dictionary.Set(N("SMask"), Ref(mask));
        var result = Export(ImageInfoTests.OnePage("q 16 0 0 4 100 100 cm /Im Do Q", [("Im", image)], pdf));

        Assert.Equal(2, Png.Read(result.Data).Type);   // RGB without alpha
        Assert.Equal((32, 8), (Png.Read(result.MaskPng!).Width, Png.Read(result.MaskPng!).Height));
    }

    [Theory]
    [InlineData("SMask", false)]
    [InlineData("SMask", true)]
    [InlineData("Mask", false)]
    [InlineData("Mask", true)]
    public void OneBitMaskBeside_StaysOneBit_WhiteWhereVisible(string key, bool decode)
    {
        // a 1-bit mask much larger than its image is written at its own depth (an 8-bit copy was eight times the data and took seconds
        // for masks of 150 megapixels); white = opaque: soft mask bit 1, image mask bit 0 (8.9.6.3), each flipped by /Decode [1 0]
        byte[] raw = [0b1100_1010, 0b0011_0000, 0b0000_1111, 0b1010_0000];
        var pdf = new TestPdf();
        var maskDict = Image(12, 2, 1, N("DeviceGray"));
        if (key == "Mask")
        {
            maskDict.Remove(N("ColorSpace"));
            maskDict.Remove(N("BitsPerComponent"));
            maskDict.Set(N("ImageMask"), PdfBoolean.True);
        }
        if (decode) maskDict.Set(N("Decode"), Box(1, 0));
        var mask = pdf.Add(new PdfStream(maskDict, raw));
        var image = FlateStream(Ramp(2 * 2 * 3), Image(2, 2, 8, N("DeviceRGB")));
        image.Dictionary.Set(N(key), Ref(mask));
        var result = Export(ImageInfoTests.OnePage("q 12 0 0 2 100 100 cm /Im Do Q", [("Im", image)], pdf));

        var png = Png.Read(result.MaskPng!);
        Assert.Equal((12, 2, 1, 0), (png.Width, png.Height, png.Depth, png.Type));
        var flip = key == "Mask" != decode;
        for (var x = 0; x < 12; x++)
        {
            Assert.Equal(flip ? 1 - Bit(raw, x) : Bit(raw, x), Bit(png.Raw, x));
            Assert.Equal(flip ? 1 - Bit(raw, 16 + x) : Bit(raw, 16 + x), Bit(png.Raw, 16 + x));
        }
    }

    [Fact]
    public void Cmyk_AsTiff_WithTheProfileTheProgramDrawsWith()
    {
        var raw = Ramp(10 * 3 * 4, 3);
        var result = Export(ImageInfoTests.OnePage("q 10 0 0 3 100 100 cm /Im Do Q", ("Im", FlateStream(raw, Image(10, 3, 8, N("DeviceCMYK"))))));

        Assert.Equal(ImageExportFormat.Tiff, result.Format);
        Assert.Equal(".tif", result.Extension);
        var tiff = Tiff.Read(result.Data);
        Assert.Equal((10, 3, 5, 4), (tiff.Width, tiff.Height, tiff.Photometric, tiff.Samples));
        Assert.Equal(raw, tiff.Raw);
        var fogra = File.ReadAllBytes(Directory.GetFiles(Path.Combine(Repo(), "Farbprofile"), "*.icc").First());
        Assert.Equal(fogra, tiff.Icc);
        // the resolution every Baseline image needs (TIFF 6.0, sections 8 and 16; No. 942): 72 per inch
        Assert.Equal(new uint[] { 7200, 100 }, tiff.Tags[282]);
        Assert.Equal(new uint[] { 7200, 100 }, tiff.Tags[283]);
        Assert.Equal(new uint[] { 2 }, tiff.Tags[296]);
    }

    [Fact]
    public void Cmyk16_IccBased_WithAlpha_KeepsItsOwnProfile()
    {
        var pdf = new TestPdf();
        var profileBytes = File.ReadAllBytes(Directory.GetFiles(Path.Combine(Repo(), "Farbprofile"), "*.icc").First());
        var profile = pdf.Add(new PdfStream(Dict(("N", PdfInteger.Of(4))), profileBytes));
        var raw = Ramp(6 * 2 * 4 * 2, 11);
        var alpha = Ramp(6 * 2, 40);
        var mask = pdf.Add(FlateStream(alpha, Image(6, 2, 8, N("DeviceGray"))));
        var image = FlateStream(raw, Image(6, 2, 16, new PdfArray([N("ICCBased"), Ref(profile)])));
        image.Dictionary.Set(N("SMask"), Ref(mask));
        var tiff = Tiff.Read(Export(ImageInfoTests.OnePage("q 6 0 0 2 100 100 cm /Im Do Q", [("Im", image)], pdf)).Data);

        Assert.Equal((5, 16), (tiff.Samples, tiff.Bits));
        Assert.Equal(profileBytes, tiff.Icc);
        for (var i = 0; i < 12; i++)
        {
            Assert.Equal(raw.AsSpan(i * 8, 8).ToArray(), tiff.Raw.AsSpan(i * 10, 8).ToArray());
            Assert.Equal(alpha[i] * 257, BinaryPrimitives.ReadUInt16BigEndian(tiff.Raw.AsSpan(i * 10 + 8)));
        }
    }

    [Theory]
    [InlineData("CalRGB")]
    [InlineData("CalGray")]
    public void Calibrated_RawValues_WithAProfileThatDrawsTheSame(string family)
    {
        // a CalRGB like old producers write it (own white point, gamma 1.8/2.2, a matrix) and a CalGray with gamma 2.2
        var parameters = family == "CalRGB"
            ? Dict(("WhitePoint", Box(0.9505, 1, 1.089)), ("Gamma", Box(1.8, 2.2, 2.2)),
                ("Matrix", Box(0.4497, 0.2446, 0.0252, 0.3163, 0.672, 0.1412, 0.1845, 0.0833, 0.9227)))
            : Dict(("WhitePoint", Box(0.9505, 1, 1.089)), ("Gamma", new PdfReal(2.2)));
        var space = new PdfArray([N(family), parameters]);
        var n = family == "CalRGB" ? 3 : 1;
        var raw = Ramp(64 * n, 4);
        var result = Export(ImageInfoTests.OnePage("q 64 0 0 1 100 100 cm /Im Do Q", ("Im", FlateStream(raw, Image(64, 1, 8, space)))));
        var png = Png.Read(result.Data);

        Assert.False(result.ColorsConverted);
        Assert.Equal(raw, png.Raw);
        var profile = Inflate(png.Chunks["iCCP"].AsSpan(13));   // name "ICC Profile", terminator, method
        using var doc = PdfDocument.Open(ImageInfoTests.OnePage(""));
        var drawn = ColorSpace.Create(space, null, doc);
        var described = ColorSpace.FromProfile(profile, n)!;
        for (var i = 0; i < 64; i++)
        {
            var values = raw.AsSpan(i * n, n).ToArray().Select(v => v / 255.0).ToArray();
            var (a, b) = (drawn.ToRgb(values), described.ToRgb(values));
            Assert.InRange(Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B))), 0, 2);
        }
    }

    [Fact]
    public void Palette_WithIccRgbBase_KeepsTheProfile()
    {
        var pdf = new TestPdf();
        var profileBytes = CalibratedProfile.Rgb(null);
        var profile = pdf.Add(new PdfStream(Dict(("N", PdfInteger.Of(3))), profileBytes));
        var space = new PdfArray([N("Indexed"), new PdfArray([N("ICCBased"), Ref(profile)]), PdfInteger.Of(1), new PdfString([1, 2, 3, 4, 5, 6])]);
        var result = Export(ImageInfoTests.OnePage("q 2 0 0 1 100 100 cm /Im Do Q", [("Im", new PdfStream(Image(2, 1, 8, space), [0, 1]))], pdf));
        var png = Png.Read(result.Data);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, png.Chunks["PLTE"]);
        Assert.Equal(profileBytes, Inflate(png.Chunks["iCCP"].AsSpan(13)));
    }

    [Fact]
    public void Lab_ConvertedToRgb_AsDrawn()
    {
        var space = new PdfArray([N("Lab"), Dict(("WhitePoint", Box(0.9505, 1, 1.089)), ("Range", Box(-100, 100, -100, 100)))]);
        byte[] raw = [255, 128, 128, 0, 128, 128];
        var result = Export(ImageInfoTests.OnePage("q 2 0 0 1 100 100 cm /Im Do Q", ("Im", FlateStream(raw, Image(2, 1, 8, space)))));
        var png = Png.Read(result.Data);

        Assert.True(result.ColorsConverted);
        Assert.Equal((8, 2), (png.Depth, png.Type));
        Assert.True(png.Raw[0] > 240 && png.Raw[3] < 15);   // L = 100 white, L = 0 black
    }

    [Fact]
    public void Stencil_PaintedPlacesBlack()
    {
        var dict = Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(8)), ("Height", PdfInteger.Of(1)),
            ("ImageMask", PdfBoolean.True));
        var result = Export(ImageInfoTests.OnePage("q 8 0 0 1 100 100 cm /Im Do Q", ("Im", new PdfStream(dict, [0b0011_1100]))));

        Assert.True(result.Stencil);
        Assert.Equal(new byte[] { 0b0011_1100 }, Png.Read(result.Data).Raw);   // bit 0 paints → 0 = black
    }

    [Fact]
    public void InlineImage_WithAbbreviations()
    {
        var content = "q 4 0 0 1 100 100 cm BI /W 4 /H 1 /BPC 8 /CS /G /F /AHx ID 00407FFF> EI Q";
        var png = Png.Read(Export(ImageInfoTests.OnePage(content)).Data);
        Assert.Equal((4, 1, 8, 0), (png.Width, png.Height, png.Depth, png.Type));
        Assert.Equal(new byte[] { 0x00, 0x40, 0x7F, 0xFF }, png.Raw);
    }

    [Fact]
    public void LargeImage_WrittenRowByRow_WithFewAllocations()
    {
        // 3000 × 3000 RGB, 27 MB raw: written straight into a stream, the program allocates memory for rows, not for the image
        const int side = 3000;
        var raw = new byte[side * side * 3];
        for (var i = 0; i < raw.Length; i++) raw[i] = (byte)(i * 31 >> 7);
        var pdf = ImageInfoTests.OnePage("q 300 0 0 300 100 100 cm /Im Do Q", ("Im", FlateStream(raw, Image(side, side, 8, N("DeviceRGB")))));
        raw = null;
        using var doc = PdfDocument.Open(pdf);
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        var source = list.Images[list.Items.Span.ToArray().First(i => i.Kind == DisplayItemKind.Image).Image];
        var counter = new CountingStream();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var plan = ImageExport.Write(doc, source, counter, null);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(ImageExportFormat.Png, plan.Format);
        Assert.True(counter.Written > 1000);
        // the compressed stream data of the PDF itself is read once (it is held by the document as well); a few MB on top
        Assert.True(allocated < 12 << 20, $"{allocated >> 20} MB belegt");
    }

    private sealed class CountingStream : Stream
    {
        public long Written { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Written += buffer.Length;
    }

    [Fact]
    public void PngEncoder_EveryFilterRoundTrips()
    {
        // noise and smooth gradients, so that all five row filters are chosen
        var random = new Random(7);
        int w = 37, h = 23;
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w * 4; x++)
                pixels[y * w * 4 + x] = y % 3 == 0 ? (byte)random.Next(256) : (byte)(x * 3 + y * 5);
        var result = ImageExport.Export(new RgbaImage(w, h, pixels));
        Assert.Equal(pixels, Png.Read(result.Data).Raw);
        Assert.Equal(pixels, PngDecoder.Decode(result.Data)!.Pixels);
    }

    // ------------------------------------------------------------ helpers

    private static ExportedImage Export(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        var item = list.Items.Span.ToArray().First(i => i.Kind == DisplayItemKind.Image);
        return ImageExport.Export(doc, list.Images[item.Image]);
    }

    private static PdfDictionary Image(int width, int height, int bpc, PdfObject space) =>
        Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(width)), ("Height", PdfInteger.Of(height)),
            ("BitsPerComponent", PdfInteger.Of(bpc)), ("ColorSpace", space));

    private static byte[] Ramp(int length, int step = 1)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)(i * step + i / 7);
        return data;
    }

    private static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        using var z = new ZLibStream(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
        using var result = new MemoryStream();
        z.CopyTo(result);
        return result.ToArray();
    }

    private static int Bit(byte[] data, int index) => data[index >> 3] >> (7 - (index & 7)) & 1;

    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    /// <summary>A small PNG reader of its own (unfiltered rows as stored, 16 bits included): checks the encoder independently.</summary>
    private sealed record Png(int Width, int Height, int Depth, int Type, byte[] Raw, Dictionary<string, byte[]> Chunks)
    {
        public static Png Read(byte[] file)
        {
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, file[..8]);
            var chunks = new Dictionary<string, byte[]>();
            using var idat = new MemoryStream();
            for (var at = 8; at < file.Length;)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(at));
                var type = Encoding.ASCII.GetString(file, at + 4, 4);
                var body = file.AsSpan(at + 8, length);
                var crc = Crc(file.AsSpan(at + 4, length + 4));
                Assert.Equal(BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 8 + length)), crc);
                if (type == "IDAT") idat.Write(body);
                else chunks[type] = body.ToArray();
                at += 12 + length;
            }
            var ihdr = chunks["IHDR"];
            int w = BinaryPrimitives.ReadInt32BigEndian(ihdr), h = BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(4)), depth = ihdr[8], kind = ihdr[9];
            var channels = kind switch { 0 or 3 => 1, 4 => 2, 2 => 3, _ => 4 };
            var rowBytes = (w * channels * depth + 7) / 8;
            var bpp = Math.Max(1, channels * depth / 8);
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            using var inflated = new MemoryStream();
            z.CopyTo(inflated);
            var data = inflated.ToArray();
            var raw = new byte[rowBytes * h];
            for (var y = 0; y < h; y++)
            {
                var filter = data[y * (rowBytes + 1)];
                for (var i = 0; i < rowBytes; i++)
                {
                    int v = data[y * (rowBytes + 1) + 1 + i];
                    int a = i >= bpp ? raw[y * rowBytes + i - bpp] : 0, b = y > 0 ? raw[(y - 1) * rowBytes + i] : 0;
                    int c = i >= bpp && y > 0 ? raw[(y - 1) * rowBytes + i - bpp] : 0;
                    var p = a + b - c;
                    var paeth = Math.Abs(p - a) <= Math.Abs(p - b) && Math.Abs(p - a) <= Math.Abs(p - c) ? a : Math.Abs(p - b) <= Math.Abs(p - c) ? b : c;
                    raw[y * rowBytes + i] = (byte)(v + filter switch { 1 => a, 2 => b, 3 => (a + b) / 2, 4 => paeth, _ => 0 });
                }
            }
            return new Png(w, h, depth, kind, raw, chunks);
        }

        private static uint Crc(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var d in data)
            {
                crc ^= d;
                for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
            return crc ^ 0xFFFFFFFF;
        }
    }

    /// <summary>A small TIFF reader for what <see cref="TiffEncoder"/> writes (big-endian, Deflate, predictor 2).</summary>
    private sealed record Tiff(int Width, int Height, int Photometric, int Samples, int Bits, byte[] Raw, byte[]? Icc, Dictionary<int, uint[]> Tags)
    {
        public static Tiff Read(byte[] file)
        {
            Assert.Equal("MM"u8.ToArray(), file[..2]);
            Assert.Equal(42, BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(2)));
            var ifd = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4));
            Assert.Equal(0, ifd % 2);   // on a word boundary (TIFF 6.0, section 2)
            var count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(ifd));
            var tags = new Dictionary<int, uint[]>();
            byte[]? icc = null;
            var lastTag = 0;
            for (var i = 0; i < count; i++)
            {
                var e = file.AsSpan(ifd + 2 + i * 12, 12);
                int tag = BinaryPrimitives.ReadUInt16BigEndian(e), type = BinaryPrimitives.ReadUInt16BigEndian(e[2..]);
                Assert.True(tag > lastTag);   // ascending, as TIFF requires
                lastTag = tag;
                var n = (int)BinaryPrimitives.ReadUInt32BigEndian(e[4..]);
                var size = type switch { 3 => 2, 4 => 4, 5 => 8, _ => 1 } * n;
                var at = size <= 4 ? ifd + 2 + i * 12 + 8 : (int)BinaryPrimitives.ReadUInt32BigEndian(e[8..]);
                if (tag == 34675)
                {
                    icc = file[at..(at + n)];
                    continue;
                }
                // RATIONAL: numerator and denominator as two values
                tags[tag] = Enumerable.Range(0, type == 5 ? 2 * n : n).Select(k => type == 3 ? BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at + k * 2)) : BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + k * 4))).ToArray();
            }
            int w = (int)tags[256][0], h = (int)tags[257][0], spp = (int)tags[277][0], bits = (int)tags[258][0];
            Assert.Equal(8u, tags[259][0]);
            Assert.Equal(2u, tags[317][0]);
            using var raw = new MemoryStream();
            for (var s = 0; s < tags[273].Length; s++)
            {
                using var z = new ZLibStream(new MemoryStream(file, (int)tags[273][s], (int)tags[279][s]), CompressionMode.Decompress);
                z.CopyTo(raw);
            }
            var data = raw.ToArray();
            var rowBytes = w * spp * bits / 8;
            var stride = spp * bits / 8;
            for (var y = 0; y < h; y++)
                if (bits == 8)
                    for (var i = stride; i < rowBytes; i++) data[y * rowBytes + i] += data[y * rowBytes + i - stride];
                else
                    for (var i = stride; i < rowBytes; i += 2)
                    {
                        var at = y * rowBytes + i;
                        var v = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at)) + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at - stride)));
                        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at), v);
                    }
            return new Tiff(w, h, (int)tags[262][0], spp, bits, data, icc, tags);
        }
    }
}
