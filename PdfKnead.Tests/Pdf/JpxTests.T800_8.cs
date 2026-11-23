using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

public sealed partial class JpxTests
{
    /// <summary>
    /// A small JPEG 2000 encoder for tests that need exact coefficients: every tile is one sample (XTsiz = YTsiz = 1), no wavelet levels, so
    /// each tile-component is one code-block of one LL coefficient. The coefficient is coded bit plane by bit plane after ITU-T T.800 Annex D
    /// (cleanup pass on the top plane, then significance, refinement and cleanup per plane — with no neighbours only the zero coding context
    /// 0, the sign context 9 and the refinement contexts 14 and 16 occur), the decisions with the MQ coder of Annex C.2, the packet header
    /// after B.10 (inclusion and zero bit planes as one-node tag trees, the number of passes by Table B.4, Lblock). Written here after the
    /// standard, apart from the decoder.
    /// </summary>
    internal static class AnnexG
    {
        /// <summary>One coded coefficient: its value at the scale of the coded bit planes, the first and last plane coded, and the ROI shift s
        /// the decoder applies to its block (it moves the planes up, so the zero bit planes count from Mb − 1 + s). <paramref name="Scale"/>:
        /// the value stands for Value · 2^Scale (the planes below Scale are 0) — for coefficients of a region shifted beyond 64 bits.</summary>
        internal readonly record struct Coded(long Value, int Top, int Last = 0, int Shift = 0, int Scale = 0)
        {
            /// <summary>All planes of <paramref name="value"/>, scaled up by <paramref name="shift"/> as a region of interest (H.2.1).</summary>
            public static Coded? Full(int value, int shift = 0)
            {
                if (value == 0) return null;
                var scaled = value * (1 << shift);
                return new Coded(scaled, BitLength(Math.Abs(scaled)) - 1, 0, shift);
            }
        }

        /// <summary>What to encode.</summary>
        internal sealed class Image
        {
            public int Width = 1;
            public int Height = 1;
            public (int Depth, bool Signed)[] Components = [(8, false)];

            /// <summary>True: 5-3 filter without quantization; false: 9-7 filter, scalar expounded quantization.</summary>
            public bool Reversible = true;

            /// <summary>The multiple component transformation field of COD (SGcod).</summary>
            public int Mct;

            public int Guard = 2;

            /// <summary>The exponent of the LL band; Mb = Guard + Exponent − 1 (E-2).</summary>
            public int Exponent = 9;

            /// <summary>Marker segments at the end of the main header.</summary>
            public List<byte[]> MainMarkers = [];

            /// <summary>Marker segments of the tile-part header of tile t.</summary>
            public Func<int, byte[]>? TileMarkers;

            /// <summary>The coefficient of tile t, component c (null: none, the packet is empty).</summary>
            public Func<int, int, Coded?> Value = (_, _) => null;

            public int MagnitudeBits => Guard + Exponent - 1;
        }

        internal static int BitLength(int v) => 32 - int.LeadingZeroCount(v);

        private static void U16(List<byte> b, int v) => b.AddRange([(byte)(v >> 8), (byte)v]);

        private static void U32(List<byte> b, int v) => b.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

        /// <summary>An RGN marker segment (A.6.3): component, style Srgn, shift SPrgn.</summary>
        internal static byte[] Rgn(int component, int shift, int style = 0) => [0xFF, 0x5E, 0, 5, (byte)component, (byte)style, (byte)shift];

        /// <summary>The codestream of <paramref name="image"/>.</summary>
        internal static byte[] Codestream(Image image)
        {
            var b = new List<byte> { 0xFF, 0x4F, 0xFF, 0x51 };
            var n = image.Components.Length;
            U16(b, 38 + 3 * n);
            U16(b, 0);
            U32(b, image.Width);
            U32(b, image.Height);
            U32(b, 0);
            U32(b, 0);
            U32(b, 1);
            U32(b, 1);
            U32(b, 0);
            U32(b, 0);
            U16(b, n);
            foreach (var (depth, signed) in image.Components) b.AddRange([(byte)((depth - 1) | (signed ? 0x80 : 0)), 1, 1]);
            // COD: LRCP, one layer, the transformation field; no levels, blocks of 64 × 64, style 0, the filter
            b.AddRange([0xFF, 0x52, 0, 12, 0, 0, 0, 1, (byte)image.Mct, 0, 4, 4, 0, (byte)(image.Reversible ? 1 : 0)]);
            if (image.Reversible) b.AddRange([0xFF, 0x5C, 0, 4, (byte)(image.Guard << 5), (byte)(image.Exponent << 3)]);
            else
            {
                b.AddRange([0xFF, 0x5C, 0, 5, (byte)(image.Guard << 5 | 2)]);
                U16(b, image.Exponent << 11);   // mantissa 0: the step is 2^(depth − exponent) (E-3)
            }
            foreach (var m in image.MainMarkers) b.AddRange(m);
            for (var t = 0; t < image.Width * image.Height; t++)
            {
                var markers = image.TileMarkers?.Invoke(t) ?? [];
                var body = new List<byte>();
                for (var c = 0; c < n; c++) body.AddRange(Packet(image.Value(t, c), image.MagnitudeBits));
                b.AddRange([0xFF, 0x90, 0, 10]);
                U16(b, t);
                U32(b, 12 + markers.Length + 2 + body.Count);
                b.AddRange([0, 1]);
                b.AddRange(markers);
                b.AddRange([0xFF, 0x93]);
                b.AddRange(body);
            }
            b.AddRange([0xFF, 0xD9]);
            return [.. b];
        }

        /// <summary>
        /// A codestream that decodes losslessly to <paramref name="samples"/> (per component, per pixel; unsigned or signed as the component):
        /// 5-3 filter, no transformation, the coefficient of an unsigned sample is the sample less 2^(depth − 1) (G.1.1).
        /// </summary>
        internal static byte[] Lossless(int width, int height, (int Depth, bool Signed)[] components, int[][] samples, int mct = 0) =>
            Codestream(new Image
            {
                Width = width, Height = height, Components = components, Mct = mct,
                Exponent = components.Max(c => c.Depth) + 1,
                Value = (t, c) => Coded.Full(samples[c][t] - (components[c].Signed ? 0 : 1 << (components[c].Depth - 1))),
            });

        /// <summary>The same for 8-bit unsigned components.</summary>
        internal static byte[] Lossless8(int width, int height, params int[][] samples) =>
            Lossless(width, height, [.. samples.Select(_ => (8, false))], samples);

        /// <summary>The packet of one tile-component (B.9, B.10): header and the codeword of its code-block.</summary>
        internal static byte[] Packet(Coded? coded, int mb)
        {
            if (coded is not { } k) return [0];   // an empty packet: the first header bit 0
            var zero = mb - 1 + k.Shift - k.Top;
            if (zero < 0 || k.Last > k.Top || 64 - (int)long.LeadingZeroCount(Math.Abs(k.Value)) + k.Scale > k.Top + 1)
                throw new ArgumentException("coefficient outside its planes");
            var passes = 1 + 3 * (k.Top - k.Last);
            var data = Block(k.Value, k.Top, k.Last, k.Scale);
            var bits = new BitWriter();
            bits.Put(1, 1);                                   // the packet is not empty
            bits.Put(1, 1);                                   // inclusion: tag tree value 0, included in layer 0
            for (var i = 0; i < zero; i++) bits.Put(0, 1);    // zero bit planes: tag tree value as zeros and a one
            bits.Put(1, 1);
            // Table B.4
            if (passes == 1) bits.Put(0, 1);
            else if (passes == 2) bits.Put(0b10, 2);
            else if (passes <= 5) bits.Put(0b1100 | (passes - 3), 4);
            else if (passes <= 36) bits.Put(0b111100000 | (passes - 6), 9);
            else bits.Put(0b1111111110000000 | (passes - 37), 16);
            // Lblock starts at 3; the length takes Lblock + ⌊log2 passes⌋ bits, raised by a one per extra bit (B.10.7.1)
            var floorLog = BitLength(passes) - 1;
            var extra = Math.Max(0, BitLength(Math.Max(1, data.Length)) - 3 - floorLog);
            for (var i = 0; i < extra; i++) bits.Put(1, 1);
            bits.Put(0, 1);
            bits.Put(data.Length, 3 + extra + floorLog);
            return [.. bits.Finish(), .. data];
        }

        /// <summary>The bits of a packet header with the bit stuffing of B.10.1: after 0xFF the next byte carries 7 bits.</summary>
        private sealed class BitWriter
        {
            private readonly List<byte> _bytes = [];
            private int _current;
            private int _count;
            private int _limit = 8;

            public void Put(int value, int count)
            {
                for (var i = count - 1; i >= 0; i--)
                {
                    _current = _current << 1 | (value >> i & 1);
                    if (++_count < _limit) continue;
                    _bytes.Add((byte)_current);
                    _limit = _current == 0xFF ? 7 : 8;
                    _current = 0;
                    _count = 0;
                }
            }

            public List<byte> Finish()
            {
                if (_count > 0)
                {
                    _bytes.Add((byte)(_current << (_limit - _count)));
                    _limit = _bytes[^1] == 0xFF ? 7 : 8;
                }
                // the header never ends with 0xFF: the stuffed zero bit follows in a byte of its own
                if (_bytes.Count > 0 && _bytes[^1] == 0xFF) _bytes.Add(0);
                return _bytes;
            }
        }

        /// <summary>
        /// The code-block of one coefficient (Annex D): on the top plane the cleanup pass codes significance (context 0) and, once significant,
        /// the sign (context 9, XOR bit 0); on each lower plane the significance pass codes nothing (no neighbours), the refinement pass codes the
        /// bit of a coefficient that became significant before (context 14 the first time, then 16), and the cleanup pass codes significance
        /// while the coefficient is not yet significant.
        /// </summary>
        private static byte[] Block(long value, int top, int last, int scale = 0)
        {
            var mq = new MqEncoder();
            var magnitude = Math.Abs(value);
            var significant = false;
            var refined = false;
            for (var plane = top; plane >= last; plane--)
            {
                var bit = plane < scale ? 0 : (int)(magnitude >> (plane - scale) & 1);
                if (plane != top && significant)
                {
                    mq.Encode(bit, refined ? 16 : 14);
                    refined = true;
                }
                if (!significant)
                {
                    mq.Encode(bit, 0);
                    if (bit == 1)
                    {
                        mq.Encode(value < 0 ? 1 : 0, 9);
                        significant = true;
                    }
                }
            }
            return mq.Flush();
        }

        /// <summary>The MQ encoder of T.800 C.2 (Figures C.3 to C.11): registers A, C, CT and the byte B, INITENC, CODEMPS, CODELPS, RENORME,
        /// BYTEOUT with the carry and the stuff bit after 0xFF, FLUSH with SETBITS. Contexts after Table D.7.</summary>
        private sealed class MqEncoder
        {
            private static readonly int[] Qe =
            [
                0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401, 0x1C01, 0x1601, 0x5601, 0x5401,
                0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201, 0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101, 0x0AC1, 0x09C1,
                0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601,
            ];
            private static readonly int[] Nmps =
                [1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46];
            private static readonly int[] Nlps =
                [1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46];

            private readonly int[] _index = new int[19];
            private readonly int[] _mps = new int[19];
            private readonly List<byte> _out = [];
            private long _a = 0x8000, _c;
            private int _ct = 12;
            private int _virtual;   // the byte before the first one (BPST − 1)

            public MqEncoder()
            {
                _index[0] = 4;
                _index[17] = 3;
                _index[18] = 46;
            }

            private int B
            {
                get => _out.Count == 0 ? _virtual : _out[^1];
                set
                {
                    if (_out.Count == 0) _virtual = value;
                    else _out[^1] = (byte)value;
                }
            }

            public void Encode(int d, int cx)
            {
                var qe = Qe[_index[cx]];
                _a -= qe;
                if (d == _mps[cx])
                {
                    if ((_a & 0x8000) != 0)
                    {
                        _c += qe;
                        return;
                    }
                    if (_a < qe) _a = qe;
                    else _c += qe;
                    _index[cx] = Nmps[_index[cx]];
                }
                else
                {
                    if (_a < qe) _c += qe;
                    else _a = qe;
                    if (_index[cx] is 0 or 6 or 14) _mps[cx] = 1 - _mps[cx];
                    _index[cx] = Nlps[_index[cx]];
                }
                do
                {
                    _a <<= 1;
                    _c <<= 1;
                    if (--_ct == 0) ByteOut();
                } while ((_a & 0x8000) == 0);
            }

            private void ByteOut()
            {
                if (B == 0xFF)
                {
                    Stuffed();
                    return;
                }
                if (_c < 0x8000000)
                {
                    Plain();
                    return;
                }
                B++;
                if (B == 0xFF)
                {
                    _c &= 0x7FFFFFF;
                    Stuffed();
                }
                else Plain();

                void Stuffed()
                {
                    _out.Add((byte)(_c >> 20));
                    _c &= 0xFFFFF;
                    _ct = 7;
                }

                void Plain()
                {
                    _out.Add((byte)(_c >> 19));
                    _c &= 0x7FFFF;
                    _ct = 8;
                }
            }

            public byte[] Flush()
            {
                var temp = _c + _a;
                _c |= 0xFFFF;
                if (_c >= temp) _c -= 0x8000;
                _c <<= _ct;
                ByteOut();
                _c <<= _ct;
                ByteOut();
                if (_out.Count > 0 && _out[^1] == 0xFF) _out.RemoveAt(_out.Count - 1);
                return [.. _out];
            }
        }

        /// <summary>The decoded planes (raw values after the level shift, ITU-T T.800 G.1.2), per component, per pixel.</summary>
        internal static ushort[][] Planes(byte[] data) => JpxDecoder.DecodePlanes(data, long.MaxValue).Planes;

        /// <summary>The planes of all components at one pixel.</summary>
        internal static int[] At(ushort[][] planes, int pixel) => [.. planes.Select(p => (int)p[pixel])];
    }

    // ---- ITU-T T.800 Annex G: DC level shift and the component transformations

    /// <summary>
    /// T.800 G.1 (Figure G.2), G.1.2, G.2.2 and G.3.2: the DC level shift comes after the inverse component transformation — three 8-bit
    /// components whose coefficients are all 0, with the RCT (5-3 filter) and with the ICT (9-7 filter), give 128 in every component. Shifted
    /// before the transformation the RCT would give 192, 64, 192 and the ICT 255, 0, 255 (307, −7, 355 clipped). Without a transformation 128
    /// as well.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DcLevelShift_AfterTheInverseComponentTransformation(bool reversible)
    {
        foreach (var mct in new[] { 1, 0 })
        {
            var planes = AnnexG.Planes(AnnexG.Codestream(new AnnexG.Image
            {
                Components = [(8, false), (8, false), (8, false)], Reversible = reversible, Mct = mct, Exponent = reversible ? 9 : 7,
            }));
            Assert.Equal([128, 128, 128], AnnexG.At(planes, 0));
        }
    }

    /// <summary>
    /// T.800 G.2.2 (Equations G-6 to G-8) with G.1.2: the inverse RCT on the reconstructed values Y0, Y1, Y2 — I1 = Y0 − ⌊(Y2 + Y1)/4⌋,
    /// I0 = Y2 + I1, I2 = Y1 + I1, rounded down also for a negative sum — and then the level shift by 128, clipped to 8 bits. Expected values
    /// worked out from the equations.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, 128, 128, 128)]
    [InlineData(10, -20, 30, 166, 136, 116)]
    [InlineData(-50, 7, -9, 70, 79, 86)]
    [InlineData(100, 40, 60, 255, 203, 243)]
    [InlineData(-128, -1, -2, 0, 1, 0)]
    public void InverseRct_ThenTheLevelShift_AsG6ToG8(int y0, int y1, int y2, int r, int g, int b)
    {
        int[] y = [y0, y1, y2];
        var planes = AnnexG.Planes(AnnexG.Codestream(new AnnexG.Image
        {
            Components = [(8, false), (8, false), (8, false)], Mct = 1, Value = (_, c) => AnnexG.Coded.Full(y[c]),
        }));
        Assert.Equal([r, g, b], AnnexG.At(planes, 0));
    }

    /// <summary>
    /// T.800 G.1.2 (Equation G-2): every sample of an unsigned component is raised by 2^(Ssiz) — half its range: 2^(depth − 1) — after the
    /// inverse transformations; a signed component is not shifted. The planes of the decoder hold signed values moved by half their range
    /// for display, so a signed component shows its value plus 2^(depth − 1) once — shifted as well, the value 0 of 8 bits would be 256 and
    /// clip to 255. One coefficient per row, coded without loss.
    /// </summary>
    [Theory]
    [InlineData(8, false, 0, 128)]
    [InlineData(8, false, -100, 28)]
    [InlineData(8, false, 127, 255)]
    [InlineData(4, false, 0, 8)]
    [InlineData(4, false, -8, 0)]
    [InlineData(4, false, 7, 15)]
    [InlineData(12, false, 0, 2048)]
    [InlineData(12, false, -2048, 0)]
    [InlineData(12, false, 2047, 4095)]
    [InlineData(16, false, 0, 32768)]
    [InlineData(16, false, 1000, 33768)]
    [InlineData(1, false, 0, 1)]
    [InlineData(1, false, -1, 0)]
    [InlineData(8, true, 0, 128)]
    [InlineData(8, true, -100, 28)]
    [InlineData(8, true, 127, 255)]
    [InlineData(12, true, 0, 2048)]
    [InlineData(12, true, -2048, 0)]
    public void DcLevelShift_HalfTheRange_UnsignedOnly(int depth, bool signed, int coefficient, int plane)
    {
        var planes = AnnexG.Planes(AnnexG.Codestream(new AnnexG.Image
        {
            Components = [(depth, signed)], Exponent = depth + 1, Value = (_, _) => AnnexG.Coded.Full(coefficient),
        }));
        Assert.Equal(plane, planes[0][0]);
    }

    /// <summary>
    /// T.800 G.1.2 with Table A.11 (depths up to 38 bits): an unsigned component of 31, 32 or 38 bits whose coefficients are all 0 is shifted
    /// by 2^30, 2^31 or 2^37 — in the 16-bit planes of the decoder (the upper 16 bits kept) 32 768 each. Before the decoder capped the shift at
    /// 2^29 and the range at 30 bits: 16 384, 8 192 and 128 (docs/FEHLERGEDAECHTNIS.md, No. 1046).
    /// </summary>
    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(38)]
    public void DcLevelShift_ComponentsAbove30Bits(int depth)
    {
        var planes = AnnexG.Planes(AnnexG.Codestream(new AnnexG.Image { Components = [(depth, false)], Exponent = 2 }));
        Assert.Equal(32768, planes[0][0]);
    }

    /// <summary>
    /// T.800 G.2 (G-6 to G-8) on values near ±2^37 (the samples of 38-bit components): the RCT in double precision is exact — G = Y0 −
    /// ⌊(Y1 + Y2) / 4⌋, R = Y2 + G, B = Y1 + G, worked out by an own reference in exact integers; in single precision every one of the three
    /// samples would come out different (docs/FEHLERGEDAECHTNIS.md, No. 1045).
    /// </summary>
    [Fact]
    public void Rct_ValuesNear2To37_ExactInDoublePrecision()
    {
        double[] y0 = [137438953477, -137438953465, 68719477759];
        double[] y1 = [-68719476733, 34359738369, 68719476737];
        double[] y2 = [68719476735, -34359738370, -68719476733];
        JpxWavelet.InverseColor(y0, y1, y2, 3, reversible: true);
        Assert.Equal([206158430212, -171798691834, 1025], y0);
        Assert.Equal([137438953477, -137438953464, 68719477758], y1);
        Assert.Equal([68719476744, -103079215095, 137438954495], y2);
    }

    /// <summary>
    /// T.800 G.2 with G.1.2 through the whole decoder: three unsigned 26-bit components (Mb 28, so the exact store), coefficients beyond 2^24
    /// (odd, which a float cannot hold), the RCT, the DC level 2^25, the upper 16 bits kept. Pixel 0, Y = (2^24 + 1023, 2^24 + 1, −2^24 − 1):
    /// R = 1022, G = 2^24 + 1023, B = 2^25 + 1024 (clipped) — 32 768, 49 152, 65 535; in single precision 32 769 and 49 153. Pixel 1 from
    /// Y = (−2^24 − 1023, 2^24 + 3, 2^24 + 5): 24 575, 8 190, 24 575. Expected values from an own reference (docs/FEHLERGEDAECHTNIS.md,
    /// No. 1045).
    /// </summary>
    [Fact]
    public void Rct_26BitComponents_ExactThroughTheDecoder()
    {
        int[][] y = [[(1 << 24) + 1023, (1 << 24) + 1, -(1 << 24) - 1], [-(1 << 24) - 1023, (1 << 24) + 3, (1 << 24) + 5]];
        var planes = AnnexG.Planes(AnnexG.Codestream(new AnnexG.Image
        {
            Width = 2, Components = [(26, false), (26, false), (26, false)], Mct = 1, Exponent = 27, Value = (t, c) => AnnexG.Coded.Full(y[t][c]),
        }));
        Assert.Equal([32768, 49152, 65535], AnnexG.At(planes, 0));
        Assert.Equal([24575, 8190, 24575], AnnexG.At(planes, 1));
    }

    /// <summary>
    /// T.800 G.2 when only one of the three components has many bit-planes: component 0 with Mb 28 (exact store), components 1 and 2 with Mb 21
    /// (QCC; single-precision store) and coefficient 0. The RCT runs on all three in double precision: R = G = B = Y0 = 2^24 + 1023 — 49 152
    /// each; a float would hold 2^24 + 1024 and give 49 153 (docs/FEHLERGEDAECHTNIS.md, No. 1045).
    /// </summary>
    [Fact]
    public void Rct_OneComponentWithManyBitPlanes_AllThreeExact()
    {
        var image = new AnnexG.Image
        {
            Components = [(26, false), (26, false), (26, false)], Mct = 1, Exponent = 27,
            MainMarkers = [[0xFF, 0x5D, 0, 5, 1, 2 << 5, 20 << 3], [0xFF, 0x5D, 0, 5, 2, 2 << 5, 20 << 3]],
            Value = (_, c) => c == 0 ? AnnexG.Coded.Full((1 << 24) + 1023) : null,
        };
        var codestream = AnnexG.Codestream(image);
        var tile = AnnexF.DecodeTile(codestream);
        Assert.Equal([true, false, false], tile.Components.Select(tc => tc.Exact is not null));
        Assert.Equal([49152, 49152, 49152], AnnexG.At(AnnexG.Planes(codestream), 0));
    }

    /// <summary>
    /// Where the decoder keeps the single-precision store: reversible with up to 23 magnitude bit-planes (Mb = guard bits 2 + ε − 1), and every
    /// irreversible tile-component — the same memory and time as before for them; from Mb 24 on (reversible) the exact store
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1045).
    /// </summary>
    [Theory]
    [InlineData(true, 22, false)]
    [InlineData(true, 23, true)]
    [InlineData(true, 27, true)]
    [InlineData(false, 30, false)]
    public void ExactStore_OnlyReversibleWithMoreThan23BitPlanes(bool reversible, int exponent, bool exact)
    {
        var tile = AnnexF.DecodeTile(AnnexG.Codestream(new AnnexG.Image { Components = [(16, false)], Reversible = reversible, Exponent = exponent }));
        var tc = Assert.Single(tile.Components);
        Assert.Equal(exact, tc.Exact is not null);
        Assert.Equal(exact ? 0 : 1, tc.Data.Length);
        Assert.Equal(1, tc.Length);
        Assert.Equal(exact ? 8 : 4, tc.Bytes);
    }

    /// <summary>
    /// ISO/IEC 15444-2, J.3.1.1.1 (array-based decorrelation, here the identity with offsets 2^25, A.3.7 with 32-bit integers) on components with
    /// the exact store: three unsigned 26-bit components (Mb 28), coefficients 2^24 + 1023, −2^24 − 1023 and 2^25 − 3, CBD three unsigned 26-bit
    /// outputs. The decorrelation gets the integers unrounded: 49 152, 16 383 and 65 535 in the upper 16 bits; from a float input the first
    /// would be 49 153 (docs/FEHLERGEDAECHTNIS.md, No. 1045).
    /// </summary>
    [Fact]
    public void Part2Decorrelation_ExactStore_IntegersUnrounded()
    {
        int[] y = [(1 << 24) + 1023, -(1 << 24) - 1023, (1 << 25) - 3];
        byte[] offsets = [0x02, 0, 0, 0, 0x02, 0, 0, 0, 0x02, 0, 0, 0];
        var codestream = AnnexG.Codestream(new AnnexG.Image
        {
            Components = [(26, false), (26, false), (26, false)], Mct = 2, Exponent = 27,
            MainMarkers =
            [
                [0xFF, 0x78, 0, 5, 0x80, 3, 0x19],                                               // CBD: three unsigned 26-bit outputs
                [0xFF, 0x74, 0, 20, 0, 0, 0x06, 0x01, 0, 0, .. offsets],                         // MCT: offsets (index 1, 32-bit integers)
                [0xFF, 0x75, 0, 23, 0, 0, 0, 0, 0, 0, 1, 1, 0, 3, 0, 1, 2, 0, 3, 0, 1, 2, 0, 1, 0], // MCC: identity with offsets 1
                [0xFF, 0x77, 0, 4, 1, 0],                                                         // MCO: one stage
            ],
            Value = (_, c) => AnnexG.Coded.Full(y[c]),
        });
        codestream[6] = 0x81;   // Rsiz: the multiple component transformation of ISO/IEC 15444-2
        Assert.Equal([49152, 16383, 65535], AnnexG.At(AnnexG.Planes(codestream), 0));
    }

    /// <summary>
    /// T.800 G.2 (with A.6.1, Table A.17): the RCT is switched on by the transformation field of COD and works on components 0, 1 and 2 only —
    /// a fourth component keeps its value (plus the level shift). Without the field each component is only shifted. Two pixels; expected values
    /// from G-6 to G-8.
    /// </summary>
    [Fact]
    public void Rct_SwitchedOnInCod_TheFirstThreeComponentsOnly()
    {
        int[][] y = [[10, -20, 30, 55], [-50, 7, -9, -77]];
        byte[] Codestream(int mct) => AnnexG.Codestream(new AnnexG.Image
        {
            Width = 2, Components = [(8, false), (8, false), (8, false), (8, false)], Mct = mct, Value = (t, c) => AnnexG.Coded.Full(y[t][c]),
        });
        var with = AnnexG.Planes(Codestream(1));
        Assert.Equal([166, 136, 116, 183], AnnexG.At(with, 0));
        Assert.Equal([70, 79, 86, 51], AnnexG.At(with, 1));
        var without = AnnexG.Planes(Codestream(0));
        Assert.Equal([138, 108, 158, 183], AnnexG.At(without, 0));
        Assert.Equal([78, 135, 119, 51], AnnexG.At(without, 1));
    }

    /// <summary>
    /// T.800 G.2 on a real codestream (40 × 27 pixels, 5-3 filter, five levels, RCT): decoded with the transformation field set to 0, the
    /// components are Y0, Y1, Y2 plus 128; the RCT and the level shift worked out here from those give the image decoded as it is — at every
    /// pixel where none of the three was clipped (most of them).
    /// </summary>
    [TestPdfsFact]
    public void Rct_RealCodestream_AsWorkedOutFromTheUntransformedComponents()
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(PdfJs("jp2k-resetprob.pdf")));
        var (stream, _) = Assert.Single(JpxImages(doc));
        var cs = Bare(Raw(doc, stream));
        var mct = MctField(cs);
        Assert.Equal(1, cs[mct]);
        var withRct = JpxDecoder.DecodePlanes(cs, long.MaxValue).Planes;
        var without = (byte[])cs.Clone();
        without[mct] = 0;
        var plain = JpxDecoder.DecodePlanes(without, long.MaxValue).Planes;
        var compared = 0;
        for (var i = 0; i < plain[0].Length; i++)
        {
            int y0 = plain[0][i], y1 = plain[1][i], y2 = plain[2][i];
            if (y0 is 0 or 255 || y1 is 0 or 255 || y2 is 0 or 255) continue;
            (y0, y1, y2) = (y0 - 128, y1 - 128, y2 - 128);
            var g = y0 - (int)Math.Floor((y1 + y2) / 4.0);
            int[] expected = [Math.Clamp(y2 + g + 128, 0, 255), Math.Clamp(g + 128, 0, 255), Math.Clamp(y1 + g + 128, 0, 255)];
            Assert.Equal(expected, new int[] { withRct[0][i], withRct[1][i], withRct[2][i] });
            compared++;
        }
        Assert.True(compared > plain[0].Length / 2, $"{compared} of {plain[0].Length}");
    }

    /// <summary>The bare codestream of JPEG 2000 data (a codestream or a JP2/JPX file).</summary>
    private static byte[] Bare(byte[] data)
    {
        var boxes = JpxBoxes.Read(data);
        return boxes.Data[boxes.Start..boxes.End];
    }

    /// <summary>The position of the transformation field of COD in a codestream (Scod, progression, layers, then this byte).</summary>
    private static int MctField(byte[] cs)
    {
        var at = 2;
        while ((cs[at] << 8 | cs[at + 1]) != 0xFF52) at += 2 + (cs[at + 2] << 8 | cs[at + 3]);
        return at + 8;
    }

    /// <summary>
    /// T.800 G.3 and G.3.2 (Equations G-12 to G-14): with the 9-7 filter the same field of COD means the ICT — I0 = Y0 + 1.402·Y2,
    /// I1 = Y0 − 0.34413·Y1 − 0.71414·Y2, I2 = Y0 + 1.772·Y1 —, then the level shift. Step size 2 (exponent 7 at 8 bits), so a coefficient q
    /// comes back as ±(2·|q| + 1), the middle of its interval; the expected values worked out from the equations, rounded and clipped. Without
    /// the field the components are only shifted.
    /// </summary>
    [Theory]
    [InlineData(10, -6, 7, 170, 143, 126, 149, 115, 143)]
    [InlineData(-20, 15, -12, 52, 94, 142, 87, 159, 103)]
    [InlineData(50, -30, 40, 255, 192, 121, 229, 67, 209)]
    [InlineData(3, 25, -24, 66, 152, 225, 135, 179, 79)]
    [InlineData(-63, -40, -1, 0, 31, 0, 1, 47, 125)]
    public void Ict_WithTheIrreversibleFilter_AsG12ToG14(int q0, int q1, int q2, int r, int g, int b, int y0, int y1, int y2)
    {
        int[] q = [q0, q1, q2];
        byte[] Codestream(int mct) => AnnexG.Codestream(new AnnexG.Image
        {
            Components = [(8, false), (8, false), (8, false)], Reversible = false, Exponent = 7, Mct = mct, Value = (_, c) => AnnexG.Coded.Full(q[c]),
        });
        Assert.Equal([r, g, b], AnnexG.At(AnnexG.Planes(Codestream(1)), 0));
        Assert.Equal([y0, y1, y2], AnnexG.At(AnnexG.Planes(Codestream(0)), 0));
    }

    /// <summary>
    /// T.800 G.3 on a real codestream (9-7 filter, five levels, ICT, from the public test collection): the ICT and the level shift worked out
    /// here from the components decoded without the transformation give the image decoded as it is, within two steps (the untransformed
    /// components come rounded to whole values), at every pixel where none of the three was clipped.
    /// </summary>
    [TestPdfsFact]
    public void Ict_RealCodestream_AsWorkedOutFromTheUntransformedComponents()
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(PdfJs("S2.pdf")));
        var cs = JpxImages(doc).Select(i => Bare(Raw(doc, i.Stream))).First(d =>
        {
            var info = JpxCodestream.Parse(d, 0, d.Length);
            return info.Components.Length == 3 && !info.Main.Coding!.Reversible && info.Main.MultipleComponentTransform;
        });
        var withIct = JpxDecoder.DecodePlanes(cs, long.MaxValue).Planes;
        var without = (byte[])cs.Clone();
        without[MctField(cs)] = 0;
        var plain = JpxDecoder.DecodePlanes(without, long.MaxValue).Planes;
        var compared = 0;
        for (var i = 0; i < plain[0].Length; i++)
        {
            int p0 = plain[0][i], p1 = plain[1][i], p2 = plain[2][i];
            if (p0 is 0 or 255 || p1 is 0 or 255 || p2 is 0 or 255) continue;
            double y0 = p0 - 128, y1 = p1 - 128, y2 = p2 - 128;
            double[] expected = [y0 + 1.402 * y2 + 128, y0 - 0.34413 * y1 - 0.71414 * y2 + 128, y0 + 1.772 * y1 + 128];
            for (var k = 0; k < 3; k++)
            {
                var e = Math.Clamp(expected[k], 0, 255);
                Assert.True(Math.Abs(e - withIct[k][i]) <= 2, $"pixel {i}, component {k}: expected {e:F2}, decoded {withIct[k][i]}");
            }
            compared++;
        }
        Assert.True(compared > plain[0].Length / 2, $"{compared} of {plain[0].Length}");
    }

    // ---- ITU-T T.800 Annex H: region of interest (Maxshift)

    /// <summary>
    /// T.800 H.1 step 1 with A.6.3: the scaling value s comes from SPrgn of an RGN with Srgn 0 — in the main header for all tiles, in the
    /// tile-part header for its tile, before the main header's. One 8-bit component in four tiles: a region coefficient −37 (scaled by s), a
    /// background coefficient 5 and 2^s − 1 (below 2^s, coded with as many zero bit planes as Mb − 1 + s less their top plane), a region
    /// coefficient 1. With s known the planes are −37, 5, 31, 1 plus 128; without an RGN, or with Srgn 1 (reserved), the background
    /// coefficients lie below the planes of the band and are lost (128), the region coefficients come out as they are.
    /// </summary>
    [Fact]
    public void Rgn_ScalingValue_FromTheMainOrTheTileHeader_StyleZeroOnly()
    {
        byte[] Codestream(int[] shifts, byte[][] main, Func<int, byte[]>? tile = null)
        {
            int[] values = [-37, 5, 31, 1];
            return AnnexG.Codestream(new AnnexG.Image
            {
                Width = 4, MainMarkers = [.. main], TileMarkers = tile,
                Value = (t, _) => t is 1 or 2
                    ? new AnnexG.Coded(values[t], AnnexG.BitLength(values[t]) - 1, 0, shifts[t])
                    : AnnexG.Coded.Full(values[t], shifts[t]),
            });
        }
        ushort[] Plane(byte[] cs) => AnnexG.Planes(cs)[0];
        Assert.Equal(new ushort[] { 91, 133, 159, 129 }, Plane(Codestream([5, 5, 5, 5], [AnnexG.Rgn(0, 5)])));
        Assert.Equal(new ushort[] { 91, 128, 128, 129 }, Plane(Codestream([5, 5, 5, 5], [AnnexG.Rgn(0, 5, style: 1)])));
        Assert.Equal(new ushort[] { 91, 128, 128, 129 }, Plane(Codestream([5, 5, 5, 5], [])));
        // the tile-part header of tile 1 names s = 3, tile 1 coded with it; the main header's 5 for the others
        Assert.Equal(new ushort[] { 91, 133, 159, 129 }, Plane(Codestream([5, 3, 5, 5], [AnnexG.Rgn(0, 5)], t => t == 1 ? AnnexG.Rgn(0, 3) : [])));
        // only the tile-part header of tile 2 has an RGN (s = 5): the other tiles have no region
        Assert.Equal(new ushort[] { 91, 133, 159, 129 }, Plane(Codestream([0, 0, 5, 0], [], t => t == 2 ? AnnexG.Rgn(0, 5) : [])));
    }

    /// <summary>
    /// T.800 H.1 steps 2 to 4 (Equations H-1, H-2) for one coefficient of a 12-bit component (Mb = 12), coded with the planes
    /// <paramref name="top"/> down to <paramref name="last"/> and the RGN shift s: a region coefficient (a one within the first Mb most
    /// significant bits) goes down by s — fully decoded (step 3) and with fewer than Mb bits decoded (step 2: the bits stay as they are on the
    /// scale of Mb, the reconstruction adds half of the next step, r = 1/2 of E.1.1.2); a background coefficient (the first Mb bits zero) keeps
    /// its value (step 4), also when not all of its planes are decoded. Expected values worked out from the steps; the plane is the value plus
    /// 2048.
    /// </summary>
    [Theory]
    [InlineData(-592, 9, 0, 4, 2011)]      // step 3: −37 · 2^4
    [InlineData(64, 6, 0, 6, 2049)]        // step 3: 1 · 2^6, the smallest region value
    [InlineData(16376, 13, 0, 3, 4095)]    // step 3: 2047 · 2^3
    [InlineData(32000, 14, 10, 5, 3056)]   // step 2: 1000 · 2^5, planes 14..10 — 992 and half of 2^5
    [InlineData(-4928, 12, 8, 6, 1970)]    // step 2: −77 · 2^6, planes 12..8 — 76 and half of 2^2
    [InlineData(5, 2, 0, 3, 2053)]         // step 4
    [InlineData(-31, 4, 0, 5, 2017)]       // step 4: the largest background value 2^5 − 1
    [InlineData(15, 3, 0, 4, 2063)]        // step 4: 2^4 − 1 beside the region value 2^4
    [InlineData(13, 3, 2, 4, 2062)]        // step 4, planes 3..2 — 12 and half of 2^2
    public void Maxshift_StepsTwoToFour(int value, int top, int last, int shift, int plane)
    {
        var cs = AnnexG.Codestream(new AnnexG.Image
        {
            Components = [(12, false)], Exponent = 11, MainMarkers = [AnnexG.Rgn(0, shift)],
            Value = (_, _) => new AnnexG.Coded(value, top, last, shift),
        });
        Assert.Equal(plane, AnnexG.Planes(cs)[0][0]);
    }

    /// <summary>
    /// T.800 H.1 step 2 against the same bits without a region: a region coefficient decoded down to plane <paramref name="last"/> gives what
    /// the coefficient without the scaling gives down to plane last − s — the region only moves the planes.
    /// </summary>
    [Theory]
    [InlineData(1000, 5, 10)]
    [InlineData(-77, 6, 8)]
    [InlineData(1501, 2, 9)]
    public void Maxshift_StepTwo_AsTheUnscaledCoefficient(int q, int shift, int last)
    {
        var scaled = q * (1 << shift);
        var top = AnnexG.BitLength(Math.Abs(scaled)) - 1;
        var region = AnnexG.Codestream(new AnnexG.Image
        {
            Components = [(12, false)], Exponent = 11, MainMarkers = [AnnexG.Rgn(0, shift)],
            Value = (_, _) => new AnnexG.Coded(scaled, top, last, shift),
        });
        var plain = AnnexG.Codestream(new AnnexG.Image
        {
            Components = [(12, false)], Exponent = 11, Value = (_, _) => new AnnexG.Coded(q, top - shift, last - shift),
        });
        var expected = AnnexG.Planes(plain)[0][0];
        Assert.NotEqual(q + 2048, expected);   // not all planes: the reconstruction is not the value itself
        Assert.Equal(expected, AnnexG.Planes(region)[0][0]);
    }

    /// <summary>
    /// T.800 H.1 step 3 on a real codestream (40 × 27 pixels, three components, five levels): an RGN with s = 7 for every component in the
    /// main header moves all planes of every code-block up by 7; every coefficient is then a region coefficient and comes back down — the
    /// image is the same as without the RGN.
    /// </summary>
    [TestPdfsFact]
    public void Maxshift_RealCodestream_EveryCoefficientARegionCoefficient()
    {
        using var doc = PdfDocument.Open(File.ReadAllBytes(PdfJs("jp2k-resetprob.pdf")));
        var (stream, _) = Assert.Single(JpxImages(doc));
        var cs = Bare(Raw(doc, stream));
        var sot = 2;
        while ((cs[sot] << 8 | cs[sot + 1]) != 0xFF90) sot += 2 + (cs[sot + 2] << 8 | cs[sot + 3]);
        byte[] withRgn = [.. cs[..sot], .. AnnexG.Rgn(0, 7), .. AnnexG.Rgn(1, 7), .. AnnexG.Rgn(2, 7), .. cs[sot..]];
        var plain = JpxDecoder.Decode(cs, long.MaxValue);
        var region = JpxDecoder.Decode(withRgn, long.MaxValue);
        Assert.False(region.Truncated);
        Assert.Equal(plain.Samples, region.Samples);
    }
}
