using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>M6 draft p-icc-92 (ICC.1:2022, 9.2: the single tags). Builders from the file of draft o (prefix IccO_).</summary>
public sealed partial class M6NormTests
{
    /// <summary>A tag of a type with the given content behind the reserved field ("hex:…" for bytes, otherwise ASCII without a zero byte); empty: meaningless content.</summary>
    private static byte[] IccP_Tag(string type, string payload)
    {
        if (payload.Length == 0) return IccO_Junk(type);
        var bytes = payload.StartsWith("hex:", StringComparison.Ordinal) ? Convert.FromHexString(payload[4..]) : Encoding.ASCII.GetBytes(payload);
        return [.. Encoding.ASCII.GetBytes(type), 0, 0, 0, 0, .. bytes];
    }

    /// <summary>
    /// ICC.1, 9.2.6–9.2.12 (B2A0–B2A2, B2D0–B2D3), 9.2.13 calt, 9.2.14 targ (also without the zero byte and with a wrong identifier), 9.2.16
    /// chrm, 9.2.17 cicp (also against the colorants), 9.2.18 clro, 9.2.19 clrt, 9.2.20 clot, 9.2.21 ciis (every state of Table 26 and an
    /// unknown one), 9.2.29 gamt, 9.2.33 lumi, 9.2.34 meas, 9.2.38 resp, 9.2.39 rig0, 9.2.40–9.2.42 pre0–pre2, 9.2.44 pseq, 9.2.45 psid,
    /// 9.2.48 rig2, 9.2.49 tech, 9.2.50 vued, 9.2.51 view; also 9.1 sentences 14 and 40–44: a tag PDF-Knead neither evaluates nor writes —
    /// added to a readable matrix profile and to a readable CMYK table profile, with meaningless or contradicting content — leaves the profile
    /// readable and every colour of every intent as it was.
    /// </summary>
    [Theory]
    [InlineData("B2A0", "mft2", "")]
    [InlineData("B2A1", "mft2", "")]
    [InlineData("B2A2", "mBA ", "")]
    [InlineData("B2D0", "mpet", "")]
    [InlineData("B2D1", "mpet", "")]
    [InlineData("B2D2", "mpet", "")]
    [InlineData("B2D3", "mpet", "")]
    [InlineData("calt", "dtim", "hex:07EA000B0019000000000000")]
    [InlineData("targ", "text", "ICCHDAT Testziel ohne Nullbyte")]
    [InlineData("targ", "text", "XYZ1234 falsche Kennung")]
    [InlineData("chrm", "chrm", "")]
    [InlineData("cicp", "cicp", "hex:09100001")]
    [InlineData("clro", "clro", "")]
    [InlineData("clrt", "clrt", "")]
    [InlineData("clot", "clrt", "")]
    [InlineData("ciis", "sig ", "scoe")]
    [InlineData("ciis", "sig ", "sape")]
    [InlineData("ciis", "sig ", "fpce")]
    [InlineData("ciis", "sig ", "rhoc")]
    [InlineData("ciis", "sig ", "rpoc")]
    [InlineData("ciis", "sig ", "qqqq")]
    [InlineData("gamt", "mft2", "")]
    [InlineData("lumi", "XYZ ", "hex:005000000050000000500000")]
    [InlineData("meas", "meas", "")]
    [InlineData("resp", "rcs2", "")]
    [InlineData("rig0", "sig ", "prmg")]
    [InlineData("rig0", "sig ", "abcd")]
    [InlineData("pre0", "mft2", "")]
    [InlineData("pre1", "mft2", "")]
    [InlineData("pre2", "mft2", "")]
    [InlineData("pseq", "pseq", "")]
    [InlineData("psid", "psid", "")]
    [InlineData("rig2", "sig ", "prmg")]
    [InlineData("tech", "sig ", "fscn")]
    [InlineData("tech", "sig ", "zzzz")]
    [InlineData("vued", "mluc", "")]
    [InlineData("view", "view", "")]
    public void Icc9_2_TagNotEvaluated_ChangesNoColour(string sig, string type, string payload)
    {
        var tag = IccP_Tag(type, payload);
        foreach (var (plain, channels) in new[] { (IccO_Matrix(), 3), (IccO_Cmyk("prtr", ("A2B0", IccO_Plain)), 4) })
        {
            var with = IccO_With(plain, [(sig, tag)]);
            Assert.NotNull(IccProfile.Parse(with));
            IccO_SameColours(plain, with, channels);
        }
    }

    /// <summary>
    /// ICC.1, 9.2.6: a profile that has only a B2A0 table (PCS → device), neither an A2B table nor matrix and curves, gives no way device → PCS:
    /// it is unreadable, the ICCBased space takes its /Alternate.
    /// </summary>
    [Fact]
    public void Icc9_2_6_OnlyB2A0_Unreadable_AlternateApplies()
    {
        var profile = IccO_Profile("prtr", IccProfile.CmykSpace, IccO_PcsXyz, [("wtpt", IccO_XyzTag(0.9642, 1, 0.8249)), ("B2A0", IccO_Lut16(3, 4, 2, v => [v[0], v[1], v[2], 0]))]);
        Assert.Null(IccProfile.Parse(profile));
        Assert.Same(ColorSpace.DeviceGray, IccO_Space(profile, 4, "DeviceGray"));
    }

    /// <summary>
    /// ICC.1, 9.2.15 sentences 4–5 and 7–12: the reader does not evaluate the chromatic adaptation matrix — a version-4 matrix profile whose
    /// chad matrix is singular (all zero) is read and gives the same colours as with the right matrix.
    /// </summary>
    [Fact]
    public void Icc9_2_15_SingularAdaptationMatrix_NotEvaluated()
    {
        var v4 = PdfARuleCases.Version4Rgb();
        var zeroed = (byte[])v4.Clone();
        var chad = IccO_Tags(zeroed).Single(t => t.Sig == "chad");
        zeroed.AsSpan(chad.Offset + 8, chad.Size - 8).Clear();
        Assert.NotNull(IccProfile.Parse(zeroed));
        IccO_SameColours(v4, zeroed, 3);
    }

    /// <summary>
    /// ICC.1, 9.2.21 Table 26 (scene colorimetry estimates): a profile with the image state 'scoe' and a white point above Y 1 gives the same
    /// colours as without — neither the state nor the media white point is evaluated.
    /// </summary>
    [Fact]
    public void Icc9_2_21_SceneStateWithWhiteAboveOne_ChangesNoColour()
    {
        var plain = IccO_Cmyk("prtr", ("A2B0", IccO_Plain));
        var with = IccO_With(plain, [("ciis", IccP_Tag("sig ", "scoe")), ("wtpt", IccO_XyzTag(1.4, 1.5, 1.2))], "wtpt");
        IccO_SameColours(plain, with, 4);
    }

    /// <summary>
    /// ICC.1, 9.2.22: the copyright of the own sRGB profile is a textType of version 2 ending with a zero byte; rewriting a version-4 profile as
    /// 2.1 turns its copyright (multiLocalizedUnicode „frei“) into such a textType.
    /// </summary>
    [Fact]
    public void Icc9_2_22_Copyright_TextTypeOfVersion2()
    {
        var own = IccO_TagData(CalibratedProfile.Srgb(), "cprt")!;
        Assert.Equal("text", Encoding.ASCII.GetString(own, 0, 4));
        Assert.Equal(0, own[^1]);
        Assert.Equal(IccO_Text("frei"), IccO_TagData(CalibratedProfile.AsVersion2(PdfARuleCases.Version4Rgb())!, "cprt"));
    }

    /// <summary>
    /// ICC.1, 9.2.24 (dmdd) and 9.2.35 (meta): „Bereinigen: Metadaten“ removes the device model description and the metadata dictionary — the
    /// tag and its text are gone, the profile stays readable with the same colours.
    /// </summary>
    [Theory]
    [InlineData("dmdd", "desc")]
    [InlineData("meta", "dict")]
    public void Icc9_2_24_MetadataTag_RemovedByCleaning(string sig, string type)
    {
        var profile = IccO_With(IccO_Matrix(), [(sig, IccP_Tag(type, "MODELLGEHEIM"))]);
        var cleaned = ObjectMetadata.Icc(profile);
        Assert.NotNull(cleaned);
        Assert.DoesNotContain(IccO_Tags(cleaned), t => t.Sig == sig);
        Assert.Equal(-1, cleaned.AsSpan().IndexOf("MODELLGEHEIM"u8));
        IccO_SameColours(profile, cleaned, 3);
    }

    /// <summary>
    /// ICC.1, 9.2.25–9.2.28: D2B0–D2B3 may replace the A2B tables, PDF-Knead keeps to them — a profile with A2B0, A2B1, A2B2 and meaningless
    /// D2B0–D2B3 gives perceptual A2B0, relative A2B1, saturation A2B2 and absolute colorimetric A2B1, as without the D2B tags.
    /// </summary>
    [Fact]
    public void Icc9_2_25_FloatingPointTables_A2BTablesApply()
    {
        var plain = IccO_Cmyk("prtr", ("A2B0", IccO_Plain), ("A2B1", IccO_Swapped), ("A2B2", IccO_Third));
        var with = IccO_With(plain, [.. Enumerable.Range(0, 4).Select(i => ($"D2B{i}", IccO_Junk("mpet", 96)))]);
        IccO_Gives(with, 0, 4, IccO_Plain);
        IccO_Gives(with, 1, 4, IccO_Swapped);
        IccO_Gives(with, 2, 4, IccO_Third);
        IccO_Gives(with, 3, 4, IccO_Swapped);
        IccO_SameColours(plain, with, 4);
    }

    /// <summary>
    /// ICC.1, 9.2.36 sentences 5 and 7: the media white point of the own display profiles is the PCS white D50 (within 1/65536) — also for
    /// CalGray and CalRGB with a D65 white point and for primaries under D65, whose colorants are adapted beforehand.
    /// </summary>
    [Fact]
    public void Icc9_2_36_OwnProfiles_WhitePointIsD50()
    {
        var d65 = TestPdf.Dict(("WhitePoint", TestPdf.Box(0.9505, 1, 1.089)), ("Gamma", new PdfReal(2.2)));
        var d65Rgb = TestPdf.Dict(("WhitePoint", TestPdf.Box(0.9505, 1, 1.089)), ("Gamma", TestPdf.Box(2.2, 2.2, 2.2)),
            ("Matrix", TestPdf.Box(0.4124, 0.2126, 0.0193, 0.3576, 0.7152, 0.1192, 0.1805, 0.0722, 0.9505)));
        foreach (var data in new[] { CalibratedProfile.Srgb(), CalibratedProfile.Gray(d65), CalibratedProfile.Rgb(d65Rgb),
                     CalibratedProfile.Primaries((0.64, 0.33), (0.30, 0.60), (0.15, 0.06), (0.3127, 0.3290), "D65") })
        {
            var wtpt = IccO_TagData(data, "wtpt")!;
            double At(int i) => BinaryPrimitives.ReadInt32BigEndian(wtpt.AsSpan(8 + 4 * i)) / 65536.0;
            Assert.InRange(At(0) - 0.9642, -1 / 65536.0, 1 / 65536.0);
            Assert.InRange(At(1) - 1.0, -1 / 65536.0, 1 / 65536.0);
            Assert.InRange(At(2) - 0.8249, -1 / 65536.0, 1 / 65536.0);
        }
    }
}
