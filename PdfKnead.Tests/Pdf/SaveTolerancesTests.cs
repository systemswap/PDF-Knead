using System.Text;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Editing;
using PdfKnead.Core.Pdf.Filters;
using PdfKnead.Core.Pdf.Filters.Jbig2;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Reading tolerances of M2 written as ISO 32000-2 asks when a copy is saved, where that is possible without loss (decided on
/// 20.11.2026; docs/normabgleich/sonderregeln.md, question 2 for S-A; docs/FEHLERGEDAECHTNIS.md, No. 992, 993): the page looks the same before
/// and after, pixel for pixel, and the image data stay as stored.
/// </summary>
public sealed class SaveTolerancesTests
{
    private static byte[] Save(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        using var output = new MemoryStream();
        PdfWriter.Write(doc, PageItem.From(doc), output, new PdfSaveOptions());
        return output.ToArray();
    }

    /// <summary>A one-page file (300 × 300) with the content given and the XObjects given in its resources.</summary>
    private static byte[] Page(byte[] content, params (string Name, PdfObject XObject)[] xobjects) => Page([content], xobjects);

    /// <summary>A one-page file (300 × 300) whose content is in the parts given (/Contents an array if more than one).</summary>
    private static byte[] Page(byte[][] parts, params (string Name, PdfObject XObject)[] xobjects)
    {
        var pdf = new TestPdf();
        var resources = new PdfDictionary();
        if (xobjects.Length > 0)
        {
            var dict = new PdfDictionary();
            foreach (var (name, x) in xobjects) dict.Set(PdfName.Get(name), x is PdfStream s ? TestPdf.Ref(pdf.Add(s)) : x);
            resources.Set(PdfName.Get("XObject"), dict);
        }
        var numbers = parts.Select(p => pdf.Add(new PdfStream(new PdfDictionary(), p))).ToList();
        PdfObject contents = numbers.Count == 1 ? TestPdf.Ref(numbers[0]) : new PdfArray([.. numbers.Select(n => (PdfObject)TestPdf.Ref(n))]);
        var pages = pdf.Add(new PdfDictionary());
        var page = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Page")), ("Parent", TestPdf.Ref(pages)), ("MediaBox", TestPdf.Box(0, 0, 300, 300)),
            ("Resources", resources), ("Contents", contents)));
        pdf.Replace(pages, TestPdf.Dict(("Type", TestPdf.N("Pages")), ("Kids", new PdfArray([TestPdf.Ref(page)])), ("Count", PdfInteger.Of(1))));
        pdf.RootNumber = pdf.Add(TestPdf.Dict(("Type", TestPdf.N("Catalog")), ("Pages", TestPdf.Ref(pages))));
        return pdf.Build();
    }

    private static byte[] Bytes(params object[] parts) =>
        [.. parts.SelectMany(p => p is string s ? Encoding.Latin1.GetBytes(s) : (byte[])p)];

    /// <summary>Every pixel of the drawn first page alike.</summary>
    private static void LooksTheSame(byte[] before, byte[] after)
    {
        using var a = PdfDocument.Open(before);
        using var b = PdfDocument.Open(after);
        using var pa = ContentImageTests.Render(a, a.Pages[0]);
        using var pb = ContentImageTests.Render(b, b.Pages[0]);
        Assert.Equal((pa.Width, pa.Height), (pb.Width, pb.Height));
        var differing = 0;
        for (var y = 0; y < pa.Height; y++)
            for (var x = 0; x < pa.Width; x++)
                if (pa.GetPixel(x, y) != pb.GetPixel(x, y)) differing++;
        Assert.True(differing == 0, $"{differing} Bildpunkte anders");
    }

    private static string Content(PdfDocument doc, PdfPage page) => page.Dictionary.Get(PdfName.Contents) switch
    {
        PdfStream s => Encoding.Latin1.GetString(doc.DecodeStream(s).Data),
        PdfArray parts => string.Concat(Enumerable.Range(0, parts.Count).Select(i => parts.Get(i)).OfType<PdfStream>().Select(s => Encoding.Latin1.GetString(doc.DecodeStream(s).Data))),
        _ => "",
    };

    private static List<PdfStream> Images(PdfDocument doc, PdfDictionary? resources) =>
        resources?.GetDictionary(PdfName.Get("XObject")) is { } x
            ? [.. x.Keys.Select(k => x.Get(k)).OfType<PdfStream>().Where(s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Image")]
            : [];

    /// <summary>The data begin with <paramref name="expected"/>, the rest is white-space at most (the separator before „EI“).</summary>
    private static bool AsRead(byte[] data, byte[] expected) =>
        data.AsSpan().StartsWith(expected) && data.AsSpan(expected.Length).TrimStart(" \t\r\n"u8).IsEmpty;

    /// <summary>A small black-and-white bitmap coded with the own JBIG2 encoder (PDF: 0 = black).</summary>
    private static byte[] Jbig2(int width, int height, out byte[] bits)
    {
        var rowBytes = (width + 7) / 8;
        bits = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                if ((x / 4 + y / 4) % 2 == 0) bits[y * rowBytes + x / 8] |= (byte)(0x80 >> (x % 8));
        return Jbig2Encoder.Generic(bits, width, height, rowBytes);
    }

    /// <summary>ASCII85 of the data (ISO 32000-2, 7.4.3), with or without the end „~>“.</summary>
    private static string Ascii85(byte[] data, bool end)
    {
        var text = new StringBuilder();
        for (var i = 0; i < data.Length; i += 4)
        {
            var n = Math.Min(4, data.Length - i);
            uint v = 0;
            for (var k = 0; k < 4; k++) v = v << 8 | (k < n ? data[i + k] : 0u);
            if (n == 4 && v == 0)
            {
                text.Append('z');
                continue;
            }
            var digits = new char[5];
            for (var k = 4; k >= 0; k--)
            {
                digits[k] = (char)('!' + v % 85);
                v /= 85;
            }
            text.Append(digits, 0, n + 1);
        }
        return text + (end ? "~>" : "~");
    }

    /// <summary>
    /// S-A A86 and A12, A13: an inline image with /JBIG2Decode (not allowed inline, 7.4.7), one with ASCII85 data without „~>“, and one
    /// whose data hold „ EI “ (where it ends is a guess for other readers, 8.9.7) — in the page and in a form without resources of its own.
    /// Saved, each is an image XObject (the form gets the page's resources with it as its own), the content holds no inline image any more,
    /// the image data as stored (ASCII85 decoded), and the page looks the same (FG 993). An inline image that breaks nothing stays inline.
    /// </summary>
    [Fact]
    public void InlineImages_BecomeXObjects_PageLooksTheSame()
    {
        var jbig2 = Jbig2(40, 24, out _);
        var grey = Enumerable.Range(0, 64).Select(i => (byte)(i * 4)).ToArray();
        byte[] withEi = [0x40, 0x20, 0x45, 0x49, 0x20, 0x80, 0xC0, 0xFF];
        byte[] content = Bytes(
            "q 80 0 0 48 20 200 cm BI /W 40 /H 24 /BPC 1 /CS /G /F /JBIG2Decode ID ", jbig2, " EI Q\n",
            "q 40 0 0 40 120 200 cm BI /W 8 /H 8 /BPC 8 /CS /G /F /A85 ID ", Ascii85(grey, end: false), " EI Q\n",
            "q 80 0 0 10 20 150 cm BI /W 8 /H 1 /BPC 8 /CS /G ID ", withEi, " EI Q\n",
            "q 40 0 0 40 200 200 cm BI /W 8 /H 8 /BPC 8 /CS /G /F /AHx ID ", Convert.ToHexString(grey), "> EI Q\n",
            "q 1 0 0 1 0 0 cm /Fm Do Q\n");
        var form = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Form")), ("BBox", TestPdf.Box(0, 0, 300, 300))),
            Bytes("q 80 0 0 48 150 50 cm BI /W 40 /H 24 /BPC 1 /CS /G /F /JBIG2Decode ID ", jbig2, " EI Q\n"));
        var original = Page(content, ("Fm", form));
        var saved = Save(original);
        LooksTheSame(original, saved);

        using var doc = PdfDocument.Open(saved);
        var page = doc.Pages[0];
        var text = Content(doc, page);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"\bBI\b"));   // only the image that breaks nothing (AHx with its end)
        var images = Images(doc, page.Resources);
        Assert.True(images.Count == 3, $"{images.Count} Bilder");
        // filtered data without /L as the parser reads them: up to the white-space before „EI“ (8.9.7), which a decoder passes over
        Assert.Contains(images, s => s.Dictionary.GetName(PdfName.Filter)?.Value == "JBIG2Decode" && AsRead(doc.GetRawStreamData(s), jbig2));
        // the ASCII85 data decoded, no ASCII filter left (packed with Flate as any stream the writer packs)
        Assert.Contains(images, s => s.Dictionary.GetName(PdfName.Filter)?.Value is null or "FlateDecode" && doc.DecodeStream(s).Data.AsSpan().SequenceEqual(grey));
        Assert.Contains(images, s => doc.DecodeStream(s).Data.AsSpan().SequenceEqual(withEi));
        Assert.All(images, s => Assert.Equal("DeviceGray", s.Dictionary.GetName(PdfName.Get("ColorSpace"))?.Value));
        var savedForm = Assert.Single(page.Resources!.GetDictionary(PdfName.Get("XObject"))!.Keys.Select(k => page.Resources.GetDictionary(PdfName.Get("XObject"))!.Get(k))
            .OfType<PdfStream>(), s => s.Dictionary.GetName(PdfName.Subtype)?.Value == "Form");
        Assert.DoesNotContain("BI", Encoding.Latin1.GetString(doc.DecodeStream(savedForm).Data));
        Assert.Single(Images(doc, savedForm.Dictionary.GetDictionary(PdfName.Resources)), s => s.Dictionary.GetName(PdfName.Filter)?.Value == "JBIG2Decode");
    }

    /// <summary>
    /// S-A A110: an inline image with /JPXDecode (not allowed inline, 7.4.9) becomes an image XObject with the codestream as stored; the page
    /// looks the same (FG 993).
    /// </summary>
    [TestPdfsFact]
    public void InlineJpx_BecomesXObject()
    {
        var codestream = JpxColourTests.Codestream("jp2k-resetprob.pdf");
        var original = Page(Bytes("q 80 0 0 54 20 20 cm BI /W 40 /H 27 /F /JPXDecode ID ", codestream, " EI Q\n"));
        var saved = Save(original);
        LooksTheSame(original, saved);
        using var doc = PdfDocument.Open(saved);
        Assert.DoesNotContain("BI", Content(doc, doc.Pages[0]));
        var image = Assert.Single(Images(doc, doc.Pages[0].Resources));
        Assert.Equal("JPXDecode", image.Dictionary.GetName(PdfName.Filter)?.Value);
        Assert.True(AsRead(doc.GetRawStreamData(image), codestream), "Codestream verändert");
    }

    /// <summary>
    /// The test before parsing (FG 993, memory: each part of a page's content in a rented buffer, joined only where one may hold an inline
    /// image): „BI“ counts only as a word of its own, as the parser reads an operator; an inline image whose „BI“ and data lie in two parts of
    /// the content (the display joins them with a line end, 7.8.2) is still found and becomes an XObject; the page looks the same.
    /// </summary>
    [Fact]
    public void InlineImageTest_WordOfItsOwnAndAcrossParts()
    {
        Assert.True(InlineImageConformance.MayHold("BI /W 1 /H 1 ID x EI"u8));
        Assert.True(InlineImageConformance.MayHold("Q\nBI"u8));
        Assert.True(InlineImageConformance.MayHold("[1]BI/W"u8));
        Assert.False(InlineImageConformance.MayHold("(OBIT) Tj /BIG Do 1BI xBI BIx"u8));
        Assert.False(InlineImageConformance.MayHold("(OBIT) Tj (INSIDE) Tj"u8));   // „BI“ and „ID“ inside words: before, parsed in full
        Assert.False(InlineImageConformance.MayHold("B I"u8));
        Assert.False(InlineImageConformance.MayHold(""u8));

        var jbig2 = Jbig2(40, 24, out _);
        var original = Page([Bytes("0 0 1 rg 10 10 280 280 re f % OBIT\nq 80 0 0 48 20 200 cm BI /W 40 /H 24 /BPC 1 /CS /G"),
                             Bytes("/F /JBIG2Decode ID ", jbig2, " EI Q\n")]);
        var saved = Save(original);
        LooksTheSame(original, saved);
        using var doc = PdfDocument.Open(saved);
        Assert.DoesNotMatch(@"\bBI\b", Content(doc, doc.Pages[0]));
        var image = Assert.Single(Images(doc, doc.Pages[0].Resources));
        Assert.True(AsRead(doc.GetRawStreamData(image), jbig2), "Daten verändert");
    }

    /// <summary>
    /// S-A A14, A75, A76: bytes before the start of a JPEG go (the JPEG unchanged), a bare JPEG 2000 codestream gets the JP2 wrapper (the
    /// codestream unchanged), and /Width, /Height of a JPEG 2000 image become the size of its data; the page looks the same (FG 992).
    /// </summary>
    [TestPdfsFact]
    public void ImageData_JpegStartBareCodestreamAndSize()
    {
        var samples = Enumerable.Range(0, 32 * 16 * 3).Select(i => (byte)(i * 7)).ToArray();
        var jpeg = SkiaImageCodec.Instance.EncodeJpeg(32, 16, 3, samples, 90)!;
        byte[] padded = [.. Enumerable.Repeat((byte)0x20, 16), .. jpeg];
        var codestream = JpxColourTests.Codestream("jp2k-resetprob.pdf");
        PdfStream Image(int width, int height, string filter, byte[] data, bool colour) => new(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(width)), ("Height", PdfInteger.Of(height)), ("Filter", TestPdf.N(filter)),
            ("ColorSpace", TestPdf.N("DeviceRGB")), ("BitsPerComponent", PdfInteger.Of(8))), data);
        var original = Page(Bytes("q 64 0 0 32 20 20 cm /Jp Do Q q 80 0 0 54 120 20 cm /Bare Do Q q 80 0 0 54 120 120 cm /Sized Do Q\n"),
            ("Jp", Image(32, 16, "DCTDecode", padded, true)), ("Bare", Image(40, 27, "JPXDecode", codestream, true)),
            ("Sized", Image(47, 30, "JPXDecode", codestream, true)));
        var saved = Save(original);
        LooksTheSame(original, saved);
        using var doc = PdfDocument.Open(saved);
        var x = doc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!;
        PdfStream Get(string name) => (PdfStream)x.Get(PdfName.Get(name));
        Assert.Equal(jpeg, doc.GetRawStreamData(Get("Jp")));
        foreach (var name in new[] { "Bare", "Sized" })
        {
            var data = doc.GetRawStreamData(Get(name));
            Assert.True(data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20]), $"{name}: keine JP2-Hülle");
            Assert.True(data.AsSpan().EndsWith(codestream), $"{name}: Codestream verändert");
            Assert.Equal((40, 27), ((int)Get(name).Dictionary.GetInteger(PdfName.Get("Width"))!, (int)Get(name).Dictionary.GetInteger(PdfName.Get("Height"))!));
        }
    }

    /// <summary>
    /// A bare codestream whose last channel the display reads as the opacity /SMaskInData announces (four channels with /DeviceRGB, two with
    /// /DeviceGray): the JP2 wrapper names it in a Channel Definition box, the colour grey or sRGB — before four became CMYK without it, and
    /// other readers saw no opacity; three channels with /DeviceRGB stay without one. The page looks the same, the codestream is unchanged,
    /// and the PDF/A check counts the colour channels without the opacity (docs/FEHLERGEDAECHTNIS.md, No. 996).
    /// </summary>
    [TestPdfsFact]
    public void BareCodestreamWithOpacity_WrappedWithChannelDefinition()
    {
        var four = JpxRobustTests.Codestream(0, 40, 40, 4, 1, 0, 1, 0);
        var two = JpxColourTests.StoredStream("jpx_numcomps.pdf", 15);
        var three = JpxColourTests.StoredStream("jpx_numcomps.pdf", 14);
        PdfStream Image(int width, int height, string space, byte[] data) => new(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")),
            ("Width", PdfInteger.Of(width)), ("Height", PdfInteger.Of(height)), ("Filter", TestPdf.N("JPXDecode")), ("ColorSpace", TestPdf.N(space)),
            ("SMaskInData", PdfInteger.Of(1))), data);
        var original = Page(Bytes("0 0 1 rg 0 0 300 300 re f q 80 0 0 8 20 20 cm /Four Do Q q 64 0 0 64 120 20 cm /Two Do Q q 64 0 0 64 120 120 cm /Three Do Q\n"),
            ("Four", Image(40, 1, "DeviceRGB", four)), ("Two", Image(32, 32, "DeviceGray", two)), ("Three", Image(32, 32, "DeviceRGB", three)));
        var saved = Save(original);
        LooksTheSame(original, saved);
        using var doc = PdfDocument.Open(saved);
        var x = doc.Pages[0].Resources!.GetDictionary(PdfName.Get("XObject"))!;
        byte[] Data(string name) => doc.GetRawStreamData((PdfStream)x.Get(PdfName.Get(name))).ToArray();
        foreach (var (name, codestream, opacity) in new[] { ("Four", four, true), ("Two", two, true), ("Three", three, false) })
        {
            var data = Data(name);
            Assert.True(data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0, 0, 0, 12, 0x6A, 0x50, 0x20, 0x20]), $"{name}: keine JP2-Hülle");
            Assert.True(data.AsSpan().EndsWith(codestream), $"{name}: Codestream verändert");
            Assert.Equal(opacity, data.AsSpan(0, data.Length - codestream.Length).IndexOf("cdef"u8) >= 0);
            Assert.Empty(PdfAJpeg2000.Faults(data, imageColourSpace: true));
        }
        // the wrapper: JP2 with sRGB or grey, the last channel opacity for the whole image
        var wrapped = Data("Four");
        Assert.True(wrapped.AsSpan().IndexOf("ftypjp2 "u8) >= 0, "keine JP2-Marke");
        var cdef = wrapped.AsSpan().IndexOf("cdef"u8) + 4;
        // N 4; channel 0: colour 1 … channel 2: colour 3; channel 3: opacity (type 1) for the whole image (association 0)
        Assert.Equal(new byte[] { 0, 4, /**/ 0, 0, 0, 0, 0, 1, /**/ 0, 1, 0, 0, 0, 2, /**/ 0, 2, 0, 0, 0, 3, /**/ 0, 3, 0, 1, 0, 0 }, wrapped[cdef..(cdef + 26)]);
        Assert.Equal("DeviceRGB", PdfAJpeg2000.DeviceSpace(wrapped));
        Assert.Equal("DeviceGray", PdfAJpeg2000.DeviceSpace(Data("Two")));
        // what the display cannot tell is not wrapped: a /ColorSpace named by a resource
        var named = TestPdf.Dict(("ColorSpace", TestPdf.N("CS0")), ("SMaskInData", PdfInteger.Of(1)));
        Assert.Equal(-1, ImageDataConformance.BareOpacity(named, four, null));
    }
}
