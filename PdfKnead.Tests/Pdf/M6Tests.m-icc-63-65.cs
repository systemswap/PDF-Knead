using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M6 draft m-icc-63-65: ICC.1:2022 sections 6.3 to 6.5 (PCS, its encodings, the conversion XYZ ↔ Lab, device encodings). Uses the profile
/// helpers <c>IccL_*</c> of the draft l-icc-1-62-an001.
/// </summary>
public sealed partial class M6NormTests
{
    /// <summary>
    /// ICC.1 6.3.2.2 sentences 1–13 and 14–27: PDF-Knead takes the PCS values of a table as media-relative values; the media white point does not
    /// enter. A grey profile with A2B1 whose paper (device 0) is D50 gives D50 with a wtpt of 0.9 · D50 exactly as with a wtpt of D50.
    /// </summary>
    [Fact]
    public void IccMediaWhitePoint_NoInfluenceOnPcsValues()
    {
        var table = IccL_Lut16(1, IccL_D50Words(), [0, 0, 0]);
        var tinted = IccProfile.Parse(IccL_Profile("GRAY", "XYZ ", [("A2B1", table), ("wtpt", IccL_Xyz(0.9 * 0.9642, 0.9, 0.9 * 0.8249))]))!;
        var plain = IccProfile.Parse(IccL_Profile("GRAY", "XYZ ", [("A2B1", table), ("wtpt", IccL_Xyz(0.9642, 1, 0.8249))]))!;
        var xyz = IccL_ToXyz(tinted, 0);
        Assert.InRange(xyz[0], 0.9641f, 0.9643f);
        Assert.InRange(xyz[1], 0.9999f, 1.0001f);
        Assert.InRange(xyz[2], 0.8248f, 0.8250f);
        Assert.Equal(IccL_ToXyz(plain, 0), xyz);
        Assert.Equal(IccL_ToXyz(plain, 0.4f), IccL_ToXyz(tinted, 0.4f));
    }

    /// <summary>
    /// ICC.1 6.3.2.2 sentences 28–42 (records today's behaviour; design decision on absolute colorimetric pending): the bundled FOGRA39 profile has a media
    /// white darker than D50, yet with the absolute colorimetric intent its paper "0 0 0 0" is drawn white 255 — the media white is not
    /// multiplied in.
    /// </summary>
    [Fact]
    public void IccAbsoluteColorimetric_Fogra39PaperStaysWhite_TodaysBehaviour()
    {
        var fogra = CmykProfile.Bytes();
        var wtpt = IccL_Tag(fogra, "wtpt")!;
        Assert.True(BinaryPrimitives.ReadInt32BigEndian(wtpt.AsSpan(12)) / 65536.0 < 0.95, "wtpt Y");
        var paper = IccL_Space(fogra, 4).WithRendering(ColorSpace.AbsoluteColorimetric, ColorSpace.BpcDefault).ToRgb([0, 0, 0, 0]);
        Assert.True(paper is { R: >= 254, G: >= 254, B: >= 254 }, paper.ToString());
    }

    /// <summary>
    /// ICC.1 6.3.2.3 sentences 1–18 and 6.4 sentences 1 and 6: PCSLAB from PCSXYZ by the CIE formulas with the PCS white as reference: D50 gives
    /// L* 100, a* = b* = 0; 0.18 · D50 gives L* 49.496, neutral; Lab 50/20/−30 back to XYZ as the formula gives it (within 1e-5).
    /// </summary>
    [Fact]
    public void IccXyzToLab_ExactValuesAgainstTheCieFormulas()
    {
        var (l, a, b) = IccColor.XyzToLab(0.9642f, 1, 0.8249f);
        Assert.InRange(l, 99.999f, 100.001f);
        Assert.InRange(a, -0.001f, 0.001f);
        Assert.InRange(b, -0.001f, 0.001f);
        (l, a, b) = IccColor.XyzToLab(0.18f * 0.9642f, 0.18f, 0.18f * 0.8249f);
        Assert.InRange(l, 49.495f, 49.497f);
        Assert.InRange(a, -0.001f, 0.001f);
        Assert.InRange(b, -0.001f, 0.001f);
        var xyz = new float[3];
        IccColor.LabToXyz(50, 20, -30, xyz);
        double fy = 66 / 116.0, fx = fy + 20 / 500.0, fz = fy + 30 / 200.0;
        Assert.InRange(xyz[0], 0.9642 * fx * fx * fx - 1e-5, 0.9642 * fx * fx * fx + 1e-5);
        Assert.InRange(xyz[1], fy * fy * fy - 1e-5, fy * fy * fy + 1e-5);
        Assert.InRange(xyz[2], 0.8249 * fz * fz * fz - 1e-5, 0.8249 * fz * fz * fz + 1e-5);
    }

    /// <summary>
    /// ICC.1 6.4 sentences 1 and 6: for negative XYZ the straight part of the conversion below 0.008856 goes on below zero: X = −0.01 gives
    /// a* ≈ −40.4 (no cube root of a negative number), and the way back gives X = −0.01 again.
    /// </summary>
    [Fact]
    public void IccXyzToLab_NegativeXyz_LinearPartContinued()
    {
        var (l, a, b) = IccColor.XyzToLab(-0.01f, 0, 0);
        Assert.InRange(l, -0.001f, 0.001f);
        Assert.InRange(a, -40.5f, -40.3f);
        Assert.InRange(b, -0.001f, 0.001f);
        var xyz = new float[3];
        IccColor.LabToXyz(l, a, b, xyz);
        Assert.InRange(xyz[0], -0.01001f, -0.00999f);
        Assert.InRange(xyz[1], -1e-6f, 1e-6f);
    }

    /// <summary>
    /// ICC.1 6.3.3.3 (sentences 1–3, 4–20, 21–40, 41–60, 61–76, 77–79 and 81, 82–84 with 85T–100T): the perceptual reference medium gamut concerns
    /// makers of perceptual tables. PDF-Knead's own profiles hold neither A2B0 nor the gamut tags rig0/rig2, and a profile with A2B0 and
    /// rig0 = prmg converts as without the tag.
    /// </summary>
    [Fact]
    public void IccPrmgGamutTag_NotWrittenAndIgnored()
    {
        foreach (var p in new[] { CalibratedProfile.Srgb(), CalibratedProfile.RommRgb() })
            foreach (var sig in new[] { "A2B0", "rig0", "rig2" }) Assert.Null(IccL_Tag(p, sig));
        byte[] rig0 = [.. "sig "u8, 0, 0, 0, 0, .. "prmg"u8];
        var table = IccL_Lut16(1, IccL_D50Words(), IccL_D50Words(0.1));
        var with = IccProfile.Parse(IccL_Grey(("A2B0", table), ("rig0", rig0)))!.ForIntent(0);
        var without = IccProfile.Parse(IccL_Grey(("A2B0", table)))!.ForIntent(0);
        foreach (var v in new[] { 0f, 0.5f, 1f }) Assert.Equal(IccL_ToXyz(without, v), IccL_ToXyz(with, v));
    }

    /// <summary>A grey profile (version 4) whose lutAToB table maps device 0 to L* given by the word <paramref name="black"/> and device 1 to L* 100.</summary>
    private static byte[] IccM_GreyLab(string tag, ushort black) =>
        IccL_Profile("GRAY", "Lab ", [(tag, IccL_LutAToB(1, [black, 0x8080, 0x8080], [0xFFFF, 0x8080, 0x8080])), ("wtpt", IccL_Xyz(0.9642, 1, 0.8249))], version: 4);

    /// <summary>sRGB value (0–1) of a neutral L*, by the CIE and sRGB formulas.</summary>
    private static double IccM_SrgbOfLightness(double l)
    {
        var f = (l + 16) / 116;
        var y = f > 6.0 / 29 ? f * f * f : 3 * (6.0 / 29) * (6.0 / 29) * (f - 4.0 / 29);
        return y <= 0.0031308 ? 12.92 * y : 1.055 * Math.Pow(y, 1 / 2.4) - 0.055;
    }

    /// <summary>
    /// ICC.1 6.3.3.4 sentences 1–4 and 5: PDF-Knead converts colorimetrically to sRGB without a model of the viewing surround. A perceptual table
    /// from the white of the reference medium (L* 100) to its black (L* 3.1373, 0808h) gives, without black point compensation, white 255 and
    /// the black as the sRGB value of L* 3.14 — unchanged by any surround.
    /// </summary>
    [Fact]
    public void IccPerceptualTable_PrmWhiteAndBlack_NoSurroundAdaptation()
    {
        var profile = IccProfile.Parse(IccM_GreyLab("A2B0", 0x0808))!.ForIntent(0);
        var transform = new IccTransform(profile, compensateBlack: false);
        var rgb = new float[3];
        transform.ToSrgb([1f], rgb);
        Assert.All(rgb, c => Assert.InRange(c, 0.998f, 1f));
        transform.ToSrgb([0f], rgb);
        var black = IccM_SrgbOfLightness(0x0808 / 65535.0 * 100);
        Assert.All(rgb, c => Assert.InRange(c, black - 0.002, black + 0.002));
    }

    /// <summary>
    /// ICC.1 6.3.4.1 sentences 1–2, 5–7 (records today's behaviour; design decision on read tolerances pending): there is no 8-bit PCSXYZ; a lut8 table
    /// in a profile with PCS XYZ is still read, its bytes on the scale of the 16-bit encoding: FFh as 1.99997, 80h as about 1.004.
    /// </summary>
    [Fact]
    public void IccLut8_PcsXyz_ReadLeniently_TodaysBehaviour()
    {
        var profile = IccProfile.Parse(IccL_Grey(("A2B0", IccL_Lut8(1, [0x80, 0x80, 0x80], [0xFF, 0xFF, 0xFF]))));
        Assert.NotNull(profile);
        Assert.InRange(IccL_ToXyz(profile!, 1)[1], 1.9999f, 2.0001f);
        Assert.InRange(IccL_ToXyz(profile!, 0)[1], 128 / 255f * 65535 / 32768 - 1e-4f, 128 / 255f * 65535 / 32768 + 1e-4f);
    }

    /// <summary>
    /// ICC.1 6.3.4.1 sentences 8–12: the encodings do not quantise the PCS: a lut16 with PCS XYZ from 0 to 8000h, read at a third between its
    /// points, gives exactly a third (within 1e-6), not the nearest step of 1/32768.
    /// </summary>
    [Fact]
    public void IccLut16_PcsXyz_InterpolatedValueNotQuantised()
    {
        var profile = IccProfile.Parse(IccL_Grey(("A2B0", IccL_Lut16(1, [0, 0, 0], [0x8000, 0x8000, 0x8000]))))!;
        var y = IccL_ToXyz(profile, 1 / 3f)[1];
        Assert.InRange(y, 1 / 3.0 - 1e-6, 1 / 3.0 + 1e-6);
    }

    /// <summary>
    /// ICC.1 6.3.4.2 sentences 1–3, 5, 6T–17T, 19T and 6.3.4.3 sentences 30T–35T: 16-bit PCSXYZ is u1Fixed15Number — 0000h is 0, 4000h is 0.5
    /// (linear), 8000h is 1.0 and FFFFh 1.99997; Lab 0/0/0 (the perfect absorber) is XYZ 0.
    /// </summary>
    [Fact]
    public void IccPcsXyz16_ZeroHalfOneAndMaximum()
    {
        // four corners of a two-input table: device (0,0), (0,1), (1,0), (1,1)
        var profile = IccProfile.Parse(IccL_Profile("2CLR", "XYZ ",
            [("A2B0", IccL_Lut16(2, [0, 0, 0], [0x4000, 0x4000, 0x4000], [0x8000, 0x8000, 0x8000], [0xFFFF, 0xFFFF, 0xFFFF]))]))!;
        foreach (var (device, value) in new[] { ((0f, 0f), 0.0), ((0f, 1f), 0.5), ((1f, 0f), 1.0), ((1f, 1f), 65535 / 32768.0) })
            Assert.All(IccL_ToXyz(profile, device.Item1, device.Item2), c => Assert.InRange(c, value - 1e-5, value + 1e-5));
        var xyz = new float[3];
        IccColor.LabToXyz(0, 0, 0, xyz);
        Assert.All(xyz, c => Assert.InRange(c, -1e-7f, 1e-7f));
    }

    /// <summary>
    /// ICC.1 6.3.4.2 sentences 20T–25T, 26T–39T and 47T, 6.3.4.3 sentences 1–2 and 4T–17T (and 6.3.4.1 sentence 1, lutAToB with PCSLAB): in the
    /// 16-bit Lab encoding L* 0…100 is 0000h…FFFFh, a* and b* −128 / 0 / +127 are 0000h / 8080h / FFFFh: FFFFh/8080h/8080h is the media white D50
    /// (within 1e-4), 0000h/8080h/8080h is black, the ends of the a* and b* axes read as −128 and +127.
    /// </summary>
    [Fact]
    public void IccLutAToB_PcsLab16_WhiteBlackAndAxisEnds()
    {
        const ushort half = 0x8000, zero = 0x8080;
        var profile = IccProfile.Parse(IccL_Profile("RGB ", "Lab ", [("A2B0", IccL_LutAToB(3,
            [0xFFFF, zero, zero], [0, zero, zero], [half, 0, zero], [half, 0xFFFF, zero],
            [half, zero, 0], [half, zero, 0xFFFF], [half, zero, zero], [0x0808, zero, zero]))], version: 4))!;
        float[] At(int corner) => IccL_ToXyz(profile, corner >> 2 & 1, corner >> 1 & 1, corner & 1);
        var white = At(0);
        Assert.InRange(white[0], 0.9641f, 0.9643f);
        Assert.InRange(white[1], 0.9999f, 1.0001f);
        Assert.InRange(white[2], 0.8248f, 0.8250f);
        Assert.All(At(1), c => Assert.InRange(c, -1e-6f, 1e-6f));
        var l50 = half / 65535f * 100;
        foreach (var (corner, a, b) in new[] { (2, -128f, 0f), (3, 127f, 0f), (4, 0f, -128f), (5, 0f, 127f), (6, 0f, 0f) })
        {
            var xyz = At(corner);
            var lab = IccColor.XyzToLab(xyz[0], xyz[1], xyz[2]);
            Assert.InRange(lab.L, l50 - 0.01f, l50 + 0.01f);
            Assert.InRange(lab.A, a - 0.01f, a + 0.01f);
            Assert.InRange(lab.B, b - 0.01f, b + 0.01f);
        }
        var prm = At(7);
        Assert.InRange(IccColor.XyzToLab(prm[0], prm[1], prm[2]).L, 3.1363f, 3.1383f);
    }

    /// <summary>
    /// ICC.1 6.3.4.2 sentences 20T–25T, 6.3.4.3 sentences 1–2, 4T–17T and 36T–53T (8-bit part): in lut8 PCSLAB L* 100 is FFh, a* = b* = 0 is 80h:
    /// FFh/80h/80h is D50 (within 1e-4), 08h/80h/80h is the black of the reference medium, L* 3.137, 00h/80h/80h is black.
    /// </summary>
    [Fact]
    public void IccLut8_PcsLab8_WhiteAndPrmBlack()
    {
        var profile = IccProfile.Parse(IccL_Profile("2CLR", "Lab ", [("A2B0", IccL_Lut8(2, [0xFF, 0x80, 0x80], [0x08, 0x80, 0x80], [0, 0x80, 0x80], [0, 0x80, 0x80]))]))!;
        var white = IccL_ToXyz(profile, 0, 0);
        Assert.InRange(white[0], 0.9641f, 0.9643f);
        Assert.InRange(white[1], 0.9999f, 1.0001f);
        Assert.InRange(white[2], 0.8248f, 0.8250f);
        var prm = IccL_ToXyz(profile, 0, 1);
        var (l, a, b) = IccColor.XyzToLab(prm[0], prm[1], prm[2]);
        Assert.InRange(l, 3.136f, 3.138f);
        Assert.InRange(a, -0.01f, 0.01f);
        Assert.InRange(b, -0.01f, 0.01f);
        Assert.All(IccL_ToXyz(profile, 1, 0), c => Assert.InRange(c, -1e-6f, 1e-6f));
    }

    /// <summary>
    /// ICC.1 6.3.4.2 sentence 43T: lut16Type uses the legacy 16-bit Lab encoding of version 2 (L* 100 = FF00h, a* = b* = 0 at 8000h):
    /// FF00h/8000h/8000h is exactly D50 (within 1e-4) — the new encoding would give L* 99.6 and a*, b* slightly below 0.
    /// </summary>
    [Fact]
    public void IccLut16_LegacyLabEncoding_FF00IsExactlyWhite()
    {
        var profile = IccProfile.Parse(IccL_Profile("GRAY", "Lab ", [("A2B0", IccL_Lut16(1, [0xFF00, 0x8000, 0x8000], [0, 0x8000, 0x8000]))]))!;
        var xyz = IccL_ToXyz(profile, 0);
        Assert.InRange(xyz[0], 0.9641f, 0.9643f);
        Assert.InRange(xyz[1], 0.9999f, 1.0001f);
        Assert.InRange(xyz[2], 0.8248f, 0.8250f);
    }

    /// <summary>
    /// ICC.1 6.3.4.3 sentences 36T–53T: the black of the reference medium in the perceptual PCS is L* 3.1373 (0808h). Read as given: a grey
    /// lutAToB with that black gives L* 3.1373; drawn with black point compensation it becomes sRGB black, with /UseBlackPtComp /OFF the sRGB value
    /// of L* 3.14.
    /// </summary>
    [Fact]
    public void IccPrmBlack_ReadAsGiven_CompensatedOnlyWithBpc()
    {
        var data = IccM_GreyLab("A2B0", 0x0808);
        var xyz = IccL_ToXyz(IccProfile.Parse(data)!, 0);
        Assert.InRange(IccColor.XyzToLab(xyz[0], xyz[1], xyz[2]).L, 3.1363f, 3.1383f);
        var space = IccL_Space(data, 1);
        var compensated = space.ToRgb([0]);
        Assert.True(compensated is { R: <= 1, G: <= 1, B: <= 1 }, compensated.ToString());
        var kept = space.WithRendering(ColorSpace.RelativeColorimetric, ColorSpace.BpcOff).ToRgb([0]);
        var expected = (int)Math.Round(IccM_SrgbOfLightness(0x0808 / 65535.0 * 100) * 255);
        Assert.True(Math.Abs(kept.R - expected) <= 1 && kept.R == kept.G && kept.G == kept.B, $"{kept} / {expected}");
    }

    /// <summary>
    /// ICC.1 6.3.4.3 sentences 66T–70T: the scaling of a zero black in perceptual tables before version 4 is asked of profile makers; PDF-Knead reads
    /// such a table as it is: a version-2 profile whose A2B0 black is XYZ 0 gives 0, and sRGB black with and without black point compensation.
    /// </summary>
    [Fact]
    public void IccVersion2PerceptualZeroBlack_NotAdjusted()
    {
        var profile = IccProfile.Parse(IccL_Grey(("A2B0", IccL_Lut16(1, [0, 0, 0], IccL_D50Words()))))!.ForIntent(0);
        Assert.All(IccL_ToXyz(profile, 0), c => Assert.Equal(0f, c));
        var rgb = new float[3];
        foreach (var bpc in new[] { true, false })
        {
            new IccTransform(profile, bpc).ToSrgb([0f], rgb);
            Assert.All(rgb, c => Assert.Equal(0f, c));
        }
    }

    /// <summary>
    /// ICC.1 6.4 sentences 2–3: converting into an integer encoding clips per component. The baked test profile writes a colour whose a* lies above
    /// 128 with the a* word FFFFh, while its L* and b* words keep their own values.
    /// </summary>
    [Fact]
    public void IccBakedTable_LabOutsideRange_ClippedPerComponent()
    {
        var baked = CmykProfile.Bake((c, m, y, k) => c == 1 && m == 0 && y == 0 && k == 0 ? (1, -3, 1) : (1, 1, 1));
        // header 128, table of two tags, wtpt (20) → lut16 at 176; its curves of two entries for four inputs → the grid at 244, C slowest
        Assert.Equal(176u, IccL_U32(baked, 148));
        var at = 244 + 6 * (16 * 17 * 17 * 17);
        var xyz = new float[3];
        IccColor.SrgbToXyz(1, -3, 1, xyz);
        var (l, a, b) = IccColor.XyzToLab(xyz[0], xyz[1], xyz[2]);
        Assert.True(a > 128, $"a* {a}");
        Assert.InRange(b, -127, 126);
        Assert.Equal((ushort)Math.Round(l / 100 * 65280), BinaryPrimitives.ReadUInt16BigEndian(baked.AsSpan(at)));
        Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16BigEndian(baked.AsSpan(at + 2)));
        Assert.Equal((ushort)Math.Round((b + 128) * 256), BinaryPrimitives.ReadUInt16BigEndian(baked.AsSpan(at + 4)));
    }

    /// <summary>
    /// ICC.1 6.5 sentences 1–2: device values 0.0…1.0 are 0…65535 in 16 bits: a 16-bit image in an ICC space draws FFFFh as the colour of 1.0 and
    /// 8000h as the colour of 32768/65535 — exactly the colour of the space at that value, not of 128/255 through the 8-bit grid (before, one
    /// step off in blue; docs/FEHLERGEDAECHTNIS.md, No. 1126).
    /// </summary>
    [Fact]
    public void IccImage16Bit_FullScaleIsOne_HalfIsHalf()
    {
        var profile = IccL_AdobeRgb();
        PdfArray? icc = null;
        byte[] samples = [0xFF, 0xFF, 0, 0, 0x80, 0, 0x80, 0, 0x80, 0, 0x80, 0];
        var (doc, list) = InterpreterTests.Page("q 2 0 0 1 0 0 cm /Im Do Q", pdf =>
        {
            icc = new PdfArray([TestPdf.N("ICCBased"), TestPdf.Ref(pdf.Add(new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(3))), profile)))]);
            var image = new PdfStream(TestPdf.Dict(("Type", TestPdf.N("XObject")), ("Subtype", TestPdf.N("Image")), ("Width", PdfInteger.Of(2)),
                ("Height", PdfInteger.Of(1)), ("BitsPerComponent", PdfInteger.Of(16)), ("ColorSpace", icc)), samples);
            return TestPdf.Dict(("XObject", TestPdf.Dict(("Im", TestPdf.Ref(pdf.Add(image))))));
        });
        using (doc)
        {
            var shown = PdfImageDecoder.Decode(list.Images[0], doc)!;
            // the space from the profile itself (the array above refers into the builder, not into the opened document)
            var space = ColorSpace.Create(new PdfArray([TestPdf.N("ICCBased"), new PdfStream(TestPdf.Dict(("N", PdfInteger.Of(3))), profile)]), null, doc);
            Assert.False(ReferenceEquals(space, ColorSpace.DeviceRgb));
            const double half = 32768 / 65535.0;
            Rgb[] expected = [space.ToRgbExact([1, 0, half]), space.ToRgbExact([half, half, half])];
            var report = "";
            var off = false;
            for (var p = 0; p < 2; p++)
            {
                var got = new Rgb(shown.Rgba[4 * p], shown.Rgba[4 * p + 1], shown.Rgba[4 * p + 2]);
                off |= got != expected[p];
                report += $"{p}: {got} / {expected[p]}; ";
            }
            Assert.False(off, report);
        }
    }
}
