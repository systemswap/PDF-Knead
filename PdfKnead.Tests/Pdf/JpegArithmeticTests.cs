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
    /// T.81, A.1.1 Sätze 1–10, A.2.1, A.2.2 Satz 2, A.2.3: each component has xi = ⌈X·Hi/Hmax⌉ columns and yi = ⌈Y·Vi/Vmax⌉ lines; a scan
    /// of one component covers its own ⌈xi/8⌉ × ⌈yi/8⌉ blocks, whatever its sampling factors, while an interleaved MCU holds Hk × Vk blocks
    /// of each component, left to right and top to bottom. Own values with factors where the rounding counts (Hmax = 3, so 2/3 and 1/3 of the
    /// width; Y odd): X = 50, Y = 33, factors 1 × 2, 3 × 1, 2 × 1 → components 17 × 33, 50 × 17, 34 × 17, i.e. 3 × 5, 7 × 3 and 5 × 3 blocks
    /// alone, inside the MCU grid of 3 × 3 MCUs (3 × 6, 9 × 3, 6 × 3 blocks). Rounded down instead, the first component would have 16 columns
    /// and 2 blocks. The same coefficients once in one interleaved scan, once progressive with every scan of a single component: the same
    /// samples only if the decoder reads exactly these blocks in both orders.
    /// </summary>
    [Fact]
    public void ComponentSizes_RoundedUp_InterleavedAndAloneTheSame()
    {
        const int width = 50, height = 33;
        byte[][] quant = [W.Quant(2), W.Quant(4)];
        (int H, int V)[] factors = [(1, 2), (3, 1), (2, 1)];
        const int mcusWide = 3, mcusHigh = 3;
        var comps = factors.Select((f, i) =>
        {
            // blocks over the grid of whole MCUs, from a single-component image of exactly that many blocks
            var blocks = W.Components(mcusWide * f.H * 8, mcusHigh * f.V * 8, 1, 1, 1, [quant[Math.Min(i, 1)]])[0].Blocks;
            return new W.Component(i + 1, f.H, f.V, Math.Min(i, 1), Math.Min(i, 1), Math.Min(i, 1), blocks);
        }).ToArray();
        // the block counts of A.1.1 worked out by hand above
        Assert.Equal([(3, 5), (7, 3), (5, 3)], factors.Select(f => ((width * f.H + 2) / 3 + 7) / 8).Zip(factors.Select(f => ((height * f.V + 1) / 2 + 7) / 8)));

        var interleaved = JpegDecoder.Decode(W.Write(width, height, comps, quant));
        W.Scan[] alone = [.. Enumerable.Range(0, 3).Select(c => new W.Scan([c], 0, 0, 0, 0)),
            .. Enumerable.Range(0, 3).Select(c => new W.Scan([c], 1, 63, 0, 0))];
        var single = JpegDecoder.Decode(W.Write(width, height, comps, quant, alone));
        Assert.False(interleaved.Damaged);
        Assert.False(single.Damaged);
        Assert.Equal((width, height), (single.Width, single.Height));
        Assert.Equal(interleaved.Samples, single.Samples);
    }

    /// <summary>
    /// T.81, A.3.2 Sätze 1–3, A.3.6 (Figure A.6): the zig-zag order runs along the anti-diagonals of the block, the odd ones downwards from
    /// the top row, the even ones upwards — built here by that walk and compared with the decoder's table; and the orientation of the block
    /// holds in the decoder too: coefficient 1 of the zig-zag order is the first horizontal frequency (every line the same, the columns
    /// differ), coefficient 2 the first vertical one (every column the same, the lines differ).
    /// </summary>
    [Fact]
    public void ZigZag_AsFigureA6_OrientationOfTheBlock()
    {
        var walk = new List<int>();
        for (var d = 0; d < 15; d++)
            for (var i = 0; i < 8; i++)
            {
                var row = d % 2 == 1 ? i : d - i;
                var column = d - row;
                if (row is >= 0 and < 8 && column is >= 0 and < 8) walk.Add(row * 8 + column);
            }
        Assert.Equal(walk, JpegEntropy.ZigZag.Select(p => (int)p));

        byte[][] quant = [W.Quant(2)];
        foreach (var (k, horizontal) in new[] { (1, true), (2, false) })
        {
            var zz = new short[64];
            zz[k] = 37;
            var image = JpegDecoder.Decode(W.Write(8, 8, [new W.Component(1, 1, 1, 0, 0, 0, [[zz]])], quant));
            byte At(int y, int x) => image.Samples[y * 8 + x];
            for (var y = 0; y < 8; y++)
                for (var x = 0; x < 8; x++)
                    Assert.Equal(horizontal ? At(0, x) : At(y, 0), At(y, x));
            Assert.True(horizontal ? At(0, 0) > At(0, 7) : At(0, 0) > At(7, 0));
        }
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
