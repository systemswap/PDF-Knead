using PdfKnead.Core.Pdf.Filters.Jpeg;
using PdfKnead.Rendering;
using W = PdfKnead.Tests.Pdf.TestJpegWriter;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Arithmetic JPEG decoding (ITU-T T.81, Annex D, F.2.4, G.1.3). No collection holds an arithmetic coded JPEG (SOF9–SOF11), so the evidence
/// comes from elsewhere (decided on 12.11.2026): the test images of libjpeg-turbo and images of the test writer over every
/// arithmetic path, both compared with libjpeg-turbo, the codec of the display before Stufe A; the test sequence of T.81, K.4.1 in
/// JpegNormTextTests. The test images are local only (not in Git); without them that test passes.
/// </summary>
public sealed class JpegArithmeticTests
{
    /// <summary>
    /// The test images of libjpeg-turbo 3.0.4 (testimages/, IJG licence; in Normen-ISO/grundlagen/pruefdaten/jpeg-libjpeg-turbo with source
    /// and checksum): testimgari.jpg is arithmetic coded (SOF9, 4:2:0), testimgint.jpg and testorig.jpg are Huffman coded. The own decoder
    /// gives exactly the samples of libjpeg-turbo — limit 0, stricter than the one step allowed for other JPEGs (the value reached is kept as
    /// the limit, ARBEITSWEISE Grundsatz 14).
    /// </summary>
    [Theory]
    [InlineData("testimgari.jpg", true)]
    [InlineData("testimgint.jpg", false)]
    [InlineData("testorig.jpg", false)]
    public void TestImagesOfLibjpegTurbo(string name, bool arithmetic)
    {
        var path = Path.Combine(NormFolder(), "grundlagen", "pruefdaten", "jpeg-libjpeg-turbo", name);
        if (!File.Exists(path)) return;
        var data = File.ReadAllBytes(path);
        Assert.Equal(arithmetic, JpegDecoder.ReadFrame(data)!.Value.Arithmetic);
        var own = JpegDecoder.Decode(data);
        Assert.False(own.Damaged);
        var (max, mean) = JpegDecoderTests.Compare(own, SkiaImageCodec.Instance.Decode(data, "DCTDecode", long.MaxValue)!.Value);
        Assert.True(max == 0, $"größter Unterschied {max}, Mittel {mean:0.000}");
    }

    /// <summary>
    /// Images of the test encoder over every arithmetic path — sequential and progressive (DC with point transform, AC bands, successive
    /// approximation in two stages, DC refinement), grey and three sampling layouts, restart intervals, conditioning (DAC) other than the
    /// defaults, values up to the largest magnitude categories, blocks without end of band: the own decoder reports nothing damaged, the
    /// progressive image gives the same pixels as the sequential one, and the samples are those of libjpeg-turbo — exactly where nothing is
    /// upsampled (grey, 4:4:4: so the coefficients are exact), within one step where the chroma is upsampled (the rounding of the
    /// upsampling, the limit for every JPEG, JpegDecoderTests.SameAsTheCodec).
    /// </summary>
    [Theory]
    [InlineData(1, 1, 1, 0, false)]
    [InlineData(3, 2, 2, 0, false)]
    [InlineData(3, 2, 1, 3, true)]
    [InlineData(3, 1, 2, 1, true)]
    [InlineData(3, 1, 1, 7, false)]
    public void TestEncoderImages_SameAsLibjpegTurbo(int components, int h, int v, int restart, bool conditioned)
    {
        const int width = 61, height = 37;
        byte[][] quant = [W.Quant(2), W.Quant(4)];
        var comps = W.Components(width, height, components, h, v, quant);
        var conditioning = conditioned ? new W.Conditioning([(2, 5), (0, 0)], [2, 40]) : null;
        byte[]? first = null;
        foreach (var data in new[] { W.Write(width, height, comps, quant, null, restart, conditioning),
                     W.Write(width, height, comps, quant, Script(components), restart, conditioning) })
        {
            var own = JpegDecoder.Decode(data);
            Assert.False(own.Damaged);
            var codec = SkiaImageCodec.Instance.Decode(data, "DCTDecode", long.MaxValue);
            Assert.True(codec is not null, "libjpeg-turbo liest das Bild nicht");
            var (max, mean) = JpegDecoderTests.Compare(own, codec.Value);
            var limit = components == 1 || h * v == 1 ? 0 : 1;
            Assert.True(max <= limit, $"{(first is null ? "sequenziell" : "progressiv")}: größter Unterschied {max}, Mittel {mean:0.000}");
            if (first is null) first = own.Samples;
            else Assert.Equal(first, own.Samples);
        }
    }

    /// <summary>
    /// A new quantisation table between progressive scans of a component (T.81, B.2.4 Satz 5 and B.2.2 Satz 38: not allowed) does not apply
    /// to it: the table of its first scan stays, as in libjpeg-turbo (S-A A103) — the same samples as without the new table.
    /// </summary>
    [Fact]
    public void Progressive_QuantisationTableLatchedAtTheFirstScan()
    {
        const int width = 61, height = 37;
        byte[][] quant = [W.Quant(2), W.Quant(4)];
        var comps = W.Components(width, height, 3, 1, 1, quant);
        byte[] other = [0xFF, 0xDB, 0, 67, 0, .. Enumerable.Repeat((byte)50, 64)];
        var reference = JpegDecoder.Decode(W.Write(width, height, comps, quant, Script(3)));
        var data = W.Write(width, height, comps, quant, Script(3), beforeScan: i => i == 3 ? other : null);
        var image = JpegDecoder.Decode(data);
        Assert.Equal(reference.Samples, image.Samples);
        var (max, _) = JpegDecoderTests.Compare(image, SkiaImageCodec.Instance.Decode(data, "DCTDecode", long.MaxValue)!.Value);
        Assert.Equal(0, max);
    }

    /// <summary>
    /// A progressive script with every kind of scan in the order T.81, G.1.1.1.1 requires: DC first with point transform 1 (interleaved),
    /// AC bands 1–5 and 6–63 with point transform 2, a refinement of the AC coefficients to bit 1, the DC refinement, the last AC refinement.
    /// </summary>
    private static W.Scan[] Script(int components)
    {
        int[] all = [.. Enumerable.Range(0, components)];
        var scans = new List<W.Scan> { new(all, 0, 0, 0, 1) };
        foreach (var c in all)
        {
            scans.Add(new([c], 1, 5, 0, 2));
            scans.Add(new([c], 6, 63, 0, 2));
        }
        foreach (var c in all) scans.Add(new([c], 1, 63, 2, 1));
        scans.Add(new(all, 0, 0, 1, 0));
        foreach (var c in all) scans.Add(new([c], 1, 63, 1, 0));
        return [.. scans];
    }

    private static string NormFolder([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source)!); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "PdfKnead.sln"))) return Path.Combine(dir.FullName, "Normen-ISO");
        return "";
    }
}
