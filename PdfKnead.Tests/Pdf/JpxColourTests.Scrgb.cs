using PdfKnead.Core.Pdf.Filters.Jpx;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// YPbPr(1250/50) after ITU-R BT.709-3 Part I (docs/FEHLERGEDAECHTNIS.md, No. 1078), scRGB after the ICC Colour Encoding Registry (No. 1079)
/// and e-sYCC with 16-bit components (PIMA 7667, B.2). The expected values were worked out apart from the decoder: the YPbPr codes forward
/// and back with the equations and constants of BT.709-3 Part I; scRGB from the registry's range (−0.5 at code 0, 7.4999 at 65535) with the
/// matrix from the BT.709 to the BT.2020 primaries computed from their chromaticities and the sRGB curve of IEC 61966-2-1; e-sYCC with the
/// printed matrix of PIMA 7667, B.2, and its extended curve (6a–6c).
/// </summary>
public sealed partial class JpxColourTests
{
    // ---- No. 1078: YPbPr(1250/50) with the primaries of BT.709

    /// <summary>
    /// YPbPr(1250/50) (T.801 Table M.25, value 23): BT.709-3 Part I gives the 1250/50 system the primaries and the transfer characteristic of
    /// 1125/60 (items 1.2–1.4: those of BT.709 and so of sRGB) and its own luma, 0.299/0.587/0.114 (item 4.2). The 8-bit codes of pure green,
    /// red, blue, yellow, cyan, magenta, grey 0.5 and an orange, written forward with that luma (item 4.3, 6.10), come back as those colours —
    /// plain RGB, no profile; read with the luma of 1125/60 they would not (green 0/220/0). Before the system was read with the primaries of
    /// H.273 value 5 (green 0.29/0.60) in an ICC profile.
    /// </summary>
    [Fact]
    public void YPbPr1250_PrimariesAndCurveOfBt709_LumaOfItem42()
    {
        int[] y = [145, 81, 41, 210, 170, 106, 125, 125], pb = [54, 90, 240, 16, 166, 202, 128, 91], pr = [34, 240, 110, 146, 16, 222, 128, 176];
        var cs = JpxTests.AnnexG.Lossless(4, 2, [(8, false), (8, false), (8, false)], [y, pb, pr]);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(23)]), long.MaxValue);
        Assert.False(image.Truncated);
        Assert.Equal(JpxColor.Rgb, image.Color);
        Assert.Null(image.IccProfile);
        Near([1, 255, 3, 255, 0, 0, 1, 0, 255, 255, 255, 0, 2, 255, 255, 255, 0, 254, 139, 139, 139, 209, 116, 68], image.Samples);
    }

    // ---- No. 1079: scRGB (linear) after the ICC Colour Encoding Registry

    /// <summary>
    /// scRGB (EnumCS 25) with 16-bit components inside sRGB: R = (C − 4096) / 8192 (white 12 288, black 4 096) is linear light of the sRGB
    /// primaries, written with the sRGB curve — plain RGB, no profile. Source: ICC Colour Encoding Registry (scRGB), not the text of
    /// IEC 61966-2-2 (not at hand). Before scRGB was passed over and the codes shown as they are.
    /// </summary>
    [Fact]
    public void ScRgb_SixteenBitCodes_LinearLight()
    {
        int[] r = [12288, 4096, 8192, 5864, 7373, 4196, 12286, 4136], g = [12288, 4096, 5734, 5864, 10650, 9096, 4097, 4136],
            b = [12288, 4096, 4506, 5864, 4915, 12096, 8096, 4136];
        var cs = JpxTests.AnnexG.Lossless(4, 2, [(16, false), (16, false), (16, false)], [r, g, b]);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(25)]), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, image.Color);
        Assert.Null(image.IccProfile);
        Assert.Equal(new byte[] { 255, 255, 255, 0, 0, 0, 188, 124, 63, 128, 128, 128, 170, 231, 89, 29, 205, 252, 255, 0, 186, 15, 15, 15 }, image.Samples);
    }

    /// <summary>
    /// scRGB beyond sRGB: red 1.3 with green and blue 0.2, a cyan with red −0.05, code 0 (−0.5) and 65 535 (7.4999) — the image comes into the
    /// wide working space (BT.2020 primaries, sRGB curve), the colours beyond sRGB kept as far as BT.2020 holds them; a colour beyond that as
    /// well (light above 1, above the reference white of the scene-referred encoding, or the cyan of pixel 6) brought to the edge of BT.2020 on the line to its colour clipped in its own primaries
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1092; before clipped per channel in the BT.2020 primaries).
    /// </summary>
    [Fact]
    public void ScRgb_BeyondSrgb_InTheWideSpace()
    {
        int[] r = [14746, 3686, 0, 65535, 12288, 4096, 6144, 20480], g = [5734, 9011, 0, 65535, 12288, 4096, 8192, 4096],
            b = [5734, 9011, 0, 65535, 12288, 4096, 3072, 4096];
        var cs = JpxTests.AnnexG.Lossless(4, 2, [(16, false), (16, false), (16, false)], [r, g, b]);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(25)]), long.MaxValue);
        Assert.Equal(JpxColor.Icc, image.Color);
        Assert.Equal(JpxParameterized.WideProfile(), image.IccProfile);
        Near([242, 143, 129, 121, 196, 202, 0, 0, 0, 255, 255, 255, 255, 255, 255, 0, 0, 0, 153, 183, 0, 255, 93, 45], image.Samples);
    }

    /// <summary>
    /// scRGB with half-precision floating point components (Pixel Format 0x400A on 16-bit components): the numbers are linear light
    /// themselves — −0.25 is kept in the wide working space; red 2.0 beside green 0.5 lies beyond BT.2020 too and is brought to the edge of BT.2020 on the line to its colour clipped in its own primaries
    /// (docs/FEHLERGEDAECHTNIS.md, No. 1092; before clipped there per channel, green 204, now 196), not a number is no light, +∞ the top of
    /// the range. Colours inside sRGB stay plain RGB.
    /// </summary>
    [Fact]
    public void ScRgb_HalfFloats_TheirNumbersAsLinearLight()
    {
        // 1, 0, (2, 0.5, 0.5), (−0.25, 0.5, 0.5), (0.6, 0.2, 0.05), (NaN, 0.5, 0.25), (+∞, 0, 0), (0.125, 0.75, −0.0625) as half precision
        int[] r = [15360, 0, 16384, 46080, 14541, 32256, 31744, 12288], g = [15360, 0, 14336, 14336, 12902, 14336, 0, 14848],
            b = [15360, 0, 14336, 14336, 10854, 13312, 0, 44032];
        var cs = JpxTests.AnnexG.Lossless(4, 2, [(16, false), (16, false), (16, false)], [r, g, b]);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(25), M117Colour.Pxfm(3, 0x400A)]), long.MaxValue);
        Assert.Equal(JpxColor.Icc, image.Color);
        Assert.Equal(JpxParameterized.WideProfile(), image.IccProfile);
        Near([255, 255, 255, 0, 0, 0, 255, 196, 190, 48, 179, 185, 178, 131, 76, 116, 181, 141, 255, 93, 45, 154, 218, 29], image.Samples);

        // 1, 0, (0.5, 0.25, 0.125), (0.75, 0.0625, 0.375): inside sRGB
        var inside = JpxTests.AnnexG.Lossless(4, 1, [(16, false), (16, false), (16, false)],
            [[15360, 0, 14336, 14848], [15360, 0, 13312, 11264], [15360, 0, 12288, 13824]]);
        var plain = JpxDecoder.Decode(Jpx(inside, [Enumerated(25), M117Colour.Pxfm(3, 0x400A)]), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, plain.Color);
        Assert.Equal(new byte[] { 255, 255, 255, 0, 0, 0, 188, 137, 99, 225, 71, 165 }, plain.Samples);
    }

    /// <summary>
    /// scRGB through a palette of half-precision columns (Bi 0x0F, Pixel Format 0x400A): the entries as the numbers they are — 1.5 and −0.125
    /// beyond sRGB, the image in the wide working space.
    /// </summary>
    [Fact]
    public void ScRgb_PaletteColumnsOfHalfFloats()
    {
        var cs = JpxTests.AnnexG.Lossless8(4, 1, [0, 1, 2, 3]);
        var palette = Pclr([0x0F, 0x0F, 0x0F], [14336, 13312, 12288], [15872, 13312, 13312], [45056, 13312, 13312], [0, 0, 0]);
        var image = JpxDecoder.Decode(Jpx(cs, [Enumerated(25), palette, CmapColumns(3), M117Colour.Pxfm(3, 0x400A)]), long.MaxValue);
        Assert.Equal(JpxColor.Icc, image.Color);
        Assert.Equal(JpxParameterized.WideProfile(), image.IccProfile);
        Near([170, 141, 105, 255, 156, 142, 32, 130, 135, 0, 0, 0], image.Samples);   // 1.5 beyond BT.2020 too (No. 1092)
    }

    /// <summary>
    /// scRGB with integer components of another depth than 16 bits (8, 12) or signed 16-bit components: the registry fixes no encoding for
    /// them, so the specification is passed over — sRGB behind it counts; alone the colour is not known and the image is the codestream's.
    /// </summary>
    [Theory]
    [InlineData(8, false)]
    [InlineData(12, false)]
    [InlineData(16, true)]
    public void ScRgb_OtherChannels_PassedOver(int bits, bool signed)
    {
        var top = signed ? (1 << (bits - 1)) - 1 : (1 << bits) - 1;
        int[] r = [top, 0, top / 2, top / 3], g = [top, 0, top / 4, top / 5], b = [top, 0, top / 8, top / 2];
        var cs = JpxTests.AnnexG.Lossless(4, 1, [(bits, signed), (bits, signed), (bits, signed)], [r, g, b]);
        var srgb = JpxDecoder.Decode(Jpx(cs, [Enumerated(16)]), long.MaxValue);
        var next = JpxDecoder.Decode(Jpx(cs, [Enumerated(25, 1, 1), Enumerated(16, 0, 1)]), long.MaxValue);
        Assert.Equal(JpxColor.Rgb, next.Color);
        Assert.Equal(srgb.Samples, next.Samples);
        var alone = JpxDecoder.Decode(Jpx(cs, [Enumerated(25)]), long.MaxValue);
        Assert.Equal(JpxColor.Unknown, alone.Color);
        Assert.Equal(Plain(cs), alone.Samples);
    }

    // ---- PIMA 7667, B.2 with 16-bit components

    /// <summary>
    /// e-sYCC (EnumCS 24) with 16-bit components (PIMA 7667, B.2 with m = 16): Y′ = Y″ / 65 535, Cb′ and Cr′ = (C″ − 32 768) / 65 535, the
    /// matrix of B.2 to R′G′B′. The entries of the 8-bit case of B9 times 256 (Y 25 600: R 172, a step below the 173 of the 8-bit code 100,
    /// since 25 600 / 65 535 lies below 100 / 255), white, black and two colours — inside sRGB, plain RGB; the 12-bit codes beyond sRGB of
    /// the 12-bit case times 16 — the same colours in the wide working space.
    /// </summary>
    [Fact]
    public void ESycc_SixteenBits_TheMatrixOfB2()
    {
        int[] y = [25600, 25600, 25600, 25600, 65535, 0, 46080, 15360], cb = [32768, 37888, 27648, 32768, 32768, 32768, 25600, 38400],
            cr = [39424, 32768, 32768, 26112, 32768, 32768, 35840, 28672];
        var inside = JpxDecoder.Decode(Jpx(JpxTests.AnnexG.Lossless(4, 2, [(16, false), (16, false), (16, false)], [y, cb, cr]), [Enumerated(24)]),
            long.MaxValue);
        Assert.Equal(JpxColor.Rgb, inside.Color);
        Assert.Equal(new byte[] { 172, 63, 100, 100, 86, 170, 100, 113, 29, 27, 137, 100, 255, 255, 255, 0, 0, 0, 213, 181, 80, 15, 67, 137 }, inside.Samples);

        int[] y2 = [26144, 29840, 24416, 65535, 0, 28640, 42688, 18272], cb2 = [27248, 31744, 48064, 32768, 32768, 28384, 38288, 38704],
            cr2 = [49152, 17456, 28736, 32768, 32768, 36576, 18704, 47280];
        var beyond = JpxDecoder.Decode(Jpx(JpxTests.AnnexG.Lossless(4, 2, [(16, false), (16, false), (16, false)], [y2, cb2, cr2]), [Enumerated(24)]),
            long.MaxValue);
        Assert.Equal(JpxColor.Icc, beyond.Color);
        Assert.Equal(JpxParameterized.WideProfile(), beyond.IccProfile);
        Near([229, 87, 49, 119, 196, 115, 86, 81, 255, 255, 255, 255, 0, 0, 0, 136, 106, 61, 149, 223, 239, 188, 62, 148], beyond.Samples);   // No. 1092
    }
}
