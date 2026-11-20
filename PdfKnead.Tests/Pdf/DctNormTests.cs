using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Rendering;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// DCTDecode (ISO 32000-2, 7.4.8; Konformitätsabgleich M2, docs/normabgleich/M2-filter.md): what the PDF adds to the JPEG — /ColorTransform and
/// the Adobe marker — with JPEGs of the public test collection; the JPEG itself is decoded by the codec.
/// </summary>
public sealed class DctNormTests
{
    private static string TestPdf(string path, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return Path.Combine(dir.FullName, "TestPDFs", path);
        throw new DirectoryNotFoundException();
    }

    /// <summary>The first DCT image of a file: its dictionary and the JPEG.</summary>
    private static (PdfDictionary Dict, byte[] Jpeg) FirstJpeg(string path)
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(TestPdf(path)));
        foreach (var page in doc.Pages)
            if (page.Resources?.GetDictionary(PdfName.Get("XObject")) is { } xobjects)
                foreach (var key in xobjects.Keys)
                    if (xobjects.Get(key) is PdfStream s && s.Dictionary.Get(PdfName.Filter) is PdfName { Value: "DCTDecode" })
                        return (s.Dictionary, doc.DecodeStream(s).Data);
        throw new InvalidDataException(path);
    }

    /// <summary>The JPEG without its Adobe marker (APP14).</summary>
    private static byte[] WithoutAdobe(byte[] jpeg)
    {
        for (var at = 2; at + 4 <= jpeg.Length && jpeg[at] == 0xFF;)
        {
            var length = jpeg[at + 2] << 8 | jpeg[at + 3];
            if (jpeg[at + 1] == 0xEE && jpeg.AsSpan(at + 4, 5).SequenceEqual("Adobe"u8)) return [.. jpeg.AsSpan(0, at), .. jpeg.AsSpan(at + 2 + length)];
            if (jpeg[at + 1] == 0xDA) break;
            at += 2 + length;
        }
        throw new InvalidDataException("kein Adobe-Marker");
    }

    private static byte[] Codec(byte[] jpeg) => SkiaImageCodec.Instance.Decode(jpeg, "DCTDecode", long.MaxValue)!.Value.Samples;

    /// <summary>
    /// Probe: the codec reads four components the same with and without the Adobe marker (transform 0) — the marker decides only about a
    /// colour transform, never about the inversion.
    /// </summary>
    [TestPdfsFact]
    public void Cmyk_TheMarkerChangesNothingButTheTransform()
    {
        var (_, jpeg) = FirstJpeg("pdfjs/cmykjpeg.pdf");
        Assert.Equal(Codec(jpeg), Codec(WithoutAdobe(jpeg)));
    }

    /// <summary>
    /// Probe: a YCCK JPEG (Adobe marker with transform 2) without its marker is read differently by the codec — the transform is what
    /// /ColorTransform 1 has to bring back.
    /// </summary>
    [TestPdfsFact]
    public void Ycck_WithoutMarker_ReadDifferently()
    {
        var (_, jpeg) = FirstJpeg("pdfium/fx/other/1.pdf");
        Assert.NotEqual(Codec(jpeg), Codec(WithoutAdobe(jpeg)));
    }

    /// <summary>The JPEG with its frame size set to <paramref name="width"/> × <paramref name="height"/>.</summary>
    private static byte[] Resized(byte[] jpeg, int width, int height)
    {
        var d = (byte[])jpeg.Clone();
        for (var at = 2; at + 4 <= d.Length && d[at] == 0xFF;)
        {
            if (d[at + 1] is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                (d[at + 5], d[at + 6], d[at + 7], d[at + 8]) = ((byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width);
                return d;
            }
            at += 2 + (d[at + 2] << 8 | d[at + 3]);
        }
        throw new InvalidDataException("kein SOF");
    }

    /// <summary>
    /// A crafted progressive JPEG (security, docs/FEHLERGEDAECHTNIS.md, No. 742): a JPEG of the public test collection with its frame claiming
    /// 12 000 × 12 000 pixels — the codec kept the coefficients of the whole image, 950 MB for 7 KB (60 000 × 60 000 would be over 20 GB).
    /// Refused now: more coefficients than any complete stream of that length can carry. The original and large real JPEGs are untouched.
    /// </summary>
    [TestPdfsFact]
    public void CraftedProgressive_RefusedBeforeTheCodec()
    {
        var (_, jpeg) = FirstJpeg("pdfjs/issue7406.pdf");
        Assert.NotNull(SkiaImageCodec.Instance.Decode(jpeg, "DCTDecode", long.MaxValue));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(SkiaImageCodec.Instance.Decode(Resized(jpeg, 12000, 12000), "DCTDecode", 16_000_000));
        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
        // the scanner placeholder of 65 535 lines (test file bug_86459): the height of the PDF goes in first, the image is drawn as before
        using var doc = PdfDocument.Open(File.ReadAllBytes(TestPdf("pdfium/pdfium/bug_86459.pdf")));
        var (dict, data) = FirstJpeg("pdfium/pdfium/bug_86459.pdf");
        Assert.NotNull(PdfImageDecoder.Decode(new ImageSource(new PdfStream(dict, data), null, null, false, 0), doc));
    }

    /// <summary>
    /// Blocks times scans (each scan passes over the whole image): a JPEG header of 20 000 × 20 000 grey (6.25 million blocks) with 700 scans
    /// (4.4 billion) is refused, with 600 (3.75 billion) not — a real progressive JPEG has about ten.
    /// </summary>
    [Fact]
    public void ManyScans_Refused()
    {
        static byte[] Header(int scans)
        {
            byte[] sof = [0xFF, 0xC2, 0, 11, 8, 0x4E, 0x20, 0x4E, 0x20, 1, 1, 0x11, 0];
            byte[] sos = [0xFF, 0xDA, 0, 8, 1, 1, 0, 0, 0, 0];
            return [0xFF, 0xD8, .. sof, .. Enumerable.Repeat(sos, scans).SelectMany(s => s), .. new byte[2_000_000], 0xFF, 0xD9];
        }
        Assert.True(SkiaImageCodec.TooCostly(Header(700)));
        Assert.False(SkiaImageCodec.TooCostly(Header(600)));
    }

    /// <summary>
    /// Four components without an Adobe marker (ISO 32000-2, 7.4.8, Table 13; docs/FEHLERGEDAECHTNIS.md, No. 738): /ColorTransform 1 means YCCK
    /// to CMYK after decoding; 0 or no entry: no transform (the default for four components). Before the entry counted only for three
    /// components, and a YCCK image without its marker was drawn with false colours.
    /// </summary>
    [TestPdfsFact]
    public void FourComponents_ColorTransformOfTheDictionary()
    {
        var (dict, jpeg) = FirstJpeg("pdfium/fx/other/1.pdf");
        using var doc = PdfDocument.Open(File.ReadAllBytes(TestPdf("pdfium/fx/other/1.pdf")));
        byte[] Drawn(byte[] data, int? transform)
        {
            var image = new PdfDictionary();
            foreach (var (key, value) in dict.Entries)
                if (key.Value is not ("Length" or "DecodeParms")) image.Set(key, value);
            if (transform is { } t)
            {
                var parms = new PdfDictionary();
                parms.Set(PdfName.Get("ColorTransform"), PdfInteger.Of(t));
                image.Set(PdfName.DecodeParms, parms);
            }
            var decoded = PdfImageDecoder.Decode(new ImageSource(new PdfStream(image, data), null, null, false, 0), doc);
            Assert.NotNull(decoded);
            return decoded.Rgba;
        }
        var original = Drawn(jpeg, null);
        var bare = WithoutAdobe(jpeg);
        Assert.Equal(original, Drawn(bare, 1));
        Assert.NotEqual(original, Drawn(bare, 0));
        Assert.Equal(Drawn(bare, 0), Drawn(bare, null));
        // the marker in the data decides over the entry
        Assert.Equal(original, Drawn(jpeg, 0));
    }
}
