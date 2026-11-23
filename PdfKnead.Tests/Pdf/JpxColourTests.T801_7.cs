using System.Text;
using PdfKnead.Core.Pdf.Filters.Jpx;
using PdfKnead.Core.Pdf.Graphics.Icc;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// ITU-T T.801 M.11.7 sentence by sentence: the Compositing Layer Header box and its boxes — colour group, colour specifications (method,
/// precedence, approximation), the enumerated spaces and the EP fields of CIELab (Equation M-18, Tables M.29 and M.29bis), channel definition,
/// opacity, registration and pixel format. Expected values are worked out here from the formulas of the standard, apart from the decoder.
/// </summary>
public sealed partial class JpxColourTests
{
    /// <summary>Helpers of the tests on M.11.7 (own names: other partial files of the class have helpers of their own).</summary>
    private static class M117Colour
    {
        /// <summary>The codestream with every component marked signed in SIZ (Ssiz high bit), the same depth.</summary>
        internal static byte[] Signed(byte[] codestream)
        {
            var cs = (byte[])codestream.Clone();
            var siz = 2;
            while (!(cs[siz] == 0xFF && cs[siz + 1] == 0x51)) siz += 2 + (cs[siz + 2] << 8 | cs[siz + 3]);
            var components = cs[siz + 38] << 8 | cs[siz + 39];
            for (var c = 0; c < components; c++) cs[siz + 40 + 3 * c] |= 0x80;
            return cs;
        }

        /// <summary>A Channel Definition box: N, then per channel Cn, Typ, Asoc (2 bytes each).</summary>
        internal static byte[] Cdef(params (int Channel, int Type, int Association)[] definitions) =>
            Box("cdef", [U16(definitions.Length), .. definitions.Select(d => (byte[])[.. U16(d.Channel), .. U16(d.Type), .. U16(d.Association)])]);

        /// <summary>A Pixel Format box naming channels 0 to <paramref name="channels"/> − 1 with the one format.</summary>
        internal static byte[] Pxfm(int channels, int format) =>
            Box("pxfm", [U16(channels), .. Enumerable.Range(0, channels).Select(c => (byte[])[.. U16(c), .. U16(format)])]);

        /// <summary>A Pixel Format box naming each channel with its own format.</summary>
        internal static byte[] Pxfm(params int[] formats) =>
            Box("pxfm", [U16(formats.Length), .. formats.Select((f, c) => (byte[])[.. U16(c), .. U16(f)])]);

        /// <summary>A colour specification of another method: METH, PREC, APPROX, METHDAT.</summary>
        internal static byte[] Colr(byte method, sbyte precedence, byte approx, byte[] data) => Box("colr", [method, (byte)precedence, approx], data);

        /// <summary>
        /// L*, a*, b* in the encoding of the decoded CIELab channels (a PDF /Lab space with the range −128 to 127): L* 0 to 100 as 0 to 255,
        /// a* and b* plus 128; rounded and clamped.
        /// </summary>
        internal static byte Code(int k, double value) => (byte)Math.Clamp(Math.Round(k == 0 ? value * 255 / 100 : value + 128), 0, 255);

        /// <summary>The ranges RL, RA, RB without EP fields (M.11.7.4.1).</summary>
        internal static readonly double[] Ranges = [100, 170, 200];

        /// <summary>Three interleaved channels in the order 2, 1, 0.</summary>
        internal static byte[] Reversed(byte[] s) => [.. Enumerable.Range(0, s.Length / 3).SelectMany(i => new[] { s[3 * i + 2], s[3 * i + 1], s[3 * i] })];

        /// <summary>
        /// An 8-bit floating point number of Table M.35bis with format 0x4003: sign, four exponent bits (bias 2^3 − 1 = 7), three mantissa bits
        /// with the hidden one; exponent 0 subnormal, exponent 15 infinity or not a number (ISO/IEC/IEEE 60559).
        /// </summary>
        internal static double Minifloat(int b)
        {
            int sign = b >> 7, exponent = b >> 3 & 15, mantissa = b & 7;
            var v = exponent == 15 ? (mantissa == 0 ? double.PositiveInfinity : double.NaN)
                : exponent == 0 ? mantissa / 8.0 * Math.Pow(2, -6)
                : (1 + mantissa / 8.0) * Math.Pow(2, exponent - 7);
            return sign == 1 ? -v : v;
        }

        /// <summary>A number as a channel level of 0 to 1 (outside clamped, not a number 0).</summary>
        internal static double Unit(double v) => double.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
    }

    // ---- M.11.7 Compositing Layer Header box

    /// <summary>
    /// T.801 M.11.7 with M.11.21: a Compositing Layer Header box inside a Compositing Layer Extensions box stands for layers after those of the
    /// top level — here with sYCC, before and behind the top-level layer with sRGB: the first layer is the top-level one, the image the sRGB
    /// codestream's.
    /// </summary>
    [TestPdfsFact]
    public void LayerInAnExtensionBox_NotTheFirstLayer()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        // Mjclx 1, Cjclx 0, Ljclx 1, Tjclx 0
        var extension = Box("jclx", Box("jlxi", U32(1), U32(0), U32(1), U32(0)), Box("jplh", Box("cgrp", Enumerated(18))));
        var layer = Box("jplh", Box("cgrp", Enumerated(16)));
        Assert.Equal(plain, JpxDecoder.Decode(Jpx(cs, [Enumerated(18)], layer, extension), long.MaxValue).Samples);
        Assert.Equal(plain, JpxDecoder.Decode(Jpx(cs, [Enumerated(18)], extension, layer), long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 M.11.7: the Channel Definition box of the first compositing layer takes the place of the one in the JP2 Header box — that one names
    /// the channels blue, green, red, the layer's red, green, blue: the image of the codestream alone; without the layer's the channels come
    /// reversed.
    /// </summary>
    [TestPdfsFact]
    public void ChannelDefinitionOfTheFirstLayer_TakesThePlaceOfTheHeaderOne()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        byte[][] header = [Enumerated(16), M117Colour.Cdef((0, 0, 3), (1, 0, 2), (2, 0, 1))];
        var layer = Box("jplh", M117Colour.Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3)));
        Assert.Equal(plain, JpxDecoder.Decode(Jpx(cs, header, layer), long.MaxValue).Samples);
        Assert.Equal(M117Colour.Reversed(plain), JpxDecoder.Decode(Jpx(cs, header), long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 M.11.7, M.11.7.7, Table M.35: the Codestream Registration box of the first compositing layer — XS, YS 2 points per unit, for
    /// codestream 0 XR, YR 2 and XO, YO 1 — is passed over: the first layer of the JPX baseline has one codestream (M.9.2.2); the colour group
    /// beside it counts and the image keeps its size.
    /// </summary>
    [TestPdfsFact]
    public void RegistrationBoxOfTheFirstLayer_PassedOver()
    {
        var cs = Rgb.Value;
        var layer = Box("jplh", Box("cgrp", Enumerated(16)), Box("creg", U16(2), U16(2), U16(0), [2, 2, 1, 1]));
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(18)], layer), long.MaxValue);
        Assert.Equal((40, 27), (image.Width, image.Height));
        Assert.Equal(Plain(cs), image.Samples);
    }

    /// <summary>
    /// T.801 M.11.7 (Resolution box in the Compositing Layer Header box, as T.800 I.5.3.7): a capture and a display resolution of 300 dpi
    /// (11 811 per metre: VR and HR numerator 11 811, denominator 1, exponent 0) leave the decoded image as it is; in PDF the image dictionary
    /// and the matrix give the size.
    /// </summary>
    [TestPdfsFact]
    public void ResolutionOfTheFirstLayer_ImageUnchanged()
    {
        var cs = Rgb.Value;
        byte[] resolution = [.. U16(11811), .. U16(1), .. U16(11811), .. U16(1), 0, 0];
        var layer = Box("jplh", Box("cgrp", Enumerated(16)), Box("res ", Box("resc", resolution), Box("resd", resolution)));
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(18)], layer), long.MaxValue);
        Assert.Equal((40, 27), (image.Width, image.Height));
        Assert.Equal(Plain(cs), image.Samples);
    }

    // ---- M.11.7.1 Colour Group box, M.11.7.2 Colour Specification box

    /// <summary>
    /// T.801 M.11.7.1, M.11.7.2 (PREC within a colour group): a colour group of two specifications — sYCC with precedence 0 and sRGB with 5:
    /// sRGB counts, the image of the codestream alone; with the precedences the other way round sYCC counts, converted to RGB.
    /// </summary>
    [TestPdfsFact]
    public void ColourGroup_SeveralSpecifications_ThePrecedenceCounts()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var srgb = JpxDecoder.Decode(Jpx(cs, [Enumerated(17)], Box("jplh", Box("cgrp", Enumerated(18, 0, 1), Enumerated(16, 5, 1)))), long.MaxValue);
        Assert.Equal(plain, srgb.Samples);
        var ycc = JpxDecoder.Decode(Jpx(cs, [Enumerated(17)], Box("jplh", Box("cgrp", Enumerated(18, 5, 1), Enumerated(16, 0, 1)))), long.MaxValue);
        NearSycc(plain, ycc);
    }

    /// <summary>
    /// T.801 M.11.7.1 (several specifications by methods 3 and 4 in one group): a vendor specification (method 4, precedence 5), a damaged ICC
    /// profile (method 3, precedence 3) and the sRGB profile (method 3, precedence 1) — the first two cannot be used and are passed over, the
    /// sRGB profile counts.
    /// </summary>
    [TestPdfsFact]
    public void ColourGroup_VendorAndUnusableProfile_ThenTheUsableOne()
    {
        var cs = Rgb.Value;
        var srgb = CalibratedProfile.Srgb();
        var uuid = new byte[16];
        uuid[0] = 0x42;
        var group = Box("cgrp", M117Colour.Colr(4, 5, 1, [.. uuid, 1, 2, 3]), M117Colour.Colr(3, 3, 1, Enumerable.Repeat((byte)0x5A, 200).ToArray()),
            M117Colour.Colr(3, 1, 1, srgb));
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(18)], Box("jplh", group)), long.MaxValue);
        Assert.Equal(JpxColor.Icc, image.Color);
        Assert.Equal(srgb, image.IccProfile);
    }

    /// <summary>
    /// T.801 M.11.7.2 sentences 2 to 5, M.11.7.4.1 (Equation M-18, Table M.29bis for unsigned fixed point): the colour specification applies to
    /// the values the Pixel Format box gives — three 8-bit channels as fixed point numbers with 7 fractional bits (the integer divided by 128,
    /// MAX 1.0), CIELab without EP: N = min(v/128, 1), the default offsets 2^BPCi/2^n = 0.5 and (2^(BPCi−1) + 2^(BPCi−2))/2^n = 0.375, so
    /// L* = 100·N, a* = 170·(N − 0.5), b* = 200·(N − 0.375).
    /// </summary>
    [TestPdfsFact]
    public void PixelFormatBeforeTheColour_FixedPointCieLab()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        Assert.Contains(plain, v => v > 128);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(14), M117Colour.Pxfm(3, 0x3007)]), long.MaxValue);
        Assert.Equal(JpxColor.Lab, image.Color);
        double[] offsets = [0, 0.5, 0.375];
        var expected = plain.Select((v, i) => M117Colour.Code(i % 3, M117Colour.Ranges[i % 3] * (Math.Min(v / 128.0, 1) - offsets[i % 3]))).ToArray();
        Near(expected, image.Samples);
    }

    /// <summary>
    /// T.801 M.11.7.2 sentence 16: signed channels of an enumerated space without EP fields map from Dzero, the value for no light, by
    /// d = Dzero + (Dmax − Dzero)·x/MAX with MAX = 2^(n−1) − 1 — sRGB: Dzero 0 for each channel, so d = 255·x/127, the negative values below
    /// black; sYCC: Y the same, Cb and Cr with Dzero 128, so 128 + x before the conversion. The codestream marked signed holds the samples
    /// less 128. Before signed channels were shown shifted by half their range, also in sRGB and in the luma of sYCC (docs/FEHLERGEDAECHTNIS.md,
    /// No. 1066).
    /// </summary>
    [TestPdfsFact]
    public void SignedChannels_EnumeratedSpaceWithoutEp_FromDzero()
    {
        var cs = M117Colour.Signed(Rgb.Value);
        var x = Plain(cs).Select(v => v - 128).ToArray();
        static byte FromZero(int x) => (byte)Math.Round(255.0 * Math.Max(x, 0) / 127);
        var srgb = JpxDecoder.Decode(Jpx(cs, [Enumerated(16)]), long.MaxValue);
        Near(x.Select(FromZero).ToArray(), srgb.Samples);
        var ycc = JpxDecoder.Decode(Jpx(cs, [Enumerated(18)]), long.MaxValue);
        NearSycc(x.Select((v, i) => i % 3 == 0 ? FromZero(v) : (byte)(128 + v)).ToArray(), ycc);
    }

    /// <summary>
    /// T.801 M.11.7.2, Table M.22: a reserved method (9, before an sRGB value in its data, precedence 5) is not understood and passed over —
    /// the sYCC specification behind it counts, converted; since 23.11.2026 the image counts as damaged (docs/FEHLERGEDAECHTNIS.md, No. 1073).
    /// </summary>
    [TestPdfsFact]
    public void ColourSpecification_ReservedMethod_PassedOver()
    {
        var cs = Rgb.Value;
        var image = JpxDecoder.Decode(Jpx(cs, [M117Colour.Colr(9, 5, 1, U32(16)), Enumerated(18, 0, 1)]), long.MaxValue);
        NearSycc(Plain(cs), image);
        Assert.True(image.Truncated);
    }

    /// <summary>
    /// T.801 M.11.7.2, Table M.24: PREC is a signed byte (−128 to 127) — sRGB with 0xFF (−1) gives way to sYCC with 0 (read without sign it
    /// would count); at the bounds sYCC with 127 counts over sRGB with −128, and sRGB with 127 over sYCC with −128.
    /// </summary>
    [TestPdfsFact]
    public void Precedence_ASignedByte()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        NearSycc(plain, JpxDecoder.Decode(Jpx(cs, [Enumerated(18, 0, 1), M117Colour.Colr(1, -1, 1, U32(16))]), long.MaxValue));
        NearSycc(plain, JpxDecoder.Decode(Jpx(cs, [Enumerated(16, -128, 1), Enumerated(18, 127, 1)]), long.MaxValue));
        Assert.Equal(plain, JpxDecoder.Decode(Jpx(cs, [Enumerated(18, -128, 1), Enumerated(16, 127, 1)]), long.MaxValue).Samples);
    }

    /// <summary>
    /// T.801 M.11.7.2, Table M.23: among specifications of the same precedence the better approximation counts — each value before the next
    /// (1 before 2, 2 before 3, 3 before 4): sRGB with APPROX k over sYCC with k + 1 standing first in the file.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Approximation_EachValueBeforeTheNext(int k)
    {
        var cs = Rgb.Value;
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(18, 0, (byte)(k + 1)), Enumerated(16, 0, (byte)k)]), long.MaxValue);
        Assert.Equal(Plain(cs), image.Samples);
    }

    /// <summary>
    /// T.801 M.11.7.2, Table M.24bis (signed fixed point: MAX 1.0), Table M.35bis: signed 8-bit channels as fixed point numbers with 6
    /// fractional bits — x/64 with x the signed integer (the samples of the codestream alone less 128) —, sRGB: the level x/64 clamped to 0 to 1.
    /// </summary>
    [TestPdfsFact]
    public void FixedPoint_SignedChannelsSrgb_TheirNumbers()
    {
        var cs = M117Colour.Signed(Rgb.Value);
        var x = Plain(cs).Select(v => v - 128).ToArray();
        Assert.Contains(x, v => v is > 0 and < 64);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(16), M117Colour.Pxfm(3, 0x3006)]), long.MaxValue);
        Assert.Equal(x.Select(v => (byte)Math.Round(Math.Clamp(v / 64.0, 0, 1) * 255)).ToArray(), image.Samples);
    }

    /// <summary>
    /// T.801 M.11.7.2 (sentence 9 and Table M.24bis), M.11.7.4.1 (Equation M-18, Table M.29bis): signed fixed point (f = 6: x/64) and signed
    /// floating point channels (format 0x4003) have MAX 1.0. CIELab without EP: the offsets are 0, so L* = 100·N, a* = 170·N, b* = 200·N with
    /// N the number; an ICC profile: d = Dmax·N, the negative values to 0. Before such channels were clamped to 0 … 1 like unsigned numbers and
    /// then read like signed integers shifted by half their range (docs/FEHLERGEDAECHTNIS.md, No. 1067).
    /// </summary>
    [TestPdfsTheory]
    [InlineData("Lab", 0x3006)]
    [InlineData("Lab", 0x4003)]
    [InlineData("Icc", 0x3006)]
    [InlineData("Icc", 0x4003)]
    public void FixedAndFloatingPoint_SignedChannels_CieLabAndIcc(string space, int format)
    {
        var cs = M117Colour.Signed(Rgb.Value);
        var x = Plain(cs).Select(v => v - 128).ToArray();
        var number = x.Select(v => format == 0x3006 ? v / 64.0 : M117Colour.Minifloat(v & 0xFF)).ToArray();
        if (space == "Lab")
        {
            var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(14), M117Colour.Pxfm(3, format)]), long.MaxValue);
            Near(number.Select((n, i) => M117Colour.Code(i % 3, M117Colour.Ranges[i % 3] * (double.IsNaN(n) ? 0 : n))).ToArray(), image.Samples);
        }
        else
        {
            var image = JpxDecoder.Decode(Jpx(cs, [Icc(CalibratedProfile.Srgb()), M117Colour.Pxfm(3, format)]), long.MaxValue);
            Assert.Equal(JpxColor.Icc, image.Color);
            Near(number.Select(n => (byte)Math.Round(M117Colour.Unit(n) * 255)).ToArray(), image.Samples);
        }
    }

    // ---- M.11.7.3.1 the enumerated method

    /// <summary>
    /// T.801 M.11.7.3.1, Table M.26 (EnumCS 0 to 2^32 − 1): values that name no colour space of Table M.25 — the general RGB and grey spaces of
    /// SPIFF (8, 10), and 0x80000010 and 0xFFFFFFFF over the whole 32 bits — are passed over: the sYCC specification behind them counts; alone
    /// the colour is not known and the image is the codestream's.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(8u)]
    [InlineData(10u)]
    [InlineData(0x80000010u)]
    [InlineData(0xFFFFFFFFu)]
    public void EnumeratedSpace_NotOfTheTable_PassedOver(uint space)
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var next = JpxDecoder.Decode(Jpx(cs, [Enumerated(space, 1, 1), Enumerated(18, 0, 1)]), long.MaxValue);
        NearSycc(plain, next);
        var alone = JpxDecoder.Decode(Jpx(cs, [Enumerated(space, 0, 1)]), long.MaxValue);
        Assert.Equal(JpxColor.Unknown, alone.Color);
        Assert.Equal(plain, alone.Samples);
    }

    /// <summary>
    /// T.801 M.11.7.3.1 (the spaces of JP2: 17 sRGB-grey): one channel with EnumCS 17 is grey; three channels do not fit it — the colour is not
    /// known.
    /// </summary>
    [TestPdfsFact]
    public void SrgbGrey_OneChannel()
    {
        var grey = JpxDecoder.Decode(Jpx(Gray.Value, [Enumerated(17)]), long.MaxValue);
        Assert.Equal((JpxColor.Gray, 1), (grey.Color, grey.Channels));
        Assert.Equal(Plain(Gray.Value), grey.Samples);
        Assert.Equal(JpxColor.Unknown, JpxDecoder.Decode(Jpx(Rgb.Value, [Enumerated(17)]), long.MaxValue).Color);
    }

    // ---- M.11.7.4.1 the EP fields of CIELab

    /// <summary>
    /// T.801 M.11.7.4.1, Equation M-18 with given offsets: the offset counts as 2^⌈log2 MAX⌉·O/2^n — for unsigned 8-bit channels (MAX 255) O
    /// itself, for signed ones (MAX 127) O/2. EP RL 100, OL 0, RA 170, OA 64, RB 200, OB 0 on signed channels: L* = x·100/127,
    /// a* = (x − 32)·170/127, b* = x·200/127 with x the signed integer (the samples of the codestream alone less 128). Before the offset counted
    /// in full on signed channels (docs/FEHLERGEDAECHTNIS.md, No. 1068).
    /// </summary>
    [TestPdfsFact]
    public void CieLab_SignedChannelsGivenOffsets_AsEquationM18()
    {
        var cs = M117Colour.Signed(Rgb.Value);
        var x = Plain(cs).Select(v => v - 128).ToArray();
        byte[] ep = [.. U32(100), .. U32(0), .. U32(170), .. U32(64), .. U32(200), .. U32(0), .. U32(0x00443530)];
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(14, ep: ep)]), long.MaxValue);
        double[] offsets = [0, 32, 0];
        Near(x.Select((v, i) => M117Colour.Code(i % 3, (v - offsets[i % 3]) * M117Colour.Ranges[i % 3] / 127)).ToArray(), image.Samples);
    }

    /// <summary>
    /// T.801 M.11.7.4.1, fields RL to OB and Table M.30: all six from EP — RL 50, OL 20, RA 170, OA 128, RB 200, OB 96 on unsigned 8-bit
    /// channels (MAX 255, offsets as given): L* = (v − 20)·50/255, a* = (v − 128)·170/255, b* = (v − 96)·200/255.
    /// </summary>
    [TestPdfsFact]
    public void CieLab_RangeAndOffsetOfLFromEp()
    {
        var cs = Rgb.Value;
        byte[] ep = [.. U32(50), .. U32(20), .. U32(170), .. U32(128), .. U32(200), .. U32(96), .. U32(0x00443530)];
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(14, ep: ep)]), long.MaxValue);
        double[] ranges = [50, 170, 200], offsets = [20, 128, 96];
        Assert.Equal(Plain(cs).Select((v, i) => M117Colour.Code(i % 3, (v - offsets[i % 3]) * ranges[i % 3] / 255)).ToArray(), image.Samples);
    }

    /// <summary>
    /// T.801 M.11.7.4.1, Table M.29 and the colour temperature „CT“ with kelvin in two bytes: the white point (X, 1, Z) of each illuminant,
    /// worked out from the chromaticities of CIE 15 (2° observer: X = x/y, Z = (1 − x − y)/y) — D50, D65, D75, A, C, F2, F7, F11 — and for a
    /// colour temperature from the CIE daylight locus (7 500 K: x 0.29909, y 0.31503; 5 000 K: x 0.34574, y 0.35867; the ends of its range,
    /// 4 000 K and 25 000 K); an identifier that is none of these gives D50, the default of the field.
    /// </summary>
    [Theory]
    [InlineData(0x00443530u, 0.96421, 0.82519)]
    [InlineData(0x00443635u, 0.95043, 1.08890)]
    [InlineData(0x00443735u, 0.94972, 1.22639)]
    [InlineData(0x00005341u, 1.09847, 0.35582)]
    [InlineData(0x00005343u, 0.98071, 1.18225)]
    [InlineData(0x00004632u, 0.99145, 0.67316)]
    [InlineData(0x00004637u, 0.95017, 1.08630)]
    [InlineData(0x00463131u, 1.00899, 0.64262)]
    [InlineData(0x43541D4Cu, 0.94942, 1.22493)]
    [InlineData(0x43541388u, 0.96396, 0.82414)]
    [InlineData(0x43540FA0u, 0.99629, 0.60946)]
    [InlineData(0x435461A8u, 0.98059, 1.94407)]
    [InlineData(0x00445858u, 0.96421, 0.82519)]
    public void Illuminant_TableM29AndColourTemperature(uint il, double x, double z)
    {
        var white = JpxDecoder.Illuminant(il);
        Assert.Equal(x, white[0], 0.002);
        Assert.Equal(1, white[1]);
        Assert.Equal(z, white[2], 0.002);
    }

    /// <summary>
    /// T.801 M.11.7.4.1, Table M.29bis (unsigned floating point) with Equation M-18: three 8-bit channels as floating point numbers of format
    /// 0x4003 (MAX 1.0), CIELab without EP — N the number within 0 to 1, L* = 100·N, a* = 170·(N − 0.5), b* = 200·(N − 0.375).
    /// </summary>
    [TestPdfsFact]
    public void CieLab_FloatingPointUnsigned_DefaultOffsets()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(14), M117Colour.Pxfm(3, 0x4003)]), long.MaxValue);
        Assert.Equal(JpxColor.Lab, image.Color);
        double[] offsets = [0, 0.5, 0.375];
        var expected = plain.Select((v, i) =>
            M117Colour.Code(i % 3, M117Colour.Ranges[i % 3] * (M117Colour.Unit(M117Colour.Minifloat(v)) - offsets[i % 3]))).ToArray();
        Near(expected, image.Samples);
    }

    // ---- M.11.7.4.2 CIEJab

    /// <summary>
    /// T.801 M.11.7.4.2, Tables M.30bis and M.31: CIEJab (EnumCS 19) — with EP fields RJ, OJ, RA, OA, RB, OB (24 bytes) or without — is not
    /// drawn (PDF excludes it): the sYCC specification behind it counts; alone the colour is not known and the image is the codestream's.
    /// </summary>
    [TestPdfsFact]
    public void CieJab_WithParameters_PassedOver_TheNextCounts()
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        byte[] ep = [.. U32(100), .. U32(0), .. U32(255), .. U32(128), .. U32(255), .. U32(128)];
        foreach (var jab in new[] { Enumerated(19, 1, 1, ep), Enumerated(19, 1, 1) })
        {
            var next = JpxDecoder.Decode(Jpx(cs, [jab, Enumerated(18, 0, 1)]), long.MaxValue);
            NearSycc(plain, next);
        }
        var alone = JpxDecoder.Decode(Jpx(cs, [Enumerated(19, 0, 1, ep)]), long.MaxValue);
        Assert.Equal(JpxColor.Unknown, alone.Color);
        Assert.Equal(plain, alone.Samples);
    }

    // ---- M.11.7.5 Channel Definition box, M.11.7.6 Opacity box

    /// <summary>
    /// T.801 M.11.7.5, Table M.32 (Lab: L, a, b; YCbCr: Y, Cb, Cr): the colours by their association, whatever the class — with the channels
    /// named 3, 2, 1 CIELab and sYCC are worked out from the samples in reversed order.
    /// </summary>
    [TestPdfsFact]
    public void ChannelDefinition_LabAndYcc_ColoursByAssociation()
    {
        var cs = Rgb.Value;
        var reversed = M117Colour.Reversed(Plain(cs));
        var definitions = M117Colour.Cdef((0, 0, 3), (1, 0, 2), (2, 0, 1));
        double[] offsets = [0, 128, 96];
        var lab = JpxDecoder.Decode(Jpx(cs, [Enumerated(14), definitions]), long.MaxValue);
        Assert.Equal(reversed.Select((v, i) => M117Colour.Code(i % 3, (v - offsets[i % 3]) * M117Colour.Ranges[i % 3] / 255)).ToArray(), lab.Samples);
        var ycc = JpxDecoder.Decode(Jpx(cs, [Enumerated(18), definitions]), long.MaxValue);
        NearSycc(reversed, ycc);
    }

    /// <summary>
    /// T.801 M.11.7.5, Table M.32 (CMYK: C, M, Y, K): five channels of a codestream of the public test collection, the first four named the
    /// colours 4, 3, 2, 1 and the fifth opacity — CMYK from the four in reversed order; named 1, 2, 3, 4 in their order.
    /// </summary>
    [TestPdfsFact]
    public void ChannelDefinition_Cmyk_ColoursByAssociation()
    {
        var stored = StoredStream("jpx_numcomps.pdf", 13);
        var boxes = JpxBoxes.Read(stored);
        var five = boxes.Data[boxes.Start..boxes.End];
        var plain = Plain(five);
        var count = plain.Length / 5;
        Assert.Equal(32 * 32, count);
        var reversed = JpxDecoder.Decode(Jpx(five, [Enumerated(12), M117Colour.Cdef((0, 0, 4), (1, 0, 3), (2, 0, 2), (3, 0, 1), (4, 1, 0))]), long.MaxValue);
        var ordered = JpxDecoder.Decode(Jpx(five, [Enumerated(12), M117Colour.Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 0, 4), (4, 1, 0))]), long.MaxValue);
        Assert.Equal((JpxColor.Cmyk, 4), (reversed.Color, reversed.Channels));
        Assert.Equal([.. Enumerable.Range(0, count).SelectMany(i => new[] { plain[5 * i + 3], plain[5 * i + 2], plain[5 * i + 1], plain[5 * i] })],
            reversed.Samples);
        Assert.Equal([.. Enumerable.Range(0, count).SelectMany(i => plain.Skip(5 * i).Take(4))], ordered.Samples);
        Assert.NotEqual(ordered.Samples, reversed.Samples);
        Assert.Equal([.. Enumerable.Range(0, count).Select(i => plain[5 * i + 4])], reversed.Alpha!);
    }

    /// <summary>
    /// T.801 M.11.7.6, Table M.33 (all values other than 0, 1, 2 reserved): an Opacity box with Otyp 3 gives no opacity — three colour channels
    /// in their order, the image of the codestream alone.
    /// </summary>
    [TestPdfsFact]
    public void OpacityBox_ReservedType_NoOpacity()
    {
        var cs = Rgb.Value;
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(16)], Box("jplh", Box("opct", [3]))), long.MaxValue);
        Assert.Equal(3, image.Channels);
        Assert.Null(image.Alpha);
        Assert.Equal(Plain(cs), image.Samples);
    }

    // ---- M.11.7.8 Pixel Format box

    /// <summary>
    /// T.801 M.11.7.8 sentences 5 and 6 (JPEG XR codestreams): a Contiguous Codestream box that holds a JPEG XR codestream (it begins with
    /// 0x49 0x49 0xBC) gives no image — also where a JPEG 2000 codestream follows within its bytes: it is not looked for.
    /// </summary>
    [TestPdfsFact]
    public void JpegXrInTheCodestreamBox_NoImage()
    {
        byte[] xr = [0x49, 0x49, 0xBC, 0x01, .. BitConverter.IsLittleEndian ? [8, 0, 0, 0] : new byte[] { 0, 0, 0, 8 }, 0, 0, .. Gray.Value];
        Assert.Throws<JpxException>(() => JpxDecoder.Decode(Jpx(xr, [Enumerated(17)]), long.MaxValue));
    }

    /// <summary>
    /// T.801 M.11.7.8, Table M.35bis (mantissa 0x1000, exponent 0x2000): a mantissa channel of colour 1 takes the exponent channel of its own
    /// colour before the one for the whole image — V = mantissa·2^(exponent − 136) with channel 1; when channel 1 belongs to another colour,
    /// with channel 2 of the whole image.
    /// </summary>
    [TestPdfsTheory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public void MantissaAndExponent_ExponentOfTheSameColourFirst(int associationOfChannel1, int exponentChannel)
    {
        var cs = Rgb.Value;
        var plain = Plain(cs);
        var definitions = M117Colour.Cdef((0, 0, 1), (1, 0, associationOfChannel1), (2, 0, 0));
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(17), definitions, M117Colour.Pxfm(0x1000, 0x2000, 0x2000)]), long.MaxValue);
        Assert.Equal(1, image.Channels);
        var expected = Enumerable.Range(0, plain.Length / 3)
            .Select(i => (byte)(Math.Round(Math.Clamp(plain[3 * i] * Math.Pow(2, plain[3 * i + exponentChannel] - 136), 0, 1) * 65535) / 256)).ToArray();
        Assert.Equal(expected, image.Samples);
        Assert.Contains(expected, v => v > 0);
    }

    /// <summary>
    /// T.801 M.11.7.8, Table M.35ter: single precision (format 0x4017 on 32 bits: 23 mantissa bits, 8 exponent bits with bias 127) — 1.0, π and
    /// −0.5 from their bit patterns, the same as .NET reads them.
    /// </summary>
    [Theory]
    [InlineData(0x3F800000u, 1.0)]
    [InlineData(0x40490FDBu, 3.14159274)]
    [InlineData(0xBF000000u, -0.5)]
    [InlineData(0x00000001u, 1.4012984643e-45)]
    [InlineData(0x7F7FFFFFu, 3.4028235e38)]
    public void FloatingPoint_SinglePrecision(uint pattern, double expected)
    {
        var value = JpxPixelFormat.Value(pattern, 32, 0x4017);
        Assert.Equal(BitConverter.Int32BitsToSingle((int)pattern), value);
        Assert.Equal(expected, value, Math.Abs(expected) * 1e-7);
    }
}
