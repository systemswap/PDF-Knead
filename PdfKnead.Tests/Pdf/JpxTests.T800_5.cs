using PdfKnead.Core.Pdf.Filters.Jbig2;
using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// ITU-T T.800, B.10 to B.12 and Annex C: packet headers, tag trees, Lblock, progression orders,
/// progression order changes and the contexts of the block decoder — each rule with its own test. The codestreams are written here by hand
/// with own values after the pattern of the examples of the standard; the tile is set up and its packets read without decoding the blocks, so
/// every value the packet header carries can be checked on its own.
/// </summary>
public sealed partial class JpxTests
{
    private static class PacketHeaders
    {
        // ---------------------------------------------------------------- codestream by hand

        public static byte[] Seg(int marker, params byte[] body) =>
            [(byte)(marker >> 8), (byte)marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];

        private static byte[] U16(int v) => [(byte)(v >> 8), (byte)v];

        public static byte[] U32(long v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

        /// <summary>SIZ: image and one tile grid starting at 0, 8-bit components with the given subsampling.</summary>
        public static byte[] Siz(int x1, int y1, int x0, int y0, int tileWidth, int tileHeight, params (int Dx, int Dy)[] components)
        {
            var b = new List<byte> { 0, 0 };
            foreach (var v in new long[] { x1, y1, x0, y0, tileWidth, tileHeight, 0, 0 }) b.AddRange(U32(v));
            b.AddRange(U16(components.Length));
            foreach (var (dx, dy) in components) b.AddRange([7, (byte)dx, (byte)dy]);
            return Seg(0xFF51, [.. b]);
        }

        /// <summary>COD: progression order, layers, no component transform, levels, code-blocks 4 × 4, the style, 5-3; precincts if given.</summary>
        public static byte[] Cod(int order, int layers, int levels, byte[]? precincts = null, int style = 0, int scod = 0) =>
            Seg(0xFF52, [(byte)(scod | (precincts is null ? 0 : 1)), (byte)order, .. U16(layers), 0, (byte)levels, 0, 0, (byte)style, 1, .. precincts ?? []]);

        public static byte[] Coc(int component, int levels, byte[]? precincts = null) =>
            Seg(0xFF53, [(byte)component, (byte)(precincts is null ? 0 : 1), (byte)levels, 0, 0, 0, 1, .. precincts ?? []]);

        /// <summary>QCD without quantisation: guard bits and one exponent for every sub-band.</summary>
        public static byte[] Qcd(int guardBits, int exponent, int levels) =>
            Seg(0xFF5C, [(byte)(guardBits << 5), .. Enumerable.Repeat((byte)(exponent << 3), 1 + 3 * levels)]);

        public static byte[] Poc(params (int Rs, int Cs, int Lye, int Re, int Ce, int Order)[] entries) =>
            Seg(0xFF5F, [.. entries.SelectMany(e => new[] { (byte)e.Rs, (byte)e.Cs, (byte)(e.Lye >> 8), (byte)e.Lye, (byte)e.Re, (byte)e.Ce, (byte)e.Order })]);

        public static byte[] TilePart(int tile, int part, byte[] header, byte[] body) =>
            [0xFF, 0x90, 0, 10, .. U16(tile), .. U32(12 + header.Length + 2 + body.Length), (byte)part, 0, .. header, 0xFF, 0x93, .. body];

        public static byte[] Codestream(byte[] main, params byte[][] tileParts) => [0xFF, 0x4F, .. main, .. tileParts.SelectMany(t => t), 0xFF, 0xD9];

        /// <summary>Distinct bytes of a contribution (never 0xFF, so no marker can appear in them).</summary>
        public static byte[] Fill(int seed, int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(1 + (seed * 31 + i) % 250))];

        /// <summary>Packs the bits of a packet header after B.10.1 (a 0 bit in front of the byte after 0xFF), the last byte filled up.</summary>
        public static byte[] Pack(string bits, char fill = '0')
        {
            var bytes = new List<byte>();
            int current = 0, count = 0, limit = 8;
            foreach (var bit in bits)
            {
                if (bit == ' ') continue;
                current = current << 1 | (bit - '0');
                if (++count < limit) continue;
                bytes.Add((byte)current);
                limit = current == 0xFF ? 7 : 8;
                current = 0;
                count = 0;
            }
            if (count > 0)
            {
                while (count++ < limit) current = current << 1 | (fill - '0');
                bytes.Add((byte)current);
            }
            if (bytes.Count > 0 && bytes[^1] == 0xFF) bytes.Add(0);
            return [.. bytes];
        }

        public static JpxBits Bits(string bits) { var data = Pack(bits); return new JpxBits(data, 0, data.Length); }

        // ---------------------------------------------------------------- the tile, set up and its packets read

        public static JpxTile Tile(byte[] codestream, int index = 0)
        {
            var cs = JpxCodestream.Parse(codestream, 0, codestream.Length);
            var tile = new JpxTile(cs, index, cs.Tiles[index]!, 0, new JpxDecodeState(long.MaxValue / 4, long.MaxValue / 4, 1 << 24));
            tile.Setup();
            return tile;
        }

        public static JpxTile Read(byte[] codestream, int index = 0)
        {
            var tile = Tile(codestream, index);
            tile.ReadPackets();
            return tile;
        }

        public static List<(int L, int C, int R, int P)> Order(JpxTile tile) => [.. tile.Packets()];

        public static JpxCodeBlock Block(JpxTile tile, int c, int r, int precinct, int band, int k) =>
            tile.Components[c].Resolutions[r].Precincts[precinct]![band]!.Blocks[k];

        // ---------------------------------------------------------------- the loops of B.12 written from the standard

        public sealed record Comp(int Dx, int Dy, int Levels, (int X, int Y)[] Precincts);

        public sealed record Grid(int Tx0, int Ty0, int Tx1, int Ty1, int Layers, Comp[] Components);

        private static long Ceil(long a, long b) => (a + b - 1) / b;

        /// <summary>The tile-resolution (B-14, B-15): the tile-component (B-12) reduced by 2^(NL − r).</summary>
        private static (long X0, long Y0, long X1, long Y1) Res(Grid g, int c, int r)
        {
            var comp = g.Components[c];
            var s = comp.Levels - r;
            return (Ceil(Ceil(g.Tx0, comp.Dx), 1L << s), Ceil(Ceil(g.Ty0, comp.Dy), 1L << s),
                Ceil(Ceil(g.Tx1, comp.Dx), 1L << s), Ceil(Ceil(g.Ty1, comp.Dy), 1L << s));
        }

        /// <summary>Precincts in a row and in a column (B-16), none when the resolution is empty.</summary>
        private static (int Wide, int High) Precincts(Grid g, int c, int r)
        {
            var (x0, y0, x1, y1) = Res(g, c, r);
            if (x1 <= x0 || y1 <= y0) return (0, 0);
            var (px, py) = g.Components[c].Precincts[r];
            return ((int)(Ceil(x1, 1L << px) - (x0 >> px)), (int)(Ceil(y1, 1L << py) - (y0 >> py)));
        }

        public static int Count(Grid g, int c, int r) { var (w, h) = Precincts(g, c, r); return w * h; }

        /// <summary>The condition of the loops of B.12.1.3 to B.12.1.5: a precinct is met where its corner lies on the reference grid, or at
        /// the tile edge when the tile cuts it.</summary>
        private static bool Hit(Grid g, int c, int r, int x, int y)
        {
            var comp = g.Components[c];
            var s = comp.Levels - r;
            var (px, py) = comp.Precincts[r];
            var (x0, y0, _, _) = Res(g, c, r);
            var hx = x % ((long)comp.Dx << (px + s)) == 0 || (x == g.Tx0 && (x0 << s) % (1L << (px + s)) != 0);
            var hy = y % ((long)comp.Dy << (py + s)) == 0 || (y == g.Ty0 && (y0 << s) % (1L << (py + s)) != 0);
            return hx && hy;
        }

        /// <summary>Equation (B-20): the precinct number from the place on the reference grid.</summary>
        private static int B20(Grid g, int c, int r, int x, int y)
        {
            var comp = g.Components[c];
            var s = comp.Levels - r;
            var (px, py) = comp.Precincts[r];
            var (x0, y0, _, _) = Res(g, c, r);
            var kx = (Ceil(x, (long)comp.Dx << s) >> px) - (x0 >> px);
            var ky = (Ceil(y, (long)comp.Dy << s) >> py) - (y0 >> py);
            return (int)(kx + Precincts(g, c, r).Wide * ky);
        }

        /// <summary>
        /// The packets in the order of the loops of B.12.1, bounded by the progression order volumes of B.12.2 (B-21); within a volume a
        /// precinct goes on with its next layer, no packet comes twice. Without volumes: one over everything in <paramref name="order"/>.
        /// </summary>
        public static List<(int L, int C, int R, int P)> NormOrder(Grid g, int order, params (int Rs, int Cs, int Lye, int Re, int Ce, int Order)[] volumes)
        {
            var nmax = g.Components.Max(c => c.Levels);
            if (volumes.Length == 0) volumes = [(0, 0, g.Layers, nmax + 1, g.Components.Length, order)];
            var list = new List<(int, int, int, int)>();
            var next = new Dictionary<(int C, int R, int P), int>();
            foreach (var v in volumes)
            {
                int lye = Math.Min(v.Lye, g.Layers), re = Math.Min(v.Re, nmax + 1), ce = Math.Min(v.Ce, g.Components.Length);
                bool Has(int c, int r) => r <= g.Components[c].Levels && Count(g, c, r) > 0;
                void One(int l, int c, int r, int k)
                {
                    if (next.GetValueOrDefault((c, r, k)) != l) return;
                    next[(c, r, k)] = l + 1;
                    list.Add((l, c, r, k));
                }
                void AtGrid(int c, int r, int x, int y)
                {
                    if (!Has(c, r) || !Hit(g, c, r, x, y)) return;
                    var k = B20(g, c, r, x, y);
                    Assert.InRange(k, 0, Count(g, c, r) - 1);
                    for (var l = next.GetValueOrDefault((c, r, k)); l < lye; l++)
                    {
                        next[(c, r, k)] = l + 1;
                        list.Add((l, c, r, k));
                    }
                }
                switch (v.Order)
                {
                    case 0:
                        for (var l = 0; l < lye; l++)
                        for (var r = v.Rs; r < re; r++)
                        for (var c = v.Cs; c < ce; c++)
                            if (Has(c, r)) for (var k = 0; k < Count(g, c, r); k++) One(l, c, r, k);
                        break;
                    case 1:
                        for (var r = v.Rs; r < re; r++)
                        for (var l = 0; l < lye; l++)
                        for (var c = v.Cs; c < ce; c++)
                            if (Has(c, r)) for (var k = 0; k < Count(g, c, r); k++) One(l, c, r, k);
                        break;
                    case 2:
                        for (var r = v.Rs; r < re; r++)
                        for (var y = g.Ty0; y < g.Ty1; y++)
                        for (var x = g.Tx0; x < g.Tx1; x++)
                        for (var c = v.Cs; c < ce; c++)
                            AtGrid(c, r, x, y);
                        break;
                    case 3:
                        for (var y = g.Ty0; y < g.Ty1; y++)
                        for (var x = g.Tx0; x < g.Tx1; x++)
                        for (var c = v.Cs; c < ce; c++)
                        for (var r = v.Rs; r < re; r++)
                            AtGrid(c, r, x, y);
                        break;
                    case 4:
                        for (var c = v.Cs; c < ce; c++)
                        for (var y = g.Ty0; y < g.Ty1; y++)
                        for (var x = g.Tx0; x < g.Tx1; x++)
                        for (var r = v.Rs; r < re; r++)
                            AtGrid(c, r, x, y);
                        break;
                }
            }
            return list;
        }

        /// <summary>
        /// Two components on a 16 × 8 tile, one decomposition level, two layers; precincts of 4 × 4 at resolution 0 and 8 × 8 at resolution 1,
        /// so each resolution of each component has two precincts side by side (and one code-block per sub-band in each of them).
        /// </summary>
        public static readonly Grid Small = new(0, 0, 16, 8, 2, [new(1, 1, 1, [(2, 2), (3, 3)]), new(1, 1, 1, [(2, 2), (3, 3)])]);

        public static byte[] SmallMain(int order, params byte[][] more) =>
            [.. Siz(16, 8, 0, 0, 16, 8, (1, 1), (1, 1)), .. Cod(order, 2, 1, [0x22, 0x33]), .. Qcd(2, 9, 1), .. more.SelectMany(m => m)];

        /// <summary>
        /// A tile whose origin is aligned with no precinct (image from x 3, y 1 to 27, 17), two components — the second subsampled by 2 in both
        /// directions — with two and one decomposition levels and own precinct sizes per resolution, two layers.
        /// </summary>
        public static readonly Grid Shifted = new(3, 1, 27, 17, 2,
            [new(1, 1, 2, [(1, 1), (2, 2), (3, 2)]), new(2, 2, 1, [(1, 1), (2, 2)])]);

        public static byte[] ShiftedCodestream(int order) => Codestream(
            [.. Siz(27, 17, 3, 1, 32, 32, (1, 1), (2, 2)), .. Cod(order, 2, 2, [0x11, 0x22, 0x23]), .. Coc(1, 1, [0x11, 0x22]), .. Qcd(2, 9, 2)],
            TilePart(0, 0, [], []));

        /// <summary>
        /// A second tile whose origin (5, 3) is aligned with no precinct: component 0 with two levels and precincts of 2 × 2 throughout,
        /// component 1 (subsampled by 2) with one level and precincts of 2 × 2 and 4 × 2. Here a precinct cut by the tile edge must be met at the
        /// edge, not at its own corner outside the tile — otherwise the order of the components changes in all three position orders.
        /// </summary>
        public static readonly Grid Edge = new(5, 3, 29, 19, 2,
            [new(1, 1, 2, [(1, 1), (1, 1), (1, 1)]), new(2, 2, 1, [(1, 1), (2, 1)])]);

        public static byte[] EdgeCodestream(int order) => Codestream(
            [.. Siz(29, 19, 5, 3, 32, 32, (1, 1), (2, 2)), .. Cod(order, 2, 2, [0x11, 0x11, 0x11]), .. Coc(1, 1, [0x11, 0x12]), .. Qcd(2, 9, 2)],
            TilePart(0, 0, [], []));

        // ---------------------------------------------------------------- the example of Table B.5 with own values

        /// <summary>
        /// One precinct of 3 × 2 code-blocks (a 12 × 8 image without decomposition, code-blocks of 4 × 4), two layers — what the encoder knows
        /// (the pattern of Figure B.13, own values): first layer 0, 0, 1 / 2, 1, 0; missing bit-planes 2, 5, 1 / 4, 3, 6. Layer 0: CB(0,0)
        /// 4 passes, 7 bytes; CB(1,0) 1 pass, 2 bytes, its Lblock raised by 2 although not needed; CB(2,1) 2 passes, 4 bytes. Layer 1: CB(0,0)
        /// 2 passes, 3 bytes; CB(2,0) 6 passes, 40 bytes; CB(1,1) 3 passes, 5 bytes. The bits worked out by hand from B.10 (the encoder's side).
        /// </summary>
        public const string LayerZero =
            "1" +              // packet not empty
            "111" +            // CB(0,0) included for the first time: the inclusion tree from its top node, value 0
            "01 01 1" +        // CB(0,0) zero bit-planes: top node 1, the node of 2 × 2 below it 2, the leaf 2
            "1101" +           // CB(0,0) 4 coding passes (Table B.4)
            "0" +              // CB(0,0) Lblock stays 3
            "00111" +          // CB(0,0) 7 bytes in 3 + ⌊log2 4⌋ = 5 bits
            "1" +              // CB(1,0) included: only the leaf is still asked
            "0001" +           // CB(1,0) zero bit-planes 5 (the nodes above are known: 2)
            "0" +              // CB(1,0) 1 coding pass
            "110" +            // CB(1,0) Lblock raised by 2 to 5
            "00010" +          // CB(1,0) 2 bytes in 5 + 0 bits (not the fewest bits)
            "10" +             // CB(2,0) not yet: its node of the second level is 0, the leaf is above 0
            "0" +              // CB(0,1) not yet: the leaf is above 0
            "0" +              // CB(1,1) not yet
            "1" +              // CB(2,1) included
            "1000001" +        // CB(2,1) zero bit-planes 6: node of the second level 1, leaf 6
            "10" +             // CB(2,1) 2 coding passes
            "0" +              // CB(2,1) Lblock stays 3
            "0100";            // CB(2,1) 4 bytes in 3 + 1 bits

        public const string LayerOne =
            "1" +              // packet not empty
            "1" +              // CB(0,0) included before: one bit, 1 = included
            "10" +             // CB(0,0) 2 coding passes
            "0" +              // CB(0,0) Lblock stays 3
            "0011" +           // CB(0,0) 3 bytes in 3 + 1 bits
            "0" +              // CB(1,0) included before, not in this layer
            "1" +              // CB(2,0) included for the first time (the tree finishes its leaf at 1)
            "1" +              // CB(2,0) zero bit-planes 1
            "111100000" +      // CB(2,0) 6 coding passes
            "10" +             // CB(2,0) Lblock raised by 1 to 4
            "101000" +         // CB(2,0) 40 bytes in 4 + 2 bits
            "0" +              // CB(0,1) not yet (its first layer is 2)
            "1" +              // CB(1,1) included for the first time
            "01" +             // CB(1,1) zero bit-planes 3
            "1100" +           // CB(1,1) 3 coding passes
            "0" +              // CB(1,1) Lblock stays 3
            "0101" +           // CB(1,1) 5 bytes in 3 + 1 bits
            "0";               // CB(2,1) included before, not in this layer

        public static readonly byte[] DataZero = [.. Fill(1, 7), .. Fill(2, 2), .. Fill(3, 4)];
        public static readonly byte[] DataOne = [.. Fill(4, 3), .. Fill(5, 40), .. Fill(6, 5)];

        public static byte[] TableB5Main(int layers, int scod = 0, params byte[][] more) =>
            [.. Siz(12, 8, 0, 0, 12, 8, (1, 1)), .. Cod(0, layers, 0, scod: scod), .. Qcd(2, 9, 0), .. more.SelectMany(m => m)];

        public static byte[] TableB5Like(int layers, char fill = '0')
        {
            byte[] body = [.. Pack(LayerZero, fill), .. DataZero];
            if (layers > 1) body = [.. body, .. Pack(LayerOne, fill), .. DataOne];
            return Codestream(TableB5Main(layers), TilePart(0, 0, [], body));
        }

        /// <summary>The code-blocks of the precinct of <see cref="TableB5Like"/> in raster order.</summary>
        public static JpxCodeBlock[] Blocks(JpxTile tile) => tile.Components[0].Resolutions[0].Precincts[0]![0]!.Blocks;

        /// <summary>Own values after the pattern of note 1 of B.10.7.1: two code-blocks side by side; CB(0) in four layers with 2, 7, 1, 12 passes
        /// and 5, 50, 9, 300 bytes, CB(1) only in layer 0 with 1 pass and 6 bytes. Only the first <paramref name="layers"/> packets.</summary>
        public static JpxTile NoteOneLike(int layers)
        {
            string[] headers =
            [
                "1" + "11" + "011" + "10" + "0" + "0101" + "1" + "01" + "0" + "0" + "110",   // CB(0): 2 passes, Lblock 3, 5 in 3 + 1 bits; CB(1): 6 in 3 bits
                "1" + "1" + "111100001" + "10" + "110010" + "0",                              // CB(0): 7 passes, Lblock + 1 = 4, 50 in 4 + 2 bits
                "1" + "1" + "0" + "0" + "1001" + "0",                                         // CB(0): 1 pass, Lblock stays 4, 9 in 4 + 0 bits
                "1" + "1" + "111100110" + "110" + "100101100" + "0",                          // CB(0): 12 passes, Lblock + 2 = 6, 300 in 6 + 3 bits
            ];
            int[] lengths = [5, 50, 9, 300];
            var body = new List<byte>();
            for (var l = 0; l < layers; l++)
            {
                body.AddRange(PacketHeaders.Pack(headers[l]));
                body.AddRange(PacketHeaders.Fill(10 + l, lengths[l]));
                if (l == 0) body.AddRange(PacketHeaders.Fill(20, 6));
            }
            return PacketHeaders.Read(PacketHeaders.Codestream([.. PacketHeaders.Siz(8, 4, 0, 0, 8, 4, (1, 1)), .. PacketHeaders.Cod(0, layers, 0), .. PacketHeaders.Qcd(2, 9, 0)],
                PacketHeaders.TilePart(0, 0, [], [.. body])));
        }

        // ---------------------------------------------------------------- tier 1 of a code-block one row high, for the context tests

        /// <summary>
        /// Encodes the coefficients of a code-block one row high after Annex D (LL; no run-length mode below four rows; the only neighbours
        /// are west and east): significance propagation, magnitude refinement and cleanup per bit-plane from <paramref name="topPlane"/> down,
        /// with RESET (contexts back to Table D.7 after each pass) and TERMALL (each pass its own codeword segment) as the style says.
        /// </summary>
        public static List<(byte[] Data, int Passes)> EncodeRow(int[] coefficients, int topPlane, int style)
        {
            var n = coefficients.Length;
            var significant = new bool[n];
            var negative = new bool[n];
            var visited = new bool[n];
            var refined = new bool[n];
            var contexts = JpxBlockDecoder.InitialContexts();
            var segments = new List<(byte[], int)>();
            var mq = new MqEncoder();
            var passes = 0;
            int Neighbours(int x) => (x > 0 && significant[x - 1] ? 1 : 0) + (x < n - 1 && significant[x + 1] ? 1 : 0);
            int ZeroContext(int x) => Neighbours(x) switch { 2 => 8, 1 => 5, _ => 0 };   // Table D.1, LL, only horizontal neighbours
            int Bit(int x, int plane) => Math.Abs(coefficients[x]) >> plane & 1;
            int Contribution(int x) => x < 0 || x >= n || !significant[x] ? 0 : negative[x] ? -1 : 1;
            void Significance(int x)
            {
                // Tables D.2 and D.3 with no vertical neighbours: context 9, or 12 with the XOR bit of the horizontal sign
                var h = Math.Clamp(Contribution(x - 1) + Contribution(x + 1), -1, 1);
                var (context, xor) = h == 0 ? (9, 0) : (12, h < 0 ? 1 : 0);
                mq.Encode(contexts, context, (coefficients[x] < 0 ? 1 : 0) ^ xor);
                significant[x] = true;
                negative[x] = coefficients[x] < 0;
            }
            void EndOfPass()
            {
                passes++;
                if ((style & JpxBlockDecoder.ResetContexts) != 0) JpxBlockDecoder.InitialContexts().CopyTo(contexts, 0);
                if ((style & JpxBlockDecoder.TerminateAll) == 0) return;
                segments.Add((mq.Finish(), passes));
                mq = new MqEncoder();
                passes = 0;
            }
            for (var plane = topPlane; plane >= 0; plane--)
            {
                if (plane != topPlane)
                {
                    for (var x = 0; x < n; x++)
                    {
                        if (significant[x] || Neighbours(x) == 0) continue;
                        visited[x] = true;
                        mq.Encode(contexts, ZeroContext(x), Bit(x, plane));
                        if (Bit(x, plane) == 1) Significance(x);
                    }
                    EndOfPass();
                    for (var x = 0; x < n; x++)
                    {
                        if (!significant[x] || visited[x]) continue;
                        mq.Encode(contexts, refined[x] ? 16 : Neighbours(x) > 0 ? 15 : 14, Bit(x, plane));
                        refined[x] = true;
                    }
                    EndOfPass();
                }
                for (var x = 0; x < n; x++)
                {
                    if (significant[x] || visited[x]) continue;
                    mq.Encode(contexts, ZeroContext(x), Bit(x, plane));
                    if (Bit(x, plane) == 1) Significance(x);
                }
                Array.Clear(visited);
                EndOfPass();
            }
            if (passes > 0) segments.Add((mq.Finish(), passes));
            return segments;
        }

        /// <summary>What the block decoder gives for a coefficient decoded down to plane 0: its magnitude twice plus the half bit, signed.</summary>
        public static int Expected(int v) => v == 0 ? 0 : Math.Sign(v) * (2 * Math.Abs(v) + 1);

        public static readonly int[] Row = [0, 13, -2, 0, 7, 1, 0, -9, 3, 0, -6, 0];
    }

    // ==================================================================== B.10.2 tag trees

    /// <summary>
    /// T.800 B.10.2 (sentences 1–7): a tag tree has coarser levels above the grid, each halving it (rounded up) until one node is left, and
    /// each node holds the minimum of the up to four below it. A value 0 at the first leaf is answered by one bit per level: 1 level for 1 × 1,
    /// 3 for 3 × 2, 4 for 6 × 3 and 8 × 8, 5 for 9 × 1. With the own array of <see cref="TagTree_RasterOrder_KnownNodesNotCodedAgain"/>: once
    /// leaf (0,0) is known to be 2, its node of 2 × 2 is fixed at 2 — so leaves (1,0) and (0,1) are not below 2, and asking so reads no bit.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(3, 2, 3)]
    [InlineData(6, 3, 4)]
    [InlineData(8, 8, 4)]
    [InlineData(9, 1, 5)]
    public void TagTree_Levels_HalvedUpToOneNode_EachNodeTheMinimum(int width, int height, int levels)
    {
        var bits = PacketHeaders.Bits(new string('1', levels) + "10110");
        Assert.Equal(0, new JpxTagTree(width, height).Value(bits, 0, 0, 74));
        Assert.Equal(0b10110, bits.Bits(5));

        var tree = new JpxTagTree(6, 3);
        bits = PacketHeaders.Bits("011011" + "10110");
        Assert.Equal(2, tree.Value(bits, 0, 0, 74));
        Assert.False(tree.Decode(bits, 1, 0, 2));
        Assert.False(tree.Decode(bits, 0, 1, 2));
        Assert.Equal(0b10110, bits.Bits(5));
    }

    /// <summary>
    /// T.800 B.10.2 (sentences 8–11): the coding is a series of questions — each node has a current value starting at zero; a 0 bit says the
    /// value is larger (the current value goes up by one), a 1 bit says it is equal. A single node of value 5 is "000001" (six bits), value 0
    /// is "1"; asking whether 5 is below 3 reads "000" and answers no, the rest of the value then follows with "001".
    /// </summary>
    [Fact]
    public void TagTree_ValueAsQuestions_ZeroRaisesOneFixes()
    {
        var bits = PacketHeaders.Bits("000001" + "1" + "000" + "001" + "1101");
        Assert.Equal(5, new JpxTagTree(1, 1).Value(bits, 0, 0, 74));
        Assert.Equal(0, new JpxTagTree(1, 1).Value(bits, 0, 0, 74));
        var tree = new JpxTagTree(1, 1);
        Assert.False(tree.Decode(bits, 0, 0, 3));
        Assert.Equal(5, tree.Value(bits, 0, 0, 74));
        Assert.Equal(0b1101, bits.Bits(4));
    }

    /// <summary>
    /// T.800 B.10.2 (sentences 12–16): the top node is asked first, then the node below it on the way to the leaf; a node only once the one
    /// above is fixed, and asking stops as soon as the answer is known. In a fresh tree of 8 × 8 (four levels), whether leaf (5,6) is below 2
    /// reads "00" at the top node only and answers no; below 4 then reads "1" (top fixed at 2), "01" (second level 3), "1" and "1" (third
    /// level and leaf 3) — five bits, answer yes; the full value then reads nothing more.
    /// </summary>
    [Fact]
    public void TagTree_QuestionsFromTheTop_StopWhenAnswered()
    {
        var tree = new JpxTagTree(8, 8);
        var bits = PacketHeaders.Bits("00" + "1" + "01" + "1" + "1" + "0110");
        Assert.False(tree.Decode(bits, 5, 6, 2));
        Assert.True(tree.Decode(bits, 5, 6, 4));
        Assert.Equal(3, tree.Value(bits, 5, 6, 74));
        Assert.Equal(0b0110, bits.Bits(4));
    }

    /// <summary>
    /// T.800 B.10.2 (sentences 17–19, after the pattern of the note to Figure B.12 with an own array of 6 × 3): the leaves are coded in raster
    /// order, and what earlier leaves fixed is not coded again — leaf by leaf, exactly the bits worked out on the encoder's side are read (a
    /// sentinel behind them is read next); a second question of a known leaf reads no bit.
    /// </summary>
    [Fact]
    public void TagTree_RasterOrder_KnownNodesNotCodedAgain()
    {
        int[] values = [2, 2, 3, 1, 4, 4, 3, 2, 3, 2, 5, 6, 4, 4, 2, 3, 1, 3];
        string[] codes = ["011011", "1", "1001", "1", "100011", "1", "01", "1", "001", "01", "01", "001", "00011", "1", "011", "01", "11", "001"];
        for (var last = 0; last < values.Length; last++)
        {
            var tree = new JpxTagTree(6, 3);
            var bits = PacketHeaders.Bits(string.Concat(codes.Take(last + 1)) + "10110");
            for (var k = 0; k <= last; k++) Assert.Equal(values[k], tree.Value(bits, k % 6, k / 6, 74));
            Assert.Equal(values[last], tree.Value(bits, last % 6, last / 6, 74));
            Assert.Equal(0b10110, bits.Bits(5));
        }
    }

    // ==================================================================== B.10.1 bit stuffing, end of the header

    /// <summary>
    /// T.800 B.10.1 (sentence 4): at the end of a header the last byte is filled up to the byte boundary — the next header begins with the
    /// next byte, whatever the fill bits are: after one bit and the alignment the position is 1, after two more 2; after a byte 0xFF the
    /// stuffed byte belongs to the header and is passed over. The packet headers of <see cref="PacketHeaders.TableB5Like"/> filled with ones instead of
    /// zeros give the same code-blocks.
    /// </summary>
    [Fact]
    public void HeaderBits_EndOfHeader_PaddedToTheByteBoundary()
    {
        byte[] data = [0b0101_1010, 0b1100_0000, 0xFF, 0x00, 0x80];
        var bits = new JpxBits(data, 0, data.Length);
        Assert.Equal(0, bits.Bit());
        bits.Align();
        Assert.Equal(1, bits.Position);
        Assert.Equal(0b11, bits.Bits(2));
        bits.Align();
        Assert.Equal(2, bits.Position);
        Assert.Equal(0xFF, bits.Bits(8));
        bits.Align();
        Assert.Equal(4, bits.Position);
        Assert.Equal(1, bits.Bit());

        var zeros = PacketHeaders.Read(PacketHeaders.TableB5Like(2));
        var ones = PacketHeaders.Read(PacketHeaders.TableB5Like(2, '1'));
        Assert.False(ones.State.Truncated);
        Assert.Equal(PacketHeaders.Blocks(zeros).Select(b => b.Segments?[0].Join()), PacketHeaders.Blocks(ones).Select(b => b.Segments?[0].Join()));
    }

    // ==================================================================== B.10.3–B.10.8 the packet header, example of Table B.5

    /// <summary>
    /// T.800 B.10.8 (sentences 8–21, Figure B.13 and Table B.5 with own values) and B.10 (sentence 5): both packet headers of a precinct of
    /// 3 × 2 code-blocks give every meaning the encoder put in — inclusion and zero bit-planes from their two tag trees, coding passes, Lblock
    /// and length per code-block — and the bytes of the packet bodies go to the code-blocks in this order; the first header ends after 7
    /// bytes (52 bits), the second after 6 (43 bits).
    /// </summary>
    [Fact]
    public void PacketHeader_TwoLayersOfAPrecinct_EveryMeaningAsTableB5()
    {
        var tile = PacketHeaders.Read(PacketHeaders.TableB5Like(2));
        Assert.False(tile.State.Truncated);
        var blocks = PacketHeaders.Blocks(tile);
        Assert.Equal(6, blocks.Length);
        (bool Included, int Zero, int Passes, int Lblock, byte[] Data)[] expected =
        [
            (true, 2, 6, 3, [.. PacketHeaders.Fill(1, 7), .. PacketHeaders.Fill(4, 3)]),
            (true, 5, 1, 5, PacketHeaders.Fill(2, 2)),
            (true, 1, 6, 4, PacketHeaders.Fill(5, 40)),
            (false, 0, 0, 3, []),
            (true, 3, 3, 3, PacketHeaders.Fill(6, 5)),
            (true, 6, 2, 3, PacketHeaders.Fill(3, 4)),
        ];
        for (var k = 0; k < 6; k++)
        {
            var (included, zero, passes, lblock, data) = expected[k];
            var block = blocks[k];
            Assert.Equal(included, block.Included);
            Assert.Equal(zero, block.ZeroPlanes);
            Assert.Equal(lblock, block.Lblock);
            Assert.Equal(passes, block.Segments?.Sum(s => s.Passes) ?? 0);
            Assert.Equal(data, block.Segments?[0].Join() ?? []);
        }
        Assert.Equal(7, blocks[0].Segments![0].Pieces![0].Start);
        Assert.Equal(7 + 13 + 6, blocks[0].Segments![0].Pieces![1].Start);
    }

    /// <summary>
    /// T.800 B.10.8 (sentences 1–4): the order within a packet header — the bit for an empty packet, then per sub-band (HL, LH, HH above
    /// resolution 0) and per code-block of the precinct: inclusion, zero bit-planes the first time, coding passes, Lblock, lengths. One
    /// decomposition level on 8 × 8 with code-blocks of 4 × 4: the packet of resolution 1 holds one code-block of HL (zero bit-planes 1,
    /// 1 pass, 2 bytes), then of LH (2, 2 passes, 3 bytes), then of HH (3, 3 passes, 4 bytes) — each value goes to the sub-band in this order.
    /// </summary>
    [Fact]
    public void PacketHeader_SubBands_HLBeforeLHBeforeHH()
    {
        const string ll = "1" + "1" + "1" + "0" + "0" + "101";                      // LL: included, zero planes 0, 1 pass, 5 bytes
        const string bands = "1" +
            "1" + "01" + "0" + "0" + "010" +                                          // HL: included, zero planes 1, 1 pass, 2 bytes
            "1" + "001" + "10" + "0" + "0011" +                                       // LH: zero planes 2, 2 passes, 3 bytes
            "1" + "0001" + "1100" + "0" + "0100";                                     // HH: zero planes 3, 3 passes, 4 bytes
        byte[] body = [.. PacketHeaders.Pack(ll), .. PacketHeaders.Fill(1, 5), .. PacketHeaders.Pack(bands), .. PacketHeaders.Fill(2, 2), .. PacketHeaders.Fill(3, 3), .. PacketHeaders.Fill(4, 4)];
        var tile = PacketHeaders.Read(PacketHeaders.Codestream([.. PacketHeaders.Siz(8, 8, 0, 0, 8, 8, (1, 1)), .. PacketHeaders.Cod(0, 1, 1), .. PacketHeaders.Qcd(2, 9, 1)],
            PacketHeaders.TilePart(0, 0, [], body)));
        Assert.False(tile.State.Truncated);
        var res = tile.Components[0].Resolutions[1];
        Assert.Equal([1, 2, 3], res.Bands.Select(b => b.Orientation));
        (int Zero, int Passes, byte[] Data)[] expected = [(1, 1, PacketHeaders.Fill(2, 2)), (2, 2, PacketHeaders.Fill(3, 3)), (3, 3, PacketHeaders.Fill(4, 4))];
        for (var b = 0; b < 3; b++)
        {
            var block = PacketHeaders.Block(tile, 0, 1, 0, b, 0);
            Assert.Equal(expected[b].Zero, block.ZeroPlanes);
            Assert.Equal(expected[b].Passes, block.Segments![0].Passes);
            Assert.Equal(expected[b].Data, block.Segments[0].Join());
        }
        Assert.Equal(PacketHeaders.Fill(1, 5), PacketHeaders.Block(tile, 0, 0, 0, 0, 0).Segments![0].Join());
    }

    /// <summary>
    /// T.800 B.10.4 (sentences 1, 2): a code-block included in an earlier packet takes one bit — 1 included in this layer, 0 not. After
    /// layer 0 of <see cref="PacketHeaders.TableB5Like"/> CB(0,0) and CB(1,0) are included; in layer 1 CB(0,0) is back with "1" (2 more passes,
    /// 3 more bytes) and CB(1,0) is left out with "0" (nothing added).
    /// </summary>
    [Fact]
    public void PacketHeader_BlockIncludedBefore_OneBit()
    {
        var first = PacketHeaders.Blocks(PacketHeaders.Read(PacketHeaders.TableB5Like(1)));
        Assert.True(first[0].Included && first[1].Included);
        Assert.Equal((4, 1), (first[0].Segments![0].Passes, first[1].Segments![0].Passes));
        var both = PacketHeaders.Blocks(PacketHeaders.Read(PacketHeaders.TableB5Like(2)));
        Assert.Equal(6, both[0].Segments![0].Passes);
        Assert.Equal(2, both[0].Segments![0].Pieces!.Count);
        Assert.Equal(1, both[1].Segments![0].Passes);
        Assert.Single(both[1].Segments![0].Pieces!);
    }

    /// <summary>
    /// T.800 B.10.4 (sentences 3–8): a code-block never included before is signalled by the inclusion tree of its precinct and sub-band, whose
    /// values are the first layer of each code-block — only the bits the current layer needs, nothing known twice, partial trees for later
    /// layers. Layer 0 of <see cref="PacketHeaders.TableB5Like"/>: CB(2,0), CB(0,1) and CB(1,1) are left out, CB(2,1) included; layer 1: CB(2,0)
    /// and CB(1,1) come in, CB(0,1) (first layer 2) stays out — every following value lands right only if exactly these bits were read.
    /// </summary>
    [Fact]
    public void PacketHeader_FirstInclusion_PartialInclusionTree()
    {
        var first = PacketHeaders.Blocks(PacketHeaders.Read(PacketHeaders.TableB5Like(1)));
        Assert.Equal([true, true, false, false, false, true], first.Select(b => b.Included));
        var both = PacketHeaders.Blocks(PacketHeaders.Read(PacketHeaders.TableB5Like(2)));
        Assert.Equal([true, true, true, false, true, true], both.Select(b => b.Included));
        Assert.Null(both[3].Segments);
    }

    /// <summary>
    /// T.800 B.10.5 (sentences 1, 2, 4): the first time a code-block is included the header carries the number P of missing most significant
    /// bit-planes, coded with a second tag tree per precinct in the same manner — <see cref="PacketHeaders.TableB5Like"/> gives 2, 5, —, —, —, 6 after
    /// layer 0 and 2, 5, 1, —, 3, 6 after layer 1 (the values of layer 0 stay; CB(0,0) is not asked again).
    /// </summary>
    [Fact]
    public void PacketHeader_ZeroBitPlanes_TagTreeAtFirstInclusion()
    {
        Assert.Equal([2, 5, 0, 0, 0, 6], PacketHeaders.Blocks(PacketHeaders.Read(PacketHeaders.TableB5Like(1))).Select(b => b.ZeroPlanes));
        Assert.Equal([2, 5, 1, 0, 3, 6], PacketHeaders.Blocks(PacketHeaders.Read(PacketHeaders.TableB5Like(2))).Select(b => b.ZeroPlanes));
    }

    /// <summary>
    /// T.800 B.10.5 (sentence 2), equation E-2: the most bit-planes of a sub-band, Mb = G + εb − 1 — 2 guard bits and exponent 8 give 9,
    /// 1 guard bit and exponent 10 give 10, 3 and 5 give 7.
    /// </summary>
    [Theory]
    [InlineData(2, 8, 9)]
    [InlineData(1, 10, 10)]
    [InlineData(3, 5, 7)]
    public void MagnitudeBitPlanes_AsEquationE2_ForTheZeroBitPlanes(int guardBits, int exponent, int magnitudeBits)
    {
        var tile = PacketHeaders.Tile(PacketHeaders.Codestream([.. PacketHeaders.Siz(12, 8, 0, 0, 12, 8, (1, 1)), .. PacketHeaders.Cod(0, 1, 0), .. PacketHeaders.Qcd(guardBits, exponent, 0)],
            PacketHeaders.TilePart(0, 0, [], [])));
        Assert.Equal(magnitudeBits, tile.Components[0].Resolutions[0].Bands[0].MagnitudeBits);
    }

    /// <summary>
    /// T.800 B.10.7 (sentence 1): the header gives the number of bytes of each included code-block, and the body holds them in this order —
    /// layer 0 of <see cref="PacketHeaders.TableB5Like"/> 7, 2 and 4 bytes, layer 1 3, 40 and 5; each code-block gets exactly its own bytes from the
    /// place where the one before ended.
    /// </summary>
    [Fact]
    public void PacketHeader_Lengths_BytesInThisOrder()
    {
        var blocks = PacketHeaders.Blocks(PacketHeaders.Read(PacketHeaders.TableB5Like(2)));
        var pieces = new[] { 0, 1, 5, 4, 2 }.SelectMany(k => blocks[k].Segments![0].Pieces!.Select(p => (Block: k, p.Start, p.Length))).OrderBy(p => p.Start).ToList();
        Assert.Equal([(0, 7, 7), (1, 14, 2), (5, 16, 4), (0, 26, 3), (2, 29, 40), (4, 69, 5)], pieces);
    }

    /// <summary>
    /// T.800 B.10.7.1 (sentence 15): the length need not be given with the fewest bits — in <see cref="PacketHeaders.TableB5Like"/> CB(1,0) raises
    /// Lblock from 3 to 5 for 2 bytes that 3 bits would hold; the length is read from 5 bits, and the following code-blocks still land right.
    /// </summary>
    [Fact]
    public void PacketHeader_LengthWithMoreBitsThanNeeded_Read()
    {
        var blocks = PacketHeaders.Blocks(PacketHeaders.Read(PacketHeaders.TableB5Like(1)));
        Assert.Equal(5, blocks[1].Lblock);
        Assert.Equal(2, blocks[1].Segments![0].Length);
        Assert.Equal(PacketHeaders.Fill(3, 4), blocks[5].Segments![0].Join());
    }

    /// <summary>
    /// T.800 B.10.3 (sentences 1–3): the first bit of a packet header 0 means an empty packet — no code-block takes part. The codestream of
    /// <see cref="PacketHeaders.TableB5Like"/> with one layer, and the same with a second layer whose packet is the single byte 0x00: the same
    /// code-blocks, nothing cut off, no further byte read; a first layer of empty packets before it changes nothing either.
    /// </summary>
    [Fact]
    public void EmptyPacket_ZeroBit_NoCodeBlocks()
    {
        var one = PacketHeaders.Blocks(PacketHeaders.Read(PacketHeaders.TableB5Like(1)));
        byte[] body = [.. PacketHeaders.Pack(PacketHeaders.LayerZero), .. PacketHeaders.DataZero, 0x00];
        var tile = PacketHeaders.Read(PacketHeaders.Codestream(PacketHeaders.TableB5Main(2), PacketHeaders.TilePart(0, 0, [], body)));
        Assert.False(tile.State.Truncated);
        var two = PacketHeaders.Blocks(tile);
        Assert.Equal([true, true, false, false, false, true], two.Select(b => b.Included));
        Assert.Equal(one.Select(b => (b.Included, b.Segments?[0].Passes, b.Segments?[0].Length)), two.Select(b => (b.Included, b.Segments?[0].Passes, b.Segments?[0].Length)));

        // an empty layer 0: nothing included; the inclusion of layer 1 then also answers for layer 0 (see the next test)
        var empty = PacketHeaders.Read(PacketHeaders.Codestream(PacketHeaders.TableB5Main(1), PacketHeaders.TilePart(0, 0, [], [0x00])));
        Assert.All(PacketHeaders.Blocks(empty), b => Assert.False(b.Included));
    }

    /// <summary>
    /// T.800 B.10.3 (sentences 5–7): after empty packets the inclusion of the code-block is coded for the empty layers too — the inclusion
    /// tree holds its first layer (2), so the packet of layer 2 asks "001": two 0 bits for layers 0 and 1, then 1. A single code-block in
    /// three layers, the first two packets empty: included from layer 2 with its zero bit-planes 1, one pass and 5 bytes.
    /// </summary>
    [Fact]
    public void EmptyPackets_InclusionOfTheSkippedLayersCodedAfter()
    {
        const string header = "1" + "001" + "01" + "0" + "0" + "101";
        byte[] body = [0x00, 0x00, .. PacketHeaders.Pack(header), .. PacketHeaders.Fill(7, 5)];
        var tile = PacketHeaders.Read(PacketHeaders.Codestream([.. PacketHeaders.Siz(4, 4, 0, 0, 4, 4, (1, 1)), .. PacketHeaders.Cod(0, 3, 0), .. PacketHeaders.Qcd(2, 9, 0)],
            PacketHeaders.TilePart(0, 0, [], body)));
        Assert.False(tile.State.Truncated);
        var block = PacketHeaders.Block(tile, 0, 0, 0, 0, 0);
        Assert.True(block.Included);
        Assert.Equal(1, block.ZeroPlanes);
        Assert.Equal(PacketHeaders.Fill(7, 5), block.Segments![0].Join());
    }

    // ==================================================================== B.10.7 codeword segments, Lblock

    /// <summary>
    /// T.800 B.10.7 (sentences 6, 7) with Tables D.8 and D.9: a contribution is one codeword segment, or several when the coder terminates
    /// between passes — the passes each segment holds: without termination all in one (874, the most a code-block can have: 3 · (37 + 255) − 2
    /// with the largest ROI shift; before 109, the most without one), with every pass terminated 1 each, with the arithmetic coding bypass 10,
    /// then 2 (raw) and 1 (cleanup) by turns, with both 1 each.
    /// </summary>
    [Theory]
    [InlineData(0, new[] { 874, 874, 874, 874, 874 })]
    [InlineData(JpxBlockDecoder.TerminateAll, new[] { 1, 1, 1, 1, 1 })]
    [InlineData(JpxBlockDecoder.Bypass, new[] { 10, 2, 1, 2, 1, 2 })]
    [InlineData(JpxBlockDecoder.Bypass | JpxBlockDecoder.TerminateAll, new[] { 1, 1, 1, 1 })]
    public void SegmentPasses_ByCodeBlockStyle(int style, int[] expected)
    {
        var segments = new List<JpxSegment>();
        foreach (var _ in expected)
            segments.Add(new JpxSegment { MaxPasses = JpxTile.MaxPasses(style, segments) });
        Assert.Equal(expected, segments.Select(s => s.MaxPasses));
    }

    /// <summary>
    /// T.800 B.10.7.1 (sentences 1–7, own values after the pattern of note 1): one codeword segment — its length is read from
    /// Lblock + ⌊log2 passes⌋ bits (B-19); Lblock belongs to each code-block and starts at 3. CB(0) gets 5, 50, 9 and 300 bytes in its one
    /// segment of 22 passes; CB(1) of the same precinct starts again with Lblock 3 (6 bytes from 3 bits).
    /// </summary>
    [Fact]
    public void SingleSegment_LengthFromLblockAndPasses()
    {
        var tile = PacketHeaders.NoteOneLike(4);
        Assert.False(tile.State.Truncated);
        var first = PacketHeaders.Block(tile, 0, 0, 0, 0, 0);
        var segment = Assert.Single(first.Segments!);
        Assert.Equal(22, segment.Passes);
        Assert.Equal([5, 50, 9, 300], segment.Pieces!.Select(p => p.Length));
        Assert.Equal([.. PacketHeaders.Fill(10, 5), .. PacketHeaders.Fill(11, 50), .. PacketHeaders.Fill(12, 9), .. PacketHeaders.Fill(13, 300)], segment.Join());
        var second = PacketHeaders.Block(tile, 0, 0, 0, 0, 1);
        Assert.Equal(3, second.Lblock);
        Assert.Equal(PacketHeaders.Fill(20, 6), second.Segments![0].Join());
        Assert.Equal(3, new JpxCodeBlock().Lblock);
    }

    /// <summary>
    /// T.800 B.10.7.1 (sentences 8–11): signalling bits before the length raise Lblock — k ones and a zero by k, a zero alone not at all;
    /// Lblock only grows, while the number of length bits can grow or shrink with the passes. After the layers of the own example Lblock is
    /// 3, 4, 4, 6, and the lengths are read from 4, 6, 4 and 9 bits.
    /// </summary>
    [Fact]
    public void SignallingBits_RaiseLblock_LengthBitsMayShrink()
    {
        int[] lblock = [3, 4, 4, 6];
        int[] total = [5, 55, 64, 364];
        for (var layers = 1; layers <= 4; layers++)
        {
            var block = PacketHeaders.Block(PacketHeaders.NoteOneLike(layers), 0, 0, 0, 0, 0);
            Assert.Equal(lblock[layers - 1], block.Lblock);
            Assert.Equal(total[layers - 1], block.Segments![0].Length);
        }
    }

    /// <summary>
    /// T.800 B.10.7.2 (sentences 1–9, 16; own values after the pattern of its note): several codeword segments with the arithmetic coding
    /// bypass — layer 0 brings 8 passes (the first segment of 10 stays open), layer 1 brings 6: they fill the open segment with 2 (11 bytes),
    /// then a raw segment of 2 (90 bytes), a cleanup segment of 1 (3 bytes) and a raw one of which only 1 of 2 passes comes (250 bytes, it
    /// stays open). The lengths follow one another, each from Lblock + ⌊log2 passes⌋ bits, and Lblock is raised once, before all of them
    /// (by 5 to 8).
    /// </summary>
    [Fact]
    public void MultipleSegments_LengthsInTurn_LblockRaisedOnce()
    {
        const string zero = "1" + "1" + "1" + "111100010" + "0" + "010100";              // 8 passes, 20 bytes in 3 + 3 bits
        const string one = "1" + "1" + "111100000" + "111110" +                          // included again, 6 passes, Lblock 3 + 5 = 8
            "000001011" + "001011010" + "00000011" + "11111010";                         // 11 and 90 in 8 + 1 bits, 3 and 250 in 8 + 0 bits
        byte[] body = [.. PacketHeaders.Pack(zero), .. PacketHeaders.Fill(1, 20), .. PacketHeaders.Pack(one), .. PacketHeaders.Fill(2, 11), .. PacketHeaders.Fill(3, 90), .. PacketHeaders.Fill(4, 3), .. PacketHeaders.Fill(5, 250)];
        var tile = PacketHeaders.Read(PacketHeaders.Codestream(
            [.. PacketHeaders.Siz(4, 4, 0, 0, 4, 4, (1, 1)), .. PacketHeaders.Cod(0, 2, 0, style: JpxBlockDecoder.Bypass), .. PacketHeaders.Qcd(2, 9, 0)],
            PacketHeaders.TilePart(0, 0, [], body)));
        Assert.False(tile.State.Truncated);
        var block = PacketHeaders.Block(tile, 0, 0, 0, 0, 0);
        Assert.Equal(8, block.Lblock);
        Assert.Equal([(10, 10), (2, 2), (1, 1), (1, 2)], block.Segments!.Select(s => (s.Passes, s.MaxPasses)));
        Assert.Equal([31, 90, 3, 250], block.Segments!.Select(s => s.Length));
        Assert.Equal([.. PacketHeaders.Fill(1, 20), .. PacketHeaders.Fill(2, 11)], block.Segments![0].Join());
        Assert.Equal(PacketHeaders.Fill(5, 250), block.Segments![3].Join());
    }

    // ==================================================================== B.10 packet headers in PPM or PPT

    /// <summary>
    /// T.800 B.10 (sentences 7, 8, 10): packet headers stand right before their packet data, unless they are moved — with PPM all into the
    /// main header (each tile-part's share after its Nppm), with PPT into the tile-part headers. The two packets of
    /// <see cref="PacketHeaders.TableB5Like"/> with their headers in the body, in a PPT segment (Zppt 0) and in a PPM segment: the body holding only
    /// the data, the same code-blocks each time, nothing cut off.
    /// </summary>
    [Fact]
    public void PacketHeaders_InPptOrPpm_SameAsInTheBody()
    {
        byte[] h0 = PacketHeaders.Pack(PacketHeaders.LayerZero), h1 = PacketHeaders.Pack(PacketHeaders.LayerOne);
        byte[] data = [.. PacketHeaders.DataZero, .. PacketHeaders.DataOne];
        var plain = PacketHeaders.Read(PacketHeaders.TableB5Like(2));
        var ppt = PacketHeaders.Read(PacketHeaders.Codestream(PacketHeaders.TableB5Main(2), PacketHeaders.TilePart(0, 0, PacketHeaders.Seg(0xFF61, [0, .. h0, .. h1]), data)));
        var ppm = PacketHeaders.Read(PacketHeaders.Codestream(PacketHeaders.TableB5Main(2, 0, PacketHeaders.Seg(0xFF60, [0, .. PacketHeaders.U32(h0.Length + h1.Length), .. h0, .. h1])),
            PacketHeaders.TilePart(0, 0, [], data)));
        static object Look(JpxTile t) => string.Join("|", PacketHeaders.Blocks(t).Select(b => $"{b.Included},{b.ZeroPlanes},{b.Lblock},{b.Segments?[0].Passes},{Convert.ToHexString(b.Segments?[0].Join() ?? [])}"));
        Assert.False(ppt.State.Truncated);
        Assert.False(ppm.State.Truncated);
        Assert.Equal(Look(plain), Look(ppt));
        Assert.Equal(Look(plain), Look(ppm));
    }

    // ==================================================================== B.11 tiles and tile-parts

    /// <summary>
    /// T.800 B.11 (sentences 1, 3): a tile is a sequence of packets, and has none for a component no sample of which falls on it. An 8 × 4
    /// image in tiles 2 wide, the second component subsampled by 4 horizontally: on tile 1 (x 2 to 4) it has no column — its packets are
    /// only those of component 0, whose code-block is read whole; tile 0 has packets of both.
    /// </summary>
    [Fact]
    public void ComponentWithoutSamplesInTheTile_NoPackets()
    {
        byte[] main = [.. PacketHeaders.Siz(8, 4, 0, 0, 2, 4, (1, 1), (4, 1)), .. PacketHeaders.Cod(0, 1, 0), .. PacketHeaders.Qcd(2, 9, 0)];
        byte[] body = [.. PacketHeaders.Pack("1" + "1" + "001" + "10" + "0" + "0110"), .. PacketHeaders.Fill(8, 6)];
        var cs = PacketHeaders.Codestream(main, PacketHeaders.TilePart(0, 0, [], [0x00, 0x00]), PacketHeaders.TilePart(1, 0, [], body));
        Assert.Equal([(0, 0, 0, 0), (0, 1, 0, 0)], PacketHeaders.Order(PacketHeaders.Tile(cs, 0)));
        Assert.Equal([(0, 0, 0, 0)], PacketHeaders.Order(PacketHeaders.Tile(cs, 1)));
        var tile = PacketHeaders.Read(cs, 1);
        Assert.False(tile.State.Truncated);
        Assert.Equal(0, tile.Components[1].Resolutions[0].PrecinctCount);
        Assert.Equal(PacketHeaders.Fill(8, 6), PacketHeaders.Block(tile, 0, 0, 0, 0, 0).Segments![0].Join());
    }

    /// <summary>
    /// T.800 B.11 (sentences 12–17, own values after the pattern of the note): each tile-part begins with SOT and the number of its tile;
    /// tile-parts of different tiles may take turns as long as each tile keeps its order. Two tiles of 4 × 4 with two layers, one tile-part
    /// per layer: in the order T0 P0, T1 P0, T0 P1, T1 P1, tile by tile, and with one tile-part per tile — the same code-blocks and the same
    /// image.
    /// </summary>
    [Fact]
    public void InterleavedTileParts_SameAsInOrder()
    {
        byte[] main = [.. PacketHeaders.Siz(8, 4, 0, 0, 4, 4, (1, 1)), .. PacketHeaders.Cod(0, 2, 0), .. PacketHeaders.Qcd(2, 9, 0)];
        byte[][] t0 = [[.. PacketHeaders.Pack("1" + "1" + "0001" + "0" + "0" + "100"), .. PacketHeaders.Fill(1, 4)], [.. PacketHeaders.Pack("1" + "1" + "0" + "0" + "011"), .. PacketHeaders.Fill(2, 3)]];
        byte[][] t1 = [[.. PacketHeaders.Pack("1" + "1" + "001" + "0" + "0" + "101"), .. PacketHeaders.Fill(3, 5)], [.. PacketHeaders.Pack("1" + "1" + "0" + "0" + "010"), .. PacketHeaders.Fill(4, 2)]];
        var whole = PacketHeaders.Codestream(main, PacketHeaders.TilePart(0, 0, [], [.. t0[0], .. t0[1]]), PacketHeaders.TilePart(1, 0, [], [.. t1[0], .. t1[1]]));
        var ordered = PacketHeaders.Codestream(main, PacketHeaders.TilePart(0, 0, [], t0[0]), PacketHeaders.TilePart(0, 1, [], t0[1]), PacketHeaders.TilePart(1, 0, [], t1[0]), PacketHeaders.TilePart(1, 1, [], t1[1]));
        var interleaved = PacketHeaders.Codestream(main, PacketHeaders.TilePart(0, 0, [], t0[0]), PacketHeaders.TilePart(1, 0, [], t1[0]), PacketHeaders.TilePart(0, 1, [], t0[1]), PacketHeaders.TilePart(1, 1, [], t1[1]));
        foreach (var cs in new[] { ordered, interleaved })
        {
            Assert.Equal([.. PacketHeaders.Fill(1, 4), .. PacketHeaders.Fill(2, 3)], PacketHeaders.Block(PacketHeaders.Read(cs, 0), 0, 0, 0, 0, 0).Segments![0].Join());
            Assert.Equal([.. PacketHeaders.Fill(3, 5), .. PacketHeaders.Fill(4, 2)], PacketHeaders.Block(PacketHeaders.Read(cs, 1), 0, 0, 0, 0, 0).Segments![0].Join());
        }
        var image = JpxDecoder.Decode(whole, long.MaxValue);
        Assert.False(image.Truncated);
        Assert.Equal(image.Samples, JpxDecoder.Decode(ordered, long.MaxValue).Samples);
        Assert.Equal(image.Samples, JpxDecoder.Decode(interleaved, long.MaxValue).Samples);
    }

    /// <summary>
    /// T.800 B.11 (sentence 19): Nsop counts every packet, whether an SOP stands before it or not — the reader passes SOP over without
    /// relying on the count. The packets of <see cref="PacketHeaders.TableB5Like"/> with SOP numbered 0 and 1, with SOP numbered 0 and 0, and without
    /// SOP give the same code-blocks.
    /// </summary>
    [Fact]
    public void SopMarkers_NsopNotRelied_SameCodeBlocks()
    {
        byte[] Sop(int n) => [0xFF, 0x91, 0x00, 0x04, 0x00, (byte)n];
        byte[] Body(int a, int b) => [.. Sop(a), .. PacketHeaders.Pack(PacketHeaders.LayerZero), .. PacketHeaders.DataZero, .. Sop(b), .. PacketHeaders.Pack(PacketHeaders.LayerOne), .. PacketHeaders.DataOne];
        static string Look(JpxTile t) => string.Join("|", PacketHeaders.Blocks(t).Select(b => Convert.ToHexString(b.Segments?[0].Join() ?? [])));
        var plain = Look(PacketHeaders.Read(PacketHeaders.TableB5Like(2)));
        foreach (var (a, b) in new[] { (0, 1), (0, 0) })
        {
            var tile = PacketHeaders.Read(PacketHeaders.Codestream(PacketHeaders.TableB5Main(2, 2), PacketHeaders.TilePart(0, 0, [], Body(a, b))));
            Assert.False(tile.State.Truncated);
            Assert.Equal(plain, Look(tile));
        }
    }

    // ==================================================================== B.12 progression order

    /// <summary>
    /// T.800 B.12 (sentences 1–3): a packet holds the data of one layer, component, resolution and precinct, and the order of the packets
    /// runs along these four axes — in each of the five orders every (layer, component, resolution, precinct) of the small tile (2 layers,
    /// 2 components, 2 resolutions, 2 precincts each) comes exactly once: 16 packets.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Packets_EachOnceWithLayerComponentResolutionPrecinct(int order)
    {
        var packets = PacketHeaders.Order(PacketHeaders.Tile(PacketHeaders.Codestream(PacketHeaders.SmallMain(order), PacketHeaders.TilePart(0, 0, [], []))));
        var all = from l in Enumerable.Range(0, 2) from c in Enumerable.Range(0, 2) from r in Enumerable.Range(0, 2) from k in Enumerable.Range(0, 2) select (l, c, r, k);
        Assert.Equal(16, packets.Count);
        Assert.Equal(all.OrderBy(p => p), packets.OrderBy(p => p));
    }

    /// <summary>
    /// T.800 B.12 (sentences 4–6, own values after the pattern of the note): components may have different numbers of resolutions; r = 0 is
    /// the NLLL sub-band of each, and from there the numbers run alike. Component 0 with 4 levels and component 1 with 1 (COC), in RLCP and
    /// in RPCL: for r = 0 and 1 both components take turns, for r = 2 to 4 only component 0 has packets — as the loops of B.12.1 give.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ComponentsWithDifferentLevels_ResolutionsAlignedAtNLLL(int order)
    {
        var grid = new PacketHeaders.Grid(0, 0, 32, 32, 1, [new(1, 1, 4, [(15, 15), (15, 15), (15, 15), (15, 15), (15, 15)]), new(1, 1, 1, [(15, 15), (15, 15)])]);
        var cs = PacketHeaders.Codestream([.. PacketHeaders.Siz(32, 32, 0, 0, 32, 32, (1, 1), (1, 1)), .. PacketHeaders.Cod(order, 1, 4), .. PacketHeaders.Coc(1, 1), .. PacketHeaders.Qcd(2, 9, 4)],
            PacketHeaders.TilePart(0, 0, [], []));
        var packets = PacketHeaders.Order(PacketHeaders.Tile(cs));
        Assert.Equal([(0, 0, 0, 0), (0, 1, 0, 0), (0, 0, 1, 0), (0, 1, 1, 0), (0, 0, 2, 0), (0, 0, 3, 0), (0, 0, 4, 0)], packets);
        Assert.Equal(PacketHeaders.NormOrder(grid, order), packets);
    }

    /// <summary>
    /// T.800 B.12.1 (sentences 1, 2): COD names one of the five progression orders (0 LRCP, 1 RLCP, 2 RPCL, 3 PCRL, 4 CPRL) — the packets
    /// of the small tile come in the order of that loop; a POC in the tile-part header overrides it (here with the order following in the
    /// list). The reserved values from 5 on are a question for the user and not tested here.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ProgressionOrderOfCod_SelectsTheLoop(int order)
    {
        Assert.Equal(PacketHeaders.NormOrder(PacketHeaders.Small, order), PacketHeaders.Order(PacketHeaders.Tile(PacketHeaders.Codestream(PacketHeaders.SmallMain(order), PacketHeaders.TilePart(0, 0, [], [])))));
        var other = (order + 1) % 5;
        var poc = PacketHeaders.Poc((0, 0, 2, 2, 2, other));
        Assert.Equal(PacketHeaders.NormOrder(PacketHeaders.Small, other), PacketHeaders.Order(PacketHeaders.Tile(PacketHeaders.Codestream(PacketHeaders.SmallMain(order), PacketHeaders.TilePart(0, 0, poc, [])))));
    }

    /// <summary>
    /// T.800 B.12.1.1 (sentences 1, 2): LRCP — layer outermost, then resolution up to Nmax, component, precinct innermost: for the small tile
    /// (l, r, c, k) counted up with k fastest and l slowest.
    /// </summary>
    [Fact]
    public void LrcpOrder_LayerResolutionComponentPrecinct()
    {
        var expected = from l in Enumerable.Range(0, 2) from r in Enumerable.Range(0, 2) from c in Enumerable.Range(0, 2) from k in Enumerable.Range(0, 2) select (l, c, r, k);
        Assert.Equal(expected, PacketHeaders.Order(PacketHeaders.Tile(PacketHeaders.Codestream(PacketHeaders.SmallMain(0), PacketHeaders.TilePart(0, 0, [], [])))));
    }

    /// <summary>
    /// T.800 B.12.1.2 (sentence 1): RLCP — resolution outermost, then layer, component, precinct innermost: for the small tile (r, l, c, k)
    /// counted up with k fastest and r slowest.
    /// </summary>
    [Fact]
    public void RlcpOrder_ResolutionLayerComponentPrecinct()
    {
        var expected = from r in Enumerable.Range(0, 2) from l in Enumerable.Range(0, 2) from c in Enumerable.Range(0, 2) from k in Enumerable.Range(0, 2) select (l, c, r, k);
        Assert.Equal(expected, PacketHeaders.Order(PacketHeaders.Tile(PacketHeaders.Codestream(PacketHeaders.SmallMain(1), PacketHeaders.TilePart(0, 0, [], [])))));
    }

    /// <summary>
    /// T.800 B.12.1.3 (sentences 1–23), B.12.1.4 (1, 2), B.12.1.5 (1, 2): RPCL, PCRL and CPRL walk the reference grid of the tile — a precinct
    /// is met where its corner lies, or at the tile edge when the tile cuts it, and its number follows from the place by equation (B-20); then
    /// all its layers. A tile whose origin (3, 1) is aligned with no precinct, two components (the second subsampled by 2) with 2 and 1 levels
    /// and own precinct sizes, and a second tile where a precinct cut by the edge must be met at the edge (<see cref="PacketHeaders.Edge"/>): the
    /// packets come exactly as the loops of the standard, written out in the test, give them.
    /// </summary>
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void PositionOrders_AsTheLoopsOverTheReferenceGrid(int order, bool edge)
    {
        var grid = edge ? PacketHeaders.Edge : PacketHeaders.Shifted;
        var packets = PacketHeaders.Order(PacketHeaders.Tile(edge ? PacketHeaders.EdgeCodestream(order) : PacketHeaders.ShiftedCodestream(order)));
        var expected = PacketHeaders.NormOrder(grid, order);
        Assert.Equal(2 * (PacketHeaders.Count(grid, 0, 0) + PacketHeaders.Count(grid, 0, 1) + PacketHeaders.Count(grid, 0, 2) + PacketHeaders.Count(grid, 1, 0) + PacketHeaders.Count(grid, 1, 1)),
            expected.Count);
        Assert.Equal(expected, packets);
    }

    // ==================================================================== B.12.2 progression order volumes

    /// <summary>
    /// T.800 B.12.2 (sentence 1): the default order stands in the COD of the main header or of the tile — main header RLCP, tile-part header
    /// LRCP: the tile follows LRCP; without the COD of the tile, RLCP.
    /// </summary>
    [Fact]
    public void TileCod_OverridesTheOrderOfTheMainHeader()
    {
        var tileCod = PacketHeaders.Cod(0, 2, 1, [0x22, 0x33]);
        Assert.Equal(PacketHeaders.NormOrder(PacketHeaders.Small, 0), PacketHeaders.Order(PacketHeaders.Tile(PacketHeaders.Codestream(PacketHeaders.SmallMain(1), PacketHeaders.TilePart(0, 0, tileCod, [])))));
        Assert.Equal(PacketHeaders.NormOrder(PacketHeaders.Small, 1), PacketHeaders.Order(PacketHeaders.Tile(PacketHeaders.Codestream(PacketHeaders.SmallMain(1), PacketHeaders.TilePart(0, 0, [], [])))));
    }

    /// <summary>
    /// T.800 B.12.2 (sentences 2–5, 10–12), equation (B-21): a POC bounds the loops — from RSpoc, CSpoc and layer 0 inclusive to REpoc, CEpoc
    /// and LYEpoc exclusive. A first volume RSpoc 1, CSpoc 1, LYEpoc 1, REpoc 2, CEpoc 2 gives only the two packets of layer 0, component 1,
    /// resolution 1; a second over everything gives the rest.
    /// </summary>
    [Fact]
    public void ProgressionVolume_StartInclusiveEndExclusive()
    {
        (int, int, int, int, int, int)[] volumes = [(1, 1, 1, 2, 2, 0), (0, 0, 0xFFFF, 33, 255, 0)];
        var packets = PacketHeaders.Order(PacketHeaders.Tile(PacketHeaders.Codestream(PacketHeaders.SmallMain(1, PacketHeaders.Poc(volumes)), PacketHeaders.TilePart(0, 0, [], []))));
        Assert.Equal([(0, 1, 1, 0), (0, 1, 1, 1)], packets.Take(2));
        Assert.DoesNotContain(packets.Skip(2), p => p == (0, 1, 1, 0) || p == (0, 1, 1, 1));
        Assert.Equal(PacketHeaders.NormOrder(PacketHeaders.Small, 0, volumes), packets);
        Assert.Equal(16, packets.Count);
    }

    /// <summary>
    /// T.800 B.12.2 (sentences 6–9): all packets of a volume come before the next one takes over, none is repeated — each precinct goes on
    /// with its next layer, which the decoder keeps. LRCP with LYEpoc 1 and then over everything gives the order without POC; RPCL the same
    /// way gives all packets of layer 0 and then those of layer 1, each once.
    /// </summary>
    [Fact]
    public void ProgressionVolumes_NoPacketRepeated_NextLayerContinues()
    {
        var lrcp = PacketHeaders.Order(PacketHeaders.Tile(PacketHeaders.Codestream(PacketHeaders.SmallMain(1, PacketHeaders.Poc((0, 0, 1, 33, 255, 0), (0, 0, 0xFFFF, 33, 255, 0))), PacketHeaders.TilePart(0, 0, [], []))));
        Assert.Equal(PacketHeaders.NormOrder(PacketHeaders.Small, 0), lrcp);
        var rpcl = PacketHeaders.Order(PacketHeaders.Tile(PacketHeaders.Codestream(PacketHeaders.SmallMain(1, PacketHeaders.Poc((0, 0, 1, 33, 255, 2), (0, 0, 0xFFFF, 33, 255, 2))), PacketHeaders.TilePart(0, 0, [], []))));
        Assert.Equal(16, rpcl.Distinct().Count());
        Assert.Equal(16, rpcl.Count);
        Assert.All(rpcl.Take(8), p => Assert.Equal(0, p.L));
        Assert.All(rpcl.Skip(8), p => Assert.Equal(1, p.L));
        Assert.Equal(PacketHeaders.NormOrder(PacketHeaders.Small, 0, (0, 0, 1, 33, 255, 2), (0, 0, 0xFFFF, 33, 255, 2)), rpcl);
    }

    /// <summary>
    /// T.800 B.12.2 (sentence 20): one POC segment describes many changes — three volumes in RLCP (resolution 0; resolution 1 of component 0;
    /// resolution 1 of component 1) give the packets in this order; split over two POC segments of the main header they are read in turn
    /// alike.
    /// </summary>
    [Fact]
    public void OnePocSegment_ManyChanges_InTurn()
    {
        (int, int, int, int, int, int)[] volumes = [(0, 0, 2, 1, 2, 1), (1, 0, 2, 2, 1, 1), (1, 1, 2, 2, 2, 1)];
        var expected = PacketHeaders.NormOrder(PacketHeaders.Small, 0, volumes);
        Assert.Equal([(0, 0, 1, 0), (0, 0, 1, 1), (1, 0, 1, 0), (1, 0, 1, 1)], expected.Skip(8).Take(4));
        Assert.Equal(expected, PacketHeaders.Order(PacketHeaders.Tile(PacketHeaders.Codestream(PacketHeaders.SmallMain(0, PacketHeaders.Poc(volumes)), PacketHeaders.TilePart(0, 0, [], [])))));
        Assert.Equal(expected, PacketHeaders.Order(PacketHeaders.Tile(PacketHeaders.Codestream(PacketHeaders.SmallMain(0, PacketHeaders.Poc(volumes[0]), PacketHeaders.Poc(volumes[1], volumes[2])), PacketHeaders.TilePart(0, 0, [], [])))));
    }

    /// <summary>
    /// T.800 B.12.2 (sentences 21, 22, 24): a POC of the main header overrides COD for all tiles without their own POC; a POC of the tile
    /// overrides COD and the main POC. Main POC only resolution 0 (REpoc 1), tile 1 with its own POC over everything in LRCP: tile 1 follows
    /// its own, tile 0 the main one (only the packets of resolution 0).
    /// </summary>
    [Fact]
    public void TilePoc_OverridesCodAndMainPoc()
    {
        byte[] main = [.. PacketHeaders.Siz(32, 8, 0, 0, 16, 8, (1, 1), (1, 1)), .. PacketHeaders.Cod(1, 2, 1, [0x22, 0x33]), .. PacketHeaders.Qcd(2, 9, 1), .. PacketHeaders.Poc((0, 0, 2, 1, 2, 0))];
        var cs = PacketHeaders.Codestream(main, PacketHeaders.TilePart(0, 0, [], []), PacketHeaders.TilePart(1, 0, PacketHeaders.Poc((0, 0, 2, 2, 2, 0)), []));
        Assert.Equal(PacketHeaders.NormOrder(PacketHeaders.Small, 0, (0, 0, 2, 1, 2, 0)), PacketHeaders.Order(PacketHeaders.Tile(cs, 0)));
        Assert.Equal(PacketHeaders.NormOrder(PacketHeaders.Small, 0), PacketHeaders.Order(PacketHeaders.Tile(cs, 1)));
    }

    /// <summary>
    /// T.800 B.12.2 (sentence 26): a volume is never filled without the next being described — the reader ends the packets after the last
    /// volume: a POC with one volume for resolution 0 gives its 8 packets and nothing more, without an error.
    /// </summary>
    [Fact]
    public void LastProgressionVolume_PacketsEndWithIt()
    {
        var packets = PacketHeaders.Order(PacketHeaders.Tile(PacketHeaders.Codestream(PacketHeaders.SmallMain(0, PacketHeaders.Poc((0, 0, 2, 1, 2, 1))), PacketHeaders.TilePart(0, 0, [], []))));
        Assert.Equal(8, packets.Count);
        Assert.All(packets, p => Assert.Equal(0, p.R));
    }

    /// <summary>
    /// T.800 B.12.2 (sentence 31, after the pattern of Figure B.15): many volumes can stand in one tile-part header although they apply only in
    /// later tile-parts. A tile in two tile-parts — the packets of the first volume (LRCP, layer 0) in the first, those of the second (RPCL,
    /// the rest) in the second: with both volumes in the first header, with the second in the second header and with one tile-part, the same
    /// packets and code-blocks (one per precinct of resolution 0, two layers each).
    /// </summary>
    [Fact]
    public void ProgressionVolumesForLaterTileParts_InTheFirstHeader()
    {
        (int, int, int, int, int, int)[] volumes = [(0, 0, 1, 2, 2, 0), (0, 0, 2, 2, 2, 2)];
        var order = PacketHeaders.NormOrder(PacketHeaders.Small, 0, volumes);
        var packets = order.Select((p, i) => p.R != 0 ? [0x00]
            : p.L == 0 ? [.. PacketHeaders.Pack("1" + "1" + "01" + "0" + "0" + "101"), .. PacketHeaders.Fill(i, 5)]
            : (byte[])[.. PacketHeaders.Pack("1" + "1" + "0" + "0" + "011"), .. PacketHeaders.Fill(i, 3)]).ToList();
        var split = order.Count(p => p.L == 0);
        byte[] first = [.. packets.Take(split).SelectMany(p => p)], second = [.. packets.Skip(split).SelectMany(p => p)];
        var one = PacketHeaders.Codestream(PacketHeaders.SmallMain(0), PacketHeaders.TilePart(0, 0, PacketHeaders.Poc(volumes), [.. first, .. second]));
        var both = PacketHeaders.Codestream(PacketHeaders.SmallMain(0), PacketHeaders.TilePart(0, 0, PacketHeaders.Poc(volumes), first), PacketHeaders.TilePart(0, 1, [], second));
        var later = PacketHeaders.Codestream(PacketHeaders.SmallMain(0), PacketHeaders.TilePart(0, 0, PacketHeaders.Poc(volumes[0]), first), PacketHeaders.TilePart(0, 1, PacketHeaders.Poc(volumes[1]), second));
        foreach (var cs in new[] { one, both, later })
        {
            Assert.Equal(order, PacketHeaders.Order(PacketHeaders.Tile(cs)));
            var tile = PacketHeaders.Read(cs);
            Assert.False(tile.State.Truncated);
            for (var c = 0; c < 2; c++)
            for (var k = 0; k < 2; k++)
            {
                int i0 = order.IndexOf((0, c, 0, k)), i1 = order.IndexOf((1, c, 0, k));
                Assert.Equal([.. PacketHeaders.Fill(i0, 5), .. PacketHeaders.Fill(i1, 3)], PacketHeaders.Block(tile, c, 0, k, 0, 0).Segments![0].Join());
            }
        }
    }

    // ==================================================================== C.3 contexts of the block decoder

    /// <summary>
    /// T.800 C.3 (sentences 88, 89): at certain points the states of the contexts go back to Table D.7 — at the start of every code-block,
    /// and with RESET after every pass. A code-block one row high encoded with RESET decodes right only with RESET; and decoded with a
    /// decoder that has just decoded another code-block, it is the same as with a fresh one.
    /// </summary>
    [Fact]
    public void ContextReset_AtEveryCodeBlockAndAfterEveryPassWithReset()
    {
        var row = PacketHeaders.Row;
        var expected = row.Select(PacketHeaders.Expected).ToArray();
        int[] Decode(JpxBlockDecoder decoder, List<(byte[], int)> segments, int style)
        {
            decoder.Decode(row.Length, 1, segments, 0, style, 4);
            return [.. Enumerable.Range(0, row.Length).Select(x => decoder.At(x, 0))];
        }

        var reset = PacketHeaders.EncodeRow(row, 4, JpxBlockDecoder.ResetContexts);
        Assert.Single(reset);
        Assert.Equal(expected, Decode(new JpxBlockDecoder(), reset, JpxBlockDecoder.ResetContexts));

        var plain = PacketHeaders.EncodeRow(row, 4, 0);
        var used = new JpxBlockDecoder();
        Decode(used, PacketHeaders.EncodeRow([5, -11, 0, 2, 0, 0, 14, -1], 4, 0), 0);
        Assert.Equal(expected, Decode(used, plain, 0));
    }

    /// <summary>
    /// T.800 C.3 (sentences 90–93): the decoder keeps the states of the contexts apart from the arithmetic decoder — with every pass terminated
    /// (TERMALL, no RESET) each codeword segment starts a new MQ decoder with INITDEC, but the contexts go on with the states the passes before
    /// left: the code-block one row high, encoded so, decodes to its coefficients, the same as encoded in one segment.
    /// </summary>
    [Fact]
    public void ContextStates_KeptAcrossTerminatedSegments()
    {
        var row = PacketHeaders.Row;
        var segments = PacketHeaders.EncodeRow(row, 4, JpxBlockDecoder.TerminateAll);
        Assert.Equal(13, segments.Count);
        var decoder = new JpxBlockDecoder();
        decoder.Decode(row.Length, 1, segments, 0, JpxBlockDecoder.TerminateAll, 4);
        Assert.Equal(row.Select(PacketHeaders.Expected), Enumerable.Range(0, row.Length).Select(x => decoder.At(x, 0)));
        var single = new JpxBlockDecoder();
        single.Decode(row.Length, 1, PacketHeaders.EncodeRow(row, 4, 0), 0, 0, 4);
        Assert.Equal(Enumerable.Range(0, row.Length).Select(x => single.At(x, 0)), Enumerable.Range(0, row.Length).Select(x => decoder.At(x, 0)));
    }
}
