using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M6 draft q-icc-10-109: ICC.1:2022 sections 10.1 to 10.9 (tag types: common layout, chromaticity, cicp, colorant order and table, curve, data,
/// date and time, dictionary). Uses the profile helpers <c>IccL_*</c> of the draft l-icc-1-62-an001.
/// </summary>
public sealed partial class M6NormTests
{
    /// <summary>
    /// ICC.1 10.1 sentences 1–2 and 10.2 sentence 2: a reader knows a tag's data by its type signature. A matrix profile whose rTRC carries the
    /// signature 'sf32' (the rest as a curv) is unreadable and the ICCBased space takes /Alternate; an A2B1 of an unknown type beside a valid A2B0
    /// counts as missing, A2B0 applies.
    /// </summary>
    [Fact]
    public void IccTagType_UnknownSignature_TagCountsAsMissing()
    {
        var tags = IccL_AdobeTags();
        var foreign = IccL_Gamma(2.2);
        Encoding.ASCII.GetBytes("sf32", foreign);
        tags[Array.FindIndex(tags, t => t.Item1 == "rTRC")] = ("rTRC", foreign);
        var data = IccL_Profile("RGB ", "XYZ ", tags);
        Assert.Null(IccProfile.Parse(data));
        Assert.Same(ColorSpace.DeviceGray, IccL_Space(data, 3, "DeviceGray"));
        var unknown = IccL_ConstantGrey(0.4);
        Encoding.ASCII.GetBytes("xxxx", unknown);
        var profile = IccProfile.Parse(IccL_Grey(("A2B0", IccL_ConstantGrey(0.2)), ("A2B1", unknown)))!;
        Assert.InRange(IccL_ToXyz(profile, 0.5f)[1], 0.1999f, 0.2001f);
    }

    /// <summary>
    /// ICC.1 10.1 sentence 3 and 10.6 sentence 11 (writer): bytes 4–7 of every tag type are reserved and zero — in every tag of every profile
    /// PDF-Knead builds itself (the own profiles and the baked CMYK test profile), curves included.
    /// </summary>
    [Fact]
    public void IccOwnProfiles_ReservedBytesOfEveryTagZero()
    {
        foreach (var (name, p) in IccL_OwnProfiles().Append(("Bake", IccL_NaiveCmyk.Value)))
            foreach (var (sig, offset, _) in IccL_Entries(p))
                Assert.True(IccL_U32(p, offset + 4) == 0, $"{name}: {sig}");
    }

    /// <summary>
    /// ICC.1 10.1 sentence 3 and 10.6 sentence 11 (reader): a curv with FFFFFFFFh in its reserved bytes 4–7 is read as the same curve (the reader
    /// passes over them).
    /// </summary>
    [Fact]
    public void IccCurv_ReservedBytesSet_SameCurve()
    {
        foreach (var curve in new[] { IccL_Curv(0, 20000, 65535), IccL_Gamma(1.8) })
        {
            var set = (byte[])curve.Clone();
            IccL_SetU32(set, 4, 0xFFFFFFFF);
            var plain = IccProfile.Parse(IccL_Grey(("kTRC", curve)))!;
            var odd = IccProfile.Parse(IccL_Grey(("kTRC", set)))!;
            foreach (var v in new[] { 0.1f, 0.5f, 0.9f }) Assert.Equal(IccL_ToXyz(plain, v), IccL_ToXyz(odd, v));
        }
    }

    /// <summary>The tags of types PDF-Knead neither reads nor writes, each with contradictory counts, sizes or offsets.</summary>
    private static (string Sig, byte[] Data)[] IccQ_ForeignTags()
    {
        byte[] chrm = [.. "chrm"u8, 0, 0, 0, 0, 0xFF, 0xFF, 0, 0];                       // 65535 channels, no values
        byte[] cicp = [.. "cicp"u8, 0, 0, 0, 0, 1, 13, 9, 1];                            // MatrixCoefficients 9 in an RGB profile
        byte[] clro = [.. "clro"u8, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0];              // 2^32 − 1 colorants, one byte
        byte[] clrt = [.. "clrt"u8, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF];                 // 2^32 − 1 colorants, length 12
        byte[] calt = [.. "dtim"u8, 0, 0, 0, 0, 0x07, 0xEA, 0, 13, 0, 1, 0, 0, 0, 0, 0, 0];   // month 13
        byte[] data = [.. "data"u8, 0, 0, 0, 0];                                          // size 8: no room for the flag (here 7)
        return [("chrm", chrm), ("cicp", cicp), ("clro", clro), ("clrt", clrt), ("calt", calt), ("zdat", data), ("meta", IccQ_BrokenDict())];
    }

    /// <summary>A dictType with 2^32 − 1 records of 16 bytes whose offsets point behind the end of the tag.</summary>
    private static byte[] IccQ_BrokenDict()
    {
        var d = new byte[32];
        Encoding.ASCII.GetBytes("dict", d);
        IccL_SetU32(d, 8, 0xFFFFFFFF);
        IccL_SetU32(d, 12, 16);
        IccL_SetU32(d, 16, 0x1000);
        IccL_SetU32(d, 20, 8);
        IccL_SetU32(d, 24, 0x2000);
        IccL_SetU32(d, 28, 8);
        return d;
    }

    /// <summary>
    /// ICC.1 10.2 sentences 13–22 and 23–27, 30–36 (chromaticityType), 10.3 sentences 1–14 (cicpType), 10.4 sentences 1–12 (colorantOrderType),
    /// 10.5 sentences 1–8, 11–16 and 17, 19–24, 26 (colorantTableType), 10.7 sentences 1–7 (dataType), 10.8 sentences 1–7 (dateTimeType),
    /// 10.9 (dictType, all rows): PDF-Knead neither reads nor writes these types. The version-4 RGB profile with chrm, cicp, clro, clrt, calt, a
    /// data tag and meta added, each with contradictory numbers, converts exactly as without them, and its version-2 copy holds none of them;
    /// a CMYK profile with clro and clrt likewise.
    /// </summary>
    [Fact]
    public void IccForeignTagTypes_ContradictoryCounts_Ignored()
    {
        var plain = PdfARuleCases.Version4Rgb();
        var extended = IccL_WithTags(plain, IccQ_ForeignTags());
        var a = IccProfile.Parse(plain)!;
        var b = IccProfile.Parse(extended);
        Assert.NotNull(b);
        foreach (var v in new[] { 0f, 0.25f, 0.6f, 1f }) Assert.Equal(IccL_ToXyz(a, v, 1 - v, v / 3), IccL_ToXyz(b!, v, 1 - v, v / 3));
        var v2 = CalibratedProfile.AsVersion2(extended);
        Assert.NotNull(v2);
        var written = IccL_Entries(v2!).Select(e => e.Sig).ToHashSet();
        foreach (var (sig, _) in IccQ_ForeignTags()) Assert.DoesNotContain(sig, written);
        var cmyk = IccProfile.Parse(IccL_NaiveCmyk.Value)!;
        var foreignCmyk = IccProfile.Parse(IccL_WithTags(IccL_NaiveCmyk.Value, IccQ_ForeignTags()[2..4]))!;
        Assert.Equal(IccL_ToXyz(cmyk, 0.1f, 0.5f, 0.2f, 0.3f), IccL_ToXyz(foreignCmyk, 0.1f, 0.5f, 0.2f, 0.3f));
    }

    /// <summary>ICC.1 10.6 sentences 1–10, 12–17: curveType holds n uInt16 values from byte 12; a curv claiming 1 000 values in 20 bytes is unreadable.</summary>
    [Fact]
    public void IccCurv_CountBeyondTag_Unreadable()
    {
        var curve = IccL_Curv(0, 30000, 65535, 65535);
        Assert.Equal(20, curve.Length);
        Assert.NotNull(IccProfile.Parse(IccL_Grey(("kTRC", curve))));
        IccL_SetU32(curve, 8, 1000);
        Assert.Null(IccProfile.Parse(IccL_Grey(("kTRC", curve))));
    }

    /// <summary>ICC.1 10.6 sentences 18–21: a curv with n = 0 is the identity: a grey profile gives Y = 0.25 for the input 0.25.</summary>
    [Fact]
    public void IccCurv_CountZero_Identity()
    {
        var profile = IccProfile.Parse(IccL_Grey(("kTRC", IccL_Curv())))!;
        var xyz = IccL_ToXyz(profile, 0.25f);
        Assert.InRange(xyz[1], 0.25f - 1e-6f, 0.25f + 1e-6f);
        Assert.InRange(xyz[0], 0.25f * 0.9642f - 1e-6f, 0.25f * 0.9642f + 1e-6f);
    }

    /// <summary>
    /// ICC.1 10.6 sentences 24–26: a curv table spans 0 to 1 in equal steps, values 0–65535 for 0–1, linear between the points: with the values 0,
    /// 65535, 0 a grey profile gives Y = 0.5 at 0.25 and 0.75 (not the nearest point) and 1 at 0.5.
    /// </summary>
    [Fact]
    public void IccCurv_Table_LinearBetweenPoints()
    {
        var profile = IccProfile.Parse(IccL_Grey(("kTRC", IccL_Curv(0, 65535, 0))))!;
        foreach (var (input, y) in new[] { (0.25f, 0.5f), (0.75f, 0.5f), (0.5f, 1f), (0f, 0f), (1f, 0f) })
            Assert.InRange(IccL_ToXyz(profile, input)[1], y - 1e-6f, y + 1e-6f);
    }

    /// <summary>
    /// ICC.1 10.9 sentences 1–7, 10–16 (and 17–33, 34–38, 41–50, 51–64): PDF-Knead does not evaluate dictType; cleaning the metadata removes the
    /// meta tag as a whole, unread: a profile whose meta claims 2^32 − 1 records pointing behind its end comes back without meta, the other tags
    /// byte for byte, and both versions convert with the same colours.
    /// </summary>
    [Fact]
    public void IccMetadataDict_RemovedWhole_OtherTagsByteEqual()
    {
        byte[] desc = [.. "text"u8, 0, 0, 0, 0, .. "Beispielprofil"u8, 0];
        var data = IccL_AdobeRgb(("desc", desc), ("meta", IccQ_BrokenDict()));
        var cleaned = ObjectMetadata.Icc(data);
        Assert.NotNull(cleaned);
        Assert.Null(IccL_Tag(cleaned!, "meta"));
        var kept = IccL_Entries(data).Where(e => e.Sig != "meta").Select(e => e.Sig).ToList();
        Assert.Equal(kept, IccL_Entries(cleaned!).Select(e => e.Sig).ToList());
        foreach (var sig in kept) Assert.Equal(IccL_Tag(data, sig), IccL_Tag(cleaned!, sig));
        var before = IccProfile.Parse(data)!;
        var after = IccProfile.Parse(cleaned!)!;
        foreach (var v in new[] { 0.1f, 0.5f, 0.9f }) Assert.Equal(IccL_ToXyz(before, v, v / 2, 1 - v), IccL_ToXyz(after, v, v / 2, 1 - v));
    }
}
