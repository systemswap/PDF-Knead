using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PdfKnead.Core.Pdf.Filters.Jpeg;

/// <summary>
/// The inverse DCT of an 8 × 8 block (ITU-T T.81, A.3.3) with level shift and clamping to 0–255 (A.3.1, 8-bit samples): columns and rows
/// separately in integers, after the factorisation of Loeffler, Ligtenberg and Moschytz (1989) with constants of 13 fractional bits and 2
/// extra bits between the passes. Accurate to ±1 against the exact transform (TN 5116, section 22, asks the same of the reference
/// implementation).
/// </summary>
/// <remarks>
/// 32-bit arithmetic: the dequantised coefficients are limited to ±2^14 first — no valid stream reaches 2^12 (a quantisation step larger than
/// twice a coefficient rounds it to 0) —, so pass 1 cannot overflow; in pass 2 only crafted values can wrap, which gives wrong pixels and nothing
/// else (C# integer arithmetic is defined, every write lies inside the block). With AVX2 eight columns and, after a transpose, eight rows at once
/// with the same integer operations per lane — bit for bit the same result (JpegSimdTests); the clamping to 0–255 then by saturating packs.
/// Speed (decided on 12.11.2026: no step back against the codec before, ARBEITSWEISE Grundsatz 14 d): with AVX2 the decoder is about
/// as fast as libjpeg-turbo was through the codec.
/// </remarks>
internal static class JpegIdct
{
    private const int ConstBits = 13;
    private const int Pass1Bits = 2;
    private const int F0298631336 = 2446, F0390180644 = 3196, F0541196100 = 4433, F0765366865 = 6270, F0899976223 = 7373, F1175875602 = 9633,
        F1501321110 = 12299, F1847759065 = 15137, F1961570560 = 16069, F2053119869 = 16819, F2562915447 = 20995, F3072711026 = 25172;

    /// <summary>0–255; never a table index from computed values (crafted data cannot reach memory outside a table).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Byte(int v) => (byte)Math.Clamp(v, 0, 255);

    // valid 8-bit data never brings a dequantised coefficient above 2^12 (a step larger than twice the coefficient rounds it to 0);
    // inputs are limited to ±2^14 so that 32-bit sums of pass 1 cannot overflow; pass 2 of crafted data may wrap (wrong pixels, no harm)
    private const int Limit = 1 << 14;

    /// <summary>
    /// Transforms <paramref name="coefSpan"/> (64 dequantised coefficients, natural order) and writes the 8 × 8 samples to
    /// <paramref name="outputSpan"/> at <paramref name="offset"/> with row distance <paramref name="stride"/>.
    /// </summary>
    /// <param name="simd">False: the scalar way only (tests compare both).</param>
    /// <exception cref="ArgumentOutOfRangeException">The block does not fit into the output (never for the decoder's own planes).</exception>
    [SkipLocalsInit]
    public static unsafe void Transform(ReadOnlySpan<int> coefSpan, Span<byte> outputSpan, int offset, int stride, bool simd = true)
    {
        // the pointer paths below write 8 rows of 8 bytes: checked once here instead of at every byte
        if (coefSpan.Length < 64 || offset < 0 || stride < 8 || offset + 7L * stride + 8 > outputSpan.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (simd && Avx2.IsSupported)
        {
            fixed (int* c = coefSpan)
            fixed (byte* o = outputSpan)
                TransformAvx2(c, o + offset, stride);
            return;
        }
        int* work = stackalloc int[64];
        fixed (int* coef = coefSpan)
        fixed (byte* output = outputSpan)
        {
            // the full factorisation for every column and row, without shortcuts for blocks without AC values: the same operations as the
            // AVX2 way, so that both give the same samples even for crafted values whose sums wrap (the shortcuts are exact only without that)
            const int n1 = ConstBits - Pass1Bits, h1 = 1 << (n1 - 1);
            for (var c = 0; c < 8; c++)
            {
                int* col = coef + c;
                int i0 = Math.Clamp(col[0], -Limit, Limit), i1 = Math.Clamp(col[8], -Limit, Limit), i2 = Math.Clamp(col[16], -Limit, Limit),
                    i3 = Math.Clamp(col[24], -Limit, Limit), i4 = Math.Clamp(col[32], -Limit, Limit), i5 = Math.Clamp(col[40], -Limit, Limit),
                    i6 = Math.Clamp(col[48], -Limit, Limit), i7 = Math.Clamp(col[56], -Limit, Limit);
                Butterfly(i0, i1, i2, i3, i4, i5, i6, i7, out var o0, out var o1, out var o2, out var o3, out var o4, out var o5, out var o6, out var o7);
                work[c] = (o0 + h1) >> n1;
                work[8 + c] = (o1 + h1) >> n1;
                work[16 + c] = (o2 + h1) >> n1;
                work[24 + c] = (o3 + h1) >> n1;
                work[32 + c] = (o4 + h1) >> n1;
                work[40 + c] = (o5 + h1) >> n1;
                work[48 + c] = (o6 + h1) >> n1;
                work[56 + c] = (o7 + h1) >> n1;
            }
            // pass 2 with rounding and the level shift by 128 in one constant, as the AVX2 way adds it
            const int n2 = ConstBits + Pass1Bits + 3, h2 = (1 << (n2 - 1)) + (128 << n2);
            for (var r = 0; r < 8; r++)
            {
                int* wr = work + r * 8;
                byte* row = output + offset + r * stride;
                Butterfly(wr[0], wr[1], wr[2], wr[3], wr[4], wr[5], wr[6], wr[7], out var o0, out var o1, out var o2, out var o3, out var o4, out var o5, out var o6, out var o7);
                row[0] = Byte((o0 + h2) >> n2);
                row[1] = Byte((o1 + h2) >> n2);
                row[2] = Byte((o2 + h2) >> n2);
                row[3] = Byte((o3 + h2) >> n2);
                row[4] = Byte((o4 + h2) >> n2);
                row[5] = Byte((o5 + h2) >> n2);
                row[6] = Byte((o6 + h2) >> n2);
                row[7] = Byte((o7 + h2) >> n2);
            }
        }
    }

    // the same factorisation on eight columns (pass 1) and, after a transpose, eight rows (pass 2) at once; identical results to the scalar
    // version (the same integer operations per lane), the clamping to 0–255 by saturating packs
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ButterflyV(Vector256<int> i0, Vector256<int> i1, Vector256<int> i2, Vector256<int> i3, Vector256<int> i4, Vector256<int> i5,
        Vector256<int> i6, Vector256<int> i7, out Vector256<int> o0, out Vector256<int> o1, out Vector256<int> o2, out Vector256<int> o3,
        out Vector256<int> o4, out Vector256<int> o5, out Vector256<int> o6, out Vector256<int> o7)
    {
        var z1 = Mul(i2 + i6, F0541196100);
        var t2 = z1 - Mul(i6, F1847759065);
        var t3 = z1 + Mul(i2, F0765366865);
        var t0 = Avx2.ShiftLeftLogical(i0 + i4, ConstBits);
        var t1 = Avx2.ShiftLeftLogical(i0 - i4, ConstBits);
        Vector256<int> t10 = t0 + t3, t13 = t0 - t3, t11 = t1 + t2, t12 = t1 - t2;
        Vector256<int> a0 = i7, a1 = i5, a2 = i3, a3 = i1;
        Vector256<int> y1 = a0 + a3, y2 = a1 + a2, y3 = a0 + a2, y4 = a1 + a3;
        var y5 = Mul(y3 + y4, F1175875602);
        a0 = Mul(a0, F0298631336);
        a1 = Mul(a1, F2053119869);
        a2 = Mul(a2, F3072711026);
        a3 = Mul(a3, F1501321110);
        y1 = Mul(y1, -F0899976223);
        y2 = Mul(y2, -F2562915447);
        y3 = Mul(y3, -F1961570560) + y5;
        y4 = Mul(y4, -F0390180644) + y5;
        a0 += y1 + y3;
        a1 += y2 + y4;
        a2 += y2 + y3;
        a3 += y1 + y4;
        o0 = t10 + a3; o7 = t10 - a3; o1 = t11 + a2; o6 = t11 - a2; o2 = t12 + a1; o5 = t12 - a1; o3 = t13 + a0; o4 = t13 - a0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Mul(Vector256<int> v, int c) => Avx2.MultiplyLow(v, Vector256.Create(c));

    /// <summary>Transposes eight rows of eight 32-bit values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(ref Vector256<int> r0, ref Vector256<int> r1, ref Vector256<int> r2, ref Vector256<int> r3, ref Vector256<int> r4,
        ref Vector256<int> r5, ref Vector256<int> r6, ref Vector256<int> r7)
    {
        var t0 = Avx2.UnpackLow(r0, r1); var t1 = Avx2.UnpackHigh(r0, r1);
        var t2 = Avx2.UnpackLow(r2, r3); var t3 = Avx2.UnpackHigh(r2, r3);
        var t4 = Avx2.UnpackLow(r4, r5); var t5 = Avx2.UnpackHigh(r4, r5);
        var t6 = Avx2.UnpackLow(r6, r7); var t7 = Avx2.UnpackHigh(r6, r7);
        var u0 = Avx2.UnpackLow(t0.AsInt64(), t2.AsInt64()).AsInt32(); var u1 = Avx2.UnpackHigh(t0.AsInt64(), t2.AsInt64()).AsInt32();
        var u2 = Avx2.UnpackLow(t1.AsInt64(), t3.AsInt64()).AsInt32(); var u3 = Avx2.UnpackHigh(t1.AsInt64(), t3.AsInt64()).AsInt32();
        var u4 = Avx2.UnpackLow(t4.AsInt64(), t6.AsInt64()).AsInt32(); var u5 = Avx2.UnpackHigh(t4.AsInt64(), t6.AsInt64()).AsInt32();
        var u6 = Avx2.UnpackLow(t5.AsInt64(), t7.AsInt64()).AsInt32(); var u7 = Avx2.UnpackHigh(t5.AsInt64(), t7.AsInt64()).AsInt32();
        r0 = Avx2.Permute2x128(u0, u4, 0x20); r4 = Avx2.Permute2x128(u0, u4, 0x31);
        r1 = Avx2.Permute2x128(u1, u5, 0x20); r5 = Avx2.Permute2x128(u1, u5, 0x31);
        r2 = Avx2.Permute2x128(u2, u6, 0x20); r6 = Avx2.Permute2x128(u2, u6, 0x31);
        r3 = Avx2.Permute2x128(u3, u7, 0x20); r7 = Avx2.Permute2x128(u3, u7, 0x31);
    }

    private static unsafe void TransformAvx2(int* coef, byte* output, int stride)
    {
        var limit = Vector256.Create(Limit);
        var low = Vector256.Create(-Limit);
        Vector256<int> c0 = Avx2.Min(Avx2.Max(Avx.LoadVector256(coef), low), limit), c1 = Avx2.Min(Avx2.Max(Avx.LoadVector256(coef + 8), low), limit),
            c2 = Avx2.Min(Avx2.Max(Avx.LoadVector256(coef + 16), low), limit), c3 = Avx2.Min(Avx2.Max(Avx.LoadVector256(coef + 24), low), limit),
            c4 = Avx2.Min(Avx2.Max(Avx.LoadVector256(coef + 32), low), limit), c5 = Avx2.Min(Avx2.Max(Avx.LoadVector256(coef + 40), low), limit),
            c6 = Avx2.Min(Avx2.Max(Avx.LoadVector256(coef + 48), low), limit), c7 = Avx2.Min(Avx2.Max(Avx.LoadVector256(coef + 56), low), limit);
        TransformLoaded(c0, c1, c2, c3, c4, c5, c6, c7, output, stride);
    }

    private static unsafe void TransformLoaded(Vector256<int> c0, Vector256<int> c1, Vector256<int> c2, Vector256<int> c3, Vector256<int> c4,
        Vector256<int> c5, Vector256<int> c6, Vector256<int> c7, byte* output, int stride)
    {
        const int n1 = ConstBits - Pass1Bits, n2 = ConstBits + Pass1Bits + 3;
        var h1 = Vector256.Create(1 << (n1 - 1));
        // pass 1: columns (lane = column)
        ButterflyV(c0, c1, c2, c3, c4, c5, c6, c7, out var w0, out var w1, out var w2, out var w3, out var w4, out var w5, out var w6, out var w7);
        w0 = Avx2.ShiftRightArithmetic(w0 + h1, n1); w1 = Avx2.ShiftRightArithmetic(w1 + h1, n1);
        w2 = Avx2.ShiftRightArithmetic(w2 + h1, n1); w3 = Avx2.ShiftRightArithmetic(w3 + h1, n1);
        w4 = Avx2.ShiftRightArithmetic(w4 + h1, n1); w5 = Avx2.ShiftRightArithmetic(w5 + h1, n1);
        w6 = Avx2.ShiftRightArithmetic(w6 + h1, n1); w7 = Avx2.ShiftRightArithmetic(w7 + h1, n1);
        // pass 2: rows (after the transpose, lane = row, vector k = column k)
        Transpose(ref w0, ref w1, ref w2, ref w3, ref w4, ref w5, ref w6, ref w7);
        ButterflyV(w0, w1, w2, w3, w4, w5, w6, w7, out var o0, out var o1, out var o2, out var o3, out var o4, out var o5, out var o6, out var o7);
        var h2 = Vector256.Create((1 << (n2 - 1)) + (128 << n2));   // rounding and the level shift by 128
        o0 = Avx2.ShiftRightArithmetic(o0 + h2, n2); o1 = Avx2.ShiftRightArithmetic(o1 + h2, n2);
        o2 = Avx2.ShiftRightArithmetic(o2 + h2, n2); o3 = Avx2.ShiftRightArithmetic(o3 + h2, n2);
        o4 = Avx2.ShiftRightArithmetic(o4 + h2, n2); o5 = Avx2.ShiftRightArithmetic(o5 + h2, n2);
        o6 = Avx2.ShiftRightArithmetic(o6 + h2, n2); o7 = Avx2.ShiftRightArithmetic(o7 + h2, n2);
        // back to rows, then 0–255 by saturation and stored
        Transpose(ref o0, ref o1, ref o2, ref o3, ref o4, ref o5, ref o6, ref o7);
        Store(o0, o1, output, stride, 0);
        Store(o2, o3, output, stride, 2);
        Store(o4, o5, output, stride, 4);
        Store(o6, o7, output, stride, 6);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Store(Vector256<int> a, Vector256<int> b, byte* output, int stride, int row)
    {
        // a = row r, b = row r + 1: to 16-bit (per 128-bit half: a0-3 b0-3 | a4-7 b4-7), then to bytes with saturation (0–255)
        var s = Avx2.PackSignedSaturate(a, b);
        var bytes = Avx2.PackUnsignedSaturate(s, s);
        var lo = bytes.GetLower().AsUInt32();
        var hi = bytes.GetUpper().AsUInt32();
        *(uint*)(output + row * stride) = lo.GetElement(0);
        *(uint*)(output + row * stride + 4) = hi.GetElement(0);
        *(uint*)(output + (row + 1) * stride) = lo.GetElement(1);
        *(uint*)(output + (row + 1) * stride + 4) = hi.GetElement(1);
    }

    /// <summary>
    /// The block as decoded (quantised, 16 bits, natural order) with its table: dequantised while it is loaded, then as <see cref="Transform"/>
    /// at full size. Without AVX2 through the scalar version.
    /// </summary>
    /// <remarks>The caller guarantees 64 coefficients, 64 table entries and 8 rows of 8 bytes at <paramref name="output"/> (the decoder: a block
    /// inside its plane, checked by the block position before the call).</remarks>
    [SkipLocalsInit]
    public static unsafe void TransformQuantised(short* block, int* quant, byte* output, int stride, bool simd = true)
    {
        if (!simd || !Avx2.IsSupported)
        {
            int* coef = stackalloc int[64];
            for (var k = 0; k < 64; k++) coef[k] = block[k] * quant[k];
            Transform(new ReadOnlySpan<int>(coef, 64), new Span<byte>(output, 7 * stride + 8), 0, stride, simd: false);
            return;
        }
        var limit = Vector256.Create(Limit);
        var low = Vector256.Create(-Limit);
        Vector256<int> L(int r) => Avx2.Min(Avx2.Max(Avx2.MultiplyLow(Avx2.ConvertToVector256Int32(block + 8 * r), Avx.LoadVector256(quant + 8 * r)), low), limit);
        TransformLoaded(L(0), L(1), L(2), L(3), L(4), L(5), L(6), L(7), output, stride);
    }

    /// <summary>The quantised block with its table into <paramref name="output"/> at <paramref name="offset"/> (span form, bounds checked).</summary>
    internal static unsafe void TransformQuantised(ReadOnlySpan<short> block, ReadOnlySpan<int> quant, Span<byte> output, int offset, int stride, bool simd = true)
    {
        if (block.Length < 64 || quant.Length < 64 || offset < 0 || stride < 8 || offset + 7L * stride + 8 > output.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        fixed (short* b = block) fixed (int* q = quant) fixed (byte* o = output) TransformQuantised(b, q, o + offset, stride, simd);
    }

    /// <summary>The DC value alone as the mean of the block: what <see cref="Transform"/> averages to, for decoding at 1/8.</summary>
    public static byte DcOnly(int dc) => (byte)Math.Clamp(((long)dc + 4 >> 3) + 128, 0, 255);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Butterfly(int i0, int i1, int i2, int i3, int i4, int i5, int i6, int i7,
        out int o0, out int o1, out int o2, out int o3, out int o4, out int o5, out int o6, out int o7)
    {
        var z1 = (i2 + i6) * F0541196100;
        var t2 = z1 - i6 * F1847759065;
        var t3 = z1 + i2 * F0765366865;
        var t0 = (i0 + i4) << ConstBits;
        var t1 = (i0 - i4) << ConstBits;
        int t10 = t0 + t3, t13 = t0 - t3, t11 = t1 + t2, t12 = t1 - t2;
        int a0 = i7, a1 = i5, a2 = i3, a3 = i1;
        int y1 = a0 + a3, y2 = a1 + a2, y3 = a0 + a2, y4 = a1 + a3;
        var y5 = (y3 + y4) * F1175875602;
        a0 *= F0298631336;
        a1 *= F2053119869;
        a2 *= F3072711026;
        a3 *= F1501321110;
        y1 *= -F0899976223;
        y2 *= -F2562915447;
        y3 = y3 * -F1961570560 + y5;
        y4 = y4 * -F0390180644 + y5;
        a0 += y1 + y3;
        a1 += y2 + y4;
        a2 += y2 + y3;
        a3 += y1 + y4;
        o0 = t10 + a3; o7 = t10 - a3; o1 = t11 + a2; o6 = t11 - a2; o2 = t12 + a1; o5 = t12 - a1; o3 = t13 + a0; o4 = t13 - a0;
    }
}
