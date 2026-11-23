using System.Buffers.Binary;
using PdfKnead.Core.Pdf.Filters.Jbig2;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// ITU-T T.800 Annex A head and A.1–A.5 sentence by sentence (codestream syntax, marker codes, SOC, SOT, SOD, EOC, SIZ). The codestreams are
/// built here: every component is coded without wavelet levels, reversibly, with one code-block per precinct and one cleanup pass on bit-plane 0,
/// so each sample is the DC level plus −1, 0 or +1 and the decoded planes can be compared exactly with what was put in. Each test changes one
/// marker, field or segment and checks the decoder against the expected planes.
/// </summary>
public sealed partial class JpxMarkerTests
{
    /// <summary>The codestream builder of these tests (own names: other partial files of the class have helpers of their own).</summary>
    private static class AnnexA
    {

        /// <summary>
        /// One component as the builder codes it: subsampling, Ssiz, precinct exponent at resolution 0 (15: no precincts given), exponent ε of
        /// QCD/QCC (guard bits 1, so Mb = ε), the ROI shift the data are coded for and the bit-plane of the coefficients.
        /// </summary>
        internal sealed record TestComponent(int Dx = 1, int Dy = 1, int Ssiz = 7, int Precinct = 15, int Exponent = 8, int Roi = 0, int Plane = 0);

        /// <summary>An image on the reference grid with its tiles and components; <see cref="Value"/> gives −1, 0 or +1 per component sample.</summary>
        internal sealed class TestImage
        {
            public long X0, Y0, X1 = 8, Y1 = 8, TileX0, TileY0, TileWidth = 64, TileHeight = 64;
            public int Rsiz;
            public TestComponent[] Components = [new(), new(), new()];
            public Func<int, long, long, int> Value = (c, x, y) => (int)((x * 5 + y * 3 + c * 7 + x * y) % 3) - 1;

            public int TilesX => (int)CeilDiv(X1 - TileX0, TileWidth);
            public int TilesY => (int)CeilDiv(Y1 - TileY0, TileHeight);
            public int Tiles => TilesX * TilesY;

            public (long X0, long Y0, long X1, long Y1) Tile(int t)
            {
                long p = t % TilesX, q = t / TilesX;
                return (Math.Max(TileX0 + p * TileWidth, X0), Math.Max(TileY0 + q * TileHeight, Y0),
                    Math.Min(TileX0 + (p + 1) * TileWidth, X1), Math.Min(TileY0 + (q + 1) * TileHeight, Y1));
            }
        }

        /// <summary>One packet: component, precinct index, its header bits and the code-block data behind them.</summary>
        internal sealed record TestPacket(int Component, int Precinct, byte[] Header, byte[] Body);

        internal static long CeilDiv(long a, long b) => (a + b - 1) / b;

        internal static byte[] Be32(long v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

        internal static byte[] Be16(int v) => [(byte)(v >> 8), (byte)v];

        internal static byte[] Siz(TestImage im) => Segment(0xFF51,
        [
            .. Be16(im.Rsiz), .. Be32(im.X1), .. Be32(im.Y1), .. Be32(im.X0), .. Be32(im.Y0),
            .. Be32(im.TileWidth), .. Be32(im.TileHeight), .. Be32(im.TileX0), .. Be32(im.TileY0),
            .. Be16(im.Components.Length), .. im.Components.SelectMany(c => new[] { (byte)c.Ssiz, (byte)c.Dx, (byte)c.Dy }),
        ]);

        /// <summary>SPcod/SPcoc: no levels, code-blocks of 64 × 64, the given style, 5-3 reversible; one precinct size if given.</summary>
        internal static byte[] Coding(int precinct, int style) =>
            precinct < 15 ? [0, 4, 4, (byte)style, 1, (byte)(precinct << 4 | precinct)] : [0, 4, 4, (byte)style, 1];

        /// <summary>COD: Scod (precincts, SOP 2, EPH 4), progression order, one layer, no component transformation.</summary>
        internal static byte[] Cod(int precinct = 15, int scod = 0, int order = 0, int style = 0) =>
            Segment(0xFF52, [(byte)((precinct < 15 ? 1 : 0) | scod), (byte)order, 0, 1, 0, .. Coding(precinct, style)]);

        internal static byte[] Coc(int component, int precinct) => Segment(0xFF53, [(byte)component, (byte)(precinct < 15 ? 1 : 0), .. Coding(precinct, 0)]);

        /// <summary>QCD without quantisation: guard bits 1, exponent ε of the one sub-band.</summary>
        internal static byte[] Qcd(int exponent = 8) => Segment(0xFF5C, 1 << 5, (byte)(exponent << 3));

        internal static byte[] Qcc(int component, int exponent) => Segment(0xFF5D, (byte)component, 1 << 5, (byte)(exponent << 3));

        internal static byte[] Rgn(int component, int shift) => Segment(0xFF5E, (byte)component, 0, (byte)shift);

        internal static byte[] Poc(params (int Rs, int Cs, int Lye, int Re, int Ce, int Order)[] entries) =>
            Segment(0xFF5F, [.. entries.SelectMany(e => new byte[] { (byte)e.Rs, (byte)e.Cs, 0, (byte)e.Lye, (byte)e.Re, (byte)e.Ce, (byte)e.Order })]);

        internal static readonly byte[] Comment = Segment(0xFF64, [0x00, 0x01, .. "Probe"u8]);

        /// <summary>
        /// The packets of tile <paramref name="t"/> in the order of LRCP with one layer and one resolution: component by component, the
        /// precincts of each in raster order — each precinct one code-block (asserted).
        /// </summary>
        internal static List<TestPacket> Packets(TestImage im, int t)
        {
            var (tx0, ty0, tx1, ty1) = im.Tile(t);
            var list = new List<TestPacket>();
            for (var c = 0; c < im.Components.Length; c++)
            {
                var k = im.Components[c];
                long cx0 = CeilDiv(tx0, k.Dx), cy0 = CeilDiv(ty0, k.Dy), cx1 = CeilDiv(tx1, k.Dx), cy1 = CeilDiv(ty1, k.Dy);
                if (cx1 <= cx0 || cy1 <= cy0) continue;
                long size = 1L << k.Precinct, block = Math.Min(6, k.Precinct);
                var index = 0;
                for (var py = cy0 >> k.Precinct; py < CeilDiv(cy1, size); py++)
                for (var px = cx0 >> k.Precinct; px < CeilDiv(cx1, size); px++)
                {
                    long x0 = Math.Max(cx0, px * size), x1 = Math.Min(cx1, (px + 1) * size);
                    long y0 = Math.Max(cy0, py * size), y1 = Math.Min(cy1, (py + 1) * size);
                    Assert.Equal(x0 >> (int)block, (x1 - 1) >> (int)block);
                    Assert.Equal(y0 >> (int)block, (y1 - 1) >> (int)block);
                    var values = new int[y1 - y0, x1 - x0];
                    for (var y = y0; y < y1; y++)
                    for (var x = x0; x < x1; x++) values[y - y0, x - x0] = im.Value(c, x, y);
                    var body = Cleanup(values);
                    var zero = k.Exponent + k.Roi - 1 - k.Plane;   // Mb = guard bits 1 + ε − 1, raised by the ROI shift (H-3)
                    Assert.True(zero >= 0);
                    list.Add(new TestPacket(c, index++, Header(zero, body.Length), body));
                }
            }
            return list;
        }

        /// <summary>
        /// The header of a packet with one code-block, included in layer 0 (B.10): non-empty, inclusion, zero bit-planes, one coding pass, the length
        /// with Lblock 3 and as many increments as it needs; bit stuffing after 0xFF (B.10.1).
        /// </summary>
        internal static byte[] Header(int zeroPlanes, int length)
        {
            var bytes = new List<byte>();
            int current = 0, count = 0, limit = 8;
            void Put(int bit)
            {
                current = current << 1 | bit;
                if (++count < limit) return;
                bytes.Add((byte)current);
                limit = current == 0xFF ? 7 : 8;
                current = count = 0;
            }
            Put(1);
            Put(1);
            for (var i = 0; i < zeroPlanes; i++) Put(0);
            Put(1);
            Put(0);
            var bits = Math.Max(3, 32 - int.LeadingZeroCount(length));
            for (var i = 3; i < bits; i++) Put(1);
            Put(0);
            for (var k = bits - 1; k >= 0; k--) Put(length >> k & 1);
            if (count > 0)
            {
                current <<= limit - count;
                bytes.Add((byte)current);
            }
            if (bytes[^1] == 0xFF) bytes.Add(0);
            return [.. bytes];
        }

        /// <summary>
        /// One cleanup pass over the block (D.3.4) for coefficients −1, 0 and +1: run-length mode where four samples of a column have no significant
        /// neighbour, otherwise zero coding; the sign of each new significant coefficient (Tables D.1, D.2, D.3, contexts of Table D.7).
        /// </summary>
        internal static byte[] Cleanup(int[,] v)
        {
            int h = v.GetLength(0), w = v.GetLength(1);
            var significant = new bool[h + 2, w + 2];
            var negative = new bool[h + 2, w + 2];
            var cx = JpxBlockDecoder.InitialContexts();
            var mq = new MqEncoder();
            bool S(int y, int x) => significant[y + 1, x + 1];
            bool N(int y, int x) => negative[y + 1, x + 1];
            int Pattern(int y, int x) =>
                (S(y, x - 1) ? 1 : 0) | (S(y, x + 1) ? 2 : 0) | (S(y - 1, x) ? 4 : 0) | (S(y + 1, x) ? 8 : 0)
                | (S(y - 1, x - 1) ? 16 : 0) | (S(y - 1, x + 1) ? 32 : 0) | (S(y + 1, x - 1) ? 64 : 0) | (S(y + 1, x + 1) ? 128 : 0);
            void Sign(int y, int x)
            {
                var index = (Pattern(y, x) & 0xF) | (N(y, x - 1) ? 16 : 0) | (N(y, x + 1) ? 32 : 0) | (N(y - 1, x) ? 64 : 0) | (N(y + 1, x) ? 128 : 0);
                var entry = JpxBlockDecoder.SignContexts[index];
                mq.Encode(cx, entry & 0x1F, (v[y, x] < 0 ? 1 : 0) ^ (entry >> 7));
                significant[y + 1, x + 1] = true;
                negative[y + 1, x + 1] = v[y, x] < 0;
            }
            for (var y0 = 0; y0 < h; y0 += 4)
            {
                var rows = Math.Min(4, h - y0);
                for (var x = 0; x < w; x++)
                {
                    var r = 0;
                    if (rows == 4 && Enumerable.Range(0, 4).All(i => Pattern(y0 + i, x) == 0))
                    {
                        var first = Enumerable.Range(0, 4).FirstOrDefault(i => v[y0 + i, x] != 0, -1);
                        mq.Encode(cx, 17, first >= 0 ? 1 : 0);
                        if (first < 0) continue;
                        mq.Encode(cx, 18, first >> 1);
                        mq.Encode(cx, 18, first & 1);
                        Sign(y0 + first, x);
                        r = first + 1;
                    }
                    for (; r < rows; r++)
                    {
                        var y = y0 + r;
                        mq.Encode(cx, JpxBlockDecoder.ZeroContexts[0][Pattern(y, x)], v[y, x] != 0 ? 1 : 0);
                        if (v[y, x] != 0) Sign(y, x);
                    }
                }
            }
            // the flushed codeword without the marker the encoder appends for JBIG2, and without a final 0xFF (C.2.9)
            var data = mq.Finish().ToList();
            data.RemoveAt(data.Count - 1);
            while (data.Count > 0 && data[^1] == 0xFF) data.RemoveAt(data.Count - 1);
            return [.. data];
        }

        /// <summary>
        /// A tile-part: SOT (Lsot 10, Psot of what follows), the header segments, SOD, then the packets — with SOP before each, EPH behind each
        /// header, or without the headers where PPM or PPT carries them.
        /// </summary>
        internal static byte[] TilePart(int isot, IEnumerable<TestPacket> packets, byte[][]? header = null, int tpsot = 0, int tnsot = 1,
            bool sop = false, bool eph = false, bool headersElsewhere = false)
        {
            var body = new List<byte>();
            var n = 0;
            foreach (var p in packets)
            {
                if (sop) body.AddRange([0xFF, 0x91, 0x00, 0x04, .. Be16(n)]);
                n++;
                if (!headersElsewhere)
                {
                    body.AddRange(p.Header);
                    if (eph) body.AddRange([0xFF, 0x92]);
                }
                body.AddRange(p.Body);
            }
            var head = (header ?? []).SelectMany(s => s).ToArray();
            return [0xFF, 0x90, 0x00, 0x0A, .. Be16(isot), .. Be32(12 + head.Length + 2 + body.Count), (byte)tpsot, (byte)tnsot, .. head, 0xFF, 0x93, .. body];
        }

        /// <summary>SOC, SIZ, the main header segments, the tile-parts, EOC.</summary>
        internal static byte[] Stream(TestImage im, byte[][] main, params byte[][] tileParts) =>
            [0xFF, 0x4F, .. Siz(im), .. main.SelectMany(s => s), .. tileParts.SelectMany(t => t), 0xFF, 0xD9];

        /// <summary>COD, QCD (and <paramref name="mainExtra"/>), each tile in one tile-part with <paramref name="tileHeader"/>.</summary>
        internal static byte[] Simple(TestImage im, byte[][]? mainExtra = null, byte[][]? tileHeader = null) =>
            Stream(im, [Cod(), Qcd(), .. mainExtra ?? []], [.. Enumerable.Range(0, im.Tiles).Select(t => TilePart(t, Packets(im, t), tileHeader))]);

        /// <summary>The planes the image should decode to: the DC level of each component plus its value (bit-plane 0).</summary>
        internal static ushort[][] Expected(TestImage im)
        {
            var planes = new ushort[im.Components.Length][];
            for (var c = 0; c < planes.Length; c++)
            {
                var k = im.Components[c];
                long x0 = CeilDiv(im.X0, k.Dx), y0 = CeilDiv(im.Y0, k.Dy), w = CeilDiv(im.X1, k.Dx) - x0, h = CeilDiv(im.Y1, k.Dy) - y0;
                planes[c] = new ushort[w * h];
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++) planes[c][y * w + x] = (ushort)((1 << (k.Ssiz & 0x7F)) + im.Value(c, x0 + x, y0 + y));
            }
            return planes;
        }

        /// <summary>The decoded planes and whether the image counts as truncated; null if the decoder refuses the data.</summary>
        internal static (ushort[][] Planes, bool Truncated)? Decoded(byte[] cs)
        {
            try
            {
                var p = JpxDecoder.DecodePlanes(cs, long.MaxValue);
                return (p.Planes, p.Truncated);
            }
            catch (JpxException)
            {
                return null;
            }
        }

        /// <summary>The decoded planes of a complete image; null if the data are refused or the image counts as truncated.</summary>
        internal static ushort[][]? Clean(byte[] cs) => Decoded(cs) is ({ } planes, false) ? planes : null;

        internal static bool Truncated(byte[] cs) => JpxCodestream.Parse(cs, 0, cs.Length).Truncated;
    }

    /// <summary>The builder itself: its codestream decodes to the planes put in (one tile, three components; four tiles; one component).</summary>
    [Fact]
    public void Builder_DecodesToItsValues()
    {
        var one = new AnnexA.TestImage();
        Assert.Equal(AnnexA.Expected(one), AnnexA.Clean(AnnexA.Simple(one)));
        var four = new AnnexA.TestImage { TileWidth = 4, TileHeight = 4 };
        Assert.Equal(4, four.Tiles);
        Assert.Equal(AnnexA.Expected(four), AnnexA.Clean(AnnexA.Simple(four)));
        var grey = new AnnexA.TestImage { X1 = 13, Y1 = 9, Components = [new()] };
        Assert.Equal(AnnexA.Expected(grey), AnnexA.Clean(AnnexA.Simple(grey)));
        Assert.Contains(AnnexA.Expected(one)[0], v => v == 127);
        Assert.Contains(AnnexA.Expected(one)[0], v => v == 129);
    }

    // ---- A.1, A.2: where the segments may stand

    /// <summary>
    /// T.800 A.1 (sentence 7), A.2 (Table A.2), A.5.1 (sentence 2): SIZ, CAP, PRF, TLM, PLM, PPM and CRG belong to the main header only; in a
    /// tile-part header they leave the image as it is — SIZ with another image size, CAP with the second bit of Rsiz and an otherwise ignored bit
    /// of the code-block style (in the main header it would refuse the data), a PPM whose packet headers would empty every packet — and the
    /// codestream is reported (decided on 23.11.2026: read and report; before they were passed over without a word).
    /// </summary>
    [Theory]
    [InlineData("SIZ")]
    [InlineData("CAP")]
    [InlineData("PRF")]
    [InlineData("TLM")]
    [InlineData("PLM")]
    [InlineData("PPM")]
    [InlineData("CRG")]
    public void MainHeaderOnlySegment_InTilePartHeader_PassedOver(string segment)
    {
        var im = new AnnexA.TestImage();
        var cod = AnnexA.Cod();
        if (segment == "CAP")
        {
            im.Rsiz = 0x4000;
            cod = AnnexA.Cod(style: 0x40);
        }
        var inTile = segment switch
        {
            "SIZ" => AnnexA.Siz(new AnnexA.TestImage { X1 = 16, Y1 = 4 }),
            "CAP" => Segment(0xFF50, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00),
            "PRF" => Segment(0xFF56, 0x10, 0x00),
            "TLM" => Segment(0xFF55, 0x00, 0x60, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00),
            "PLM" => Segment(0xFF57, 0x00, 0x02, 0x81, 0x00),
            "PPM" => Segment(0xFF60, [0x00, .. AnnexA.Be32(12), .. new byte[12]]),
            _ => Segment(0xFF63, new byte[12]),
        };
        var expected = AnnexA.Expected(im);
        Assert.Equal(expected, AnnexA.Clean(AnnexA.Stream(im, [cod, AnnexA.Qcd()], AnnexA.TilePart(0, AnnexA.Packets(im, 0)))));
        var misplaced = AnnexA.Decoded(AnnexA.Stream(im, [cod, AnnexA.Qcd()], AnnexA.TilePart(0, AnnexA.Packets(im, 0), [inTile])));
        Assert.Equal(expected, misplaced!.Value.Planes);
        Assert.True(misplaced.Value.Truncated);
    }

    /// <summary>
    /// T.800 A.1 (sentence 7), A.2 (Table A.2): PLT and PPT belong to tile-part headers only; in the main header they leave the image as it is — a
    /// PPT there whose content, read as packet headers, would empty every packet — and the codestream is reported (read and report).
    /// </summary>
    [Theory]
    [InlineData("PLT")]
    [InlineData("PPT")]
    public void TilePartOnlySegment_InMainHeader_PassedOver(string segment)
    {
        var im = new AnnexA.TestImage();
        var inMain = segment == "PLT" ? Segment(0xFF58, 0x00, 0x05, 0x03) : Segment(0xFF61, [0x00, .. AnnexA.Be32(12), .. new byte[12]]);
        var misplaced = AnnexA.Decoded(AnnexA.Simple(im, mainExtra: [inMain]));
        Assert.Equal(AnnexA.Expected(im), misplaced!.Value.Planes);
        Assert.True(misplaced.Value.Truncated);
    }

    /// <summary>
    /// T.800 A.1.2 (sentence 4) with A.1.3: the codes 0xFF30 to 0xFF3F are markers without parameters — two bytes, skipped, in the main header
    /// (between COD and QCD) and in a tile-part header; the image stays the same.
    /// </summary>
    [Theory]
    [InlineData(0xFF30, false)]
    [InlineData(0xFF30, true)]
    [InlineData(0xFF3F, false)]
    [InlineData(0xFF3F, true)]
    public void MarkerWithoutParameters_0xFF30To0xFF3F_Skipped(int marker, bool inTilePart)
    {
        var im = new AnnexA.TestImage();
        byte[] bare = [(byte)(marker >> 8), (byte)marker];
        var cs = inTilePart
            ? AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], AnnexA.TilePart(0, AnnexA.Packets(im, 0), [bare, AnnexA.Comment]))
            : AnnexA.Stream(im, [AnnexA.Cod(), bare, AnnexA.Qcd()], AnnexA.TilePart(0, AnnexA.Packets(im, 0)));
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(cs));
    }

    /// <summary>
    /// T.800 A.1.2, Table A.1: codes of ITU-T T.81 (0xFFC4, 0xFFFE), T.84 (0xFFF0), T.87 (0xFFF7) and reserved ones (0xFF70, 0xFF80) are no
    /// segments of this standard — passed over by their length in the main header and in a tile-part header; the image stays the same. (The
    /// range 0xFF30–0xFF3F without a length: <see cref="MarkerWithoutParameters_0xFF30To0xFF3F_Skipped"/>; a code of the range of T.800:
    /// <see cref="UnknownMarkerSegment_PassedOver"/>.)
    /// </summary>
    [Theory]
    [InlineData(0xFFC4)]
    [InlineData(0xFFFE)]
    [InlineData(0xFFF0)]
    [InlineData(0xFFF7)]
    [InlineData(0xFF70)]
    [InlineData(0xFF80)]
    public void MarkerOfAnotherStandardOrReserved_PassedOverByItsLength(int marker)
    {
        var im = new AnnexA.TestImage();
        var segment = Segment(marker, 0x01, 0x02, 0x03);
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(), segment, AnnexA.Qcd()], AnnexA.TilePart(0, AnnexA.Packets(im, 0), [segment]))));
    }

    // ---- A.2, Table A.2: each code recognised

    /// <summary>
    /// T.800 A.2, Table A.2 (COC 0xFF53): a COC in the main header gives component 1 precincts of 4 × 4 (four packets) — the data coded so
    /// decode exactly, components 0 and 2 keep the coding of COD. The same COC naming component 0 does not fit the data.
    /// </summary>
    [Fact]
    public void Coc_0xFF53_AppliesToItsComponent()
    {
        var im = new AnnexA.TestImage { Components = [new(), new(Precinct: 2), new()] };
        var packets = AnnexA.Packets(im, 0);
        Assert.Equal(6, packets.Count);
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Coc(1, 2), AnnexA.Qcd()], AnnexA.TilePart(0, packets))));
        Assert.NotEqual(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Coc(0, 2), AnnexA.Qcd()], AnnexA.TilePart(0, packets))));
    }

    /// <summary>
    /// T.800 A.2, Table A.2 (QCC 0xFF5D): a QCC with exponent 9 for component 1 (one more magnitude bit-plane) — the data coded so decode
    /// exactly, the others keep QCD; the same QCC naming component 0 does not fit.
    /// </summary>
    [Fact]
    public void Qcc_0xFF5D_AppliesToItsComponent()
    {
        var im = new AnnexA.TestImage { Components = [new(), new(Exponent: 9), new()] };
        var packets = AnnexA.Packets(im, 0);
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd(), AnnexA.Qcc(1, 9)], AnnexA.TilePart(0, packets))));
        Assert.NotEqual(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd(), AnnexA.Qcc(0, 9)], AnnexA.TilePart(0, packets))));
    }

    /// <summary>
    /// T.800 A.2, Table A.2 (RGN 0xFF5E) with H.1: component 0 coded as background of an ROI with shift 3 (the bit-plane counted from Mb + 3,
    /// H-3) — with the RGN it decodes exactly; without it the coefficients lie below the bit-planes there are and component 0 stays at its DC
    /// level, the image changes.
    /// </summary>
    [Fact]
    public void Rgn_0xFF5E_ShiftApplies()
    {
        var im = new AnnexA.TestImage { Components = [new(Roi: 3), new(), new()] };
        var packets = AnnexA.Packets(im, 0);
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd(), AnnexA.Rgn(0, 3)], AnnexA.TilePart(0, packets))));
        var without = AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], AnnexA.TilePart(0, packets)))!;
        Assert.NotNull(without);
        Assert.All(without[0], v => Assert.Equal(128, v));
        Assert.Equal(AnnexA.Expected(im)[1], without[1]);
    }

    /// <summary>
    /// T.800 A.2, Table A.2 (POC 0xFF5F): four precincts in each of three components, the packets written position by position (RPCL) while COD
    /// says LRCP — with a POC of one entry in RPCL the image decodes exactly; without it the packets land in the wrong precincts.
    /// </summary>
    [Fact]
    public void Poc_0xFF5F_ChangesTheOrderOfThePackets()
    {
        var im = new AnnexA.TestImage { Components = [new(Precinct: 2), new(Precinct: 2), new(Precinct: 2)] };
        var byPosition = AnnexA.Packets(im, 0).OrderBy(p => p.Precinct).ThenBy(p => p.Component).ToList();
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(2), AnnexA.Qcd(), AnnexA.Poc((0, 0, 1, 1, 3, 2))], AnnexA.TilePart(0, byPosition))));
        Assert.NotEqual(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(2), AnnexA.Qcd()], AnnexA.TilePart(0, byPosition))));
    }

    /// <summary>
    /// T.800 A.2, Table A.2 (PPM 0xFF60): the packet headers of the tile-part moved into a PPM of the main header (Zppm, Nppm, the headers), the
    /// tile-part with only the code-block data — the same image.
    /// </summary>
    [Fact]
    public void Ppm_0xFF60_PacketHeadersInTheMainHeader()
    {
        var im = new AnnexA.TestImage { TileWidth = 4, TileHeight = 8 };
        var tiles = Enumerable.Range(0, im.Tiles).Select(t => AnnexA.Packets(im, t)).ToList();
        byte[] ippm = [.. tiles.SelectMany(packets => (byte[])[.. AnnexA.Be32(packets.Sum(p => p.Header.Length)), .. packets.SelectMany(p => p.Header)])];
        var ppm = Segment(0xFF60, [0x00, .. ippm]);
        var cs = AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd(), ppm], [.. tiles.Select((packets, t) => AnnexA.TilePart(t, packets, headersElsewhere: true))]);
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(cs));
    }

    /// <summary>
    /// T.800 A.2, Table A.2 (PPT 0xFF61): the packet headers moved into a PPT of the tile-part header — the same image; also with EPH behind each
    /// header inside the PPT (Table A.2: EPH in the bit stream or in PPM/PPT).
    /// </summary>
    [Fact]
    public void Ppt_0xFF61_PacketHeadersInTheTilePartHeader()
    {
        var im = new AnnexA.TestImage();
        var packets = AnnexA.Packets(im, 0);
        var ppt = Segment(0xFF61, [0x00, .. packets.SelectMany(p => p.Header)]);
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], AnnexA.TilePart(0, packets, [ppt], headersElsewhere: true))));
        var withEph = Segment(0xFF61, [0x00, .. packets.SelectMany(p => (byte[])[.. p.Header, 0xFF, 0x92])]);
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(scod: 4), AnnexA.Qcd()], AnnexA.TilePart(0, packets, [withEph], headersElsewhere: true))));
    }

    /// <summary>T.800 A.2, Table A.2 (SOP 0xFF91): an SOP segment before each packet (Scod announces them) — the same image.</summary>
    [Fact]
    public void Sop_0xFF91_BeforeEachPacket_SameImage()
    {
        var im = new AnnexA.TestImage { Components = [new(Precinct: 2), new(), new()] };
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(scod: 2), AnnexA.Coc(0, 2), AnnexA.Qcd()], AnnexA.TilePart(0, AnnexA.Packets(im, 0), sop: true))));
    }

    /// <summary>T.800 A.2, Table A.2 (EPH 0xFF92): an EPH marker behind each packet header (Scod announces them) — the same image.</summary>
    [Fact]
    public void Eph_0xFF92_BehindEachPacketHeader_SameImage()
    {
        var im = new AnnexA.TestImage { Components = [new(Precinct: 2), new(), new()] };
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(scod: 4), AnnexA.Coc(0, 2), AnnexA.Qcd()], AnnexA.TilePart(0, AnnexA.Packets(im, 0), eph: true))));
    }

    // ---- A.3, A.5: SIZ directly behind SOC

    /// <summary>
    /// T.800 A.2 (Table A.2: SIZ required in the main header), A.5 (sentence 2): a codestream without SIZ is refused — with COD directly behind
    /// SOC, and with only a comment before the first tile-part.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithoutSiz_Refused(bool onlyAComment)
    {
        var im = new AnnexA.TestImage();
        var whole = AnnexA.Simple(im);
        var siz = AnnexA.Siz(im);
        byte[] cs = onlyAComment
            ? [0xFF, 0x4F, .. AnnexA.Comment, .. AnnexA.TilePart(0, AnnexA.Packets(im, 0)), 0xFF, 0xD9]
            : [.. whole[..2], .. whole[(2 + siz.Length)..]];
        Assert.Contains("SIZ fehlt", Assert.Throws<JpxException>(() => JpxDecoder.Decode(cs, long.MaxValue)).Message);
    }

    /// <summary>T.800 A.3 (Figure A.3), A.5 (sentence 2): SIZ follows SOC directly — a COD or QCD before SIZ is refused.</summary>
    [Theory]
    [InlineData(0xFF52)]
    [InlineData(0xFF5C)]
    public void FunctionalSegmentBeforeSiz_Refused(int marker)
    {
        var im = new AnnexA.TestImage();
        var segment = marker == 0xFF52 ? AnnexA.Cod() : AnnexA.Qcd();
        byte[] cs = [0xFF, 0x4F, .. segment, .. AnnexA.Simple(im)[2..]];
        Assert.Contains("SIZ fehlt", Assert.Throws<JpxException>(() => JpxDecoder.Decode(cs, long.MaxValue)).Message);
    }

    /// <summary>
    /// T.800 A.3, A.5 (sentence 2), writer: only a codestream with SIZ directly behind SOC is wrapped into JP2. With a comment between SOC and
    /// SIZ it is not — even one whose bytes, read at the fixed places of SIZ, would give the same size, components and depth.
    /// </summary>
    [Fact]
    public void Wrap_SegmentBetweenSocAndSiz_NotWrapped()
    {
        var im = new AnnexA.TestImage();
        var cs = AnnexA.Simple(im);
        Assert.NotNull(PdfAJpeg2000.Wrap(cs));
        var lsiz = BinaryPrimitives.ReadUInt16BigEndian(cs.AsSpan(4));
        byte[] com = [0xFF, 0x64, .. AnnexA.Be16(lsiz), 0x00, 0x01, .. cs[8..(4 + lsiz)]];   // Rcom 1, then the bytes of SIZ behind Rsiz
        Assert.Null(PdfAJpeg2000.Wrap([0xFF, 0x4F, .. com, .. cs[2..]]));
    }

    // ---- A.3: the headers of tile-parts, precedence, order

    /// <summary>
    /// T.800 A.3 (Figures A.4, A.5): COD and QCD belong to the first tile-part header of a tile — in the second one they are passed over (in
    /// the first they would change the image) and reported (read and report); a POC counts in every tile-part header: component 0 in LRCP in
    /// the first tile-part, components 1 and 2 position by position in the second.
    /// </summary>
    [Fact]
    public void SecondTilePartOfATile_CodAndQcdPassedOver_PocCounts()
    {
        var im = new AnnexA.TestImage { Components = [new(Precinct: 2), new(Precinct: 2), new(Precinct: 2)] };
        var packets = AnnexA.Packets(im, 0);
        byte[][] main = [AnnexA.Cod(2), AnnexA.Qcd()];
        byte[][] other = [AnnexA.Cod(), AnnexA.Qcd(10)];
        var passedOver = AnnexA.Decoded(AnnexA.Stream(im, main, AnnexA.TilePart(0, packets.Take(4), null, 0, 2), AnnexA.TilePart(0, packets.Skip(4), other, 1, 2)));
        Assert.Equal(AnnexA.Expected(im), passedOver!.Value.Planes);
        Assert.True(passedOver.Value.Truncated);
        Assert.NotEqual(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, main, AnnexA.TilePart(0, packets.Take(4), other, 0, 2), AnnexA.TilePart(0, packets.Skip(4), null, 1, 2))));
        var rest = packets.Skip(4).OrderBy(p => p.Precinct).ThenBy(p => p.Component);
        var poc = AnnexA.Stream(im, main, AnnexA.TilePart(0, packets.Take(4), [AnnexA.Poc((0, 0, 1, 1, 1, 0))], 0, 2), AnnexA.TilePart(0, rest, [AnnexA.Poc((0, 1, 1, 1, 3, 2))], 1, 2));
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(poc));
    }

    /// <summary>
    /// T.800 A.6.5 (sentence 5): QCC belongs to the first tile-part header of a tile — in the second one it is passed over and reported, as COD
    /// and QCD in <see cref="SecondTilePartOfATile_CodAndQcdPassedOver_PocCounts"/>; in the first one the same QCC (exponent 9 for component 1,
    /// the data coded with QCD) changes the image.
    /// </summary>
    [Fact]
    public void SecondTilePartOfATile_QccPassedOver()
    {
        var im = new AnnexA.TestImage { Components = [new(Precinct: 2), new(Precinct: 2), new(Precinct: 2)] };
        var packets = AnnexA.Packets(im, 0);
        byte[][] main = [AnnexA.Cod(2), AnnexA.Qcd()];
        byte[][] qcc = [AnnexA.Qcc(1, 9)];
        var passedOver = AnnexA.Decoded(AnnexA.Stream(im, main, AnnexA.TilePart(0, packets.Take(4), null, 0, 2), AnnexA.TilePart(0, packets.Skip(4), qcc, 1, 2)));
        Assert.Equal(AnnexA.Expected(im), passedOver!.Value.Planes);
        Assert.True(passedOver.Value.Truncated);
        Assert.NotEqual(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, main, AnnexA.TilePart(0, packets.Take(4), qcc, 0, 2), AnnexA.TilePart(0, packets.Skip(4), null, 1, 2))));
    }

    /// <summary>
    /// T.800 A.3 (sentences 23–28, Figure A.5): COC of the tile before COD of the tile before COC of the main header before COD of the main
    /// header, and the same for QCC and QCD — each pair with the data coded after the one that counts; every component compared.
    /// </summary>
    [Theory]
    [InlineData("CocMainOverCodMain")]
    [InlineData("CodTileOverCocMain")]
    [InlineData("CodTileOverCodMain")]
    [InlineData("CocTileOverCodTile")]
    [InlineData("QccMainOverQcdMain")]
    [InlineData("QcdTileOverQccMain")]
    [InlineData("QcdTileOverQcdMain")]
    [InlineData("QccTileOverQcdTile")]
    public void CodingAndQuantisation_TileBeforeMainComponentBeforeAll(string relation)
    {
        AnnexA.TestComponent[] components;
        byte[][] main, tile;
        switch (relation)
        {
            case "CocMainOverCodMain":
                (components, main, tile) = ([new(), new(Precinct: 2), new()], [AnnexA.Cod(), AnnexA.Coc(1, 2), AnnexA.Qcd()], []);
                break;
            case "CodTileOverCocMain":
                (components, main, tile) = ([new(), new(), new()], [AnnexA.Cod(), AnnexA.Coc(1, 2), AnnexA.Qcd()], [AnnexA.Cod()]);
                break;
            case "CodTileOverCodMain":
                (components, main, tile) = ([new(Precinct: 2), new(Precinct: 2), new(Precinct: 2)], [AnnexA.Cod(), AnnexA.Qcd()], [AnnexA.Cod(2)]);
                break;
            case "CocTileOverCodTile":
                (components, main, tile) = ([new(Precinct: 2), new(Precinct: 2), new()], [AnnexA.Cod(), AnnexA.Qcd()], [AnnexA.Cod(2), AnnexA.Coc(2, 15)]);
                break;
            case "QccMainOverQcdMain":
                (components, main, tile) = ([new(), new(Exponent: 9), new()], [AnnexA.Cod(), AnnexA.Qcd(8), AnnexA.Qcc(1, 9)], []);
                break;
            case "QcdTileOverQccMain":
                (components, main, tile) = ([new(), new(), new()], [AnnexA.Cod(), AnnexA.Qcd(10), AnnexA.Qcc(1, 9)], [AnnexA.Qcd(8)]);
                break;
            case "QcdTileOverQcdMain":
                (components, main, tile) = ([new(), new(), new()], [AnnexA.Cod(), AnnexA.Qcd(10)], [AnnexA.Qcd(8)]);
                break;
            default:
                (components, main, tile) = ([new(), new(), new(Exponent: 9)], [AnnexA.Cod(), AnnexA.Qcd(10)], [AnnexA.Qcd(8), AnnexA.Qcc(2, 9)]);
                break;
        }
        var im = new AnnexA.TestImage { Components = components };
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, main, AnnexA.TilePart(0, AnnexA.Packets(im, 0), tile))));
    }

    /// <summary>
    /// T.800 A.3 (sentence 29): a POC in the main header counts for every tile unless the tile-part header brings its own — the packets written
    /// in RPCL, the main header's POC says LRCP (wrong), the tile's says RPCL: the image decodes exactly; with the main header's POC alone not.
    /// </summary>
    [Fact]
    public void PocOfTheTile_ReplacesPocOfTheMainHeader()
    {
        var im = new AnnexA.TestImage { Components = [new(Precinct: 2), new(Precinct: 2), new(Precinct: 2)] };
        var byPosition = AnnexA.Packets(im, 0).OrderBy(p => p.Precinct).ThenBy(p => p.Component).ToList();
        byte[][] main = [AnnexA.Cod(2), AnnexA.Qcd(), AnnexA.Poc((0, 0, 1, 1, 3, 0))];
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, main, AnnexA.TilePart(0, byPosition, [AnnexA.Poc((0, 0, 1, 1, 3, 2))]))));
        Assert.NotEqual(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, main, AnnexA.TilePart(0, byPosition))));
    }

    /// <summary>
    /// T.800 A.3 (sentence 30) with A.1.3: apart from the delimiting segments and SIZ, the segments of a header stand in any order — QCD before
    /// COD with a comment between them, in the main header and in a tile-part header; the same image.
    /// </summary>
    [Fact]
    public void SegmentsOfAHeader_InAnyOrder_SameImage()
    {
        var im = new AnnexA.TestImage();
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Qcd(), AnnexA.Comment, AnnexA.Cod()], AnnexA.TilePart(0, AnnexA.Packets(im, 0)))));
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(2), AnnexA.Qcd(10)], AnnexA.TilePart(0, AnnexA.Packets(im, 0), [AnnexA.Qcd(), AnnexA.Comment, AnnexA.Cod()]))));
    }

    // ---- A.4: SOC, SOT, SOD, EOC

    /// <summary>
    /// T.800 A.4 (sentences 2, 3): at least one tile-part — EOC directly behind the main header is refused (also when a tile-part follows behind
    /// EOC); a tile-part without SOD marks the image as truncated.
    /// </summary>
    [Fact]
    public void EocBehindTheMainHeader_Refused_TilePartWithoutSod_Truncated()
    {
        var im = new AnnexA.TestImage();
        var tile = AnnexA.TilePart(0, AnnexA.Packets(im, 0));
        byte[] eoc = [0xFF, 0x4F, .. AnnexA.Siz(im), .. AnnexA.Cod(), .. AnnexA.Qcd(), 0xFF, 0xD9, .. tile, 0xFF, 0xD9];
        Assert.Contains("keine Kacheln", Assert.Throws<JpxException>(() => JpxDecoder.Decode(eoc, long.MaxValue)).Message);
        byte[] withoutSod = [0xFF, 0x90, 0x00, 0x0A, 0x00, 0x00, .. AnnexA.Be32(12 + AnnexA.Comment.Length), 0x00, 0x01, .. AnnexA.Comment];
        Assert.False(AnnexA.Truncated(AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], tile)));
        Assert.True(AnnexA.Truncated(AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], withoutSod)));
        Assert.True(AnnexA.Decoded(AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], withoutSod))!.Value.Truncated);
    }

    /// <summary>
    /// T.800 A.4.2, Table A.5 (and sentences 9, 14, 17, 21): Lsot is 10 — 9 and 11 end the tile-parts, the image counts as truncated; Psot 14 is
    /// an empty tile-part (SOT and SOD), read, while 13 cannot hold SOD; a Psot beyond the end of the data marks the image as truncated; TPsot
    /// up to 254 is read.
    /// </summary>
    [Theory]
    [InlineData("Lsot10", false)]
    [InlineData("Lsot9", true)]
    [InlineData("Lsot11", true)]
    [InlineData("Psot14", false)]
    [InlineData("Psot13", true)]
    [InlineData("PsotBeyondTheEnd", true)]
    [InlineData("TPsot254", false)]
    public void SotFields_AsTableA5(string field, bool truncated)
    {
        var im = new AnnexA.TestImage();
        var packets = AnnexA.Packets(im, 0);
        var part = AnnexA.TilePart(0, packets);
        byte[] cs;
        switch (field)
        {
            case "Lsot9" or "Lsot11":
                part[3] = field == "Lsot9" ? (byte)9 : (byte)11;
                cs = AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], part);
                break;
            case "Psot14" or "Psot13":
                var empty = AnnexA.TilePart(0, [], null, 0, 2);
                Assert.Equal(14, BinaryPrimitives.ReadInt32BigEndian(empty.AsSpan(6)));
                if (field == "Psot13") BinaryPrimitives.WriteInt32BigEndian(empty.AsSpan(6), 13);
                cs = AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], empty, AnnexA.TilePart(0, packets, null, 1, 2));
                break;
            case "PsotBeyondTheEnd":
                BinaryPrimitives.WriteInt32BigEndian(part.AsSpan(6), part.Length + 5);
                cs = AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], part);
                break;
            case "TPsot254":
                cs = AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], AnnexA.TilePart(0, packets, null, 254, 0));
                break;
            default:
                cs = AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], part);
                break;
        }
        Assert.Equal(truncated, AnnexA.Truncated(cs));
        if (!truncated) Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(cs));
    }

    /// <summary>
    /// T.800 A.4.2 (sentences 15, 16): Isot counts the tiles in raster order from 0 — four tiles of 4 × 4: tile 1 is the upper right one, tile 2
    /// the lower left; the order of the tile-parts in the data does not matter; tile-parts 1 and 2 with swapped Isot swap those two quarters;
    /// Isot equal to the number of tiles marks the image as truncated.
    /// </summary>
    [Theory]
    [InlineData("InOrder")]
    [InlineData("TilePartsBackwards")]
    [InlineData("OneAndTwoSwapped")]
    [InlineData("IsotEqualToTheCount")]
    public void TileIndex_RasterOrderFromZero(string arrangement)
    {
        var im = new AnnexA.TestImage { TileWidth = 4, TileHeight = 4 };
        var parts = Enumerable.Range(0, 4).Select(t => (Isot: t, Packets: AnnexA.Packets(im, t))).ToList();
        var expected = AnnexA.Expected(im);
        switch (arrangement)
        {
            case "TilePartsBackwards":
                parts.Reverse();
                break;
            case "OneAndTwoSwapped":
                parts[1] = (2, parts[1].Packets);
                parts[2] = (1, parts[2].Packets);
                var swapped = new AnnexA.TestImage
                {
                    TileWidth = 4, TileHeight = 4,
                    Value = (c, x, y) => x >= 4 && y < 4 ? im.Value(c, x - 4, y + 4) : x < 4 && y >= 4 ? im.Value(c, x + 4, y - 4) : im.Value(c, x, y),
                };
                expected = AnnexA.Expected(swapped);
                break;
            case "IsotEqualToTheCount":
                parts[3] = (4, parts[3].Packets);
                break;
        }
        var cs = AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], [.. parts.Select(p => AnnexA.TilePart(p.Isot, p.Packets))]);
        if (arrangement == "IsotEqualToTheCount") Assert.True(AnnexA.Truncated(cs));
        else Assert.Equal(expected, AnnexA.Clean(cs));
    }

    /// <summary>
    /// T.800 A.4.2, Table A.5: Isot up to 65 534 — the last of 65 535 tiles (65 535 × 1, tiles of 1 × 1) is read and lands at the right end;
    /// Isot 65 535 is beyond the tiles and marks the image as truncated.
    /// </summary>
    [Fact]
    public void TileIndex_LastOf65535Tiles_Read()
    {
        var im = new AnnexA.TestImage { X1 = 65535, Y1 = 1, TileWidth = 1, TileHeight = 1, Components = [new()], Value = (_, x, _) => x == 65534 ? 1 : 0 };
        Assert.Equal(65535, im.Tiles);
        var cs = AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], AnnexA.TilePart(65534, AnnexA.Packets(im, 65534)));
        var parsed = JpxCodestream.Parse(cs, 0, cs.Length);
        Assert.False(parsed.Truncated);
        Assert.NotNull(parsed.Tiles[65534]);
        Assert.Equal(129, JpxDecoder.DecodePlanes(cs, long.MaxValue).Planes[0][65534]);
        Assert.True(AnnexA.Truncated(AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], AnnexA.TilePart(65535, AnnexA.Packets(im, 65534)))));
    }

    /// <summary>T.800 A.4.2 (sentences 21–23): TPsot is not needed to put the tile-parts together — 5 in the only tile-part gives the same image.</summary>
    [Fact]
    public void TilePartIndex_WrongValue_SameImage()
    {
        var im = new AnnexA.TestImage();
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], AnnexA.TilePart(0, AnnexA.Packets(im, 0), null, 5, 1))));
    }

    /// <summary>
    /// T.800 A.4.2 (sentences 25–27), Table A.6: TNsot 0 (not given), 1 (right) and 255 (wrong) in the only tile-part — the same image each time.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    public void NumberOfTileParts_AnyValue_SameImage(int tnsot)
    {
        var im = new AnnexA.TestImage();
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], AnnexA.TilePart(0, AnnexA.Packets(im, 0), null, 0, tnsot))));
    }

    /// <summary>
    /// T.800 A.4.4 (Table A.8: EOC 0xFFD9): EOC ends the codestream — a tile-part of the second tile behind it is not read (before EOC it would
    /// fill the right half).
    /// </summary>
    [Fact]
    public void BytesBehindEoc_NotRead()
    {
        var im = new AnnexA.TestImage { Y1 = 4, TileWidth = 4, TileHeight = 4 };
        var first = AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], AnnexA.TilePart(0, AnnexA.Packets(im, 0)));
        var behind = AnnexA.Decoded([.. first, .. AnnexA.TilePart(1, AnnexA.Packets(im, 1))]);
        var alone = AnnexA.Decoded(first);
        Assert.NotNull(behind);
        Assert.Equal(alone!.Value.Planes, behind.Value.Planes);
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd()], AnnexA.TilePart(0, AnnexA.Packets(im, 0)), AnnexA.TilePart(1, AnnexA.Packets(im, 1)))));
        Assert.NotEqual(AnnexA.Expected(im), alone.Value.Planes);
    }

    // ---- A.5.1: SIZ

    /// <summary>T.800 A.5.1 (sentences 10–13, equation A-1): Lsiz = 38 + 3 · Csiz — one byte less is refused, for one and for three components.</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void SizLength_38Plus3TimesCsiz(int components, bool shorter)
    {
        var im = new AnnexA.TestImage { Components = [.. Enumerable.Repeat(new AnnexA.TestComponent(), components)] };
        var siz = AnnexA.Siz(im);
        Assert.Equal(38 + 3 * components, BinaryPrimitives.ReadUInt16BigEndian(siz.AsSpan(2)));
        if (shorter)
        {
            siz = siz[..^1];
            BinaryPrimitives.WriteUInt16BigEndian(siz.AsSpan(2), (ushort)(siz.Length - 2));
        }
        byte[] cs = [0xFF, 0x4F, .. siz, .. AnnexA.Cod(), .. AnnexA.Qcd(), .. AnnexA.TilePart(0, AnnexA.Packets(im, 0)), 0xFF, 0xD9];
        if (shorter) Assert.Throws<JpxException>(() => JpxDecoder.DecodePlanes(cs, long.MaxValue));
        else Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(cs));
    }

    /// <summary>
    /// T.800 A.5.1 (sentences 15–18): the image area runs from XOsiz to Xsiz and from YOsiz to Ysiz — with offsets 3 and 2 an image of 8 × 5
    /// decodes exactly, and the JP2 wrapper writes that size into its image header.
    /// </summary>
    [Fact]
    public void ImageOffset_SizeIsXsizLessXOsiz()
    {
        var im = new AnnexA.TestImage { X0 = 3, Y0 = 2, X1 = 11, Y1 = 7 };
        var cs = AnnexA.Simple(im);
        var planes = JpxDecoder.DecodePlanes(cs, long.MaxValue);
        Assert.Equal((8, 5), (planes.Width, planes.Height));
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(cs));
        var wrapped = PdfAJpeg2000.Wrap(cs)!;
        var ihdr = wrapped.AsSpan().IndexOf("ihdr"u8);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32BigEndian(wrapped.AsSpan(ihdr + 4)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32BigEndian(wrapped.AsSpan(ihdr + 8)));
    }

    /// <summary>
    /// T.800 A.5.1 (sentences 19–22): tiles of 4 × 4 whose grid starts at (2, 1), the image at (5, 3) up to (13, 10) — nine tiles, the outer ones
    /// cut by the image area; the image decodes exactly, nothing shifted.
    /// </summary>
    [Fact]
    public void TileOffset_SeveralTiles_DecodedInPlace()
    {
        var im = new AnnexA.TestImage { X0 = 5, Y0 = 3, X1 = 13, Y1 = 10, TileX0 = 2, TileY0 = 1, TileWidth = 4, TileHeight = 4 };
        Assert.Equal((3, 3), (im.TilesX, im.TilesY));
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Simple(im)));
    }

    /// <summary>
    /// T.800 A.5.1 (sentences 30–33): XRsiz, YRsiz are the sample distances of a component on the reference grid — a component with 2 next to two
    /// with 1 on an image of 7 × 5 has a plane of ⌈7/2⌉ × ⌈5/2⌉ = 4 × 3, decoded exactly, and in the image each of its samples covers 2 × 2
    /// pixels. XRsiz or YRsiz 0 is refused.
    /// </summary>
    [Fact]
    public void Subsampling_PlaneOfItsOwnSize_StretchedInTheImage()
    {
        var im = new AnnexA.TestImage { X1 = 7, Y1 = 5, Components = [new(), new(), new(Dx: 2, Dy: 2)] };
        var cs = AnnexA.Simple(im);
        var planes = JpxDecoder.DecodePlanes(cs, long.MaxValue);
        Assert.Equal((4, 3), (planes.PlaneWidth[2], planes.PlaneHeight[2]));
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(cs));
        var image = JpxDecoder.Decode(cs, long.MaxValue);
        Assert.Equal((7, 5, 3), (image.Width, image.Height, image.Channels));
        for (var y = 0; y < 5; y++)
        for (var x = 0; x < 7; x++) Assert.Equal(128 + im.Value(2, x / 2, y / 2), image.Samples[(y * 7 + x) * 3 + 2]);
        foreach (var (dx, dy) in new[] { (0, 1), (1, 0) })
        {
            var zero = new AnnexA.TestImage { Components = [new(), new(), new(Dx: dx, Dy: dy)] };
            byte[] refused = [0xFF, 0x4F, .. AnnexA.Siz(zero), .. AnnexA.Simple(im)[(2 + AnnexA.Siz(im).Length)..]];
            Assert.Throws<JpxException>(() => JpxDecoder.DecodePlanes(refused, long.MaxValue));
        }
    }

    /// <summary>
    /// T.800 A.5.1, Table A.9: the ranges of the fields of SIZ — Csiz 1 to 16 384 (0 and 16 385 refused), XRsiz and YRsiz 1 to 255 (255 read and
    /// decoded: a plane of one sample), Xsiz above XOsiz and Ysiz above YOsiz (equal: refused), XTsiz and YTsiz from 1 (0 refused). Lsiz 41 for
    /// one component: <see cref="SizLength_38Plus3TimesCsiz"/>.
    /// </summary>
    [Theory]
    [InlineData("Csiz0", false)]
    [InlineData("Csiz16384", true)]
    [InlineData("Csiz16385", false)]
    [InlineData("Rsiz255", true)]
    [InlineData("XsizEqualXOsiz", false)]
    [InlineData("YsizEqualYOsiz", false)]
    [InlineData("XTsiz0", false)]
    [InlineData("YTsiz0", false)]
    public void SizValues_AsTableA9(string value, bool read)
    {
        var im = new AnnexA.TestImage();
        switch (value)
        {
            case "Csiz0":
                im.Components = [];
                break;
            case "Csiz16384" or "Csiz16385":
                im.Components = [.. Enumerable.Repeat(new AnnexA.TestComponent(), value == "Csiz16384" ? 16384 : 16385)];
                break;
            case "Rsiz255":
                im.Components = [new(Dx: 255, Dy: 255)];
                break;
            case "XsizEqualXOsiz":
                im.X0 = 8;
                break;
            case "YsizEqualYOsiz":
                im.Y0 = 8;
                break;
            case "XTsiz0":
                im.TileWidth = 0;
                break;
            default:
                im.TileHeight = 0;
                break;
        }
        // the header alone where the builder cannot code the image (no components, no area, no tiles)
        byte[] header = [0xFF, 0x4F, .. AnnexA.Siz(im), .. AnnexA.Cod(), .. AnnexA.Qcd(), .. AnnexA.TilePart(0, []), 0xFF, 0xD9];
        if (!read)
        {
            Assert.Throws<JpxException>(() => JpxCodestream.Parse(header, 0, header.Length));
            return;
        }
        var parsed = JpxCodestream.Parse(header, 0, header.Length);
        Assert.Equal(im.Components.Length, parsed.Components.Length);
        if (value == "Rsiz255")
        {
            Assert.Equal((255, 255), (parsed.Components[0].Dx, parsed.Components[0].Dy));
            var cs = AnnexA.Simple(im);
            Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(cs));
            Assert.Single(AnnexA.Clean(cs)![0]);
        }
    }

    /// <summary>
    /// T.800 A.5.1, Table A.10: the profiles of Part 1 (Rsiz 1 profile 0, 2 profile 1, 0x0307 broadcast reversible main level 7, 0x0FFF with the
    /// profile in PRF behind SIZ) only restrict the codestream — the image is the same as with Rsiz 0 (and without PRF).
    /// </summary>
    [Theory]
    [InlineData(0x0001)]
    [InlineData(0x0002)]
    [InlineData(0x0307)]
    [InlineData(0x0FFF)]
    public void ProfileInRsiz_SameImageAsRsiz0(int rsiz)
    {
        var im = new AnnexA.TestImage();
        var plain = AnnexA.Clean(AnnexA.Simple(im));
        Assert.Equal(AnnexA.Expected(im), plain);
        im.Rsiz = rsiz;
        byte[][] prf = rsiz == 0x0FFF ? [Segment(0xFF56, 0x00, 0x01)] : [];
        Assert.Equal(plain, AnnexA.Clean(AnnexA.Stream(im, [.. prf, AnnexA.Cod(), AnnexA.Qcd()], AnnexA.TilePart(0, AnnexA.Packets(im, 0)))));
    }

    /// <summary>
    /// T.800 A.5.1, Table A.10, writer: the codestream feature in the Reader Requirements box of a wrapped CMYK codestream follows Rsiz — none
    /// beyond CMYK for profile 0 (1), 4 for profile 1 (2), 5 for unrestricted Part 1 and every other Part 1 profile (0, 3–7, broadcast 0x0101,
    /// 0x0203, 0x0306, 0x0307, IMF 0x0401, 0x0517, 0x0612, 0x0701, 0x0801, 0x0901, 0x0FFF with PRF), 6 for Part 2 (bit 15, also with bit 14);
    /// bit 14 alone (ISO/IEC 15444-15) is not wrapped.
    /// </summary>
    [Theory]
    [InlineData(0x0000, 5)]
    [InlineData(0x0001, 0)]
    [InlineData(0x0002, 4)]
    [InlineData(0x0003, 5)]
    [InlineData(0x0004, 5)]
    [InlineData(0x0005, 5)]
    [InlineData(0x0006, 5)]
    [InlineData(0x0007, 5)]
    [InlineData(0x0101, 5)]
    [InlineData(0x0203, 5)]
    [InlineData(0x0306, 5)]
    [InlineData(0x0307, 5)]
    [InlineData(0x0401, 5)]
    [InlineData(0x0517, 5)]
    [InlineData(0x0612, 5)]
    [InlineData(0x0701, 5)]
    [InlineData(0x0801, 5)]
    [InlineData(0x0901, 5)]
    [InlineData(0x0FFF, 5)]
    [InlineData(0x8000, 6)]
    [InlineData(0xC000, 6)]
    [InlineData(0x4000, -1)]
    public void Wrap_RsizGivesTheCodestreamFeature(int rsiz, int feature)
    {
        var cmyk = JpxRobustTests.Codestream(0, 64, 64, 4, 1, 0, 1, 0);
        BinaryPrimitives.WriteUInt16BigEndian(cmyk.AsSpan(6), (ushort)rsiz);
        var wrapped = PdfAJpeg2000.Wrap(cmyk);
        if (feature < 0)
        {
            Assert.Null(wrapped);
            return;
        }
        Assert.NotNull(wrapped);
        var rreq = wrapped.AsSpan().IndexOf("rreq"u8) + 4;
        Assert.Equal(1, wrapped[rreq]);   // mask length 1
        var count = BinaryPrimitives.ReadUInt16BigEndian(wrapped.AsSpan(rreq + 3));
        var features = Enumerable.Range(0, count).Select(i => (int)BinaryPrimitives.ReadUInt16BigEndian(wrapped.AsSpan(rreq + 5 + 3 * i))).ToArray();
        Assert.Equal(feature == 0 ? new[] { 55 } : new[] { 55, feature }, features);
    }

    /// <summary>
    /// T.800 A.5.1, Table A.11: the lower seven bits of Ssiz give the bit depth less 1 — 0x07 eight bits, 0x0F sixteen bits kept without loss
    /// (the DC level 2^15 plus the coefficient, exactly).
    /// </summary>
    [Theory]
    [InlineData(0x07)]
    [InlineData(0x0F)]
    public void ComponentDepth_AsTableA11(int ssiz)
    {
        var im = new AnnexA.TestImage { Components = [new(Ssiz: ssiz, Exponent: ssiz + 1), new(Ssiz: ssiz, Exponent: ssiz + 1), new(Ssiz: ssiz, Exponent: ssiz + 1)] };
        Assert.Equal(AnnexA.Expected(im), AnnexA.Clean(AnnexA.Stream(im, [AnnexA.Cod(), AnnexA.Qcd(ssiz + 1)], AnnexA.TilePart(0, AnnexA.Packets(im, 0)))));
    }

    /// <summary>
    /// T.800 A.5.1, Table A.11 (Ssiz 0x25, the largest depth: 38 bits) with G.1.2: the DC level of an unsigned 38-bit component is 2^37. With
    /// coefficients 0 and ±2^29 (Mb = 30, the highest bit-plane the decoder reads; a truncated plane is reconstructed within it) the samples are
    /// 2^37 and 2^37 ± 2^29 … 2^30, shifted down by 22 bits to the 16 the planes hold: 32 768, and 32 768 ± 128 … 255. Before the decoder
    /// shifted by 2^29 and clipped to 30 bits, the samples came out at 0 to 255 (docs/FEHLERGEDAECHTNIS.md, No. 1046).
    /// </summary>
    [Fact]
    public void ComponentDepth38Bits_DcLevel2To37_ShiftedTo16Bits()
    {
        var deep = new AnnexA.TestImage { Components = [new(Ssiz: 0x25, Exponent: 30, Plane: 29)] };
        var decoded = AnnexA.Decoded(AnnexA.Stream(deep, [AnnexA.Cod(), AnnexA.Qcd(30)], AnnexA.TilePart(0, AnnexA.Packets(deep, 0))));
        Assert.NotNull(decoded);
        var (planes, truncated) = decoded.Value;
        Assert.False(truncated);
        Assert.Equal(64, planes[0].Length);
        for (var i = 0; i < 64; i++)
        {
            var value = deep.Value(0, i % 8, i / 8);
            int sample = planes[0][i], low = value == 0 ? 32768 : value > 0 ? 32768 + 128 : 32768 - 256, high = value == 0 ? 32768 : low + 128;
            Assert.True(sample >= low && sample <= high, $"Wert {i} (Koeffizient {value}): {sample}, erwartet {low} bis {high}");
        }
    }
}
