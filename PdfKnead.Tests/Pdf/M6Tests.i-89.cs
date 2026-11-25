using System.Text;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;
using SkiaSharp;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Graphics and colour (ISO 32000-2, chapter 8; Konformitätsabgleich M6, rewritten sentence by sentence): the Nachweise of the draft i-89
/// (8.9 images) that had none.
/// </summary>
public sealed partial class M6NormTests
{
    // ---- helpers of draft i-89 (8.9)

    /// <summary>A one-page file of 200 × 200: object 1 the catalogue (with <paramref name="catalog"/>), 3 the page with
    /// <paramref name="resources"/>, 4 the content, 5 … the objects given.</summary>
    private static byte[] I89_File(string catalog, string resources, string content, params string[] more) => DocumentStructureNormTests.FileOf([
        $"<< /Type /Catalog /Pages 2 0 R {catalog} >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << {resources} >> /Contents 4 0 R >>",
        $"<< /Length {content.Length} >>\nstream\n{content}\nendstream", .. more]);

    /// <summary>An image XObject with the entries given and its data as stored.</summary>
    private static string I89_Image(string entries, string data) =>
        $"<< /Type /XObject /Subtype /Image {entries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    /// <summary>The first page drawn at 72 dpi (one pixel per unit).</summary>
    private static SKBitmap I89_Render(byte[] file, bool forPrint = false)
    {
        using var doc = PdfDocument.Open(file);
        var page = doc.Pages[0];
        return SkiaRenderer.RenderPage(PageInterpreter.Build(doc, page, forPrint: forPrint), doc, page, 1, new ImageCache());
    }

    /// <summary>The image /Im0 (object 5) drawn over the whole page; further objects from 6 on.</summary>
    private static SKBitmap I89_FullImage(string image, params string[] more) =>
        I89_Render(I89_File("", "/XObject << /Im0 5 0 R >>", "q 200 0 0 200 0 0 cm /Im0 Do Q", [image, .. more]));

    /// <summary>The pixel at (<paramref name="x"/>, <paramref name="y"/>) in page units, y counted from the bottom.</summary>
    private static SKColor I89_At(SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, bitmap.Height - 1 - y);

    private static bool I89_Red(SKColor c) => c is { Red: > 200, Green: < 60, Blue: < 60 };

    private static bool I89_Blue(SKColor c) => c is { Red: < 60, Green: < 60, Blue: > 200 };

    private static bool I89_White(SKColor c) => c is { Red: > 245, Green: > 245, Blue: > 245 };

    private static string I89_Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    /// <summary>The content of the first page, decoded.</summary>
    private static string I89_Content(PdfDocument doc) => doc.Pages[0].Dictionary.Get(PdfName.Contents) switch
    {
        PdfStream s => Encoding.Latin1.GetString(doc.GetStreamData(s)),
        PdfArray parts => string.Concat(Enumerable.Range(0, parts.Count).Select(i => parts.Get(i)).OfType<PdfStream>().Select(s => Encoding.Latin1.GetString(doc.GetStreamData(s)))),
        _ => "",
    };

    /// <summary>The image XObjects in the resources of the first page.</summary>
    private static List<PdfStream> I89_Images(PdfDocument doc) =>
        doc.Pages[0].Resources?.GetDictionary(PdfName.Get("XObject")) is { } x
            ? [.. x.Keys.Select(k => x.Get(k)).OfType<PdfStream>().Where(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image")]
            : [];

    /// <summary>A JPEG (made by the codec of the display) of <paramref name="width"/> × <paramref name="height"/> pixels in one colour.</summary>
    private static byte[] I89_Jpeg(int width, int height, SKColor colour)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(colour);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 100);
        return encoded.ToArray();
    }

    /// <summary>LZW data (7.4.4) of <paramref name="data"/> as literal codes only: Clear, one 9-bit code per byte, EOD — few bytes, so the
    /// table never reaches the width of ten bits.</summary>
    private static byte[] I89_LzwLiterals(byte[] data)
    {
        var codes = new List<int> { 256 };
        codes.AddRange(data.Select(b => (int)b));
        codes.Add(257);
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var code in codes)
        {
            buffer = (buffer << 9) | code;
            bits += 9;
            while (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
                buffer &= (1 << bits) - 1;
            }
        }
        if (bits > 0) output.Add((byte)(buffer << (8 - bits)));
        return [.. output];
    }

    private static byte[] I89_U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static byte[] I89_Box(string type, params byte[][] parts)
    {
        var body = parts.SelectMany(p => p).ToArray();
        return [.. I89_U32((uint)(8 + body.Length)), .. Encoding.ASCII.GetBytes(type), .. body];
    }

    /// <summary>A CMYK profile with two tables: A2B0 (perceptual) from one formula, A2B1 (relative colorimetric) from the other (as built in
    /// <see cref="IccTests"/>).</summary>
    private static byte[] I89_TwoIntents(byte[] perceptual, byte[] relative)
    {
        var lutAt = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(perceptual.AsSpan(148));
        var lutSize = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(perceptual.AsSpan(152));
        var size = perceptual.Length + 12 + lutSize;
        var d = new byte[size];
        perceptual.AsSpan(0, 156).CopyTo(d);
        perceptual.AsSpan(156).CopyTo(d.AsSpan(168));
        void U32(int at, int v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(at), (uint)v);
        U32(0, size);
        U32(128, 3);
        U32(136, (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(perceptual.AsSpan(136)) + 12);
        U32(148, lutAt + 12);
        U32(156, 0x41324231); U32(160, perceptual.Length + 12); U32(164, lutSize);
        relative.AsSpan(lutAt, lutSize).CopyTo(d.AsSpan(perceptual.Length + 12));
        return d;
    }

    /// <summary>
    /// A colour space profile (v2, class „spac“) with data colour space Lab and PCS Lab whose A2B0 is an identity lut8 (grid 2): the input
    /// 0…1 is the PCS encoding — L 0…100, a and b −128…127.
    /// </summary>
    private static byte[] I89_LabProfile()
    {
        var body = new List<byte>();
        body.AddRange("mft1"u8.ToArray());
        body.AddRange(new byte[4]);
        body.AddRange([3, 3, 2, 0]);
        foreach (var v in new[] { 65536, 0, 0, 0, 65536, 0, 0, 0, 65536 }) body.AddRange(I89_U32((uint)v));
        for (var i = 0; i < 3; i++) for (var b = 0; b < 256; b++) body.Add((byte)b);
        for (var i = 0; i < 2; i++) for (var j = 0; j < 2; j++) for (var k = 0; k < 2; k++) body.AddRange([(byte)(i * 255), (byte)(j * 255), (byte)(k * 255)]);
        for (var i = 0; i < 3; i++) for (var b = 0; b < 256; b++) body.Add((byte)b);
        const int tagAt = 128 + 4 + 12;
        var header = new List<byte>();
        header.AddRange(I89_U32((uint)(tagAt + body.Count)));
        header.AddRange("none"u8.ToArray());
        header.AddRange([2, 0x10, 0, 0]);
        header.AddRange("spacLab Lab "u8.ToArray());
        header.AddRange(new byte[12]);
        header.AddRange("acspMSFT"u8.ToArray());
        header.AddRange(new byte[4]);
        header.AddRange("none"u8.ToArray());
        header.AddRange(new byte[4]);
        header.AddRange(new byte[12]);
        foreach (var v in new[] { 63190, 65536, 54061 }) header.AddRange(I89_U32((uint)v));
        header.AddRange("none"u8.ToArray());
        header.AddRange(new byte[44]);
        header.AddRange(I89_U32(1));
        header.AddRange("A2B0"u8.ToArray());
        header.AddRange(I89_U32(tagAt));
        header.AddRange(I89_U32((uint)body.Count));
        return [.. header, .. body];
    }

    // ---- 8.9.1, 8.9.3, 8.9.4

    /// <summary>
    /// 8.9.1 sentences 7–8: a component may have 2 bits (four values). A grey image of 4 × 1 samples at 2 bits, the byte 0b00011011, shows
    /// the four steps 0, 85, 170 and 255 from left to right (<c>PdfImageDecoder.ReadBits</c>).
    /// </summary>
    [Fact]
    public void Image_TwoBitsPerComponent_GiveFourSteps()
    {
        using var bitmap = I89_FullImage(I89_Image("/Width 4 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 2", "\x1B"));
        int[] expected = [0, 85, 170, 255];
        for (var i = 0; i < 4; i++)
            Assert.InRange(I89_At(bitmap, 25 + 50 * i, 100).Red, expected[i] - 2, expected[i] + 2);
    }

    /// <summary>
    /// 8.9.3 sentence 8: a 16-bit sample is read with its high byte first. A grey image of 2 × 1 at 16 bits with the bytes 80 00 and 00 FF
    /// shows 128 and 0 — read the other way round it would be 0 and 255.
    /// </summary>
    [Fact]
    public void Image_SixteenBits_HighByteFirst()
    {
        using var bitmap = I89_FullImage(I89_Image("/Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 16", "\x80\x00\x00\xFF"));
        Assert.InRange(I89_At(bitmap, 50, 100).Red, 126, 129);
        Assert.InRange(I89_At(bitmap, 150, 100).Red, 0, 2);
    }

    /// <summary>
    /// 8.9.3 sentence 15: without /ImageMask the samples are colours of the image's own space; the fill colour of the graphics state does not
    /// matter. A red and blue image drawn under „0 1 0 rg“ stays red and blue (<c>PageInterpreter.Image</c>).
    /// </summary>
    [Fact]
    public void Image_OwnColours_NotTheFillColour()
    {
        using var bitmap = I89_Render(I89_File("", "/XObject << /Im0 5 0 R >>", "0 1 0 rg q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image("/Width 2 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8", "\xff\x00\x00\x00\x00\xff")));
        Assert.True(I89_Red(I89_At(bitmap, 50, 100)));
        Assert.True(I89_Blue(I89_At(bitmap, 150, 100)));
    }

    /// <summary>
    /// 8.9.4 sentences 1–5, 7–8 and 11–14: image space has its origin at the top left, the first row of samples is the top one; the unit
    /// square of user space takes the image with (0, 0) at the bottom left of the image. A 1 × 2 image (first row red, second blue) drawn with
    /// „100 0 0 100 0 0 cm“: red at the top (y 75), blue at the bottom (y 25) (<c>SkiaRenderer.DrawImage</c>).
    /// </summary>
    [Fact]
    public void Image_FirstRowAtTheTop()
    {
        using var bitmap = I89_Render(I89_File("", "/XObject << /Im0 5 0 R >>", "q 100 0 0 100 0 0 cm /Im0 Do Q",
            I89_Image("/Width 1 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8", "\xff\x00\x00\x00\x00\xff")));
        Assert.True(I89_Red(I89_At(bitmap, 50, 75)));
        Assert.True(I89_Blue(I89_At(bitmap, 50, 25)));
    }

    // ---- 8.9.5.1 image dictionaries

    /// <summary>
    /// 8.9.5.1 sentence 3 and sentences 20, 29–31: /BitsPerComponent is 1, 2, 4, 8 or 16; an image with another depth (3, 12) and no filter is
    /// an error — the display draws the placeholder colour (E6 E8 EC) in place of the image, without an exception (<c>DecodeCore</c>,
    /// <c>DrawImage</c>).
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(12)]
    public void Image_BitsPerComponentNotAllowed_DrawsThePlaceholder(int bits)
    {
        using var bitmap = I89_FullImage(I89_Image($"/Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent {bits}", "\x00\x40\x80\xC0"));
        var c = I89_At(bitmap, 100, 100);
        Assert.True(Math.Abs(c.Red - 0xE6) <= 1 && Math.Abs(c.Green - 0xE8) <= 1 && Math.Abs(c.Blue - 0xEC) <= 1, c.ToString());
    }

    /// <summary>
    /// 8.9.5.1 sentences 7–8 and 9–11 (writer): images PDF-Knead writes carry /Type /XObject and /Subtype /Image — an inserted image
    /// (<c>ElementAppearance.ImageStream</c>) and inline images that become XObjects when saved (<c>InlineImageConformance</c>).
    /// </summary>
    [Fact]
    public void WrittenImages_CarryTypeAndSubtype()
    {
        var inserted = ElementAppearance.ImageStream(new Core.Imaging.RgbaImage(1, 1, [255, 0, 0, 255])).Dictionary;
        Assert.Equal("XObject", inserted.GetName(PdfName.Type)?.Value);
        Assert.Equal("Image", inserted.GetName(PdfName.Subtype)?.Value);

        var withEi = I89_Latin1([0x40, 0x20, 0x45, 0x49, 0x20, 0x80, 0xC0, 0xFF]);
        var content = $"q 80 0 0 10 20 150 cm BI /W 8 /H 1 /BPC 8 /CS /G ID {withEi} EI Q";
        using var saved = DocumentStructureNormTests.Saved(I89_File("", "", content));
        var images = I89_Images(saved);
        Assert.NotEmpty(images);
        Assert.All(images, s =>
        {
            Assert.Equal("XObject", s.Dictionary.GetName(PdfName.Type)?.Value);
            Assert.Equal("Image", s.Dictionary.GetName(PdfName.Subtype)?.Value);
        });
    }

    /// <summary>
    /// 8.9.5.1 sentences 9–11 (reader): /Subtype /Image is required; an image XObject without it, drawn with „Do“, draws nothing
    /// (<c>PageInterpreter.XObject</c>).
    /// </summary>
    [Fact]
    public void ImageXObject_WithoutSubtype_DrawsNothing()
    {
        const string data = "\xff\x00\x00";
        using var bitmap = I89_FullImage($"<< /Type /XObject /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>\nstream\n{data}\nendstream");
        Assert.True(I89_White(I89_At(bitmap, 100, 100)));
    }

    /// <summary>
    /// 8.9.5.1 sentence 32: with /ImageMask true, /BitsPerComponent is 1 if given. A stencil mask of 8 × 1 with /BitsPerComponent 8 and the
    /// single byte 0x0F is read at one bit: the left half paints in the fill colour, the right half not (<c>DecodeCore</c>).
    /// </summary>
    [Fact]
    public void StencilMask_BitsPerComponent8_ReadAsOneBit()
    {
        using var bitmap = I89_Render(I89_File("", "/XObject << /Im0 5 0 R >>", "1 0 0 rg q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image("/Width 8 /Height 1 /ImageMask true /BitsPerComponent 8", "\x0F")));
        Assert.True(I89_Red(I89_At(bitmap, 50, 100)));
        Assert.True(I89_White(I89_At(bitmap, 150, 100)));
    }

    /// <summary>
    /// 8.9.5.1 sentences 33–34 (reader): with a filter the depth is that of the filter's output — a DCT image is read with the 8 bits of the
    /// JPEG also where /BitsPerComponent says 1: a mid grey JPEG shows mid grey (<c>DecodeCore</c>, DCT branch).
    /// </summary>
    [Fact]
    public void Image_DctWithBitsPerComponent1_ShownWithItsEightBits()
    {
        var jpeg = I89_Latin1(I89_Jpeg(16, 16, new SKColor(128, 128, 128)));
        using var bitmap = I89_FullImage(I89_Image("/Width 16 /Height 16 /ColorSpace /DeviceRGB /BitsPerComponent 1 /Filter /DCTDecode", jpeg));
        var c = I89_At(bitmap, 100, 100);
        Assert.True(c.Red is > 118 and < 138 && c.Green is > 118 and < 138 && c.Blue is > 118 and < 138, c.ToString());
    }

    /// <summary>
    /// 8.9.5.1 sentences 35–36: with JPXDecode /BitsPerComponent is passed over, the depth comes from the data. An 8-bit JPEG 2000 grey image
    /// (200, 50) decodes the same with /BitsPerComponent 1 and without it (<c>DecodeCore</c>, JPX branch).
    /// </summary>
    [Fact]
    public void Image_Jpx_BitsPerComponentPassedOver()
    {
        var codestream = I89_Latin1(JpxTests.AnnexG.Lossless8(2, 1, [200, 50]));
        var file = DocumentStructureNormTests.FileOf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>",
            I89_Image("/Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JPXDecode", codestream),
            I89_Image("/Width 2 /Height 1 /ColorSpace /DeviceGray /Filter /JPXDecode", codestream));
        using var doc = PdfDocument.Open(file);
        byte[] Pixels(int number) => PdfImageDecoder.Decode(new ImageSource((PdfStream)doc.GetObject(number), null, null, false, 0), doc)!.Rgba;
        var with = Pixels(3);
        Assert.Equal(Pixels(4), with);
        Assert.Equal(200, with[0]);
        Assert.Equal(50, with[4]);
    }

    /// <summary>
    /// 8.9.5.1 sentence 39: with /ImageMask true the /Intent of the image is passed over. A stencil mask with /Intent /Perceptual, painted in
    /// an ICC space whose perceptual table swaps cyan and magenta, under „/RelativeColorimetric ri“: the fill colour magenta stays magenta
    /// (<c>PageInterpreter.Image</c>, stencil branch).
    /// </summary>
    [Fact]
    public void StencilMask_IntentPassedOver()
    {
        var swapped = Core.Pdf.Graphics.Icc.CmykProfile.Bake((c, m, y, k) => ((1 - m) * (1 - k), (1 - c) * (1 - k), (1 - y) * (1 - k)));
        var plain = Core.Pdf.Graphics.Icc.CmykProfile.Bake((c, m, y, k) => ((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k)));
        var profile = I89_Latin1(I89_TwoIntents(swapped, plain));
        using var bitmap = I89_Render(I89_File("", "/ColorSpace << /C0 [/ICCBased 6 0 R] >> /XObject << /Im0 5 0 R >>",
                "/RelativeColorimetric ri /C0 cs 0 1 0 0 sc q 200 0 0 200 0 0 cm /Im0 Do Q",
                I89_Image("/Width 1 /Height 1 /ImageMask true /Intent /Perceptual", "\x00"),
                $"<< /N 4 /Length {profile.Length} >>\nstream\n{profile}\nendstream"));
        var c = I89_At(bitmap, 100, 100);
        Assert.True(c is { Red: > 230, Green: < 25, Blue: > 230 }, c.ToString());
    }

    /// <summary>
    /// 8.9.5.1 sentences 25–26 and 44–46: a stencil mask has no /Mask; one that has it (as a stream or as an array) paints as without it.
    /// A stencil of 2 × 1 that paints both samples, with a /Mask that would cover both: the whole page red (<c>DecodeCore</c>).
    /// </summary>
    [Theory]
    [InlineData("/Mask [0 0]")]
    [InlineData("/Mask 6 0 R")]
    public void StencilMask_MaskEntryPassedOver(string mask)
    {
        using var bitmap = I89_Render(I89_File("", "/XObject << /Im0 5 0 R >>", "1 0 0 rg q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image($"/Width 2 /Height 1 /ImageMask true {mask}", "\x00"),
            I89_Image("/Width 2 /Height 1 /ImageMask true", "\xC0")));
        Assert.True(I89_Red(I89_At(bitmap, 50, 100)));
        Assert.True(I89_Red(I89_At(bitmap, 150, 100)));
    }

    /// <summary>
    /// 8.9.5.1 sentences 27–28 and 51–52, 8.9.5.3 sentences 1–3: /Interpolate true asks for smoothing; the display follows the hint when it
    /// enlarges. A 2 × 1 image (black, white) over 200 units: with /Interpolate between the sample centres a ramp (dark grey at x 75, light
    /// grey at x 125), without a hard edge in the middle (<c>SkiaRenderer.DrawImage</c>).
    /// </summary>
    [Fact]
    public void Image_Interpolate_SmoothsWhenEnlarged()
    {
        using (var smooth = I89_FullImage(I89_Image("/Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Interpolate true", "\x00\xff")))
        {
            Assert.InRange(I89_At(smooth, 75, 100).Red, 25, 105);
            Assert.InRange(I89_At(smooth, 125, 100).Red, 150, 230);
        }
        using var sharp = I89_FullImage(I89_Image("/Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", "\x00\xff"));
        Assert.InRange(I89_At(sharp, 75, 100).Red, 0, 5);
        Assert.InRange(I89_At(sharp, 125, 100).Red, 250, 255);
    }

    /// <summary>
    /// 8.9.5.1 sentences 53–55: an alternate image has no /Alternates of its own; where it has, they are not followed. Printed, an image marked
    /// alternate (blue) whose own /Alternates name a green image marked for printing: blue is printed (<c>PageInterpreter.Alternate</c>).
    /// </summary>
    [Fact]
    public void Image_AlternatesOfAnAlternate_NotFollowed()
    {
        const string rgb = "/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8";
        using var bitmap = I89_Render(I89_File("", "/XObject << /Im0 5 0 R >>", "q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image($"{rgb} /Alternates [<< /Image 6 0 R /DefaultForPrinting true >>]", "\xff\x00\x00"),
            I89_Image($"{rgb} /Alternates [<< /Image 7 0 R /DefaultForPrinting true >>]", "\x00\x00\xff"),
            I89_Image(rgb, "\x00\xff\x00")), forPrint: true);
        Assert.True(I89_Blue(I89_At(bitmap, 100, 100)));
    }

    /// <summary>
    /// 8.9.5.1 sentence 60 (→ M8, shape and opacity, 11.6.4), today's behaviour: /AIS of the graphics state is not read — an image with a soft
    /// mask (value 128) looks the same under „/AIS true“ (shape) and without it (opacity): half red over the white page.
    /// </summary>
    [Fact]
    public void Image_SoftMask_AlphaIsShapeNotRead_TodaysBehaviour()
    {
        using var bitmap = I89_Render(I89_File("", "/ExtGState << /S1 << /AIS true >> >> /XObject << /Im0 5 0 R >>",
            "q /S1 gs q 100 0 0 200 0 0 cm /Im0 Do Q Q q 100 0 0 200 100 0 cm /Im0 Do Q",
            I89_Image("/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 6 0 R", "\xff\x00\x00"),
            I89_Image("/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", "\x80")));
        var (shape, opacity) = (I89_At(bitmap, 50, 100), I89_At(bitmap, 150, 100));
        Assert.Equal(opacity, shape);
        Assert.True(opacity is { Red: > 245 } && Math.Abs(opacity.Green - 127) <= 8 && Math.Abs(opacity.Blue - 127) <= 8, opacity.ToString());
    }

    /// <summary>
    /// 8.9.5.1 sentences 62–63: with a soft mask of the image the constant opacity still applies — an opaque /SMask under „/ca 0.5“ gives half
    /// red (left); an image without a mask of its own stays under the soft mask of the graphics state — one that lets nothing through hides it
    /// (right) (<c>PageInterpreter.Image</c>, <c>ownMask</c>).
    /// </summary>
    [Fact]
    public void Image_ConstantOpacityAndStateMask_StillApply()
    {
        const string image = "/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8";
        const string blocking = "0 g 0 0 200 200 re f";
        using var bitmap = I89_Render(I89_File("",
            "/ExtGState << /H << /ca 0.5 >> /SM << /SMask << /Type /Mask /S /Luminosity /G 8 0 R >> >> >> /XObject << /Im0 5 0 R /Im1 7 0 R >>",
            "q /H gs q 100 0 0 200 0 0 cm /Im0 Do Q Q q /SM gs q 100 0 0 200 100 0 cm /Im1 Do Q Q",
            I89_Image($"{image} /SMask 6 0 R", "\xff\x00\x00"),
            I89_Image("/Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", "\xff"),
            I89_Image(image, "\xff\x00\x00"),
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /I true >> /Length {blocking.Length} >>\nstream\n{blocking}\nendstream"));
        var half = I89_At(bitmap, 50, 100);
        Assert.True(half is { Red: > 245 } && Math.Abs(half.Green - 128) <= 8 && Math.Abs(half.Blue - 128) <= 8, half.ToString());
        Assert.True(I89_White(I89_At(bitmap, 150, 100)));
    }

    /// <summary>
    /// 8.9.5.1 sentence 69: /SMaskInData 2 — the colour channels of a JPEG 2000 image are premultiplied with its opacity channel; the colour is
    /// divided back (value × 255 / α) and the opacity taken from the channel: 64, 32, 100 at α 128 become 127, 63, 199 (<c>ApplyAlpha</c>).
    /// </summary>
    [Fact]
    public void Jpx_SMaskInData2_PremultipliedColourDividedBack()
    {
        var cs = JpxTests.AnnexG.Lossless8(3, 1, [64, 255, 0], [32, 100, 0], [100, 50, 0], [128, 255, 0]);
        byte[] definition = [0, 4, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 2, 0, 2, 0, 0, 0, 3, 0, 3, 0, 2, 0, 0];
        var jpx = JpxColourTests.Jp2HeaderBoxes.File(cs, [I89_Box("colr", [1, 0, 0], I89_U32(16)), I89_Box("cdef", definition)]);
        var pdf = new TestPdf();
        var number = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(3)),
            ("Height", PdfInteger.Of(1)), ("Filter", TestPdf.N("JPXDecode")), ("SMaskInData", PdfInteger.Of(2))), jpx));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Pages")),
            ("Kids", new PdfArray()), ("Count", PdfInteger.Of(0))))))));
        using var doc = PdfDocument.Open(pdf.Build());
        var decoded = PdfImageDecoder.Decode(new ImageSource((PdfStream)doc.GetObject(number), null, null, false, 0), doc)!;
        Assert.Equal(ImagePixels.Rgba, decoded.Format);
        Assert.Equal(new byte[] { 127, 63, 199, 128, 255, 100, 50, 255, 0, 0, 0, 0 }, decoded.Pixels[..12]);
    }

    /// <summary>
    /// 8.9.5.1 sentences 74–75: /Name of an image XObject is deprecated in PDF 2.0 — saved as 2.0 the image goes without it, saved as 1.7
    /// with it (<c>VersionKeys.Filter</c>).
    /// </summary>
    [Fact]
    public void ImageXObject_Name_GoesAs20_StaysAs17()
    {
        var file = I89_File("", "/XObject << /Im0 5 0 R >>", "q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image("/Name /Im0 /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8", "\xff\x00\x00"));
        using (var as20 = DocumentStructureNormTests.Saved(file, new PdfSaveOptions { Version = "2.0" }))
            Assert.Null(Assert.Single(I89_Images(as20)).Dictionary.GetRaw(PdfName.Get("Name")));
        using var as17 = DocumentStructureNormTests.Saved(file, new PdfSaveOptions { Version = "1.7" });
        Assert.Equal("Im0", Assert.Single(I89_Images(as17)).Dictionary.GetName(PdfName.Get("Name"))?.Value);
    }

    /// <summary>
    /// 8.9.5.1 sentences 76–78 (→ M12, 14.7.5.4), today's behaviour: an image that is structured content carries /StructParent, its key in the
    /// parent tree — after saving the key still finds the Figure element of the image.
    /// </summary>
    [Fact]
    public void Image_StructParent_FindsItsElementAfterSaving_TodaysBehaviour()
    {
        var file = I89_File("/StructTreeRoot 6 0 R /MarkInfo << /Marked true >>", "/XObject << /Im0 5 0 R >>", "q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image("/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /StructParent 0", "\xff\x00\x00"),
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree << /Nums [0 7 0 R] >> /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Figure /P 6 0 R /Pg 3 0 R /Alt (Rotes Quadrat) /K << /Type /OBJR /Obj 5 0 R /Pg 3 0 R >> >>");
        using var saved = DocumentStructureNormTests.Saved(file);
        var image = Assert.Single(I89_Images(saved));
        var key = NumberTree.KeyOf(image.Dictionary.Get(PdfName.Get("StructParent")));
        Assert.NotNull(key);
        var tree = saved.Catalog!.GetDictionary(PdfName.Get("StructTreeRoot"))!.GetDictionary(PdfName.Get("ParentTree"))!;
        var element = NumberTree.Value(tree, key!.Value)?.AsDictionary();
        Assert.Equal("Figure", element?.GetName(PdfName.Get("S"))?.Value);
    }

    /// <summary>
    /// 8.9.5.1 sentences 79–81 (→ M11, Web Capture 14.10), today's behaviour: the /ID of an image (the identifier of its Web Capture content
    /// set) stays unchanged when saved.
    /// </summary>
    [Fact]
    public void Image_WebCaptureId_StaysOnSaving_TodaysBehaviour()
    {
        using var saved = DocumentStructureNormTests.Saved(I89_File("", "/XObject << /Im0 5 0 R >>", "q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image("/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /ID <0123456789ABCDEF0123456789ABCDEF>", "\xff\x00\x00")));
        var id = Assert.Single(I89_Images(saved)).Dictionary.GetString(PdfName.ID);
        Assert.Equal(Convert.FromHexString("0123456789ABCDEF0123456789ABCDEF"), id?.Bytes);
    }

    /// <summary>
    /// 8.9.5.1 sentences 86–88 (→ M11, metadata 14.3.2), today's behaviour: the /Metadata of an image stays when saved; cleaning up the
    /// metadata removes it.
    /// </summary>
    [Fact]
    public void Image_Metadata_StaysOnSaving_CleanupRemovesIt_TodaysBehaviour()
    {
        const string xmp = "<x:xmpmeta>Bild-Beschreibung</x:xmpmeta>";
        var file = I89_File("", "/XObject << /Im0 5 0 R >>", "q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image("/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Metadata 6 0 R", "\xff\x00\x00"),
            $"<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n{xmp}\nendstream");
        using (var saved = DocumentStructureNormTests.Saved(file))
        {
            var metadata = Assert.Single(I89_Images(saved)).Dictionary.Get(PdfName.Metadata) as PdfStream;
            Assert.NotNull(metadata);
            Assert.Contains("Bild-Beschreibung", Encoding.Latin1.GetString(saved.GetStreamData(metadata)));
        }
        using var cleaned = DocumentStructureNormTests.Saved(file, new PdfSaveOptions { Cleanup = CleanupItems.Metadata });
        Assert.Null(Assert.Single(I89_Images(cleaned)).Dictionary.GetRaw(PdfName.Metadata));
    }

    /// <summary>
    /// 8.9.5.1 sentences 90–94: before an image with /OC is drawn its visibility is decided; hidden, it is passed over as if there were no
    /// „Do“. An image on a layer that is off, without /Alternates: the page stays white (<c>PageInterpreter.XObject</c>).
    /// </summary>
    [Fact]
    public void Image_HiddenByItsLayer_WithoutAlternates_DrawsNothing()
    {
        using var bitmap = I89_Render(I89_File("/OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >>", "/XObject << /Im0 5 0 R >>",
            "q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image("/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /OC 6 0 R", "\xff\x00\x00"),
            "<< /Type /OCG /Name (Ebene) >>"));
        Assert.True(I89_White(I89_At(bitmap, 100, 100)));
    }

    /// <summary>
    /// 8.9.5.1 sentences 95–98 (→ M11, associated files 14.13), today's behaviour: the /AF of an image stays when saved as PDF 2.0.
    /// </summary>
    [Fact]
    public void Image_AssociatedFiles_StayWhenSavedAs20_TodaysBehaviour()
    {
        const string data = "a;b\n1;2\n";
        using var saved = DocumentStructureNormTests.Saved(I89_File("", "/XObject << /Im0 5 0 R >>", "q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image("/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /AF [6 0 R]", "\xff\x00\x00"),
            "<< /Type /Filespec /F (daten.csv) /UF (daten.csv) /AFRelationship /Data /EF << /F 7 0 R >> >>",
            $"<< /Type /EmbeddedFile /Subtype /text#2Fcsv /Length {data.Length} >>\nstream\n{data}\nendstream"), new PdfSaveOptions { Version = "2.0" });
        var af = Assert.Single(I89_Images(saved)).Dictionary.GetArray(PdfName.Get("AF"));
        Assert.NotNull(af);
        Assert.Equal("Data", af!.Get(0)?.AsDictionary()?.GetName(PdfName.Get("AFRelationship"))?.Value);
    }

    /// <summary>
    /// 8.9.5.1 sentences 99–104 (→ M10, measuring and geospatial data 12.9), today's behaviour: /Measure and /PtData of an image stay when saved.
    /// </summary>
    [Fact]
    public void Image_MeasureAndPointData_StayOnSaving_TodaysBehaviour()
    {
        using var saved = DocumentStructureNormTests.Saved(I89_File("", "/XObject << /Im0 5 0 R >>", "q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image("/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 " +
                      "/Measure << /Type /Measure /Subtype /RL /R (1 in = 1 ft) /X [<< /U (ft) /C 0.0138 >>] /D [<< /U (ft) /C 1 >>] /A [<< /U (sq ft) /C 1 >>] >> " +
                      "/PtData << /Type /PtData /Subtype /Cloud /Names [/LAT /LON /ALT] /XPTS [[48.1 11.5 520]] >>", "\xff\x00\x00")), new PdfSaveOptions { Version = "2.0" });
        var dict = Assert.Single(I89_Images(saved)).Dictionary;
        Assert.Equal("RL", dict.GetDictionary(PdfName.Get("Measure"))?.GetName(PdfName.Subtype)?.Value);
        Assert.Equal("Cloud", dict.GetDictionary(PdfName.Get("PtData"))?.GetName(PdfName.Subtype)?.Value);
    }

    // ---- 8.9.5.2 decode arrays (Table 88)

    /// <summary>
    /// 8.9.5.2 sentences 22–26 (Table 88): DeviceCMYK decodes by default with [0 1] four times — a CMYK image without /Decode and 255 in cyan
    /// only shows full cyan (<c>ColorSpace.DefaultDecode</c>).
    /// </summary>
    [Fact]
    public void Image_DefaultDecode_DeviceCmyk_FullCyan()
    {
        using var bitmap = I89_FullImage(I89_Image("/Width 1 /Height 1 /ColorSpace /DeviceCMYK /BitsPerComponent 8", "\xff\x00\x00\x00"));
        var c = I89_At(bitmap, 100, 100);
        Assert.True(c is { Red: < 60, Green: > 140, Blue: > 200 }, c.ToString());
    }

    /// <summary>
    /// 8.9.5.2 sentences 27–30 (Table 88): CalGray decodes by default with [0 1] — samples 0 and 255 without /Decode are black and white.
    /// </summary>
    [Fact]
    public void Image_DefaultDecode_CalGray_BlackAndWhite()
    {
        using var bitmap = I89_FullImage(I89_Image("/Width 2 /Height 1 /ColorSpace [/CalGray << /WhitePoint [0.9505 1 1.089] >>] /BitsPerComponent 8", "\x00\xff"));
        Assert.InRange(I89_At(bitmap, 50, 100).Red, 0, 10);
        Assert.True(I89_White(I89_At(bitmap, 150, 100)));
    }

    /// <summary>
    /// 8.9.5.2 sentences 31–33 (Table 88): Lab decodes by default with [0 100 amin amax bmin bmax] from the /Range of the space — with /Range
    /// [−100 100 −100 100] the samples (255, 128, 128) are nearly white, (0, 128, 128) black (<c>ColorSpace.LabSpace.DefaultDecode</c>).
    /// </summary>
    [Fact]
    public void Image_DefaultDecode_Lab_FromItsRange()
    {
        using var bitmap = I89_FullImage(I89_Image("/Width 2 /Height 1 /ColorSpace [/Lab << /WhitePoint [0.9505 1 1.089] /Range [-100 100 -100 100] >>] /BitsPerComponent 8",
            "\xff\x80\x80\x00\x80\x80"));
        var light = I89_At(bitmap, 50, 100);
        Assert.True(light is { Red: > 235, Green: > 235, Blue: > 235 }, light.ToString());
        var dark = I89_At(bitmap, 150, 100);
        Assert.True(dark is { Red: < 20, Green: < 20, Blue: < 20 }, dark.ToString());
    }

    /// <summary>
    /// 8.9.5.2 sentences 34–35 (Table 88): an ICCBased image decodes by default with the /Range of its space. A Lab profile with /Range
    /// [0 100 −128 127 −128 127]: the image without /Range and with it look alike; with /Decode given in the units of that /Range (the same
    /// numbers) it must look like the image without /Decode — L 50, a −128, b 127 for the samples (128, 0, 255).
    /// </summary>
    [Fact]
    public void Image_IccBasedRange_DecodeInItsUnits()
    {
        var profile = I89_Latin1(I89_LabProfile());
        const string image = "/Width 1 /Height 1 /BitsPerComponent 8";
        const string range = "/Range [0 100 -128 127 -128 127]";
        var content = "q 50 0 0 200 0 0 cm /Im0 Do Q q 50 0 0 200 50 0 cm /Im1 Do Q q 50 0 0 200 100 0 cm /Im2 Do Q";
        using var bitmap = I89_Render(I89_File("", "/XObject << /Im0 5 0 R /Im1 6 0 R /Im2 7 0 R >>", content,
            I89_Image($"{image} /ColorSpace [/ICCBased 8 0 R]", "\x80\x00\xff"),
            I89_Image($"{image} /ColorSpace [/ICCBased 9 0 R]", "\x80\x00\xff"),
            I89_Image($"{image} /ColorSpace [/ICCBased 9 0 R] /Decode [0 100 -128 127 -128 127]", "\x80\x00\xff"),
            $"<< /N 3 /Length {profile.Length} >>\nstream\n{profile}\nendstream",
            $"<< /N 3 {range} /Length {profile.Length} >>\nstream\n{profile}\nendstream"));
        var (without, with, decoded) = (I89_At(bitmap, 25, 100), I89_At(bitmap, 75, 100), I89_At(bitmap, 125, 100));
        Assert.Equal(without, with);
        Assert.True(Math.Abs(decoded.Red - with.Red) <= 3 && Math.Abs(decoded.Green - with.Green) <= 3 && Math.Abs(decoded.Blue - with.Blue) <= 3,
            $"ohne /Decode {with}, mit /Decode {decoded}");
    }

    /// <summary>
    /// 8.9.5.2 sentences 40–43 (Table 88): Separation decodes by default with [0 1], DeviceN with one pair [0 1] per component — a Separation
    /// image with 255 shows the full colour of its tint transform (red, left), a DeviceN image of two components with (255, 255) as well
    /// (right) (<c>ColorSpace.DefaultDecode</c>).
    /// </summary>
    [Fact]
    public void Image_DefaultDecode_SeparationAndDeviceN_FullColour()
    {
        const string ps = "{ 1 exch sub exch 1 exch sub exch 1 3 1 roll }";
        using var bitmap = I89_Render(I89_File("", "/XObject << /Im0 5 0 R /Im1 6 0 R >>",
            "q 100 0 0 200 0 0 cm /Im0 Do Q q 100 0 0 200 100 0 cm /Im1 Do Q",
            I89_Image("/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace [/Separation /Schmuckrot /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 1 1] /C1 [1 0 0] /N 1 >>]", "\xff"),
            I89_Image("/Width 1 /Height 1 /BitsPerComponent 8 /ColorSpace [/DeviceN [/Erste /Zweite] /DeviceRGB 7 0 R]", "\xff\xff"),
            $"<< /FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1] /Length {ps.Length} >>\nstream\n{ps}\nendstream"));
        Assert.True(I89_Red(I89_At(bitmap, 50, 100)), I89_At(bitmap, 50, 100).ToString());
        Assert.True(I89_Red(I89_At(bitmap, 150, 100)), I89_At(bitmap, 150, 100).ToString());
    }

    /// <summary>
    /// 8.9.5.2 sentences 46 and 49: Dmin and Dmax may lie outside the range of the component; a result outside it is set to the nearest value
    /// allowed. Grey with /Decode [0 2]: 128 and 255 are both white (left); an indexed image of three entries with /Decode [0 512]: the sample
    /// 2 gives index 4, the last entry (blue, right).
    /// </summary>
    [Fact]
    public void Image_DecodeBeyondTheRange_TakesTheNearestValue()
    {
        using var bitmap = I89_Render(I89_File("", "/XObject << /Im0 5 0 R /Im1 6 0 R >>",
            "q 100 0 0 200 0 0 cm /Im0 Do Q q 100 0 0 200 100 0 cm /Im1 Do Q",
            I89_Image("/Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [0 2]", "\x80\xff"),
            I89_Image("/Width 1 /Height 1 /ColorSpace [/Indexed /DeviceRGB 2 <FF000000FF000000FF>] /BitsPerComponent 8 /Decode [0 512]", "\x02")));
        Assert.True(I89_White(I89_At(bitmap, 25, 100)));
        Assert.True(I89_White(I89_At(bitmap, 75, 100)));
        Assert.True(I89_Blue(I89_At(bitmap, 150, 100)), I89_At(bitmap, 150, 100).ToString());
    }

    // ---- 8.9.5.4 alternate images

    private const string I89_Rgb = "/Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8";

    /// <summary>
    /// 8.9.5.4 sentences 15–21: an entry of /Alternates may have /OC to choose the version. The base image is hidden; of two alternates the
    /// first (green) is on a layer that is off, the second (blue) has none: blue is shown (<c>PageInterpreter.Alternate</c>).
    /// </summary>
    [Fact]
    public void Image_Alternates_FirstVisibleChosen()
    {
        using var bitmap = I89_Render(I89_File("/OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >>", "/XObject << /Im0 5 0 R >>",
            "q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image($"{I89_Rgb} /OC 6 0 R /Alternates [<< /Image 7 0 R /OC 6 0 R >> << /Image 8 0 R >>]", "\xff\x00\x00"),
            "<< /Type /OCG /Name (Aus) >>",
            I89_Image(I89_Rgb, "\x00\xff\x00"),
            I89_Image(I89_Rgb, "\x00\x00\xff")));
        Assert.True(I89_Blue(I89_At(bitmap, 100, 100)));
    }

    /// <summary>
    /// 8.9.5.4 sentences 22 and 24–25: a base image with /OC — /DefaultForPrinting counts nowhere; visible, the base image itself is shown, also
    /// when printed. Base image red on a layer that is on, an alternate blue marked for printing: printed red (<c>PageInterpreter.XObject</c>).
    /// </summary>
    [Fact]
    public void Image_Alternates_BaseImageWithOc_PrintedItself()
    {
        using var bitmap = I89_Render(I89_File("/OCProperties << /OCGs [6 0 R] /D << >> >>", "/XObject << /Im0 5 0 R >>",
            "q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image($"{I89_Rgb} /OC 6 0 R /Alternates [<< /Image 7 0 R /DefaultForPrinting true >>]", "\xff\x00\x00"),
            "<< /Type /OCG /Name (An) >>",
            I89_Image(I89_Rgb, "\x00\x00\xff")), forPrint: true);
        Assert.True(I89_Red(I89_At(bitmap, 100, 100)));
    }

    /// <summary>
    /// 8.9.5.4 sentences 26–29: a hidden base image whose alternates are all on layers that are off shows nothing (<c>PageInterpreter.Alternate</c>).
    /// </summary>
    [Fact]
    public void Image_Alternates_AllHidden_NothingDrawn()
    {
        using var bitmap = I89_Render(I89_File("/OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >>", "/XObject << /Im0 5 0 R >>",
            "q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image($"{I89_Rgb} /OC 6 0 R /Alternates [<< /Image 7 0 R /OC 6 0 R >> << /Image 7 0 R /OC 6 0 R >>]", "\xff\x00\x00"),
            "<< /Type /OCG /Name (Aus) >>",
            I89_Image(I89_Rgb, "\x00\x00\xff")));
        Assert.True(I89_White(I89_At(bitmap, 100, 100)));
    }

    /// <summary>
    /// 8.9.5.4 sentences 30–33: printed, a base image without /OC takes the first alternate marked /DefaultForPrinting, whose /OC decides —
    /// marked but on a layer that is off: nothing is printed (left); none marked: the base image is printed (right).
    /// </summary>
    [Fact]
    public void Image_Alternates_PrintedMarkedHiddenOrNoneMarked()
    {
        using var bitmap = I89_Render(I89_File("/OCProperties << /OCGs [7 0 R] /D << /OFF [7 0 R] >> >>", "/XObject << /Im0 5 0 R /Im1 6 0 R >>",
            "q 100 0 0 200 0 0 cm /Im0 Do Q q 100 0 0 200 100 0 cm /Im1 Do Q",
            I89_Image($"{I89_Rgb} /Alternates [<< /Image 8 0 R /DefaultForPrinting true /OC 7 0 R >>]", "\xff\x00\x00"),
            I89_Image($"{I89_Rgb} /Alternates [<< /Image 8 0 R >>]", "\xff\x00\x00"),
            "<< /Type /OCG /Name (Aus) >>",
            I89_Image(I89_Rgb, "\x00\x00\xff")), forPrint: true);
        Assert.True(I89_White(I89_At(bitmap, 50, 100)));
        Assert.True(I89_Red(I89_At(bitmap, 150, 100)));
    }

    // ---- 8.9.6 masked images

    /// <summary>
    /// 8.9.6.2 sentences 9–11: /Decode [1 0] turns a stencil mask round — the sample 1 paints, 0 not. A stencil of 2 × 1 without filter, bits
    /// 0 1: left white, right red (<c>PdfImageDecoder.Stencil</c>).
    /// </summary>
    [Fact]
    public void StencilMask_DecodeOneZero_SampleOnePaints()
    {
        using var bitmap = I89_Render(I89_File("", "/XObject << /Im0 5 0 R >>", "1 0 0 rg q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image("/Width 2 /Height 1 /ImageMask true /Decode [1 0]", "\x40")));
        Assert.True(I89_White(I89_At(bitmap, 50, 100)));
        Assert.True(I89_Red(I89_At(bitmap, 150, 100)));
    }

    /// <summary>
    /// 8.9.6.2 sentences 15–16: smoothing a stencil mask smooths only its edge, not the colour painted. A 2 × 1 stencil (left paints) with
    /// /Interpolate true over 200 units in red: in the middle partial coverage in red over white (red stays full, green equals blue, between
    /// the ends) — no mixed colour (<c>SkiaRenderer.DrawImage</c>).
    /// </summary>
    [Fact]
    public void StencilMask_Interpolate_SmoothsOnlyTheEdge()
    {
        using var bitmap = I89_Render(I89_File("", "/XObject << /Im0 5 0 R >>", "1 0 0 rg q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image("/Width 2 /Height 1 /ImageMask true /Interpolate true", "\x40")));
        var middle = I89_At(bitmap, 100, 100);
        Assert.True(middle.Red >= 250 && middle.Green is > 30 and < 225 && Math.Abs(middle.Green - middle.Blue) <= 2, middle.ToString());
        Assert.True(I89_Red(I89_At(bitmap, 20, 100)));
        Assert.True(I89_White(I89_At(bitmap, 180, 100)));
    }

    /// <summary>
    /// 8.9.6.3 sentences 4–5: an explicit mask (/Mask as an image mask) says where the image is painted. Image 2 × 1 red, blue with a mask of
    /// the bits 0 1: left red, right nothing (left image); with /Decode [1 0] at the mask the other way round (right image)
    /// (<c>PdfImageDecoder.MaskAlpha</c>).
    /// </summary>
    [Fact]
    public void Image_ExplicitMask_SampleOneMaskedOut()
    {
        const string image = "/Width 2 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8";
        using var bitmap = I89_Render(I89_File("", "/XObject << /Im0 5 0 R /Im1 6 0 R >>",
            "q 100 0 0 200 0 0 cm /Im0 Do Q q 100 0 0 200 100 0 cm /Im1 Do Q",
            I89_Image($"{image} /Mask 7 0 R", "\xff\x00\x00\x00\x00\xff"),
            I89_Image($"{image} /Mask 8 0 R", "\xff\x00\x00\x00\x00\xff"),
            I89_Image("/Width 2 /Height 1 /ImageMask true", "\x40"),
            I89_Image("/Width 2 /Height 1 /ImageMask true /Decode [1 0]", "\x40")));
        Assert.True(I89_Red(I89_At(bitmap, 25, 100)));
        Assert.True(I89_White(I89_At(bitmap, 75, 100)));
        Assert.True(I89_White(I89_At(bitmap, 125, 100)));
        Assert.True(I89_Blue(I89_At(bitmap, 175, 100)));
    }

    /// <summary>
    /// 8.9.6.4 sentences 1–2, today's behaviour with JPXDecode: a colour key (/Mask as an array) has no effect on a JPEG 2000 image — of a
    /// grey 2 × 1 image (0, 255) with /Mask [0 0] the black sample is still painted (<c>DecodeCore</c>, JPX branch without colour key).
    /// </summary>
    [Fact]
    public void Jpx_ColourKey_WithoutEffect_TodaysBehaviour()
    {
        var codestream = I89_Latin1(JpxTests.AnnexG.Lossless8(2, 1, [0, 255]));
        using var bitmap = I89_FullImage(I89_Image("/Width 2 /Height 1 /ColorSpace /DeviceGray /Filter /JPXDecode /Mask [0 0]", codestream));
        Assert.InRange(I89_At(bitmap, 50, 100).Red, 0, 10);
    }

    /// <summary>
    /// 8.9.6.4 sentences 4–6: the ranges of a colour key apply to the raw samples, before /Decode. Over a red area a grey image (0, 255) with
    /// /Decode [1 0] and /Mask [0 0]: the raw 0 is masked (red shows through), the raw 255 — drawn black after /Decode — is painted
    /// (<c>PdfImageDecoder.Convert</c>).
    /// </summary>
    [Fact]
    public void Image_ColourKey_ComparesTheRawSamples()
    {
        using var bitmap = I89_Render(I89_File("", "/XObject << /Im0 5 0 R >>", "1 0 0 rg 0 0 200 200 re f q 200 0 0 200 0 0 cm /Im0 Do Q",
            I89_Image("/Width 2 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [1 0] /Mask [0 0]", "\x00\xff")));
        Assert.True(I89_Red(I89_At(bitmap, 50, 100)), I89_At(bitmap, 50, 100).ToString());
        var right = I89_At(bitmap, 150, 100);
        Assert.True(right is { Red: < 10, Green: < 10, Blue: < 10 }, right.ToString());
    }

    // ---- 8.9.7 inline images

    /// <summary>
    /// 8.9.7 sentences 3 and 25, today's behaviour (design decision pending): an inline image of more than 4 096 bytes stays inline when the page
    /// is saved (<c>InlineImageConformance.Breaks</c> has no size limit).
    /// </summary>
    [Fact]
    public void InlineImage_Over4096Bytes_StaysInline_TodaysBehaviour()
    {
        var data = new string('\x80', 5000);
        using var saved = DocumentStructureNormTests.Saved(I89_File("", "", $"q 200 0 0 100 0 50 cm BI /W 100 /H 50 /CS /G /BPC 8 ID {data} EI Q"));
        Assert.Contains("BI", I89_Content(saved));
        Assert.Empty(I89_Images(saved));
    }

    /// <summary>
    /// 8.9.7 sentences 18–19: inline images are not nested — a second BI before ID is no new image; ID belongs only between BI and its EI.
    /// „BI /W 1 /H 1 BI /BPC 8 /CS /G ID … EI Q“: one image (black, the upper square), then Q is read — the rectangle after it lies in the
    /// page's own space (red at the bottom left).
    /// </summary>
    [Fact]
    public void InlineImage_SecondBiBeforeId_OneImageThenTheRest()
    {
        using var bitmap = I89_Render(I89_File("", "", "1 0 0 rg q 100 0 0 100 50 50 cm BI /W 1 /H 1 BI /BPC 8 /CS /G ID \x00 EI Q 0 0 40 40 re f"));
        var image = I89_At(bitmap, 100, 100);
        Assert.True(image is { Red: < 10, Green: < 10, Blue: < 10 }, image.ToString());
        Assert.True(I89_Red(I89_At(bitmap, 20, 20)));
    }

    /// <summary>
    /// 8.9.7 sentence 23: other entries of an inline image are passed over — /Mask is none of its entries (Table 91). A grey inline image of
    /// 2 × 1 (0, 255) with /Mask [0 0]: both samples are painted, the left one black.
    /// </summary>
    [Fact(Skip = "Design decision pending (read tolerance): /Mask of an inline image applies today")]
    public void InlineImage_ColourKey_PassedOver()
    {
        using var bitmap = I89_Render(I89_File("", "", "q 200 0 0 200 0 0 cm BI /W 2 /H 1 /CS /G /BPC 8 /Mask [0 0] ID \x00\xff EI Q"));
        Assert.InRange(I89_At(bitmap, 50, 100).Red, 0, 10);
    }

    /// <summary>
    /// 8.9.7 sentence 24, today's behaviour (design decision pending): saved as PDF 2.0 without PDF/A an inline image without /L stays without it
    /// (<c>ContentConformance.WriteInlineImage</c> adds it only for PDF/A-4).
    /// </summary>
    [Fact]
    public void InlineImage_SavedAs20_NoLengthAdded_TodaysBehaviour()
    {
        using var saved = DocumentStructureNormTests.Saved(I89_File("", "", "q 200 0 0 200 0 0 cm BI /W 1 /H 1 /CS /G /BPC 8 ID \x80 EI Q"),
            new PdfSaveOptions { Version = "2.0" });
        var content = I89_Content(saved);
        var at = content.IndexOf("BI", StringComparison.Ordinal);
        Assert.True(at >= 0, content);
        var dictionary = content[at..content.IndexOf("ID", at, StringComparison.Ordinal)];
        Assert.DoesNotContain("/L ", dictionary.Replace("/L\n", "/L "));
        Assert.DoesNotContain("/Length", dictionary);
    }

    /// <summary>
    /// 8.9.7 sentence 31, today's behaviour: an inline image with /F /Crypt (not allowed inline) stays inline when saved normally; only the
    /// PDF/A ways repack it.
    /// </summary>
    [Fact]
    public void InlineImage_WithCryptFilter_StaysInline_TodaysBehaviour()
    {
        using var saved = DocumentStructureNormTests.Saved(I89_File("", "", "q 200 0 0 200 0 0 cm BI /W 1 /H 1 /CS /G /BPC 8 /F /Crypt ID \x80 EI Q"));
        var content = I89_Content(saved);
        Assert.Contains("BI", content);
        Assert.Contains("Crypt", content);
    }

    /// <summary>
    /// 8.9.7 sentences 32–39 (Table 91): the abbreviations D (Decode) and I (Interpolate) of an inline image work in the display — /D [1 0]
    /// turns the grey image (0, 255) round (upper half: white, black); /I true smooths the enlarged image (lower half: a ramp).
    /// </summary>
    [Fact]
    public void InlineImage_DecodeAndInterpolateAbbreviations()
    {
        using var bitmap = I89_Render(I89_File("", "",
            "q 200 0 0 100 0 100 cm BI /W 2 /H 1 /CS /G /BPC 8 /D [1 0] ID \x00\xff EI Q q 200 0 0 100 0 0 cm BI /W 2 /H 1 /CS /G /BPC 8 /I true ID \x00\xff EI Q"));
        Assert.True(I89_White(I89_At(bitmap, 50, 150)));
        Assert.InRange(I89_At(bitmap, 150, 150).Red, 0, 10);
        Assert.InRange(I89_At(bitmap, 75, 50).Red, 25, 105);
    }

    /// <summary>
    /// 8.9.7 sentences 40–47 (Table 92) and sentences 62–63: the colour space abbreviations CMYK and I (Indexed, with a byte string as table)
    /// of an inline image work in the display — cyan (left), and red, green from the palette (right).
    /// </summary>
    [Fact]
    public void InlineImage_CmykAndIndexedAbbreviations()
    {
        using var bitmap = I89_Render(I89_File("", "",
            "q 100 0 0 200 0 0 cm BI /W 1 /H 1 /CS /CMYK /BPC 8 ID \xff\x00\x00\x00 EI Q q 100 0 0 200 100 0 cm BI /W 2 /H 1 /CS [/I /RGB 1 <FF000000FF00>] /BPC 8 ID \x00\x01 EI Q"));
        var cyan = I89_At(bitmap, 50, 100);
        Assert.True(cyan is { Red: < 60, Green: > 140, Blue: > 200 }, cyan.ToString());
        Assert.True(I89_Red(I89_At(bitmap, 125, 100)), I89_At(bitmap, 125, 100).ToString());
        var green = I89_At(bitmap, 175, 100);
        Assert.True(green is { Red: < 60, Green: > 200, Blue: < 60 }, green.ToString());
    }

    /// <summary>
    /// 8.9.7 sentences 48–61 (Table 92): the filter abbreviations LZW and DCT of an inline image work in the display — an LZW-coded red pixel
    /// (left) and a blue JPEG (right; with /L, so its bytes cannot be mistaken for EI).
    /// </summary>
    [Fact]
    public void InlineImage_LzwAndDctAbbreviations()
    {
        var lzw = I89_Latin1(I89_LzwLiterals([255, 0, 0]));
        var jpeg = I89_Latin1(I89_Jpeg(8, 8, new SKColor(0, 0, 255)));
        using var bitmap = I89_Render(I89_File("", "",
            $"q 100 0 0 200 0 0 cm BI /W 1 /H 1 /CS /RGB /BPC 8 /F /LZW /L {lzw.Length} ID {lzw}\nEI Q " +
            $"q 100 0 0 200 100 0 cm BI /W 8 /H 8 /CS /RGB /BPC 8 /F /DCT /L {jpeg.Length} ID {jpeg}\nEI Q"));
        Assert.True(I89_Red(I89_At(bitmap, 50, 100)), I89_At(bitmap, 50, 100).ToString());
        Assert.True(I89_Blue(I89_At(bitmap, 150, 100)), I89_At(bitmap, 150, 100).ToString());
    }

    /// <summary>
    /// 8.9.7 sentences 62–63 (reader), today's reading: an inline image without /CS and without /IM is read as grey — the sample 0x80 shows mid
    /// grey (<c>PdfImageDecoder.DecodeCore</c>).
    /// </summary>
    [Fact]
    public void InlineImage_WithoutColourSpace_ReadAsGrey_TodaysReading()
    {
        using var bitmap = I89_Render(I89_File("", "", "q 200 0 0 200 0 0 cm BI /W 1 /H 1 /BPC 8 ID \x80 EI Q"));
        var c = I89_At(bitmap, 100, 100);
        Assert.True(Math.Abs(c.Red - 128) <= 2 && c.Red == c.Green && c.Green == c.Blue, c.ToString());
    }
}
