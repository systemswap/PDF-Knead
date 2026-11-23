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
public sealed partial class JpxTests
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

    /// <summary>
    /// ISO/IEC 15444-1, Table B.4 (rows as data): the codewords of the number of coding passes — 1, 2, 3 to 5, 6 to 36 and 37 to 164 at both
    /// ends of each range — read through the bit stuffing of B.10.1 (after a byte 0xFF the next one carries only 7 bits).
    /// </summary>
    [Theory]
    [InlineData("0", 1)]
    [InlineData("10", 2)]
    [InlineData("1100", 3)]
    [InlineData("1101", 4)]
    [InlineData("1110", 5)]
    [InlineData("111100000", 6)]
    [InlineData("111111110", 36)]
    [InlineData("1111111110000000", 37)]
    [InlineData("1111111111111111", 164)]
    public void CodingPasses_AsTableB4(string codeword, int passes)
    {
        // pack with the bit stuffing of B.10.1: a 0 bit in front of the byte after 0xFF
        var bytes = new List<byte>();
        int current = 0, count = 0;
        var limit = 8;
        foreach (var bit in codeword + "0000000000000000")
        {
            current = current << 1 | (bit - '0');
            if (++count < limit) continue;
            bytes.Add((byte)current);
            limit = current == 0xFF ? 7 : 8;
            current = 0;
            count = 0;
        }
        var data = bytes.ToArray();
        Assert.Equal(passes, JpxTile.Passes(new JpxBits(data, 0, data.Length)));
    }

    /// <summary>
    /// ISO/IEC 15444-1, Table D.1 (rows as data, "x" for any): the context of every one of the 256 neighbour patterns for the three kinds of
    /// sub-band — LL and LH by the sums of the horizontal, vertical and diagonal neighbours, HL with horizontal and vertical swapped, HH by the
    /// diagonal sum first. The first row of the table that matches decides (checked row by row on 23.11.2026).
    /// </summary>
    [Fact]
    public void ZeroCodingContexts_AsTableD1()
    {
        // (H, V, D, context) for LL and LH; -1 = any, 10 + n = at least n
        int[][] lhRows = [[2, -1, -1, 8], [1, 11, -1, 7], [1, 0, 11, 6], [1, 0, 0, 5], [0, 2, -1, 4], [0, 1, -1, 3], [0, 0, 12, 2], [0, 0, 1, 1], [0, 0, 0, 0]];
        // (H + V, D, context) for HH
        int[][] hhRows = [[-1, 13, 8], [11, 2, 7], [0, 2, 6], [12, 1, 5], [1, 1, 4], [0, 1, 3], [12, 0, 2], [1, 0, 1], [0, 0, 0]];
        static bool Fits(int want, int value) => want == -1 || (want >= 10 ? value >= want - 10 : value == want);
        for (var n = 0; n < 256; n++)
        {
            int h = (n & 1) + (n >> 1 & 1), v = (n >> 2 & 1) + (n >> 3 & 1), d = (n >> 4 & 1) + (n >> 5 & 1) + (n >> 6 & 1) + (n >> 7 & 1);
            var ll = lhRows.First(r => Fits(r[0], h) && Fits(r[1], v) && Fits(r[2], d))[3];
            var hl = lhRows.First(r => Fits(r[0], v) && Fits(r[1], h) && Fits(r[2], d))[3];
            var hh = hhRows.First(r => Fits(r[0], h + v) && Fits(r[1], d))[2];
            Assert.Equal(ll, JpxBlockDecoder.ZeroContexts[0][n]);
            Assert.Equal(hl, JpxBlockDecoder.ZeroContexts[1][n]);
            Assert.Equal(hh, JpxBlockDecoder.ZeroContexts[2][n]);
        }
    }

    /// <summary>
    /// ISO/IEC 15444-1, Tables D.2 and D.3 (rows as data): the contributions of the horizontal and vertical neighbours (both positive or one
    /// positive and none: 1, both negative or one negative and none: −1, else 0) and for each of the nine pairs the context (9 to 13) and the
    /// XOR bit — for every one of the 256 patterns of significance and sign of the four direct neighbours.
    /// </summary>
    [Fact]
    public void SignContexts_AsTablesD2AndD3()
    {
        var table = new Dictionary<(int H, int V), (int Context, int Xor)>
        {
            [(1, 1)] = (13, 0), [(1, 0)] = (12, 0), [(1, -1)] = (11, 0), [(0, 1)] = (10, 0), [(0, 0)] = (9, 0),
            [(0, -1)] = (10, 1), [(-1, 1)] = (11, 1), [(-1, 0)] = (12, 1), [(-1, -1)] = (13, 1),
        };
        static int Contribution(int a, int b) => (a, b) switch
        {
            (1, 1) or (1, 0) or (0, 1) => 1,
            (-1, -1) or (-1, 0) or (0, -1) => -1,
            _ => 0,
        };
        for (var index = 0; index < 256; index++)
        {
            int Neighbour(int k) => (index >> k & 1) == 0 ? 0 : (index >> (4 + k) & 1) != 0 ? -1 : 1;
            var (context, xor) = table[(Contribution(Neighbour(0), Neighbour(1)), Contribution(Neighbour(2), Neighbour(3)))];
            Assert.Equal(context | xor << 7, JpxBlockDecoder.SignContexts[index]);
        }
    }

    /// <summary>ISO/IEC 15444-1, Table D.7: after a reset UNIFORM (18) starts in state 46, run-length (17) in state 3, all neighbours
    /// insignificant (0) in state 4, all others in state 0 — each with MPS 0.</summary>
    [Fact]
    public void InitialContextStates_AsTableD7()
    {
        var expected = new byte[19];
        expected[0] = 4 << 1;
        expected[17] = 3 << 1;
        expected[18] = 46 << 1;
        Assert.Equal(expected, JpxBlockDecoder.InitialContexts());
    }

    /// <summary>ISO/IEC 15444-1, Table F.4: the lifting parameters of the irreversible 9-7 filter.</summary>
    [Fact]
    public void LiftingParameters_AsTableF4()
    {
        Assert.Equal((float)-1.586134342059924, JpxWavelet.Alpha);
        Assert.Equal((float)-0.052980118572961, JpxWavelet.Beta);
        Assert.Equal((float)0.882911075530934, JpxWavelet.Gamma);
        Assert.Equal((float)0.443506852043971, JpxWavelet.Delta);
        Assert.Equal((float)1.230174104914001, JpxWavelet.K);
    }

    /// <summary>
    /// ISO/IEC 15444-1, G.2.2 and G.3.2: the inverse RCT — G = Y0 − ⌊(Y2 + Y1) / 4⌋, R = Y2 + G, B = Y1 + G, rounded down also for a negative
    /// sum — and the inverse ICT with the coefficients 1.402, 0.34413, 0.71414 and 1.772.
    /// </summary>
    [Fact]
    public void InverseComponentTransforms_AsG22AndG32()
    {
        float[] y0 = [100, 100], y1 = [-20, -40], y2 = [30, 9];
        JpxWavelet.InverseColor(y0, y1, y2, 2, reversible: true);
        Assert.Equal([128f, 98f, 78f], new[] { y0[0], y1[0], y2[0] });
        Assert.Equal([117f, 108f, 68f], new[] { y0[1], y1[1], y2[1] });
        float[] y = [100], cb = [10], cr = [-20];
        JpxWavelet.InverseColor(y, cb, cr, 1, reversible: false);
        Assert.Equal(100 + 1.402 * -20, y[0], 0.001);
        Assert.Equal(100 - 0.34413 * 10 - 0.71414 * -20, cb[0], 0.001);
        Assert.Equal(100 + 1.772 * 10, cr[0], 0.001);
    }

    [TestPdfsFact]
    public void TwoTilesWithEmptyTileParts_DecodedWhole()
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
