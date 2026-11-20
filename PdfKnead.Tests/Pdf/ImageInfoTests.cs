using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using SkiaSharp;
using Xunit;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Image info: accuracy of the details (quality, EXIF, XMP, colour profile, placement, dpi) and security against crafted
/// images: no crash, no external loading, no endless loop, nothing invisible in the display.
/// </summary>
public class ImageInfoTests
{
    // ------------------------------------------------------------ Accuracy

    [Theory]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(75)]
    [InlineData(90)]
    [InlineData(95)]
    public void JpegQuality_EstimatedFromTables(int quality)
    {
        var meta = ImageMetadata.FromJpeg(Jpeg(64, 48, quality))!;
        Assert.InRange(meta.Quality!.Value, quality - 2, quality + 2);
        Assert.Equal("4:2:0", meta.Subsampling);
        Assert.False(meta.Progressive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Exif_BothByteOrders(bool little)
    {
        var jpeg = WithSegments(Jpeg(32, 32, 80), Exif(little));
        var meta = ImageMetadata.FromJpeg(jpeg)!;
        Assert.Equal("Mustermarke", meta.Make);
        Assert.Equal("Mustermarke Scan 100", meta.Model);
        Assert.Equal("Scanprogramm 3.0", meta.Software);
        Assert.Equal(new DateTime(2019, 6, 15, 9, 30, 0), meta.Taken);
        Assert.Equal((300.0, 300.0), meta.StoredDpi);
        Assert.Equal(6, meta.Orientation);
        Assert.NotNull(meta.Gps);
        Assert.Equal(51.51, meta.Gps!.Value.Latitude, 5);
        Assert.Equal(-7.255, meta.Gps.Value.Longitude, 5);
    }

    [Fact]
    public void Jfif_DotsPerCentimetre_InDpi()
    {
        // JFIF: unit 2 = dots per cm; 118 per cm ≈ 300 dpi
        var app0 = Segment(0xE0, [.. "JFIF\0"u8, 1, 2, 2, 0, 118, 0, 118, 0, 0]);
        var meta = ImageMetadata.FromJpeg(WithSegments(Jpeg(16, 16, 80), app0))!;
        Assert.Equal(299.72, meta.StoredDpi!.Value.X, 2);
    }

    [Fact]
    public void Xmp_FieldsAsAttributesAndElements()
    {
        var xmp = """
            <x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
            <rdf:Description xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmlns:tiff="http://ns.adobe.com/tiff/1.0/" xmp:CreatorTool="Bildprogramm 2.1">
            <tiff:Model>Mustermarke Scan 200</tiff:Model><xmp:CreateDate>2024-05-01T10:20:30Z</xmp:CreateDate></rdf:Description></rdf:RDF></x:xmpmeta>
            """;
        var meta = ImageMetadata.FromJpeg(WithSegments(Jpeg(16, 16, 80), Segment(0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. Encoding.UTF8.GetBytes(xmp)])))!;
        Assert.Equal("Bildprogramm 2.1", meta.Software);
        Assert.Equal("Mustermarke Scan 200", meta.Model);
        Assert.Equal(new DateTime(2024, 5, 1, 10, 20, 30), meta.Taken);
    }

    [Fact]
    public void IccName_FromProfiles()
    {
        // The bundled CMYK profile (version 2, "desc") and a hand-built version 4 profile ("mluc")
        var fogra = Directory.GetFiles(Path.Combine(Repo(), "Farbprofile"), "*.icc").First();
        Assert.Contains("FOGRA39", ImageMetadata.IccName(File.ReadAllBytes(fogra)), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Mein Profil", ImageMetadata.IccName(IccV4("Mein Profil")));
    }

    [Fact]
    public void Placement_SizeDpiRotation_AndOnlyImagesCount()
    {
        // Two images on top of each other with a rectangle above them: the image info finds both, the upper one first; the rectangle does not interfere
        var jpeg = WithSegments(Jpeg(300, 150, 85), Exif(true));
        using var doc = PdfDocument.Open(OnePage(
            "q 216 0 0 108 72 600 cm /Unten Do Q q 0 72 -72 0 200 620 cm /Oben Do Q 0 0 1 rg 60 590 300 150 re f",
            ("Unten", JpegImage(jpeg, 300, 150)), ("Oben", JpegImage(Jpeg(100, 100, 60), 100, 100))));
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        var hits = ImageInspector.ImagesAt(list, 170, 640);
        Assert.Equal(2, hits.Count);
        var top = ImageInspector.Inspect(doc, doc.Pages[0], list, hits[0]);
        var bottom = ImageInspector.Inspect(doc, doc.Pages[0], list, hits[1]);

        Assert.Equal(90, top.Rotation);
        Assert.Equal(100, top.DpiX, 3);   // 100 pixels on 72 pt = 1 inch
        Assert.InRange(top.Embedded!.Quality!.Value, 58, 62);

        Assert.Equal(ImageCodec.Jpeg, bottom.Codec);
        Assert.Equal((300, 150), (bottom.Width, bottom.Height));
        Assert.Equal(100, bottom.DpiX, 3);   // 300 pixels on 216 pt = 3 inches
        Assert.Equal(76.2, bottom.WidthPoints / 72 * 25.4, 3);
        Assert.Equal("Mustermarke Scan 100", bottom.Embedded!.Model);
        Assert.Equal(0, bottom.Rotation);
        Assert.False(bottom.LikelyScan);   // does not cover the page

        // Beside it: no image
        Assert.Empty(ImageInspector.ImagesAt(list, 500, 100));
        var text = ImageInspector.AsText(ImageInspector.Sections(bottom));
        Assert.Contains("Mustermarke Scan 100", text);
        Assert.Contains("100 dpi", text);
    }

    [Fact]
    public void InsertedImage_PixelsAndPlacement()
    {
        // Image inserted in PDF Knead: 200 × 100 pixels on 2 × 1 inches, once straight, once rotated by 90°
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var pixels = new RgbaImage(200, 100, new byte[200 * 100 * 4]);
        var straight = new Core.Pdf.Editing.ImageElement { Rect = new PdfRectangle(100, 500, 244, 572), Image = pixels };
        var info = ImageInspector.Inspect(straight, doc.Pages[0]);
        Assert.True(info.FromElement);
        Assert.Equal(100, info.DpiX, 3);
        Assert.Equal(100, info.DpiY, 3);
        Assert.Equal(0, info.Rotation);

        var turned = straight with { Rect = new PdfRectangle(100, 500, 172, 644), Rotation = 90 };
        var rotated = ImageInspector.Inspect(turned, doc.Pages[0]);
        Assert.Equal(100, rotated.DpiX, 3);
        Assert.NotEqual(0, rotated.Rotation);
        Assert.DoesNotContain(ImageInspector.Sections(rotated), s => s.Title == Tr.T("Herkunft"));
    }

    [Fact]
    public void ScanOfAWholePage_IsRecognised()
    {
        // 1-bit image over the whole A4 page at 300 dpi: probably a scan
        var bits = new byte[(2480 + 7) / 8 * 3508];
        var image = Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(2480)), ("Height", PdfInteger.Of(3508)),
            ("BitsPerComponent", PdfInteger.Of(1)), ("ColorSpace", N("DeviceGray")));
        using var doc = PdfDocument.Open(OnePage("q 595.28 0 0 841.89 0 0 cm /Scan Do Q", ("Scan", FlateStream(bits, image))));
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        var info = ImageInspector.Inspect(doc, doc.Pages[0], list, ImageInspector.ImagesAt(list, 300, 400)[0]);
        Assert.True(info.CoversPage);
        Assert.True(info.TypicalScanResolution);
        Assert.True(info.LikelyScan);
        Assert.Equal(300, info.DpiX, 0);
    }

    // ------------------------------------------------------------ Security

    [Fact]
    public void Xmp_WithDtd_IsNotExpanded()
    {
        // "Billion laughs" and an external entity: no DTD, no external loading; the field stays empty, nothing throws
        var bomb = """
            <?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">
            <!ENTITY e SYSTEM "file:///c:/windows/win.ini">]><x:xmpmeta xmlns:x="adobe:ns:meta/"><Model>&c;&e;</Model></x:xmpmeta>
            """;
        var meta = ImageMetadata.Xmp(new ImageMetadata(), Encoding.UTF8.GetBytes(bomb));
        Assert.Null(meta.Model);
    }

    [Fact]
    public void Texts_WithoutControlAndDirectionCharacters()
    {
        // U+202E reverses the reading direction ("gpj.exe" would look like "exe.jpg"); control characters could disturb the terminal
        Assert.Equal("Scanner gpj.exe", ImageMetadata.Clean("Scanner\u202E gpj.exe"));
        Assert.Equal("a  b", ImageMetadata.Clean("a\u001B b\u200B"));
        Assert.Null(ImageMetadata.Clean("\u202E\u0007 "));
        Assert.Equal(120, ImageMetadata.Clean(new string('x', 10_000))!.Length);
    }

    [Fact]
    public void SelfReferencingPalette_DoesNotLoop()
    {
        // Indexed whose base is itself again (via a reference): would otherwise end in a stack overflow
        var pdf = new TestPdf();
        var space = pdf.Add(new PdfArray());
        pdf.Replace(space, new PdfArray([N("Indexed"), Ref(space), PdfInteger.Of(1), new PdfString([0, 0, 0, 255, 255, 255])]));
        var image = Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(2)), ("Height", PdfInteger.Of(2)),
            ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", Ref(space)));
        using var doc = PdfDocument.Open(OnePage("q 100 0 0 100 100 100 cm /Im Do Q", [("Im", new PdfStream(image, [0, 1, 1, 0]))], pdf));
        var list = PageInterpreter.Build(doc, doc.Pages[0]);
        foreach (var index in ImageInspector.ImagesAt(list, 150, 150))
            Assert.Equal("Indexed", ImageInspector.Inspect(doc, doc.Pages[0], list, index).ColorSpace);
    }

    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void Fuzzed_Headers_NeverThrow_AndStayFast()
    {
        // Valid headers randomly corrupted (bits, lengths, truncation, repetition): never an exception, fast in total
        var random = new Random(4711);
        var jpeg = WithSegments(Jpeg(40, 30, 70), Exif(true), Exif(false), Segment(0xE2, [.. "ICC_PROFILE\0"u8, 1, 1, .. IccV4("Profil")]),
            Segment(0xE1, [.. "http://ns.adobe.com/xap/1.0/\0"u8, .. "<x:xmpmeta xmlns:x='adobe:ns:meta/'><Make>A</Make></x:xmpmeta>"u8]));
        var jpx = Jpx();
        var icc = IccV4("Profil");
        var total = Stopwatch.StartNew();
        var slowest = 0L;
        for (var round = 0; round < 20_000; round++)
        {
            var (source, kind) = (round % 3) switch { 0 => (jpeg, 0), 1 => (jpx, 1), _ => (icc, 2) };
            var data = Mutate(source, random);
            var watch = Stopwatch.StartNew();
            switch (kind)
            {
                case 0:
                    ImageMetadata.FromJpeg(data);
                    break;
                case 1:
                    ImageMetadata.FromJpx(data);
                    break;
                default:
                    ImageMetadata.IccName(data);
                    break;
            }
            slowest = Math.Max(slowest, watch.ElapsedMilliseconds);
        }
        // About 0.25 s for all rounds on their own; the total time reveals a file that runs into a loop, without a garbage
        // collection pause under load (full test run) turning a single call into a finding
        Assert.True(total.ElapsedMilliseconds < 15_000 * TestKind.Factor, $"20 000 Runden in {total.ElapsedMilliseconds} ms, langsamster Aufruf {slowest} ms");
    }

    // ------------------------------------------------------------ Helpers

    private static byte[] Mutate(byte[] source, Random random)
    {
        var data = source.ToArray();
        switch (random.Next(5))
        {
            case 0:
                for (var i = 0; i < 1 + random.Next(8); i++) data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
                break;
            case 1:
                for (var i = 0; i < 1 + random.Next(4); i++) data[random.Next(data.Length)] = (byte)random.Next(256);
                break;
            case 2:
                data = data[..random.Next(data.Length)];
                break;
            case 3:
                // Length fields (two or four bytes) with extreme values
                var at = random.Next(Math.Max(1, data.Length - 4));
                var extreme = (byte)(random.Next(2) == 0 ? 0xFF : 0x00);
                for (var i = 0; i < 4 && at + i < data.Length; i++) data[at + i] = extreme;
                break;
            default:
                var start = random.Next(data.Length);
                var length = Math.Min(64, data.Length - start);
                data = [.. data[..start], .. data.AsSpan(start, length), .. data[start..]];
                break;
        }
        return data;
    }

    internal static byte[] Jpeg(int width, int height, int quality)
    {
        using var bitmap = new SKBitmap(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 255 / width), (byte)(y * 255 / height), (byte)((x + y) * 7)));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    /// <summary>Inserts segments directly after SOI.</summary>
    internal static byte[] WithSegments(byte[] jpeg, params byte[][] segments) => [.. jpeg[..2], .. segments.SelectMany(s => s), .. jpeg[2..]];

    private static byte[] Segment(byte marker, byte[] body)
    {
        var length = body.Length + 2;
        return [0xFF, marker, (byte)(length >> 8), (byte)length, .. body];
    }

    /// <summary>Builds EXIF (APP1) with device, software, date, 300 dpi, orientation 6 and GPS 51° 30′ 36″ N, 7° 15′ 18″ W.</summary>
    internal static byte[] Exif(bool little)
    {
        var tiff = new List<byte>();
        void U16(int v) { var b = new byte[2]; if (little) BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); else BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v); tiff.AddRange(b); }
        void U32(long v) { var b = new byte[4]; if (little) BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v); else BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); tiff.AddRange(b); }

        var extra = new List<byte>();
        var entries0 = new List<(int Tag, int Type, int Count, Func<int, long> Value)>();
        // Values longer than 4 bytes lie behind the directories; offsets are fixed while writing
        int Data(byte[] bytes)
        {
            var offset = extra.Count;
            extra.AddRange(bytes);
            if (extra.Count % 2 == 1) extra.Add(0);
            return offset;
        }
        byte[] Rational(params (uint N, uint D)[] values)
        {
            var b = new byte[8 * values.Length];
            for (var i = 0; i < values.Length; i++)
            {
                if (little) { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8 * i), values[i].N); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8 * i + 4), values[i].D); }
                else { BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 * i), values[i].N); BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8 * i + 4), values[i].D); }
            }
            return b;
        }
        var make = Data("Mustermarke\0"u8.ToArray());
        var model = Data("Mustermarke Scan 100\0"u8.ToArray());
        var software = Data("Scanprogramm 3.0\0"u8.ToArray());
        var date = Data("2019:06:15 09:30:00\0"u8.ToArray());
        var xres = Data(Rational((300, 1)));
        var yres = Data(Rational((600, 2)));
        var lat = Data(Rational((51, 1), (30, 1), (3600, 100)));
        var lon = Data(Rational((7, 1), (15, 1), (18, 1)));

        // IFD0 (8 entries + GPS pointer) at 8, GPS IFD (4 entries) after it, then the data
        const int ifd0Count = 9, gpsCount = 4;
        var ifd0 = 8;
        var gps = ifd0 + 2 + 12 * ifd0Count + 4;
        var dataStart = gps + 2 + 12 * gpsCount + 4;
        tiff.AddRange(little ? "II"u8.ToArray() : "MM"u8.ToArray());
        U16(42);
        U32(ifd0);
        void Entry(int tag, int type, int count, long value, bool inline = false)
        {
            U16(tag);
            U16(type);
            U32(count);
            if (inline && type == 3) { U16((int)value); U16(0); }
            else if (inline && type == 2) { tiff.AddRange(Encoding.ASCII.GetBytes(((char)value).ToString())); tiff.AddRange(new byte[3]); }
            else U32(value);
        }
        U16(ifd0Count);
        Entry(0x010F, 2, 12, dataStart + make);
        Entry(0x0110, 2, 21, dataStart + model);
        Entry(0x0112, 3, 1, 6, inline: true);
        Entry(0x011A, 5, 1, dataStart + xres);
        Entry(0x011B, 5, 1, dataStart + yres);
        Entry(0x0128, 3, 1, 2, inline: true);
        Entry(0x0131, 2, 17, dataStart + software);
        Entry(0x0132, 2, 20, dataStart + date);
        Entry(0x8825, 4, 1, gps);
        U32(0);
        U16(gpsCount);
        Entry(1, 2, 2, 'N', inline: true);
        Entry(2, 5, 3, dataStart + lat);
        Entry(3, 2, 2, 'W', inline: true);
        Entry(4, 5, 3, dataStart + lon);
        U32(0);
        tiff.AddRange(extra);
        return Segment(0xE1, [.. "Exif\0\0"u8, .. tiff]);
    }

    /// <summary>Builds an ICC profile version 4 with only one description ("mluc", English).</summary>
    private static byte[] IccV4(string name)
    {
        var text = Encoding.BigEndianUnicode.GetBytes(name);
        var tag = new byte[28 + text.Length];
        "mluc"u8.CopyTo(tag);
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(12), 12);
        "enUS"u8.CopyTo(tag.AsSpan(16));
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(20), (uint)text.Length);
        BinaryPrimitives.WriteUInt32BigEndian(tag.AsSpan(24), 28);
        text.CopyTo(tag, 28);
        var profile = new byte[144 + tag.Length];
        BinaryPrimitives.WriteUInt32BigEndian(profile, (uint)profile.Length);
        "acsp"u8.CopyTo(profile.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(128), 1);
        "desc"u8.CopyTo(profile.AsSpan(132));
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(136), 144);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(140), (uint)tag.Length);
        tag.CopyTo(profile, 144);
        return profile;
    }

    /// <summary>Builds the header of a JPEG 2000 file: signature, ftyp, jp2h with ihdr, colr (sRGB) and res/resc (300 dpi).</summary>
    private static byte[] Jpx()
    {
        static byte[] Box(string type, byte[] body)
        {
            var box = new byte[8 + body.Length];
            BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
            body.CopyTo(box, 8);
            return box;
        }
        // 300 dpi = 11811 pixels per metre: 11811 / 1 · 10^0
        byte[] resc = [0x2E, 0x23, 0, 1, 0x2E, 0x23, 0, 1, 0, 0];
        var header = Box("jp2h", [.. Box("ihdr", new byte[14]), .. Box("colr", [1, 0, 0, 0, 0, 0, 16]), .. Box("res ", Box("resc", resc))]);
        return [.. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]), .. Box("ftyp", "jp2 \0\0\0\0jp2 "u8.ToArray()), .. header];
    }

    [Fact]
    public void Jpx_ColorAndResolution()
    {
        var meta = ImageMetadata.FromJpx(Jpx())!;
        Assert.Equal(16, meta.JpxColor);
        Assert.Equal(300, meta.StoredDpi!.Value.X, 0);
    }

    internal static PdfStream JpegImage(byte[] jpeg, int width, int height) =>
        new(Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(width)), ("Height", PdfInteger.Of(height)),
            ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", N("DeviceRGB")), ("Filter", N("DCTDecode"))), jpeg);

    /// <summary>Builds an A4 page with content and images as XObjects.</summary>
    internal static byte[] OnePage(string content, params (string Name, PdfStream Image)[] images) => OnePage(content, images, null);

    internal static byte[] OnePage(string content, (string Name, PdfStream Image)[] images, TestPdf? pdf)
    {
        pdf ??= new TestPdf();
        var xobjects = new PdfDictionary();
        foreach (var (name, image) in images) xobjects.Set(N(name), Ref(pdf.Add(image)));
        var pages = pdf.Add(new PdfDictionary());
        var stream = pdf.Add(new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes(content)));
        var page = pdf.Add(Dict(("Type", N("Page")), ("Parent", Ref(pages)), ("Contents", Ref(stream)), ("MediaBox", Box(0, 0, 595.28, 841.89)),
            ("Resources", Dict(("XObject", xobjects)))));
        pdf.Replace(pages, Dict(("Type", N("Pages")), ("Kids", new PdfArray([Ref(page)])), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(Dict(("Type", N("Catalog")), ("Pages", Ref(pages))));
        return pdf.Build();
    }

    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
