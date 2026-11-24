using System.Buffers.Binary;
using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Colour management: ICC profiles (matrix and tables), DeviceCMYK via FOGRA39 or the output intent profile, /DefaultCMYK,
/// CalRGB and Lab. The test profiles are created in code (matrix profile, "baked" CMYK table).
/// </summary>
public sealed class IccTests
{
    private static PdfDocument EmptyDoc() => PdfDocument.Open(TestPdf.Simple(1).Build());

    /// <summary>Builds an RGB matrix profile: primaries (XYZ D50, one per column) and one gamma curve for all three channels.</summary>
    private static byte[] MatrixProfile(float[] columns, float gamma)
    {
        var tags = new (uint Sig, byte[] Data)[6];
        uint[] xyzSigs = [0x7258595A, 0x6758595A, 0x6258595A];
        uint[] trcSigs = [0x72545243, 0x67545243, 0x62545243];
        for (var c = 0; c < 3; c++)
        {
            var xyz = new byte[20];
            BinaryPrimitives.WriteUInt32BigEndian(xyz, 0x58595A20);
            for (var r = 0; r < 3; r++) BinaryPrimitives.WriteInt32BigEndian(xyz.AsSpan(8 + 4 * r), (int)Math.Round(columns[r * 3 + c] * 65536));
            tags[c] = (xyzSigs[c], xyz);
            byte[] curve;
            if (gamma > 0)
            {
                curve = new byte[16];
                BinaryPrimitives.WriteUInt32BigEndian(curve, 0x63757276);   // curv with a single gamma
                BinaryPrimitives.WriteUInt32BigEndian(curve.AsSpan(8), 1);
                BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(12), (ushort)Math.Round(gamma * 256));
            }
            else
            {
                // Gamma 0: the sRGB curve as para type 3
                curve = new byte[12 + 5 * 4];
                BinaryPrimitives.WriteUInt32BigEndian(curve, 0x70617261);
                BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(8), 3);
                double[] p = [2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045];
                for (var i = 0; i < 5; i++) BinaryPrimitives.WriteInt32BigEndian(curve.AsSpan(12 + 4 * i), (int)Math.Round(p[i] * 65536));
            }
            tags[3 + c] = (trcSigs[c], curve);
        }
        var size = 128 + 4 + 12 * tags.Length + tags.Sum(t => t.Data.Length);
        var d = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(d, (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), 0x02100000);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(12), 0x6D6E7472);   // mntr
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), IccProfile.RgbSpace);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), 0x58595A20);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(36), 0x61637370);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(128), (uint)tags.Length);
        var at = 128 + 4 + 12 * tags.Length;
        for (var i = 0; i < tags.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(132 + 12 * i), tags[i].Sig);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(136 + 12 * i), (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(140 + 12 * i), (uint)tags[i].Data.Length);
            tags[i].Data.CopyTo(d, at);
            at += tags[i].Data.Length;
        }
        return d;
    }

    // sRGB and AdobeRGB primaries, Bradford-adapted to D50 (Lindbloom)
    private static readonly float[] SrgbColumns = [0.4360747f, 0.3850649f, 0.1430804f, 0.2225045f, 0.7168786f, 0.0606169f, 0.0139322f, 0.0971045f, 0.7141733f];
    private static readonly float[] AdobeColumns = [0.6097559f, 0.2052401f, 0.1492240f, 0.3111242f, 0.6256560f, 0.0632197f, 0.0194811f, 0.0608902f, 0.7448387f];

    /// <summary>Simple CMYK formula as a profile: for checking the tables (lut16, Lab) against known values.</summary>
    private static readonly Lazy<byte[]> NaiveCmyk = new(() => CmykProfile.Bake((c, m, y, k) => ((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k))));

    [Fact]
    public void Version4MatrixProfile_AsVersion2_KeepsItsColours()
    {
        // PDF/A-1 allows profiles of version 2 only: a matrix profile of version 4 (curves as formulas, texts as mluc) is written as 2.1
        var v4 = PdfARuleCases.Version4Rgb();
        var v2 = CalibratedProfile.AsVersion2(v4);
        Assert.NotNull(v2);
        Assert.Equal(2, v2![8]);
        Assert.True(v2.AsSpan(84, 16).IndexOfAnyExcept((byte)0) < 0);
        var before = new IccTransform(IccProfile.Parse(v4)!, compensateBlack: false);
        var after = new IccTransform(IccProfile.Parse(v2)!, compensateBlack: false);
        Span<float> a = stackalloc float[3];
        Span<float> b = stackalloc float[3];
        foreach (var (r, g, bl) in new[] { (0f, 0f, 0f), (1f, 1f, 1f), (0.02f, 0.5f, 0.9f), (0.3f, 0.04f, 0.6f), (0.8f, 0.8f, 0.1f) })
        {
            before.ToSrgb([r, g, bl], a);
            after.ToSrgb([r, g, bl], b);
            for (var c = 0; c < 3; c++) Assert.InRange(b[c] - a[c], -0.002f, 0.002f);
        }
        // a profile of tables cannot be written so
        Assert.Null(CalibratedProfile.AsVersion2(NaiveCmyk.Value));
    }

    [Fact]
    public void Lut16Profile_ReproducesItsFunction()
    {
        var transform = new IccTransform(IccProfile.Parse(NaiveCmyk.Value)!, compensateBlack: false);
        Span<float> rgb = stackalloc float[3];
        foreach (var (c, m, y, k) in new[] { (0f, 0f, 0f, 0f), (1f, 0f, 0f, 0f), (0.3f, 0.6f, 0.1f, 0.2f), (0f, 0f, 0f, 0.5f), (0.7f, 0.2f, 0.9f, 0.1f) })
        {
            transform.ToSrgb([c, m, y, k], rgb);
            Assert.InRange(rgb[0], (1 - c) * (1 - k) - 0.02f, (1 - c) * (1 - k) + 0.02f);
            Assert.InRange(rgb[1], (1 - m) * (1 - k) - 0.02f, (1 - m) * (1 - k) + 0.02f);
            Assert.InRange(rgb[2], (1 - y) * (1 - k) - 0.02f, (1 - y) * (1 - k) + 0.02f);
        }
    }

    [Fact]
    public void ImageRows_MatchSingleColors()
    {
        // Grid (17⁴, tetrahedral) against the exact computation per colour
        var transform = CmykProfile.Default;
        byte[] row = [0, 0, 0, 0, 255, 0, 0, 0, 30, 200, 90, 40, 0, 0, 0, 255, 128, 100, 100, 160];
        var rgba = new byte[5 * 4];
        transform.Row8(row, 5, rgba);
        for (var i = 0; i < 5; i++)
        {
            var exact = transform.ToRgb([row[i * 4] / 255.0, row[i * 4 + 1] / 255.0, row[i * 4 + 2] / 255.0, row[i * 4 + 3] / 255.0]);
            Assert.InRange(rgba[i * 4] - exact.R, -3, 3);
            Assert.InRange(rgba[i * 4 + 1] - exact.G, -3, 3);
            Assert.InRange(rgba[i * 4 + 2] - exact.B, -3, 3);
            Assert.Equal(255, rgba[i * 4 + 3]);
        }
    }

    [Fact]
    public void DeviceCmyk_UsesFogra39_WithNeutralDepths()
    {
        using var doc = EmptyDoc();
        var cmyk = ColorSpace.DeviceCmykOf(doc);
        Assert.True(cmyk.IsDeviceCmyk);
        var black = cmyk.ToRgb([0, 0, 0, 1]);
        var rich = cmyk.ToRgb([1, 1, 1, 1]);
        var gray = cmyk.ToRgb([0.5, 0.4, 0.4, 0.6]);
        var paper = cmyk.ToRgb([0, 0, 0, 0]);
        // Subdued, neutral black (a widely used SWOP approximation gives 44/46/53, bluish), deep black almost 0, paper white
        Assert.True(black is { R: > 15 and < 45 } && Math.Abs(black.R - black.B) <= 5, black.ToString());
        Assert.True(rich is { R: < 10, G: < 10, B: < 10 }, rich.ToString());
        Assert.True(Math.Abs(gray.R - gray.G) <= 6 && Math.Abs(gray.G - gray.B) <= 6, gray.ToString());
        Assert.True(paper is { R: >= 250, G: >= 250, B: >= 250 }, paper.ToString());
    }

    [Fact]
    public void OutputIntent_ReplacesTheDefaultCmykProfile()
    {
        var pdf = TestPdf.Simple(1);
        var profile = pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), NaiveCmyk.Value));
        var intent = TestPdf.Dict(("Type", TestPdf.N("OutputIntent")), ("S", TestPdf.N("GTS_PDFX")), ("DestOutputProfile", TestPdf.Ref(profile)));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("OutputIntents"), new PdfArray([intent]));
        using var doc = PdfDocument.Open(pdf.Build());
        var cmyk = ColorSpace.DeviceCmykOf(doc);
        Assert.True(cmyk.IsDeviceCmyk);
        // The simple formula from the profile: 100 % cyan becomes pure cyan, not the subdued cyan of the FOGRA profile
        var cyan = cmyk.ToRgb([1, 0, 0, 0]);
        Assert.True(cyan is { R: < 8, G: > 245, B: > 245 }, cyan.ToString());
        // The page also uses it for k/K
        Assert.Same(cmyk, ColorSpace.Device(4, null, doc));
    }

    [Fact]
    public void DefaultCmykOfTheResources_TakesPrecedence()
    {
        using var doc = EmptyDoc();
        var icc = new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), NaiveCmyk.Value);
        var resources = TestPdf.Dict(("ColorSpace", TestPdf.Dict(("DefaultCMYK", new PdfArray([TestPdf.N("ICCBased"), icc])))));
        var space = ColorSpace.Create(TestPdf.N("DeviceCMYK"), resources, doc);
        var magenta = space.ToRgb([0, 1, 0, 0]);
        Assert.True(magenta is { R: > 245, G: < 8, B: > 245 }, magenta.ToString());
        Assert.Same(ColorSpace.DeviceCmykOf(doc), ColorSpace.Create(TestPdf.N("DeviceCMYK"), null, doc));
    }

    /// <summary>
    /// Found by fuzzing: /DefaultRGB is an unreadable ICC profile with /Alternate /DeviceRGB. If the fallback led back to /DefaultRGB,
    /// it would recurse until the stack overflows (the process ends; this cannot be caught). Within the default colour space the device colours apply.
    /// </summary>
    [Fact]
    public void DefaultRgbWithBrokenProfile_FallsBackWithoutRecursion()
    {
        using var doc = EmptyDoc();
        var broken = new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(3)), ("Alternate", TestPdf.N("DeviceRGB"))), [1, 2, 3, 4, 5]);
        var resources = TestPdf.Dict(("ColorSpace", TestPdf.Dict(("DefaultRGB", new PdfArray([TestPdf.N("ICCBased"), broken])))));
        Assert.Same(ColorSpace.DeviceRgb, ColorSpace.Create(TestPdf.N("DeviceRGB"), resources, doc));
        // Without /Alternate: the fallback by /N does not lead back either
        var plain = new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(3))), [9, 9, 9]);
        var again = TestPdf.Dict(("ColorSpace", TestPdf.Dict(("DefaultRGB", new PdfArray([TestPdf.N("ICCBased"), plain])))));
        Assert.Same(ColorSpace.DeviceRgb, ColorSpace.Create(TestPdf.N("DeviceRGB"), again, doc));
    }

    [Fact]
    public void MatrixProfiles_SrgbIsPassedThrough_AdobeRgbIsConverted()
    {
        // sRGB as a matrix profile (attached to many images): like DeviceRGB, without the detour via tables
        using var doc = PdfDocument.Open(TestPdf.Simple().Build());
        Assert.Same(ColorSpace.DeviceRgb, ColorSpace.FromProfile(MatrixProfile(SrgbColumns, 0), 3, doc));
        var adobe = ColorSpace.FromProfile(MatrixProfile(AdobeColumns, 2.2f), 3, doc)!;
        Assert.NotSame(ColorSpace.DeviceRgb, adobe);
        // Grey stays grey; pure green from AdobeRGB lies outside sRGB and is clipped
        var gray = adobe.ToRgb([0.5, 0.5, 0.5]);
        Assert.True(Math.Abs(gray.R - gray.G) <= 1 && Math.Abs(gray.G - gray.B) <= 1 && gray.R is > 115 and < 140, gray.ToString());
        var green = adobe.ToRgb([0, 1, 0]);
        Assert.True(green is { R: 0, G: > 240 } && green.B < 80, green.ToString());
        // 8-bit rows (tables) like single colours
        Assert.True(adobe.HasRow8);
        var rgba = new byte[4];
        adobe.ConvertRow8([128, 128, 128], 1, rgba);
        Assert.InRange(rgba[0] - gray.R, -1, 1);
    }

    [Fact]
    public void LabAndCalRgb_AreConvertedOverXyz()
    {
        using var doc = EmptyDoc();
        // Lab with white point D50: L 50, a = b = 0 is neutral grey (computing with D65 gives a yellow cast)
        var lab = ColorSpace.Create(new PdfArray([TestPdf.N("Lab"), TestPdf.Dict(("WhitePoint", TestPdf.Box(0.9642, 1, 0.8249)))]), null, doc);
        var mid = lab.ToRgb([50, 0, 0]);
        Assert.True(Math.Abs(mid.R - mid.B) <= 2 && Math.Abs(mid.R - mid.G) <= 2 && mid.R is > 110 and < 130, mid.ToString());
        // CalRGB with an sRGB-like matrix (D65) and gamma 2.2: white stays white, grey stays grey
        var cal = ColorSpace.Create(new PdfArray([TestPdf.N("CalRGB"), TestPdf.Dict(("WhitePoint", TestPdf.Box(0.9505, 1, 1.089)),
            ("Gamma", TestPdf.Box(2.2, 2.2, 2.2)), ("Matrix", TestPdf.Box(0.4124, 0.2126, 0.0193, 0.3576, 0.7152, 0.1192, 0.1805, 0.0722, 0.9505)))]), null, doc);
        var white = cal.ToRgb([1, 1, 1]);
        Assert.True(white is { R: >= 252, G: >= 252, B: >= 252 }, white.ToString());
        var grey = cal.ToRgb([0.5, 0.5, 0.5]);
        Assert.True(Math.Abs(grey.R - grey.B) <= 2 && grey.R is > 120 and < 136, grey.ToString());
    }

    [Fact]
    public void IndexedLab_MapsTheTableIntoTheLabRange()
    {
        // issue10339: entries 0…255 map to L 0…100 and a/b to /Range, not to 0…1; otherwise everything is black
        using var doc = EmptyDoc();
        var lab = new PdfArray([TestPdf.N("Lab"), TestPdf.Dict(("WhitePoint", TestPdf.Box(0.9642, 1, 0.8249)), ("Range", TestPdf.Box(-128, 127, -128, 127)))]);
        var indexed = ColorSpace.Create(new PdfArray([TestPdf.N("Indexed"), lab, PdfInteger.Of(1), new PdfString([255, 128, 128, 128, 128, 128])]), null, doc);
        var white = indexed.ToRgb([0]);
        Assert.True(white is { R: >= 250, G: >= 250, B: >= 250 }, white.ToString());
        var mid = indexed.ToRgb([1]);
        Assert.True(mid.R is > 110 and < 130 && Math.Abs(mid.R - mid.B) <= 3, mid.ToString());
    }

    [Fact]
    public void BrokenProfiles_FallBackToTheAlternate()
    {
        using var doc = EmptyDoc();
        var random = new Random(17);
        for (var i = 0; i < 300; i++)
        {
            var data = (byte[])NaiveCmyk.Value.Clone();
            if (i % 2 == 0) data = data[..random.Next(1, 400)];
            for (var k = 0; k < 8; k++) data[random.Next(data.Length)] = (byte)random.Next(256);
            IccProfile.Parse(data);   // never an exception
        }
        var broken = new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(3)), ("Alternate", TestPdf.N("DeviceGray"))), [1, 2, 3, 4]);
        Assert.Same(ColorSpace.DeviceGray, ColorSpace.Create(new PdfArray([TestPdf.N("ICCBased"), broken]), null, doc));
        var missing = new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), [1, 2, 3]);
        Assert.Same(ColorSpace.DeviceCmykOf(doc), ColorSpace.Create(new PdfArray([TestPdf.N("ICCBased"), missing]), null, doc));
    }

    /// <summary>
    /// A CMYK photo is converted as every other image of its colour space and within the accuracy of the image grid like a flat area
    /// (docs/FEHLERGEDAECHTNIS.md, No. 805): the own decoder gives the CMYK values as they stand, and every pixel of the display is the colour
    /// space's conversion of them — before, the codec converted CMYK JPEGs itself through a table baked from the colour space.
    /// </summary>
    [TestPdfsFact]
    public void CmykJpeg_ConvertedLikeFlatAreas()
    {
        var path = Path.Combine(Root(), "TestPDFs", "pdfjs", "cmykjpeg.pdf");
        using var doc = PdfDocument.Open(File.ReadAllBytes(path));
        var image = PageInterpreter.Build(doc, doc.Pages[0]).Images[0];
        var samples = Core.Pdf.Filters.Jpeg.JpegDecoder.Decode(doc.DecodeStream(image.Stream!).Data);
        Assert.Equal(4, samples.Channels);
        var shown = PdfImageDecoder.Decode(image, doc)!;
        Assert.Equal((samples.Width, samples.Height), (shown.Width, shown.Height));
        var space = ColorSpace.DeviceCmykOf(doc);
        var random = new Random(8);
        var most = 0;
        for (var i = 0; i < 2000; i++)
        {
            var p = random.Next(samples.Width * samples.Height);
            var cmyk = Enumerable.Range(0, 4).Select(c => samples.Samples[p * 4 + c] / 255.0).ToArray();
            var flat = space.ToRgb(cmyk);
            for (var c = 0; c < 3; c++)
                most = Math.Max(most, Math.Abs(shown.Rgba[p * 4 + c] - (c == 0 ? flat.R : c == 1 ? flat.G : flat.B)));
        }
        // images go through the grid of the transform (17⁴ points, tetrahedral, IccTransform), single colours exactly: at most 3 steps apart
        Assert.True(most <= 3, $"größter Unterschied {most}");
    }

    [TestPdfsFact]
    public void CmykJpeg_WithDecodeArray_StaysRgb()
    {
        // Skia already delivers the CMYK JPEG as RGB; /Decode must not read these values as CMYK once more
        var path = Path.Combine(Root(), "TestPDFs", "pdfjs", "cmykjpeg.pdf");
        using var doc = PdfDocument.Open(File.ReadAllBytes(path));
        var page = doc.Pages[0];
        var image = PageInterpreter.Build(doc, page).Images[0];
        var plain = PdfImageDecoder.Decode(image, doc)!;
        var stream = image.Stream!;
        stream.Dictionary.Set(PdfName.Get("Decode"), new PdfArray([.. Enumerable.Range(0, 8).Select(i => (PdfObject)PdfInteger.Of(i % 2))]));
        var withDecode = PdfImageDecoder.Decode(image, doc)!;
        Assert.Equal((plain.Width, plain.Height), (withDecode.Width, withDecode.Height));
        Assert.Equal(plain.Rgba, withDecode.Rgba);
    }

    [TestPdfsFact]
    public void CmykJpeg_RawValuesAreInk_DecodeOneZeroInverts()
    {
        // Skia reads CMYK JPEGs as inverted (convention of common image editors); in PDF the raw value is the amount of ink, otherwise the sky at the top
        // of the photo turns black. With /Decode [1 0 …] the inversion applies (this is how a widely used PDF producer writes inverted JPEGs)
        var path = Path.Combine(Root(), "TestPDFs", "pdfjs", "cmykjpeg.pdf");
        using var doc = PdfDocument.Open(File.ReadAllBytes(path));
        var image = PageInterpreter.Build(doc, doc.Pages[0]).Images[0];
        Assert.True(TopBrightness(PdfImageDecoder.Decode(image, doc)!) > 170);
        image.Stream!.Dictionary.Set(PdfName.Get("Decode"), new PdfArray([.. Enumerable.Range(0, 8).Select(i => (PdfObject)PdfInteger.Of(1 - i % 2))]));
        Assert.True(TopBrightness(PdfImageDecoder.Decode(image, doc)!) < 100);
    }

    /// <summary>Mean brightness of the top ten rows (sky).</summary>
    private static double TopBrightness(DecodedImage image)
    {
        long sum = 0;
        for (var i = 0; i < image.Width * 10; i++) sum += image.Pixels[4 * i] + image.Pixels[4 * i + 1] + image.Pixels[4 * i + 2];
        return sum / (3.0 * image.Width * 10);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }

    // ---- 7.4 E-02: rendering intent and black point compensation (ISO 32000-2, 8.6.5.8, 8.6.5.9; docs/FEHLERGEDAECHTNIS.md, No. 572)

    /// <summary>A CMYK profile with two tables: A2B0 (perceptual) from one formula, A2B1 (relative colorimetric) from the other.</summary>
    private static byte[] TwoIntents(byte[] perceptual, byte[] relative)
    {
        // both baked the same way: header, tag table (wtpt, A2B0), wtpt, lut16 at the same place; the second lut appended as A2B1
        var lutAt = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(perceptual.AsSpan(148));
        var lutSize = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(perceptual.AsSpan(152));
        var size = perceptual.Length + 12 + lutSize;
        var d = new byte[size];
        // header and two tags as they are, a third tag entry inserted: everything after the tag table moves by 12
        perceptual.AsSpan(0, 156).CopyTo(d);
        perceptual.AsSpan(156).CopyTo(d.AsSpan(168));
        void U32(int at, int v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(at), (uint)v);
        U32(0, size);
        U32(128, 3);
        U32(136, (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(perceptual.AsSpan(136)) + 12);   // wtpt
        U32(148, lutAt + 12);                                                                                      // A2B0
        U32(156, 0x41324231); U32(160, perceptual.Length + 12); U32(164, lutSize);                                 // A2B1
        relative.AsSpan(lutAt, lutSize).CopyTo(d.AsSpan(perceptual.Length + 12));
        return d;
    }

    /// <summary>Perceptual: cyan and magenta swapped; relative colorimetric: the plain formula.</summary>
    private static readonly Lazy<byte[]> SwappedAndPlain = new(() => TwoIntents(
        CmykProfile.Bake((c, m, y, k) => ((1 - m) * (1 - k), (1 - c) * (1 - k), (1 - y) * (1 - k))), NaiveCmyk.Value));

    /// <summary>A CMYK profile whose black is only a dark grey (20 %): black point compensation pulls it down to black.</summary>
    private static readonly Lazy<byte[]> LightBlack = new(() =>
        CmykProfile.Bake((c, m, y, k) => (0.2 + 0.8 * (1 - c) * (1 - k), 0.2 + 0.8 * (1 - m) * (1 - k), 0.2 + 0.8 * (1 - y) * (1 - k))));

    private static ColorSpace IccCmyk(byte[] profile)
    {
        using var doc = EmptyDoc();
        return ColorSpace.Create(new PdfArray([TestPdf.N("ICCBased"), new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), profile)]), null, doc);
    }

    /// <summary>
    /// A JPEG 2000 image without /ColorSpace takes the ICC profile of its JP2 wrapper — and the rendering intent of the image with it
    /// (docs/FEHLERGEDAECHTNIS.md, No. 592): /Intent /Perceptual uses A2B0, which swaps cyan and magenta, so magenta comes out cyan; relative
    /// colorimetric stays magenta. Before, the wrapper's space was taken as it was.
    /// </summary>
    [Fact]
    public void JpxWithoutColorSpace_FollowsTheRenderingIntent()
    {
        using var doc = EmptyDoc();
        var perceptual = PdfImageDecoder.JpxSpace(null, 4, SwappedAndPlain.Value, doc, ColorSpace.Perceptual, ColorSpace.BpcDefault);
        Assert.True(IsCyan(perceptual.ToRgb([0, 1, 0, 0])), $"{perceptual.ToRgb([0, 1, 0, 0])}");
        var relative = PdfImageDecoder.JpxSpace(null, 4, SwappedAndPlain.Value, doc, ColorSpace.RelativeColorimetric, ColorSpace.BpcDefault);
        Assert.True(IsMagenta(relative.ToRgb([0, 1, 0, 0])), $"{relative.ToRgb([0, 1, 0, 0])}");
    }

    private static bool IsMagenta(Rgb c) => c is { R: > 230, G: < 25, B: > 230 };
    private static bool IsCyan(Rgb c) => c is { R: < 25, G: > 230, B: > 230 };

    [Fact]
    public void PerceptualIntent_UsesTheA2B0Table()
    {
        var space = IccCmyk(SwappedAndPlain.Value);
        Assert.True(IsMagenta(space.ToRgb([0, 1, 0, 0])), space.ToRgb([0, 1, 0, 0]).ToString());   // relative colorimetric (A2B1) by default
        var perceptual = space.WithRendering(ColorSpace.Perceptual, ColorSpace.BpcDefault);
        Assert.True(IsCyan(perceptual.ToRgb([0, 1, 0, 0])), perceptual.ToRgb([0, 1, 0, 0]).ToString());
        // saturation without A2B2 falls back to A2B0, absolute colorimetric uses A2B1; one variant per combination, shared
        Assert.True(IsCyan(space.WithRendering(ColorSpace.Saturation, ColorSpace.BpcDefault).ToRgb([0, 1, 0, 0])));
        Assert.True(IsMagenta(space.WithRendering(ColorSpace.AbsoluteColorimetric, ColorSpace.BpcDefault).ToRgb([0, 1, 0, 0])));
        Assert.Same(perceptual, space.WithRendering(ColorSpace.Perceptual, ColorSpace.BpcOn));
        Assert.Same(space, space.WithRendering(ColorSpace.RelativeColorimetric, ColorSpace.BpcOn));
        // through the content: ri and /RI (Indexed passes it to its base)
        foreach (var (content, states) in new[] { ("/Perceptual ri", ""), ("/P gs", "/RI /Perceptual") })
        {
            var (doc, list) = InterpreterTests.Page($"/CS0 cs 0 1 0 0 sc 0 0 100 200 re f {content} 0 1 0 0 sc 100 0 100 200 re f /CS1 cs 1 sc 100 0 100 50 re f", pdf =>
            {
                var icc = new PdfArray([TestPdf.N("ICCBased"), TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), SwappedAndPlain.Value)))]);
                var indexed = new PdfArray([TestPdf.N("Indexed"), icc, PdfInteger.Of(1), new PdfString([0, 0, 0, 0, 0, 255, 0, 0])]);
                var resources = TestPdf.Dict(("ColorSpace", TestPdf.Dict(("CS0", icc), ("CS1", indexed))));
                if (states.Length > 0) resources.Set(TestPdf.N("ExtGState"), TestPdf.Dict(("P", TestPdf.Dict(("RI", TestPdf.N("Perceptual"))))));
                return resources;
            });
            using (doc)
            {
                using var b = PdfKnead.Rendering.SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new PdfKnead.Rendering.ImageCache());
                Rgb At(int x, int y) { var p = b.GetPixel(x, b.Height - 1 - y); return new Rgb(p.Red, p.Green, p.Blue); }
                Assert.True(IsMagenta(At(50, 100)), $"{content}: links {At(50, 100)}");
                Assert.True(IsCyan(At(150, 100)), $"{content}: rechts {At(150, 100)}");
                Assert.True(IsCyan(At(150, 25)), $"{content}: Indexed {At(150, 25)}");
            }
        }
    }

    [Fact]
    public void UseBlackPtCompOff_KeepsTheBlackOfTheSource()
    {
        var space = IccCmyk(LightBlack.Value);
        var compensated = space.ToRgb([0, 0, 0, 1]);
        Assert.True(compensated.R < 12, compensated.ToString());   // by default (and with ON) the black point is compensated
        var kept = space.WithRendering(ColorSpace.RelativeColorimetric, ColorSpace.BpcOff).ToRgb([0, 0, 0, 1]);
        Assert.InRange(kept.R, 40, 62);                              // 20 % grey of the source
        Assert.True(space.WithRendering(ColorSpace.RelativeColorimetric, ColorSpace.BpcOn).ToRgb([0, 0, 0, 1]).R < 12);
        // through /UseBlackPtComp in a graphics state; white stays white either way
        var (doc, list) = InterpreterTests.Page("/CS0 cs 0 0 0 1 sc 0 0 100 200 re f /Off gs 0 0 0 1 sc 100 0 100 200 re f", pdf => TestPdf.Dict(
            ("ColorSpace", TestPdf.Dict(("CS0", new PdfArray([TestPdf.N("ICCBased"), TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), LightBlack.Value)))])))),
            ("ExtGState", TestPdf.Dict(("Off", TestPdf.Dict(("UseBlackPtComp", TestPdf.N("OFF"))))))));
        using (doc)
        {
            using var b = PdfKnead.Rendering.SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new PdfKnead.Rendering.ImageCache());
            Assert.True(b.GetPixel(50, 100).Red < 12, b.GetPixel(50, 100).ToString());
            Assert.InRange(b.GetPixel(150, 100).Red, (byte)40, (byte)62);
        }
    }

    [Fact]
    public void AbsoluteColorimetric_TurnsBpcOff()
    {
        var space = IccCmyk(LightBlack.Value);
        Assert.InRange(space.WithRendering(ColorSpace.AbsoluteColorimetric, ColorSpace.BpcOn).ToRgb([0, 0, 0, 1]).R, 40, 62);
        Assert.InRange(space.WithRendering(ColorSpace.AbsoluteColorimetric, ColorSpace.BpcDefault).ToRgb([0, 0, 0, 1]).R, 40, 62);
    }

    /// <summary>
    /// DeviceCMYK without an output profile is a device space — the intent does not apply (8.6.5.8: only CIE-based conversions); with an
    /// output profile of the document it is characterised by that profile and reacts.
    /// </summary>
    [Fact]
    public void DeviceCmyk_IntentOnlyThroughAnOutputProfile()
    {
        using (var doc = EmptyDoc())
        {
            var cmyk = ColorSpace.DeviceCmykOf(doc);
            Assert.Same(cmyk, cmyk.WithRendering(ColorSpace.Perceptual, ColorSpace.BpcOff));
        }
        var pdf = TestPdf.Simple(1);
        var profile = pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), SwappedAndPlain.Value));
        var intent = TestPdf.Dict(("Type", TestPdf.N("OutputIntent")), ("S", TestPdf.N("GTS_PDFX")), ("DestOutputProfile", TestPdf.Ref(profile)));
        ((PdfDictionary)pdf.Get(pdf.RootNumber)).Set(PdfName.Get("OutputIntents"), new PdfArray([intent]));
        using var withIntent = PdfDocument.Open(pdf.Build());
        var device = ColorSpace.DeviceCmykOf(withIntent);
        Assert.True(IsMagenta(device.ToRgb([0, 1, 0, 0])));
        var perceptual = device.WithRendering(ColorSpace.Perceptual, ColorSpace.BpcDefault);
        Assert.True(IsCyan(perceptual.ToRgb([0, 1, 0, 0])));
        Assert.True(perceptual.IsDeviceCmyk);
    }

    // ---- 7.4 E-02 for images and shadings (ISO 32000-2, 8.9.5, 8.6.5.8; docs/FEHLERGEDAECHTNIS.md, No. 573)

    private static Rgb PixelAt(SkiaSharp.SKBitmap b, int x, int y)
    {
        var p = b.GetPixel(x, b.Height - 1 - y);
        return new Rgb(p.Red, p.Green, p.Blue);
    }

    /// <summary>
    /// A magenta 1×1 image in the ICC space whose A2B0 swaps cyan and magenta: /Intent /Perceptual at the image gives cyan; /Intent
    /// /RelativeColorimetric at the image wins over ri /Perceptual of the state; one image drawn with both intents keeps two entries in the
    /// image cache. A shading in the same space follows ri.
    /// </summary>
    [Fact]
    public void ImageIntentAndShadings_FollowTheRenderingIntent()
    {
        var (doc, list) = InterpreterTests.Page(
            "q 100 0 0 100 0 0 cm /Plain Do Q q 100 0 0 100 100 0 cm /Own Do Q /Perceptual ri q 100 0 0 50 0 100 cm /Plain Do Q " +
            "q 100 0 0 50 0 150 cm /Relative Do Q q 100 100 100 100 re W n /Sh sh Q", pdf =>
        {
            var icc = new PdfArray([TestPdf.N("ICCBased"), TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), SwappedAndPlain.Value)))]);
            PdfObject Image(string? intent)
            {
                var dict = TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(1)), ("Height", PdfInteger.Of(1)),
                    ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", icc));
                if (intent is not null) dict.Set(TestPdf.N("Intent"), TestPdf.N(intent));
                return TestPdf.Ref(pdf.Add(new PdfStream(dict, [0, 255, 0, 0])));
            }
            var plain = Image(null);
            var shading = TestPdf.Dict(("ShadingType", PdfInteger.Of(2)), ("ColorSpace", icc), ("Coords", TestPdf.Box(100, 0, 200, 0)),
                ("Function", TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)), ("C0", TestPdf.Box(0, 1, 0, 0)),
                    ("C1", TestPdf.Box(0, 1, 0, 0)), ("N", PdfInteger.Of(1)))), ("Extend", new PdfArray([PdfBoolean.Of(true), PdfBoolean.Of(true)])));
            return TestPdf.Dict(("XObject", TestPdf.Dict(("Plain", plain), ("Own", Image("Perceptual")), ("Relative", Image("RelativeColorimetric")))),
                ("Shading", TestPdf.Dict(("Sh", shading))));
        });
        using (doc)
        {
            using var b = PdfKnead.Rendering.SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new PdfKnead.Rendering.ImageCache());
            Assert.True(IsMagenta(PixelAt(b, 50, 50)), $"ohne Absicht {PixelAt(b, 50, 50)}");
            Assert.True(IsCyan(PixelAt(b, 150, 50)), $"/Intent am Bild {PixelAt(b, 150, 50)}");
            Assert.True(IsCyan(PixelAt(b, 50, 125)), $"dasselbe Bild unter ri {PixelAt(b, 50, 125)}");
            Assert.True(IsMagenta(PixelAt(b, 50, 175)), $"/Intent am Bild vor ri {PixelAt(b, 50, 175)}");
            Assert.True(IsCyan(PixelAt(b, 150, 150)), $"Verlauf unter ri {PixelAt(b, 150, 150)}");
        }
    }

    /// <summary>
    /// E-14: a shading pattern whose /ExtGState sets /RI /Perceptual, /LW 50 and /ca 0.2: the intent applies to the shading (cyan instead of
    /// magenta), line width and opacity do not (ISO 32000-2, 11.6.7, Table 75; docs/FEHLERGEDAECHTNIS.md, No. 574) — the fill is opaque, a
    /// stroke with the pattern stays 2 wide.
    /// </summary>
    [Fact]
    public void ShadingPatternExtGState_LineWidthIgnored_IntentUsed()
    {
        var (doc, list) = InterpreterTests.Page("/Pattern cs /P scn 0 0 100 200 re f 2 w /Pattern CS /P SCN 120 20 m 180 20 l S", pdf =>
        {
            var icc = new PdfArray([TestPdf.N("ICCBased"), TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), SwappedAndPlain.Value)))]);
            var shading = TestPdf.Dict(("ShadingType", PdfInteger.Of(2)), ("ColorSpace", icc), ("Coords", TestPdf.Box(0, 0, 200, 0)),
                ("Function", TestPdf.Dict(("FunctionType", PdfInteger.Of(2)), ("Domain", TestPdf.Box(0, 1)), ("C0", TestPdf.Box(0, 1, 0, 0)),
                    ("C1", TestPdf.Box(0, 1, 0, 0)), ("N", PdfInteger.Of(1)))), ("Extend", new PdfArray([PdfBoolean.Of(true), PdfBoolean.Of(true)])));
            var pattern = TestPdf.Dict(("PatternType", PdfInteger.Of(2)), ("Shading", shading),
                ("ExtGState", TestPdf.Dict(("RI", TestPdf.N("Perceptual")), ("LW", PdfInteger.Of(50)), ("ca", new PdfReal(0.2)))));
            return TestPdf.Dict(("Pattern", TestPdf.Dict(("P", TestPdf.Ref(pdf.Add(pattern))))));
        });
        using (doc)
        {
            using var b = PdfKnead.Rendering.SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, new PdfKnead.Rendering.ImageCache());
            Assert.True(IsCyan(PixelAt(b, 50, 100)), $"Fläche {PixelAt(b, 50, 100)}");
            Assert.True(IsCyan(PixelAt(b, 150, 20)), $"Linie {PixelAt(b, 150, 20)}");
            Assert.Equal(new Rgb(255, 255, 255), PixelAt(b, 150, 40));   // 20 above the line: a width of 50 would reach here
        }
    }

    // ---- 7.4 E-03: output intents of a page (ISO 32000-2, 14.11.5, Tables 401 and 402; docs/FEHLERGEDAECHTNIS.md, No. 575)

    /// <summary>A CMYK profile with cyan and magenta swapped (one table).</summary>
    private static readonly Lazy<byte[]> Swapped = new(() => CmykProfile.Bake((c, m, y, k) => ((1 - m) * (1 - k), (1 - c) * (1 - k), (1 - y) * (1 - k))));

    /// <summary>
    /// Catalog output intent with the plain profile; page 1 has its own with the swapped profile (/DestOutputProfile), page 2 the same through
    /// /DestOutputProfileRef with an embedded file, page 3 none. DeviceCMYK fill and DeviceCMYK image follow the page: cyan, cyan, magenta.
    /// Before: the catalog's everywhere.
    /// </summary>
    [Fact]
    public void PageOutputIntent_TakesPrecedenceOverTheCatalog()
    {
        var pdf = new TestPdf();
        var catalog = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog"))));
        var pagesNode = pdf.Add(new PdfDictionary());
        PdfDictionary Intent(string key, PdfObject value) => TestPdf.Dict(("Type", TestPdf.N("OutputIntent")), ("S", TestPdf.N("GTS_PDFX")), (key, value));
        var image = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(1)),
            ("Height", PdfInteger.Of(1)), ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", TestPdf.N("DeviceCMYK"))), [0, 255, 0, 0]));
        var swapped = pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), Swapped.Value));
        var embedded = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile"))), Swapped.Value));
        var reference = TestPdf.Dict(("URLs", new PdfArray([TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("F", new PdfString("swapped.icc"u8.ToArray())),
            ("EF", TestPdf.Dict(("F", TestPdf.Ref(embedded))))), new PdfString("https://example.invalid/profil.icc"u8.ToArray())])));
        PdfDictionary?[] own = [Intent("DestOutputProfile", TestPdf.Ref(swapped)), Intent("DestOutputProfileRef", reference), null];
        var kids = new PdfArray();
        foreach (var intent in own)
        {
            var content = pdf.Add(TestPdf.FlateStream("0 1 0 0 k 0 0 100 100 re f q 100 0 0 100 100 0 cm /Im Do Q"u8.ToArray()));
            var page = TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pagesNode)), ("Contents", TestPdf.Ref(content)),
                ("MediaBox", TestPdf.Box(0, 0, 200, 100)), ("Resources", TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(image)))))));
            if (intent is not null) page.Set(TestPdf.N("OutputIntents"), new PdfArray([intent]));
            kids.Add(TestPdf.Ref(pdf.Add(page)));
        }
        pdf.Replace(pagesNode, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", kids), ("Count", PdfInteger.Of(3))));
        var plain = pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), NaiveCmyk.Value));
        pdf.Replace(catalog, TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pagesNode)),
            ("OutputIntents", new PdfArray([Intent("DestOutputProfile", TestPdf.Ref(plain))]))));
        pdf.RootNumber = catalog;
        using var doc = PdfDocument.Open(pdf.Build());
        var cache = new PdfKnead.Rendering.ImageCache();
        bool[] cyan = [true, true, false];
        for (var i = 0; i < 3; i++)
        {
            var list = Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[i]);
            using var b = PdfKnead.Rendering.SkiaRenderer.RenderPage(list, doc, doc.Pages[i], 1, cache);
            foreach (var (x, what) in new[] { (50, "Fläche"), (150, "Bild") })
            {
                var c = PixelAt(b, x, 50);
                Assert.True(cyan[i] ? IsCyan(c) : IsMagenta(c), $"Seite {i + 1}, {what}: {c}");
            }
        }
    }

    /// <summary>
    /// 7.11.3: readers take /UF before /F — also for the embedded profile of a /DestOutputProfileRef: page 1 has it only under /UF, page 2 the
    /// plain profile under /F and the swapped one under /UF; both take the swapped one (cyan). Before, /F only — and a missing /F gave the null
    /// object, so /UF was never looked at: magenta from the catalogue on both (docs/FEHLERGEDAECHTNIS.md, No. 789).
    /// </summary>
    [Fact]
    public void PageOutputIntent_EmbeddedProfileUnderUf_TakenFirst()
    {
        var pdf = new TestPdf();
        var catalog = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog"))));
        var pagesNode = pdf.Add(new PdfDictionary());
        var swapped = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile"))), Swapped.Value));
        var plainFile = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("EmbeddedFile"))), NaiveCmyk.Value));
        PdfDictionary Reference(PdfDictionary files) => TestPdf.Dict(("Type", TestPdf.N("OutputIntent")), ("S", TestPdf.N("GTS_PDFX")),
            ("DestOutputProfileRef", TestPdf.Dict(("URLs", new PdfArray([TestPdf.Dict(("Type", TestPdf.N("Filespec")), ("UF", new PdfString("swapped.icc"u8.ToArray())), ("EF", files))])))));
        PdfDictionary[] intents = [Reference(TestPdf.Dict(("UF", TestPdf.Ref(swapped)))), Reference(TestPdf.Dict(("F", TestPdf.Ref(plainFile)), ("UF", TestPdf.Ref(swapped))))];
        var kids = new PdfArray();
        foreach (var intent in intents)
        {
            var content = pdf.Add(TestPdf.FlateStream("0 1 0 0 k 0 0 100 100 re f"u8.ToArray()));
            kids.Add(TestPdf.Ref(pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pagesNode)), ("Contents", TestPdf.Ref(content)),
                ("MediaBox", TestPdf.Box(0, 0, 100, 100)), ("OutputIntents", new PdfArray([intent]))))));
        }
        pdf.Replace(pagesNode, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", kids), ("Count", PdfInteger.Of(2))));
        var plain = pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), NaiveCmyk.Value));
        pdf.Replace(catalog, TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pagesNode)),
            ("OutputIntents", new PdfArray([TestPdf.Dict(("Type", TestPdf.N("OutputIntent")), ("S", TestPdf.N("GTS_PDFX")), ("DestOutputProfile", TestPdf.Ref(plain)))]))));
        pdf.RootNumber = catalog;
        using var doc = PdfDocument.Open(pdf.Build());
        var cache = new PdfKnead.Rendering.ImageCache();
        for (var i = 0; i < 2; i++)
        {
            var list = Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[i]);
            using var b = PdfKnead.Rendering.SkiaRenderer.RenderPage(list, doc, doc.Pages[i], 1, cache);
            var c = PixelAt(b, 50, 50);
            Assert.True(IsCyan(c), $"Seite {i + 1}: {c}");
        }
    }

    /// <summary>
    /// The output intent of a page also holds where other interpreters draw (docs/FEHLERGEDAECHTNIS.md, No. 593): a DeviceCMYK image in the
    /// cell of a tiling pattern, and an annotation drawn over the page (as edited ones are) with a DeviceCMYK fill and image — all cyan with
    /// the swapped profile of the page. Before, they took the catalog's profile: magenta.
    /// </summary>
    [Fact]
    public void PageOutputIntent_HoldsInPatternCellsAndAnnotations()
    {
        var pdf = new TestPdf();
        var catalog = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog"))));
        var pagesNode = pdf.Add(new PdfDictionary());
        PdfDictionary Intent(PdfObject profile) => TestPdf.Dict(("Type", TestPdf.N("OutputIntent")), ("S", TestPdf.N("GTS_PDFX")), ("DestOutputProfile", profile));
        var image = pdf.Add(new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(1)),
            ("Height", PdfInteger.Of(1)), ("BitsPerComponent", PdfInteger.Of(8)), ("ColorSpace", TestPdf.N("DeviceCMYK"))), [0, 255, 0, 0]));
        var pattern = pdf.Add(new PdfStream(TestPdf.Dict(("PatternType", PdfInteger.Of(1)), ("PaintType", PdfInteger.Of(1)), ("TilingType", PdfInteger.Of(1)),
            ("BBox", TestPdf.Box(0, 0, 100, 100)), ("XStep", PdfInteger.Of(100)), ("YStep", PdfInteger.Of(100)),
            ("Resources", TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(image))))))), "q 100 0 0 100 0 0 cm /Im Do Q"u8.ToArray()));
        var content = pdf.Add(TestPdf.FlateStream("/Pattern cs /P scn 0 0 100 100 re f"u8.ToArray()));
        var swapped = pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), Swapped.Value));
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pagesNode)), ("Contents", TestPdf.Ref(content)),
            ("MediaBox", TestPdf.Box(0, 0, 300, 100)), ("OutputIntents", new PdfArray([Intent(TestPdf.Ref(swapped))])),
            ("Resources", TestPdf.Dict(("Pattern", TestPdf.Dict(("P", TestPdf.Ref(pattern))))))));
        pdf.Replace(pagesNode, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        var plain = pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(4))), NaiveCmyk.Value));
        pdf.Replace(catalog, TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pagesNode)), ("OutputIntents", new PdfArray([Intent(TestPdf.Ref(plain))]))));
        pdf.RootNumber = catalog;
        using var doc = PdfDocument.Open(pdf.Build());
        var cache = new PdfKnead.Rendering.ImageCache();
        var list = Core.Pdf.Content.PageInterpreter.Build(doc, doc.Pages[0]);
        using var b = PdfKnead.Rendering.SkiaRenderer.RenderPage(list, doc, doc.Pages[0], 1, cache);
        Assert.True(IsCyan(PixelAt(b, 50, 50)), $"Musterzelle: {PixelAt(b, 50, 50)}");

        // an annotation drawn over the page: fill at 100–200, image at 200–300
        var appearance = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 200, 100)),
            ("Resources", TestPdf.Dict(("XObject", TestPdf.Dict(("Im", doc.GetObject(image)!)))))), "0 1 0 0 k 0 0 100 100 re f q 100 0 0 100 100 0 cm /Im Do Q"u8.ToArray());
        var annot = TestPdf.Dict(("Type", TestPdf.N("Annot")), ("Subtype", TestPdf.N("Square")), ("Rect", TestPdf.Box(100, 0, 300, 100)),
            ("AP", TestPdf.Dict(("N", appearance))));
        var overlay = Core.Pdf.Content.PageInterpreter.BuildAnnotations(doc, doc.Pages[0], [annot]);
        using var o = PdfKnead.Rendering.SkiaRenderer.RenderPage(overlay, doc, doc.Pages[0], 1, cache);
        Assert.True(IsCyan(PixelAt(o, 150, 50)), $"Anmerkung, Fläche: {PixelAt(o, 150, 50)}");
        Assert.True(IsCyan(PixelAt(o, 250, 50)), $"Anmerkung, Bild: {PixelAt(o, 250, 50)}");
    }

    // ---- M6 security: colour tables of ICC profiles within the budget on every way (docs/FEHLERGEDAECHTNIS.md, No. 1122)

    /// <summary>The baked CMYK profile with A2B1 and A2B2 naming the very bytes of its A2B0 (as many real profiles and the bundled one do).</summary>
    private static byte[] ThreeTagsOneTable(byte[] baked)
    {
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(baked.AsSpan(128));
        var d = new byte[baked.Length + 24];
        baked.AsSpan(0, 132 + 12 * count).CopyTo(d);
        baked.AsSpan(132 + 12 * count).CopyTo(d.AsSpan(132 + 12 * count + 24));
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(at), v);
        U32(0, (uint)d.Length);
        U32(128, (uint)(count + 2));
        (uint Offset, uint Size) lut = default;
        for (var i = 0; i < count; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32BigEndian(baked.AsSpan(136 + 12 * i)) + 24;
            U32(136 + 12 * i, offset);
            if (BinaryPrimitives.ReadUInt32BigEndian(baked.AsSpan(132 + 12 * i)) == 0x41324230)
                lut = (offset, BinaryPrimitives.ReadUInt32BigEndian(baked.AsSpan(140 + 12 * i)));
        }
        U32(132 + 12 * count, 0x41324231); U32(136 + 12 * count, lut.Offset); U32(140 + 12 * count, lut.Size);   // A2B1
        U32(144 + 12 * count, 0x41324232); U32(148 + 12 * count, lut.Offset); U32(152 + 12 * count, lut.Size);   // A2B2
        return d;
    }

    /// <summary>
    /// A2B0, A2B1 and A2B2 naming the same bytes (ICC.1, 7.3.1 allows it): the table is read once and counted once — before, it was built three
    /// times and counted three times, a profile of 32 MB took about 384 MB before any budget was asked.
    /// </summary>
    [Fact]
    public void SameTableForThreeIntents_ReadAndCountedOnce()
    {
        var one = IccProfile.Parse(NaiveCmyk.Value)!;
        var three = IccProfile.Parse(ThreeTagsOneTable(NaiveCmyk.Value))!;
        Assert.Equal(one.TableEntries, three.TableEntries);
        var input = ThreeTagsOneTable(NaiveCmyk.Value);
        IccProfile.Parse(NaiveCmyk.Value);   // warm
        var before = GC.GetAllocatedBytesForCurrentThread();
        IccProfile.Parse(input);
        var shared = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        IccProfile.Parse(NaiveCmyk.Value);
        Assert.True(shared < 1.5 * (GC.GetAllocatedBytesForCurrentThread() - before), "Tabelle mehrfach angelegt");
    }

    /// <summary>A matrix profile whose three tone curves are one curveType table of a million entries (all three tags name its bytes).</summary>
    private static byte[] HugeCurveProfile(int entries)
    {
        var profile = MatrixProfile(SrgbColumns, 2.2f);
        var curve = new byte[12 + 2 * entries];
        BinaryPrimitives.WriteUInt32BigEndian(curve, 0x63757276);
        BinaryPrimitives.WriteUInt32BigEndian(curve.AsSpan(8), (uint)entries);
        for (var i = 0; i < entries; i++)
            BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(12 + 2 * i), (ushort)Math.Round(Math.Pow((double)i / (entries - 1), 2.2) * 65535));
        var d = new byte[profile.Length + curve.Length];
        profile.CopyTo(d, 0);
        curve.CopyTo(d, profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(d, (uint)d.Length);
        for (var i = 0; i < 6; i++)
            if (BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(132 + 12 * i)) is 0x72545243 or 0x67545243 or 0x62545243)   // rTRC, gTRC, bTRC
            {
                BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(136 + 12 * i), (uint)profile.Length);
                BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(140 + 12 * i), (uint)curve.Length);
            }
        return d;
    }

    /// <summary>
    /// A curveType table of a million entries named by rTRC, gTRC and bTRC: kept resampled onto 4 096 points and once — before, three tables of a
    /// million floats each, outside every budget (matrix profiles were not reserved at all); the colours stay those of the curve.
    /// </summary>
    [Fact]
    public void HugeToneCurve_ResampledOnce_ColoursKept()
    {
        var data = HugeCurveProfile(1_000_000);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var huge = IccProfile.Parse(data)!;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1 << 20, $"{allocated} Byte beim Lesen");   // before: three tables of a million floats (12 MB)
        Assert.InRange(huge.TableEntries, 1, 4096);   // counted (before: tone curves never were), and kept small
        var gamma = IccProfile.Parse(MatrixProfile(SrgbColumns, 2.2f))!;
        Span<float> a = stackalloc float[3], b = stackalloc float[3];
        foreach (var v in new[] { 0.1f, 0.5f, 0.9f })
        {
            huge.ToXyz([v, v, v], a);
            gamma.ToXyz([v, v, v], b);
            for (var i = 0; i < 3; i++) Assert.True(Math.Abs(a[i] - b[i]) < 0.002, $"{v}: {a[i]} / {b[i]}");
        }
    }

    /// <summary>
    /// The profile of a JPEG 2000 image within the document's budget and once per content: over the budget it is not used (the image is drawn
    /// by its channels), within it the same colour space comes back for every decoding, reserved once — before, every decoding read and kept
    /// the profile outside any budget. An ICCBased matrix profile counts too.
    /// </summary>
    [Fact]
    public void EmbeddedProfiles_WithinTheBudget_ReadOncePerDocument()
    {
        var huge = HugeCurveProfile(1_000_000);
        var entries = IccProfile.Parse(huge)!.TableEntries;
        using (var tight = PdfDocument.Open(TestPdf.Simple().Build()))
        {
            tight.IccTableBudget = entries - 1;
            Assert.Null(ColorSpace.FromProfile(huge, 3, tight));
        }
        using var doc = PdfDocument.Open(TestPdf.Simple().Build());
        doc.IccTableBudget = entries;
        var first = ColorSpace.FromProfile(huge, 3, doc);
        Assert.NotNull(first);
        Assert.Same(first, ColorSpace.FromProfile(huge, 3, doc));
        // the budget is spent: an ICCBased stream with another copy of a table profile falls back to its alternate
        var pdf = TestPdf.Simple();
        var other = pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(3)), ("Alternate", TestPdf.N("DeviceGray"))), HugeCurveProfile(999_999)));
        using var file = PdfDocument.Open(pdf.Build());
        file.IccTableBudget = 100;
        Assert.Same(ColorSpace.DeviceGray, ColorSpace.Create(new PdfArray { TestPdf.N("ICCBased"), file.GetObject(other) }, null, file));
    }
}
