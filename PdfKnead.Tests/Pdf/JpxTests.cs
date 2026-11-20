using System.Diagnostics;
using PdfKnead.Core.Pdf;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// JPEG 2000 with the files of the public test collection (TestPDFs). The full breadth of the standard (all progression orders, ROI, PPM/PPT,
/// 9/7 …) is checked by the probe run <see cref="JpxConformance"/> against the reference images of ITU-T T.803.
/// </summary>
public sealed class JpxTests
{
    private static string PdfJs(string name, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "TestPDFs"))) return Path.Combine(dir.FullName, "TestPDFs", "pdfjs", name);
        throw new DirectoryNotFoundException();
    }

    /// <summary>Collects all JPX images of a file: the stream together with the resources of its page.</summary>
    private static List<(PdfStream Stream, PdfDictionary? Resources)> JpxImages(PdfDocument doc)
    {
        var list = new List<(PdfStream, PdfDictionary?)>();
        foreach (var page in doc.Pages)
        {
            var resources = page.Resources;
            if (resources?.GetDictionary(PdfName.Get("XObject")) is not { } xobjects) continue;
            foreach (var key in xobjects.Keys)
                if (xobjects.Get(key) is PdfStream s && s.Dictionary.Get(PdfName.Filter) is PdfName { Value: "JPXDecode" })
                    list.Add((s, resources));
        }
        return list;
    }

    private static byte[] Raw(PdfDocument doc, PdfStream stream) => doc.DecodeStream(stream).Data;

    [TestPdfsFact]
    public void TwoTilesWithEmptyTileParts_DecodeLikeTheWindowsReader()
    {
        // 128 × 64 grey in two tiles with empty tile-parts in between; one of the reference readers shows a black area
        using var doc = PdfDocument.Open(File.ReadAllBytes(PdfJs("bug_jpx.pdf")));
        var (stream, _) = Assert.Single(JpxImages(doc));
        var image = JpxDecoder.Decode(Raw(doc, stream), long.MaxValue);
        Assert.Equal((128, 64, 1), (image.Width, image.Height, image.Channels));
        Assert.False(image.Truncated);
        Assert.All(image.Samples, v => Assert.Equal(0, v));
    }

    [TestPdfsFact]
    public void Text_IsDarkOnLight()
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(PdfJs("issue19326.pdf")));
        foreach (var (stream, resources) in JpxImages(doc))
        {
            var decoded = PdfImageDecoder.Decode(new ImageSource(stream, null, resources, false, 0), doc);
            Assert.NotNull(decoded);
            var gray = decoded.Format == ImagePixels.Gray ? decoded.Pixels : decoded.Rgba.Where((_, i) => i % 4 == 0).ToArray();
            Assert.Contains(gray, v => v < 64);
            Assert.Contains(gray, v => v > 192);
        }
    }

    [TestPdfsFact]
    public void IndexedColorSpaceOfThePage_GetsTheRawIndices()
    {
        // /Indexed with 16 colours over ICCBased, 4-bit image: the palette comes from the PDF, not from the JP2 wrapper
        using var doc = PdfDocument.Open(File.ReadAllBytes(PdfJs("issue12213.pdf")));
        var (stream, resources) = Assert.Single(JpxImages(doc));
        var raw = JpxDecoder.Decode(Raw(doc, stream), long.MaxValue, applyPalette: false);
        Assert.All(raw.Samples, v => Assert.InRange(v, 0, 15));
        Assert.Contains(raw.Samples, v => v != raw.Samples[0]);

        var decoded = PdfImageDecoder.Decode(new ImageSource(stream, null, resources, false, 0), doc);
        Assert.NotNull(decoded);
        Assert.Equal((684, 74), (decoded.Width, decoded.Height));
        // The blue of the logo (the shield) comes from the palette
        var rgba = decoded.Rgba;
        Assert.Contains(Enumerable.Range(0, rgba.Length / 4), i => rgba[i * 4 + 2] > 150 && rgba[i * 4] < 100);
    }

    [TestPdfsFact]
    public void LargePhoto_IsReducedWhileDecoding()
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(PdfJs("S2.pdf")));
        var (stream, _) = JpxImages(doc).OrderByDescending(i => i.Stream.Dictionary.GetInteger(PdfName.Get("Width")) ?? 0).First();
        var data = Raw(doc, stream);
        var full = JpxDecoder.Decode(data, long.MaxValue);
        var small = JpxDecoder.Decode(data, (long)((full.Width + 1) / 2) * ((full.Height + 1) / 2));
        Assert.Equal(1, small.Reduced);
        Assert.Equal(((full.Width + 1) / 2, (full.Height + 1) / 2), (small.Width, small.Height));
        Assert.False(full.Truncated);
    }

    /// <summary>
    /// Corrupted and truncated data: never an exception other than the own one, never a long computation (small-scale fuzzing;
    /// the large run belongs to the separate robustness analysis).
    /// </summary>
    [Trait(TestKind.Name, TestKind.Time)]
    [TestPdfsFact]
    public void DamagedData_NeverThrowsForeignExceptionsOrHangs()
    {
        var sources = new List<byte[]>();
        foreach (var name in new[] { "S2.pdf", "issue19326.pdf", "issue12213.pdf", "bug_jpx.pdf" })
        {
            using var doc = PdfDocument.Open(File.ReadAllBytes(PdfJs(name)));
            sources.AddRange(JpxImages(doc).Select(i => Raw(doc, i.Stream)).Where(d => d.Length < 200_000));
        }
        Assert.NotEmpty(sources);
        var random = new Random(4711);
        var watch = new Stopwatch();
        for (var round = 0; round < 400; round++)
        {
            var source = sources[round % sources.Count];
            var data = (byte[])source.Clone();
            if (round % 3 == 0) data = data[..random.Next(1, data.Length)];
            for (var k = random.Next(1, 8); k > 0; k--) data[random.Next(data.Length)] = (byte)random.Next(256);
            watch.Restart();
            try
            {
                JpxDecoder.Decode(data, 4_000_000);
            }
            catch (PdfException)
            {
                // own exception: broken file, the image is dropped
            }
            Assert.True(watch.ElapsedMilliseconds < 5000 * TestKind.Factor, $"Runde {round}: {watch.ElapsedMilliseconds} ms");
        }
    }

    [Trait(TestKind.Name, TestKind.Time)]
    [Fact]
    public void ImpossibleSizes_AreRefusedBeforeAllocating()
    {
        // SIZ written by hand: 1 component, 8 bits; COD without decomposition, QCD; one empty tile-part
        static byte[] Codestream(uint width, uint height, uint tile, byte levels)
        {
            var b = new List<byte> { 0xFF, 0x4F, 0xFF, 0x51, 0x00, 0x29, 0x00, 0x00 };
            void U32(uint v) => b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
            U32(width); U32(height); U32(0); U32(0); U32(tile); U32(tile); U32(0); U32(0);
            b.AddRange([0x00, 0x01, 0x07, 0x01, 0x01]);
            b.AddRange([0xFF, 0x52, 0x00, 0x0C, 0x00, 0x00, 0x00, 0x01, 0x00, levels, 0x04, 0x04, 0x00, 0x01]);
            b.AddRange([0xFF, 0x5C, 0x00, 0x04, 0x40, 0x40]);
            b.AddRange([0xFF, 0x90, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x01, 0xFF, 0x93, 0xFF, 0xD9]);
            return [.. b];
        }

        // Sides above 2^20, too many tiles, too large without levels for reduction
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(Codestream(2_000_000, 10, 2_000_000, 0), 16_000_000));
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(Codestream(100_000, 100_000, 64, 0), 16_000_000));
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(Codestream(60_000, 60_000, 60_000, 0), 16_000_000));

        // The same with levels: reduced, fast, without the memory of the full size
        var watch = Stopwatch.StartNew();
        var image = JpxDecoder.Decode(Codestream(60_000, 60_000, 60_000, 5), 16_000_000);
        Assert.Equal(4, image.Reduced);   // 3750 × 3750 fits below 16 MP
        Assert.True(image.Width * image.Height <= 16_000_000);
        Assert.True(watch.ElapsedMilliseconds < 10_000 * TestKind.Factor, $"{watch.ElapsedMilliseconds} ms");
    }
}
