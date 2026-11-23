using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// Signed components of the Parameterized colour specification (METH 5) mapped by the rule of T.801 M.11.7.2 „for all other colourspaces“:
/// d = Dzero + (Dmax − Dzero)·x / MAX, MAX = 2^(n−1) − 1 (Table M.24bis), Dmax the top code 2^n − 1, Dzero the code of no light — luma and the
/// R, G, B of the identity matrix from black, the colour differences from the achromatic 2^(n−1) (decided on 23.11.2026; before they were read
/// as the offset binary codes of ITU-T H.273, 0 as mid-grey; docs/FEHLERGEDAECHTNIS.md, No. 1076). The expected values are worked out apart
/// from the decoder from T.801 M.11.7.2, H.273 (equations 23–38, 45–47, 62–65; Table 3) and the sRGB curve (IEC 61966-2-1).
/// </summary>
public sealed partial class JpxColourTests
{
    /// <summary>The BT.709 transfer characteristic inverted (H.273 Table 3, value 1: α and β of continuous value and slope at the slope 4.5).</summary>
    private static double Bt709Light(double v)
    {
        const double alpha = 1.09929682680944, beta = 0.018053968510807;
        return v < 4.5 * beta ? v / 4.5 : Math.Pow((v + alpha - 1) / alpha, 1 / 0.45);
    }

    /// <summary>A signed channel of n bits by T.801 M.11.7.2: Dzero + (2^n − 1 − Dzero)·x / (2^(n−1) − 1).</summary>
    private static double FromNoLight(int x, int bits, double zero) => zero + ((1 << bits) - 1 - zero) * x / ((1 << (bits - 1)) - 1);

    /// <summary>
    /// MatrixCoefficients 0 (equations 48–50: G, B, R), the sRGB curve (13), full range, 8 bits signed: each channel from black, d = 255·x/127 —
    /// rounded to the whole code the integer arithmetic of H.273 works with, negative values black. With the sRGB curve and primaries the
    /// samples are those codes in the order R, G, B. Before x + 128 (−128 black, 0 mid-grey).
    /// </summary>
    [Fact]
    public void Parameterized_SignedIdentity_FromBlack()
    {
        int[] g = [-128, 0, 64, 127], b = [-1, 127, 0, 30], r = [5, 127, -50, 100];
        var image = JpxDecoder.Decode(JpxFile(SignedRow(4, 8, g, b, r), [Parameterized(1, 13, 0, true)]), long.MaxValue);
        static byte Code(int x) => (byte)Math.Max(0, Math.Round(255.0 * x / 127, MidpointRounding.AwayFromZero));
        Assert.Equal(JpxColor.Rgb, image.Color);
        Assert.Equal(Enumerable.Range(0, 4).SelectMany(i => new[] { Code(r[i]), Code(g[i]), Code(b[i]) }).ToArray(), image.Samples);
        Assert.Equal(new byte[] { 10, 0, 0, 255, 0, 255, 0, 129, 0, 201, 255, 60 }, image.Samples);
    }

    /// <summary>
    /// BT.709 (primaries, transfer characteristic and matrix 1), limited range, 10 bits signed: Y from its black 64 (64 + 959·x/511), Cb and Cr
    /// from 512 (512 + x); then H.273 equations 30–32 undone, E′Y clipped to 0 … 1 and the colour differences to ±0.5, the matrix of BT.709
    /// inverted, the light through the inverse characteristic, written with the sRGB curve. x = 0 is black, 511 white (the top code lies
    /// above the nominal peak 940), −512 black; before x = 0 was the code 512, a mid-grey.
    /// </summary>
    [Fact]
    public void Parameterized_SignedLumaAndColourDifferences_FromNoLight()
    {
        int[] y = [0, 511, -512, 255, 200, 300, 100, 400], cb = [0, 0, 0, 0, -100, 120, 50, -200], cr = [0, 0, 0, 0, 150, -80, 50, -30];
        var image = JpxDecoder.Decode(JpxFile(SignedRow(8, 10, y, cb, cr), [Parameterized(1, 1, 1, false)]), long.MaxValue);
        var expected = new byte[24];
        for (var i = 0; i < 8; i++)
        {
            var luma = Math.Clamp((FromNoLight(y[i], 10, 64) / 4 - 16) / 219, 0, 1);
            var pb = Math.Clamp(((512.0 + cb[i]) / 4 - 128) / 224, -0.5, 0.5);
            var pr = Math.Clamp(((512.0 + cr[i]) / 4 - 128) / 224, -0.5, 0.5);
            double red = luma + 1.5748 * pr, blue = luma + 1.8556 * pb, green = (luma - 0.2126 * red - 0.0722 * blue) / 0.7152;
            expected[3 * i] = SrgbByte(Bt709Light(red));
            expected[3 * i + 1] = SrgbByte(Bt709Light(green));
            expected[3 * i + 2] = SrgbByte(Bt709Light(blue));
        }
        Assert.Equal(JpxColor.Rgb, image.Color);
        Near(expected, image.Samples);
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255, 0, 0, 0 }, image.Samples[..9]);
    }

    /// <summary>
    /// One signed component (luma alone, drawn as grey): BT.709 limited range at 12 bits, Y from 256 (256 + 3 839·x/2 047); the grey is the
    /// luminance, R = G = B. x = 0 black, 2 047 white, −1 black.
    /// </summary>
    [Fact]
    public void Parameterized_SignedLumaAlone_GreyFromBlack()
    {
        int[] y = [0, 2047, -1, 1024, 300];
        var image = JpxDecoder.Decode(JpxFile(SignedRow(5, 12, y), [Parameterized(1, 1, 1, false)]), long.MaxValue);
        var expected = y.Select(x => SrgbByte(Bt709Light(Math.Clamp((FromNoLight(x, 12, 256) / 16 - 16) / 219, 0, 1)))).ToArray();
        Assert.Equal(JpxColor.Gray, image.Color);
        Near(expected, image.Samples);
        Assert.Equal(new byte[] { 0, 255, 0 }, image.Samples[..3]);
    }

    /// <summary>
    /// YCgCo-Re (MatrixCoefficients 16; equations 23–26 and 62–65), limited range, BT.709 primaries and curve, 10-bit components — the coded R, G, B two bits
    /// shallower, 8 bits: Y from the black of those codes, 16 (not 64 of 10 bits), 16 + 1 007·x/511 rounded to a whole code; Cg and Co from 512
    /// (512 + x). The inverse transform in integers, R, G, B within 0 … 255, then (v − 16)/219 as E′ through the inverse characteristic, written
    /// with the sRGB curve. x = 0 is black.
    /// </summary>
    [Fact]
    public void Parameterized_SignedYCgCoRe_LumaFromTheBlackOfTheShallowerCodes()
    {
        int[] y = [0, 100, 511, -20, 60], cg = [0, 0, 0, 0, 40], co = [0, 0, 0, 0, -30];
        var image = JpxDecoder.Decode(JpxFile(SignedRow(5, 10, y, cg, co), [Parameterized(1, 1, 16, false)]), long.MaxValue);
        var expected = new byte[15];
        for (var i = 0; i < 5; i++)
        {
            var luma = (long)Math.Round(FromNoLight(y[i], 10, 16), MidpointRounding.AwayFromZero);
            var t = luma - (cg[i] >> 1);
            long green = Math.Clamp(t + cg[i], 0, 255), blue = Math.Clamp(t - (co[i] >> 1), 0, 255);
            var red = Math.Clamp(blue + co[i], 0, 255);
            expected[3 * i] = SrgbByte(Bt709Light((red - 16) / 219.0));
            expected[3 * i + 1] = SrgbByte(Bt709Light((green - 16) / 219.0));
            expected[3 * i + 2] = SrgbByte(Bt709Light((blue - 16) / 219.0));
        }
        Assert.Equal(JpxColor.Rgb, image.Color);
        Near(expected, image.Samples);
        Assert.Equal(new byte[] { 0, 0, 0 }, image.Samples[..3]);
    }
}
