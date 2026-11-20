namespace PdfKnead.Core.Pdf.Filters.Jbig2;

/// <summary>Damaged or nonsensical JBIG2 data (caught in the decoder; the rest of the page is kept).</summary>
internal sealed class Jbig2Exception(string message) : PdfException(message);

/// <summary>
/// 1-bit image in JBIG2 style: 1 = black, rows padded to whole bytes (padding bits always 0),
/// most significant bit on the left. Reading outside the image returns 0.
/// </summary>
internal sealed class Jbig2Bitmap
{
    public const int Or = 0, And = 1, Xor = 2, Xnor = 3, Replace = 4;

    public readonly int Width;
    public readonly int Height;
    public readonly int Stride;
    public readonly byte[] Data;

    public Jbig2Bitmap(int width, int height, int defaultPixel = 0)
    {
        if (width < 0 || height < 0) throw new Jbig2Exception("JBIG2: Bildgröße");
        Stride = (width + 7) >> 3;
        StreamFilters.CheckSize((long)Stride * height);
        Width = width;
        Height = height;
        Data = new byte[Stride * height];
        if (defaultPixel != 0) Fill(1);
    }

    public Jbig2Bitmap(int width, int height, byte[] data)
    {
        Width = width;
        Height = height;
        Stride = (width + 7) >> 3;
        Data = data;
    }

    public void Fill(int value)
    {
        Data.AsSpan().Fill(value != 0 ? (byte)0xFF : (byte)0);
        if (value != 0) ClearPadding();
    }

    /// <summary>Clears the bits to the right of the image width (composition and comparisons rely on this).</summary>
    public void ClearPadding()
    {
        if ((Width & 7) == 0 || Stride == 0) return;
        var mask = (byte)(0xFF << (8 - (Width & 7)));
        for (var i = Stride - 1; i < Data.Length; i += Stride) Data[i] &= mask;
    }

    public int Get(int x, int y) =>
        (uint)x < (uint)Width && (uint)y < (uint)Height ? (Data[y * Stride + (x >> 3)] >> (7 - (x & 7))) & 1 : 0;

    public void Set(int x, int y) => Data[y * Stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));

    /// <summary>Returns a section; parts outside the image stay 0.</summary>
    public Jbig2Bitmap Crop(int x, int y, int width, int height)
    {
        var result = new Jbig2Bitmap(width, height);
        result.Compose(this, -x, -y, Replace);
        return result;
    }

    /// <summary>
    /// Composes <paramref name="source"/> at (x, y) with the combination operator (7.4.1.5: OR, AND, XOR,
    /// XNOR, REPLACE); parts outside are clipped. Works byte by byte: eight source bits at once
    /// per destination byte.
    /// </summary>
    public void Compose(Jbig2Bitmap source, long x, long y, int op)
    {
        // Compare first, then narrow to int: otherwise a position above int.MaxValue (crafted file) would become negative (found by fuzzing)
        if (x >= Width || y >= Height || x + source.Width <= 0 || y + source.Height <= 0) return;
        var x0 = (int)Math.Max(0, x);
        var x1 = (int)Math.Min(Width, x + source.Width);
        var y0 = (int)Math.Max(0, y);
        var y1 = (int)Math.Min(Height, y + source.Height);
        if (x0 >= x1 || y0 >= y1) return;
        int firstByte = x0 >> 3, lastByte = (x1 - 1) >> 3;
        var firstMask = (byte)(0xFF >> (x0 & 7));
        var lastMask = (byte)(0xFF << (7 - ((x1 - 1) & 7)));
        var src = source.Data;
        var srcStride = source.Stride;
        for (var dy = y0; dy < y1; dy++)
        {
            var srcRow = (int)(dy - y) * srcStride;
            var dstRow = dy * Stride;
            for (var b = firstByte; b <= lastByte; b++)
            {
                var mask = (byte)((b == firstByte ? firstMask : 0xFF) & (b == lastByte ? lastMask : 0xFF));
                var s = Gather(src, srcRow, srcStride, (int)(b * 8L - x));
                ref var d = ref Data[dstRow + b];
                d = op switch
                {
                    Or => (byte)(d | (s & mask)),
                    And => (byte)(d & (s | ~mask)),
                    Xor => (byte)(d ^ (s & mask)),
                    Xnor => (byte)(d ^ (~s & mask)),
                    _ => (byte)((d & ~mask) | (s & mask)),
                };
            }
        }
    }

    /// <summary>Eight source pixels from column <paramref name="sx"/> (which may lie to the left of the image) as one byte.</summary>
    private static int Gather(byte[] data, int rowOffset, int stride, int sx)
    {
        if (sx < 0) return sx <= -8 ? 0 : data[rowOffset] >> -sx;
        int i = sx >> 3, shift = sx & 7;
        var b0 = i < stride ? data[rowOffset + i] : 0;
        if (shift == 0) return b0;
        var b1 = i + 1 < stride ? data[rowOffset + i + 1] : 0;
        return (b0 << shift) | (b1 >> (8 - shift));
    }
}

/// <summary>Big-endian numbers from segment data, with bounds checks.</summary>
internal static class Jbig2Read
{
    public static int U8(byte[] d, int p, int end) => p < end ? d[p] : throw new Jbig2Exception("JBIG2: Daten zu kurz");

    public static int U16(byte[] d, int p, int end) => p + 2 <= end ? d[p] << 8 | d[p + 1] : throw new Jbig2Exception("JBIG2: Daten zu kurz");

    public static uint U32(byte[] d, int p, int end) =>
        p + 4 <= end ? (uint)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]) : throw new Jbig2Exception("JBIG2: Daten zu kurz");

    public static int I32(byte[] d, int p, int end) => (int)U32(d, p, end);

    public static int I8(byte[] d, int p, int end) => (sbyte)U8(d, p, end);

    /// <summary>Returns a count as int; beyond the limit it is nonsense and an error.</summary>
    public static int Count(uint value, int limit) => value <= limit ? (int)value : throw new Jbig2Exception("JBIG2: Anzahl zu groß");

    /// <summary>⌈log2 n⌉ (0 for n ≤ 1).</summary>
    public static int CeilLog2(long n)
    {
        var bits = 0;
        while ((1L << bits) < n) bits++;
        return bits;
    }
}
