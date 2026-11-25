using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Objects;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M6 draft u-icc-1017-1031-F: the tag types of ICC.1, 10.17 to 10.31 and the models of annex F as PDF-Knead reads, passes over or writes them —
/// profiles built here (helpers RIcc_ of the draft r-icc-1010-1011 and TIcc_ of t-icc-1014-1016, own ones with the prefix UIcc_).
/// </summary>
public sealed partial class M6NormTests
{
    /// <summary>A fixed-length ASCII field padded with zeros.</summary>
    private static byte[] UIcc_Ascii(string text, int length)
    {
        var d = new byte[length];
        Encoding.ASCII.GetBytes(text, d);
        return d;
    }

    /// <summary>
    /// namedColor2Type: <paramref name="count"/> as given, <paramref name="colours"/> entries written (32-byte name, three PCS values, three
    /// device values each).
    /// </summary>
    private static byte[] UIcc_Ncl2(uint count, int colours)
    {
        var d = new List<byte>([.. "ncl2"u8, 0, 0, 0, 0, 0, 0, 0, 0]);
        d.AddRange(TIcc_U32(count));
        d.AddRange(TIcc_U32(3));
        d.AddRange(UIcc_Ascii("Muster ", 32));
        d.AddRange(UIcc_Ascii(" Farbe", 32));
        for (var c = 0; c < colours; c++)
        {
            d.AddRange(UIcc_Ascii($"Ton{c}", 32));
            d.AddRange([0x80, 0x00, 0x80, 0x00, 0x80, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00]);
        }
        return [.. d];
    }

    /// <summary>profileSequenceDescType with one structure whose device descriptions (dmnd, dmdd) are whole tags; the count as given.</summary>
    private static byte[] UIcc_Pseq(uint count, byte[] dmnd, byte[] dmdd) =>
        [.. "pseq"u8, 0, 0, 0, 0, .. TIcc_U32(count), .. "XMPL"u8, .. "M001"u8, 0, 0, 0, 0, 0, 0, 0, 0, .. "fscn"u8, .. dmnd, .. dmdd];

    /// <summary>
    /// profileSequenceIdentifierType with two structures (profile ID and description): positions beyond the tag, or both naming one structure at
    /// an offset not divisible by four.
    /// </summary>
    private static byte[] UIcc_Psid(bool beyond)
    {
        byte[] structure = [.. Enumerable.Repeat((byte)0x5A, 16), .. TIcc_Mluc(("deDE", "Beispiel"))];
        var d = new List<byte>([.. "psid"u8, 0, 0, 0, 0, .. TIcc_U32(2)]);
        var at = beyond ? 0x10000u : 12 + 16 + 1u;
        for (var i = 0; i < 2; i++)
        {
            d.AddRange(TIcc_U32(at));
            d.AddRange(TIcc_U32((uint)structure.Length));
        }
        d.Add(0);
        d.AddRange(structure);
        return [.. d];
    }

    /// <summary>The (signature, data) of an extra tag of <see cref="UnevaluatedTag_ProfileReadSameColours"/>.</summary>
    private static (string, byte[]) UIcc_ExtraTag(string variant)
    {
        var empty = TIcc_MlucRaw(0, 12, [], []);
        return variant switch
        {
            "ncl2" => ("ncl2", UIcc_Ncl2(2, 2)),
            "ncl2Short" => ("ncl2", UIcc_Ncl2(2, 2)[..20]),
            "ncl2CountHuge" => ("ncl2", UIcc_Ncl2(0xFFFFFFFF, 1)),
            "ncl2CountBeyond" => ("ncl2", UIcc_Ncl2(1000, 1)),
            "pseq" => ("pseq", UIcc_Pseq(1, TIcc_Mluc(("deDE", "Gerät")), empty)),
            "pseqCountHuge" => ("pseq", UIcc_Pseq(0xFFFFFFFF, TIcc_Mluc(("deDE", "Gerät")), empty)),
            "psidBeyond" => ("psid", UIcc_Psid(beyond: true)),
            "psidSharedUnaligned" => ("psid", UIcc_Psid(beyond: false)),
            "rcs2Beyond" => ("rcs2", [.. "rcs2"u8, 0, 0, 0, 0, 0, 4, 0, 2, .. TIcc_U32(0xFFFF0000), .. TIcc_U32(0xFFFF0100), .. "StaA"u8]),
            "techSig" => ("tech", [.. "sig "u8, 0, 0, 0, 0, .. "fscn"u8]),
            "techSig8" => ("tech", [.. "sig "u8, 0, 0, 0, 0]),
            "uf32" => ("zz01", [.. "uf32"u8, 0, 0, 0, 0, 0, 1, 0, 0, 0, 2, 0x80, 0]),
            "ui16Odd" => ("zz02", [.. "ui16"u8, 0, 0, 0, 0, 0, 1, 0, 2, 7]),
            "ui32" => ("zz03", [.. "ui32"u8, 0, 0, 0, 0, 0, 0, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF]),
            "ui64" => ("zz04", [.. "ui64"u8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]),
            "ui08" => ("zz05", [.. "ui08"u8, 0, 0, 0, 0, 1, 2, 3]),
            "viewShort" => ("view", [.. "view"u8, 0, 0, 0, 0, 0, 0, 0, 1]),
            _ => throw new ArgumentException(variant),
        };
    }

    /// <summary>
    /// ICC.1, 10.17 sentences 9–11 and 12–25; 10.19 sentences 1–3; 10.20.1 sentences 1–10 and 11–20 (also for 10.20.3); 10.21 sentences 1–8,
    /// 9–18, 19–22, 23–38, 39–56, 57–75, 76–79, 80–97; 10.23 sentences 1–8; 10.25 to 10.29 sentences 1–7 (10.27: 1–9); 10.30 sentences 1–9:
    /// tags that PDF-Knead neither evaluates nor writes (named colours, profile sequences and their identifiers, the response curves, a
    /// signature, private arrays of fixed and whole numbers, the viewing conditions) — a matrix profile with such a tag, also too short, with
    /// counts beyond its data or positions beyond the tag, is read with exactly the colours of the same profile without it.
    /// </summary>
    [Theory]
    [InlineData("ncl2")]
    [InlineData("ncl2Short")]
    [InlineData("ncl2CountHuge")]
    [InlineData("ncl2CountBeyond")]
    [InlineData("pseq")]
    [InlineData("pseqCountHuge")]
    [InlineData("psidBeyond")]
    [InlineData("psidSharedUnaligned")]
    [InlineData("rcs2Beyond")]
    [InlineData("techSig")]
    [InlineData("techSig8")]
    [InlineData("uf32")]
    [InlineData("ui16Odd")]
    [InlineData("ui32")]
    [InlineData("ui64")]
    [InlineData("ui08")]
    [InlineData("viewShort")]
    public void UnevaluatedTag_ProfileReadSameColours(string variant) =>
        RIcc_AssertSameColours(RIcc_MatrixProfile(), RIcc_MatrixProfile(UIcc_ExtraTag(variant)));

    /// <summary>
    /// ICC.1, 10.17 sentences 1–8: PDF-Knead reads no named-colour profiles — a profile of the class nmcl (RGB, only an ncl2 tag) as an ICCBased
    /// space with /Alternate /DeviceGray is unreadable, the space is DeviceGray; the PDF/A check does not accept its header (class nmcl).
    /// </summary>
    [Fact]
    public void NamedColourProfile_Unreadable_AlternateApplies_PdfARejects()
    {
        var data = RIcc_Profile("nmcl", IccProfile.RgbSpace, RIcc_LabSpace, [("ncl2", UIcc_Ncl2(2, 2))]);
        Assert.Null(IccProfile.Parse(data));
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        Assert.Same(ColorSpace.DeviceGray, RIcc_IccBased(doc, data, 3, "DeviceGray"));
        Assert.False(PdfACheck.IccProfileHeaderValid(data, 4));
        // the same header with class mntr passes
        Encoding.ASCII.GetBytes("mntr", data.AsSpan(12, 4));
        Assert.True(PdfACheck.IccProfileHeaderValid(data, 4));
    }

    /// <summary>
    /// ICC.1, 10.17 sentences 37–38 (the note on the parameters of parametric curves): parameters that give undefined values (a negative base,
    /// a negative exponent at 0) end in finite values within 0–1 — type 1 with g 0.5, a −1, b 0.5 and type 0 with g −1 (at 0: 1), never NaN; a
    /// matrix profile with such curves draws without an exception.
    /// </summary>
    [Fact]
    public void ParametricCurve_UndefinedParameters_FiniteWithinRange()
    {
        var negativeBase = IccCurve.Parametric(1, [0.5f, -1, 0.5f, 0, 0, 0, 0]);
        var negativeExponent = IccCurve.Parametric(0, [-1, 0, 0, 0, 0, 0, 0]);
        foreach (var x in new[] { 0f, 0.5f, 1f })
            foreach (var y in new[] { negativeBase.Evaluate(x), negativeExponent.Evaluate(x) })
            {
                Assert.False(float.IsNaN(y));
                Assert.InRange(y, 0, 1);
            }
        Assert.Equal(1f, negativeExponent.Evaluate(0));
        foreach (var trc in new[] { RIcc_Para(1, 0.5, -1, 0.5), RIcc_Para(0, -1) })
        {
            var data = RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, RIcc_MatrixTags(trc));
            Assert.NotNull(IccProfile.Parse(data));
            using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
            var space = RIcc_IccBased(doc, data, 3);
            foreach (var v in new[] { 0.0, 0.5, 1.0 }) space.ToRgb([v, v, v]);
        }
    }

    /// <summary>
    /// ICC.1, 10.18 sentences 9–18, 40: function type 0 is Y = X^g, its parameters in the order of the table — g 2.2 gives 0.5^2.2 (0.2176) at
    /// 0.5, and a matrix profile with this curve draws grey 0.5 as with curv gamma 2.2.
    /// </summary>
    [Fact]
    public void ParametricType0_PowerOfX()
    {
        Assert.InRange(IccCurve.Parametric(0, [2.2f, 0, 0, 0, 0, 0, 0]).Evaluate(0.5f), 0.2176f - 1e-4f, 0.2176f + 1e-4f);
        var para = IccProfile.Parse(RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, RIcc_MatrixTags(RIcc_Para(0, 2.2))))!;
        var curv = IccProfile.Parse(RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, RIcc_MatrixTags(RIcc_Gamma(2.2))))!;
        var a = RIcc_ToXyz(para, 0.5f, 0.5f, 0.5f);
        var b = RIcc_ToXyz(curv, 0.5f, 0.5f, 0.5f);
        for (var i = 0; i < 3; i++) Assert.InRange(a[i] - b[i], -5e-4f, 5e-4f);
    }

    /// <summary>ICC.1, 10.18 sentences 19–24: function type 1 is (aX + b)^g from X = −b/a on, 0 below — g 2, a 2, b −1: 0 at 0.25, 0.25 at 0.75, 1 at 1.</summary>
    [Theory]
    [InlineData(0.25f, 0f)]
    [InlineData(0.75f, 0.25f)]
    [InlineData(1f, 1f)]
    public void ParametricType1_ZeroBelowMinusBOverA(float x, float expected) =>
        Assert.InRange(IccCurve.Parametric(1, [2, 2, -1, 0, 0, 0, 0]).Evaluate(x), expected - 1e-5f, expected + 1e-5f);

    /// <summary>ICC.1, 10.18 sentences 25–30: function type 2 is (aX + b)^g + c from X = −b/a on, c below — g 1, a 1, b −0.5, c 0.1: 0.1 at 0.25, 0.35 at 0.75.</summary>
    [Theory]
    [InlineData(0.25f, 0.1f)]
    [InlineData(0.75f, 0.35f)]
    public void ParametricType2_ConstantBelowMinusBOverA(float x, float expected) =>
        Assert.InRange(IccCurve.Parametric(2, [1, 1, -0.5f, 0.1f, 0, 0, 0]).Evaluate(x), expected - 1e-5f, expected + 1e-5f);

    /// <summary>ICC.1, 10.18 sentences 35–39: function type 4 is (aX + b)^g + e from X = d on, cX + f below — g 1, a 0.5, b 0, c 0.2, d 0.5, e 0.1, f 0.05: 0.1 at 0.25, 0.475 at 0.75.</summary>
    [Theory]
    [InlineData(0.25f, 0.1f)]
    [InlineData(0.75f, 0.475f)]
    public void ParametricType4_LinearBelowD(float x, float expected) =>
        Assert.InRange(IccCurve.Parametric(4, [1, 0.5f, 0, 0.2f, 0.5f, 0.1f, 0.05f]).Evaluate(x), expected - 1e-5f, expected + 1e-5f);

    /// <summary>
    /// ICC.1, 10.18 sentences 41–42: domain and range of every function are 0 to 1, values outside are clipped — type 4 with g 1, a 1, b 0, d 0,
    /// e 0.5 gives 1 at 1 (not 1.5); type 2 with c −0.2 gives 0 below −b/a (not −0.2); an input of 1.5 counts as 1.
    /// </summary>
    [Fact]
    public void ParametricCurve_DomainAndRangeClipped()
    {
        Assert.Equal(1f, IccCurve.Parametric(4, [1, 1, 0, 0, 0, 0.5f, 0]).Evaluate(1));
        var type2 = IccCurve.Parametric(2, [1, 1, -0.5f, -0.2f, 0, 0, 0]);
        Assert.Equal(0f, type2.Evaluate(0.25f));
        Assert.Equal(type2.Evaluate(1), type2.Evaluate(1.5f));
    }

    /// <summary>
    /// ICC.1, 10.19 sentences 4–13 (docs/FEHLERGEDAECHTNIS.md, No. 1133): cleaning removes the profile sequence with the device descriptions
    /// copied into it, together with the device description (dmnd) of the profile itself — the description „HERSTELLERGEHEIM“ in the structure
    /// of the sequence is gone; before, the sequence stayed.
    /// </summary>
    [Fact]
    public void Pseq_DeviceDescriptions_RemovedByMetadataCleaning()
    {
        var pseq = UIcc_Pseq(1, TIcc_Mluc(("deDE", "HERSTELLERGEHEIM")), TIcc_MlucRaw(0, 12, [], []));
        var profile = RIcc_MatrixProfile(("dmnd", TIcc_Mluc(("deDE", "Geraet"))), ("pseq", pseq));
        var cleaned = ObjectMetadata.Icc(profile)!;
        Assert.NotNull(cleaned);
        Assert.Null(TIcc_Tag(cleaned, "dmnd"));
        Assert.Null(TIcc_Tag(cleaned, "pseq"));
        Assert.Equal(-1, cleaned.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes("HERSTELLERGEHEIM")));
    }

    /// <summary>ICC.1, 10.19 sentences 14–17: PDF-Knead writes no profile sequence — its own profiles carry no pseq tag.</summary>
    [Fact]
    public void OwnProfiles_NoProfileSequence()
    {
        foreach (var profile in new[] { CalibratedProfile.Srgb(), CalibratedProfile.Gray(null), CalibratedProfile.Rgb(null) })
            Assert.Null(TIcc_Tag(profile, "pseq"));
    }

    /// <summary>
    /// ICC.1, 10.22 sentences 1–7: rewriting as 2.1 takes the chromatic adaptation (chad) only as s15Fixed16ArrayType and then byte for byte —
    /// the example profile keeps its chad unchanged; a chad of the type XYZ is left out.
    /// </summary>
    [Fact]
    public void AsVersion2_Chad_Sf32CopiedXyzLeftOut()
    {
        var v4 = PdfARuleCases.Version4Rgb();
        Assert.Equal(TIcc_Tag(v4, "chad"), TIcc_Tag(CalibratedProfile.AsVersion2(v4)!, "chad"));
        var xyzChad = RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, [.. RIcc_MatrixTags(), ("chad", RIcc_XyzTag(1, 1, 1))], major: 4);
        var v2 = CalibratedProfile.AsVersion2(xyzChad);
        Assert.NotNull(v2);
        Assert.Null(TIcc_Tag(v2!, "chad"));
    }

    /// <summary>
    /// ICC.1, 10.24 sentences 1–3: textType is 7-bit ASCII ending with 00h, its length the element size minus 8 — the copyright of the own sRGB
    /// profile ends with 00h, every byte before is below 80h, its entry gives 8 + text + 1; the 2.1 rewriting of the example profile (desc
    /// „Beispiel-RGB v4 – Grün“) carries only 7-bit characters in desc and cprt.
    /// </summary>
    [Fact]
    public void OwnTextTags_SevenBitWithFinalZero()
    {
        var cprt = TIcc_Tag(CalibratedProfile.Srgb(), "cprt")!;
        Assert.Equal(0, cprt[^1]);
        Assert.All(cprt.AsSpan(8).ToArray(), b => Assert.True(b < 0x80));
        Assert.Equal(cprt.Length, Array.IndexOf(cprt, (byte)0, 8) + 1);
        var v2 = CalibratedProfile.AsVersion2(PdfARuleCases.Version4Rgb())!;
        foreach (var tag in new[] { TIcc_Tag(v2, "desc")!, TIcc_Tag(v2, "cprt")! })
            Assert.All(tag, b => Assert.True(b < 0x80));
    }

    /// <summary>ICC.1, 10.24 sentences 4–9: textType begins with the signature „text“ and four zero bytes — so the copyright of the own sRGB profile.</summary>
    [Fact]
    public void OwnTextTag_SignatureAndReservedZero()
    {
        var cprt = TIcc_Tag(CalibratedProfile.Srgb(), "cprt")!;
        Assert.True(cprt.AsSpan().StartsWith("text\0\0\0\0"u8));
    }

    /// <summary>
    /// ICC.1, 10.30 sentences 10–12: the luminance tag must equal the Y of the viewing conditions — PDF-Knead writes neither: its own profiles
    /// carry no lumi and no view tag.
    /// </summary>
    [Fact]
    public void OwnProfiles_NoLuminanceNorViewingConditions()
    {
        foreach (var profile in new[] { CalibratedProfile.Srgb(), CalibratedProfile.Gray(null), CalibratedProfile.Rgb(null), CalibratedProfile.RommRgb() })
        {
            Assert.Null(TIcc_Tag(profile, "lumi"));
            Assert.Null(TIcc_Tag(profile, "view"));
        }
    }

    /// <summary>
    /// ICC.1, 10.31 sentences 1–4: the colorants are XYZType of at least one set of three values — an rXYZ of 16 bytes or of the type sf32 makes
    /// the matrix profile unreadable; an ICCBased space with /Alternate /DeviceGray then gives DeviceGray.
    /// </summary>
    [Theory]
    [InlineData("short16")]
    [InlineData("sf32")]
    public void ColorantTag_ShortOrNotXyz_Unreadable_AlternateApplies(string variant)
    {
        var tags = RIcc_MatrixTags();
        var red = RIcc_XyzTag(0.6097559, 0.3111242, 0.0194811);
        if (variant == "sf32") "sf32"u8.CopyTo(red);
        tags[0] = ("rXYZ", variant == "short16" ? red[..16] : red);
        var data = RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, tags);
        Assert.Null(IccProfile.Parse(data));
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        Assert.Same(ColorSpace.DeviceGray, RIcc_IccBased(doc, data, 3, "DeviceGray"));
    }

    /// <summary>
    /// ICC.1, 10.31 sentences 5–10: XYZType is the signature, four zero bytes and s15Fixed16 numbers, negative ones in two's complement — the
    /// profile of a CalRGB (white D50) with the colorant Z −0.1 writes it so and reads it back as −0.1 (to 2e-5).
    /// </summary>
    [Fact]
    public void CalRgb_NegativeColorant_TwosComplement()
    {
        var dict = TestPdf.Dict(("WhitePoint", TestPdf.Box(0.9642, 1, 0.8249)), ("Matrix", TestPdf.Box(0.5, 0.25, -0.1, 0.3, 0.6, 0.1, 0.15, 0.15, 0.8)));
        var red = TIcc_Tag(CalibratedProfile.Rgb(dict), "rXYZ")!;
        Assert.True(red.AsSpan().StartsWith("XYZ \0\0\0\0"u8));
        Assert.True((red[16] & 0x80) != 0);
        Assert.InRange(BinaryPrimitives.ReadInt32BigEndian(red.AsSpan(16)) / 65536.0, -0.1 - 2e-5, -0.1 + 2e-5);
    }

    /// <summary>
    /// ICC.1, F.3 sentences 14–23: the inverse of the matrix model clips the linear value below 0 to 0 and above 1 to 1 — XYZ 1.2 × D50 gives
    /// sRGB (1, 1, 1); NaN gives 0.
    /// </summary>
    [Fact]
    public void XyzToSrgb_AboveWhiteIsWhite_NaNIsBlack()
    {
        Span<float> rgb = stackalloc float[3];
        IccColor.XyzToSrgb(1.2f * IccProfile.WhiteX, 1.2f, 1.2f * IccProfile.WhiteZ, rgb);
        Assert.Equal(new[] { 1f, 1f, 1f }, rgb.ToArray());
        IccColor.XyzToSrgb(float.NaN, float.NaN, float.NaN, rgb);
        Assert.Equal(new[] { 0f, 0f, 0f }, rgb.ToArray());
        Assert.Equal(0f, IccColor.Encode(float.NaN));
    }

    /// <summary>
    /// ICC.1, F.3 sentences 24–39: green and blue are clipped on their own as red is — an XYZ of the linear sRGB (0.5, 1.3, −0.2) gives G 1 and
    /// B 0, red stays the encoding of 0.5.
    /// </summary>
    [Fact]
    public void XyzToSrgb_EachChannelClippedOnItsOwn()
    {
        // linear sRGB → XYZ (D50), Bradford-adapted
        double[] m = [0.4360747, 0.3850649, 0.1430804, 0.2225045, 0.7168786, 0.0606169, 0.0139322, 0.0971045, 0.7141733];
        double[] lin = [0.5, 1.3, -0.2];
        var xyz = Enumerable.Range(0, 3).Select(r => (float)(m[3 * r] * lin[0] + m[3 * r + 1] * lin[1] + m[3 * r + 2] * lin[2])).ToArray();
        Span<float> rgb = stackalloc float[3];
        IccColor.XyzToSrgb(xyz[0], xyz[1], xyz[2], rgb);
        Assert.InRange(rgb[0], IccColor.Encode(0.5f) - 1e-3f, IccColor.Encode(0.5f) + 1e-3f);
        Assert.Equal(1f, rgb[1]);
        Assert.Equal(0f, rgb[2]);
    }

    /// <summary>
    /// ICC.1, F.3 sentences 43–45 (read leniently, docs/normabgleich/sonderregeln.md S-A A190): the matrix model needs PCS XYZ — an RGB matrix
    /// profile whose header names PCS Lab is read as with PCS XYZ, the same colours: the matrix gives XYZ, as the three reference readers take
    /// it (T26 of tools/normabgleich/proben/lesetoleranzen_m6.py).
    /// </summary>
    [Fact]
    public void MatrixProfileWithPcsLab_ReadAsXyzAsTheReferenceReaders() =>
        RIcc_AssertSameColours(RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, RIcc_MatrixTags()),
            RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_LabSpace, RIcc_MatrixTags()));
}
