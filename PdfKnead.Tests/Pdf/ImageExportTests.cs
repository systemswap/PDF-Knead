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

    /// <summary>
    /// The Adobe marker is written only where the image leaves PDF (Adobe TN 5116, section 18: applications should not imitate it; ITU-T
    /// T.872, 6.5.3 standardises it; docs/FEHLERGEDAECHTNIS.md, No. 738; docs/normabgleich/M2-filter.md, row 166): a JPEG with three
    /// components and /ColorTransform 0 in /DecodeParms is exported with the marker right after SOI and transform 0, its JFIF marker (which
    /// other programs take for YCbCr) made another application marker; with /ColorTransform 1 the marker says 1 and JFIF stays. The scan data
    /// is unchanged, and without the entry the JPEG goes out byte for byte (<see cref="Jpeg_ByteForByte_MaskBeside"/>).
    /// </summary>
    [Fact]
    public void Jpeg_ColorTransformOfTheEntry_WrittenAsAdobeMarkerOnExport()
    {
        var jpeg = ImageInfoTests.Jpeg(40, 20, 80);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0], jpeg[..4]);
        foreach (var transform in new[] { 0, 1 })
        {
            var image = ImageInfoTests.JpegImage(jpeg, 40, 20);
            image.Dictionary.Set(N("DecodeParms"), Dict(("ColorTransform", PdfInteger.Of(transform))));
            var result = Export(ImageInfoTests.OnePage("q 40 0 0 20 100 100 cm /Im Do Q", ("Im", image)));

            Assert.Equal(ImageExportFormat.Jpeg, result.Format);
            byte[] marker = [0xFF, 0xEE, 0x00, 0x0E, .. "Adobe"u8, 0x00, 0x64, 0x00, 0x00, 0x00, 0x00, (byte)transform];
            Assert.Equal([0xFF, 0xD8, .. marker], result.Data[..(2 + marker.Length)]);
            Assert.Equal(jpeg.Length + marker.Length, result.Data.Length);
            Assert.Equal(transform == 0 ? 0xEF : 0xE0, result.Data[2 + marker.Length + 1]);
            Assert.Equal(jpeg[4..], result.Data[(2 + marker.Length + 2)..]);
        }
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

    /// <summary>
    /// RFC 2083, 2.4 and 9.4 Sätze 11, 13 with ISO 32000-2, 11.6.5.3: a soft mask with /Matte means the colours are premultiplied with the matte
    /// (c′ = m + α (c − m)); PNG keeps colours unpremultiplied, so the export unblends them: c = m + (c′ − m) / α; opaque pixels stay, at
    /// opacity 0 the colour is undefined and written black. Matte (0.2, 0.4, 0.6) = (51, 102, 153); the colour (200, 10, 255) premultiplied with
    /// α = 128 is (126, 56, 204) — the export gives back (200, 10, 255). The same as an indexed image (base DeviceRGB, the palette premultiplied).
    /// Before, the premultiplied values were written (FG 1014).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SoftMaskWithMatte_ColoursUnblended(bool indexed)
    {
        var pdf = new TestPdf();
        var mask = FlateStream([128, 255, 0], Image(3, 1, 8, N("DeviceGray")));
        mask.Dictionary.Set(N("Matte"), Box(0.2, 0.4, 0.6));
        var maskRef = pdf.Add(mask);
        byte[] premultiplied = [126, 56, 204, 30, 40, 50, 51, 102, 153];
        var image = indexed
            ? FlateStream([0, 1, 2], Image(3, 1, 8, new PdfArray([N("Indexed"), N("DeviceRGB"), PdfInteger.Of(2), new PdfString(premultiplied)])))
            : FlateStream(premultiplied, Image(3, 1, 8, N("DeviceRGB")));
        image.Dictionary.Set(N("SMask"), Ref(maskRef));
        var png = Png.ReadColour(Export(ImageInfoTests.OnePage("q 3 0 0 1 100 100 cm /Im Do Q", [("Im", image)], pdf)).Data);
        Assert.Equal((3, 1, 8, 6), (png.Width, png.Height, png.Depth, png.Type));
        Assert.Equal(new byte[] { 200, 10, 255, 128, 30, 40, 50, 255, 0, 0, 0, 0 }, png.Raw);
    }

    /// <summary>
    /// FG 1014 after the probe: /Matte holds components of the colour space, not values 0 … 1 — a Lab image with the matte white [100 0 0] is
    /// unblended after the conversion to RGB exactly as when drawing (the export gives the colours the drawn image has); an inverted grey of
    /// 16 bits (/Decode [1 0]) with the matte 0.25 is unblended in its components: the component 0.6 premultiplied with α = 0.5 is 0.425, the
    /// export gives back 0.6 (± one step).
    /// </summary>
    [Fact]
    public void SoftMaskWithMatte_LabAsDrawn_InvertedGreyInItsComponents()
    {
        var pdf = new TestPdf();
        var mask = FlateStream([200, 128, 255], Image(3, 1, 8, N("DeviceGray")));
        mask.Dictionary.Set(N("Matte"), Box(100, 0, 0));
        var maskRef = pdf.Add(mask);
        var lab = new PdfArray([N("Lab"), Dict(("WhitePoint", Box(0.9505, 1, 1.089)), ("Range", Box(-100, 100, -100, 100)))]);
        var image = FlateStream([128, 128, 128, 90, 150, 110, 200, 100, 140], Image(3, 1, 8, lab));
        image.Dictionary.Set(N("SMask"), Ref(maskRef));
        var bytes = ImageInfoTests.OnePage("q 3 0 0 1 100 100 cm /Im Do Q", [("Im", image)], pdf);
        var png = Png.ReadColour(Export(bytes).Data);
        using (var doc = PdfDocument.Open(bytes))
        {
            var list = PageInterpreter.Build(doc, doc.Pages[0]);
            var drawn = PdfImageDecoder.Decode(list.Images[0], doc)!;
            for (var i = 0; i < 2; i++)   // the pixels with 0 < α < 255
                for (var c = 0; c < 3; c++)
                    Assert.InRange(png.Raw[i * 4 + c], drawn.Pixels[i * 4 + c] - 1, drawn.Pixels[i * 4 + c] + 1);
        }

        var greyPdf = new TestPdf();
        var alpha16 = new byte[] { 0x80, 0x00 };
        var greyMask = FlateStream(alpha16, Image(1, 1, 16, N("DeviceGray")));
        greyMask.Dictionary.Set(N("Matte"), Box(0.25));
        var greyMaskRef = greyPdf.Add(greyMask);
        // component 0.425 under /Decode [1 0]: raw = (1 − 0.425) · 65535
        var raw = (ushort)Math.Round((1 - 0.425) * 65535);
        var grey = FlateStream([(byte)(raw >> 8), (byte)raw], Image(1, 1, 16, N("DeviceGray")));
        grey.Dictionary.Set(N("Decode"), Box(1, 0));
        grey.Dictionary.Set(N("SMask"), Ref(greyMaskRef));
        var greyPng = Png.ReadColour(Export(ImageInfoTests.OnePage("q 1 0 0 1 100 100 cm /Im Do Q", [("Im", grey)], greyPdf)).Data);
        Assert.Equal((16, 4), (greyPng.Depth, greyPng.Type));
        var opacity = 0x8000 / 65535.0;
        var expected = (0.25 + (0.425 - 0.25) / opacity) * 65535;
        Assert.InRange(BinaryPrimitives.ReadUInt16BigEndian(greyPng.Raw), expected - 2, expected + 2);
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
        var png = Png.ReadColour(result.Data);

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

    /// <summary>
    /// A 16-bit RGB image with a 16-bit soft mask of the same size: the PNG keeps the opacity at 16 bits, every bit of the mask — before it went
    /// through 8 bits and lost its lower byte (docs/FEHLERGEDAECHTNIS.md, No. 983). With an 8-bit mask the opacity is spread by repeating its
    /// bits (a · 257, RFC 2083, 12.5; Bericht M2, Zeile 186).
    /// </summary>
    [Fact]
    public void Rgb16_WithMask_OpacityAtSixteenBits()
    {
        foreach (var maskBits in new[] { 16, 8 })
        {
            var pdf = new TestPdf();
            var rgb = new byte[8 * 2 * 3 * 2];
            for (var i = 0; i < rgb.Length / 2; i++) BinaryPrimitives.WriteUInt16BigEndian(rgb.AsSpan(i * 2), (ushort)(i * 4099 + 7));
            var alpha = new byte[8 * 2 * maskBits / 8];
            for (var i = 0; i < 16; i++)
                if (maskBits == 16) BinaryPrimitives.WriteUInt16BigEndian(alpha.AsSpan(i * 2), (ushort)(i * 4111 + 1));
                else alpha[i] = (byte)(i * 17 + 3);
            var mask = pdf.Add(FlateStream(alpha, Image(8, 2, maskBits, N("DeviceGray"))));
            var image = FlateStream(rgb, Image(8, 2, 16, N("DeviceRGB")));
            image.Dictionary.Set(N("SMask"), Ref(mask));
            var png = Png.Read(Export(ImageInfoTests.OnePage("q 8 0 0 2 100 100 cm /Im Do Q", [("Im", image)], pdf)).Data);
            Assert.Equal((8, 2, 16, 6), (png.Width, png.Height, png.Depth, png.Type));
            // RFC 2083, 4.2.6 Satz 6: an 8-bit mask scaled up to 16 bits is noted, a 16-bit one needs nothing
            if (maskBits == 8) Assert.Equal(new byte[] { 16, 16, 16, 8 }, png.Chunks["sBIT"]);
            else Assert.False(png.Chunks.ContainsKey("sBIT"));
            for (var i = 0; i < 16; i++)
            {
                Assert.Equal(rgb.AsSpan(i * 6, 6).ToArray(), png.Raw.AsSpan(i * 8, 6).ToArray());
                int expected = maskBits == 16 ? BinaryPrimitives.ReadUInt16BigEndian(alpha.AsSpan(i * 2)) : alpha[i] * 257;
                Assert.Equal(expected, BinaryPrimitives.ReadUInt16BigEndian(png.Raw.AsSpan(i * 8 + 6)));
            }
        }
    }

    /// <summary>
    /// RFC 2083, 2.3 and 12.5: PNG holds RGB and grey with alpha only at 8 or 16 bits — an RGB image of 2 bits becomes 8 bits, each value spread by
    /// repeating its bits (v · 85), not padded with zeros; a grey image of 4 bits with a 2-bit soft mask becomes grey with alpha at 8 bits,
    /// v · 17 and the mask 0, 85, 170, 255 (Bericht M2, Zeilen 186, 356; the report said before that nothing is scaled up).
    /// </summary>
    [Fact]
    public void LowBitDepths_SpreadByRepeatingTheirBits()
    {
        // RGB, 2 bits: 4 pixels = 12 values 0..3 → 3 bytes
        byte[] rgb2 = [0b0001_1011, 0b1110_0100, 0b0101_1010];
        var dict = Image(4, 1, 2, N("DeviceRGB"));
        var pdf = new TestPdf();
        var mask = pdf.Add(new PdfStream(Image(4, 1, 8, N("DeviceGray")), [0, 85, 170, 255]));
        var image = new PdfStream(dict, rgb2);
        image.Dictionary.Set(N("SMask"), Ref(mask));
        var png = Png.ReadColour(Export(ImageInfoTests.OnePage("q 4 0 0 1 100 100 cm /Im Do Q", [("Im", image)], pdf)).Data);
        Assert.Equal((8, 6), (png.Depth, png.Type));
        for (var i = 0; i < 12; i++) Assert.Equal((rgb2[i / 4] >> (6 - 2 * (i % 4)) & 3) * 85, png.Raw[i / 3 * 4 + i % 3]);
        // RFC 2083, 4.2.6 Satz 6: scaled up from 2 bits; four colours make a palette image (9.6), whose sBIT names the three colour channels
        Assert.Equal(3, png.StoredType);
        Assert.Equal(new byte[] { 2, 2, 2 }, png.Chunks["sBIT"]);

        // grey, 4 bits, with a 2-bit mask
        byte[] grey4 = [0x0F, 0x5A];
        var greyPdf = new TestPdf();
        var greyMask = greyPdf.Add(new PdfStream(Image(4, 1, 2, N("DeviceGray")), [0b0001_1011]));
        var grey = new PdfStream(Image(4, 1, 4, N("DeviceGray")), grey4);
        grey.Dictionary.Set(N("SMask"), Ref(greyMask));
        var greyPng = Png.ReadColour(Export(ImageInfoTests.OnePage("q 4 0 0 1 100 100 cm /Im Do Q", [("Im", grey)], greyPdf)).Data);
        Assert.Equal((8, 4), (greyPng.Depth, greyPng.Type));
        Assert.Equal(new byte[] { 0, 0, 255, 85, 85, 170, 170, 255 }, greyPng.Raw);
        Assert.Equal(new byte[] { 4, 2 }, greyPng.Chunks["sBIT"]);
    }

    /// <summary>
    /// ITU-T T.800 I.2, I.2.1: a JPEG 2000 image is saved as it is — a JP2 file with the extension .jp2, a JPX file with .jpf (T.801 M.2.1),
    /// a bare codestream with .j2k, the bytes
    /// unchanged (Bericht M2, Zeile 493: PDF-Knead writes the JP2 format here, the rows said before that it writes no JPEG 2000).
    /// </summary>
    [TestPdfsFact]
    public void Jpeg2000_SavedAsItIs_Jp2OrJ2k()
    {
        var codestream = JpxColourTests.Codestream("jp2k-resetprob.pdf");
        byte[] Box(string type, byte[] content) => [.. BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(8 + content.Length)),
            .. Encoding.ASCII.GetBytes(type), .. content];
        byte[] jp2 = [.. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]), .. Box("ftyp", [.. "jp2 "u8, 0, 0, 0, 0, .. "jp2 "u8]),
            .. Box("jp2h", Box("colr", [1, 0, 0, 0, 0, 0, 16])), .. Box("jp2c", codestream)];
        // a JPX file (brand „jpx “): .jpf, unless its compatibility list names „jp2 “ (T.801 M.2.1 sentences 2, 4; FG 988: before .jp2)
        byte[] jpx = [.. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]), .. Box("ftyp", [.. "jpx "u8, 0, 0, 0, 1, .. "jpx "u8]),
            .. Box("jp2h", Box("colr", [1, 0, 1, 0, 0, 0, 16])), .. Box("jp2c", codestream)];
        byte[] jpxCompatible = [.. Box("jP  ", [0x0D, 0x0A, 0x87, 0x0A]), .. Box("ftyp", [.. "jpx "u8, 0, 0, 0, 1, .. "jpx "u8, .. "jp2 "u8]),
            .. Box("jp2h", Box("colr", [1, 0, 1, 0, 0, 0, 16])), .. Box("jp2c", codestream)];
        foreach (var (data, extension) in new[] { (jp2, ".jp2"), (codestream, ".j2k"), (jpx, ".jpf"), (jpxCompatible, ".jp2") })
        {
            var dict = Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(40)), ("Height", PdfInteger.Of(27)),
                ("Filter", N("JPXDecode")));
            var result = Export(ImageInfoTests.OnePage("q 40 0 0 27 100 100 cm /Im Do Q", ("Im", new PdfStream(dict, data))));
            Assert.Equal(extension, result.Extension);
            Assert.Equal(data, result.Data);
        }
    }

    [Fact]
    public void Rgb_MaskOfAnotherSize_Beside()
    {
        var pdf = new TestPdf();
        var mask = pdf.Add(FlateStream(Ramp(32 * 8), Image(32, 8, 8, N("DeviceGray"))));
        var image = FlateStream(Ramp(16 * 4 * 3), Image(16, 4, 8, N("DeviceRGB")));
        image.Dictionary.Set(N("SMask"), Ref(mask));
        var result = Export(ImageInfoTests.OnePage("q 16 0 0 4 100 100 cm /Im Do Q", [("Im", image)], pdf));

        Assert.Equal(2, Png.ReadColour(result.Data).Type);   // RGB without alpha
        Assert.Equal((32, 8), (Png.ReadColour(result.MaskPng!).Width, Png.ReadColour(result.MaskPng!).Height));
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
        // the resolution every Baseline image needs (TIFF 6.0, sections 8 and 16; No. 942): that of its drawn size — 10 × 3 pixels drawn
        // 10 × 3 points large, 72 per inch (FG 1019)
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
        var png = Png.ReadColour(result.Data);

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
        var png = Png.ReadColour(result.Data);

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

    /// <summary>
    /// RFC 2083, 9.6 Satz 3 (decided by the user on 22.11.2026): a colour image of at most 256 colours is written as a palette image with the
    /// smallest index depth, the transparent entries first in tRNS; one of 257 colours stays RGB. Both read back exactly. Before, every RGB
    /// image was written as true colour.
    /// </summary>
    [Fact]
    public void FewColours_PaletteImage_ManyColours_TrueColour()
    {
        foreach (var (colours, depth) in new[] { (2, 1), (3, 2), (16, 4), (256, 8), (257, -1) })
        {
            var pixels = new byte[32 * 9 * 4];
            for (var i = 0; i < 32 * 9; i++)
            {
                var k = i % colours;
                (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) = ((byte)k, (byte)(k >> 8), 77, k == 1 ? (byte)0 : (byte)255);
            }
            var file = ImageExport.Export(new RgbaImage(32, 9, pixels)).Data;
            var stored = Png.Read(file);
            if (depth < 0) Assert.Equal((8, 6), (stored.Depth, stored.Type));
            else
            {
                Assert.Equal((depth, 3), (stored.Depth, stored.Type));
                Assert.Equal(new byte[] { 0 }, stored.Chunks["tRNS"]);   // the one transparent colour is the first entry
            }
            Assert.Equal(pixels, Png.ReadColour(file).Raw);
            Assert.Equal(pixels, PngDecoder.Decode(file).Pixels);
        }
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
        // with its drawn size, as the program passes it (the resolution of a TIFF, FG 1019)
        return ImageExport.Export(doc, list.Images[item.Image], ImageExport.DrawnSize(list.Matrices[item.Matrix]));
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
    private sealed record Png(int Width, int Height, int Depth, int Type, byte[] Raw, Dictionary<string, byte[]> Chunks, int StoredType = -1)
    {
        /// <summary>
        /// Reads like <see cref="Read"/>, a palette image expanded as a reader sees it: RGBA with 8 bits if it has tRNS, otherwise RGB;
        /// <see cref="StoredType"/> keeps the colour type in the file. Colour images of at most 256 colours are written as palette images
        /// (RFC 2083, 9.6), so the tests of their colours read them this way.
        /// </summary>
        public static Png ReadColour(byte[] file)
        {
            var png = Read(file);
            if (png.Type != 3) return png with { StoredType = png.Type };
            var palette = png.Chunks["PLTE"];
            var alphas = png.Chunks.GetValueOrDefault("tRNS") ?? [];
            var alpha = alphas.Length > 0;
            var channels = alpha ? 4 : 3;
            var rowBytes = (png.Width * png.Depth + 7) / 8;
            var raw = new byte[png.Width * png.Height * channels];
            for (var y = 0; y < png.Height; y++)
                for (var x = 0; x < png.Width; x++)
                {
                    var bit = x * png.Depth;
                    var index = png.Raw[y * rowBytes + (bit >> 3)] >> (8 - png.Depth - (bit & 7)) & ((1 << png.Depth) - 1);
                    var at = (y * png.Width + x) * channels;
                    palette.AsSpan(index * 3, 3).CopyTo(raw.AsSpan(at));
                    if (alpha) raw[at + 3] = index < alphas.Length ? alphas[index] : (byte)255;
                }
            return new Png(png.Width, png.Height, 8, alpha ? 6 : 2, raw, png.Chunks, 3);
        }

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

    /// <summary>
    /// TIFF 6.0, section 17 (sentence 40) and section 20: grey without a profile counts as linear, RGB as gamma 2.2 — the page images are
    /// rendered or scanned values in the sRGB curve. Grey pages carry a grey profile with the sRGB tone curve (mid grey 0.5 stays 0.5 in sRGB),
    /// RGB pages the sRGB profile, in tag 34675 (decided on 20.11.2026; docs/FEHLERGEDAECHTNIS.md, No. 980). Before none.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TiffPages_GreyAndRgb_CarryTheirProfile(bool rgb)
    {
        var kind = rgb ? TiffPageKind.Rgb : TiffPageKind.Gray;
        var channels = rgb ? 3 : 1;
        using var output = new MemoryStream();
        TiffEncoder.EncodePages(1, _ => new TiffPage(4, 2, kind, 72, (_, row) => row.Fill(128)), output);
        var tiff = Tiff.Read(output.ToArray());
        Assert.Equal(channels, tiff.Samples);
        var profile = PdfKnead.Core.Pdf.Graphics.Icc.IccProfile.Parse(Assert.IsType<byte[]>(tiff.Icc))!;
        Assert.Equal(rgb ? PdfKnead.Core.Pdf.Graphics.Icc.IccProfile.RgbSpace : PdfKnead.Core.Pdf.Graphics.Icc.IccProfile.GraySpace, profile.Space);
        var transform = new PdfKnead.Core.Pdf.Graphics.Icc.IccTransform(profile);
        Span<float> device = [0.5f, 0.5f, 0.5f];
        Span<float> srgb = stackalloc float[3];
        transform.ToSrgb(device[..channels], srgb);
        for (var k = 0; k < 3; k++) Assert.Equal(0.5, srgb[k], 0.01);
    }

    /// <summary>The tags of the first directory of a TIFF written by <see cref="TiffEncoder"/> (big-endian): values and where they lie.</summary>
    private static Dictionary<int, (uint[] Values, int At, int Size)> TiffTags(byte[] file)
    {
        var ifd = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4));
        var tags = new Dictionary<int, (uint[], int, int)>();
        for (var i = 0; i < BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(ifd)); i++)
        {
            var e = file.AsSpan(ifd + 2 + i * 12, 12);
            int tag = BinaryPrimitives.ReadUInt16BigEndian(e), type = BinaryPrimitives.ReadUInt16BigEndian(e[2..]);
            var n = (int)BinaryPrimitives.ReadUInt32BigEndian(e[4..]);
            var unit = type switch { 3 => 2, 4 => 4, 5 => 8, _ => 1 };
            var at = unit * n <= 4 ? ifd + 2 + i * 12 + 8 : (int)BinaryPrimitives.ReadUInt32BigEndian(e[8..]);
            var values = type is 3 or 4
                ? Enumerable.Range(0, n).Select(k => type == 3 ? BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at + k * 2)) : BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + k * 4))).ToArray()
                : [];
            tags[tag] = (values, at, unit * n);
        }
        return tags;
    }

    /// <summary>
    /// TIFF 6.0, section 3 and 2: a page whose single row holds more than 32 KB gets one row per strip and reads back exactly; the strips and the
    /// long values of the directory do not overlap (Bericht M2, Zeile 385). Section 4: a greyscale page has BitsPerSample 8, SamplesPerPixel 1,
    /// PhotometricInterpretation 1 (Zeile 389).
    /// </summary>
    [Fact]
    public void TiffPages_WideRowsAndGreyTags()
    {
        const int width = 40000, height = 3;
        using var output = new MemoryStream();
        TiffEncoder.EncodePages(1, _ => new TiffPage(width, height, TiffPageKind.Gray, 300, (y, row) => { for (var x = 0; x < row.Length; x++) row[x] = (byte)(x * 7 + y); }), output);
        var file = output.ToArray();
        var tiff = Tiff.Read(file);
        Assert.Equal(1u, tiff.Tags[278][0]);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x += 997) Assert.Equal((byte)(x * 7 + y), tiff.Raw[y * width + x]);
        Assert.Equal((8u, 1u, 1u), (tiff.Tags[258][0], tiff.Tags[277][0], tiff.Tags[262][0]));
        var tags = TiffTags(file);
        var areas = tags.Values.Where(v => v.Size > 4).Select(v => (Start: v.At, End: v.At + v.Size))
            .Concat(tags[273].Values.Zip(tags[279].Values, (o, n) => (Start: (int)o, End: (int)(o + n)))).OrderBy(a => a.Start).ToList();
        for (var i = 1; i < areas.Count; i++) Assert.True(areas[i - 1].End <= areas[i].Start, $"überlappt bei {areas[i].Start}");
    }

    /// <summary>
    /// TIFF 6.0, section 16 and 18: CMYK with opacity has SamplesPerPixel 5, ExtraSamples [2] (unassociated alpha) and five BitsPerSample; without
    /// opacity there is no ExtraSamples (Zeile 388).
    /// </summary>
    [Fact]
    public void TiffCmyk_ExtraSamplesOnlyWithAlpha()
    {
        foreach (var alpha in new[] { true, false })
        {
            using var output = new MemoryStream();
            TiffEncoder.EncodeCmyk(2, 2, 8, alpha, (_, row) => row.Fill(77), null, output);
            var tags = TiffTags(output.ToArray());
            var samples = alpha ? 5u : 4u;
            Assert.Equal(samples, tags[277].Values[0]);
            Assert.Equal(Enumerable.Repeat(8u, (int)samples), tags[258].Values);
            if (alpha) Assert.Equal([2u], tags[338].Values);
            else Assert.False(tags.ContainsKey(338));
        }
    }

    /// <summary>
    /// TIFF 6.0, section 11: a black and white page has T6Options 0 and no T4Options; its Group 4 data never switch to the uncompressed mode —
    /// the encoder has no way to an extension code (it writes only pass, horizontal and vertical codes and runs), and the strips read back
    /// whole (Zeile 391); every strip ends with EOFB and at most 7 zero bits, and
    /// StripByteCounts is exactly that length (Zeile 392).
    /// </summary>
    [Fact]
    public void TiffBlackWhite_Group4Strips()
    {
        const int width = 1700, height = 120;
        byte Pixel(int x, int y) => (byte)((x / 3 + y / 5) % 2);
        using var output = new MemoryStream();
        TiffEncoder.EncodePages(1, _ => new TiffPage(width, height, TiffPageKind.BlackWhite, 200, (y, row) =>
        {
            row.Clear();
            for (var x = 0; x < width; x++) if (Pixel(x, y) == 1) row[x >> 3] |= (byte)(0x80 >> (x & 7));
        }), output);
        var file = output.ToArray();
        var tags = TiffTags(file);
        Assert.Equal([0u], tags[293].Values);
        Assert.False(tags.ContainsKey(292));
        var rowsPerStrip = (int)tags[278].Values[0];
        var offsets = tags[273].Values;
        var counts = tags[279].Values;
        for (var s = 0; s < offsets.Length; s++)
        {
            var strip = file[(int)offsets[s]..(int)(offsets[s] + counts[s])];
            var bits = string.Concat(strip.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
            var end = bits.LastIndexOf("000000000001000000000001", StringComparison.Ordinal);
            Assert.True(end >= 0, $"Streifen {s}: kein EOFB");
            var tail = bits[(end + 24)..];
            Assert.True(tail.Length < 8 && !tail.Contains('1'), $"Streifen {s}: {tail.Length} Bits hinter EOFB");
            var rows = Math.Min(rowsPerStrip, height - s * rowsPerStrip);
            var parms = Dict(("K", PdfInteger.Of(-1)), ("Columns", PdfInteger.Of(width)), ("Rows", PdfInteger.Of(rows)), ("BlackIs1", PdfBoolean.True));
            var truncated = false;
            var decoded = PdfKnead.Core.Pdf.Filters.CcittFax.Decode(strip, parms, ref truncated);
            Assert.False(truncated);
            var rowBytes = (width + 7) / 8;
            for (var y = 0; y < rows; y++)
                for (var x = 0; x < width; x += 13)
                    Assert.Equal(Pixel(x, s * rowsPerStrip + y), (decoded[y * rowBytes + (x >> 3)] >> (7 - (x & 7))) & 1);
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
