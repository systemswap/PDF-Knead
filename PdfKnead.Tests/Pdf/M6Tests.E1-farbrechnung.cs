using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M6, colour computation (docs/FEHLERGEDAECHTNIS.md, No. 1129): absolute colorimetric with the media white (ISO 32000-2, 8.6.5.8; ICC.1
/// 6.2.3, 6.3.2.2), black point compensation with the black point detection of ISO 18619 (ISO 32000-2, 8.6.5.9; PDF Association AN001), and
/// /BlackPoint of CalGray, CalRGB, Lab (8.6.5.2–8.6.5.4). Uses the helpers <c>IccL_*</c> and <c>D_*</c> of the drafts l and d.
/// </summary>
public sealed partial class M6NormTests
{
    // ------------------------------------------------------------ helpers

    /// <summary>
    /// lut16Type PCS → device (a BToA table): three inputs, <paramref name="outputs"/> outputs, two grid points per input, identity matrix and
    /// curves; <paramref name="corners"/> holds the output words of each corner, the first input varying slowest.
    /// </summary>
    private static byte[] E1_BToA16(int outputs, params ushort[][] corners)
    {
        const int inputs = 3, points = 8;
        var d = new byte[52 + inputs * 4 + points * outputs * 2 + outputs * 4];
        Encoding.ASCII.GetBytes("mft2", d);
        d[8] = inputs;
        d[9] = (byte)outputs;
        d[10] = 2;
        foreach (var at in (ReadOnlySpan<int>)[12, 28, 44]) IccL_SetU32(d, at, 0x10000);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(50), 2);
        var o = 52;
        for (var i = 0; i < inputs; i++, o += 4) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(o + 2), 65535);
        for (var c = 0; c < points; c++, o += 2 * outputs)
            for (var k = 0; k < outputs; k++) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(o + 2 * k), corners[c % corners.Length][k]);
        for (var i = 0; i < outputs; i++, o += 4) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(o + 2), 65535);
        return d;
    }

    /// <summary>lut8Type PCS → device: as <see cref="E1_BToA16"/> with bytes (identity tables of 256 entries).</summary>
    private static byte[] E1_BToA8(int outputs, params byte[][] corners)
    {
        const int inputs = 3, points = 8;
        var d = new byte[48 + inputs * 256 + points * outputs + outputs * 256];
        Encoding.ASCII.GetBytes("mft1", d);
        d[8] = inputs;
        d[9] = (byte)outputs;
        d[10] = 2;
        foreach (var at in (ReadOnlySpan<int>)[12, 28, 44]) IccL_SetU32(d, at, 0x10000);
        var o = 48;
        for (var i = 0; i < inputs; i++, o += 256)
            for (var v = 0; v < 256; v++) d[o + v] = (byte)v;
        for (var c = 0; c < points; c++, o += outputs) corners[c % corners.Length].AsSpan(0, outputs).CopyTo(d.AsSpan(o));
        for (var i = 0; i < outputs; i++, o += 256)
            for (var v = 0; v < 256; v++) d[o + v] = (byte)v;
        return d;
    }

    /// <summary>lutBToAType: B curves (identity), a 16-bit CLUT of two points per input, A curves (identity); no matrix, no M curves.</summary>
    private static byte[] E1_BToAmBA(int outputs, params ushort[][] corners)
    {
        const int points = 8, b = 32, clut = b + 3 * 12;
        var data = points * outputs * 2;
        var a = clut + 20 + ((data + 3) & ~3);
        var d = new byte[a + outputs * 12];
        Encoding.ASCII.GetBytes("mBA ", d);
        d[8] = 3;
        d[9] = (byte)outputs;
        IccL_SetU32(d, 12, b);
        IccL_SetU32(d, 24, clut);
        IccL_SetU32(d, 28, (uint)a);
        for (var i = 0; i < 3; i++) Encoding.ASCII.GetBytes("curv", d.AsSpan(b + 12 * i));
        for (var i = 0; i < 3; i++) d[clut + i] = 2;
        d[clut + 16] = 2;
        for (var c = 0; c < points; c++)
            for (var k = 0; k < outputs; k++)
                BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(clut + 20 + 2 * (c * outputs + k)), corners[c % corners.Length][k]);
        for (var i = 0; i < outputs; i++) Encoding.ASCII.GetBytes("curv", d.AsSpan(a + 12 * i));
        return d;
    }

    /// <summary>
    /// A CMYK table (A2B1, PCS XYZ, two points per input): paper D50, K alone 0.06 · D50 (L* 29.4), all four inks 0.01 · D50 (L* 9.0), the other
    /// corners 0.5 · D50.
    /// </summary>
    private static byte[] E1_InkLimitedA2B1()
    {
        var corners = new ushort[16][];
        for (var i = 0; i < 16; i++) corners[i] = IccL_D50Words(0.5);
        corners[0] = IccL_D50Words();
        corners[1] = IccL_D50Words(0.06);
        corners[15] = IccL_D50Words(0.01);
        return IccL_Lut16(4, corners);
    }

    /// <summary>A CMYK profile with <see cref="E1_InkLimitedA2B1"/> and, if given, a BToA0 table; class and version as given.</summary>
    private static byte[] E1_InkLimited(byte[]? bToA0, string deviceClass = "prtr", byte version = 2)
    {
        var tags = new List<(string, byte[])> { ("A2B1", E1_InkLimitedA2B1()), ("wtpt", IccL_Xyz(0.9642, 1, 0.8249)) };
        if (bToA0 is not null) tags.Add(("B2A0", bToA0));
        return IccL_Profile("CMYK", "XYZ ", [.. tags], version, deviceClass);
    }

    /// <summary>A BToA0 that gives K alone for every PCS colour (the perceptual black of an ink-limited press is K, not all four inks).</summary>
    private static byte[] E1_KOnly16() => E1_BToA16(4, [0, 0, 0, 65535]);

    private static float[] E1_BlackPoint(byte[] profile, int intent)
    {
        var bp = new float[3];
        IccTransform.DetectBlackPoint(IccProfile.Parse(profile)!, intent, bp);
        return bp;
    }

    private static void E1_Close(Rgb expected, Rgb actual, int tolerance = 1)
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
            $"erwartet {expected}, ist {actual}");
    }

    private static Rgb E1_Srgb(float x, float y, float z)
    {
        var rgb = new float[3];
        IccColor.XyzToSrgb(x, y, z, rgb);
        return Rgb.FromUnit(rgb[0], rgb[1], rgb[2]);
    }

    /// <summary>The fill of „<paramref name="prefix"/> /CS0 cs values sc“ in a colour space written as text.</summary>
    private static (byte R, byte G, byte B) E1_In(string space, string values, string prefix, string resources = "") =>
        D_Rgb(D_Fill($"{prefix} /CS0 cs {values} sc 0 0 10 10 re f", $"/ColorSpace << /CS0 {space} >> {resources}"));

    private static Rgb E1_Rgb((byte R, byte G, byte B) c) => new(c.R, c.G, c.B);

    // ------------------------------------------------------------ absolute colorimetric (ICC-based)

    /// <summary>
    /// ICC.1 6.2.3 sentences 1–7 and 9.2.36 (tolerance, docs/FEHLERGEDAECHTNIS.md, No. 1129): without a usable media white point absolute
    /// colorimetric is relative colorimetric — the tinted paper profile with its wtpt renamed, cut to 8 bytes, of another type, with Y 0 or
    /// negative: the paper „0 0 0 0“ stays white 255. With the wtpt 0.9 · D50 it is 243.
    /// </summary>
    [Fact]
    public void IccAbsoluteColorimetric_WithoutUsableMediaWhite_AsRelative()
    {
        byte[] Tinted(Action<byte[], int>? spoil)
        {
            var p = (byte[])IccL_NaiveCmyk.Value.Clone();
            var at = (int)IccL_U32(p, 136);
            IccL_Xyz(0.9 * 0.9642, 0.9, 0.9 * 0.8249).CopyTo(p, at);
            spoil?.Invoke(p, at);
            return p;
        }
        Rgb Paper(byte[] p) => IccL_Space(p, 4).WithRendering(ColorSpace.AbsoluteColorimetric, ColorSpace.BpcDefault).ToRgb([0, 0, 0, 0]);
        Assert.InRange(Paper(Tinted(null)).R, 242, 244);
        var spoilt = new Action<byte[], int>[]
        {
            (p, _) => Encoding.ASCII.GetBytes("wtpX", p.AsSpan(132)),
            (p, _) => IccL_SetU32(p, 140, 8),
            (p, at) => Encoding.ASCII.GetBytes("XYZX", p.AsSpan(at)),
            (p, at) => IccL_SetU32(p, at + 12, 0),
            (p, at) => IccL_SetU32(p, at + 12, 0xFFFF0000),
        };
        foreach (var spoil in spoilt)
            Assert.True(Paper(Tinted(spoil)) is { R: >= 254, G: >= 254, B: >= 254 }, Paper(Tinted(spoil)).ToString());
    }

    /// <summary>
    /// ICC.1 6.2.3 sentences 8–9: for display profiles the media white is the PCS white, so absolute is relative colorimetric there — an RGB
    /// matrix profile of class „mntr“ carrying the measured white D65 (as version-2 display profiles often do) draws white 255 with
    /// /AbsoluteColorimetric; the same profile as an input profile („scnr“) draws its white as XYZ D65 in the PCS, bluish (within one step of
    /// that XYZ in sRGB).
    /// </summary>
    [Fact]
    public void IccAbsoluteColorimetric_DisplayProfile_MediaWhiteIsThePcsWhite()
    {
        (string, byte[])[] tags = [.. IccL_AdobeTags().Where(t => t.Item1 != "wtpt"), ("wtpt", IccL_Xyz(0.9505, 1, 1.089))];
        var display = IccL_Space(IccL_Profile("RGB ", "XYZ ", tags, 2, "mntr"), 3).WithRendering(ColorSpace.AbsoluteColorimetric, ColorSpace.BpcDefault);
        Assert.True(display.ToRgb([1, 1, 1]) is { R: >= 254, G: >= 254, B: >= 254 }, display.ToRgb([1, 1, 1]).ToString());
        var input = IccL_Space(IccL_Profile("RGB ", "XYZ ", tags, 2, "scnr"), 3).WithRendering(ColorSpace.AbsoluteColorimetric, ColorSpace.BpcDefault);
        E1_Close(E1_Srgb(0.9505f, 1, 1.089f), input.ToRgb([1, 1, 1]));
        Assert.True(input.ToRgb([1, 1, 1]).R < 240);
    }

    /// <summary>
    /// ICC.1 6.2.3 sentences 1–7 for images: 8-bit rows give the colours of single colours under absolute colorimetric — through the matrix
    /// shaper of an RGB matrix profile (input class, wtpt 0.9 · D50; white 243) within one step, through the grid of a CMYK table profile (the
    /// tinted paper) within three.
    /// </summary>
    [Fact]
    public void IccAbsoluteColorimetric_ImageRowsLikeSingleColours()
    {
        (string, byte[])[] tags = [.. IccL_AdobeTags().Where(t => t.Item1 != "wtpt"), ("wtpt", IccL_Xyz(0.9 * 0.9642, 0.9, 0.9 * 0.8249))];
        var rgb = IccL_Space(IccL_Profile("RGB ", "XYZ ", tags, 2, "scnr"), 3).WithRendering(ColorSpace.AbsoluteColorimetric, ColorSpace.BpcDefault);
        Assert.True(rgb.HasRow8);
        byte[] row = [255, 255, 255, 128, 64, 200, 10, 10, 10];
        var rgba = new byte[12];
        rgb.ConvertRow8(row, 3, rgba);
        for (var i = 0; i < 3; i++)
            E1_Close(rgb.ToRgb([row[3 * i] / 255.0, row[3 * i + 1] / 255.0, row[3 * i + 2] / 255.0]), new Rgb(rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2]));
        Assert.InRange(rgba[0], (byte)242, (byte)244);

        var tinted = (byte[])IccL_NaiveCmyk.Value.Clone();
        IccL_Xyz(0.9 * 0.9642, 0.9, 0.9 * 0.8249).CopyTo(tinted, (int)IccL_U32(tinted, 136));
        var cmyk = IccL_Space(tinted, 4).WithRendering(ColorSpace.AbsoluteColorimetric, ColorSpace.BpcDefault);
        byte[] inks = [0, 0, 0, 0, 30, 200, 90, 40, 0, 0, 0, 255];
        rgba = new byte[12];
        cmyk.ConvertRow8(inks, 3, rgba);
        for (var i = 0; i < 3; i++)
            E1_Close(cmyk.ToRgb([inks[4 * i] / 255.0, inks[4 * i + 1] / 255.0, inks[4 * i + 2] / 255.0, inks[4 * i + 3] / 255.0]),
                new Rgb(rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2]), 3);
        Assert.InRange(rgba[0], (byte)240, (byte)246);
    }

    /// <summary>
    /// ICC.1 9.2.28 and 8.10.2 step a: DToB3 (multiProcessElementsType) is not evaluated — absolute colorimetric comes from the media-relative
    /// table scaled with the media white, also when the profile carries a DToB3: the tinted paper with a DToB3 tag still draws 243.
    /// </summary>
    [Fact]
    public void IccAbsoluteColorimetric_DToB3_TableScaledInstead()
    {
        var tinted = (byte[])IccL_NaiveCmyk.Value.Clone();
        IccL_Xyz(0.9 * 0.9642, 0.9, 0.9 * 0.8249).CopyTo(tinted, (int)IccL_U32(tinted, 136));
        var mpet = new byte[16];
        Encoding.ASCII.GetBytes("mpet", mpet);
        BinaryPrimitives.WriteUInt16BigEndian(mpet.AsSpan(8), 4);
        BinaryPrimitives.WriteUInt16BigEndian(mpet.AsSpan(10), 3);
        var withD2B3 = IccL_WithTags(tinted, ("D2B3", mpet));
        var paper = IccL_Space(withD2B3, 4).WithRendering(ColorSpace.AbsoluteColorimetric, ColorSpace.BpcDefault).ToRgb([0, 0, 0, 0]);
        Assert.True(paper is { R: >= 242 and <= 244, G: >= 242 and <= 244, B: >= 242 and <= 244 }, paper.ToString());
    }

    // ------------------------------------------------------------ absolute colorimetric (CIE-based)

    /// <summary>
    /// ISO 32000-2, 8.6.5.8 (Table 69, AbsoluteColorimetric: no correction for the white point): CalGray, CalRGB and Lab take their XYZ as they
    /// are — under D65 their white (CalGray A 1, CalRGB 1 1 1 with an sRGB-like matrix, Lab 100 0 0) is XYZ D65 in the PCS, bluish (within one
    /// step of that XYZ in sRGB), relative colorimetric white 255; under D50 absolute and relative are the same (CalGray A 0.5).
    /// </summary>
    [Fact]
    public void CieBased_AbsoluteColorimetric_WhitePointNotAdapted()
    {
        const string matrix = "/Matrix [0.4124 0.2126 0.0193 0.3576 0.7152 0.1192 0.1805 0.0722 0.9505]";
        var bluish = E1_Srgb(0.9505f, 1, 1.089f);
        foreach (var (space, white) in new[] { ($"[/CalGray << {D_D65} >>]", "1"), ($"[/CalRGB << {D_D65} {matrix} >>]", "1 1 1"), ($"[/Lab << {D_D65} >>]", "100 0 0") })
        {
            E1_Close(bluish, E1_Rgb(E1_In(space, white, "/AbsoluteColorimetric ri")));
            Assert.True(E1_In(space, white, "/AbsoluteColorimetric ri").R < 240, space);
            Assert.True(E1_In(space, white, "") is { R: >= 254, G: >= 254, B: >= 254 }, space);
        }
        Assert.Equal(D_In($"[/CalGray << {D_D50} >>]", "0.5"), E1_In($"[/CalGray << {D_D50} >>]", "0.5", "/AbsoluteColorimetric ri"));
    }

    // ------------------------------------------------------------ black point compensation (ISO 18619)

    /// <summary>
    /// ISO 32000-2, 8.6.5.9 sentence 3 with ISO 18619 (AN001 General sentence 5; ICC.1 6.3.3.2): for perceptual tables of a version-4 profile
    /// the black point is the black of the perceptual reference medium (Y = 0.30911 / 89, neutral), not the black of the table: a version-4
    /// CMYK profile whose perceptual table has a 20 % black draws „0 0 0 1“ perceptual with compensation as that black moved by the reference
    /// black (a dark grey, as computed, about 48) — not black; the same table in a version-2 profile is compensated from its own black (below
    /// 12).
    /// </summary>
    [Fact]
    public void IccBlackPoint_Version4Perceptual_IsTheReferenceMediumBlack()
    {
        var table = IccL_Tag(IccL_LightBlack.Value, "A2B0")!;
        var v4 = IccL_Profile("CMYK", "Lab ", [("A2B0", table), ("wtpt", IccL_Xyz(0.9642, 1, 0.8249))], 4, "prtr");
        var bp = E1_BlackPoint(v4, ColorSpace.Perceptual);
        Assert.InRange(bp[1], 0.0034721f, 0.0034741f);
        Assert.InRange(bp[0], 0.9642f * 0.0034721f, 0.9642f * 0.0034741f);
        Assert.InRange(bp[2], 0.8249f * 0.0034721f, 0.8249f * 0.0034741f);

        var black = IccL_ToXyz(IccProfile.Parse(v4)!.ForIntent(0), 0, 0, 0, 1);
        float[] w = [0.9642f, 1, 0.8249f];
        var expected = E1_Srgb((black[0] - bp[0]) * w[0] / (w[0] - bp[0]), (black[1] - bp[1]) * w[1] / (w[1] - bp[1]), (black[2] - bp[2]) * w[2] / (w[2] - bp[2]));
        var drawn = IccL_Space(v4, 4).WithRendering(ColorSpace.Perceptual, ColorSpace.BpcDefault).ToRgb([0, 0, 0, 1]);
        E1_Close(expected, drawn);
        Assert.True(drawn.R > 30, drawn.ToString());

        var v2 = IccL_Profile("CMYK", "Lab ", [("A2B0", table), ("wtpt", IccL_Xyz(0.9642, 1, 0.8249))], 2, "prtr");
        Assert.True(IccL_Space(v2, 4).WithRendering(ColorSpace.Perceptual, ColorSpace.BpcDefault).ToRgb([0, 0, 0, 1]).R < 12);
    }

    /// <summary>
    /// ISO 18619 (as published for it): a version-4 profile with matrix and curves takes the darkest colorant through its relative tables also
    /// for perceptual — a grey profile whose curve starts at 0.04 has the black point Y 0.04, neutral, not the reference medium's black.
    /// </summary>
    [Fact]
    public void IccBlackPoint_Version4MatrixProfile_Perceptual_DarkestColorant()
    {
        var grey = IccL_Profile("GRAY", "XYZ ", [("kTRC", IccL_Curv(2621, 65535)), ("wtpt", IccL_Xyz(0.9642, 1, 0.8249))], 4, "mntr");
        var bp = E1_BlackPoint(grey, ColorSpace.Perceptual);
        Assert.InRange(bp[1], 0.0399f, 0.0401f);
        Assert.InRange(bp[0] / bp[1], 0.9641f, 0.9643f);
        Assert.InRange(bp[2] / bp[1], 0.8248f, 0.8250f);
        Assert.True(IccL_Space(grey, 1).WithRendering(ColorSpace.Perceptual, ColorSpace.BpcDefault).ToRgb([0]).R < 2);
    }

    /// <summary>
    /// ISO 18619 for output profiles of CMYK under relative colorimetric: the black point comes from the perceptual round trip (Lab 0/0/0 through
    /// BToA0, back through the relative table), which discounts an ink limit. A press whose all-inks corner is darker (L* 9) than its perceptual
    /// black (K alone, L* 29.4): the black point is Y 0.06, neutral — with BToA0 as lut16, lut8 and lutBToA, version 2 and 4 —, and K alone is
    /// drawn black; as an input profile, or without BToA0, the darkest colorant (all inks, Y 0.01) is the black point, and K alone stays a grey
    /// (sRGB 0.24–0.26).
    /// </summary>
    [Fact]
    public void IccBlackPoint_OutputCmykRelative_FromThePerceptualRoundTrip()
    {
        foreach (var (profile, label) in new[]
                 {
                     (E1_InkLimited(E1_KOnly16()), "lut16"), (E1_InkLimited(E1_BToA8(4, [0, 0, 0, 255])), "lut8"),
                     (E1_InkLimited(E1_BToAmBA(4, [0, 0, 0, 65535]), version: 4), "mBA v4"),
                 })
        {
            var bp = E1_BlackPoint(profile, ColorSpace.RelativeColorimetric);
            Assert.True(bp[1] is > 0.0599f and < 0.0601f, $"{label}: {bp[1]}");
            Assert.InRange(bp[0] / bp[1], 0.9641f, 0.9643f);
            var k = new float[3];
            new IccTransform(IccProfile.Parse(profile)!).ToSrgb([0, 0, 0, 1], k);
            Assert.True(k.All(v => v < 0.005f), $"{label}: {string.Join(" ", k)}");
        }
        foreach (var (profile, label) in new[] { (E1_InkLimited(E1_KOnly16(), "scnr"), "scnr"), (E1_InkLimited(null), "ohne B2A0") })
        {
            var bp = E1_BlackPoint(profile, ColorSpace.RelativeColorimetric);
            Assert.True(bp[1] is > 0.0099f and < 0.0101f, $"{label}: {bp[1]}");
            var k = new float[3];
            new IccTransform(IccProfile.Parse(profile)!).ToSrgb([0, 0, 0, 1], k);
            Assert.True(k.All(v => v is > 0.24f and < 0.26f), $"{label}: {string.Join(" ", k)}");
        }
    }

    /// <summary>
    /// ISO 18619 for the bundled FOGRA39 profile (output, CMYK, version 2, with BToA0): under relative colorimetric the black point is the
    /// perceptual round trip's — neutral, L* 9.3–9.8, darker than the all-inks black of its relative table (about L* 9.8, beyond the ink limit).
    /// </summary>
    [Fact]
    public void IccBlackPoint_Fogra39_RoundTripBelowTheAllInksBlack()
    {
        var fogra = IccProfile.Parse(CmykProfile.Bytes())!;
        var bp = new float[3];
        Assert.True(IccTransform.DetectBlackPoint(fogra, ColorSpace.RelativeColorimetric, bp));
        var (l, a, b) = IccColor.XyzToLab(bp[0], bp[1], bp[2]);
        Assert.InRange(l, 9.3f, 9.8f);
        Assert.InRange(a, -0.01f, 0.01f);
        Assert.InRange(b, -0.01f, 0.01f);
        var all = IccL_ToXyz(fogra, 1, 1, 1, 1);
        Assert.True(IccColor.XyzToLab(all[0], all[1], all[2]).L > l + 0.1f);
    }

    /// <summary>
    /// ISO 18619 (AN001 General sentences 1–5): a coloured source black is made neutral — the black point keeps its L* with a* = b* = 0. A CMYK
    /// profile whose black is sRGB 0.25/0.2/0.15 has a black point of the same lightness, neutral.
    /// </summary>
    [Fact]
    public void IccBlackPoint_ColouredBlack_NeutralOfTheSameLightness()
    {
        var warm = CmykProfile.Bake((c, m, y, k) => (0.25 + 0.75 * (1 - c) * (1 - k), 0.2 + 0.8 * (1 - m) * (1 - k), 0.15 + 0.85 * (1 - y) * (1 - k)));
        var bp = E1_BlackPoint(warm, ColorSpace.RelativeColorimetric);
        var (l, a, b) = IccColor.XyzToLab(bp[0], bp[1], bp[2]);
        var black = IccL_ToXyz(IccProfile.Parse(warm)!, 1, 1, 1, 1);
        var (l0, a0, b0) = IccColor.XyzToLab(black[0], black[1], black[2]);
        Assert.True(Math.Abs(a0) > 1 || Math.Abs(b0) > 1, $"{a0} {b0}");
        Assert.InRange(l, l0 - 0.01f, l0 + 0.01f);
        Assert.InRange(a, -0.01f, 0.01f);
        Assert.InRange(b, -0.01f, 0.01f);
    }

    /// <summary>
    /// Security (ISO 18619 round trip): a BToA0 whose CLUT claims more points than the profile holds, or whose grid is 1, is passed over — the
    /// darkest colorant applies, nothing is read beyond the tag.
    /// </summary>
    [Fact]
    public void IccBlackPoint_BrokenBToA0_PassedOver()
    {
        var big = E1_KOnly16();
        big[10] = 255;
        var one = E1_KOnly16();
        one[10] = 1;
        foreach (var lut in new[] { big, one, E1_KOnly16()[..40] })
            Assert.InRange(E1_BlackPoint(E1_InkLimited(lut), ColorSpace.RelativeColorimetric)[1], 0.0099f, 0.0101f);
    }

    // ------------------------------------------------------------ /BlackPoint

    /// <summary>
    /// 8.6.5.2 sentences 20T–23T, 8.6.5.3 sentences 12T–14T: a /BlackPoint that is not three non-negative numbers is passed over (lenient, as
    /// before), as is one not darker than the white: CalGray (D50) with [−0.1 0.1 0.1], [0.1 0.1], [0.1 /X 0.1], [2 2 2] draws A 0.1 as without
    /// the entry (grey 88–90).
    /// </summary>
    [Fact]
    public void CieBased_BlackPoint_InvalidPassedOver()
    {
        foreach (var bp in new[] { "[-0.1 0.1 0.1]", "[0.1 0.1]", "[0.1 /X 0.1]", "[2 2 2]" })
            D_Grey(D_In($"[/CalGray << {D_D50} /BlackPoint {bp} >>]", "0.1"), 88, 90);
    }

    /// <summary>
    /// 8.6.5.9 sentences 4 and 6: the /BlackPoint is moved onto the display's black as black point compensation — not under /UseBlackPtComp
    /// /OFF and not with /AbsoluteColorimetric: CalGray (D50, black point 0.1 · D50) at A 0.1 is then grey 88–90; with /ON black.
    /// </summary>
    [Fact]
    public void CieBased_BlackPoint_NotUnderOffOrAbsolute()
    {
        const string space = $"[/CalGray << {D_D50} /BlackPoint [0.09642 0.1 0.08249] >>]";
        const string states = "/ExtGState << /Off << /UseBlackPtComp /OFF >> /On << /UseBlackPtComp /ON >> >>";
        D_Grey(E1_In(space, "0.1", "/Off gs", states), 88, 90);
        D_Grey(E1_In(space, "0.1", "/AbsoluteColorimetric ri"), 88, 90);
        D_Grey(E1_In(space, "0.1", "/On gs", states), 0, 1);
        D_Grey(E1_In(space, "0.1", "/Perceptual ri"), 0, 1);
    }

    /// <summary>
    /// 8.6.6.3 with 8.6.5.4: an Indexed space with a Lab base takes the /BlackPoint of the base into its palette — entry 0 at L* 26.74 under a
    /// black point of 0.05 · D50 is black.
    /// </summary>
    [Fact]
    public void CieBased_BlackPoint_IndexedBase()
    {
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        var lab = new PdfArray([TestPdf.N("Lab"), TestPdf.Dict(("WhitePoint", TestPdf.Box(0.9642, 1, 0.8249)), ("BlackPoint", TestPdf.Box(0.04821, 0.05, 0.041245)),
            ("Range", TestPdf.Box(-128, 127, -128, 127)))]);
        var indexed = ColorSpace.Create(new PdfArray([TestPdf.N("Indexed"), lab, PdfInteger.Of(0), new PdfString([(byte)Math.Round(26.74 * 2.55), 128, 128])]), null, doc);
        Assert.True(indexed.ToRgb([0]) is { R: <= 3, G: <= 3, B: <= 3 }, indexed.ToRgb([0]).ToString());
    }
}
