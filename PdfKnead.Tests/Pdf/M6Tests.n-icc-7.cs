using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M6 draft n-icc-7: ICC.1:2022 section 7 (profile structure: header, tag table, tag data). Uses the profile helpers <c>IccL_*</c> of the draft
/// l-icc-1-62-an001.
/// </summary>
public sealed partial class M6NormTests
{
    /// <summary>The profiles PDF-Knead writes or rewrites: its own, the version-2 copy of a version-4 profile and a profile cleaned of metadata.</summary>
    private static IEnumerable<(string Name, byte[] Data)> IccN_WrittenProfiles()
    {
        foreach (var own in IccL_OwnProfiles()) yield return own;
        yield return ("AsVersion2", CalibratedProfile.AsVersion2(PdfARuleCases.Version4Rgb())!);
        // a description of 13 bytes (not a multiple of four) and the device description that cleaning removes
        byte[] odd = [.. "text"u8, 0, 0, 0, 0, .. "abcd"u8, 0];
        byte[] maker = [.. "text"u8, 0, 0, 0, 0, .. "Erfundene Geraete AG"u8, 0];
        yield return ("Cleaned", ObjectMetadata.Icc(IccL_AdobeRgb(("desc", odd), ("dmnd", maker)))!);
    }

    /// <summary>
    /// ICC.1 7.1.1 sentence 1, 7.1.2 sentences 7–8, 7.2.2 sentences 3–4 (writer), 7.3.1 sentences 7–11 (writer), 7.3.4 sentences 1–5 (writer),
    /// 7.3.5 sentences 1–2: every profile PDF-Knead writes is header, tag table and tag data in this order: the size field is the length (a multiple
    /// of four), the first tag follows the table directly at 132 + 12 · n, every tag starts at a multiple of four, the tags follow without gaps,
    /// padded with at most three zero bytes (also after the last), signatures are unique, tags share data only with the same offset and size,
    /// and each size counts the data without padding (14 for a gamma curve, 12 + 2n for a table, 20 for XYZ).
    /// </summary>
    [Fact]
    public void IccWrittenProfiles_HeaderTableAndAlignedTagsWithoutGaps()
    {
        var cleaned = false;
        foreach (var (name, p) in IccN_WrittenProfiles())
        {
            Assert.Equal((uint)p.Length, IccL_U32(p, 0));
            Assert.Equal(0, p.Length % 4);
            var entries = IccL_Entries(p);
            Assert.True(entries.Count > 0, name);
            var first = 132 + 12 * entries.Count;
            Assert.Equal(first, entries.Min(e => e.Offset));
            Assert.Equal(entries.Count, entries.Select(e => e.Sig).Distinct().Count());
            var at = first;
            foreach (var (offset, size) in entries.Select(e => (e.Offset, e.Size)).Distinct().OrderBy(e => e.Offset))
            {
                Assert.True(offset == at, $"{name}: tag at {offset}, expected {at}");
                Assert.Equal(0, offset % 4);
                var end = (offset + size + 3) & ~3;
                Assert.True(end - offset - size <= 3);
                Assert.All(p.AsSpan(offset + size, end - offset - size).ToArray(), b => Assert.Equal(0, b));
                at = end;
            }
            Assert.Equal(p.Length, at);
            foreach (var (sig, offset, size) in entries)
            {
                switch (Encoding.ASCII.GetString(p, offset, 4))
                {
                    case "curv":
                        Assert.Equal(12 + 2 * (int)IccL_U32(p, offset + 8), size);
                        break;
                    case "XYZ ":
                        Assert.Equal(20, size);
                        break;
                }
                if (sig == "desc" && name == "Cleaned")
                {
                    Assert.Equal(13, size);
                    cleaned = true;
                }
            }
        }
        Assert.True(cleaned);
        Assert.Equal(14, IccL_Tag(CalibratedProfile.Gray(null), "kTRC")!.Length);
    }

    /// <summary>
    /// ICC.1 7.2.1 sentence 45T and 7.2.19 sentence 1 (bytes 100–127 reserved), 7.2.3 sentences 1–4 (preferred CMM, 4–7), 7.2.10 sentences 1–3
    /// (platform, 40–43), 7.2.11 sentences 1–8 (flags, 44–47: bit 1 "only with the embedded data" is 0; bit 0 is 0 today, design decision pending),
    /// 7.2.12 and 7.2.13 sentences 1–3 (maker and model, 48–55), 7.2.14 sentences 1–4, 5T–20T (device attributes, 56–63), 7.2.17 sentences 1–3
    /// (creator, 80–83): fields PDF-Knead does not fill are zero in all its own profiles.
    /// </summary>
    [Theory]
    [InlineData(4, 8)]
    [InlineData(40, 44)]
    [InlineData(44, 48)]
    [InlineData(48, 52)]
    [InlineData(52, 56)]
    [InlineData(56, 64)]
    [InlineData(80, 84)]
    [InlineData(100, 128)]
    public void IccOwnProfiles_UnusedHeaderField_IsZero(int from, int to)
    {
        foreach (var (name, p) in IccL_OwnProfiles())
            Assert.True(p.AsSpan(from, to - from).IndexOfAnyExcept((byte)0) < 0, $"{name}: bytes {from}–{to - 1}");
    }

    /// <summary>
    /// ICC.1 7.2.2 sentences 3–4 (reader, S-A A25): a size field of 0 is read as the length of the data; a smaller size (from 132 on) cuts off
    /// the tags behind it — here A2B1, so A2B0 applies; a larger size is limited to the data.
    /// </summary>
    [Fact]
    public void IccProfileSizeField_ZeroSmallerOrLarger()
    {
        var full = IccL_Grey(("A2B0", IccL_ConstantGrey(0.2)), ("A2B1", IccL_ConstantGrey(0.4)));
        float Y(byte[] p) => IccL_ToXyz(IccProfile.Parse(p)!, 0.5f)[1];
        Assert.InRange(Y(full), 0.3999f, 0.4001f);
        var zero = (byte[])full.Clone();
        IccL_SetU32(zero, 0, 0);
        Assert.InRange(Y(zero), 0.3999f, 0.4001f);
        var cut = (byte[])full.Clone();
        IccL_SetU32(cut, 0, (uint)IccL_Entries(full).Single(e => e.Sig == "A2B1").Offset);
        Assert.InRange(Y(cut), 0.1999f, 0.2001f);
        var larger = (byte[])full.Clone();
        IccL_SetU32(larger, 0, (uint)full.Length + 1000);
        Assert.InRange(Y(larger), 0.3999f, 0.4001f);
    }

    /// <summary>
    /// ICC.1 7.2.4 sentences 8–13 (records today's behaviour; design decision on read tolerances pending): a major version above 4 is not refused — a
    /// profile with byte 8 = 5 is read with the same colours as the same profile of version 4.
    /// </summary>
    [Fact]
    public void IccMajorVersion5_ReadLikeVersion4_TodaysBehaviour()
    {
        var v4 = IccL_Profile("RGB ", "XYZ ", IccL_AdobeTags(), version: 4);
        var v5 = (byte[])v4.Clone();
        v5[8] = 5;
        var five = IccProfile.Parse(v5);
        Assert.NotNull(five);
        Assert.Equal(IccL_ToXyz(IccProfile.Parse(v4)!, 0.2f, 0.5f, 0.8f), IccL_ToXyz(five!, 0.2f, 0.5f, 0.8f));
    }

    /// <summary>
    /// ICC.1 7.2.5 sentences 1–5, 6T–9T (reader; records today's behaviour; design decision on read tolerances pending): the reader does not look at the
    /// profile class — a profile of the class DeviceLink ('link') or Abstract ('abst') with PCS XYZ and matrix tags is used by an ICCBased space
    /// like a device profile, its /Alternate does not apply.
    /// </summary>
    [Theory]
    [InlineData("link")]
    [InlineData("abst")]
    public void IccDeviceLinkAndAbstractClass_UsedAsDeviceProfile_TodaysBehaviour(string deviceClass)
    {
        var data = IccL_Profile("RGB ", "XYZ ", IccL_AdobeTags(), deviceClass: deviceClass);
        Assert.NotNull(IccProfile.Parse(data));
        var space = IccL_Space(data, 3, "DeviceGray");
        Assert.NotSame(ColorSpace.DeviceGray, space);
        Assert.Equal(3, space.Components);
    }

    /// <summary>ICC.1 7.2.6 sentences 1–3: a data colour space signature not in Table 19 ('ABCD') makes the profile unreadable; /Alternate applies.</summary>
    [Fact]
    public void IccUnknownDataColourSpace_Unreadable_AlternateApplies()
    {
        var data = IccL_Profile("ABCD", "XYZ ", IccL_AdobeTags());
        Assert.Null(IccProfile.Parse(data));
        Assert.Same(ColorSpace.DeviceGray, IccL_Space(data, 3, "DeviceGray"));
    }

    /// <summary>
    /// ICC.1 7.2.6 sentences 6T–25T and 26T–45T (Table 19): each data colour space signature gives its number of channels — XYZ, Lab, Luv, YCbr,
    /// Yxy, RGB, HSV, HLS, CMY three, GRAY one, CMYK four, 2CLR to 8CLR two to eight; a profile with 9CLR is unreadable because PDF-Knead
    /// evaluates tables of at most eight inputs.
    /// </summary>
    [Theory]
    [InlineData("XYZ ", 3)]
    [InlineData("Lab ", 3)]
    [InlineData("Luv ", 3)]
    [InlineData("YCbr", 3)]
    [InlineData("Yxy ", 3)]
    [InlineData("RGB ", 3)]
    [InlineData("GRAY", 1)]
    [InlineData("HSV ", 3)]
    [InlineData("HLS ", 3)]
    [InlineData("CMYK", 4)]
    [InlineData("CMY ", 3)]
    [InlineData("2CLR", 2)]
    [InlineData("5CLR", 5)]
    [InlineData("8CLR", 8)]
    [InlineData("9CLR", 9)]
    public void IccDataColourSpace_ChannelCountFromTable19(string space, int channels)
    {
        var profile = IccProfile.Parse(IccL_Profile(space, "XYZ ", [("A2B0", IccL_Lut16(channels, IccL_D50Words(0.5)))]));
        if (channels > 8)
        {
            Assert.Null(profile);
            return;
        }
        Assert.NotNull(profile);
        Assert.Equal(channels, profile!.Channels);
    }

    /// <summary>
    /// ICC.1 7.2.6 sentences 46T–60T: ACLR and FCLR stand for 10 and 15 colours; PDF-Knead counts them so, but a profile with such a table is
    /// unreadable (its own limit of eight table inputs), so /Alternate applies.
    /// </summary>
    [Fact]
    public void IccDataColourSpace_TenAndFifteenColours_CountedButUnreadable()
    {
        var channelsOf = typeof(IccProfile).GetMethod("ChannelsOf", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(10, channelsOf.Invoke(null, [IccL_Sig("ACLR")]));
        Assert.Equal(15, channelsOf.Invoke(null, [IccL_Sig("FCLR")]));
        var data = IccL_Profile("ACLR", "XYZ ", [("A2B0", IccL_Lut16(10, IccL_D50Words(0.5)))]);
        Assert.Null(IccProfile.Parse(data));
    }

    /// <summary>ICC.1 7.2.7 sentences 1–3: the PCS field of a device profile is XYZ or Lab; 'RGB ' there makes the profile unreadable.</summary>
    [Fact]
    public void IccPcsField_NeitherXyzNorLab_Unreadable()
    {
        Assert.NotNull(IccProfile.Parse(IccL_Profile("RGB ", "XYZ ", IccL_AdobeTags())));
        Assert.Null(IccProfile.Parse(IccL_Profile("RGB ", "RGB ", IccL_AdobeTags())));
    }

    private static byte[] IccN_NotAcsp()
    {
        var data = IccL_AdobeRgb();
        Encoding.ASCII.GetBytes("acsq", data.AsSpan(36));
        return data;
    }

    /// <summary>ICC.1 7.2.9 sentences 1–3 (reader): without 'acsp' at bytes 36–39 a profile is unreadable and the ICCBased space takes /Alternate.</summary>
    [Fact]
    public void IccProfileSignature_NotAcsp_Unreadable()
    {
        Assert.Null(IccProfile.Parse(IccN_NotAcsp()));
        Assert.Same(ColorSpace.DeviceGray, IccL_Space(IccN_NotAcsp(), 3, "DeviceGray"));
    }

    /// <summary>
    /// ICC.1 7.2.9 sentences 1–3 (checker): the PDF/A check of an ICCBased profile's header should find a profile whose bytes 36–39 are 'acsq' —
    /// class, colour space and version are valid, but it is no ICC profile.
    /// </summary>
    [Fact]
    public void IccProfileSignature_NotAcsp_CheckerReportsInvalidHeader()
    {
        Assert.True(PdfACheck.IccProfileHeaderValid(IccL_AdobeRgb(), 4));
        Assert.False(PdfACheck.IccProfileHeaderValid(IccN_NotAcsp(), 4));
    }

    /// <summary>
    /// ICC.1 7.2.16 sentences 1–4: the PCS illuminant at bytes 68–79 is D50 as XYZNumber, rounded to four places 0.9642, 1.0, 0.8249, in all own
    /// profiles.
    /// </summary>
    [Fact]
    public void IccOwnProfiles_PcsIlluminantD50()
    {
        foreach (var (name, p) in IccL_OwnProfiles())
        {
            double At(int i) => Math.Round(BinaryPrimitives.ReadInt32BigEndian(p.AsSpan(68 + 4 * i)) / 65536.0, 4);
            Assert.True(At(0) == 0.9642 && At(1) == 1.0 && At(2) == 0.8249, $"{name}: {At(0)} {At(1)} {At(2)}");
        }
    }

    /// <summary>ICC.1 7.3.1 sentences 7–11 (reader): of two entries with the same signature A2B0 the first applies.</summary>
    [Fact]
    public void IccDuplicateTagSignature_FirstEntryApplies()
    {
        var profile = IccProfile.Parse(IccL_Grey(("A2B0", IccL_ConstantGrey(0.2)), ("A2B0", IccL_ConstantGrey(0.4))))!;
        Assert.InRange(IccL_ToXyz(profile, 0.5f)[1], 0.1999f, 0.2001f);
    }

    /// <summary>
    /// ICC.1 7.3.2 sentences 1–4: the tag count is a uInt32 at byte 128. Over 1 000 entries (own limit) the profile is unreadable; a count larger
    /// than the table, running past the end of the data, keeps the entries there are — the colours stay.
    /// </summary>
    [Fact]
    public void IccTagCount_Over1000Unreadable_LargerCountKeepsTheEntries()
    {
        var data = IccL_AdobeRgb();
        var expected = IccL_ToXyz(IccProfile.Parse(data)!, 0.3f, 0.6f, 0.9f);
        var many = (byte[])data.Clone();
        IccL_SetU32(many, 128, 1001);
        Assert.Null(IccProfile.Parse(many));
        var beyond = (byte[])data.Clone();
        IccL_SetU32(beyond, 128, 999);
        Assert.True(132 + 12 * 999 > beyond.Length);
        Assert.Equal(expected, IccL_ToXyz(IccProfile.Parse(beyond)!, 0.3f, 0.6f, 0.9f));
    }

    /// <summary>
    /// ICC.1 7.3.4 sentences 1–5 (reader): offsets count from the start of the header; a tag whose offset lies behind the end of the data, or
    /// whose size runs past it, is passed over — here A2B1, so A2B0 applies.
    /// </summary>
    [Theory]
    [InlineData(100, 1)]
    [InlineData(-4, 100)]
    public void IccTagOffsetBeyondData_TagPassedOver(int offsetFromEnd, int size)
    {
        var data = IccL_Grey(("A2B0", IccL_ConstantGrey(0.2)), ("A2B1", IccL_ConstantGrey(0.4)));
        var index = IccL_Entries(data).FindIndex(e => e.Sig == "A2B1");
        IccL_SetU32(data, 136 + 12 * index, (uint)(data.Length + offsetFromEnd));
        IccL_SetU32(data, 140 + 12 * index, (uint)size);
        Assert.InRange(IccL_ToXyz(IccProfile.Parse(data)!, 0.5f)[1], 0.1999f, 0.2001f);
    }

    /// <summary>
    /// ICC.1 7.4 sentences 1–2: the norm limits sizes only by the 32-bit fields; PDF-Knead reads profiles up to 32 MB (own limit): one of
    /// exactly 32 MB is read, one of 32 MB + 4 bytes is unreadable and the ICCBased space takes /Alternate.
    /// </summary>
    [Fact]
    public void IccProfileOver32MB_Unreadable_AlternateApplies()
    {
        var data = IccL_AdobeRgb();
        byte[] Padded(int length)
        {
            var p = new byte[length];
            data.CopyTo(p, 0);
            IccL_SetU32(p, 0, (uint)length);
            return p;
        }
        Assert.NotNull(IccProfile.Parse(Padded(32 << 20)));
        var over = Padded((32 << 20) + 4);
        Assert.Null(IccProfile.Parse(over));
        Assert.Same(ColorSpace.DeviceGray, IccL_Space(over, 3, "DeviceGray"));
    }
}
