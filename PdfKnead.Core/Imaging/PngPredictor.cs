namespace PdfKnead.Core.Imaging;

/// <summary>
/// PNG predictors for writing (PDF /Predictor 15, ISO 32000-2, Table 10): every row gets its own filter (None, Sub,
/// Up, Average, Paeth), the one with the smallest sum of absolute values (the "MinSum" heuristic from libpng; the filter type is the first
/// byte of the row). Where areas are smooth, Flate compresses the deviations from the prediction much better than the values themselves.
/// </summary>
public static class PngPredictor
{
    /// <summary>
    /// Filters the rows and puts the filter type in front of each. The distance for Sub/Average/Paeth is one whole pixel rounded up to whole
    /// bytes, at least one byte — the same as the reading side (<c>Predictor.Apply</c>) and widely used readers; PDF allows pixels such as three
    /// components of 4 bits that PNG does not know, and rounding down there left the image corrupt after reading (ISO 32000-2, 7.4.4.4;
    /// docs/FEHLERGEDAECHTNIS.md, No. 728).
    /// </summary>
    /// <param name="samples">The samples, rows padded to whole bytes.</param>
    /// <param name="width">Width in px.</param>
    /// <param name="height">Height in px.</param>
    /// <param name="colors">Components per pixel.</param>
    /// <param name="bitsPerComponent">1, 2, 4, 8 or 16.</param>
    /// <returns>(row length + 1) × <paramref name="height"/> bytes.</returns>
    public static byte[] Encode(ReadOnlySpan<byte> samples, int width, int height, int colors, int bitsPerComponent)
    {
        var stride = (width * colors * bitsPerComponent + 7) / 8;
        var bpp = Math.Max(1, (colors * bitsPerComponent + 7) / 8);
        var output = new byte[(long)(stride + 1) * height];
        var trial = new byte[5][];
        for (var f = 0; f < 5; f++) trial[f] = new byte[stride];
        var zero = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var row = samples.Slice(y * stride, stride);
            var up = y > 0 ? samples.Slice((y - 1) * stride, stride) : zero;
            var best = 0;
            var bestSum = long.MaxValue;
            for (var f = 0; f < 5; f++)
            {
                var t = trial[f];
                long sum = 0;
                for (var i = 0; i < stride; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = up[i], c = i >= bpp ? up[i - bpp] : 0;
                    var v = (byte)(row[i] - f switch
                    {
                        1 => a,
                        2 => b,
                        3 => (a + b) >> 1,
                        4 => Paeth(a, b, c),
                        _ => 0,
                    });
                    t[i] = v;
                    sum += v < 128 ? v : 256 - v;   // as a signed number
                    if (sum >= bestSum) break;
                }
                if (sum < bestSum)
                {
                    bestSum = sum;
                    best = f;
                }
            }
            var o = (long)y * (stride + 1);
            output[o] = (byte)best;
            trial[best].AsSpan().CopyTo(output.AsSpan((int)o + 1, stride));
        }
        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
