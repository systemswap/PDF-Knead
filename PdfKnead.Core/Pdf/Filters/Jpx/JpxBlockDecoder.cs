using PdfKnead.Core.Pdf.Filters.Jbig2;

namespace PdfKnead.Core.Pdf.Filters.Jpx;

/// <summary>
/// Code-block decoder (tier 1, ISO/IEC 15444-1, Annex D): bit planes from top to bottom, per plane a significance
/// propagation, magnitude refinement and cleanup pass, arithmetic (MQ) or raw in "bypass" mode. Result per coefficient: magnitude with
/// half a bit of extra precision (centre of the interval still open) and sign. One decoder per thread, reused for many blocks.
/// When a coefficient becomes significant, it registers itself with its 8 neighbours (significance, and for the four direct ones
/// also the sign), so a context is one table lookup instead of eight reads per query.
/// </summary>
internal sealed class JpxBlockDecoder
{
    // Code-block style (SPcod, ISO/IEC 15444-1, Table A.19)
    public const int Bypass = 1, ResetContexts = 2, TerminateAll = 4, VerticallyCausal = 8, SegmentationSymbols = 32;

    /// <summary>Whether the last block decoded showed an error: a segmentation symbol other than 1010 (D.5).</summary>
    public bool Damaged { get; private set; }

    // State per coefficient: its own flags, above them the neighbours (bits 4–11: W, E, N, S, NW, NE, SW, SE) and the
    // signs of the direct neighbours (bits 12–15: W, E, N, S); in the wide decoder with an ROI shift also whether the coefficient belongs to
    // the region (bit 16) and whether it was refined below the shift (bit 17)
    private const int Significant = 1, Negative = 2, Visited = 4, Refined = 8, Region = 1 << 16, RegionLow = 1 << 17;
    private const int NeighborShift = 4, SignShift = 12;
    private const int W = 1 << 4, E = 1 << 5, N = 1 << 6, S = 1 << 7, NW = 1 << 8, NE = 1 << 9, SW = 1 << 10, SE = 1 << 11;
    private const int SignW = 1 << 12, SignE = 1 << 13, SignN = 1 << 14, SignS = 1 << 15;

    /// <summary>Vertically causal, last row of a stripe: everything from the row below is ignored.</summary>
    private const int South = S | SW | SE | SignS;

    private const int UniformContext = 18, RunLengthContext = 17;

    /// <summary>Zero-coding context per sub-band orientation (LL/LH, HL, HH) and neighbours (8 bits).</summary>
    internal static readonly byte[][] ZeroContexts = BuildZeroContexts();

    /// <summary>Sign context (bits 0–4) and XOR bit (bit 7) from significance and sign of the four direct neighbours.</summary>
    internal static readonly byte[] SignContexts = BuildSignContexts();

    private int[] _flags = [];
    private int[] _magnitude = [];

    /// <summary>The magnitudes of the wide decoder (<see cref="MaxNarrowPlane"/>), allocated on first use.</summary>
    private long[] _wide = [];

    /// <summary>The ROI shift s of the block the wide decoder works on (0 without one).</summary>
    private int _split;
    private int _stride;

    /// <summary>
    /// The highest bit-plane the 32-bit magnitudes hold: a coefficient significant in plane p takes 3 · 2^p (magnitude × 2 with the half bit),
    /// below 2^31 up to p = 29. Higher planes go to the wide decoder with 64-bit magnitudes.
    /// </summary>
    public const int MaxNarrowPlane = 29;

    /// <summary>
    /// The most bit-planes a code-block can have: Mb = G + εb − 1 is at most 7 + 31 − 1 = 37 (Equation E-2 with the 3-bit guard bits and the
    /// 5-bit exponent of Tables A.28 to A.30; ISO/IEC 15444-1, B.10.7.1, note), and an ROI shift adds up to 255 (SPrgn, Table A.26; H.2: M′b =
    /// Mb + s) — 292.
    /// </summary>
    public const int MaxBitPlanes = 37 + 255;

    /// <summary>The most coding passes of a code-block: the first plane with the cleanup pass only, then three per plane (D.3).</summary>
    public const int MaxCodingPasses = 3 * MaxBitPlanes - 2;
    private readonly byte[] _contexts = new byte[19];

    /// <summary>Width of the block decoded last.</summary>
    public int Width { get; private set; }

    /// <summary>Height of the block decoded last.</summary>
    public int Height { get; private set; }

    /// <summary>Signed coefficient of the block decoded last, as magnitude × 2 (the extra half bit).</summary>
    public int At(int x, int y)
    {
        var i = (y + 1) * _stride + x + 1;
        var m = _magnitude[i];
        return (_flags[i] & Negative) != 0 ? -m : m;
    }

    /// <summary>
    /// <see cref="At"/> for a block decoded wide: the magnitude × 2 in 64 bits, a coefficient of the region already scaled down by the ROI shift
    /// (ISO/IEC 15444-1, H.1).
    /// </summary>
    public long AtWide(int x, int y)
    {
        var i = (y + 1) * _stride + x + 1;
        var m = Math.Max(0, _wide[i]);
        return (_flags[i] & Negative) != 0 ? -m : m;
    }

    /// <summary>
    /// Decodes a block.
    /// </summary>
    /// <param name="width">Block width.</param>
    /// <param name="height">Block height.</param>
    /// <param name="segments">Data per codeword segment with its number of coding passes.</param>
    /// <param name="orientation">0 LL, 1 HL, 2 LH, 3 HH.</param>
    /// <param name="style">Code-block style flags (<see cref="Bypass"/> …).</param>
    /// <param name="topPlane">Index of the top bit plane (Mb − 1 − zero bit planes + ROI shift).</param>
    /// <param name="roi">
    /// Negative: the 32-bit magnitudes (<paramref name="topPlane"/> at most <see cref="MaxNarrowPlane"/>). Otherwise the wide decoder with this
    /// ROI shift s (0 for none): 64-bit magnitudes, and a coefficient that becomes significant in plane s or above belongs to the region, its
    /// bits counted from plane s (H.1 step 3: its bits below s carry nothing) — so its magnitude needs no more bits than Mb, whatever s is.
    /// </param>
    /// <returns>Coefficients processed × passes (for the work limit).</returns>
    public long Decode(int width, int height, IReadOnlyList<(byte[] Data, int Passes)> segments, int orientation, int style, int topPlane, int roi = -1)
    {
        Damaged = false;
        Width = width;
        Height = height;
        _stride = width + 2;
        var size = _stride * (height + 2);
        if (_flags.Length < size)
        {
            _flags = new int[size];
            _magnitude = new int[size];
        }
        else
        {
            Array.Clear(_flags, 0, size);
            Array.Clear(_magnitude, 0, size);
        }
        ResetAll();
        if (roi < 0) return Passes<Narrow>(segments, orientation, style, topPlane);
        if (_wide.Length < size) _wide = new long[_flags.Length];
        else Array.Clear(_wide, 0, size);
        _split = roi;
        return Passes<Wide>(segments, orientation, style, topPlane);
    }

    /// <summary>The coding passes of a block, with the magnitudes kept by <typeparamref name="TStore"/>.</summary>
    private long Passes<TStore>(IReadOnlyList<(byte[] Data, int Passes)> segments, int orientation, int style, int topPlane)
        where TStore : struct, IMagnitudes
    {
        int width = Width, height = Height;

        var zero = ZeroContexts[orientation switch { 1 => 1, 3 => 2, _ => 0 }];
        var vsc = (style & VerticallyCausal) != 0;
        var plane = topPlane;
        var type = 2;   // the first plane starts with the cleanup pass
        var pass = 0;
        long work = 0;
        foreach (var (data, passes) in segments)
        {
            MqDecoder? mq = null;
            RawBits? raw = null;
            for (var k = 0; k < passes; k++, pass++)
            {
                if (plane < 0) return work;
                // Bypass: from the 11th pass on, significance and refinement passes are raw
                var isRaw = (style & Bypass) != 0 && pass >= 10 && type != 2;
                if (isRaw) raw ??= new RawBits(data);
                else mq ??= new MqDecoder(data, 0, data.Length);
                switch (type)
                {
                    case 0:
                        if (isRaw) SignificancePassRaw<TStore>(raw!, plane, vsc);
                        else SignificancePass<TStore>(mq!, zero, plane, vsc);
                        break;
                    case 1:
                        RefinementPass<TStore>(mq, raw, isRaw, plane, vsc);
                        break;
                    default:
                        CleanupPass<TStore>(mq!, zero, plane, vsc);
                        if ((style & SegmentationSymbols) != 0)
                        {
                            // the symbol 1010 in the UNIFORM context (D.5): another value means an error in this bit-plane — the block is drawn as
                            // decoded and counts as damaged (better than the standard asks: before the value was passed over; M2, T.800 D.5,
                            // 23.11.2026)
                            var symbol = 0;
                            for (var i = 0; i < 4; i++) symbol = symbol << 1 | mq!.Decode(_contexts, UniformContext);
                            if (symbol != 0b1010) Damaged = true;
                        }
                        break;
                }
                work += (long)width * height;
                if ((style & ResetContexts) != 0) ResetAll();
                if (type == 2)
                {
                    type = 0;
                    plane--;
                }
                else
                {
                    type++;
                }
            }
        }
        return work;
    }

    private void ResetAll() => Initial.CopyTo(_contexts, 0);

    /// <summary>The contexts after a reset, made once (copied for each code-block and pass, all 19 of them).</summary>
    private static readonly byte[] Initial = InitialContexts();

    /// <summary>The 19 contexts after a reset (ISO/IEC 15444-1, Table D.7), each as (index &lt;&lt; 1) | MPS.</summary>
    internal static byte[] InitialContexts()
    {
        var contexts = new byte[19];
        contexts[0] = 4 << 1;
        contexts[RunLengthContext] = 3 << 1;
        contexts[UniformContext] = 46 << 1;
        return contexts;
    }

    /// <summary>How the magnitudes are kept: 32 bits, or 64 bits with the region of an ROI counted from its shift.</summary>
    private interface IMagnitudes
    {
        /// <summary>The coefficient becomes significant in <paramref name="plane"/>.</summary>
        static abstract void Start(JpxBlockDecoder d, int i, int plane);

        /// <summary>The refinement bit of <paramref name="plane"/>: half a step up (1) or down (0).</summary>
        static abstract void Refine(JpxBlockDecoder d, int i, int plane, int bit);
    }

    private readonly struct Narrow : IMagnitudes
    {
        public static void Start(JpxBlockDecoder d, int i, int plane) => d._magnitude[i] = 3 << plane;

        public static void Refine(JpxBlockDecoder d, int i, int plane, int bit)
        {
            var step = 1 << plane;
            d._magnitude[i] += bit != 0 ? step : -step;
        }
    }

    /// <summary>
    /// 64-bit magnitudes. Planes from the ROI shift s up count for the region, as plane − s; a coefficient of the region refined below s keeps
    /// its half bit only if the first such bit is 1 — what scaling the whole magnitude down by s gives (<see cref="JpxTile"/> does so for
    /// 32-bit magnitudes). The background keeps its planes below s; one significant in plane 61 or above cannot be held (an ROI shift above
    /// 61 with background bits up there, which H.1 discards): the block counts as damaged.
    /// </summary>
    private readonly struct Wide : IMagnitudes
    {
        public static void Start(JpxBlockDecoder d, int i, int plane)
        {
            var s = d._split;
            if (plane >= s)
            {
                d._wide[i] = 3L << (plane - s);
                if (s > 0) d._flags[i] |= Region;
            }
            else if (plane <= 60) d._wide[i] = 3L << plane;
            else d.Damaged = true;
        }

        public static void Refine(JpxBlockDecoder d, int i, int plane, int bit)
        {
            var s = d._split;
            if (plane >= s)
            {
                var step = 1L << (plane - s);
                d._wide[i] += bit != 0 ? step : -step;
            }
            else if ((d._flags[i] & Region) != 0)
            {
                if ((d._flags[i] & RegionLow) != 0) return;
                d._flags[i] |= RegionLow;
                if (bit == 0) d._wide[i]--;
            }
            else if (plane <= 60)
            {
                var step = 1L << plane;
                d._wide[i] += bit != 0 ? step : -step;
            }
        }
    }

    /// <summary>The coefficient becomes significant: set its own flags and magnitude, and register with the 8 neighbours.</summary>
    private void Become<TStore>(int i, bool negative, int plane) where TStore : struct, IMagnitudes
    {
        var f = _flags;
        var s = _stride;
        f[i] |= Significant | (negative ? Negative : 0);
        TStore.Start(this, i, plane);
        f[i - 1] |= E | (negative ? SignE : 0);
        f[i + 1] |= W | (negative ? SignW : 0);
        f[i - s] |= S | (negative ? SignS : 0);
        f[i + s] |= N | (negative ? SignN : 0);
        f[i - s - 1] |= SE;
        f[i - s + 1] |= SW;
        f[i + s - 1] |= NE;
        f[i + s + 1] |= NW;
    }

    private int DecodeSign(MqDecoder mq, int flags)
    {
        // Index: significance of W, E, N, S (bits 0–3) and their signs (bits 4–7)
        var index = (flags >> NeighborShift & 0xF) | (flags >> SignShift & 0xF) << 4;
        var entry = SignContexts[index];
        return mq.Decode(_contexts, entry & 0x1F) ^ (entry >> 7);
    }

    private void SignificancePass<TStore>(MqDecoder mq, byte[] zero, int plane, bool vsc) where TStore : struct, IMagnitudes
    {
        var f = _flags;
        var s = _stride;
        for (var y0 = 0; y0 < Height; y0 += 4)
        {
            var rows = Math.Min(4, Height - y0);
            for (var x = 0; x < Width; x++)
            {
                var i = (y0 + 1) * s + x + 1;
                for (var r = 0; r < rows; r++, i += s)
                {
                    var flags = f[i];
                    if (vsc && r == 3) flags &= ~South;
                    // Not significant, but at least one significant neighbour
                    if ((flags & Significant) != 0 || (flags & 0xFF0) == 0) continue;
                    f[i] |= Visited;
                    if (mq.Decode(_contexts, zero[flags >> NeighborShift & 0xFF]) == 0) continue;
                    Become<TStore>(i, DecodeSign(mq, flags) != 0, plane);
                }
            }
        }
    }

    private void SignificancePassRaw<TStore>(RawBits raw, int plane, bool vsc) where TStore : struct, IMagnitudes
    {
        var f = _flags;
        var s = _stride;
        for (var y0 = 0; y0 < Height; y0 += 4)
        {
            var rows = Math.Min(4, Height - y0);
            for (var x = 0; x < Width; x++)
            {
                var i = (y0 + 1) * s + x + 1;
                for (var r = 0; r < rows; r++, i += s)
                {
                    var flags = f[i];
                    if (vsc && r == 3) flags &= ~South;
                    if ((flags & Significant) != 0 || (flags & 0xFF0) == 0) continue;
                    f[i] |= Visited;
                    if (raw.Bit() == 0) continue;
                    Become<TStore>(i, raw.Bit() != 0, plane);
                }
            }
        }
    }

    private void RefinementPass<TStore>(MqDecoder? mq, RawBits? raw, bool isRaw, int plane, bool vsc) where TStore : struct, IMagnitudes
    {
        var f = _flags;
        var s = _stride;
        for (var y0 = 0; y0 < Height; y0 += 4)
        {
            var rows = Math.Min(4, Height - y0);
            for (var x = 0; x < Width; x++)
            {
                var i = (y0 + 1) * s + x + 1;
                for (var r = 0; r < rows; r++, i += s)
                {
                    var flags = f[i];
                    if ((flags & (Significant | Visited)) != Significant) continue;
                    int bit;
                    if (isRaw)
                    {
                        bit = raw!.Bit();
                    }
                    else
                    {
                        if (vsc && r == 3) flags &= ~South;
                        var context = (flags & Refined) != 0 ? 16 : (flags & 0xFF0) != 0 ? 15 : 14;
                        bit = mq!.Decode(_contexts, context);
                    }
                    // Half bit: the centre of the open interval moves up or down by half a step
                    TStore.Refine(this, i, plane, bit);
                    f[i] |= Refined;
                }
            }
        }
    }

    private void CleanupPass<TStore>(MqDecoder mq, byte[] zero, int plane, bool vsc) where TStore : struct, IMagnitudes
    {
        var f = _flags;
        var s = _stride;
        for (var y0 = 0; y0 < Height; y0 += 4)
        {
            var rows = Math.Min(4, Height - y0);
            for (var x = 0; x < Width; x++)
            {
                var i0 = (y0 + 1) * s + x + 1;
                var r = 0;
                if (rows == 4 && (f[i0] | f[i0 + s] | f[i0 + 2 * s] | (vsc ? f[i0 + 3 * s] & ~South : f[i0 + 3 * s])) == 0)
                {
                    // Run-length mode: four insignificant, unvisited coefficients without significant neighbours
                    if (mq.Decode(_contexts, RunLengthContext) == 0) continue;
                    r = mq.Decode(_contexts, UniformContext) << 1;
                    r |= mq.Decode(_contexts, UniformContext);
                    var i = i0 + r * s;
                    var flags = f[i];
                    if (vsc && r == 3) flags &= ~South;
                    Become<TStore>(i, DecodeSign(mq, flags) != 0, plane);
                    r++;
                }
                for (var i = i0 + r * s; r < rows; r++, i += s)
                {
                    var flags = f[i];
                    if ((flags & (Significant | Visited)) != 0) continue;
                    if (vsc && r == 3) flags &= ~South;
                    if (mq.Decode(_contexts, zero[flags >> NeighborShift & 0xFF]) == 0) continue;
                    Become<TStore>(i, DecodeSign(mq, flags) != 0, plane);
                }
            }
        }
        // Plane finished: clear the flag "already handled in this plane"
        for (var y = 0; y < Height; y++)
        {
            var row = (y + 1) * s + 1;
            for (var x = 0; x < Width; x++) f[row + x] &= ~Visited;
        }
    }

    private static byte[][] BuildZeroContexts()
    {
        var tables = new byte[3][];
        for (var t = 0; t < 3; t++)
        {
            tables[t] = new byte[256];
            for (var n = 0; n < 256; n++)
            {
                var h = (n & 1) + (n >> 1 & 1);
                var v = (n >> 2 & 1) + (n >> 3 & 1);
                var d = (n >> 4 & 1) + (n >> 5 & 1) + (n >> 6 & 1) + (n >> 7 & 1);
                tables[t][n] = t switch
                {
                    0 => Context(h, v, d),
                    1 => Context(v, h, d),   // HL: horizontal and vertical swapped
                    _ => DiagonalContext(h + v, d),
                };
            }
        }
        return tables;

        // ISO/IEC 15444-1, Table D.1, LL and LH
        static byte Context(int h, int v, int d) => (byte)(h == 2 ? 8
            : h == 1 ? (v >= 1 ? 7 : d >= 1 ? 6 : 5)
            : v == 2 ? 4 : v == 1 ? 3 : d >= 2 ? 2 : d);

        // HH
        static byte DiagonalContext(int hv, int d) => (byte)(d >= 3 ? 8
            : d == 2 ? (hv >= 1 ? 7 : 6)
            : d == 1 ? (hv >= 2 ? 5 : hv == 1 ? 4 : 3)
            : hv >= 2 ? 2 : hv);
    }

    private static byte[] BuildSignContexts()
    {
        var table = new byte[256];
        for (var index = 0; index < 256; index++)
        {
            static int Contribution(int index, int k) => (index >> k & 1) == 0 ? 0 : (index >> (4 + k) & 1) != 0 ? -1 : 1;
            var h = Math.Clamp(Contribution(index, 0) + Contribution(index, 1), -1, 1);
            var v = Math.Clamp(Contribution(index, 2) + Contribution(index, 3), -1, 1);
            // ISO/IEC 15444-1, Table D.3: context and XOR bit
            int context, xor;
            if (h == 0)
            {
                context = v == 0 ? 9 : 10;
                xor = v < 0 ? 1 : 0;
            }
            else
            {
                context = 12 + h * v;   // same direction 13, opposite 11, v = 0 → 12
                xor = h < 0 ? 1 : 0;
            }
            table[index] = (byte)(context | xor << 7);
        }
        return table;
    }

    /// <summary>Raw bits (bypass, ISO/IEC 15444-1, D.6): after 0xFF the next byte carries only 7 bits. Beyond the end ones, as in the MQ decoder.</summary>
    private sealed class RawBits(byte[] data)
    {
        private int _pos;
        private int _byte;
        private int _count;

        public int Bit()
        {
            if (_count == 0)
            {
                var previous = _byte;
                _byte = _pos < data.Length ? data[_pos] : 0xFF;
                _pos++;
                _count = previous == 0xFF ? 7 : 8;
            }
            _count--;
            return _byte >> _count & 1;
        }
    }
}
