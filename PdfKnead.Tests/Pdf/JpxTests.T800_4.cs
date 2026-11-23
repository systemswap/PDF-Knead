using PdfKnead.Core.Pdf.Filters.Jbig2;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// ITU-T T.800 Annex B.1–B.9: the bounds of tiles, tile-components, resolutions,
/// sub-bands, precincts and code-blocks (equations B-1 to B-18, Table B.1), layers and packets — each equation and step with its own test on
/// small codestreams built here. The expected values were worked out from the equations beforehand, outside the code under test. Two grids:
/// grid A (Xsiz 13, Ysiz 7, XOsiz 3, YOsiz 2, XTsiz 5, YTsiz 3, XTOsiz 2, YTOsiz 1; component 0 with XRsiz = YRsiz = 1, component 1 with
/// XRsiz 2, YRsiz 3) and grid B (one tile, Xsiz 18, Ysiz 10, XOsiz 5, YOsiz 3, tile offset 0, NL 2).
/// </summary>
public sealed partial class JpxTests
{
    /// <summary>Codestreams, packet headers and code-block data for the tests of Annex B.1–B.9.</summary>
    private static class AnnexB
    {
        /// <summary>Grid A in the order of SIZ: Xsiz, Ysiz, XOsiz, YOsiz, XTsiz, YTsiz, XTOsiz, YTOsiz (3 × 2 tiles).</summary>
        public static readonly uint[] GridA = [13, 7, 3, 2, 5, 3, 2, 1];

        /// <summary>Grid B: one tile of 13 × 7 reference grid points from (5, 3).</summary>
        public static readonly uint[] GridB = [18, 10, 5, 3, 18, 10, 0, 0];

        public static byte[] Seg(int marker, params byte[] body) =>
            [(byte)(marker >> 8), (byte)marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];

        /// <summary>COD: precincts given if <paramref name="precincts"/> is not null; no component transformation; 5-3 reversible.</summary>
        public static byte[] Cod(int levels, int order = 0, int layers = 1, int xcb = 6, int ycb = 6, byte[]? precincts = null) =>
            Seg(0xFF52, [(byte)(precincts is null ? 0 : 1), (byte)order, (byte)(layers >> 8), (byte)layers, 0,
                (byte)levels, (byte)(xcb - 2), (byte)(ycb - 2), 0, 1, .. precincts ?? []]);

        /// <summary>COC for <paramref name="component"/> (fewer than 257 components: one byte for the index).</summary>
        public static byte[] Coc(int component, int levels, int xcb = 6, int ycb = 6, byte[]? precincts = null) =>
            Seg(0xFF53, [(byte)component, (byte)(precincts is null ? 0 : 1), (byte)levels, (byte)(xcb - 2), (byte)(ycb - 2), 0, 1, .. precincts ?? []]);

        /// <summary>QCD: no quantisation, one guard bit, exponent 7 for every sub-band — Mb = 7, so the top bit plane of a block is 6.</summary>
        public static readonly byte[] Qcd = Seg(0xFF5C, 0x20, 7 << 3);

        /// <summary>
        /// SOC, SIZ (8-bit unsigned components with the given subsampling), the main header segments, one tile-part for each of the
        /// <paramref name="tiles"/> tiles (its header segments and body), EOC.
        /// </summary>
        public static byte[] Codestream(uint[] grid, (int Dx, int Dy)[] components, byte[][] main, int tiles, Func<int, byte[]> body,
            Func<int, byte[][]>? tileHeader = null)
        {
            var siz = new List<byte> { 0, 0 };   // Rsiz
            foreach (var v in grid) siz.AddRange(U32(v));
            siz.AddRange([(byte)(components.Length >> 8), (byte)components.Length]);
            foreach (var (dx, dy) in components) siz.AddRange([0x07, (byte)dx, (byte)dy]);
            var b = new List<byte> { 0xFF, 0x4F };
            b.AddRange(Seg(0xFF51, [.. siz]));
            foreach (var s in main) b.AddRange(s);
            for (var t = 0; t < tiles; t++)
            {
                var header = (tileHeader?.Invoke(t) ?? []).SelectMany(s => s).ToArray();
                var data = body(t);
                b.AddRange([0xFF, 0x90, 0x00, 0x0A, (byte)(t >> 8), (byte)t]);
                b.AddRange(U32((uint)(12 + header.Length + 2 + data.Length)));
                b.AddRange([0x00, 0x01]);
                b.AddRange(header);
                b.AddRange([0xFF, 0x93]);
                b.AddRange(data);
            }
            b.AddRange([0xFF, 0xD9]);
            return [.. b];
        }

        private static byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

        /// <summary>Bodies of empty packets only (each a single 0 byte), more than any of the small tiles here needs.</summary>
        public static byte[] Empty(int _) => new byte[64];

        /// <summary>Grid A with both components (or only component 0), NL <paramref name="levels"/>, empty packets.</summary>
        public static byte[] GridACodestream(int levels = 0, bool bothComponents = true) =>
            Codestream(GridA, bothComponents ? [(1, 1), (2, 3)] : [(1, 1)], [Cod(levels), Qcd], 6, Empty);

        /// <summary>Grid B with NL 2 and the given COD values, empty packets.</summary>
        public static byte[] GridBCodestream(int xcb = 6, int ycb = 6, byte[]? precincts = null, uint[]? grid = null) =>
            Codestream(grid ?? GridB, [(1, 1)], [Cod(2, xcb: xcb, ycb: ycb, precincts: precincts), Qcd], 1, Empty);

        /// <summary>Parses the codestream and decodes tile <paramref name="t"/> at full size; the tile keeps its structure for the checks.</summary>
        public static JpxTile Tile(byte[] data, int t = 0)
        {
            var cs = JpxCodestream.Parse(data, 0, data.Length);
            var tile = new JpxTile(cs, t, cs.Tiles[t]!, 0, new JpxDecodeState(long.MaxValue, long.MaxValue, long.MaxValue));
            tile.Decode();
            return tile;
        }

        /// <summary>Bits of a packet header, packed with the bit stuffing of B.10.1 (after 0xFF a 0 bit in front); spaces are ignored, the
        /// last byte is filled with 0 bits.</summary>
        public static byte[] Bits(string bits)
        {
            var bytes = new List<byte>();
            int current = 0, count = 0, limit = 8;
            foreach (var c in bits)
            {
                if (c == ' ') continue;
                current = current << 1 | (c - '0');
                if (++count < limit) continue;
                bytes.Add((byte)current);
                limit = current == 0xFF ? 7 : 8;
                current = 0;
                count = 0;
            }
            if (count > 0) bytes.Add((byte)(current << (limit - count)));
            return [.. bytes];
        }

        /// <summary>
        /// A packet of a precinct with one code-block (tag trees of one node), first included in layer 0: not empty, included, no missing
        /// bit planes, one coding pass, Lblock raised as far as the length needs, the length (<paramref name="claimed"/> instead of the true
        /// one for a damaged packet), then the data.
        /// </summary>
        public static byte[] OneBlockPacket(byte[] data, int? claimed = null)
        {
            var length = claimed ?? data.Length;
            var extra = 0;
            while (length >= 1 << (3 + extra)) extra++;
            return [.. Bits("1 1 1 0 " + new string('1', extra) + "0 " + Convert.ToString(length, 2).PadLeft(3 + extra, '0')), .. data];
        }

        /// <summary>MQ-coded decisions (context, bit), the contexts as after a reset (Table D.7).</summary>
        public static byte[] Mq(params (int Context, int Bit)[] decisions)
        {
            var contexts = JpxBlockDecoder.InitialContexts();
            var mq = new MqEncoder();
            foreach (var (context, bit) in decisions) mq.Encode(contexts, context, bit);
            return mq.Finish();
        }

        /// <summary>
        /// The cleanup pass of a 1 × 1 code-block: the coefficient becomes significant (zero-coding context 0, no neighbours) with the sign
        /// <paramref name="negative"/> (sign context 9, no XOR). With the top bit plane 6 its value is ±96 (3 · 2^6 at half a bit more,
        /// halved), so the 8-bit sample after the level shift of 128 is 224 or 32.
        /// </summary>
        public static byte[] SingleCoefficient(bool negative) => Mq((0, 1), (9, negative ? 1 : 0));

        /// <summary>The packet of layer 0 for a 4 × 4 tile with NL 0 (one code-block): included, 1 coding pass, 2 bytes.</summary>
        public static byte[] Layer0 => [.. Bits("1 1 1 0 0 010"), 0xA0, 0xA1];

        /// <summary>The packet of layer 1 for the same block: included already (one bit), 2 coding passes, length in 3 + 1 bits: 3 bytes.</summary>
        public static byte[] Layer1 => [.. Bits("1 1 10 0 0011"), 0xB0, 0xB1, 0xB2];

        /// <summary>Ceiling division as the equations of Annex B use it, for the cross-check of Table B.1.</summary>
        public static int Ceil(int a, int b) => (int)Math.Ceiling(a / (double)b);
    }

    // ------------------------------------------------------------ B.1

    /// <summary>
    /// T.800 B.1 (S7): tile-components are coded independently — two tiles of one sample each; damaged packet data in one tile (other data
    /// and a length beyond its end) leave the sample of the other tile as in the intact codestream, whichever of the two is damaged.
    /// </summary>
    [Fact]
    public void TileComponents_DamageInOneTile_LeavesTheOtherAlone()
    {
        uint[] grid = [2, 1, 0, 0, 1, 1, 0, 0];
        byte[] Image(Func<int, byte[]> body) => AnnexB.Codestream(grid, [(1, 1)], [AnnexB.Cod(0), AnnexB.Qcd], 2, body);
        var positive = AnnexB.SingleCoefficient(false);
        var negative = AnnexB.SingleCoefficient(true);
        var intact = JpxDecoder.DecodePlanes(Image(t => AnnexB.OneBlockPacket(t == 0 ? positive : negative)), long.MaxValue, 0);
        Assert.Equal(new ushort[] { 224, 32 }, intact.Planes[0]);
        Assert.False(intact.Truncated);

        var secondDamaged = JpxDecoder.DecodePlanes(Image(t => t == 0
            ? AnnexB.OneBlockPacket(positive)
            : AnnexB.OneBlockPacket(positive, positive.Length + 1)), long.MaxValue, 0);
        Assert.True(secondDamaged.Truncated);
        Assert.Equal(224, secondDamaged.Planes[0][0]);
        Assert.NotEqual(32, secondDamaged.Planes[0][1]);

        var firstDamaged = JpxDecoder.DecodePlanes(Image(t => t == 0
            ? AnnexB.OneBlockPacket(negative, negative.Length + 1)
            : AnnexB.OneBlockPacket(negative)), long.MaxValue, 0);
        Assert.True(firstDamaged.Truncated);
        Assert.NotEqual(224, firstDamaged.Planes[0][0]);
        Assert.Equal(32, firstDamaged.Planes[0][1]);
    }

    /// <summary>
    /// T.800 B.1 (S15, S19): the values behind the formulas may differ per tile, component and resolution — main header COD (NL 1, PPx/PPy
    /// 2 and 6) and COC for component 1 (NL 3); tile 0 with its own COD (NL 2, PPx/PPy 3, 4, 5), tile 1 with its own COC for component 1
    /// (NL 0). Tile 0 takes its COD for both components (before the COC of the main header), tile 1 the main COD for component 0 and its own
    /// COC for component 1; the precinct sizes follow per resolution.
    /// </summary>
    [Fact]
    public void CodingValues_PerTileComponentAndResolution_FromTheRightHeader()
    {
        var data = AnnexB.Codestream([16, 8, 0, 0, 8, 8, 0, 0], [(1, 1), (1, 1)],
            [AnnexB.Cod(1, precincts: [0x22, 0x66]), AnnexB.Coc(1, 3), AnnexB.Qcd], 2, AnnexB.Empty,
            t => t == 0 ? [AnnexB.Cod(2, precincts: [0x33, 0x44, 0x55])] : [AnnexB.Coc(1, 0)]);
        var tile0 = AnnexB.Tile(data, 0);
        foreach (var tc in tile0.Components)
        {
            Assert.Equal(2, tc.Coding.Levels);
            Assert.Equal(new[] { 3, 4, 5 }, tc.Resolutions.Select(r => r.PrecinctWidth));
            Assert.Equal(new[] { 3, 4, 5 }, tc.Resolutions.Select(r => r.PrecinctHeight));
        }
        var tile1 = AnnexB.Tile(data, 1);
        Assert.Equal(1, tile1.Components[0].Coding.Levels);
        Assert.Equal(new[] { 2, 6 }, tile1.Components[0].Resolutions.Select(r => r.PrecinctWidth));
        Assert.Equal(0, tile1.Components[1].Coding.Levels);
        Assert.Equal(15, tile1.Components[1].Resolutions[0].PrecinctWidth);
    }

    /// <summary>
    /// T.800 B.1 (S18): bounds include the left and upper edge and exclude the right and lower one — grid A, component 0: tile 0 ends at
    /// x = 7 where tile 1 begins, tile 1 ends at 12 where tile 2 begins, tile 0 ends at y = 4 where tile 3 begins; the widths 4 + 5 + 1 and
    /// the heights 2 + 3 add up to the component (10 × 5), no grid point counted twice.
    /// </summary>
    [Fact]
    public void TileBounds_HalfOpen_AdjacentTilesShareNoPoint()
    {
        var data = AnnexB.GridACodestream();
        var tiles = Enumerable.Range(0, 6).Select(t => AnnexB.Tile(data, t).Components[0]).ToArray();
        Assert.Equal((3, 7), (tiles[0].X0, tiles[0].X1));
        Assert.Equal((7, 12), (tiles[1].X0, tiles[1].X1));
        Assert.Equal((12, 13), (tiles[2].X0, tiles[2].X1));
        Assert.Equal((2, 4, 4, 7), (tiles[0].Y0, tiles[0].Y1, tiles[3].Y0, tiles[3].Y1));
        var planes = JpxDecoder.DecodePlanes(data, long.MaxValue, 0);
        Assert.Equal(planes.PlaneWidth[0], tiles[..3].Sum(t => t.Resolutions[0].Width));
        Assert.Equal(planes.PlaneHeight[0], tiles[0].Resolutions[0].Height + tiles[3].Resolutions[0].Height);
        Assert.Equal((10, 5), (planes.PlaneWidth[0], planes.PlaneHeight[0]));
    }

    // ------------------------------------------------------------ B.2

    /// <summary>
    /// T.800 B.2 (S3–S5): the image area reaches from (XOsiz, YOsiz) to (Xsiz − 1, Ysiz − 1) — grid A with one component decodes to
    /// 10 × 5 samples, and the JP2 wrapper writes height 5 and width 10 into the image header box.
    /// </summary>
    [Fact]
    public void ImageArea_WithOffset_SizeIsXsizMinusXOsiz()
    {
        var data = AnnexB.GridACodestream(bothComponents: false);
        var image = JpxDecoder.Decode(data, long.MaxValue);
        Assert.Equal((10, 5), (image.Width, image.Height));
        var wrapped = PdfAJpeg2000.Wrap(data)!;
        var ihdr = wrapped.AsSpan().IndexOf("ihdr"u8) + 4;
        Assert.Equal(5u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(wrapped.AsSpan(ihdr)));
        Assert.Equal(10u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(wrapped.AsSpan(ihdr + 4)));
    }

    /// <summary>
    /// T.800 B.2, equation B-1: the samples of a component start at ⌈XOsiz / XRsiz⌉, ⌈YOsiz / YRsiz⌉ and end before ⌈Xsiz / XRsiz⌉,
    /// ⌈Ysiz / YRsiz⌉ — grid A: component 0 from (3, 2) to (13, 7), component 1 (XRsiz 2, YRsiz 3) from (2, 1) to (7, 3).
    /// </summary>
    [Theory]
    [InlineData(0, 3, 13, 2, 7)]
    [InlineData(1, 2, 7, 1, 3)]
    public void ComponentBounds_AsB1(int component, int x0, int x1, int y0, int y1)
    {
        var planes = JpxDecoder.DecodePlanes(AnnexB.GridACodestream(), long.MaxValue, 0);
        Assert.Equal((x0, y0), (planes.PlaneX0[component], planes.PlaneY0[component]));
        Assert.Equal((x1, y1), (planes.PlaneX0[component] + planes.PlaneWidth[component], planes.PlaneY0[component] + planes.PlaneHeight[component]));
    }

    /// <summary>T.800 B.2, equation B-2: the component is x1 − x0 wide and y1 − y0 high — grid A: component 0 10 × 5, component 1 5 × 2.</summary>
    [Theory]
    [InlineData(0, 10, 5)]
    [InlineData(1, 5, 2)]
    public void ComponentSize_AsB2(int component, int width, int height)
    {
        var planes = JpxDecoder.DecodePlanes(AnnexB.GridACodestream(), long.MaxValue, 0);
        Assert.Equal((width, height), (planes.PlaneWidth[component], planes.PlaneHeight[component]));
        Assert.Equal(width * height, planes.Planes[component].Length);
    }

    // ------------------------------------------------------------ B.3

    /// <summary>
    /// T.800 B.3 (S1, S2, S4, S5, S8): equal tiles of XTsiz × YTsiz grid points, tile 0 at (XTOsiz, YTOsiz) — in grid A the tiles start at
    /// x = 2, 7, 12 and y = 1, 4; inside the image tiles 1 and 2 begin at 7 and 12, the tiles of the second row at 4, and a tile the image
    /// does not cut is 5 wide and 3 high.
    /// </summary>
    [Fact]
    public void TileGrid_StartsAtTileOffset_TilesOfEqualSize()
    {
        var data = AnnexB.GridACodestream();
        var cs = JpxCodestream.Parse(data, 0, data.Length);
        Assert.Equal((5, 3, 2, 1), (cs.TileWidth, cs.TileHeight, cs.TileX0, cs.TileY0));
        var tile1 = AnnexB.Tile(data, 1).Components[0];
        Assert.Equal(7, tile1.X0);
        Assert.Equal(12, AnnexB.Tile(data, 2).Components[0].X0);
        var tile4 = AnnexB.Tile(data, 4).Components[0];
        Assert.Equal(4, tile4.Y0);
        Assert.Equal((5, 3), (tile4.X1 - tile4.X0, tile4.Y1 - tile4.Y0));
    }

    /// <summary>
    /// T.800 B.3, equation B-6: tile t lies at p = t mod numXtiles, q = ⌊t / numXtiles⌋ (raster order) — grid A with 3 × 2 tiles, every tile
    /// with the bounds of its (p, q) on component 0.
    /// </summary>
    [Theory]
    [InlineData(0, 3, 7, 2, 4)]
    [InlineData(1, 7, 12, 2, 4)]
    [InlineData(2, 12, 13, 2, 4)]
    [InlineData(3, 3, 7, 4, 7)]
    [InlineData(4, 7, 12, 4, 7)]
    [InlineData(5, 12, 13, 4, 7)]
    public void TileIndex_ToPositionAsB6(int t, int x0, int x1, int y0, int y1)
    {
        var tc = AnnexB.Tile(AnnexB.GridACodestream(), t).Components[0];
        Assert.Equal((x0, x1, y0, y1), (tc.X0, tc.X1, tc.Y0, tc.Y1));
    }

    /// <summary>
    /// T.800 B.3, equation B-3: 0 ≤ XTOsiz ≤ XOsiz and 0 ≤ YTOsiz ≤ YOsiz — grid A with the tile offset at or below the image offset is read,
    /// XTOsiz 4 (above XOsiz 3) or YTOsiz 3 (above YOsiz 2) is refused.
    /// </summary>
    [Theory]
    [InlineData(2, 1, false)]
    [InlineData(3, 2, false)]
    [InlineData(4, 1, true)]
    [InlineData(2, 3, true)]
    public void TileOffset_NotBeyondImageOffset_AsB3(uint xtosiz, uint ytosiz, bool refused)
    {
        uint[] grid = [13, 7, 3, 2, 5, 3, xtosiz, ytosiz];
        var data = AnnexB.Codestream(grid, [(1, 1)], [AnnexB.Cod(0), AnnexB.Qcd], 1, AnnexB.Empty);
        if (refused) Assert.Throws<JpxException>(() => JpxCodestream.Parse(data, 0, data.Length));
        else Assert.Equal(13, JpxCodestream.Parse(data, 0, data.Length).X1);
    }

    /// <summary>
    /// T.800 B.3, equation B-4 (vertical): YTsiz + YTOsiz must exceed YOsiz — with YOsiz 10 and YTOsiz 0 a tile height of 10 (the first tile
    /// row ends at the image origin) is refused, 11 is read.
    /// </summary>
    [Theory]
    [InlineData(10u, true)]
    [InlineData(11u, false)]
    public void FirstTile_VerticallyOutsideTheImage_RefusedAsB4(uint ytsiz, bool refused)
    {
        uint[] grid = [8, 20, 0, 10, 8, ytsiz, 0, 0];
        var data = AnnexB.Codestream(grid, [(1, 1)], [AnnexB.Cod(0), AnnexB.Qcd], 1, AnnexB.Empty);
        if (refused) Assert.Throws<JpxException>(() => JpxCodestream.Parse(data, 0, data.Length));
        else Assert.Equal(2, JpxCodestream.Parse(data, 0, data.Length).TilesY);
    }

    /// <summary>
    /// T.800 B.3, equation B-5: numXtiles = ⌈(Xsiz − XTOsiz) / XTsiz⌉, numYtiles = ⌈(Ysiz − YTOsiz) / YTsiz⌉ — grid A: 3 × 2 tiles; with
    /// Xsiz 12 the width divides evenly (2), with Ysiz 8 a third row begins.
    /// </summary>
    [Theory]
    [InlineData(13u, 7u, 3, 2)]
    [InlineData(12u, 7u, 2, 2)]
    [InlineData(13u, 8u, 3, 3)]
    public void NumberOfTiles_AsB5(uint xsiz, uint ysiz, int tilesX, int tilesY)
    {
        uint[] grid = [xsiz, ysiz, 3, 2, 5, 3, 2, 1];
        var data = AnnexB.Codestream(grid, [(1, 1)], [AnnexB.Cod(0), AnnexB.Qcd], 1, AnnexB.Empty);
        var cs = JpxCodestream.Parse(data, 0, data.Length);
        Assert.Equal((tilesX, tilesY), (cs.TilesX, cs.TilesY));
        Assert.Equal(tilesX * tilesY, cs.Tiles.Length);
    }

    /// <summary>T.800 B.3, equation B-7: tx0 = max(XTOsiz + p · XTsiz, XOsiz) — grid A, component 0 (XRsiz 1): p = 0 starts at the image
    /// offset 3 (not at 2), p = 1 at 7, p = 2 at 12.</summary>
    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 7)]
    [InlineData(2, 12)]
    public void TileLeft_AsB7(int p, int x0) => Assert.Equal(x0, AnnexB.Tile(AnnexB.GridACodestream(), p).Components[0].X0);

    /// <summary>T.800 B.3, equation B-8: ty0 = max(YTOsiz + q · YTsiz, YOsiz) — grid A, component 0: q = 0 starts at the image offset 2 (not
    /// at 1), q = 1 at 4.</summary>
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    public void TileTop_AsB8(int q, int y0) => Assert.Equal(y0, AnnexB.Tile(AnnexB.GridACodestream(), 3 * q).Components[0].Y0);

    /// <summary>T.800 B.3, equation B-9: tx1 = min(XTOsiz + (p + 1) · XTsiz, Xsiz) — grid A, component 0: p = 0 ends at 7, p = 1 at 12, p = 2
    /// at the image end 13 (not at 17).</summary>
    [Theory]
    [InlineData(0, 7)]
    [InlineData(1, 12)]
    [InlineData(2, 13)]
    public void TileRight_AsB9(int p, int x1) => Assert.Equal(x1, AnnexB.Tile(AnnexB.GridACodestream(), p).Components[0].X1);

    /// <summary>T.800 B.3, equation B-10: ty1 = min(YTOsiz + (q + 1) · YTsiz, Ysiz) — grid A, component 0: q = 0 ends at 4, q = 1 at 7; with
    /// Ysiz 6 the second row ends at the image end 6 (not at 7).</summary>
    [Theory]
    [InlineData(7u, 0, 4)]
    [InlineData(7u, 1, 7)]
    [InlineData(6u, 1, 6)]
    public void TileBottom_AsB10(uint ysiz, int q, int y1)
    {
        uint[] grid = [13, ysiz, 3, 2, 5, 3, 2, 1];
        var data = AnnexB.Codestream(grid, [(1, 1)], [AnnexB.Cod(0), AnnexB.Qcd], 6, AnnexB.Empty);
        Assert.Equal(y1, AnnexB.Tile(data, 3 * q).Components[0].Y1);
    }

    /// <summary>
    /// T.800 B.3 (S44), equation B-11: the tile is tx1 − tx0 wide and ty1 − ty0 high, (tx0, ty0) its upper left corner — grid A, component 0
    /// (XRsiz 1): tile 0 4 × 2, tile 4 5 × 3, tile 5 1 × 3; the buffer of the tile-component holds as many samples.
    /// </summary>
    [Theory]
    [InlineData(0, 3, 2, 4, 2)]
    [InlineData(4, 7, 4, 5, 3)]
    [InlineData(5, 12, 4, 1, 3)]
    public void TileSize_AsB11(int t, int x0, int y0, int width, int height)
    {
        var tc = AnnexB.Tile(AnnexB.GridACodestream(), t).Components[0];
        Assert.Equal((x0, y0), (tc.X0, tc.Y0));
        Assert.Equal((width, height), (tc.X1 - tc.X0, tc.Y1 - tc.Y0));
        Assert.Equal(width * height, tc.Data.Length);
    }

    /// <summary>
    /// T.800 B.3, equation B-12: tcx0 = ⌈tx0 / XRsiz⌉, tcx1 = ⌈tx1 / XRsiz⌉, tcy0 = ⌈ty0 / YRsiz⌉, tcy1 = ⌈ty1 / YRsiz⌉ — grid A, component 1
    /// (XRsiz 2, YRsiz 3), all six tiles.
    /// </summary>
    [Theory]
    [InlineData(0, 2, 4, 1, 2)]
    [InlineData(1, 4, 6, 1, 2)]
    [InlineData(2, 6, 7, 1, 2)]
    [InlineData(3, 2, 4, 2, 3)]
    [InlineData(4, 4, 6, 2, 3)]
    [InlineData(5, 6, 7, 2, 3)]
    public void TileComponentBounds_AsB12(int t, int x0, int x1, int y0, int y1)
    {
        var tc = AnnexB.Tile(AnnexB.GridACodestream(), t).Components[1];
        Assert.Equal((x0, x1, y0, y1), (tc.X0, tc.X1, tc.Y0, tc.Y1));
    }

    /// <summary>
    /// T.800 B.3, equation B-13: the tile-component is tcx1 − tcx0 wide and tcy1 − tcy0 high — grid A, component 1, NL 1: the highest
    /// resolution has that size, and so has the buffer (tile 0 and 4: 2 × 1, tile 2 and 5: 1 × 1).
    /// </summary>
    [Theory]
    [InlineData(0, 2, 1)]
    [InlineData(2, 1, 1)]
    [InlineData(4, 2, 1)]
    [InlineData(5, 1, 1)]
    public void TileComponentSize_AsB13(int t, int width, int height)
    {
        var tc = AnnexB.Tile(AnnexB.GridACodestream(levels: 1), t).Components[1];
        Assert.Equal((width, height), (tc.Resolutions[1].Width, tc.Resolutions[1].Height));
        Assert.Equal(width * height, tc.Data.Length);
    }

    // ------------------------------------------------------------ B.5

    /// <summary>
    /// T.800 B.5 (S1–S3): NL decomposition levels give NL + 1 resolutions r = 0 … NL; the lowest holds only the NL-LL band, every other one
    /// HL, LH and HH — grid B with NL 0, 1, 2 and 5.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void ResolutionLevels_NLPlusOne_LowestOnlyLL(int levels)
    {
        var data = AnnexB.Codestream(AnnexB.GridB, [(1, 1)], [AnnexB.Cod(levels), AnnexB.Qcd], 1, AnnexB.Empty);
        var tc = AnnexB.Tile(data).Components[0];
        Assert.Equal(levels + 1, tc.Resolutions.Length);
        Assert.Equal([0], tc.Resolutions[0].Bands.Select(b => b.Orientation));
        for (var r = 1; r <= levels; r++) Assert.Equal([1, 2, 3], tc.Resolutions[r].Bands.Select(b => b.Orientation));
    }

    /// <summary>
    /// T.800 B.5 (S4, S6–S11), equation B-14: trx0 = ⌈tcx0 / 2^(NL − r)⌉ and so on — grid B (tile-component 5 … 18 × 3 … 10, NL 2): r = 0
    /// 2 … 5 × 1 … 3, r = 1 3 … 9 × 2 … 5, r = 2 the tile-component itself; decoded with NL − r levels left out, the component has the size
    /// and origin of resolution r.
    /// </summary>
    [Theory]
    [InlineData(0, 2, 5, 1, 3)]
    [InlineData(1, 3, 9, 2, 5)]
    [InlineData(2, 5, 18, 3, 10)]
    public void ResolutionBounds_AsB14(int r, int x0, int x1, int y0, int y1)
    {
        var data = AnnexB.GridBCodestream();
        var res = AnnexB.Tile(data).Components[0].Resolutions[r];
        Assert.Equal((x0, x1, y0, y1), (res.X0, res.X1, res.Y0, res.Y1));
        var planes = JpxDecoder.DecodePlanes(data, long.MaxValue, 2 - r);
        Assert.Equal((x0, y0), (planes.PlaneX0[0], planes.PlaneY0[0]));
        Assert.Equal((x1 - x0, y1 - y0), (planes.PlaneWidth[0], planes.PlaneHeight[0]));
    }

    /// <summary>
    /// T.800 B.5 (S13–S26), equation B-15 horizontal: tbx0 = ⌈(tcx0 − 2^(nb − 1) · xob) / 2^nb⌉, tbx1 likewise — grid B: the three high-pass
    /// bands of nb 1 (r = 2) and nb 2 (r = 1); with XOsiz 0 the numerator of 1HL is negative (⌈−1/2⌉ = 0).
    /// </summary>
    [Theory]
    [InlineData(5u, 2, 1, 2, 9)]
    [InlineData(5u, 2, 2, 3, 9)]
    [InlineData(5u, 2, 3, 2, 9)]
    [InlineData(5u, 1, 1, 1, 4)]
    [InlineData(5u, 1, 2, 2, 5)]
    [InlineData(5u, 1, 3, 1, 4)]
    [InlineData(0u, 2, 1, 0, 9)]
    public void SubBandBoundsHorizontal_AsB15(uint xosiz, int r, int orientation, int x0, int x1)
    {
        uint[] grid = [18, 10, xosiz, 3, 18, 10, 0, 0];
        var band = AnnexB.Tile(AnnexB.GridBCodestream(grid: grid)).Components[0].Resolutions[r].Bands[orientation - 1];
        Assert.Equal(orientation, band.Orientation);
        Assert.Equal((x0, x1), (band.X0, band.X1));
    }

    /// <summary>
    /// T.800 B.5 (S27–S42), equation B-15 vertical: tby0 = ⌈(tcy0 − 2^(nb − 1) · yob) / 2^nb⌉, tby1 likewise, nb = NL − r + 1 — grid B: the
    /// high-pass bands of nb 1 and 2; with YOsiz 0 the numerator of 1LH is negative (⌈−1/2⌉ = 0).
    /// </summary>
    [Theory]
    [InlineData(3u, 2, 1, 2, 5)]
    [InlineData(3u, 2, 2, 1, 5)]
    [InlineData(3u, 2, 3, 1, 5)]
    [InlineData(3u, 1, 1, 1, 3)]
    [InlineData(3u, 1, 2, 1, 2)]
    [InlineData(3u, 1, 3, 1, 2)]
    [InlineData(0u, 2, 2, 0, 5)]
    public void SubBandBoundsVertical_AsB15(uint yosiz, int r, int orientation, int y0, int y1)
    {
        uint[] grid = [18, 10, 5, yosiz, 18, 10, 0, 0];
        var band = AnnexB.Tile(AnnexB.GridBCodestream(grid: grid)).Components[0].Resolutions[r].Bands[orientation - 1];
        Assert.Equal(orientation, band.Orientation);
        Assert.Equal((y0, y1), (band.Y0, band.Y1));
    }

    /// <summary>
    /// T.800 B.5, Table B.1 (one row each): xob/yob of LL 0/0, HL 1/0, LH 0/1, HH 1/1 — grid B, LL at nb 2 (r = 0), the others at nb 1
    /// (r = 2): the bounds are the worked-out numbers and agree with equation B-15 for the xob and yob of the row.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, 2, 2, 5, 1, 3)]
    [InlineData(1, 1, 0, 1, 2, 9, 2, 5)]
    [InlineData(2, 0, 1, 1, 3, 9, 1, 5)]
    [InlineData(3, 1, 1, 1, 2, 9, 1, 5)]
    public void SubBandOffsets_AsTableB1(int orientation, int xob, int yob, int nb, int x0, int x1, int y0, int y1)
    {
        var tc = AnnexB.Tile(AnnexB.GridBCodestream()).Components[0];
        var band = orientation == 0 ? tc.Resolutions[0].Bands[0] : tc.Resolutions[2].Bands[orientation - 1];
        Assert.Equal(orientation, band.Orientation);
        Assert.Equal((x0, x1, y0, y1), (band.X0, band.X1, band.Y0, band.Y1));
        int half = 1 << (nb - 1), full = 1 << nb;
        Assert.Equal((AnnexB.Ceil((int)tc.X0 - half * xob, full), AnnexB.Ceil((int)tc.X1 - half * xob, full)), (band.X0, band.X1));
        Assert.Equal((AnnexB.Ceil((int)tc.Y0 - half * yob, full), AnnexB.Ceil((int)tc.Y1 - half * yob, full)), (band.Y0, band.Y1));
    }

    /// <summary>
    /// T.800 B.5 (S63, S64): each sub-band has its own domain, tbx1 − tbx0 wide and tby1 − tby0 high — grid B, r = 2: HL 7 × 3, LH 6 × 4,
    /// HH 7 × 4; the next lower resolution (6 × 3) and the high-pass bands fill resolution 2 (13 × 7), and in the buffer HL lies right of
    /// it (from x 6), LH below it (from y 3), HH at both.
    /// </summary>
    [Fact]
    public void SubBandSize_OwnDomain_FillsTheResolution()
    {
        var tc = AnnexB.Tile(AnnexB.GridBCodestream()).Components[0];
        var (lower, res) = (tc.Resolutions[1], tc.Resolutions[2]);
        var sizes = res.Bands.Select(b => (b.X1 - b.X0, b.Y1 - b.Y0)).ToArray();
        Assert.Equal([(7, 3), (6, 4), (7, 4)], sizes);
        Assert.Equal((6, 3, 13, 7), (lower.Width, lower.Height, res.Width, res.Height));
        Assert.Equal(res.Width, lower.Width + sizes[0].Item1);
        Assert.Equal(res.Height, lower.Height + sizes[1].Item2);
        Assert.Equal([(6, 0), (0, 3), (6, 3)], res.Bands.Select(b => (b.OffsetX, b.OffsetY)));
    }

    // ------------------------------------------------------------ B.6

    /// <summary>
    /// T.800 B.6 (S2): the precinct partition is anchored at (0, 0), PPx in the lower and PPy in the upper four bits of the byte of COD —
    /// grid B, r = 2 with PPx 2 and PPy 3 (byte 0x32): the first precinct column is 1 (from x 4, before trx0 = 5, so the first precinct is
    /// cut), the first row 0; in sub-band HL (precinct 2 × 4) precinct 0 covers x 2 … 4, y 2 … 4.
    /// </summary>
    [Fact]
    public void PrecinctPartition_AnchoredAtOrigin()
    {
        var res = AnnexB.Tile(AnnexB.GridBCodestream(precincts: [0xFF, 0xFF, 0x32])).Components[0].Resolutions[2];
        Assert.Equal((2, 3), (res.PrecinctWidth, res.PrecinctHeight));
        Assert.Equal((1, 0), (res.FirstPrecinctX, res.FirstPrecinctY));
        Assert.True(res.FirstPrecinctX << res.PrecinctWidth < res.X0 && res.X0 < (res.FirstPrecinctX + 1) << res.PrecinctWidth);
        var block = Assert.Single(res.Precincts[0]![0]!.Blocks);
        Assert.Equal((2, 4, 2, 4), (block.X0, block.X1, block.Y0, block.Y1));
    }

    /// <summary>
    /// T.800 B.6 (S6–S15), equation B-16: numprecinctswide = ⌈trx1 / 2^PPx⌉ − ⌊trx0 / 2^PPx⌋ (0 for an empty resolution), likewise high —
    /// grid B, r = 2 with PPx 2, PPy 3: 5 − 1 = 4 wide, 2 − 0 = 2 high, 8 precincts; r = 0 without precincts given (15) one; a
    /// tile-component without samples (XRsiz 255 over x 1 … 2) none.
    /// </summary>
    [Fact]
    public void NumberOfPrecincts_AsB16()
    {
        var tc = AnnexB.Tile(AnnexB.GridBCodestream(precincts: [0xFF, 0xFF, 0x32])).Components[0];
        Assert.Equal((4, 2, 8), (tc.Resolutions[2].PrecinctsX, tc.Resolutions[2].PrecinctsY, tc.Resolutions[2].PrecinctCount));
        Assert.Equal(1, tc.Resolutions[0].PrecinctCount);
        var empty = AnnexB.Codestream([2, 1, 1, 0, 2, 1, 0, 0], [(255, 1)], [AnnexB.Cod(0), AnnexB.Qcd], 1, AnnexB.Empty);
        var res = AnnexB.Tile(empty).Components[0].Resolutions[0];
        Assert.Equal(0, res.Width);
        Assert.Equal((0, 0), (res.PrecinctsX, res.PrecinctCount));
    }

    /// <summary>
    /// T.800 B.6 (S20, S21): precinct indices run in raster order — grid B, r = 2 (4 × 2 precincts from column 1): precinct 3 is the last of
    /// row 0 (column 4), precinct 4 the first of row 1 (column 1), precinct 5 column 2 of row 1; their parts of sub-band HL (precincts 2 × 4)
    /// cut to the band.
    /// </summary>
    [Theory]
    [InlineData(3, 8, 9, 2, 4)]
    [InlineData(4, 2, 4, 4, 5)]
    [InlineData(5, 4, 6, 4, 5)]
    public void PrecinctIndex_InRasterOrder(int precinct, int x0, int x1, int y0, int y1)
    {
        var res = AnnexB.Tile(AnnexB.GridBCodestream(precincts: [0xFF, 0xFF, 0x32])).Components[0].Resolutions[2];
        var block = Assert.Single(res.Precincts[precinct]![0]!.Blocks);
        Assert.Equal((x0, x1, y0, y1), (block.X0, block.X1, block.Y0, block.Y1));
    }

    /// <summary>
    /// T.800 B.6 (S22, S23): a tile-component and resolution without precincts has no packets — component 0 (XRsiz 255 over x 1 … 2) is
    /// empty, so the only packet of the tile is that of component 1: its one coefficient comes out right (224) and nothing is cut short.
    /// </summary>
    [Fact]
    public void NoPrecincts_NoPackets()
    {
        var data = AnnexB.Codestream([2, 1, 1, 0, 2, 1, 0, 0], [(255, 1), (1, 1)], [AnnexB.Cod(0), AnnexB.Qcd], 1,
            _ => AnnexB.OneBlockPacket(AnnexB.SingleCoefficient(false)));
        var planes = JpxDecoder.DecodePlanes(data, long.MaxValue, 0);
        Assert.Equal(0, planes.PlaneWidth[0]);
        Assert.Equal(new ushort[] { 224 }, planes.Planes[1]);
        Assert.False(planes.Truncated);
    }

    /// <summary>
    /// T.800 B.6 (S24, S25): a precinct can be empty — component 0 with NL 1 over the single grid point (0, 0): resolution 1 has one precinct,
    /// but HL, LH and HH have no coefficients (tbx and tby ⌈−1/2⌉ … 0). Its empty packet (one 0 byte) is read all the same, in CPRL between the
    /// LL packet of component 0 and the packet of component 1; both coefficients come out right (32 and 224).
    /// </summary>
    [Fact]
    public void EmptyPrecinct_ItsPacketStillRead()
    {
        var body = (byte[])[.. AnnexB.OneBlockPacket(AnnexB.SingleCoefficient(true)), 0x00, .. AnnexB.OneBlockPacket(AnnexB.SingleCoefficient(false))];
        var data = AnnexB.Codestream([1, 1, 0, 0, 1, 1, 0, 0], [(1, 1), (1, 1)], [AnnexB.Cod(0, order: 4), AnnexB.Coc(0, 1), AnnexB.Qcd], 1, _ => body);
        var res = AnnexB.Tile(data).Components[0].Resolutions[1];
        Assert.Equal(1, res.PrecinctCount);
        Assert.All(res.Bands, b => Assert.True(b.X1 <= b.X0 || b.Y1 <= b.Y0));
        var planes = JpxDecoder.DecodePlanes(data, long.MaxValue, 0);
        Assert.Equal(new ushort[] { 32 }, planes.Planes[0]);
        Assert.Equal(new ushort[] { 224 }, planes.Planes[1]);
        Assert.False(planes.Truncated);
    }

    // ------------------------------------------------------------ B.7

    /// <summary>
    /// T.800 B.7 (S1–S4, S11): xcb and ycb from COD (SPcod 4 and 2: 2^6 × 2^4), the same for every sub-band of the tile-component; with
    /// PPx 3 from r = 1 on the blocks there are bounded to 2^(3 − 1) = 4 columns (a block of HL in r = 2 covers x 4 … 8), r = 0 keeps 2^6.
    /// </summary>
    [Fact]
    public void CodeBlockSize_FromCodAndBoundedByPrecinct()
    {
        var tc = AnnexB.Tile(AnnexB.GridBCodestream(xcb: 6, ycb: 4)).Components[0];
        Assert.Equal((6, 4), (tc.Coding.BlockWidth, tc.Coding.BlockHeight));
        Assert.All(tc.Resolutions.SelectMany(r => r.Bands), b => Assert.Equal((6, 4), (b.BlockWidth, b.BlockHeight)));
        var bounded = AnnexB.Tile(AnnexB.GridBCodestream(xcb: 6, ycb: 4, precincts: [0xFF, 0xF3, 0xF3])).Components[0];
        Assert.Equal(6, bounded.Resolutions[0].Bands[0].BlockWidth);
        Assert.All(bounded.Resolutions[1..].SelectMany(r => r.Bands), b => Assert.Equal((2, 4), (b.BlockWidth, b.BlockHeight)));
        var block = bounded.Resolutions[2].Precincts[1]![0]!.Blocks[0];
        Assert.Equal((4, 8), (block.X0, block.X1));
    }

    /// <summary>
    /// T.800 B.7, equation B-17: xcb′ = min(xcb, PPx − 1) for r &gt; 0 and min(xcb, PPx) for r = 0 — xcb 6: PPx 5 gives 4 above r = 0 and 5
    /// at r = 0, PPx 7 gives 6 on both, PPx 0 at r = 0 gives 0 (one column).
    /// </summary>
    [Theory]
    [InlineData(15, 5, 1, 4)]
    [InlineData(5, 15, 0, 5)]
    [InlineData(15, 7, 1, 6)]
    [InlineData(7, 15, 0, 6)]
    [InlineData(0, 15, 0, 0)]
    public void CodeBlockWidth_AsB17(int ppx0, int ppx1, int r, int expected)
    {
        var data = AnnexB.Codestream([8, 8, 0, 0, 8, 8, 0, 0], [(1, 1)],
            [AnnexB.Cod(1, xcb: 6, ycb: 4, precincts: [(byte)(0xF0 | ppx0), (byte)(0xF0 | ppx1)]), AnnexB.Qcd], 1, AnnexB.Empty);
        Assert.All(AnnexB.Tile(data).Components[0].Resolutions[r].Bands, b => Assert.Equal(expected, b.BlockWidth));
    }

    /// <summary>
    /// T.800 B.7, equation B-18: ycb′ = min(ycb, PPy − 1) for r &gt; 0 and min(ycb, PPy) for r = 0 — ycb 4: PPy 3 gives 2 above r = 0 and 3 at
    /// r = 0; without precinct sizes in COD (15) 4 on both.
    /// </summary>
    [Theory]
    [InlineData(15, 3, 1, 2)]
    [InlineData(3, 15, 0, 3)]
    [InlineData(-1, -1, 1, 4)]
    [InlineData(-1, -1, 0, 4)]
    public void CodeBlockHeight_AsB18(int ppy0, int ppy1, int r, int expected)
    {
        byte[]? precincts = ppy0 < 0 ? null : [(byte)(ppy0 << 4 | 0x0F), (byte)(ppy1 << 4 | 0x0F)];
        var data = AnnexB.Codestream([8, 8, 0, 0, 8, 8, 0, 0], [(1, 1)], [AnnexB.Cod(1, xcb: 4, ycb: 4, precincts: precincts), AnnexB.Qcd], 1, AnnexB.Empty);
        Assert.All(AnnexB.Tile(data).Components[0].Resolutions[r].Bands, b => Assert.Equal(expected, b.BlockHeight));
    }

    /// <summary>
    /// T.800 B.7 (S12, S13): the code-block partition is anchored at (0, 0) — grid B, band 1HL (x 2 … 9, y 2 … 5) with 4 × 4 blocks in one
    /// precinct: columns 2 … 4, 4 … 8, 8 … 9 and rows 2 … 4, 4 … 5 (block edges on multiples of 4, cut to the band).
    /// </summary>
    [Fact]
    public void CodeBlockPartition_AnchoredAtOrigin()
    {
        var precinct = AnnexB.Tile(AnnexB.GridBCodestream(xcb: 2, ycb: 2)).Components[0].Resolutions[2].Precincts[0]![0]!;
        Assert.Equal(3, precinct.BlocksX);
        Assert.Equal([(2, 4, 2, 4), (4, 8, 2, 4), (8, 9, 2, 4), (2, 4, 4, 5), (4, 8, 4, 5), (8, 9, 4, 5)],
            precinct.Blocks.Select(b => (b.X0, b.X1, b.Y0, b.Y1)));
    }

    /// <summary>
    /// T.800 B.7 (S15, S16): only the coefficients inside the band are coded, the first stripe being the first four rows of the cut block —
    /// a block of 1 × 6: the cleanup pass codes rows 0–3 as a stripe of four (run-length decision, the run broken at row 2, its sign, row 3
    /// in context 3) and rows 4–5 as a stripe of two without run-length mode (row 4 in context 0 with a negative sign, row 5 in context 3).
    /// The decisions are coded by hand for this layout; decoded, rows 2 and 4 are significant (±3 · 2^6), all others 0. A block of 1 × 1 at
    /// the band end codes its one coefficient.
    /// </summary>
    [Fact]
    public void CodeBlockStripes_FirstFourRowsThenTheRest()
    {
        var data = AnnexB.Mq((17, 1), (18, 1), (18, 0), (9, 0), (3, 0), (0, 1), (9, 1), (3, 0));
        var decoder = new JpxBlockDecoder();
        decoder.Decode(1, 6, [(data, 1)], 0, 0, 6);
        Assert.Equal([0, 0, 192, 0, -192, 0], Enumerable.Range(0, 6).Select(y => decoder.At(0, y)));
        decoder.Decode(1, 1, [(AnnexB.SingleCoefficient(false), 1)], 0, 0, 6);
        Assert.Equal(192, decoder.At(0, 0));
    }

    // ------------------------------------------------------------ B.8

    /// <summary>
    /// T.800 B.8 (S1, S2, S5): the passes of a code-block are spread over layers, those of layer 1 following those of layer 0 — 1 pass in
    /// layer 0, 2 in layer 1, without termination on each pass: one codeword segment of 3 passes, its data in the order of the layers.
    /// </summary>
    [Fact]
    public void Layers_PassesOfACodeBlockContinueAcrossLayers()
    {
        var data = AnnexB.Codestream([4, 4, 0, 0, 4, 4, 0, 0], [(1, 1)], [AnnexB.Cod(0, layers: 2), AnnexB.Qcd], 1,
            _ => [.. AnnexB.Layer0, .. AnnexB.Layer1]);
        var block = AnnexB.Tile(data).Components[0].Resolutions[0].Precincts[0]![0]!.Blocks[0];
        var segment = Assert.Single(block.Segments!);
        Assert.Equal(3, segment.Passes);
        Assert.Equal(2, segment.Pieces!.Count);
        Assert.Equal(new byte[] { 0xA0, 0xA1, 0xB0, 0xB1, 0xB2 }, segment.Join());
    }

    /// <summary>
    /// T.800 B.8 (S4, S7): the number of layers L of the tile comes from COD, layers 0 … L − 1 — main header COD with 3 layers, the tile's own
    /// COD with 2: a third packet behind the two is not read (3 passes, 2 pieces); with the main header alone it is (4 passes, 3 pieces).
    /// </summary>
    [Fact]
    public void Layers_NumberFromTheTileCod()
    {
        byte[] layer2 = [.. AnnexB.Bits("1 1 0 0 010"), 0xC0, 0xC1];
        byte[] Body(int _) => [.. AnnexB.Layer0, .. AnnexB.Layer1, .. layer2];
        var withTileCod = AnnexB.Codestream([4, 4, 0, 0, 4, 4, 0, 0], [(1, 1)], [AnnexB.Cod(0, layers: 3), AnnexB.Qcd], 1, Body,
            _ => [AnnexB.Cod(0, layers: 2)]);
        var segment = Assert.Single(AnnexB.Tile(withTileCod).Components[0].Resolutions[0].Precincts[0]![0]!.Blocks[0].Segments!);
        Assert.Equal((3, 2), (segment.Passes, segment.Pieces!.Count));
        var mainOnly = AnnexB.Codestream([4, 4, 0, 0, 4, 4, 0, 0], [(1, 1)], [AnnexB.Cod(0, layers: 3), AnnexB.Qcd], 1, Body);
        segment = Assert.Single(AnnexB.Tile(mainOnly).Components[0].Resolutions[0].Precincts[0]![0]!.Blocks[0].Segments!);
        Assert.Equal((4, 3), (segment.Passes, segment.Pieces!.Count));
    }

    /// <summary>
    /// T.800 B.8 (S55, S56): a packet per layer, resolution and precinct of the tile-component — a 2 × 2 tile, NL 1, one precinct per
    /// resolution, 2 layers: exactly 4 packets. Four empty packets, then a packet that would include a block: it is not read. With three empty
    /// packets the same packet is the fourth (layer 1, resolution 1) and its block of HL gets the data.
    /// </summary>
    [Fact]
    public void Packets_OnePerLayerResolutionAndPrecinct()
    {
        // layer 1, resolution 1: HL block included (tag tree reaches threshold 2 at once), 1 pass, 2 bytes; LH and HH not included
        byte[] including = [.. AnnexB.Bits("1  1 1 0 0 010  00  00"), 0xD0, 0xD1];
        byte[] Data(int empty) => AnnexB.Codestream([2, 2, 0, 0, 2, 2, 0, 0], [(1, 1)], [AnnexB.Cod(1, layers: 2), AnnexB.Qcd], 1,
            _ => [.. new byte[empty], .. including]);
        var tc = AnnexB.Tile(Data(4)).Components[0];
        var blocks = tc.Resolutions.SelectMany(r => r.Precincts).SelectMany(p => p!).SelectMany(b => b?.Blocks ?? []).ToArray();
        Assert.Equal(4, blocks.Length);   // LL, HL, LH, HH: one block each
        Assert.All(blocks, b => Assert.Null(b.Segments));
        var hl = AnnexB.Tile(Data(3)).Components[0].Resolutions[1].Precincts[0]![0]!.Blocks[0];
        Assert.Equal(new byte[] { 0xD0, 0xD1 }, Assert.Single(hl.Segments!).Join());
    }

    // ------------------------------------------------------------ B.9

    /// <summary>
    /// T.800 B.9 (S1, S2): packet data is aligned to bytes — after a header of 3 bits the next packet data starts at the next byte; in the
    /// codestream of two layers the data of packet 0 follows its header at byte 1, and the header of packet 1 starts at byte 3, right after
    /// it, with its data at byte 5.
    /// </summary>
    [Fact]
    public void PacketData_AlignedToBytes()
    {
        var bits = new JpxBits([0xA0, 0x12], 0, 2);
        Assert.Equal(5, bits.Bits(3));
        bits.Align();
        Assert.Equal(1, bits.Position);
        Assert.Equal(0x12, bits.Bits(8));
        var data = AnnexB.Codestream([4, 4, 0, 0, 4, 4, 0, 0], [(1, 1)], [AnnexB.Cod(0, layers: 2), AnnexB.Qcd], 1,
            _ => [.. AnnexB.Layer0, .. AnnexB.Layer1]);
        var pieces = AnnexB.Tile(data).Components[0].Resolutions[0].Precincts[0]![0]!.Blocks[0].Segments![0].Pieces!;
        Assert.Equal([(1, 2), (5, 3)], pieces.Select(p => (p.Start, p.Length)));
    }

    /// <summary>
    /// T.800 B.9 (S3–S5, S9): r = 0 holds the NL-LL band, every r &gt; 0 the bands nHL, nLH, nHH with n = NL − r + 1 — grid B (NL 2): the
    /// bands of resolution 1 have the bounds of decomposition level 2, those of resolution 2 the bounds of level 1, resolution 0 only LL.
    /// </summary>
    [Fact]
    public void ResolutionSubBands_DecompositionLevelPerResolution()
    {
        var tc = AnnexB.Tile(AnnexB.GridBCodestream()).Components[0];
        var ll = Assert.Single(tc.Resolutions[0].Bands);
        Assert.Equal((0, 2, 5, 1, 3), (ll.Orientation, ll.X0, ll.X1, ll.Y0, ll.Y1));
        Assert.Equal([(1, 4, 1, 3), (2, 5, 1, 2), (1, 4, 1, 2)], tc.Resolutions[1].Bands.Select(b => (b.X0, b.X1, b.Y0, b.Y1)));
        Assert.Equal([(2, 9, 2, 5), (3, 9, 1, 5), (2, 9, 1, 5)], tc.Resolutions[2].Bands.Select(b => (b.X0, b.X1, b.Y0, b.Y1)));
    }

    /// <summary>
    /// T.800 B.9 (S6–S8, S10, S44, S45): in a packet HL, LH, HH (or LL) follow in this order, within a band the code-blocks in raster order,
    /// only those inside band and precinct, first the header, then the body in the same order. Tile-component 7 … 11 × 6 … 9, NL 1, 4 × 4
    /// blocks: resolution 1 holds 2 × 2 blocks of HL, one of LH and 2 × 1 of HH. The header includes HL blocks 0 and 3 (2 and 3 bytes) and the
    /// LH block (1 byte): body bytes 0–1 go to HL block 0, 2–4 to HL block 3, 5 to the LH block.
    /// </summary>
    [Fact]
    public void PacketContents_InSubBandAndRasterOrder()
    {
        var header = AnnexB.Bits("1  11 11 0 0 010  0  0  1 1 0 0 011  1 1 0 0 001  0");
        byte[] body = [0x00, .. header, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16];
        var data = AnnexB.Codestream([11, 9, 7, 6, 11, 9, 0, 0], [(1, 1)], [AnnexB.Cod(1, xcb: 2, ycb: 2), AnnexB.Qcd], 1, _ => body);
        var precinct = AnnexB.Tile(data).Components[0].Resolutions[1].Precincts[0]!;
        var (hl, lh, hh) = (precinct[0]!, precinct[1]!, precinct[2]!);
        Assert.Equal([(3, 4, 3, 4), (4, 5, 3, 4), (3, 4, 4, 5), (4, 5, 4, 5)], hl.Blocks.Select(b => (b.X0, b.X1, b.Y0, b.Y1)));
        Assert.Equal([(4, 6, 3, 4)], lh.Blocks.Select(b => (b.X0, b.X1, b.Y0, b.Y1)));
        Assert.Equal([(3, 4, 3, 4), (4, 5, 3, 4)], hh.Blocks.Select(b => (b.X0, b.X1, b.Y0, b.Y1)));
        var start = 1 + header.Length;
        Assert.Equal([(start, 2)], hl.Blocks[0].Segments!.SelectMany(s => s.Pieces!).Select(p => (p.Start, p.Length)));
        Assert.Equal([(start + 2, 3)], hl.Blocks[3].Segments!.SelectMany(s => s.Pieces!).Select(p => (p.Start, p.Length)));
        Assert.Equal([(start + 5, 1)], lh.Blocks[0].Segments!.SelectMany(s => s.Pieces!).Select(p => (p.Start, p.Length)));
        Assert.Null(hl.Blocks[1].Segments);
        Assert.Null(hl.Blocks[2].Segments);
        Assert.All(hh.Blocks, b => Assert.Null(b.Segments));
    }
}
