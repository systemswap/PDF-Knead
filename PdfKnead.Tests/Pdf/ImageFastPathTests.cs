using PdfKnead.Core.Pdf.Content;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using static PdfKnead.Tests.Pdf.TestPdf;

namespace PdfKnead.Tests.Pdf;

/// <summary>Images with /Decode and 16 bits per component decoded through per-component tables yield the same values as per-pixel computation.</summary>
public sealed class ImageFastPathTests
{
    private static DecodedImage Decode(byte[] pixels, int width, int height, string space, int bpc, PdfArray? decode = null)
    {
        var pdf = new TestPdf();
        var catalog = pdf.Add(Dict(("Type", N("Catalog"))));
        var pagesNode = pdf.Add(new PdfDictionary());
        var dict = Dict(("Type", N("XObject")), ("Subtype", N("Image")), ("Width", PdfInteger.Of(width)), ("Height", PdfInteger.Of(height)),
            ("BitsPerComponent", PdfInteger.Of(bpc)), ("ColorSpace", N(space)));
        if (decode is not null) dict.Set(N("Decode"), decode);
        var image = pdf.Add(FlateStream(pixels, dict));
        var content = pdf.Add(FlateStream("q 100 0 0 100 0 0 cm /I Do Q"u8.ToArray()));
        var page = pdf.Add(Dict(("Type", N("Page")), ("Parent", Ref(pagesNode)), ("MediaBox", Box(0, 0, 100, 100)),
            ("Resources", Dict(("XObject", Dict(("I", Ref(image)))))), ("Contents", Ref(content))));
        pdf.Replace(pagesNode, Dict(("Type", N("Pages")), ("Kids", new PdfArray([Ref(page)])), ("Count", PdfInteger.Of(1))));
        ((PdfDictionary)pdf.Get(catalog)).Set(PdfName.Pages, Ref(pagesNode));
        pdf.RootNumber = catalog;
        using var doc = PdfDocument.Open(pdf.Build());
        return PdfImageDecoder.Decode(PageInterpreter.Build(doc, doc.Pages[0]).Images[0], doc)!;
    }

    /// <summary>Compares large arrays directly: Assert.Equal keeps track of every element (millions of bytes take minutes).</summary>
    private static void Same(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        var at = expected.AsSpan().CommonPrefixLength(actual);
        Assert.True(at == expected.Length, $"erste Abweichung bei {at}: {(at < expected.Length ? expected[at] : 0)} statt {(at < actual.Length ? actual[at] : 0)}");
    }

    [Theory]
    [InlineData(64, 64)]
    [InlineData(1200, 1000)]   // from 1 million pixels on, in parallel bands
    public void DecodeArrayAndSixteenBit_MatchPerPixelValues(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i * 31 + (i >> 9));
        var plain = Decode(rgb, width, height, "DeviceRGB", 8).Rgba;

        // /Decode [1 0 1 0 1 0]: exactly 255 − v
        var inverted = Decode(rgb, width, height, "DeviceRGB", 8, new PdfArray([.. Enumerable.Range(0, 6).Select(i => (PdfObject)PdfInteger.Of(1 - i % 2))])).Rgba;
        Same(plain.Select((v, i) => i % 4 == 3 ? (byte)255 : (byte)(255 - v)).ToArray(), inverted);

        // 16 bits: v · 257 is the same fraction as v in 8 bits
        var wide = new byte[rgb.Length * 2];
        for (var i = 0; i < rgb.Length; i++) (wide[2 * i], wide[2 * i + 1]) = (rgb[i], rgb[i]);
        Same(plain, Decode(wide, width, height, "DeviceRGB", 16).Rgba);

        // Grey with 16 bits (one component, without the table derived from 8 bits)
        var gray = new byte[width * height * 2];
        for (var i = 0; i < width * height; i++) (gray[2 * i], gray[2 * i + 1]) = ((byte)i, (byte)i);
        var g = Decode(gray, width, height, "DeviceGray", 16);
        Same([.. Enumerable.Range(0, width * height).Select(i => (byte)i)], [.. g.Rgba.Where((_, i) => i % 4 == 0)]);
    }
}
