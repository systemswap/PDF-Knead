using System.Numerics;
using PdfKnead.Core.Pdf.Filters.Jbig2;
using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// ITU-T T.800 A.6 and A.7 one rule at a time: the coding, quantisation, region and progression
/// segments and the segments that carry packet headers. Header rules are checked on the values the decoder reads (<see cref="JpxCodestream"/>,
/// <see cref="JpxTile"/>); rules about the data are checked on images: codestreams written here by a small encoder after the standard, so the
/// decoded image is compared with the samples it was made from.
/// </summary>
public sealed partial class JpxMarkerTests
{
    // ================================================================ A.6.1 COD

    /// <summary>
    /// T.800 A.6.1: COD gives the default for all components, COC that of one component; the tile-part header of the first tile-part has its
    /// own; the precedence is tile COC, tile COD, main COC, main COD — so the main COC of component 1 is covered by the tile COD. A COD in a later
    /// tile-part of the same tile is not read.
    /// </summary>
    [Fact]
    public void Cod_MainAndTileHeader_PrecedenceAsA61()
    {
        var siz = AnnexA67.Siz(16, 16, 3);
        byte[][] main = [AnnexA67.Cod(levels: 5), AnnexA67.Coc(3, 1, levels: 4), AnnexA67.Qcd0(2, 16)];
        byte[][] tile = [AnnexA67.Cod(levels: 3), AnnexA67.Coc(3, 2, levels: 2)];
        Assert.Equal([5, 4, 5], AnnexA67.Levels(AnnexA67.Stream(siz, main, (0, [], []))));
        Assert.Equal([3, 3, 2], AnnexA67.Levels(AnnexA67.Stream(siz, main, (0, tile, []))));
        // a COD in the second tile-part of the tile counts for nothing
        Assert.Equal([5, 4, 5], AnnexA67.Levels(AnnexA67.Stream(siz, main, (0, [], []), (0, [AnnexA67.Cod(levels: 1)], []))));
        Assert.Equal([3, 3, 2], AnnexA67.Levels(AnnexA67.Stream(siz, main, (0, tile, []), (0, [AnnexA67.Cod(levels: 1)], []))));
    }

    /// <summary>
    /// T.800 A.6.1, equation A-2: Lcod is 12 with the largest precincts and 13 + NL with precincts of its own; both are read. One byte less
    /// than 12 leaves no COD (refused: COD is required), a COD with precincts of its own but only 12 + NL bytes is refused.
    /// </summary>
    [Fact]
    public void CodLength_AsEquationA2()
    {
        var siz = AnnexA67.Siz(16, 16, 1);
        var qcd = AnnexA67.Qcd0(2, 7);
        static byte[] Read(byte[] siz, byte[] cod, byte[] qcd) => AnnexA67.Stream(siz, [cod, qcd], (0, [], []));

        var shortest = AnnexA67.Cod(levels: 2);
        Assert.Equal(12, AnnexA67.Length(shortest));
        var coding = AnnexA67.Parse(Read(siz, shortest, qcd)).Main.Coding!;
        Assert.Equal(2, coding.Levels);
        Assert.Empty(coding.Precincts);

        var own = AnnexA67.Cod(scod: 1, levels: 2, precincts: [0x33, 0x44, 0x55]);
        Assert.Equal(13 + 2, AnnexA67.Length(own));
        Assert.Equal([0x33, 0x44, 0x55], AnnexA67.Parse(Read(siz, own, qcd)).Main.Coding!.Precincts);

        var eleven = AnnexA67.Seg(0xFF52, shortest[4..^1]);
        Assert.Equal(11, AnnexA67.Length(eleven));
        Assert.Throws<JpxException>(() => AnnexA67.Parse(Read(siz, eleven, qcd)));

        var missing = AnnexA67.Cod(scod: 1, levels: 2, precincts: [0x33, 0x44]);
        Assert.Equal(12 + 2, AnnexA67.Length(missing));
        Assert.Throws<JpxException>(() => AnnexA67.Parse(Read(siz, missing, qcd)));
    }

    /// <summary>T.800 A.6.1, Table A.13: Scod with bit 0 clear — no precinct bytes, PPx = PPy = 15 at every resolution.</summary>
    [Fact]
    public void Scod_Bit0Clear_PrecinctsOf15()
    {
        var data = AnnexA67.Stream(AnnexA67.Siz(16, 16, 1), [AnnexA67.Cod(levels: 3), AnnexA67.Qcd0(2, 10)], (0, [], []));
        var coding = AnnexA67.Parse(data).Main.Coding!;
        var resolutions = AnnexA67.Tile(data)[0].Resolutions;
        for (var r = 0; r <= 3; r++)
        {
            Assert.Equal((15, 15), (coding.PrecinctWidth(r), coding.PrecinctHeight(r)));
            Assert.Equal((15, 15), (resolutions[r].PrecinctWidth, resolutions[r].PrecinctHeight));
        }
    }

    /// <summary>
    /// T.800 A.6.1, Table A.13: Scod bit 1 — an SOP segment (0xFF91, Lsop 4, Nsop counting up) in front of every packet; the image is the same
    /// as without them, and it is the source.
    /// </summary>
    [Fact]
    public void Scod_SopSegments_SameImage()
    {
        var plain = AnnexA67Encoder.Rich();
        var withSop = AnnexA67Encoder.Rich();
        withSop.Sop = true;
        var cs = withSop.Build(withSop.Packets());
        Assert.Equal(32 * 6, cs.Length - plain.Build(plain.Packets()).Length);
        Assert.Equal(2, cs[AnnexA67.Find(cs, 0xFF52) + 4] & 2);
        Assert.Equal(plain.Samples, AnnexA67.Decoded(cs));
    }

    /// <summary>T.800 A.6.1, Table A.13: Scod bit 2 — the EPH marker (0xFF92) behind every packet header; the image is the source.</summary>
    [Fact]
    public void Scod_EphMarkers_SameImage()
    {
        var plain = AnnexA67Encoder.Rich();
        var withEph = AnnexA67Encoder.Rich();
        withEph.Eph = true;
        var cs = withEph.Build(withEph.Packets());
        Assert.Equal(32 * 2, cs.Length - plain.Build(plain.Packets()).Length);
        Assert.Equal(4, cs[AnnexA67.Find(cs, 0xFF52) + 4] & 4);
        Assert.Equal(plain.Samples, AnnexA67.Decoded(cs));
    }

    /// <summary>
    /// T.800 A.6.1, Table A.14: the number of layers in SGcod. A codestream of two layers in LRCP with the field set to 1 is read as one layer:
    /// the same image as the codestream of the layer-0 packets alone, not the source.
    /// </summary>
    [Fact]
    public void Layers_SetToOne_OnlyLayerZeroRead()
    {
        var enc = AnnexA67Encoder.Rich();
        var packets = enc.Packets();
        var full = enc.Build(packets);
        Assert.Equal(enc.Samples, AnnexA67.Decoded(full));
        var one = AnnexA67.Patch(full, 0xFF52, 3, 1);
        var layer0 = AnnexA67.Patch(enc.Build([.. packets.Where(p => p.L == 0)]), 0xFF52, 3, 1);
        Assert.Equal(AnnexA67.Decoded(layer0), AnnexA67.Decoded(one));
        Assert.False(AnnexA67.Gives(one, enc.Samples));
    }

    /// <summary>
    /// T.800 A.6.1, Table A.15: NL 0 to 32 — 32 is read, 33 refused; with NL 0 there is no wavelet transformation, the plane is the decoded
    /// coefficients with the level shift; the first precinct byte belongs to the lowest resolution (an exponent 0 there is allowed, at a
    /// higher resolution not).
    /// </summary>
    [Fact]
    public void SPcod_LevelsAndPrecinctBytes_AsTableA15()
    {
        var siz = AnnexA67.Siz(16, 16, 1);
        Assert.Equal(32, AnnexA67.Parse(AnnexA67.Stream(siz, [AnnexA67.Cod(levels: 32), AnnexA67.Qcd0(2, 97)], (0, [], []))).Main.Coding!.Levels);
        Assert.Throws<JpxException>(() => AnnexA67.Parse(AnnexA67.Stream(siz, [AnnexA67.Cod(levels: 33), AnnexA67.Qcd0(2, 100)], (0, [], []))));

        var flat = AnnexA67.Noise(256, 3);
        var enc = new AnnexA67Encoder { Samples = [[.. flat.Select(v => v - 128)]], Direct = true, Levels = 0 };
        Assert.Equal([flat], AnnexA67.Decoded(enc.Build(enc.Packets())));

        static byte[] WithPrecincts(byte[] siz, params byte[] precincts) =>
            AnnexA67.Stream(siz, [AnnexA67.Cod(scod: 1, levels: 2, precincts: precincts), AnnexA67.Qcd0(2, 7)], (0, [], []));
        Assert.Equal(0, AnnexA67.Tile(WithPrecincts(siz, 0x00, 0x77, 0x77))[0].Resolutions[0].PrecinctWidth);
        Assert.Throws<JpxException>(() => AnnexA67.Tile(WithPrecincts(siz, 0x77, 0x00, 0x77)));
    }

    /// <summary>
    /// T.800 A.6.1, Table A.16, B.12.1.1–B.12.1.5: the progression order 0 LRCP, 1 RLCP, 2 RPCL, 3 PCRL, 4 CPRL. The packets of two layers, two
    /// resolutions, two components and four precincts each are written in the order the loops of B.12.1 give (written out in
    /// <see cref="AnnexA67Encoder.Sequence"/>, the position loops over the reference grid as there); the decoder gives the source back, and the
    /// same packets read in the next order do not.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ProgressionOrder_PacketsAsB12(int order)
    {
        var sequences = Enumerable.Range(0, 5).Select(o => AnnexA67Encoder.Rich(o).Sequence()).ToList();
        for (var a = 0; a < 5; a++)
        {
            Assert.Equal(32, sequences[a].Distinct().Count());
            for (var b = a + 1; b < 5; b++) Assert.NotEqual(sequences[a], sequences[b]);
        }
        var enc = AnnexA67Encoder.Rich(order);
        var cs = enc.Build(enc.Packets());
        Assert.Equal(enc.Samples, AnnexA67.Decoded(cs));
        Assert.False(AnnexA67.Gives(AnnexA67.Patch(cs, 0xFF52, 1, (order + 1) % 5), enc.Samples));
    }

    /// <summary>
    /// T.800 A.6.1, Table A.17: the multiple component transformation field — 0 none (the planes are the components), 1 with the 5-3 filter
    /// the RCT (the planes are R, G, B again), 1 with the 9-7 filter the ICT (checked on coefficients given directly, without decomposition:
    /// the planes are the ICT of the values the same codestream gives with the field 0).
    /// </summary>
    [Fact]
    public void ComponentTransform_AsTableA17()
    {
        int[][] rgb = [AnnexA67.Noise(256, 21), AnnexA67.Noise(256, 22), AnnexA67.Noise(256, 23)];
        var none = new AnnexA67Encoder { Samples = rgb };
        Assert.Equal(rgb, AnnexA67.Decoded(none.Build(none.Packets())));

        var rct = new AnnexA67Encoder { Samples = rgb, Mct = 1 };
        var cs = rct.Build(rct.Packets());
        Assert.Equal(rgb, AnnexA67.Decoded(cs));
        Assert.False(AnnexA67.Gives(AnnexA67.Patch(cs, 0xFF52, 4, 0), rgb));

        // 9-7: step 2 (exponent 7, mantissa 0), so an index q gives the value ±(2|q| + 1)
        var random = new Random(24);
        int[][] q = [[.. Enumerable.Range(0, 64).Select(_ => random.Next(-30, 31))], [.. Enumerable.Range(0, 64).Select(_ => random.Next(-15, 16))],
            [.. Enumerable.Range(0, 64).Select(_ => random.Next(-15, 16))]];
        var ict = new AnnexA67Encoder { Width = 8, Height = 8, Samples = q, Direct = true, Levels = 0, Reversible = false, Mct = 1 };
        var data = ict.Build(ict.Packets());
        static double V(int x) => x == 0 ? 0 : Math.Sign(x) * (2 * Math.Abs(x) + 1);
        var plain = AnnexA67.Decoded(AnnexA67.Patch(data, 0xFF52, 4, 0));
        for (var c = 0; c < 3; c++)
        for (var i = 0; i < 64; i++)
            Assert.Equal(V(q[c][i]) + 128, plain[c][i]);
        var planes = AnnexA67.Decoded(data);
        for (var i = 0; i < 64; i++)
        {
            double y = V(q[0][i]), cb = V(q[1][i]), cr = V(q[2][i]);
            Assert.InRange(planes[0][i] - (y + 1.402 * cr + 128), -1, 1);
            Assert.InRange(planes[1][i] - (y - 0.34413 * cb - 0.71414 * cr + 128), -1, 1);
            Assert.InRange(planes[2][i] - (y + 1.772 * cb + 128), -1, 1);
        }
    }

    /// <summary>
    /// T.800 A.6.1, Table A.18: the code-block width and height exponents are the field values plus 2, the values 0 to 8 (4 to 1 024), the
    /// two exponents together at most 12; anything else is refused.
    /// </summary>
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(4, 4, true)]
    [InlineData(8, 0, true)]
    [InlineData(0, 8, true)]
    [InlineData(5, 3, true)]
    [InlineData(9, 0, false)]
    [InlineData(0, 9, false)]
    [InlineData(5, 4, false)]
    [InlineData(5, 5, false)]
    [InlineData(0x14, 0, false)]
    public void CodeBlockSize_AsTableA18(int width, int height, bool read)
    {
        var data = AnnexA67.Stream(AnnexA67.Siz(16, 16, 1), [AnnexA67.Cod(levels: 1, cbw: width, cbh: height), AnnexA67.Qcd0(2, 4)], (0, [], []));
        if (!read)
        {
            Assert.Throws<JpxException>(() => AnnexA67.Parse(data));
            return;
        }
        var coding = AnnexA67.Parse(data).Main.Coding!;
        Assert.Equal((width + 2, height + 2), (coding.BlockWidth, coding.BlockHeight));
    }

    /// <summary>
    /// T.800 A.6.1, Table A.19: the code-block style, bit by bit — code-blocks coded with the bit (bypass, context reset, termination on each
    /// pass, vertically causal contexts, segmentation symbols) give the source back with the bit set and not with it cleared; the predictable
    /// termination (bit 4) only restricts the encoder: the same image with and without it.
    /// </summary>
    [Theory]
    [InlineData(0x01, true)]
    [InlineData(0x02, true)]
    [InlineData(0x04, true)]
    [InlineData(0x08, true)]
    [InlineData(0x10, false)]
    [InlineData(0x20, true)]
    public void CodeBlockStyle_EachBit_AsTableA19(int bit, bool matters)
    {
        var enc = new AnnexA67Encoder { Samples = [AnnexA67.Noise(256, 31)], Levels = 1, Xcb = 4, Ycb = 4, Style = bit };
        var cs = enc.Build(enc.Packets());
        Assert.Equal(enc.Samples, AnnexA67.Decoded(cs));
        var cleared = AnnexA67.Patch(cs, 0xFF52, 8, 0);
        if (matters) Assert.False(AnnexA67.Gives(cleared, enc.Samples));
        else Assert.Equal(enc.Samples, AnnexA67.Decoded(cleared));
    }

    /// <summary>T.800 A.6.1, Table A.20: wavelet 1 is the reversible 5-3 filter (the source comes back exactly), 0 the irreversible 9-7 filter.</summary>
    [Fact]
    public void Wavelet_AsTableA20()
    {
        var enc = new AnnexA67Encoder { Samples = [AnnexA67.Noise(256, 41)], Levels = 2 };
        var cs = enc.Build(enc.Packets());
        Assert.True(AnnexA67.Parse(cs).Main.Coding!.Reversible);
        Assert.Equal(enc.Samples, AnnexA67.Decoded(cs));
        var irreversible = AnnexA67.Patch(cs, 0xFF52, 9, 0);
        Assert.False(AnnexA67.Parse(irreversible).Main.Coding!.Reversible);
        Assert.False(AnnexA67.Gives(irreversible, enc.Samples));
    }

    /// <summary>T.800 A.6.1, Table A.21: in each precinct byte the lower four bits are PPx, the upper four PPy.</summary>
    [Fact]
    public void PrecinctSize_LowerBitsWidthUpperBitsHeight()
    {
        var data = AnnexA67.Stream(AnnexA67.Siz(16, 16, 1), [AnnexA67.Cod(scod: 1, levels: 1, precincts: [0x54, 0x76]), AnnexA67.Qcd0(2, 4)], (0, [], []));
        var coding = AnnexA67.Parse(data).Main.Coding!;
        Assert.Equal((4, 5, 6, 7), (coding.PrecinctWidth(0), coding.PrecinctHeight(0), coding.PrecinctWidth(1), coding.PrecinctHeight(1)));
        var resolutions = AnnexA67.Tile(data)[0].Resolutions;
        Assert.Equal((4, 5), (resolutions[0].PrecinctWidth, resolutions[0].PrecinctHeight));
        Assert.Equal((6, 7), (resolutions[1].PrecinctWidth, resolutions[1].PrecinctHeight));
    }

    // ================================================================ A.6.2 COC

    /// <summary>
    /// T.800 A.6.2: a COC of the main header sets only its component; the tile's COC comes before the tile COD and the main COC; a COC in a
    /// later tile-part of the tile is not read.
    /// </summary>
    [Fact]
    public void Coc_OneComponent_PrecedenceAsA62()
    {
        var siz = AnnexA67.Siz(16, 16, 3);
        byte[][] main = [AnnexA67.Cod(levels: 5), AnnexA67.Coc(3, 1, levels: 4), AnnexA67.Qcd0(2, 16)];
        Assert.Equal([5, 4, 5], AnnexA67.Levels(AnnexA67.Stream(siz, main, (0, [], []))));
        byte[][] tile = [AnnexA67.Cod(levels: 3), AnnexA67.Coc(3, 1, levels: 1)];
        Assert.Equal([3, 1, 3], AnnexA67.Levels(AnnexA67.Stream(siz, main, (0, tile, []))));
        Assert.Equal([3, 1, 3], AnnexA67.Levels(AnnexA67.Stream(siz, main, (0, tile, []), (0, [AnnexA67.Coc(3, 0, levels: 2)], []))));
    }

    /// <summary>
    /// T.800 A.6.2, equation A-3: Lcoc is 9 or 10 + NL with fewer than 257 components, 10 or 11 + NL with more; all are read. A COC with
    /// precincts of its own but without the precinct bytes is refused.
    /// </summary>
    [Fact]
    public void CocLength_AsEquationA3()
    {
        foreach (var components in new[] { 3, 257 })
        {
            var siz = AnnexA67.Siz(16, 16, components);
            var wide = components >= 257 ? 1 : 0;
            byte[] Main(byte[] coc) => AnnexA67.Stream(siz, [AnnexA67.Cod(levels: 2), coc, AnnexA67.Qcd0(2, 7)], (0, [], []));

            var plain = AnnexA67.Coc(components, 1, levels: 1);
            Assert.Equal(9 + wide, AnnexA67.Length(plain));
            Assert.Equal(1, AnnexA67.Parse(Main(plain)).Main.ComponentCoding[1]!.Levels);

            var own = AnnexA67.Coc(components, 1, scoc: 1, levels: 1, precincts: [0x33, 0x44]);
            Assert.Equal(10 + wide + 1, AnnexA67.Length(own));
            Assert.Equal([0x33, 0x44], AnnexA67.Parse(Main(own)).Main.ComponentCoding[1]!.Precincts);

            Assert.Throws<JpxException>(() => AnnexA67.Parse(Main(AnnexA67.Coc(components, 1, scoc: 1, levels: 1))));
        }
    }

    /// <summary>
    /// T.800 A.6.2, Table A.22: Ccoc names the component (8 bits below 257 components, 16 bits from 257 on), only that component gets the
    /// coding; an index beyond the components is passed over.
    /// </summary>
    [Fact]
    public void Coc_Segment_AsTableA22()
    {
        static JpxCoding?[] Components(int components, int index) =>
            AnnexA67.Parse(AnnexA67.Stream(AnnexA67.Siz(16, 16, components), [AnnexA67.Cod(levels: 5), AnnexA67.Coc(components, index, levels: 2), AnnexA67.Qcd0(2, 16)],
                (0, [], []))).Main.ComponentCoding;

        var three = Components(3, 1);
        Assert.Equal(2, three[1]!.Levels);
        Assert.Null(three[0]);
        Assert.Null(three[2]);
        var wide = Components(257, 256);
        Assert.Equal(2, wide[256]!.Levels);
        Assert.Equal(1, wide.Count(c => c is not null));
        Assert.All(Components(3, 3), c => Assert.Null(c));
    }

    /// <summary>T.800 A.6.2, Table A.23: Scoc 0 — the component has precincts of 15/15 although COD has its own; Scoc 1 — its own bytes.</summary>
    [Fact]
    public void Scoc_Precincts_AsTableA23()
    {
        static JpxTileComponent[] With(byte[] coc) => AnnexA67.Tile(AnnexA67.Stream(AnnexA67.Siz(16, 16, 3),
            [AnnexA67.Cod(scod: 1, levels: 1, precincts: [0x33, 0x44]), coc, AnnexA67.Qcd0(2, 4)], (0, [], [])));

        var largest = With(AnnexA67.Coc(3, 1, scoc: 0, levels: 1));
        var own = With(AnnexA67.Coc(3, 1, scoc: 1, levels: 1, precincts: [0x55, 0x66]));
        for (var r = 0; r <= 1; r++)
        {
            Assert.Equal((15, 15), (largest[1].Resolutions[r].PrecinctWidth, largest[1].Resolutions[r].PrecinctHeight));
            Assert.Equal((5 + r, 5 + r), (own[1].Resolutions[r].PrecinctWidth, own[1].Resolutions[r].PrecinctHeight));
            Assert.Equal((3 + r, 3 + r), (own[0].Resolutions[r].PrecinctWidth, own[0].Resolutions[r].PrecinctHeight));
        }
    }

    // ================================================================ A.6.3 RGN

    /// <summary>
    /// T.800 A.6.3: an RGN of the main header holds for every tile without its own, an RGN of a tile's first tile-part for that tile and before
    /// the main one; the other components keep 0; an RGN in a later tile-part is not read.
    /// </summary>
    [Fact]
    public void Rgn_MainAndTileHeader_PrecedenceAsA63()
    {
        var data = AnnexA67.Stream(AnnexA67.Siz(32, 16, 3, 16, 16), [AnnexA67.Cod(levels: 1), AnnexA67.Qcd0(2, 4), AnnexA67.Rgn(3, 0, 0, 3)],
            (0, [], []), (1, [AnnexA67.Rgn(3, 0, 0, 5)], []), (0, [AnnexA67.Rgn(3, 0, 0, 7)], []));
        Assert.Equal([3, 0, 0], AnnexA67.Tile(data, 0).Select(tc => tc.Roi));
        Assert.Equal([5, 0, 0], AnnexA67.Tile(data, 1).Select(tc => tc.Roi));
    }

    /// <summary>
    /// T.800 A.6.3, Table A.24: RGN of 0xFF5E, Lrgn 5 with an 8-bit Crgn (fewer than 257 components) or 6 with a 16-bit one, then Srgn and
    /// SPrgn — only the named component gets the shift.
    /// </summary>
    [Fact]
    public void Rgn_Segment_AsTableA24()
    {
        static int?[] Shifts(int components, byte[] rgn) =>
            AnnexA67.Parse(AnnexA67.Stream(AnnexA67.Siz(16, 16, components), [AnnexA67.Cod(levels: 1), AnnexA67.Qcd0(2, 4), rgn], (0, [], []))).Main.RoiShift;

        var narrow = AnnexA67.Rgn(3, 2, 0, 7);
        Assert.Equal(5, AnnexA67.Length(narrow));
        Assert.Equal([null, null, 7], Shifts(3, narrow));
        var wide = AnnexA67.Rgn(257, 256, 0, 7);
        Assert.Equal(6, AnnexA67.Length(wide));
        var shifts = Shifts(257, wide);
        Assert.Equal(7, shifts[256]);
        Assert.Equal(1, shifts.Count(s => s is not null));
    }

    /// <summary>T.800 A.6.3, Table A.25: Srgn 0 is the implicit ROI (the shift is taken), Srgn 1 is no style of this part (the segment is passed over).</summary>
    [Fact]
    public void Rgn_Style_AsTableA25()
    {
        static int?[] Shifts(int srgn) => AnnexA67.Parse(AnnexA67.Stream(AnnexA67.Siz(16, 16, 1), [AnnexA67.Cod(levels: 1), AnnexA67.Qcd0(2, 4), AnnexA67.Rgn(1, 0, srgn, 4)],
            (0, [], []))).Main.RoiShift;
        Assert.Equal([4], Shifts(0));
        Assert.Equal([null], Shifts(1));
    }

    /// <summary>
    /// T.800 A.6.3, Table A.26: SPrgn is the shift of the region coefficients above the background — coefficients written 2^s larger (here
    /// s = 3, the background below 2^3) come back divided by 2^s, the smaller ones as they are: the image is the source.
    /// </summary>
    [Fact]
    public void Rgn_Shift_RegionScaledDown()
    {
        var random = new Random(51);
        var source = new int[64];
        var written = new int[64];
        for (var i = 0; i < 64; i++)
        {
            var region = i % 8 < 4;
            source[i] = region ? random.Next(-100, 101) : random.Next(-7, 8);
            written[i] = region ? source[i] << 3 : source[i];
        }
        var enc = new AnnexA67Encoder { Width = 8, Height = 8, Samples = [written], Direct = true, Levels = 0, ExtraPlanes = 3 };
        var cs = enc.Build(enc.Packets(), main: [AnnexA67.Rgn(1, 0, 0, 3)]);
        Assert.Equal([source.Select(v => v + 128).ToArray()], AnnexA67.Decoded(cs));
    }

    // ================================================================ A.6.4 QCD

    /// <summary>
    /// T.800 A.6.4: QCD is the default quantisation, QCC that of one component; the precedence is tile QCC, tile QCD, main QCC, main QCD — the tile
    /// QCD covers the main QCC; a QCD in a later tile-part of the tile is not read. The guard bits tell the segments apart.
    /// </summary>
    [Fact]
    public void Qcd_MainAndTileHeader_PrecedenceAsA64()
    {
        var siz = AnnexA67.Siz(16, 16, 3);
        byte[][] main = [AnnexA67.Cod(levels: 1), AnnexA67.Qcd0(1, 4), AnnexA67.Qcc(3, 1, 2 << 5, 0x50, 0x50, 0x50, 0x50)];
        byte[][] tile = [AnnexA67.Qcd0(3, 4), AnnexA67.Qcc(3, 2, 4 << 5, 0x50, 0x50, 0x50, 0x50)];
        static int[] Guards(byte[] data) => [.. AnnexA67.Tile(data).Select(tc => tc.Quantization.GuardBits)];
        Assert.Equal([1, 2, 1], Guards(AnnexA67.Stream(siz, main, (0, [], []))));
        Assert.Equal([3, 3, 4], Guards(AnnexA67.Stream(siz, main, (0, tile, []))));
        Assert.Equal([1, 2, 1], Guards(AnnexA67.Stream(siz, main, (0, [], []), (0, [AnnexA67.Qcd0(5, 4)], []))));
    }

    /// <summary>
    /// T.800 A.6.4, equation A-4: the number of values follows from Lqcd — 4 + 3·NL without quantisation, 5 derived, 5 + 6·NL expounded. A QCD
    /// with style 2 but no value (Lqcd 3) leaves no QCD: refused.
    /// </summary>
    [Fact]
    public void QcdLength_AsEquationA4()
    {
        static JpxQuantization Read(byte[] qcd) =>
            AnnexA67.Parse(AnnexA67.Stream(AnnexA67.Siz(16, 16, 1), [AnnexA67.Cod(levels: 2), qcd], (0, [], []))).Main.Quantization!;

        var none = AnnexA67.Qcd(0x40, [.. Enumerable.Repeat(0x50, 7)]);
        Assert.Equal(4 + 3 * 2, AnnexA67.Length(none));
        Assert.Equal(7, Read(none).Exponents.Length);
        var derived = AnnexA67.Qcd(0x41, 0x5000);
        Assert.Equal(5, AnnexA67.Length(derived));
        Assert.Single(Read(derived).Exponents);
        var expounded = AnnexA67.Qcd(0x42, [.. Enumerable.Repeat(0x5000, 7)]);
        Assert.Equal(5 + 6 * 2, AnnexA67.Length(expounded));
        Assert.Equal(7, Read(expounded).Exponents.Length);
        Assert.Equal(7, Read(expounded).Mantissas.Length);
        Assert.Throws<JpxException>(() => Read(AnnexA67.Qcd(0x42)));
    }

    /// <summary>
    /// T.800 A.6.4, Table A.27 with F.3.1: the values of SPqcd stand per sub-band in the order LL, then HL, LH, HH of each resolution upwards —
    /// with distinct exponents and mantissas each band gets its own (magnitude bit planes and step size after E-3).
    /// </summary>
    [Fact]
    public void Qcd_ValuesInTheOrderOfF31()
    {
        int[] exponents = [8, 9, 10, 11, 12, 13, 14], mantissas = [7, 107, 207, 307, 407, 507, 607];
        var qcd = AnnexA67.Qcd(0x42, [.. exponents.Select((e, i) => e << 11 | mantissas[i])]);
        var tc = AnnexA67.Tile(AnnexA67.Stream(AnnexA67.Siz(16, 16, 1), [AnnexA67.Cod(levels: 2, wavelet: 0), qcd], (0, [], [])))[0];
        for (var r = 0; r <= 2; r++)
        for (var o = 0; o < tc.Resolutions[r].Bands.Length; o++)
        {
            var band = tc.Resolutions[r].Bands[o];
            var index = r == 0 ? 0 : 1 + 3 * (r - 1) + o;
            var gain = r == 0 ? 0 : o == 2 ? 2 : 1;
            Assert.Equal(2 + exponents[index] - 1, band.MagnitudeBits);
            Assert.Equal(Math.Pow(2, 8 + gain - exponents[index]) * (1 + mantissas[index] / 2048.0), band.Step, 1e-6);
        }
    }

    /// <summary>
    /// T.800 A.6.4, Table A.28: the lower five bits of Sqcd are the style (0 none with one byte per value, 1 derived and 2 expounded with two),
    /// the upper three the number of guard bits, 0 to 7.
    /// </summary>
    [Theory]
    [InlineData(0x00, 0, 0)]
    [InlineData(0x01, 1, 0)]
    [InlineData(0x02, 2, 0)]
    [InlineData(0x22, 2, 1)]
    [InlineData(0x42, 2, 2)]
    [InlineData(0x62, 2, 3)]
    [InlineData(0x82, 2, 4)]
    [InlineData(0xA2, 2, 5)]
    [InlineData(0xC2, 2, 6)]
    [InlineData(0xE2, 2, 7)]
    public void Sqcd_StyleAndGuardBits_AsTableA28(int sqcd, int style, int guard)
    {
        var q = AnnexA67.Parse(AnnexA67.Stream(AnnexA67.Siz(16, 16, 1), [AnnexA67.Cod(levels: 1), AnnexA67.Seg(0xFF5C, (byte)sqcd, 0x48, 0x01, 0x50, 0x02)],
            (0, [], []))).Main.Quantization!;
        Assert.Equal((style, guard), (q.Style, q.GuardBits));
        if (style == 0)
        {
            Assert.Equal([9, 0, 10, 0], q.Exponents);
        }
        else
        {
            Assert.Equal([9, 10], q.Exponents);
            Assert.Equal([1, 2], q.Mantissas);
        }
    }

    /// <summary>
    /// T.800 A.6.4, Table A.28 with equation E-5: derived quantisation gives only the LL value (ε0, μ0); a band of a resolution with nb levels
    /// gets the exponent ε0 − NL + nb and the mantissa μ0.
    /// </summary>
    [Fact]
    public void Sqcd_Derived_AsEquationE5()
    {
        const int e0 = 12, m0 = 300, levels = 3;
        var tc = AnnexA67.Tile(AnnexA67.Stream(AnnexA67.Siz(16, 16, 1), [AnnexA67.Cod(levels: levels, wavelet: 0), AnnexA67.Qcd(0x41, e0 << 11 | m0)],
            (0, [], [])))[0];
        for (var r = 0; r <= levels; r++)
        for (var o = 0; o < tc.Resolutions[r].Bands.Length; o++)
        {
            var nb = r == 0 ? levels : levels - r + 1;
            var exponent = e0 - levels + nb;
            var gain = r == 0 ? 0 : o == 2 ? 2 : 1;
            var band = tc.Resolutions[r].Bands[o];
            Assert.Equal(2 + exponent - 1, band.MagnitudeBits);
            Assert.Equal(Math.Pow(2, 8 + gain - exponent) * (1 + m0 / 2048.0), band.Step, 1e-6);
        }
    }

    /// <summary>
    /// T.800 A.6.4, Table A.29: without quantisation each value is the exponent in the upper five bits, the lower three are reserved — 0x48 and
    /// 0x4F both give 9; with 2 guard bits the band has 10 magnitude bit planes.
    /// </summary>
    [Fact]
    public void SPqcd_WithoutQuantisation_ExponentInTheUpperFiveBits()
    {
        foreach (var value in new[] { 0x48, 0x4F })
        {
            var data = AnnexA67.Stream(AnnexA67.Siz(16, 16, 1), [AnnexA67.Cod(levels: 0), AnnexA67.Qcd(0x40, value)], (0, [], []));
            Assert.Equal([9], AnnexA67.Parse(data).Main.Quantization!.Exponents);
            Assert.Equal(10, AnnexA67.Tile(data)[0].Resolutions[0].Bands[0].MagnitudeBits);
        }
    }

    /// <summary>
    /// T.800 A.6.4, Table A.30 with equation E-3: with quantisation each value has the mantissa in the lower eleven bits and the exponent in the
    /// upper five — 0x4801 is ε 9, μ 1 and 0xFFFF ε 31, μ 2047; the step size of the band follows.
    /// </summary>
    [Theory]
    [InlineData(0x4801, 9, 1)]
    [InlineData(0xFFFF, 31, 2047)]
    public void SPqcd_WithQuantisation_MantissaAndExponent(int value, int exponent, int mantissa)
    {
        var data = AnnexA67.Stream(AnnexA67.Siz(16, 16, 1), [AnnexA67.Cod(levels: 0, wavelet: 0), AnnexA67.Qcd(0x42, value)], (0, [], []));
        var q = AnnexA67.Parse(data).Main.Quantization!;
        Assert.Equal([exponent], q.Exponents);
        Assert.Equal([mantissa], q.Mantissas);
        var band = AnnexA67.Tile(data)[0].Resolutions[0].Bands[0];
        Assert.Equal(Math.Pow(2, 8 - exponent) * (1 + mantissa / 2048.0), band.Step, Math.Pow(2, 8 - exponent) * 1e-6);
    }

    // ================================================================ A.6.5 QCC

    /// <summary>
    /// T.800 A.6.5: a QCC of the main header holds for its component only while the tile has no QCD; the tile's QCC comes before the tile QCD;
    /// a QCC in a later tile-part of the tile is not read.
    /// </summary>
    [Fact]
    public void Qcc_OneComponent_PrecedenceAsA65()
    {
        var siz = AnnexA67.Siz(16, 16, 3);
        byte[][] main = [AnnexA67.Cod(levels: 1), AnnexA67.Qcd0(1, 4), AnnexA67.Qcc(3, 1, 2 << 5, 0x50, 0x50, 0x50, 0x50)];
        static int[] Guards(byte[] data) => [.. AnnexA67.Tile(data).Select(tc => tc.Quantization.GuardBits)];
        Assert.Equal([3, 3, 3], Guards(AnnexA67.Stream(siz, main, (0, [AnnexA67.Qcd0(3, 4)], []))));
        byte[][] tile = [AnnexA67.Qcd0(3, 4), AnnexA67.Qcc(3, 2, 4 << 5, 0x50, 0x50, 0x50, 0x50)];
        Assert.Equal([3, 3, 4], Guards(AnnexA67.Stream(siz, main, (0, tile, []))));
        Assert.Equal([3, 3, 4], Guards(AnnexA67.Stream(siz, main, (0, tile, []), (0, [AnnexA67.Qcc(3, 0, 6 << 5, 0x50, 0x50, 0x50, 0x50)], []))));
    }

    /// <summary>T.800 A.6.5, equation A-5: Lqcc is one byte (fewer than 257 components) or two longer than Lqcd — 6 + 6·NL and 7 + 6·NL expounded.</summary>
    [Fact]
    public void QccLength_AsEquationA5()
    {
        foreach (var components in new[] { 3, 257 })
        {
            var qcc = AnnexA67.Qcc(components, 1, 0x42, 0x5000, 0x5001, 0x5002, 0x5003);
            Assert.Equal(6 + 6 * 1 + (components >= 257 ? 1 : 0), AnnexA67.Length(qcc));
            var q = AnnexA67.Parse(AnnexA67.Stream(AnnexA67.Siz(16, 16, components), [AnnexA67.Cod(levels: 1), AnnexA67.Qcd0(2, 4), qcc], (0, [], [])))
                .Main.ComponentQuantization[1]!;
            Assert.Equal([10, 10, 10, 10], q.Exponents);
            Assert.Equal([0, 1, 2, 3], q.Mantissas);
        }
    }

    /// <summary>
    /// T.800 A.6.5, Table A.31: Cqcc names the component (8 bits below 257 components, 16 bits from 257 on), only that one gets the quantisation;
    /// an index beyond the components is passed over.
    /// </summary>
    [Fact]
    public void Qcc_Segment_AsTableA31()
    {
        static JpxQuantization?[] Components(int components, int index) =>
            AnnexA67.Parse(AnnexA67.Stream(AnnexA67.Siz(16, 16, components), [AnnexA67.Cod(levels: 1), AnnexA67.Qcd0(2, 4), AnnexA67.Qcc(components, index, 0x60, 0x48)],
                (0, [], []))).Main.ComponentQuantization;

        var three = Components(3, 1);
        Assert.Equal(3, three[1]!.GuardBits);
        Assert.Null(three[0]);
        Assert.Null(three[2]);
        var wide = Components(257, 256);
        Assert.Equal(3, wide[256]!.GuardBits);
        Assert.Equal(1, wide.Count(q => q is not null));
        Assert.All(Components(3, 3), q => Assert.Null(q));
    }

    // ================================================================ A.6.6 POC

    /// <summary>
    /// T.800 A.6.6: packets written in RPCL. With COD naming LRCP the image is wrong; a POC of the main header in RPCL comes before the COD of
    /// the main header and of the tile; a POC of the tile in RPCL comes before a POC of the main header in LRCP; without POC the tile COD comes
    /// before the main COD.
    /// </summary>
    [Fact]
    public void Poc_MainAndTileHeader_PrecedenceAsA66()
    {
        var enc = AnnexA67Encoder.Rich(2);
        var packets = enc.Packets();
        var lrcpCod = AnnexA67Encoder.Rich(0).Cod();
        Assert.Equal(enc.Samples, AnnexA67.Decoded(enc.Build(packets)));
        Assert.False(AnnexA67.Gives(AnnexA67.Patch(enc.Build(packets), 0xFF52, 1, 0), enc.Samples));
        var mainPoc = AnnexA67.Poc(2, (0, 0, 2, 2, 2, 2));
        Assert.Equal(enc.Samples, AnnexA67.Decoded(AnnexA67.Patch(enc.Build(packets, main: [mainPoc], tile: [lrcpCod]), 0xFF52, 1, 0)));
        var tilePoc = AnnexA67.Poc(2, (0, 0, 2, 2, 2, 2));
        Assert.Equal(enc.Samples, AnnexA67.Decoded(AnnexA67.Patch(enc.Build(packets, main: [AnnexA67.Poc(2, (0, 0, 2, 2, 2, 0))], tile: [tilePoc]), 0xFF52, 1, 0)));
        Assert.Equal(enc.Samples, AnnexA67.Decoded(AnnexA67.Patch(enc.Build(packets, tile: [enc.Cod()]), 0xFF52, 1, 0)));
    }

    /// <summary>
    /// T.800 A.6.6, equation A-6: Lpoc gives the number of entries, 7 bytes each below 257 components, 9 from 257 on. Two entries split RLCP
    /// at resolution 1 — the image is the source; with the first entry alone the packets of resolution 1 are not read.
    /// </summary>
    [Fact]
    public void PocLength_EntriesAsEquationA6()
    {
        var enc = AnnexA67Encoder.Rich(1);
        var packets = enc.Packets();
        var two = AnnexA67.Poc(2, (0, 0, 2, 1, 2, 1), (1, 0, 2, 2, 2, 1));
        Assert.Equal(2 + 2 * 7, AnnexA67.Length(two));
        Assert.Equal(enc.Samples, AnnexA67.Decoded(enc.Build(packets, main: [two])));
        Assert.False(AnnexA67.Gives(enc.Build(packets, main: [AnnexA67.Poc(2, (0, 0, 2, 1, 2, 1))]), enc.Samples));

        var wide = AnnexA67.Poc(257, (1, 0x0100, 3, 2, 0x0101, 4), (0, 5, 1, 33, 0x00FF, 2));
        Assert.Equal(2 + 2 * 9, AnnexA67.Length(wide));
        var read = AnnexA67.Parse(AnnexA67.Stream(AnnexA67.Siz(16, 16, 257), [AnnexA67.Cod(levels: 1), AnnexA67.Qcd0(2, 4), wide], (0, [], []))).Main.Progressions;
        Assert.Equal([new JpxProgression(1, 0x0100, 3, 2, 0x0101, 4), new JpxProgression(0, 5, 1, 33, 0x00FF, 2)], read);
    }

    /// <summary>
    /// T.800 A.6.6, Table A.32: RSpoc and CSpoc are the first resolution and component of an entry, both included — an entry from resolution 1
    /// and component 1 first, then the rest (LRCP); component 1 first, then the rest (CPRL): the image is the source.
    /// </summary>
    [Fact]
    public void Poc_StartsIncluded_AsTableA32()
    {
        foreach (var (order, entries) in new (int, (int, int, int, int, int, int)[])[]
        {
            (0, [(1, 1, 2, 2, 2, 0), (0, 0, 2, 2, 2, 0)]),
            (4, [(0, 1, 2, 2, 2, 4), (0, 0, 2, 2, 2, 4)]),
        })
        {
            var enc = AnnexA67Encoder.Rich(order);
            enc.Progressions = [.. entries];
            var sequence = enc.Sequence();
            Assert.All(sequence.Take(order == 0 ? 8 : 16), p => Assert.Equal(1, p.C));
            if (order == 0) Assert.All(sequence.Take(8), p => Assert.Equal(1, p.R));
            Assert.Equal(enc.Samples, AnnexA67.Decoded(enc.Build(enc.Packets(), main: [AnnexA67.Poc(2, entries)])));
        }
    }

    /// <summary>
    /// T.800 A.6.6, Table A.32: LYEpoc, REpoc and CEpoc end an entry, each excluded — an entry ending at layer 1, resolution 1 and component 1
    /// reads only layer 0 of resolution 0 of component 0, then an entry over everything in CPRL the rest (there layer 1 of component 0 comes
    /// next, so an end read as included would take packets out of turn): the image is the source.
    /// </summary>
    [Fact]
    public void Poc_EndsExcluded_AsTableA32()
    {
        var enc = AnnexA67Encoder.Rich(0);
        (int, int, int, int, int, int)[] entries = [(0, 0, 1, 1, 1, 0), (0, 0, 2, 2, 2, 4)];
        enc.Progressions = [.. entries];
        Assert.Equal([(0, 0, 0, 0), (0, 0, 0, 1), (0, 0, 0, 2), (0, 0, 0, 3)], enc.Sequence().Take(4));
        Assert.Equal((1, 0, 0, 0), enc.Sequence()[4]);
        Assert.Equal(enc.Samples, AnnexA67.Decoded(enc.Build(enc.Packets(), main: [AnnexA67.Poc(2, entries)])));
    }

    /// <summary>
    /// T.800 A.6.6, Table A.32: Ppoc is the progression of the entry, with the values of the COD field — an entry over everything in the order of
    /// the packets gives the source whatever COD says; the same entry in the next order does not.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Poc_OrderOfTheEntry_AsPpoc(int order)
    {
        var enc = AnnexA67Encoder.Rich(order);
        var packets = enc.Packets();
        var other = (order + 1) % 5;
        Assert.Equal(enc.Samples, AnnexA67.Decoded(AnnexA67.Patch(enc.Build(packets, main: [AnnexA67.Poc(2, (0, 0, 2, 2, 2, order))]), 0xFF52, 1, other)));
        Assert.False(AnnexA67.Gives(AnnexA67.Patch(enc.Build(packets, main: [AnnexA67.Poc(2, (0, 0, 2, 2, 2, other))]), 0xFF52, 1, order), enc.Samples));
    }

    /// <summary>
    /// T.800 A.6.6, Table A.32: the sizes and ranges of the fields — CEpoc 0 with fewer than 257 components means 256 (all components); an entry
    /// whose REpoc is not above RSpoc names nothing; with 257 components CSpoc and CEpoc have 16 bits (up to 16 383 and 16 384).
    /// </summary>
    [Fact]
    public void Poc_FieldRanges_AsTableA32()
    {
        var enc = AnnexA67Encoder.Rich(0);
        var packets = enc.Packets();
        Assert.Equal(enc.Samples, AnnexA67.Decoded(enc.Build(packets, main: [AnnexA67.Poc(2, (0, 0, 2, 2, 0, 0))])));
        Assert.Equal(enc.Samples, AnnexA67.Decoded(enc.Build(packets, main: [AnnexA67.Poc(2, (1, 0, 2, 1, 2, 0), (2, 0, 2, 1, 2, 1), (0, 0, 2, 2, 2, 0))])));
        var read = AnnexA67.Parse(AnnexA67.Stream(AnnexA67.Siz(16, 16, 257), [AnnexA67.Cod(levels: 1), AnnexA67.Qcd0(2, 4), AnnexA67.Poc(257, (32, 16383, 65535, 33, 16384, 4))],
            (0, [], []))).Main.Progressions;
        Assert.Equal([new JpxProgression(32, 16383, 65535, 33, 16384, 4)], read);
    }

    // ================================================================ A.7.4 PPM

    /// <summary>
    /// T.800 A.7.4: the packet headers in a PPM of the main header (Nppm = their length), the tile-part with the packet data alone — the image is
    /// the source; a PPT in the tile besides changes nothing (PPM holds), a PPM in a tile-part header is passed over — both, against A.7.5 and
    /// A.7.4, reported (Ppm_BesidePptOrInTheWrongHeader_Reported).
    /// </summary>
    [Fact]
    public void Ppm_PacketHeadersInTheMainHeader_SameImage()
    {
        var enc = AnnexA67Encoder.Rich();
        var packets = enc.Packets();
        var headers = AnnexA67Encoder.Headers(packets);
        var bodies = AnnexA67Encoder.Bodies(packets);
        byte[] ippm = [.. AnnexA67.U32(headers.Length), .. headers];
        byte[][] main = [.. enc.MainHeader(), AnnexA67.Ppm(0, ippm)];
        Assert.Equal(enc.Samples, AnnexA67.Decoded(AnnexA67.Stream(enc.Siz(), main, (0, [], bodies))));
        var besidePpt = AnnexA67.Read(AnnexA67.Stream(enc.Siz(), main, (0, [AnnexA67.Ppt(0, [.. Enumerable.Reverse(headers)])], bodies)));
        Assert.Equal(enc.Samples, besidePpt.Planes);
        var inTilePart = AnnexA67.Read(AnnexA67.Stream(enc.Siz(), enc.MainHeader(), (0, [AnnexA67.Ppm(0, ippm)], AnnexA67Encoder.InStream(packets))));
        Assert.Equal(enc.Samples, inTilePart.Planes);
    }

    /// <summary>
    /// T.800 A.7.4, Table A.38: PPM segments are joined in the order of Zppm, and a run of headers may go on in the next segment without a new
    /// Nppm (two segments cut inside a run, Zppm 1 before Zppm 0 in the codestream); Nppm counts per tile-part, the tile-parts get the runs in
    /// their order — the image is the source.
    /// </summary>
    [Fact]
    public void Ppm_SegmentsAndTileParts_AsTableA38()
    {
        var enc = AnnexA67Encoder.Rich();
        var packets = enc.Packets();
        var headers = AnnexA67Encoder.Headers(packets);
        byte[] ippm = [.. AnnexA67.U32(headers.Length), .. headers];
        var cut = 4 + headers.Length / 2;
        Assert.Equal(enc.Samples, AnnexA67.Decoded(AnnexA67.Stream(enc.Siz(), [.. enc.MainHeader(), AnnexA67.Ppm(1, ippm[cut..]), AnnexA67.Ppm(0, ippm[..cut])],
            (0, [], AnnexA67Encoder.Bodies(packets)))));

        var first = packets.Take(16).ToList();
        var second = packets.Skip(16).ToList();
        byte[] runs = [.. AnnexA67.U32(AnnexA67Encoder.Headers(first).Length), .. AnnexA67Encoder.Headers(first),
            .. AnnexA67.U32(AnnexA67Encoder.Headers(second).Length), .. AnnexA67Encoder.Headers(second)];
        Assert.Equal(enc.Samples, AnnexA67.Decoded(AnnexA67.Stream(enc.Siz(), [.. enc.MainHeader(), AnnexA67.Ppm(0, runs)],
            (0, [], AnnexA67Encoder.Bodies(first)), (0, [], AnnexA67Encoder.Bodies(second)))));
    }

    /// <summary>
    /// T.800 A.7.4, Table A.38: Zppm has 8 bits (0 to 255; the segment with 0 comes first although it stands behind the one with 255); an Nppm
    /// larger than the bytes that follow is limited to them, without error.
    /// </summary>
    [Fact]
    public void Ppm_FieldSizes_AsTableA38()
    {
        var enc = AnnexA67Encoder.Rich();
        var packets = enc.Packets();
        var headers = AnnexA67Encoder.Headers(packets);
        var bodies = AnnexA67Encoder.Bodies(packets);
        byte[] ippm = [.. AnnexA67.U32(headers.Length), .. headers];
        var cut = 4 + headers.Length / 3;
        Assert.Equal(enc.Samples, AnnexA67.Decoded(AnnexA67.Stream(enc.Siz(), [.. enc.MainHeader(), AnnexA67.Ppm(255, ippm[cut..]), AnnexA67.Ppm(0, ippm[..cut])],
            (0, [], bodies))));
        Assert.Equal(enc.Samples, AnnexA67.Decoded(AnnexA67.Stream(enc.Siz(), [.. enc.MainHeader(), AnnexA67.Ppm(0, [.. AnnexA67.U32(0xFFFFFFFF), .. headers])],
            (0, [], bodies))));
    }

    // ================================================================ A.7.5 PPT

    /// <summary>T.800 A.7.5: the packet headers of the tile-part in a PPT of its header, the packet data alone behind SOD — the image is the source.</summary>
    [Fact]
    public void Ppt_PacketHeadersInTheTilePartHeader_SameImage()
    {
        var enc = AnnexA67Encoder.Rich();
        var packets = enc.Packets();
        Assert.Equal(enc.Samples, AnnexA67.Decoded(AnnexA67.Stream(enc.Siz(), enc.MainHeader(),
            (0, [AnnexA67.Ppt(0, AnnexA67Encoder.Headers(packets))], AnnexA67Encoder.Bodies(packets)))));
    }

    /// <summary>
    /// T.800 A.7.5, Table A.39: the PPT segments of one header are joined in the order of Zppt — two in one header, Zppt 1 before Zppt 0: the
    /// image is the source.
    /// </summary>
    [Fact]
    public void Ppt_TwoInOneHeader_JoinedByZppt()
    {
        var enc = AnnexA67Encoder.Rich();
        var packets = enc.Packets();
        var headers = AnnexA67Encoder.Headers(packets);
        var cut = headers.Length / 2;
        Assert.Equal(enc.Samples, AnnexA67.Decoded(AnnexA67.Stream(enc.Siz(), enc.MainHeader(),
            (0, [AnnexA67.Ppt(1, headers[cut..]), AnnexA67.Ppt(0, headers[..cut])], AnnexA67Encoder.Bodies(packets)))));
    }

    /// <summary>
    /// T.800 A.7.5, Table A.39: Zppt counts among the PPT of the same header; the headers of a tile are those of its tile-parts one after the
    /// other — two tile-parts with two PPT each (Zppt 0, 1): tile-part 0 (Zppt 0, 1), then tile-part 1 (Zppt 0, 1). Before the decoder ordered
    /// all PPT of the tile by Zppt alone (tile-part 0 Zppt 0, tile-part 1 Zppt 0, …) and the image came out wrong (docs/FEHLERGEDAECHTNIS.md,
    /// No. 1038).
    /// </summary>
    [Fact]
    public void Ppt_TwoTileParts_EachHeaderJoinedOnItsOwn()
    {
        var enc = AnnexA67Encoder.Rich();
        var packets = enc.Packets();
        var first = packets.Take(16).ToList();
        var second = packets.Skip(16).ToList();
        byte[][] Header(List<AnnexA67Encoder.Packet> part)
        {
            var headers = AnnexA67Encoder.Headers(part);
            return [AnnexA67.Ppt(0, headers[..(headers.Length / 2)]), AnnexA67.Ppt(1, headers[(headers.Length / 2)..])];
        }
        Assert.Equal(enc.Samples, AnnexA67.Decoded(AnnexA67.Stream(enc.Siz(), enc.MainHeader(),
            (0, Header(first), AnnexA67Encoder.Bodies(first)), (0, Header(second), AnnexA67Encoder.Bodies(second)))));
    }

    /// <summary>
    /// T.800 A.7.5, Table A.39: Zppt has 8 bits (0 to 255; the segment with 0 first although it stands behind the one with 255); a PPT with Zppt
    /// alone (Lppt 3) adds no headers and no error.
    /// </summary>
    [Fact]
    public void Ppt_FieldSizes_AsTableA39()
    {
        var enc = AnnexA67Encoder.Rich();
        var packets = enc.Packets();
        var headers = AnnexA67Encoder.Headers(packets);
        var cut = headers.Length / 3;
        var empty = AnnexA67.Ppt(1);
        Assert.Equal(3, AnnexA67.Length(empty));
        Assert.Equal(enc.Samples, AnnexA67.Decoded(AnnexA67.Stream(enc.Siz(), enc.MainHeader(),
            (0, [AnnexA67.Ppt(255, headers[cut..]), empty, AnnexA67.Ppt(0, headers[..cut])], AnnexA67Encoder.Bodies(packets)))));
    }

    // ================================================================ helpers

    /// <summary>Marker segments and small codestreams written by hand for the header rules of A.6 and A.7.</summary>
    private static class AnnexA67
    {
        public static byte[] Seg(int marker, params byte[] body) =>
            [(byte)(marker >> 8), (byte)marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];

        /// <summary>The length field of a segment (without the marker).</summary>
        public static int Length(byte[] segment) => segment[2] << 8 | segment[3];

        public static byte[] U16(int v) => [(byte)(v >> 8), (byte)v];

        public static byte[] U32(long v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

        /// <summary>A component index: 8 bits below 257 components, else 16.</summary>
        public static byte[] Index(int components, int c) => components >= 257 ? U16(c) : [(byte)c];

        /// <summary>SIZ: unsigned 8-bit components without subsampling, image and tiles at the origin.</summary>
        public static byte[] Siz(int width, int height, int components, int tileWidth = 0, int tileHeight = 0)
        {
            var body = new List<byte>(U16(0));
            foreach (var v in new long[] { width, height, 0, 0, tileWidth == 0 ? width : tileWidth, tileHeight == 0 ? height : tileHeight, 0, 0 })
                body.AddRange(U32(v));
            body.AddRange(U16(components));
            for (var c = 0; c < components; c++) body.AddRange([7, 1, 1]);
            return Seg(0xFF51, [.. body]);
        }

        /// <summary>COD with its fields as written (the code-block sizes as the field values: exponent − 2).</summary>
        public static byte[] Cod(int scod = 0, int order = 0, int layers = 1, int mct = 0, int levels = 1, int cbw = 4, int cbh = 4, int style = 0,
            int wavelet = 1, byte[]? precincts = null) =>
            Seg(0xFF52, [(byte)scod, (byte)order, .. U16(layers), (byte)mct, (byte)levels, (byte)cbw, (byte)cbh, (byte)style, (byte)wavelet, .. precincts ?? []]);

        public static byte[] Coc(int components, int c, int scoc = 0, int levels = 1, int cbw = 4, int cbh = 4, int style = 0, int wavelet = 1,
            byte[]? precincts = null) =>
            Seg(0xFF53, [.. Index(components, c), (byte)scoc, (byte)levels, (byte)cbw, (byte)cbh, (byte)style, (byte)wavelet, .. precincts ?? []]);

        /// <summary>QCD: Sqcd, then one byte per value without quantisation (style 0), two bytes otherwise.</summary>
        public static byte[] Qcd(int sqcd, params int[] values) => Seg(0xFF5C, [(byte)sqcd, .. Values(sqcd, values)]);

        public static byte[] Qcc(int components, int c, int sqcc, params int[] values) =>
            Seg(0xFF5D, [.. Index(components, c), (byte)sqcc, .. Values(sqcc, values)]);

        private static byte[] Values(int style, int[] values) =>
            (style & 0x1F) == 0 ? [.. values.Select(v => (byte)v)] : [.. values.SelectMany(v => U16(v))];

        /// <summary>QCD without quantisation: <paramref name="count"/> exponents of 10 and <paramref name="guard"/> guard bits.</summary>
        public static byte[] Qcd0(int guard, int count) => Qcd(guard << 5, [.. Enumerable.Repeat(10 << 3, count)]);

        public static byte[] Rgn(int components, int c, int srgn, int shift) => Seg(0xFF5E, [.. Index(components, c), (byte)srgn, (byte)shift]);

        public static byte[] Poc(int components, params (int RS, int CS, int LYE, int RE, int CE, int P)[] entries) =>
            Seg(0xFF5F, [.. entries.SelectMany(e => PocEntry(components, e))]);

        private static byte[] PocEntry(int components, (int RS, int CS, int LYE, int RE, int CE, int P) e) =>
            [(byte)e.RS, .. Index(components, e.CS), .. U16(e.LYE), (byte)e.RE, .. Index(components, e.CE), (byte)e.P];

        public static byte[] Ppm(int z, params byte[] data) => Seg(0xFF60, [(byte)z, .. data]);

        public static byte[] Ppt(int z, params byte[] data) => Seg(0xFF61, [(byte)z, .. data]);

        /// <summary>A codestream: SOC, SIZ, the main header, the tile-parts (tile index, header segments, data behind SOD), EOC.</summary>
        public static byte[] Stream(byte[] siz, IEnumerable<byte[]> main, params (int Tile, byte[][] Header, byte[] Body)[] parts)
        {
            var b = new List<byte> { 0xFF, 0x4F };
            b.AddRange(siz);
            foreach (var s in main) b.AddRange(s);
            var counts = new Dictionary<int, int>();
            foreach (var (tile, header, body) in parts)
            {
                var head = header.SelectMany(h => h).ToArray();
                counts.TryGetValue(tile, out var part);
                counts[tile] = part + 1;
                b.AddRange([0xFF, 0x90, 0x00, 0x0A, .. U16(tile), .. U32(12 + head.Length + 2 + body.Length), (byte)part, 0]);
                b.AddRange(head);
                b.AddRange([0xFF, 0x93]);
                b.AddRange(body);
            }
            b.AddRange([0xFF, 0xD9]);
            return [.. b];
        }

        public static JpxCodestream Parse(byte[] cs) => JpxCodestream.Parse(cs, 0, cs.Length);

        /// <summary>The components of a tile after the decoder set it up (and read its packets, if any).</summary>
        public static JpxTileComponent[] Tile(byte[] data, int tile = 0)
        {
            var cs = Parse(data);
            var t = new JpxTile(cs, tile, cs.Tiles[tile]!, 0, new JpxDecodeState(long.MaxValue, long.MaxValue, long.MaxValue));
            t.Decode();
            return t.Components;
        }

        public static int[] Levels(byte[] data) => [.. Tile(data).Select(tc => tc.Coding.Levels)];

        /// <summary>Where the first segment <paramref name="marker"/> begins.</summary>
        public static int Find(byte[] cs, int marker)
        {
            var p = 2;
            while ((cs[p] << 8 | cs[p + 1]) != marker) p += 2 + (cs[p + 2] << 8 | cs[p + 3]);
            return p;
        }

        /// <summary>The codestream with byte <paramref name="offset"/> of the body of the first segment <paramref name="marker"/> set to <paramref name="value"/>.</summary>
        public static byte[] Patch(byte[] cs, int marker, int offset, int value)
        {
            var copy = (byte[])cs.Clone();
            copy[Find(cs, marker) + 4 + offset] = (byte)value;
            return copy;
        }

        /// <summary>The decoded components (after the level shift), complete.</summary>
        public static int[][] Decoded(byte[] cs)
        {
            var planes = JpxDecoder.DecodePlanes(cs, long.MaxValue);
            Assert.False(planes.Truncated);
            return [.. planes.Planes.Select(p => p.Select(v => (int)v).ToArray())];
        }

        /// <summary>The decoded components (after the level shift) and whether the codestream counts as damaged (read and reported).</summary>
        public static (int[][] Planes, bool Truncated) Read(byte[] cs)
        {
            var planes = JpxDecoder.DecodePlanes(cs, long.MaxValue);
            return ([.. planes.Planes.Select(p => p.Select(v => (int)v).ToArray())], planes.Truncated);
        }

        /// <summary>Whether the codestream decodes completely to <paramref name="expected"/>.</summary>
        public static bool Gives(byte[] cs, int[][] expected)
        {
            try
            {
                var planes = JpxDecoder.DecodePlanes(cs, long.MaxValue);
                return !planes.Truncated && planes.Planes.Length == expected.Length
                    && planes.Planes.Select((p, c) => p.Select(v => (int)v).SequenceEqual(expected[c])).All(same => same);
            }
            catch (JpxException)
            {
                return false;
            }
        }

        public static int[] Noise(int count, int seed)
        {
            var random = new Random(seed);
            return [.. Enumerable.Range(0, count).Select(_ => random.Next(256))];
        }
    }

    /// <summary>
    /// A small JPEG 2000 encoder for these tests, written from the standard: level shift and RCT (G.1, G.2), the forward 5-3 transform (F.4,
    /// columns then rows, symmetric extension), code-blocks in bit planes with the three coding passes, their contexts and the code-block styles
    /// (Annex D) on the MQ coder (C.2; the coder of the JBIG2 encoder, checked against the test sequence of T.88), and packets with tag trees,
    /// pass counts and lengths (B.9, B.10) in the progression of B.12. One tile at the origin, components without subsampling, all with the
    /// same coding.
    /// </summary>
    private sealed class AnnexA67Encoder
    {
        public int Width = 16, Height = 16;

        /// <summary>Per component the samples (0 … 255), or with <see cref="Direct"/> the coefficients of the single band.</summary>
        public int[][] Samples = [];

        /// <summary>The samples are the coefficients themselves (NL 0; for the 9-7 filter the quantisation indices).</summary>
        public bool Direct;

        public int Levels = 1;
        public int Layers = 1;
        public int Order;

        /// <summary>The entries the packets follow (B.12.1 with a progression order change); the test writes the POC itself.</summary>
        public List<(int RS, int CS, int LYE, int RE, int CE, int P)>? Progressions;

        /// <summary>Precinct bytes per resolution (Scod bit 0); none: 15/15.</summary>
        public byte[]? Precincts;

        public int Xcb = 6, Ycb = 6;
        public int Style;
        public bool Reversible = true;
        public int Mct;
        public bool Sop, Eph;
        public int Guard = 2;

        /// <summary>The ROI shift the coefficients are written with (more bit planes than the quantisation gives).</summary>
        public int ExtraPlanes;

        /// <summary>
        /// Which coding passes of a code-block go into which layer: from its missing most significant bit-planes and its number of passes, the
        /// first pass of each layer and the end (<see cref="Layers"/> + 1 values); none: the passes shared out evenly.
        /// </summary>
        public Func<int, int, int[]>? Split;

        /// <summary>
        /// Two components of 16 × 16 samples, one decomposition level, two layers, precincts of 4 × 4 at resolution 0 and 8 × 8 at resolution 1,
        /// code-blocks of 4 × 4 (one per precinct and band): 32 packets, each with data; the precincts of both resolutions share their places on
        /// the reference grid.
        /// </summary>
        public static AnnexA67Encoder Rich(int order = 0) => new()
        {
            Samples = [AnnexA67.Noise(256, 11), AnnexA67.Noise(256, 12)],
            Levels = 1,
            Layers = 2,
            Order = order,
            Precincts = [0x22, 0x33],
            Xcb = 2,
            Ycb = 2,
        };

        public sealed record Packet(int L, int C, int R, int P, byte[] Sop, byte[] Header, byte[] Data);

        private int Components => Samples.Length;

        private int PPx(int r) => Precincts is { } p && r < p.Length ? p[r] & 15 : 15;

        private int PPy(int r) => Precincts is { } p && r < p.Length ? p[r] >> 4 : 15;

        private static int Gain(int band) => band == 0 ? 0 : (band - 1) % 3 == 2 ? 2 : 1;

        /// <summary>The exponent of each sub-band: room enough for 8-bit samples after RCT and transform; 9-7 with step 2.</summary>
        private int Exponent(int band) => Reversible ? 10 + Gain(band) : 7;

        public byte[] Siz() => AnnexA67.Siz(Width, Height, Components);

        public byte[] Cod() => AnnexA67.Cod((Precincts is null ? 0 : 1) | (Sop ? 2 : 0) | (Eph ? 4 : 0), Order, Layers, Mct, Levels, Xcb - 2, Ycb - 2, Style,
            Reversible ? 1 : 0, Precincts);

        public byte[] Qcd() => Reversible
            ? AnnexA67.Qcd(Guard << 5, [.. Enumerable.Range(0, 3 * Levels + 1).Select(b => Exponent(b) << 3)])
            : AnnexA67.Qcd(Guard << 5 | 2, [.. Enumerable.Range(0, 3 * Levels + 1).Select(b => Exponent(b) << 11)]);

        public byte[][] MainHeader() => [Cod(), Qcd()];

        /// <summary>One tile-part with the packets as they come in the codestream (headers in front of their data).</summary>
        public byte[] Build(List<Packet> packets, byte[][]? main = null, byte[][]? tile = null) =>
            AnnexA67.Stream(Siz(), [.. MainHeader(), .. main ?? []], (0, tile ?? [], InStream(packets)));

        public static byte[] InStream(IEnumerable<Packet> packets) => [.. packets.SelectMany(p => p.Sop.Concat(p.Header).Concat(p.Data))];

        public static byte[] Bodies(IEnumerable<Packet> packets) => [.. packets.SelectMany(p => p.Sop.Concat(p.Data))];

        public static byte[] Headers(IEnumerable<Packet> packets) => [.. packets.SelectMany(p => p.Header)];

        // ------------------------------------------------------------ geometry

        private int Size(int full, int r) => (full + (1 << (Levels - r)) - 1) >> (Levels - r);

        private (int X, int Y) PrecinctCount(int r)
        {
            int w = Size(Width, r), h = Size(Height, r);
            return w == 0 || h == 0 ? (0, 0) : ((w + (1 << PPx(r)) - 1) >> PPx(r), (h + (1 << PPy(r)) - 1) >> PPy(r));
        }

        /// <summary>
        /// The packets in the order of B.12.1: per entry the loops of its progression order as written there — layer, resolution, component,
        /// precinct for LRCP; resolution, layer, component, precinct for RLCP; for RPCL, PCRL and CPRL the loops over the positions y, x of the
        /// reference grid, where a precinct is met at a multiple of 2^(PP + NL − r) — with packets already in the codestream left out.
        /// </summary>
        public List<(int L, int C, int R, int P)> Sequence()
        {
            var entries = Progressions ?? [(0, 0, Layers, Levels + 1, Components, Order)];
            var done = new HashSet<(int, int, int, int)>();
            var result = new List<(int L, int C, int R, int P)>();
            void Emit(int l, int c, int r, int p)
            {
                if (done.Contains((l, c, r, p)) || (l > 0 && !done.Contains((l - 1, c, r, p)))) return;
                done.Add((l, c, r, p));
                result.Add((l, c, r, p));
            }
            int? At(int r, int x, int y)
            {
                var (px, _) = PrecinctCount(r);
                if (px == 0) return null;
                int ex = PPx(r) + Levels - r, ey = PPy(r) + Levels - r;
                if (x % (1L << ex) != 0 || y % (1L << ey) != 0) return null;
                return (int)((y >> Math.Min(ey, 30)) * px + (x >> Math.Min(ex, 30)));
            }
            foreach (var (rs, cs, lyeEntry, reEntry, ceEntry, order) in entries)
            {
                int lye = Math.Min(lyeEntry, Layers), re = Math.Min(reEntry, Levels + 1), ce = Math.Min(ceEntry == 0 ? 256 : ceEntry, Components);
                switch (order)
                {
                    case 0:
                        for (var l = 0; l < lye; l++)
                        for (var r = rs; r < re; r++)
                        for (var c = cs; c < ce; c++)
                        for (var p = 0; p < PrecinctCount(r).X * PrecinctCount(r).Y; p++)
                            Emit(l, c, r, p);
                        break;
                    case 1:
                        for (var r = rs; r < re; r++)
                        for (var l = 0; l < lye; l++)
                        for (var c = cs; c < ce; c++)
                        for (var p = 0; p < PrecinctCount(r).X * PrecinctCount(r).Y; p++)
                            Emit(l, c, r, p);
                        break;
                    case 2:
                        for (var r = rs; r < re; r++)
                        for (var y = 0; y < Height; y++)
                        for (var x = 0; x < Width; x++)
                        for (var c = cs; c < ce; c++)
                            if (At(r, x, y) is { } p)
                                for (var l = 0; l < lye; l++) Emit(l, c, r, p);
                        break;
                    case 3:
                        for (var y = 0; y < Height; y++)
                        for (var x = 0; x < Width; x++)
                        for (var c = cs; c < ce; c++)
                        for (var r = rs; r < re; r++)
                            if (At(r, x, y) is { } p)
                                for (var l = 0; l < lye; l++) Emit(l, c, r, p);
                        break;
                    case 4:
                        for (var c = cs; c < ce; c++)
                        for (var y = 0; y < Height; y++)
                        for (var x = 0; x < Width; x++)
                        for (var r = rs; r < re; r++)
                            if (At(r, x, y) is { } p)
                                for (var l = 0; l < lye; l++) Emit(l, c, r, p);
                        break;
                }
            }
            return result;
        }

        // ------------------------------------------------------------ coefficients

        private sealed record Band(int W, int H, int[] D);

        /// <summary>Per component and resolution the sub-bands (LL; HL, LH, HH).</summary>
        private Band[][][] Coefficients()
        {
            if (Direct) return [.. Samples.Select(s => new[] { new[] { new Band(Width, Height, s) } })];
            var shifted = Samples.Select(s => s.Select(v => v - 128).ToArray()).ToArray();
            if (Mct != 0 && Reversible && Components >= 3)
            {
                for (var i = 0; i < Width * Height; i++)
                {
                    int r = shifted[0][i], g = shifted[1][i], b = shifted[2][i];
                    (shifted[0][i], shifted[1][i], shifted[2][i]) = ((r + 2 * g + b) >> 2, b - g, r - g);
                }
            }
            var result = new Band[Components][][];
            for (var c = 0; c < Components; c++)
            {
                result[c] = new Band[Levels + 1][];
                var current = new Band(Width, Height, shifted[c]);
                for (var level = 1; level <= Levels; level++)
                {
                    var (ll, hl, lh, hh) = Decompose(current);
                    result[c][Levels - level + 1] = [hl, lh, hh];
                    current = ll;
                }
                result[c][0] = [current];
            }
            return result;
        }

        /// <summary>One level of the forward 5-3 transform: every column, then every row.</summary>
        private static (Band LL, Band HL, Band LH, Band HH) Decompose(Band a)
        {
            int w = a.W, h = a.H, lw = (w + 1) / 2, lh = (h + 1) / 2;
            var columns = new int[w * h];
            for (var x = 0; x < w; x++)
            {
                var (low, high) = Lift([.. Enumerable.Range(0, h).Select(y => a.D[y * w + x])]);
                for (var j = 0; j < low.Length; j++) columns[j * w + x] = low[j];
                for (var j = 0; j < high.Length; j++) columns[(lh + j) * w + x] = high[j];
            }
            var both = new int[w * h];
            for (var y = 0; y < h; y++)
            {
                var (low, high) = Lift(columns[(y * w)..(y * w + w)]);
                low.CopyTo(both, y * w);
                high.CopyTo(both, y * w + lw);
            }
            Band Part(int x0, int y0, int bw, int bh) =>
                new(bw, bh, [.. Enumerable.Range(0, bw * bh).Select(i => both[(y0 + i / Math.Max(1, bw)) * w + x0 + i % Math.Max(1, bw)])]);
            return (Part(0, 0, lw, lh), Part(lw, 0, w - lw, lh), Part(0, lh, lw, h - lh), Part(lw, lh, w - lw, h - lh));
        }

        /// <summary>The forward 5-3 lifting of a signal starting at an even position, with symmetric extension at both ends.</summary>
        private static (int[] Low, int[] High) Lift(int[] x)
        {
            var n = x.Length;
            if (n == 1) return ([x[0]], []);
            int M(int i) => i < 0 ? -i : i >= n ? 2 * (n - 1) - i : i;
            var y = new int[n];
            for (var i = 1; i < n; i += 2) y[i] = x[i] - ((x[M(i - 1)] + x[M(i + 1)]) >> 1);
            for (var i = 0; i < n; i += 2) y[i] = x[i] + ((y[M(i - 1)] + y[M(i + 1)] + 2) >> 2);
            return ([.. y.Where((_, i) => i % 2 == 0)], [.. y.Where((_, i) => i % 2 == 1)]);
        }

        // ------------------------------------------------------------ code-blocks and precincts

        private sealed class Block
        {
            public List<(byte[] Data, int Passes)> Segments = [];
            public int[] LayerStart = [];
            public bool Included;
            public int Lblock = 3;
        }

        private sealed class PrecinctBand
        {
            public int BlocksX;
            public Block[] Blocks = [];
            public TagTree Inclusion = null!, Zero = null!;
        }

        /// <summary>[component][resolution][precinct][band]; null where a band has no part in the precinct.</summary>
        private PrecinctBand?[][][][] Prepare()
        {
            var coefficients = Coefficients();
            var result = new PrecinctBand?[Components][][][];
            for (var c = 0; c < Components; c++)
            {
                result[c] = new PrecinctBand?[Levels + 1][][];
                for (var r = 0; r <= Levels; r++)
                {
                    var (px, py) = PrecinctCount(r);
                    result[c][r] = new PrecinctBand?[px * py][];
                    for (var p = 0; p < px * py; p++)
                    {
                        var bands = coefficients[c][r];
                        result[c][r][p] = new PrecinctBand?[bands.Length];
                        for (var b = 0; b < bands.Length; b++)
                            result[c][r][p][b] = MakePrecinctBand(bands[b], r, b, p % px, p / px);
                    }
                }
            }
            return result;
        }

        private PrecinctBand? MakePrecinctBand(Band band, int r, int b, int kx, int ky)
        {
            int ex = r == 0 ? PPx(r) : PPx(r) - 1, ey = r == 0 ? PPy(r) : PPy(r) - 1;
            long x0 = (long)kx << ex, y0 = (long)ky << ey;
            long x1 = Math.Min(band.W, (long)(kx + 1) << ex), y1 = Math.Min(band.H, (long)(ky + 1) << ey);
            if (x1 <= x0 || y1 <= y0) return null;
            int bx = Math.Min(Xcb, ex), by = Math.Min(Ycb, ey);
            int first = (int)(x0 >> bx), top = (int)(y0 >> by);
            int blocksX = (int)((x1 + (1L << bx) - 1) >> bx) - first, blocksY = (int)((y1 + (1L << by) - 1) >> by) - top;
            var blocks = new Block[blocksX * blocksY];
            var zero = new int[blocks.Length];
            var orientation = r == 0 ? 0 : b + 1;
            var index = r == 0 ? 0 : 1 + 3 * (r - 1) + b;
            var mb = Guard + Exponent(index) - 1 + ExtraPlanes;
            for (var j = 0; j < blocksY; j++)
            for (var i = 0; i < blocksX; i++)
            {
                int bx0 = (int)Math.Max(x0, (long)(first + i) << bx), bx1 = (int)Math.Min(x1, (long)(first + i + 1) << bx);
                int by0 = (int)Math.Max(y0, (long)(top + j) << by), by1 = (int)Math.Min(y1, (long)(top + j + 1) << by);
                int w = bx1 - bx0, h = by1 - by0;
                var coefficients = new int[w * h];
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    coefficients[y * w + x] = band.D[(by0 + y) * band.W + bx0 + x];
                var block = new Block { Segments = Tier1.Encode(coefficients, w, h, orientation, Style, out var k) };
                Assert.True(k <= mb, "too few bit planes for the coefficients");
                zero[j * blocksX + i] = mb - k;
                var passes = block.Segments.Sum(s => s.Passes);
                block.LayerStart = Split?.Invoke(mb - k, passes) ?? [.. Enumerable.Range(0, Layers + 1).Select(l => passes * l / Layers)];
                blocks[j * blocksX + i] = block;
            }
            int FirstLayer(int i)
            {
                for (var l = 0; l < Layers; l++)
                    if (blocks[i].LayerStart[l + 1] > blocks[i].LayerStart[l]) return l;
                return 1 << 20;
            }
            return new PrecinctBand
            {
                BlocksX = blocksX,
                Blocks = blocks,
                Inclusion = new TagTree(blocksX, blocksY, (x, y) => FirstLayer(y * blocksX + x)),
                Zero = new TagTree(blocksX, blocksY, (x, y) => zero[y * blocksX + x]),
            };
        }

        // ------------------------------------------------------------ packets

        /// <summary>The packets in the order of <see cref="Sequence"/> (headers written in that order: the tag trees carry over between layers).</summary>
        public List<Packet> Packets()
        {
            var precincts = Prepare();
            var packets = new List<Packet>();
            foreach (var (l, c, r, p) in Sequence())
            {
                var bits = new Bits(raw: false);
                var data = new List<byte>();
                var bands = precincts[c][r][p];
                if (!bands.Any(b => b is not null && b.Blocks.Any(k => k.LayerStart[l + 1] > k.LayerStart[l])))
                {
                    bits.Put(0);
                }
                else
                {
                    bits.Put(1);
                    foreach (var band in bands)
                    {
                        if (band is null) continue;
                        for (var k = 0; k < band.Blocks.Length; k++)
                        {
                            var block = band.Blocks[k];
                            int x = k % band.BlocksX, y = k / band.BlocksX;
                            var start = block.LayerStart[l];
                            var count = block.LayerStart[l + 1] - start;
                            if (!block.Included)
                            {
                                band.Inclusion.Encode(bits, x, y, l + 1);
                                if (count == 0) continue;
                                for (var t = 1; !band.Zero.Known(x, y); t++) band.Zero.Encode(bits, x, y, t);
                                block.Included = true;
                            }
                            else
                            {
                                bits.Put(count > 0 ? 1 : 0);
                                if (count == 0) continue;
                            }
                            PassCount(bits, count);
                            var pieces = Pieces(block, start, count);
                            var need = pieces.Max(q => BitLength(q.Bytes.Length) - FloorLog2(q.Passes));
                            for (; block.Lblock < need; block.Lblock++) bits.Put(1);
                            bits.Put(0);
                            foreach (var (passes, bytes) in pieces)
                            {
                                bits.Put(bytes.Length, block.Lblock + FloorLog2(passes));
                                data.AddRange(bytes);
                            }
                        }
                    }
                }
                byte[] header = [.. bits.Finish(), .. Eph ? [0xFF, 0x92] : Array.Empty<byte>()];
                byte[] sop = Sop ? [0xFF, 0x91, 0x00, 0x04, .. AnnexA67.U16(packets.Count & 0xFFFF)] : [];
                packets.Add(new Packet(l, c, r, p, sop, header, [.. data]));
            }
            return packets;
        }

        /// <summary>The number of coding passes (Table B.4).</summary>
        private static void PassCount(Bits bits, int n)
        {
            if (n == 1) bits.Put(0, 1);
            else if (n == 2) bits.Put(0b10, 2);
            else if (n <= 5) bits.Put(0b1100 | (n - 3), 4);
            else if (n <= 36) bits.Put(0b1111_00000 | (n - 6), 9);
            else bits.Put(0b1111_11111_0000000 | (n - 37), 16);
        }

        /// <summary>The data of passes [start, start + count) split at the ends of the codeword segments; inside a segment shared by passes.</summary>
        private static List<(int Passes, byte[] Bytes)> Pieces(Block block, int start, int count)
        {
            var pieces = new List<(int, byte[])>();
            var s0 = 0;
            foreach (var (data, passes) in block.Segments)
            {
                int s1 = s0 + passes, lo = Math.Max(start, s0), hi = Math.Min(start + count, s1);
                if (lo < hi)
                {
                    int Offset(int j) => j == passes ? data.Length : data.Length * j / passes;
                    pieces.Add((hi - lo, data[Offset(lo - s0)..Offset(hi - s0)]));
                }
                s0 = s1;
            }
            return pieces;
        }

        private static int BitLength(int v) => 32 - BitOperations.LeadingZeroCount((uint)v);

        private static int FloorLog2(int v) => 31 - BitOperations.LeadingZeroCount((uint)v);

        /// <summary>Bits MSB first; after a byte 0xFF the next one carries 7 bits (B.10.1, D.6).</summary>
        private sealed class Bits(bool raw)
        {
            private readonly List<byte> _bytes = [];
            private int _current, _count, _limit = 8;

            public void Put(int bit)
            {
                _current = _current << 1 | (bit & 1);
                if (++_count < _limit) return;
                _bytes.Add((byte)_current);
                _limit = _current == 0xFF ? 7 : 8;
                _current = 0;
                _count = 0;
            }

            public void Put(int value, int n)
            {
                for (var k = n - 1; k >= 0; k--) Put(value >> k);
            }

            /// <summary>Pads to a whole byte; a packet header ending in 0xFF gets a byte of 0 behind it, a raw segment drops it.</summary>
            public byte[] Finish()
            {
                if (_count > 0) _bytes.Add((byte)(_current << (_limit - _count)));
                if (_bytes.Count > 0 && _bytes[^1] == 0xFF)
                {
                    if (raw) _bytes.RemoveAt(_bytes.Count - 1);
                    else _bytes.Add(0);
                }
                return [.. _bytes];
            }
        }

        /// <summary>A tag tree (B.10.2) for the encoder: every node the minimum of those below; the bits as the decoder asks for them.</summary>
        private sealed class TagTree
        {
            private readonly List<int[]> _value = [], _current = [];
            private readonly List<bool[]> _known = [];
            private readonly List<int> _width = [];

            public TagTree(int width, int height, Func<int, int, int> leaf)
            {
                int w = width, h = height;
                var values = new int[w * h];
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    values[y * w + x] = leaf(x, y);
                while (true)
                {
                    _value.Add(values);
                    _current.Add(new int[values.Length]);
                    _known.Add(new bool[values.Length]);
                    _width.Add(w);
                    if (w == 1 && h == 1) break;
                    int nw = (w + 1) / 2, nh = (h + 1) / 2;
                    var next = new int[nw * nh];
                    for (var y = 0; y < nh; y++)
                    for (var x = 0; x < nw; x++)
                    {
                        var min = int.MaxValue;
                        for (var j = 2 * y; j < Math.Min(h, 2 * y + 2); j++)
                        for (var i = 2 * x; i < Math.Min(w, 2 * x + 2); i++)
                            min = Math.Min(min, values[j * w + i]);
                        next[y * nw + x] = min;
                    }
                    (values, w, h) = (next, nw, nh);
                }
            }

            public void Encode(Bits bits, int x, int y, int threshold)
            {
                var above = 0;
                for (var level = _value.Count - 1; level >= 0; level--)
                {
                    var n = (y >> level) * _width[level] + (x >> level);
                    var current = _current[level];
                    var known = _known[level];
                    current[n] = Math.Max(current[n], above);
                    while (!known[n] && current[n] < threshold)
                    {
                        if (current[n] == _value[level][n])
                        {
                            bits.Put(1);
                            known[n] = true;
                        }
                        else
                        {
                            bits.Put(0);
                            current[n]++;
                        }
                    }
                    if (!known[n]) return;
                    above = current[n];
                }
            }

            public bool Known(int x, int y) => _known[0][y * _width[0] + x];
        }

        /// <summary>
        /// Code-blocks in bit planes (Annex D): the first plane with the cleanup pass, then each plane significance propagation, magnitude
        /// refinement and cleanup, in stripes of four rows; zero coding (Table D.1), sign coding (Tables D.2, D.3), refinement contexts
        /// (Table D.4), run-length coding; the styles of Table A.19.
        /// </summary>
        private sealed class Tier1
        {
            private const int Bypass = 1, Reset = 2, TerminateAll = 4, Causal = 8, Segmentation = 32;
            private const int RunLength = 17, Uniform = 18;

            private readonly int _w, _h, _s, _orientation, _style;
            private readonly int[] _magnitude;
            private readonly bool[] _negative, _significant, _visited, _refined;
            private byte[] _contexts = Initial();
            private MqEncoder? _mq;
            private Bits? _raw;
            private bool _isRaw;

            private Tier1(int[] coefficients, int w, int h, int orientation, int style)
            {
                (_w, _h, _s, _orientation, _style) = (w, h, w + 2, orientation, style);
                var size = (w + 2) * (h + 2);
                _magnitude = new int[size];
                _negative = new bool[size];
                _significant = new bool[size];
                _visited = new bool[size];
                _refined = new bool[size];
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    _magnitude[I(x, y)] = Math.Abs(coefficients[y * w + x]);
                    _negative[I(x, y)] = coefficients[y * w + x] < 0;
                }
            }

            /// <summary>The 19 contexts at the start (Table D.7): zero coding with no significant neighbour state 4, run-length 3, uniform 46.</summary>
            private static byte[] Initial()
            {
                var contexts = new byte[19];
                contexts[0] = 4 << 1;
                contexts[RunLength] = 3 << 1;
                contexts[Uniform] = 46 << 1;
                return contexts;
            }

            /// <summary>The codeword segments with their passes; <paramref name="planes"/> the number of magnitude bit planes used.</summary>
            public static List<(byte[] Data, int Passes)> Encode(int[] coefficients, int w, int h, int orientation, int style, out int planes)
            {
                var max = coefficients.Length == 0 ? 0 : coefficients.Max(v => Math.Abs(v));
                planes = BitLength(max);
                return planes == 0 ? [] : new Tier1(coefficients, w, h, orientation, style).Run(planes);
            }

            private List<(byte[] Data, int Passes)> Run(int planes)
            {
                var segments = new List<(byte[], int)>();
                var total = 1 + 3 * (planes - 1);
                int pass = 0, inSegment = 0;
                for (var plane = planes - 1; plane >= 0; plane--)
                for (var type = plane == planes - 1 ? 2 : 0; type <= 2; type++)
                {
                    _isRaw = (_style & Bypass) != 0 && pass >= 10 && type != 2;
                    if (_isRaw) _raw ??= new Bits(raw: true);
                    else _mq ??= new MqEncoder();
                    if (type == 0) Significance(plane);
                    else if (type == 1) Refinement(plane);
                    else Cleanup(plane);
                    if ((_style & Reset) != 0) _contexts = Initial();
                    pass++;
                    inSegment++;
                    var end = pass == total || (_style & TerminateAll) != 0 || ((_style & Bypass) != 0 && (pass == 10 || (pass > 10 && type != 0)));
                    if (!end) continue;
                    // the MQ codeword flushed (C.2.9); the encoder adds the JBIG2 end marker 0xFF 0xAC, which is not part of it
                    segments.Add((_mq is not null ? _mq.Finish()[..^2] : _raw!.Finish(), inSegment));
                    _mq = null;
                    _raw = null;
                    inSegment = 0;
                }
                return segments;
            }

            private int I(int x, int y) => (y + 1) * _s + x + 1;

            private bool South(int y) => !((_style & Causal) != 0 && y % 4 == 3);

            private int Sig(int x, int y) => _significant[I(x, y)] ? 1 : 0;

            private (int H, int V, int D) Neighbours(int x, int y)
            {
                var south = South(y);
                return (Sig(x - 1, y) + Sig(x + 1, y), Sig(x, y - 1) + (south ? Sig(x, y + 1) : 0),
                    Sig(x - 1, y - 1) + Sig(x + 1, y - 1) + (south ? Sig(x - 1, y + 1) + Sig(x + 1, y + 1) : 0));
            }

            /// <summary>Table D.1: LL and LH by the horizontal neighbours first, HL by the vertical, HH by the diagonal.</summary>
            private int ZeroContext(int h, int v, int d)
            {
                if (_orientation == 1) (h, v) = (v, h);
                if (_orientation == 3)
                {
                    var hv = h + v;
                    if (d >= 3) return 8;
                    if (d == 2) return hv >= 1 ? 7 : 6;
                    if (d == 1) return hv >= 2 ? 5 : hv == 1 ? 4 : 3;
                    return hv >= 2 ? 2 : hv;
                }
                if (h == 2) return 8;
                if (h == 1) return v >= 1 ? 7 : d >= 1 ? 6 : 5;
                if (v == 2) return 4;
                if (v == 1) return 3;
                return d >= 2 ? 2 : d;
            }

            private void Code(int context, int bit)
            {
                if (_isRaw) _raw!.Put(bit);
                else _mq!.Encode(_contexts, context, bit);
            }

            private int Bit(int x, int y, int plane) => _magnitude[I(x, y)] >> plane & 1;

            private int Contribution(int x, int y) => _significant[I(x, y)] ? _negative[I(x, y)] ? -1 : 1 : 0;

            /// <summary>Tables D.2 and D.3: the contributions of the horizontal and the vertical neighbours give context and XOR bit.</summary>
            private void Sign(int x, int y)
            {
                var negative = _negative[I(x, y)] ? 1 : 0;
                if (_isRaw)
                {
                    _raw!.Put(negative);
                }
                else
                {
                    var h = Math.Clamp(Contribution(x - 1, y) + Contribution(x + 1, y), -1, 1);
                    var v = Math.Clamp(Contribution(x, y - 1) + (South(y) ? Contribution(x, y + 1) : 0), -1, 1);
                    var (context, xor) = (h, v) switch
                    {
                        (1, 1) => (13, 0), (1, 0) => (12, 0), (1, -1) => (11, 0), (0, 1) => (10, 0), (0, 0) => (9, 0),
                        (0, -1) => (10, 1), (-1, 1) => (11, 1), (-1, 0) => (12, 1), _ => (13, 1),
                    };
                    _mq!.Encode(_contexts, context, negative ^ xor);
                }
                _significant[I(x, y)] = true;
            }

            private IEnumerable<(int X, int Y)> Scan()
            {
                for (var y0 = 0; y0 < _h; y0 += 4)
                for (var x = 0; x < _w; x++)
                for (var y = y0; y < Math.Min(y0 + 4, _h); y++)
                    yield return (x, y);
            }

            private void Significance(int plane)
            {
                foreach (var (x, y) in Scan())
                {
                    if (_significant[I(x, y)]) continue;
                    var (h, v, d) = Neighbours(x, y);
                    if (h + v + d == 0) continue;
                    _visited[I(x, y)] = true;
                    var bit = Bit(x, y, plane);
                    Code(ZeroContext(h, v, d), bit);
                    if (bit == 1) Sign(x, y);
                }
            }

            private void Refinement(int plane)
            {
                foreach (var (x, y) in Scan())
                {
                    var i = I(x, y);
                    if (!_significant[i] || _visited[i]) continue;
                    var (h, v, d) = Neighbours(x, y);
                    Code(_refined[i] ? 16 : h + v + d > 0 ? 15 : 14, Bit(x, y, plane));
                    _refined[i] = true;
                }
            }

            private void Cleanup(int plane)
            {
                for (var y0 = 0; y0 < _h; y0 += 4)
                {
                    var rows = Math.Min(4, _h - y0);
                    for (var x = 0; x < _w; x++)
                    {
                        var from = 0;
                        if (rows == 4 && Enumerable.Range(y0, 4).All(y =>
                                !_significant[I(x, y)] && !_visited[I(x, y)] && Neighbours(x, y) is (0, 0, 0)))
                        {
                            var first = Enumerable.Range(0, 4).FirstOrDefault(k => Bit(x, y0 + k, plane) == 1, -1);
                            Code(RunLength, first >= 0 ? 1 : 0);
                            if (first < 0) continue;
                            Code(Uniform, first >> 1);
                            Code(Uniform, first & 1);
                            Sign(x, y0 + first);
                            from = first + 1;
                        }
                        for (var y = y0 + from; y < y0 + rows; y++)
                        {
                            if (_significant[I(x, y)] || _visited[I(x, y)]) continue;
                            var (h, v, d) = Neighbours(x, y);
                            var bit = Bit(x, y, plane);
                            Code(ZeroContext(h, v, d), bit);
                            if (bit == 1) Sign(x, y);
                        }
                    }
                }
                if ((_style & Segmentation) != 0)
                    foreach (var bit in new[] { 1, 0, 1, 0 }) Code(Uniform, bit);
                Array.Clear(_visited);
            }
        }
    }
}
