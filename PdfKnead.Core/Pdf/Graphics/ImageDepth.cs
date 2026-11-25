using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Core.Pdf.Graphics;

/// <summary>
/// Samples of a depth between the allowed ones (ISO 32000-2, 8.9.5.1, Table 87: 1, 2, 4, 8 or 16 bits): read leniently at any depth from
/// 1 to 16, as a reference reader does, and written at the next allowed depth — 8 bits below 8, 16 above — with the same appearance
/// (docs/normabgleich/sonderregeln.md S-A A192; docs/FEHLERGEDAECHTNIS.md, No. 1134). The values of a colour space are scaled to the wider
/// range (v × (2^B − 1) / (2^b − 1), rounded; the map is strictly increasing, so a colour key keeps exactly the same samples); the indices of
/// an Indexed space stay as they are, an explicit /Decode widened so that every index stays the same.
/// </summary>
internal static class ImageDepth
{
    private static readonly PdfName BitsKey = PdfName.Get("BitsPerComponent"), DecodeKey = PdfName.Get("Decode"), MaskKey = PdfName.Get("Mask");

    /// <summary>A depth Table 87 allows.</summary>
    public static bool Allowed(int bpc) => bpc is 1 or 2 or 4 or 8 or 16;

    /// <summary>A depth the decoder reads: 1 to 16 bits.</summary>
    public static bool Readable(int bpc) => bpc is >= 1 and <= 16;

    /// <summary>The allowed depth a sample of <paramref name="bpc"/> bits is written with: 8 below 8 bits, 16 above.</summary>
    public static int Wider(int bpc) => bpc < 8 ? 8 : 16;

    /// <summary>
    /// The sample of <paramref name="bpc"/> bits (1–16) starting at bit <paramref name="bit"/>, from the most significant bit (7.4.4, 8.9.5.1);
    /// a sample that crosses a byte boundary is read from up to three bytes; bytes beyond the data count as 0.
    /// </summary>
    public static int Read(ReadOnlySpan<byte> data, long bit, int bpc)
    {
        var at = bit >> 3;
        if (at >= data.Length) return 0;
        var i = (int)at;
        var used = (int)(bit & 7) + bpc;   // bits from the start of the first byte up to the end of the sample
        if (used <= 8) return (data[i] >> (8 - used)) & ((1 << bpc) - 1);
        var bytes = (used + 7) >> 3;
        var value = 0;
        for (var k = 0; k < bytes; k++) value = value << 8 | (i + k < data.Length ? data[i + k] : 0);
        return (value >> (bytes * 8 - used)) & ((1 << bpc) - 1);
    }

    /// <summary>The sample as written at the wider depth: scaled, or as it stands for the indices of an Indexed space.</summary>
    public static int Widen(int value, int bpc, bool indexed)
    {
        if (indexed) return value;
        long max = (1 << bpc) - 1, top = (1 << Wider(bpc)) - 1;
        return (int)((value * top * 2 + max) / (2 * max));
    }

    /// <summary>One row of <paramref name="samples"/> samples of <paramref name="bpc"/> bits into <paramref name="wide"/> at <see cref="Wider"/> bits.</summary>
    public static void WidenRow(ReadOnlySpan<byte> row, Span<byte> wide, int samples, int bpc, bool indexed)
    {
        var to = Wider(bpc);
        for (var i = 0; i < samples; i++)
        {
            var v = Widen(Read(row, (long)i * bpc, bpc), bpc, indexed);
            if (to == 8) wide[i] = (byte)v;
            else
            {
                wide[2 * i] = (byte)(v >> 8);
                wide[2 * i + 1] = (byte)v;
            }
        }
    }

    /// <summary>The bytes of one row of <paramref name="samples"/> samples at <paramref name="bpc"/> bits.</summary>
    public static long RowBytes(long samples, int bpc) => (samples * bpc + 7) / 8;

    /// <summary>
    /// Every row of an image (<paramref name="width"/> × <paramref name="height"/>, <paramref name="n"/> components, rows filled up to a whole
    /// byte) at <see cref="Wider"/> bits; rows the data lacks count as 0, as the display reads them.
    /// </summary>
    public static byte[] Widen(ReadOnlySpan<byte> data, int width, int height, int n, int bpc, bool indexed)
    {
        var samples = (long)width * n;
        var inRow = RowBytes(samples, bpc);
        var outRow = RowBytes(samples, Wider(bpc));
        var result = new byte[outRow * height];
        for (var y = 0; y < height; y++)
        {
            var start = y * inRow;
            var row = start < data.Length ? data.Slice((int)start, (int)Math.Min(inRow, data.Length - start)) : [];
            WidenRow(row, result.AsSpan((int)(y * outRow), (int)outRow), (int)samples, bpc, indexed);
        }
        return result;
    }

    /// <summary>
    /// The number of components of an image colour space and whether it is Indexed, without the document (for writing: inline images and the
    /// spaces Table 87 allows by name or as an array); a missing space as the display reads it, one grey component; (0, false) where unknown.
    /// </summary>
    public static (int Components, bool Indexed) ComponentsOf(PdfObject space)
    {
        switch (space)
        {
            case PdfNull:
                return (1, false);
            case PdfName { Value: "DeviceGray" or "G" or "CalGray" }:
                return (1, false);
            case PdfName { Value: "DeviceRGB" or "RGB" or "CalRGB" or "Lab" }:
                return (3, false);
            case PdfName { Value: "DeviceCMYK" or "CMYK" }:
                return (4, false);
            case PdfArray { Count: > 0 } array when array.Get(0) is PdfName family:
                return family.Value switch
                {
                    "Indexed" or "I" => (1, true),
                    "CalGray" or "Separation" => (1, false),
                    "CalRGB" or "Lab" => (3, false),
                    "ICCBased" => array.Count > 1 && array.Get(1) is PdfStream icc && icc.Dictionary.GetInteger(PdfName.Get("N")) is >= 1 and <= 32 and var n
                        ? ((int)n, false) : (0, false),
                    "DeviceN" => array.Count > 1 && array.Get(1) is PdfArray { Count: >= 1 and <= 32 } names ? (names.Count, false) : (0, false),
                    _ => (0, false),
                };
            default:
                return (0, false);
        }
    }

    /// <summary>An image (not a stencil mask) whose /BitsPerComponent lies between the allowed ones and is readable (1–16).</summary>
    public static bool Odd(PdfDictionary dict) => dict.GetName(PdfName.Subtype)?.Value == "Image" && dict.GetBoolean(PdfName.Get("ImageMask")) != true &&
        dict.GetInteger(BitsKey) is { } bits && !Allowed((int)bits) && Readable((int)bits);

    /// <summary>
    /// An image whose /BitsPerComponent lies between the allowed ones, written with the next allowed depth (S-A A192): its samples at 8 or 16
    /// bits, unfiltered (the caller packs them), and <paramref name="dict"/> changed — filters gone, the depth, /Decode and a colour key as
    /// <see cref="Describe"/> sets them. Null where it is no such image or its samples cannot be had (a filter of an image format, an unknown
    /// colour space, damaged data): then it stays as stored.
    /// </summary>
    /// <param name="dict">The dictionary to be written (full keys); changed only where the result is not null.</param>
    /// <param name="data">The data as stored (decrypted, still filtered).</param>
    public static byte[]? Conforming(PdfDictionary dict, byte[] data)
    {
        if (!Odd(dict)) return null;
        var bpc = (int)dict.GetInteger(BitsKey)!.Value;
        var (n, indexed) = ComponentsOf(dict.Get(PdfName.Get("ColorSpace")));
        var width = dict.GetInteger(PdfName.Get("Width")) ?? 0;
        var height = dict.GetInteger(PdfName.Get("Height")) ?? 0;
        if (n == 0 || width <= 0 || height <= 0 || width * height > PdfImageDecoder.MaxPixels || width * n * height * 2 > Array.MaxLength) return null;
        Filters.StreamFilters.DecodeResult decoded;
        try
        {
            decoded = Filters.StreamFilters.Decode(data, dict, (int)Math.Min(int.MaxValue / 2, RowBytes(width * n, bpc) * height));
        }
        catch (Exception e) when (e is PdfException or InvalidDataException)
        {
            return null;
        }
        if (decoded.ImageFilter is not null) return null;
        var wide = Widen(decoded.Data, (int)width, (int)height, n, bpc, indexed);
        dict.Remove(PdfName.Filter);
        dict.Remove(PdfName.DecodeParms);
        dict.Remove(PdfName.Get("DL"));
        Describe(dict, bpc, indexed);
        return wide;
    }

    /// <summary>Rows of samples of <paramref name="bpc"/> bits handed on at <see cref="Wider"/> bits (blacking out reads them so).</summary>
    public sealed class WideRows(IRowSource rows, int samples, int bpc, bool indexed) : IRowSource, IDisposable
    {
        private readonly byte[] _raw = new byte[RowBytes(samples, bpc)];

        public bool Read(Span<byte> row)
        {
            var read = rows.Read(_raw);
            if (!read) Array.Clear(_raw);
            WidenRow(_raw, row, samples, bpc, indexed);
            return read;
        }

        public void Dispose() => (rows as IDisposable)?.Dispose();
    }

    /// <summary>
    /// The /Decode for the widened samples: for Indexed an explicit range widened (D0, D0 + (D1 − D0) × (2^B − 1) / (2^b − 1)), so that every
    /// index stays the same; otherwise as it is (the samples are scaled instead).
    /// </summary>
    public static double[]? Decode(double[]? decode, int bpc, bool indexed)
    {
        if (!indexed || decode is not { Length: >= 2 }) return decode;
        double max = (1 << bpc) - 1, top = (1 << Wider(bpc)) - 1;
        return [decode[0], decode[0] + (decode[1] - decode[0]) * top / max];
    }

    /// <summary>
    /// The dictionary of an image whose samples of <paramref name="bpc"/> bits were widened (<see cref="Widen(ReadOnlySpan{byte}, int, int, int, int, bool)"/>):
    /// /BitsPerComponent the wider depth; for Indexed an explicit /Decode widened so that every index stays the same; otherwise a colour key
    /// (/Mask as an array) scaled with the samples.
    /// </summary>
    public static void Describe(PdfDictionary dict, int bpc, bool indexed)
    {
        dict.Set(BitsKey, PdfInteger.Of(Wider(bpc)));
        if (indexed)
        {
            if (Decode(PdfFunction.Numbers(dict.Get(DecodeKey) as PdfArray), bpc, indexed: true) is { } d)
                dict.Set(DecodeKey, new PdfArray([new PdfReal(d[0]), new PdfReal(d[1])]));
            return;
        }
        if (dict.Get(MaskKey) is PdfArray key && PdfFunction.Numbers(key) is { } values)
        {
            var max = (1 << bpc) - 1;
            dict.Set(MaskKey, new PdfArray([.. values.Select(v => (PdfObject)PdfInteger.Of(Widen((int)Math.Clamp(Math.Round(v), 0, max), bpc, false)))]));
        }
    }
}
