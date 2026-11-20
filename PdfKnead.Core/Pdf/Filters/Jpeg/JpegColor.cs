using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PdfKnead.Core.Pdf.Filters.Jpeg;

/// <summary>
/// From the planes of the components to the image: upsampling of subsampled components and the colour transform of the Adobe marker or
/// /ColorTransform (ISO 32000-2, 7.4.8, Table 13; Adobe TN 5116, section 13).
/// </summary>
/// <remarks>
/// Upsampling is not part of ITU-T T.81. Twofold steps are interpolated: a chroma sample stands between the luma samples it covers
/// (ITU-T T.871, JFIF, centred siting), each output sample takes three quarters of the nearer and one quarter of the farther one in each
/// direction ("triangle filter") — what common decoders do, so that photos keep their soft colour edges. Other ratios repeat the
/// samples, as TN 5116, section 15, describes for its own decoder.
/// With SSE4.1 and AVX2 the twofold steps and the colour transform run 8 samples at a time with the same integer operations — bit for bit the
/// scalar result (JpegSimdTests, through the <c>simd</c> argument). Every pointer access stays inside its row: plane rows hold at least the
/// image width (whole blocks), row buffers 16 bytes more than it, and the inputs are bytes, so the indices into the tables cannot leave them.
/// </remarks>
internal static class JpegColor
{
    // YCbCr → RGB (TN 5116, 13.2; T.871, 7): 16 fractional bits, rounded
    private static readonly int[] CrR = new int[256], CbB = new int[256], CrG = new int[256], CbG = new int[256];

    static JpegColor()
    {
        static int Fix(double v) => (int)Math.Round(v * 65536);
        for (var i = 0; i < 256; i++)
        {
            var x = i - 128;
            CrR[i] = (Fix(1.4020) * x + 32768) >> 16;
            CbB[i] = (Fix(1.772) * x + 32768) >> 16;
            CrG[i] = -Fix(0.71413636) * x;
            CbG[i] = -Fix(0.3441363) * x + 32768;
        }
    }

    private static byte Clamp(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;

    /// <summary>0–255 for −256 … 511 (index + 256): the colour sums never leave that range.</summary>
    private static readonly byte[] Range = Enumerable.Range(-256, 768).Select(v => (byte)Math.Clamp(v, 0, 255)).ToArray();

    /// <summary>One component's samples at its own resolution.</summary>
    /// <param name="Data">Samples, rows of <paramref name="Stride"/> bytes.</param>
    /// <param name="Stride">Row distance.</param>
    /// <param name="Width">Samples per row that belong to the image.</param>
    /// <param name="Height">Rows that belong to the image.</param>
    /// <param name="H">Horizontal sampling factor.</param>
    /// <param name="V">Vertical sampling factor.</param>
    internal readonly record struct Plane(byte[] Data, int Stride, int Width, int Height, int H, int V);

    /// <summary>
    /// The image, interleaved: every component upsampled to <paramref name="width"/> × <paramref name="height"/>, then transformed.
    /// </summary>
    /// <param name="planes">The components.</param>
    /// <param name="hMax">Largest horizontal sampling factor.</param>
    /// <param name="vMax">Largest vertical sampling factor.</param>
    /// <param name="transform">The colour transform.</param>
    public static unsafe byte[] Assemble(Plane[] planes, int hMax, int vMax, int width, int height, JpegTransform transform)
    {
        var n = planes.Length;
        var output = new byte[(long)width * height * n];
        var buffers = new byte[n][];
        for (var c = 0; c < n; c++) buffers[c] = new byte[width + 16];
        var sums = new short[planes.Max(p => p.Width) + 16];
        // components at full resolution are read from their plane directly, the others upsampled into a row buffer
        var direct = new bool[n];
        for (var c = 0; c < n; c++) direct[c] = planes[c].H == hMax && planes[c].V == vMax;
        var rows = new byte[n][];
        var offsets = new int[n];
        fixed (byte* range = Range)
        fixed (int* crR = CrR, cbB = CbB, crG = CrG, cbG = CbG)
        {
            var clamp = range + 256;
            for (var y = 0; y < height; y++)
            {
                for (var c = 0; c < n; c++)
                {
                    var p = planes[c];
                    if (direct[c])
                    {
                        rows[c] = p.Data;
                        offsets[c] = Math.Min(y, p.Height - 1) * p.Stride;
                    }
                    else
                    {
                        Row(p, hMax, vMax, y, width, buffers[c], sums);
                        rows[c] = buffers[c];
                        offsets[c] = 0;
                    }
                }
                var line = (long)y * width * n;
                if (n == 1)
                {
                    rows[0].AsSpan(offsets[0], width).CopyTo(output.AsSpan((int)line, width));
                    continue;
                }
                fixed (byte* o0 = &output[line], r0 = &rows[0][offsets[0]], r1 = &rows[1][offsets[1]])
                {
                    byte* r2 = null, r3 = null;
                    var keep2 = n > 2 ? rows[2] : null;
                    var keep3 = n > 3 ? rows[3] : null;
                    fixed (byte* p2 = keep2, p3 = keep3)
                    {
                        if (n > 2) r2 = p2 + offsets[2];
                        if (n > 3) r3 = p3 + offsets[3];
                        var o = o0;
                        switch (transform)
                        {
                            case JpegTransform.YCbCr when n == 3:
                                YccToRgb(r0, r1, r2, o, width);
                                break;
                            case JpegTransform.YCbCr when n == 4:
                                for (var x = 0; x < width; x++, o += 4)
                                {
                                    int yy = r0[x], cb = r1[x], cr = r2[x];
                                    o[0] = clamp[yy + crR[cr]];
                                    o[1] = clamp[yy + ((cbG[cb] + crG[cr]) >> 16)];
                                    o[2] = clamp[yy + cbB[cb]];
                                    o[3] = r3[x];
                                }
                                break;
                            case JpegTransform.Ycck when n == 4:
                                // TN 5116, 13.1: Y, Cb, Cr were made from 255 − C, 255 − M, 255 − Y; K passed through
                                for (var x = 0; x < width; x++, o += 4)
                                {
                                    int yy = r0[x], cb = r1[x], cr = r2[x];
                                    o[0] = (byte)(255 - clamp[yy + crR[cr]]);
                                    o[1] = (byte)(255 - clamp[yy + ((cbG[cb] + crG[cr]) >> 16)]);
                                    o[2] = (byte)(255 - clamp[yy + cbB[cb]]);
                                    o[3] = r3[x];
                                }
                                break;
                            default:
                                for (var x = 0; x < width; x++, o += n)
                                {
                                    o[0] = r0[x];
                                    o[1] = r1[x];
                                    if (n > 2) o[2] = r2[x];
                                    if (n > 3) o[3] = r3[x];
                                }
                                break;
                        }
                    }
                }
            }
        }
        return output;
    }

    /// <summary>YCbCr → RGB for one row of <paramref name="width"/> pixels; <paramref name="simd"/> false: the scalar way only (tests).</summary>
    internal static unsafe void YccToRgb(byte* yRow, byte* cbRow, byte* crRow, byte* o, int width, bool simd = true)
    {
        var x0 = simd && Avx2.IsSupported ? YccToRgbAvx2(yRow, cbRow, crRow, o, width) : 0;
        fixed (byte* range = Range)
        fixed (int* crR = CrR, cbB = CbB, crG = CrG, cbG = CbG)
        {
            var clamp = range + 256;
            var q = o + 3 * x0;
            for (var x = x0; x < width; x++, q += 3)
            {
                int yy = yRow[x], cb = cbRow[x], cr = crRow[x];
                q[0] = clamp[yy + crR[cr]];
                q[1] = clamp[yy + ((cbG[cb] + crG[cr]) >> 16)];
                q[2] = clamp[yy + cbB[cb]];
            }
        }
    }

    /// <summary>
    /// YCbCr → RGB for eight pixels at a time with the same integer operations as the tables (16 fractional bits, rounded, arithmetic
    /// shift), clamped by saturating packs; returns the pixels done (a multiple of 8), the rest goes the scalar way.
    /// </summary>
    [System.Runtime.CompilerServices.SkipLocalsInit]
    private static unsafe int YccToRgbAvx2(byte* yRow, byte* cbRow, byte* crRow, byte* o, int width)
    {
        var c128 = Vector256.Create(128);
        var half = Vector256.Create(32768);
        Vector256<int> kR = Vector256.Create(91881), kB = Vector256.Create(116130), kGb = Vector256.Create(-22554), kGr = Vector256.Create(-46802);
        byte* tmp = stackalloc byte[32];
        var x = 0;
        for (; x + 8 <= width; x += 8, o += 24)
        {
            var yy = Avx2.ConvertToVector256Int32(yRow + x);
            var xb = Avx2.ConvertToVector256Int32(cbRow + x) - c128;
            var xr = Avx2.ConvertToVector256Int32(crRow + x) - c128;
            var r = yy + Avx2.ShiftRightArithmetic(Avx2.MultiplyLow(xr, kR) + half, 16);
            var g = yy + Avx2.ShiftRightArithmetic(Avx2.MultiplyLow(xb, kGb) + half + Avx2.MultiplyLow(xr, kGr), 16);
            var bl = yy + Avx2.ShiftRightArithmetic(Avx2.MultiplyLow(xb, kB) + half, 16);
            // per 128-bit half: R0-3 G0-3 B0-3 B0-3 | R4-7 G4-7 B4-7 B4-7
            var bytes = Avx2.PackUnsignedSaturate(Avx2.PackSignedSaturate(r, g), Avx2.PackSignedSaturate(bl, bl));
            Avx.Store(tmp, bytes);
            for (var i = 0; i < 4; i++)
            {
                o[3 * i] = tmp[i];
                o[3 * i + 1] = tmp[4 + i];
                o[3 * i + 2] = tmp[8 + i];
                o[12 + 3 * i] = tmp[16 + i];
                o[12 + 3 * i + 1] = tmp[20 + i];
                o[12 + 3 * i + 2] = tmp[24 + i];
            }
        }
        return x;
    }

    // span forms of the row routines (bounds checked once, then the pointer forms): for callers without unsafe code and the tests

    /// <summary>YCbCr → RGB for <paramref name="width"/> pixels (span form of the pointer routine).</summary>
    internal static unsafe void YccToRgb(ReadOnlySpan<byte> y, ReadOnlySpan<byte> cb, ReadOnlySpan<byte> cr, Span<byte> rgb, int width, bool simd = true)
    {
        if (width < 0 || y.Length < width || cb.Length < width || cr.Length < width || rgb.Length < 3L * width) throw new ArgumentOutOfRangeException(nameof(width));
        fixed (byte* py = y, pb = cb, pr = cr, po = rgb) YccToRgb(py, pb, pr, po, width, simd);
    }

    /// <summary>Vertical sums (span form).</summary>
    internal static unsafe void VerticalSums(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<short> sums, int w, bool simd = true)
    {
        if (w < 0 || a.Length < w || b.Length < w || sums.Length < w) throw new ArgumentOutOfRangeException(nameof(w));
        fixed (byte* pa = a, pb = b) fixed (short* ps = sums) VerticalSums(pa, pb, ps, w, simd);
    }

    /// <summary>Widening to 16 bits (span form).</summary>
    internal static unsafe void Widen(ReadOnlySpan<byte> d, Span<short> sums, int w, bool simd = true)
    {
        if (w < 0 || d.Length < w || sums.Length < w) throw new ArgumentOutOfRangeException(nameof(w));
        fixed (byte* pd = d) fixed (short* ps = sums) Widen(pd, ps, w, simd);
    }

    /// <summary>Twofold horizontally (span form): <paramref name="w"/> ≥ 3 sums into 2 w samples.</summary>
    internal static unsafe void Twofold(ReadOnlySpan<short> s, Span<byte> o, int w, short even, short odd, byte shift, bool simd = true)
    {
        // the SIMD step reads 8 sums from i + 1 and writes 16 samples from 2i; it stops 8 sums before the end, so 2 w samples suffice
        if (w < 3 || s.Length < w || o.Length < 2L * w) throw new ArgumentOutOfRangeException(nameof(w));
        fixed (short* ps = s) fixed (byte* po = o) Twofold(ps, po, w, even, odd, shift, simd);
    }

    /// <summary>Three times the row <paramref name="a"/> plus the row <paramref name="b"/>, as 16-bit sums (at most 1 020).</summary>
    internal static unsafe void VerticalSums(byte* a, byte* b, short* sums, int w, bool simd = true)
    {
        var x0 = 0;
        if (simd && Sse41.IsSupported)
            for (; x0 + 8 <= w; x0 += 8)
            {
                var va = Sse41.ConvertToVector128Int16(a + x0);
                var vb = Sse41.ConvertToVector128Int16(b + x0);
                Sse2.Store(sums + x0, Sse2.Add(Sse2.Add(Sse2.ShiftLeftLogical(va, 1), va), vb));
            }
        for (var x = x0; x < w; x++) sums[x] = (short)(3 * a[x] + b[x]);
    }

    /// <summary>The samples of a row as 16-bit values.</summary>
    internal static unsafe void Widen(byte* d, short* sums, int w, bool simd = true)
    {
        var x0 = 0;
        if (simd && Sse41.IsSupported)
            for (; x0 + 8 <= w; x0 += 8) Sse2.Store(sums + x0, Sse41.ConvertToVector128Int16(d + x0));
        for (var x = x0; x < w; x++) sums[x] = d[x];
    }

    /// <summary>
    /// Twofold horizontally (<paramref name="w"/> ≥ 3 samples into 2 w): out 2i = (3 s[i] + s[i − 1] + <paramref name="even"/>) »
    /// <paramref name="shift"/>, out 2i + 1 = (3 s[i] + s[i + 1] + <paramref name="odd"/>) » <paramref name="shift"/>; at the edges the
    /// sample stands for its missing neighbour — the triangle filter of common decoders (for 4:2:2 with sums of 4, for 4:2:0 of 16).
    /// </summary>
    internal static unsafe void Twofold(short* s, byte* o, int w, short even, short odd, byte shift, bool simd = true)
    {
        o[0] = (byte)((4 * s[0] + even) >> shift);
        o[1] = (byte)((3 * s[0] + s[1] + odd) >> shift);
        var i = simd && Sse41.IsSupported ? UpsampleH2Sse(s, o, w, even, odd, shift) : 1;
        for (; i < w - 1; i++)
        {
            var t = 3 * s[i];
            o[2 * i] = (byte)((t + s[i - 1] + even) >> shift);
            o[2 * i + 1] = (byte)((t + s[i + 1] + odd) >> shift);
        }
        o[2 * w - 2] = (byte)((3 * s[w - 1] + s[w - 2] + even) >> shift);
        o[2 * w - 1] = (byte)((4 * s[w - 1] + odd) >> shift);
    }

    /// <summary>
    /// Twofold horizontally with 16-bit lanes (eight input samples per step): out 2i = (3 s[i] + s[i − 1] + <paramref name="even"/>) »
    /// <paramref name="shift"/>, out 2i + 1 = (3 s[i] + s[i + 1] + <paramref name="odd"/>) » <paramref name="shift"/> — the same sums as the
    /// scalar loop. Starts at i = 1; returns where the scalar loop goes on (the last sample and the rest that is not a whole step).
    /// </summary>
    private static unsafe int UpsampleH2Sse(short* s, byte* o, int w, short even, short odd, byte shift)
    {
        var ve = Vector128.Create(even);
        var vo = Vector128.Create(odd);
        // the shift differs per call (2 or 4): as a count in a register (psraw xmm, xmm) — the immediate form wants a constant and would
        // fall back to a slower path (CA1857); the result is the same for every count up to 15
        var count = Vector128.CreateScalar((short)shift);
        var i = 1;
        for (; i + 8 <= w - 1; i += 8)
        {
            var c = Sse2.LoadVector128(s + i);
            var l = Sse2.LoadVector128(s + i - 1);
            var r = Sse2.LoadVector128(s + i + 1);
            var t = Sse2.Add(Sse2.ShiftLeftLogical(c, 1), c);
            var e = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.Add(t, l), ve), count);
            var od = Sse2.ShiftRightArithmetic(Sse2.Add(Sse2.Add(t, r), vo), count);
            var bytes = Sse2.PackUnsignedSaturate(Sse2.UnpackLow(e, od), Sse2.UnpackHigh(e, od));
            Sse2.Store(o + 2 * i, bytes);
        }
        return i;
    }

    /// <summary>Row <paramref name="y"/> of the image from one component, upsampled to <paramref name="width"/> samples.</summary>
    private static unsafe void Row(Plane p, int hMax, int vMax, int y, int width, byte[] row, short[] sums)
    {
        int hf = hMax / p.H, vf = vMax / p.V;
        var integral = hMax % p.H == 0 && vMax % p.V == 0;
        var data = p.Data;
        int Last(int r) => Math.Clamp(r, 0, p.Height - 1);
        if (!integral)
        {
            // ratios like 3 : 2 (allowed by T.81, A.1.1): the nearest sample
            var r = Last((int)((long)y * p.V / vMax));
            for (var x = 0; x < width; x++) row[x] = data[r * p.Stride + Math.Min(p.Width - 1, (int)((long)x * p.H / hMax))];
            return;
        }
        if (hf == 1 && vf == 1)
        {
            data.AsSpan(Last(y) * p.Stride, Math.Min(width, p.Width)).CopyTo(row);
            return;
        }
        // twofold horizontally only from three samples on: narrower components repeat their samples, as common decoders do (an image of
        // 2–4 pixels with halved chroma otherwise differs from the display by up to 23 steps)
        var horizontal = hf == 2 && p.Width > 2;
        if (vf == 2 && (hf == 1 || horizontal))
        {
            // vertical triangle: three quarters of the own row, one quarter of the row above (even output rows) or below (odd)
            int r0 = Last(y >> 1), r1 = Last((y & 1) == 0 ? (y >> 1) - 1 : (y >> 1) + 1);
            var w = p.Width;
            fixed (byte* da = &data[r0 * p.Stride], db = &data[r1 * p.Stride], o = row)
            fixed (short* sm = sums)
            {
                VerticalSums(da, db, sm, w);
                if (hf == 1)
                {
                    var bias = (y & 1) == 0 ? 1 : 2;
                    var n1 = Math.Min(width, w);
                    for (var x = 0; x < n1; x++) o[x] = (byte)((sm[x] + bias) >> 2);
                    for (var x = n1; x < width; x++) o[x] = (byte)((sm[w - 1] + bias) >> 2);
                    return;
                }
                // and horizontally (w ≥ 3), sums of 16 with the biases 8 and 7; 2w ≥ width, the row has room
                Twofold(sm, o, w, 8, 7, 4);
            }
            return;
        }
        if (vf == 1 && horizontal)
        {
            var w = p.Width;
            fixed (byte* d = &data[Last(y) * p.Stride], o = row)
            fixed (short* sm = sums)
            {
                // the samples as 16-bit values, then the same step with sums of 4 and the biases 1 and 2 (at the edges: the sample itself)
                Widen(d, sm, w);
                Twofold(sm, o, w, 1, 2, 2);
            }
            return;
        }
        // other whole ratios (4 : 1, 3 : 1, …) and narrow components: repeated (TN 5116, section 15)
        {
            var a = Last(y / vf) * p.Stride;
            for (var x = 0; x < width; x++) row[x] = data[a + Math.Min(p.Width - 1, x / hf)];
        }
    }
}

/// <summary>The colour transform after decoding (ISO 32000-2, 7.4.8, Table 13).</summary>
internal enum JpegTransform
{
    /// <summary>The components as they are.</summary>
    None,

    /// <summary>YCbCr → RGB (three components).</summary>
    YCbCr,

    /// <summary>YCbCrK → CMYK (four components).</summary>
    Ycck,
}
