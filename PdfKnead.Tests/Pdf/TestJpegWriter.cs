namespace PdfKnead.Tests.Pdf;

/// <summary>
/// JPEGs for the tests that no collection holds and PDF-Knead never writes (test code only), made from given quantised coefficients:
/// arithmetic coded images, sequential (SOF9) or progressive (SOF10) — ITU-T T.81, Annex D (the binary encoder), F.1.4 (the sequential
/// model), G.1.2 and G.1.3 (point transform, successive approximation), B.2.4.3 (DAC), B.2.4.4 (DRI) — and baseline images with the typical
/// Huffman tables of K.3 (Annex C, F.1.2), with or without their DHT segments. The own decoder is compared with libjpeg-turbo on them
/// (JpegArithmeticTests, JpegDecoderTests); the binary arithmetic encoder itself is checked against the test sequence of T.81, K.4.1.
/// </summary>
internal static class TestJpegWriter
{
    // ITU-T T.81, Table D.3 (Qe, next index after an LPS and after an MPS, the indices where the MPS sense switches) and the typical Huffman
    // tables of K.3 — the writer's own copy, so that it compiles against any earlier state of the decoder (counter-checks) and does not share
    // the decoder's tables; libjpeg-turbo, which reads everything it writes in the tests, checks both
    private static readonly ushort[] Qe =
    [
        0x5A1D, 0x2586, 0x1114, 0x080B, 0x03D8, 0x01DA, 0x00E5, 0x006F, 0x0036, 0x001A, 0x000D, 0x0006, 0x0003, 0x0001, 0x5A7F, 0x3F25,
        0x2CF2, 0x207C, 0x17B9, 0x1182, 0x0CEF, 0x09A1, 0x072F, 0x055C, 0x0406, 0x0303, 0x0240, 0x01B1, 0x0144, 0x00F5, 0x00B7, 0x008A,
        0x0068, 0x004E, 0x003B, 0x002C, 0x5AE1, 0x484C, 0x3A0D, 0x2EF1, 0x261F, 0x1F33, 0x19A8, 0x1518, 0x1177, 0x0E74, 0x0BFB, 0x09F8,
        0x0861, 0x0706, 0x05CD, 0x04DE, 0x040F, 0x0363, 0x02D4, 0x025C, 0x01F8, 0x01A4, 0x0160, 0x0125, 0x00F6, 0x00CB, 0x00AB, 0x008F,
        0x5B12, 0x4D04, 0x412C, 0x37D8, 0x2FE8, 0x293C, 0x2379, 0x1EDF, 0x1AA9, 0x174E, 0x1424, 0x119C, 0x0F6B, 0x0D51, 0x0BB6, 0x0A40,
        0x5832, 0x4D1C, 0x438E, 0x3BDD, 0x34EE, 0x2EAE, 0x299A, 0x2516, 0x5570, 0x4CA9, 0x44D9, 0x3E22, 0x3824, 0x32B4, 0x2E17, 0x56A8,
        0x4F46, 0x47E5, 0x41CF, 0x3C3D, 0x375E, 0x5231, 0x4C0F, 0x4639, 0x415E, 0x5627, 0x50E7, 0x4B85, 0x5597, 0x504F, 0x5A10, 0x5522,
        0x59EB,
    ];

    private static readonly byte[] NextLps =
    [
        1, 14, 16, 18, 20, 23, 25, 28, 30, 33, 35, 9, 10, 12, 15, 36, 38, 39, 40, 42, 43, 45, 46, 48, 49, 51, 52, 54, 56, 57, 59, 60,
        62, 63, 32, 33, 37, 64, 65, 67, 68, 69, 70, 72, 73, 74, 75, 77, 78, 79, 48, 50, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 61, 61,
        65, 80, 81, 82, 83, 84, 86, 87, 87, 72, 72, 74, 74, 75, 77, 77, 80, 88, 89, 90, 91, 92, 93, 86, 88, 95, 96, 97, 99, 99, 93, 95,
        101, 102, 103, 104, 99, 105, 106, 107, 103, 105, 108, 109, 110, 111, 110, 112, 112,
    ];

    private static readonly byte[] NextMps =
    [
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 13, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
        33, 34, 35, 9, 37, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 62, 63, 32,
        65, 66, 67, 68, 69, 70, 71, 72, 73, 74, 75, 76, 77, 78, 79, 48, 81, 82, 83, 84, 85, 86, 87, 71, 89, 90, 91, 92, 93, 94, 86, 96,
        97, 98, 99, 100, 93, 102, 103, 104, 99, 106, 107, 103, 109, 107, 111, 109, 111,
    ];

    private static readonly int[] Switches = [0, 14, 36, 64, 80, 88, 95, 105, 110, 112];

    private static readonly byte[] DcLuminanceCounts = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcChrominanceCounts = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] DcSymbols = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly byte[] AcLuminanceCounts = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D];
    private static readonly byte[] AcLuminanceSymbols =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91,
        0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A,
        0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x53,
        0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79,
        0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9,
        0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2,
        0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA,
    ];
    private static readonly byte[] AcChrominanceCounts = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
    private static readonly byte[] AcChrominanceSymbols =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14,
        0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0, 0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17,
        0x18, 0x19, 0x1A, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A,
        0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78,
        0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3,
        0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5, 0xC6, 0xC7,
        0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2,
        0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8, 0xF9, 0xFA,
    ];

    /// <summary>The typical tables of K.3 in the order DC luminance, DC chrominance, AC luminance, AC chrominance.</summary>
    internal static readonly (byte[] Counts, byte[] Symbols)[] Typical =
        [(DcLuminanceCounts, DcSymbols), (DcChrominanceCounts, DcSymbols), (AcLuminanceCounts, AcLuminanceSymbols), (AcChrominanceCounts, AcChrominanceSymbols)];

    /// <summary>
    /// The binary arithmetic encoder of T.81, D.1: Code_0 and Code_1 with the conditional MPS/LPS exchange (Figures D.3, D.4), Renorm_e
    /// (D.7), Byte_out with the carry over stacked X'FF' bytes and byte stuffing (D.8–D.11), Initenc (D.12) and Flush (D.13–D.15).
    /// One instance per entropy-coded segment.
    /// </summary>
    internal sealed class Coder
    {
        // index 0 stands for the byte before the segment: the segment pointer starts one before the first byte (Figure D.12)
        private readonly List<byte> _out = [0];
        private long _c;
        private int _a = 0x10000, _ct = 11, _st;

        /// <summary>Codes the decision <paramref name="d"/> with the estimate in bin <paramref name="s"/> of <paramref name="stats"/>.</summary>
        public void Code(byte[] stats, int s, int d)
        {
            var state = stats[s];
            int index = state & 0x7F, mps = state >> 7;
            int qe = Qe[index];
            _a -= qe;
            if (d == mps)
            {
                if (_a >= 0x8000) return;          // no renormalisation, the estimate stays
                if (_a < qe)
                {
                    _c += _a;                      // exchange: the MPS takes the upper sub-interval
                    _a = qe;
                }
                index = NextMps[index];
            }
            else
            {
                if (_a >= qe)
                {
                    _c += _a;
                    _a = qe;
                }
                if (Array.IndexOf(Switches, index) >= 0) mps = 1 - mps;
                index = NextLps[index];
            }
            stats[s] = (byte)(index | mps << 7);
            Renormalise();
        }

        /// <summary>A decision with the fixed estimate Qe = X'5A1D', MPS = 0, which never changes (signs of AC values, correction bits of DC).</summary>
        public void CodeFixed(int d)
        {
            const int qe = 0x5A1D;
            _a -= qe;
            if (d == 0)
            {
                if (_a >= 0x8000) return;
                if (_a < qe)
                {
                    _c += _a;
                    _a = qe;
                }
            }
            else if (_a >= qe)
            {
                _c += _a;
                _a = qe;
            }
            Renormalise();
        }

        private void Renormalise()
        {
            do
            {
                _a <<= 1;
                _c <<= 1;
                if (--_ct == 0)
                {
                    ByteOut();
                    _ct = 8;
                }
            }
            while (_a < 0x8000);
        }

        private void ByteOut()
        {
            var t = (int)(_c >> 19);
            if (t > 0xFF)
            {
                // carry: into the byte written last (never the byte before the segment), stuffed if it becomes X'FF'; the stacked X'FF'
                // bytes become zeros
                Assert.True(_out.Count > 1, "Übertrag vor das Segment");
                _out[^1]++;
                if (_out[^1] == 0xFF) _out.Add(0);
                for (; _st > 0; _st--) _out.Add(0);
                _out.Add((byte)(t & 0xFF));
            }
            else if (t == 0xFF)
            {
                _st++;                             // held back until it is known whether a carry runs through it
            }
            else
            {
                for (; _st > 0; _st--)
                {
                    _out.Add(0xFF);
                    _out.Add(0);
                }
                _out.Add((byte)t);
            }
            _c &= 0x7FFFF;
        }

        /// <summary>Terminates the segment and returns its bytes (Flush; the trailing zero bytes left out, stuffed zeros kept).</summary>
        public byte[] Flush()
        {
            var t = (_c + _a - 1) & 0xFFFF0000L;   // Clear_final_bits: as many trailing zero bits as the final interval allows
            if (t < _c) t += 0x8000;
            _c = t << _ct;
            ByteOut();
            _c <<= 8;
            ByteOut();
            var end = _out.Count;                  // Discard_final_zeros
            while (end > 1 && _out[end - 1] == 0) end--;
            if (end > 1 && _out[end - 1] == 0xFF) end++;
            return [.. _out.Skip(1).Take(end - 1)];
        }
    }

    /// <summary>
    /// A component: identifier, sampling factors, quantisation table, its DC and AC table numbers, and the quantised coefficients of each
    /// block in zig-zag order — <see cref="Blocks"/>[row][column] over the grid of whole MCUs.
    /// </summary>
    internal sealed record Component(int Id, int H, int V, int Tq, int Td, int Ta, short[][][] Blocks);

    /// <summary>A scan: the components (indices into the frame), spectral selection and successive approximation.</summary>
    internal sealed record Scan(int[] Components, int Ss, int Se, int Ah, int Al);

    /// <summary>Conditioning of the DAC segment: per DC table (L, U) and per AC table Kx; null for the defaults (0, 1) and 5.</summary>
    internal sealed record Conditioning((int L, int U)[] Dc, int[] Kx);

    /// <summary>A quantisation table: 1 for the DC and the first eleven AC coefficients (so that large categories stay valid), above that
    /// <paramref name="q"/> rising slowly along the zig-zag order.</summary>
    internal static byte[] Quant(int q) => [.. Enumerable.Range(0, 64).Select(k => (byte)(k < 12 ? 1 : q + k / 6))];

    /// <summary>
    /// The components with quantised coefficients (zig-zag order) from a fixed pseudo-random sequence: the first component sampled
    /// <paramref name="h"/> × <paramref name="v"/>, the others 1 × 1. DC differences small and now and then large (all context classes of
    /// F.1.4.4.1.2), AC values mostly small with rare large ones up to the category 2^9 (both sides of Kx), blocks with DC only, blocks whose
    /// last coefficient is non-zero (no end of band), and blocks with the DC of the one before (difference 0). Every block stays in the range
    /// of a valid 8-bit image: the inverse DCT gives at most ±512 around the middle — beyond that libjpeg-turbo wraps the values around
    /// (its range table) where the own decoder clamps them, a difference that only invalid data shows. (Bound: a DC coefficient adds at most
    /// |DC·Q|/8 to a sample, an AC coefficient at most |AC·Q|/4; here at most 100 and 400.)
    /// </summary>
    internal static Component[] Components(int width, int height, int count, int h, int v, byte[][] quant)
    {
        uint state = 0x9E3779B9;
        uint Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }
        int hmax = count == 1 ? 1 : h, vmax = count == 1 ? 1 : v;
        int mw = (width + 8 * hmax - 1) / (8 * hmax), mh = (height + 8 * vmax - 1) / (8 * vmax);
        var comps = new Component[count];
        for (var c = 0; c < count; c++)
        {
            int ch = c == 0 ? hmax : 1, cv = c == 0 ? vmax : 1;
            var blocks = new short[mh * cv][][];
            short dc = 0;
            for (var by = 0; by < blocks.Length; by++)
            {
                blocks[by] = new short[mw * ch][];
                for (var bx = 0; bx < blocks[by].Length; bx++)
                {
                    var zz = new short[64];
                    var n = by * blocks[by].Length + bx;
                    var r = Next();
                    var q = quant[Math.Min(c, 1)];
                    if (n % 17 != 3)
                        dc = (short)Math.Clamp(r % 23 == 0 ? dc + (int)(r >> 8) % 1400 - 700 : dc + (int)(r >> 8) % 41 - 20, -800 / q[0], 800 / q[0]);
                    zz[0] = dc;
                    var budget = 1600;                 // Σ |AC·Q| of the block
                    if (n % 13 == 5)
                    {
                        zz[63] = (short)((r & 1) != 0 ? -3 : 1);
                        budget -= Math.Abs(zz[63]) * q[63];
                    }
                    if (n % 11 != 0)
                    {
                        for (var k = 1; k < 63; k++)
                        {
                            var x = Next();
                            if (x % 1000 >= (uint)(700 * Math.Exp(-k / 10.0))) continue;
                            var size = x >> 10;
                            var m = size % 40 == 0 && k < 12 ? (int)(size >> 6) % 1023 + 1 : (int)(size >> 6) % 6 + 1;
                            m = Math.Min(m, budget / q[k]);
                            if (m == 0) continue;
                            budget -= m * q[k];
                            zz[k] = (short)((x & 0x200) != 0 ? -m : m);
                        }
                    }
                    blocks[by][bx] = zz;
                }
            }
            comps[c] = new Component(c + 1, ch, cv, Math.Min(c, 1), Math.Min(c, 1), Math.Min(c, 1), blocks);
        }
        return comps;
    }

    /// <summary>
    /// The whole image: SOI, DQT, SOF9 or SOF10, optional DAC and DRI, the scans (sequential: one, all components), EOI.
    /// <paramref name="beforeScan"/> gives bytes to put before the scan with that index (a segment the test wants there), or null.
    /// </summary>
    public static byte[] Write(int width, int height, Component[] comps, byte[][] quant, Scan[]? progressive = null,
        int restartInterval = 0, Conditioning? conditioning = null, Func<int, byte[]?>? beforeScan = null)
    {
        var o = new List<byte> { 0xFF, 0xD8 };
        for (var t = 0; t < quant.Length; t++) Segment(o, 0xDB, [(byte)t, .. quant[t]]);
        var sof = new List<byte> { 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)comps.Length };
        foreach (var c in comps) sof.AddRange([(byte)c.Id, (byte)(c.H << 4 | c.V), (byte)c.Tq]);
        Segment(o, progressive is null ? (byte)0xC9 : (byte)0xCA, [.. sof]);
        if (conditioning is not null)
        {
            var dac = new List<byte>();
            for (var t = 0; t < conditioning.Dc.Length; t++) dac.AddRange([(byte)t, (byte)(conditioning.Dc[t].U << 4 | conditioning.Dc[t].L)]);
            for (var t = 0; t < conditioning.Kx.Length; t++) dac.AddRange([(byte)(0x10 | t), (byte)conditioning.Kx[t]]);
            Segment(o, 0xCC, [.. dac]);
        }
        if (restartInterval > 0) Segment(o, 0xDD, [(byte)(restartInterval >> 8), (byte)restartInterval]);
        var scans = progressive ?? [new Scan([.. Enumerable.Range(0, comps.Length)], 0, 63, 0, 0)];
        for (var index = 0; index < scans.Length; index++)
        {
            var scan = scans[index];
            if (beforeScan?.Invoke(index) is { } extra) o.AddRange(extra);
            var sos = new List<byte> { (byte)scan.Components.Length };
            foreach (var i in scan.Components) sos.AddRange([(byte)comps[i].Id, (byte)(comps[i].Td << 4 | comps[i].Ta)]);
            sos.AddRange([(byte)scan.Ss, (byte)scan.Se, (byte)(scan.Ah << 4 | scan.Al)]);
            Segment(o, 0xDA, [.. sos]);
            o.AddRange(EntropyCoded(width, height, comps, scan, restartInterval, conditioning));
        }
        o.AddRange([0xFF, 0xD9]);
        return [.. o];
    }

    /// <summary>
    /// A baseline image (SOF0, one interleaved scan) coded with the typical Huffman tables of T.81, K.3 — DC and AC table 0 luminance,
    /// 1 chrominance — with or without the DHT segments that specify them (Motion-JPEG leaves them out). With
    /// <paramref name="destinations"/> 2 the quantisation and Huffman tables go to the destinations 2 and 3 instead of 0 and 1, and the frame
    /// is extended sequential (SOF1, F.1.3: four destinations of each kind). With <paramref name="adobe"/> an Adobe marker (APP14, Adobe TN 5116)
    /// with that colour transform comes first (0: none, 1: YCbCr, 2: YCCK).
    /// </summary>
    public static byte[] Baseline(int width, int height, Component[] comps, byte[][] quant, bool withTables, int destinations = 0, int? adobe = null)
    {
        var o = new List<byte> { 0xFF, 0xD8 };
        if (adobe is { } transform) Segment(o, 0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0, (byte)transform]);
        for (var t = 0; t < quant.Length; t++) Segment(o, 0xDB, [(byte)(destinations + t), .. quant[t]]);
        var sof = new List<byte> { 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, (byte)comps.Length };
        foreach (var c in comps) sof.AddRange([(byte)c.Id, (byte)(c.H << 4 | c.V), (byte)(destinations + c.Tq)]);
        Segment(o, destinations == 0 ? (byte)0xC0 : (byte)0xC1, [.. sof]);
        var typical = Typical;
        if (withTables)
            for (var t = 0; t < 4; t++) Segment(o, 0xC4, [(byte)((t >> 1) << 4 | (destinations + (t & 1))), .. typical[t].Counts, .. typical[t].Symbols]);
        var scan = new Scan([.. Enumerable.Range(0, comps.Length)], 0, 63, 0, 0);
        var sos = new List<byte> { (byte)comps.Length };
        foreach (var c in comps) sos.AddRange([(byte)c.Id, (byte)((destinations + c.Td) << 4 | (destinations + c.Ta))]);
        sos.AddRange([0, 63, 0]);
        Segment(o, 0xDA, [.. sos]);

        // Annex C: codes in order of length, counting up within a length and doubling to the next one
        var codes = typical.Select(t =>
        {
            var table = new Dictionary<int, (int Code, int Length)>();
            int code = 0, k = 0;
            for (var length = 1; length <= 16; length++, code <<= 1)
                for (var i = 0; i < t.Counts[length - 1]; i++) table[t.Symbols[k++]] = (code++, length);
            return table;
        }).ToArray();
        var bits = new BitWriter();
        var prediction = new int[comps.Length];
        foreach (var mcu in Mcus(width, height, comps, scan))
            foreach (var (slot, zz) in mcu)
            {
                var c = comps[slot];
                var (dc, ac) = (codes[c.Td], codes[2 + c.Ta]);
                var diff = zz[0] - prediction[slot];
                prediction[slot] = zz[0];
                var size = Size(diff);
                bits.Write(dc[size]);
                bits.Write(diff < 0 ? diff + (1 << size) - 1 : diff, size);
                var run = 0;
                for (var k = 1; k < 64; k++)
                {
                    if (zz[k] == 0)
                    {
                        run++;
                        continue;
                    }
                    for (; run > 15; run -= 16) bits.Write(ac[0xF0]);   // ZRL: sixteen zeros
                    size = Size(zz[k]);
                    bits.Write(ac[run << 4 | size]);
                    bits.Write(zz[k] < 0 ? zz[k] + (1 << size) - 1 : zz[k], size);
                    run = 0;
                }
                if (run > 0) bits.Write(ac[0x00]);                       // end of block
            }
        o.AddRange(bits.Finish());
        o.AddRange([0xFF, 0xD9]);
        return [.. o];
    }

    /// <summary>The magnitude category of a value (F.1.2.1.1): the number of bits of its magnitude.</summary>
    private static int Size(int v) => v == 0 ? 0 : 32 - int.LeadingZeroCount(Math.Abs(v));

    /// <summary>Bits most significant first, each X'FF' byte followed by a stuffed zero, the last byte filled with 1-bits (F.1.2.3, B.1.1.5).</summary>
    private sealed class BitWriter
    {
        private readonly List<byte> _out = [];
        private int _buffer, _count;

        public void Write((int Code, int Length) code) => Write(code.Code, code.Length);

        public void Write(int value, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                _buffer = _buffer << 1 | (value >> i & 1);
                if (++_count == 8) Flush();
            }
        }

        private void Flush()
        {
            _out.Add((byte)_buffer);
            if (_buffer == 0xFF) _out.Add(0);
            _buffer = _count = 0;
        }

        public List<byte> Finish()
        {
            while (_count != 0) Write(1, 1);
            return _out;
        }
    }

    private static void Segment(List<byte> o, byte marker, byte[] content)
    {
        o.AddRange([0xFF, marker, (byte)((content.Length + 2) >> 8), (byte)(content.Length + 2)]);
        o.AddRange(content);
    }

    /// <summary>The entropy-coded segments of one scan with the restart markers between them (A.2: order of blocks; E.1.4: restarts).</summary>
    private static List<byte> EntropyCoded(int width, int height, Component[] comps, Scan scan, int interval, Conditioning? conditioning)
    {
        var mcus = Mcus(width, height, comps, scan);
        var o = new List<byte>();
        Model? model = null;
        for (var m = 0; m < mcus.Count; m++)
        {
            if (model is null || (interval > 0 && m % interval == 0))
            {
                if (model is not null)
                {
                    o.AddRange(model.Coder.Flush());
                    o.AddRange([0xFF, (byte)(0xD0 + (m / interval - 1) % 8)]);
                }
                model = new Model([.. scan.Components.Select(i => comps[i])], conditioning);
            }
            foreach (var (slot, block) in mcus[m]) model.Block(scan, slot, block);
        }
        o.AddRange(model!.Coder.Flush());
        return o;
    }

    /// <summary>The blocks of a scan, MCU by MCU: interleaved in the grid of whole MCUs (A.2.3), a single component over its own blocks (A.2.2).</summary>
    private static List<List<(int Slot, short[] Block)>> Mcus(int width, int height, Component[] comps, Scan scan)
    {
        int hmax = comps.Max(c => c.H), vmax = comps.Max(c => c.V);
        var mcus = new List<List<(int Slot, short[] Block)>>();
        if (scan.Components.Length == 1)
        {
            var c = comps[scan.Components[0]];
            int cw = (width * c.H + hmax - 1) / hmax, ch = (height * c.V + vmax - 1) / vmax;
            for (var by = 0; by < (ch + 7) / 8; by++)
                for (var bx = 0; bx < (cw + 7) / 8; bx++)
                    mcus.Add([(0, c.Blocks[by][bx])]);
        }
        else
        {
            int mw = (width + 8 * hmax - 1) / (8 * hmax), mh = (height + 8 * vmax - 1) / (8 * vmax);
            for (var my = 0; my < mh; my++)
                for (var mx = 0; mx < mw; mx++)
                {
                    var mcu = new List<(int, short[])>();
                    for (var slot = 0; slot < scan.Components.Length; slot++)
                    {
                        var c = comps[scan.Components[slot]];
                        for (var v = 0; v < c.V; v++)
                            for (var h = 0; h < c.H; h++)
                                mcu.Add((slot, c.Blocks[my * c.V + v][mx * c.H + h]));
                    }
                    mcus.Add(mcu);
                }
        }
        return mcus;
    }

    /// <summary>The statistics of one entropy-coded segment (reset at each scan and restart; F.1.4, G.1.3).</summary>
    private sealed class Model((int L, int U)[] dcBounds, int[] kx, Component[] comps)
    {
        public readonly Coder Coder = new();
        private readonly byte[][] _dc = [new byte[64], new byte[64], new byte[64], new byte[64]];
        private readonly byte[][] _ac = [new byte[256], new byte[256], new byte[256], new byte[256]];
        private readonly int[] _context = new int[4], _prediction = new int[4];

        public Model(Component[] comps, Conditioning? conditioning)
            : this(conditioning?.Dc ?? [(0, 1), (0, 1), (0, 1), (0, 1)], conditioning?.Kx ?? [5, 5, 5, 5], comps)
        {
        }

        public void Block(Scan scan, int slot, short[] zz)
        {
            var c = comps[slot];
            if (scan.Ss == 0)
            {
                if (scan.Ah == 0)
                {
                    var v = zz[0] >> scan.Al;      // DC point transform: arithmetic shift (G.1.2.1)
                    Dc(c.Td, slot, v - _prediction[slot]);
                    _prediction[slot] = v;
                }
                else
                {
                    Coder.CodeFixed(zz[0] >> scan.Al & 1);
                }
                if (scan.Se == 0) return;
            }
            var ss = Math.Max(1, scan.Ss);
            if (scan.Ah == 0) Band(c.Ta, zz, ss, scan.Se, scan.Al);
            else Refine(c.Ta, zz, ss, scan.Se, scan.Al);
        }

        /// <summary>A DC difference (Figures F.4, F.7, F.8) and the context of the next block by the bounds L, U (F.1.4.4.1.2).</summary>
        private void Dc(int t, int slot, int diff)
        {
            var stats = _dc[t];
            var s0 = _context[slot];
            if (diff == 0)
            {
                Coder.Code(stats, s0, 0);
                _context[slot] = 0;
                return;
            }
            Coder.Code(stats, s0, 1);
            var sign = diff < 0 ? 1 : 0;
            Coder.Code(stats, s0 + 1, sign);
            var v = Math.Abs(diff);
            Magnitude(stats, s0 + 2 + sign, 20, 21, v - 1);
            var (l, u) = dcBounds[t];
            var lower = l == 0 ? 0 : 1 << (l - 1);
            _context[slot] = v <= lower ? 0 : v > 1 << u ? 12 + 4 * sign : 4 + 4 * sign;
        }

        /// <summary>The magnitude category and the bits below the leading one (Figures F.8, F.9), all bits in the bin 14 further.</summary>
        private void Magnitude(byte[] stats, int s, int x1, int x2, int sz)
        {
            if (sz == 0)
            {
                Coder.Code(stats, s, 0);
                return;
            }
            Coder.Code(stats, s, 1);
            var m = 1;
            if (sz == 1)
            {
                Coder.Code(stats, x1, 0);
                s = x1;
            }
            else
            {
                Coder.Code(stats, x1, 1);
                m = 2;
                s = x2;
                while (2 * m <= sz)
                {
                    Coder.Code(stats, s, 1);
                    m <<= 1;
                    s++;
                }
                Coder.Code(stats, s, 0);
            }
            s += 14;
            for (var bit = m >> 1; bit != 0; bit >>= 1) Coder.Code(stats, s, (sz & bit) != 0 ? 1 : 0);
        }

        /// <summary>The AC coefficients <paramref name="ss"/>–<paramref name="se"/> divided by 2^Al toward zero (Figure F.5; G.1.2.2, G.1.3.2).</summary>
        private void Band(int t, short[] zz, int ss, int se, int al)
        {
            var stats = _ac[t];
            int Value(int k) => zz[k] < 0 ? -(-zz[k] >> al) : zz[k] >> al;
            var eob = se;
            while (eob >= ss && Value(eob) == 0) eob--;
            for (var k = ss; k <= se; k++)
            {
                if (k > eob)
                {
                    Coder.Code(stats, 3 * (k - 1), 1);   // end of band
                    return;
                }
                Coder.Code(stats, 3 * (k - 1), 0);
                while (Value(k) == 0)
                {
                    Coder.Code(stats, 3 * (k - 1) + 1, 0);
                    k++;
                }
                Coder.Code(stats, 3 * (k - 1) + 1, 1);
                var v = Value(k);
                Coder.CodeFixed(v < 0 ? 1 : 0);
                var s = 3 * (k - 1) + 2;
                Magnitude(stats, s, s, k <= kx[t] ? 189 : 217, Math.Abs(v) - 1);
            }
        }

        /// <summary>
        /// A refinement scan of the AC band (G.1.3.3, Figure G.10): coefficients already non-zero get their correction bit, newly non-zero
        /// ones (±1 at this bit) their sign; the end of band is coded only after the last coefficient non-zero in the earlier scans.
        /// </summary>
        private void Refine(int t, short[] zz, int ss, int se, int al)
        {
            var stats = _ac[t];
            int Magnitude(int k) => Math.Abs((int)zz[k]) >> al;
            var eob = se;
            while (eob >= ss && Magnitude(eob) == 0) eob--;
            var eobx = eob;                       // the end of band of the earlier scans
            while (eobx >= ss && Magnitude(eobx) >> 1 == 0) eobx--;
            var k = ss;
            for (; k <= eob; k++)
            {
                if (k > eobx) Coder.Code(stats, 3 * (k - 1), 0);
                while (true)
                {
                    var m = Magnitude(k);
                    if (m >> 1 != 0)
                    {
                        Coder.Code(stats, 3 * (k - 1) + 2, m & 1);
                        break;
                    }
                    if (m != 0)
                    {
                        Coder.Code(stats, 3 * (k - 1) + 1, 1);
                        Coder.CodeFixed(zz[k] < 0 ? 1 : 0);
                        break;
                    }
                    Coder.Code(stats, 3 * (k - 1) + 1, 0);
                    k++;
                }
            }
            if (k <= se) Coder.Code(stats, 3 * (k - 1), 1);
        }
    }
}
