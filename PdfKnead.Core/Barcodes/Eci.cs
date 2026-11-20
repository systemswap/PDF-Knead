using System.Text;

namespace PdfKnead.Core.Barcodes;

/// <summary>
/// Character sets of Extended Channel Interpretation (AIM ECI, as listed in ISO/IEC 18004:2015 and ISO/IEC 16022:2006) and the reading of
/// byte data without one.
/// </summary>
internal static class Eci
{
    static Eci() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The character set of an ECI number; null if it names none (or one the program does not know).</summary>
    public static Encoding? Charset(int eci)
    {
        try
        {
            return eci switch
            {
                0 or 2 => Encoding.GetEncoding(437),
                1 or 3 => Encoding.Latin1,
                >= 4 and <= 18 and not 14 => Encoding.GetEncoding("iso-8859-" + (eci - 2)),
                20 => Encoding.GetEncoding("shift_jis"),
                21 => Encoding.GetEncoding(1250),
                22 => Encoding.GetEncoding(1251),
                23 => Encoding.GetEncoding(1252),
                24 => Encoding.GetEncoding(1256),
                25 => Encoding.BigEndianUnicode,
                26 => new UTF8Encoding(false),
                27 or 170 => Encoding.ASCII,
                28 => Encoding.GetEncoding("big5"),
                29 => Encoding.GetEncoding("gb18030"),
                30 => Encoding.GetEncoding("euc-kr"),
                _ => null,
            };
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Name of the character set for the display ("UTF-8", "ISO-8859-1" …).</summary>
    public static string Name(int eci) => Charset(eci)?.WebName.ToUpperInvariant() ?? "ECI " + eci;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Bytes without an ECI: UTF-8 if they are valid UTF-8 (what most generators write today), otherwise ISO-8859-1 (the default of the norms).
    /// </summary>
    public static (string Text, string Charset) Guess(byte[] bytes)
    {
        try
        {
            return (StrictUtf8.GetString(bytes), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(bytes), "ISO-8859-1");
        }
    }
}
