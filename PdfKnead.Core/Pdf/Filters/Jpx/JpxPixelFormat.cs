namespace PdfKnead.Core.Pdf.Filters.Jpx;

/// <summary>
/// The Pixel Format box of ISO/IEC 15444-2, M.11.7.8 (Table M.35bis): how the bit pattern of a channel is read as a number. The reconstructed
/// sample, as a two's complement integer of the channel's bit depth, is reinterpreted: as an integer (format 0, what the decoder does without
/// the box), as a fixed point number with f fractional bits, as a floating point number with one sign bit, m mantissa bits and the remaining
/// bits for the exponent, or as the mantissa and the exponent of a number split over two channels (V = mantissa · 2^(exponent − 136)). The
/// value 1.0 is the full intensity of the colour (M.2.8); values outside 0 to 1 are clamped — the standard leaves them to the implementation
/// (docs/FEHLERGEDAECHTNIS.md, No. 969).
/// </summary>
internal static class JpxPixelFormat
{
    /// <summary>The kinds of Table M.35bis, in the top four bits of the format.</summary>
    public const int Integer = 0, Mantissa = 1, Exponent = 2, FixedPoint = 3, FloatingPoint = 4;

    /// <summary>The kind of a format.</summary>
    public static int Kind(int format) => format >> 12;

    /// <summary>Whether a format value is one of Table M.35bis (the others are reserved).</summary>
    public static bool Known(int format) => Kind(format) switch
    {
        Integer or Mantissa or Exponent => (format & 0x0FFF) == 0,
        FixedPoint or FloatingPoint => true,
        _ => false,
    };

    /// <summary>
    /// Whether a channel of <paramref name="bits"/> bits can carry the format: a floating point number needs its sign bit, at least one
    /// exponent bit and its mantissa bits; mantissa and exponent are integers of at most 30 bits.
    /// </summary>
    public static bool Fits(int format, int bits) => Kind(format) switch
    {
        FloatingPoint => bits <= 38 && bits - 1 - (format & 0x0FFF) >= 1,
        FixedPoint => bits <= 38,
        Mantissa or Exponent => bits <= 30,
        _ => true,
    };

    /// <summary>
    /// The number a reconstructed sample stands for in a fixed or floating point format: <paramref name="sample"/> is the integer of the
    /// channel (signed or not, within its <paramref name="bits"/> bits).
    /// </summary>
    public static double Value(long sample, int bits, int format)
    {
        var mask = bits >= 63 ? -1L : (1L << bits) - 1;
        switch (Kind(format))
        {
            case FixedPoint:
                // the integer divided by 2^f (Table M.35bis)
                return sample / Math.Pow(2, format & 0x0FFF);
            case FloatingPoint:
            {
                var pattern = sample & mask;
                var m = format & 0x0FFF;
                var e = bits - 1 - m;
                var negative = (pattern >> (bits - 1) & 1) != 0;
                // the exponent field has up to 37 bits (a 38-bit channel without mantissa bits, Fits): field, bias and the all-ones value in
                // 64 bits — in 32 bits the shifts wrapped from 32 exponent bits on, and bias, infinity and not a number came out wrong (M2,
                // T.801 M.11.7.8, 23.11.2026)
                var exponent = pattern >> m & ((1L << e) - 1);
                var mantissa = pattern & ((1L << m) - 1);
                var bias = (1L << (e - 1)) - 1;
                double v;
                if (exponent == (1L << e) - 1) v = mantissa == 0 ? double.PositiveInfinity : double.NaN;   // as ISO/IEC/IEEE 60559
                else if (exponent == 0) v = mantissa * Math.Pow(2, 1 - bias - m);                          // subnormal
                else v = (1 + mantissa / Math.Pow(2, m)) * Math.Pow(2, exponent - bias);
                return negative ? -v : v;
            }
            default:
                return sample;
        }
    }

    /// <summary>Whether a format makes the channel a fixed or floating point number, whose 1.0 is the full intensity (M.2.8, Table M.24bis).</summary>
    public static bool IsNumber(int format) => Kind(format) is FixedPoint or FloatingPoint;

    /// <summary>A number split over a mantissa and an exponent channel (Table M.35bis): V = mantissa · 2^(exponent − 136).</summary>
    public static double Combined(long mantissa, long exponent) => mantissa * Math.Pow(2, exponent - 136);

    /// <summary>The value as a level of <paramref name="max"/> steps: 0 to 1 is the range of the colour, outside clamped, not a number is 0.</summary>
    public static ushort Level(double value, int max) =>
        double.IsNaN(value) || value <= 0 ? (ushort)0 : value >= 1 ? (ushort)max : (ushort)Math.Round(value * max);

    /// <summary>
    /// The number of a signed channel (ISO/IEC 15444-2, Table M.24bis: MAX 1.0 for signed fixed and floating point channels) as 16 bits in
    /// the coding of a signed integer channel shifted by half its range: 32 768 + 32 767·x, so that x = (level − 2^15) / (2^15 − 1) — what
    /// the colour specifications read from signed channels. −1 … 1 is the range kept, beyond it clamped (M.2.8 leaves values out of range to
    /// the implementation), not a number is 0. Before the number was clamped to 0 … 1 like an unsigned one, and the negative half was lost
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1067).
    /// </summary>
    public static ushort SignedLevel(double value) =>
        double.IsNaN(value) ? (ushort)32768 : (ushort)(32768 + Math.Round(Math.Clamp(value, -1, 1) * 32767));

    /// <summary>The bit depth of the level a fixed or floating point channel is stored as (<see cref="Level"/>, <see cref="SignedLevel"/>).</summary>
    public static int LevelBits(int depth, bool signed) => signed ? 16 : Math.Min(depth, 16);
}
