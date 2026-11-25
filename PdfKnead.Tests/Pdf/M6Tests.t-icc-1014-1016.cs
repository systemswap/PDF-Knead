using System.Buffers.Binary;
using System.Text;
using PdfKnead.Core.Imaging;
using PdfKnead.Core.Pdf.Document;
using PdfKnead.Core.Pdf.Graphics;
using PdfKnead.Core.Pdf.Graphics.Icc;
using PdfKnead.Core.Pdf.Writing;

namespace PdfKnead.Tests.Pdf;

/// <summary>
/// M6 draft t-icc-1014-1016: measurementType (ICC.1, 10.14), multiLocalizedUnicodeType (10.15) and multiProcessElementsType (10.16) — profiles
/// built here (helpers RIcc_ of the draft r-icc-1010-1011, own ones with the prefix TIcc_).
/// </summary>
public sealed partial class M6NormTests
{
    // ------------------------------------------------------------ 10.14 measurementType

    /// <summary>measurementType: observer, backing XYZ, geometry, flare (u16Fixed16), illuminant.</summary>
    private static byte[] TIcc_Meas(uint observer = 1, uint geometry = 1, double flare = 0, uint illuminant = 1, uint reserved = 0)
    {
        var d = new byte[36];
        "meas"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(4), reserved);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), observer);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12), (int)Math.Round(0.8 * 65536));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(16), (int)Math.Round(0.85 * 65536));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(20), (int)Math.Round(0.7 * 65536));
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(24), geometry);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(28), (uint)Math.Round(flare * 65536));
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(32), illuminant);
        return d;
    }

    /// <summary>
    /// ICC.1, 10.14 sentences 1, 2–6, 7–9, 10–12, 15–17, 18–20: the measurement conditions (meas) are not evaluated — PDF-Knead computes with the
    /// standard conditions of the PCS. A profile with a meas tag (valid, too short, reserved bytes set, observer 10°, geometry 0°:d, flare 100 %,
    /// illuminant D65) is read with exactly the colours of the same profile without it.
    /// </summary>
    [Theory]
    [InlineData("valid")]
    [InlineData("short")]
    [InlineData("reserved")]
    [InlineData("observer2")]
    [InlineData("geometry2")]
    [InlineData("flare1")]
    [InlineData("d65")]
    public void Meas_NotEvaluated_SameColours(string variant)
    {
        var meas = variant switch
        {
            "short" => TIcc_Meas()[..12],
            "reserved" => TIcc_Meas(reserved: 0xFFFFFFFF),
            "observer2" => TIcc_Meas(observer: 2),
            "geometry2" => TIcc_Meas(geometry: 2),
            "flare1" => TIcc_Meas(flare: 1),
            "d65" => TIcc_Meas(illuminant: 2),
            _ => TIcc_Meas(),
        };
        RIcc_AssertSameColours(RIcc_MatrixProfile(), RIcc_MatrixProfile(("meas", meas)));
    }

    // ------------------------------------------------------------ 10.15 multiLocalizedUnicodeType

    /// <summary>
    /// A multiLocalizedUnicodeType: <paramref name="count"/> and <paramref name="recordSize"/> as given, the records (language and country,
    /// length, offset) one every <paramref name="recordSize"/> bytes from byte 16, then <paramref name="tail"/>.
    /// </summary>
    private static byte[] TIcc_MlucRaw(uint count, int recordSize, (string Code, uint Length, uint Offset)[] records, byte[] tail)
    {
        var d = new byte[16 + recordSize * records.Length + tail.Length];
        "mluc"u8.CopyTo(d);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(8), count);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(12), (uint)recordSize);
        for (var r = 0; r < records.Length; r++)
        {
            var at = 16 + recordSize * r;
            Encoding.ASCII.GetBytes(records[r].Code, d.AsSpan(at, 4));
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(at + 4), records[r].Length);
            BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(at + 8), records[r].Offset);
        }
        tail.CopyTo(d, 16 + recordSize * records.Length);
        return d;
    }

    /// <summary>An mluc of records of the size 12, each with its own text (UTF-16BE) behind the records, in order.</summary>
    private static byte[] TIcc_Mluc(params (string Code, string Text)[] records)
    {
        var texts = records.Select(r => Encoding.BigEndianUnicode.GetBytes(r.Text)).ToArray();
        var at = 16 + 12 * records.Length;
        var list = new (string, uint, uint)[records.Length];
        for (var r = 0; r < records.Length; r++)
        {
            list[r] = (records[r].Code, (uint)texts[r].Length, (uint)at);
            at += texts[r].Length;
        }
        return TIcc_MlucRaw((uint)records.Length, 12, list, [.. texts.SelectMany(t => t)]);
    }

    /// <summary>A version-4 RGB matrix profile with the description (and, if given, the copyright) as given.</summary>
    private static byte[] TIcc_V4WithDesc(byte[] desc, byte[]? cprt = null) =>
        RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, [.. RIcc_MatrixTags(), ("desc", desc), .. cprt is null ? [] : new[] { ("cprt", cprt) }], major: 4);

    /// <summary>The data of a tag, null where the profile has none.</summary>
    internal static byte[]? TIcc_Tag(byte[] profile, string signature)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128));
        for (var i = 0; i < count; i++)
        {
            if (Encoding.ASCII.GetString(profile, 132 + 12 * i, 4) != signature) continue;
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(136 + 12 * i));
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(140 + 12 * i));
            return profile.AsSpan(offset, size).ToArray();
        }
        return null;
    }

    /// <summary>The ASCII part of a textDescriptionType (version 2), without its final zero.</summary>
    private static string TIcc_DescText(byte[] tag)
    {
        Assert.True(tag.AsSpan().StartsWith("desc"u8));
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(tag.AsSpan(8));
        return Encoding.ASCII.GetString(tag, 12, count).TrimEnd('\0');
    }

    /// <summary>
    /// ICC.1, 10.15 sentence 4: the layout of mluc is read as its table gives it — rewriting the version-4 example profile as 2.1 takes the first
    /// record of desc and cprt: desc as textDescriptionType „Beispiel-RGB v4 ? Gr?n“ (7-bit), cprt as textType „frei“.
    /// </summary>
    [Fact]
    public void AsVersion2_MlucTexts_AsVersion2Types()
    {
        var v2 = CalibratedProfile.AsVersion2(PdfARuleCases.Version4Rgb())!;
        Assert.NotNull(v2);
        Assert.Equal("Beispiel-RGB v4 ? Gr?n", TIcc_DescText(TIcc_Tag(v2, "desc")!));
        var cprt = TIcc_Tag(v2, "cprt")!;
        Assert.True(cprt.AsSpan().StartsWith("text"u8));
        Assert.Equal("frei", Encoding.ASCII.GetString(cprt, 8, cprt.Length - 8).TrimEnd('\0'));
    }

    /// <summary>
    /// ICC.1, 10.15 sentence 5: PDF-Knead writes no mluc (whose record size should be 12) — its own profiles carry no tag of that type, the
    /// description is a textDescriptionType.
    /// </summary>
    [Fact]
    public void OwnProfiles_NoMluc_DescriptionAsTextDescription()
    {
        foreach (var profile in new[] { CalibratedProfile.Srgb(), CalibratedProfile.Gray(null), CalibratedProfile.Rgb(null) })
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128));
            for (var i = 0; i < count; i++)
            {
                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(136 + 12 * i));
                Assert.False(profile.AsSpan(offset, 4).SequenceEqual("mluc"u8), Encoding.ASCII.GetString(profile, 132 + 12 * i, 4));
            }
            Assert.True(TIcc_Tag(profile, "desc")!.AsSpan().StartsWith("desc"u8));
        }
    }

    /// <summary>
    /// ICC.1, 10.15 sentences 6–7: the n-th record lies at n × record size + 16 — with a record size of 20 and two records („Eins“, „Zwei“)
    /// blanking the notices turns both texts into spaces of the same length, and the description read is „Eins“. (A record size near 2^31:
    /// IccCraftedProfileTests.Notices_MlucRecordSizeNear2Pow31_TextsStillBlanked.)
    /// </summary>
    [Fact]
    public void Mluc_RecordSize20_EachRecordFound()
    {
        var texts = Encoding.BigEndianUnicode.GetBytes("EinsZwei");
        var desc = TIcc_MlucRaw(2, 20, [("deDE", 8, 56), ("enUS", 8, 64)], texts);
        var profile = TIcc_V4WithDesc(desc);
        Assert.Equal("Eins", ImageMetadata.IccName(profile));
        var blank = ObjectMetadata.IccNotices(profile)!;
        Assert.NotNull(blank);
        Assert.Equal(profile.Length, blank.Length);
        var tag = TIcc_Tag(blank, "desc")!;
        Assert.Equal(new string(' ', 8), Encoding.BigEndianUnicode.GetString(tag, 56, 16));
    }

    /// <summary>
    /// ICC.1, 10.15 sentence 8: records may share their characters — two records with the same offset and length: the description is read,
    /// blanking turns the shared text into spaces, without an exception.
    /// </summary>
    [Fact]
    public void Mluc_RecordsShareTheirCharacters()
    {
        var desc = TIcc_MlucRaw(2, 12, [("deDE", 12, 40), ("deAT", 12, 40)], Encoding.BigEndianUnicode.GetBytes("Gemeinsam"[..6]));
        var profile = TIcc_V4WithDesc(desc);
        Assert.Equal("Gemein", ImageMetadata.IccName(profile));
        var blank = ObjectMetadata.IccNotices(profile)!;
        Assert.Equal(new string(' ', 6), Encoding.BigEndianUnicode.GetString(TIcc_Tag(blank, "desc")!, 40, 12));
    }

    /// <summary>
    /// ICC.1, 10.15 sentence 12: the characters are UTF-16BE without a final zero — a text „Profil“ followed by 0000h is read as „Profil“ for the
    /// description, and rewriting as 2.1 writes „Profil“ without a further character.
    /// </summary>
    [Fact]
    public void Mluc_TrailingZero_NotPartOfTheText()
    {
        var text = Encoding.BigEndianUnicode.GetBytes("Profil\0");
        var desc = TIcc_MlucRaw(1, 12, [("deDE", (uint)text.Length, 28)], text);
        var profile = TIcc_V4WithDesc(desc);
        Assert.Equal("Profil", ImageMetadata.IccName(profile));
        Assert.Equal("Profil", TIcc_DescText(TIcc_Tag(CalibratedProfile.AsVersion2(profile)!, "desc")!));
    }

    /// <summary>
    /// ICC.1, 10.15 sentence 15 (today's behaviour; the choice by language: design decision pending): records deDE „Grün“, enUS „Green“, deCH
    /// „Grüezi“ — no record is chosen by language or region, the description read is the first one, „Grün“.
    /// </summary>
    [Fact]
    public void Mluc_LanguageNotChosen_FirstRecordToday()
    {
        var profile = TIcc_V4WithDesc(TIcc_Mluc(("deDE", "Grün"), ("enUS", "Green"), ("deCH", "Grüezi")));
        Assert.Equal("Grün", ImageMetadata.IccName(profile));
    }

    /// <summary>
    /// ICC.1, 10.15 sentence 16: without a wanted language the first record applies — records „Erster“ and „Zweiter“: the description read is
    /// „Erster“, and rewriting as 2.1 writes „Erster“.
    /// </summary>
    [Fact]
    public void Mluc_NoWantedLanguage_FirstRecord()
    {
        var profile = TIcc_V4WithDesc(TIcc_Mluc(("deDE", "Erster"), ("enUS", "Zweiter")));
        Assert.Equal("Erster", ImageMetadata.IccName(profile));
        Assert.Equal("Erster", TIcc_DescText(TIcc_Tag(CalibratedProfile.AsVersion2(profile)!, "desc")!));
    }

    /// <summary>
    /// ICC.1, 10.15 sentences 19–24, 26: the header gives the number of records — an mluc with n = 0 whose bytes after the header look like a
    /// record (and a text „Hi“) has no text: the description read is null. (Rewriting as 2.1 with n = 0:
    /// IccCraftedProfileTests.AsVersion2_MlucOffsetFrom2Pow31OrNoRecord_ReplacementText.)
    /// </summary>
    [Fact]
    public void Mluc_NoRecords_NoDescription()
    {
        var desc = TIcc_MlucRaw(0, 12, [("deDE", 4, 28)], Encoding.BigEndianUnicode.GetBytes("Hi"));
        Assert.Null(ImageMetadata.IccName(TIcc_V4WithDesc(desc)));
    }

    // ------------------------------------------------------------ 10.16 multiProcessElementsType

    private static byte[] TIcc_U16(int v) => [(byte)(v >> 8), (byte)v];

    private static byte[] TIcc_U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static byte[] TIcc_F32(params float[] values)
    {
        var d = new byte[4 * values.Length];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleBigEndian(d.AsSpan(4 * i), values[i]);
        return d;
    }

    /// <summary>A processing element: signature, reserved, p inputs, q outputs, its data.</summary>
    private static byte[] TIcc_Element(string sig, int p, int q, byte[] data, uint reserved = 0) =>
        [.. Encoding.ASCII.GetBytes(sig), .. TIcc_U32(reserved), .. TIcc_U16(p), .. TIcc_U16(q), .. data];

    /// <summary>A formula segment (parf) of a function type with its parameters.</summary>
    private static byte[] TIcc_Parf(int type, params float[] p) => [.. "parf"u8, 0, 0, 0, 0, .. TIcc_U16(type), 0, 0, .. TIcc_F32(p)];

    /// <summary>A sampled segment (samf) with its values; the count as given, else theirs.</summary>
    private static byte[] TIcc_Samf(float[] values, uint? count = null) => [.. "samf"u8, 0, 0, 0, 0, .. TIcc_U32(count ?? (uint)values.Length), .. TIcc_F32(values)];

    /// <summary>A segmented curve (curf): the segment count as given, else theirs; the break points; the segments, each aligned to four bytes.</summary>
    private static byte[] TIcc_Curf(float[] breakPoints, byte[][] segments, int? count = null)
    {
        var d = new List<byte>([.. "curf"u8, 0, 0, 0, 0, .. TIcc_U16(count ?? segments.Length), 0, 0, .. TIcc_F32(breakPoints)]);
        foreach (var s in segments)
        {
            while (d.Count % 4 != 0) d.Add(0);
            d.AddRange(s);
        }
        return [.. d];
    }

    /// <summary>A curve set (cvst): p = q curves, their positions (relative to the element, from 12 + 8p), the curves aligned to four bytes.</summary>
    private static byte[] TIcc_Cvst(byte[][] curves, int? p = null, int? q = null, bool shareFirst = false)
    {
        var positions = new byte[8 * curves.Length];
        var data = new List<byte>();
        var start = 12 + positions.Length;
        for (var i = 0; i < curves.Length; i++)
        {
            if (shareFirst && i > 0)
            {
                positions.AsSpan(0, 8).CopyTo(positions.AsSpan(8 * i));
                continue;
            }
            while (data.Count % 4 != 0) data.Add(0);
            BinaryPrimitives.WriteUInt32BigEndian(positions.AsSpan(8 * i), (uint)(start + data.Count));
            BinaryPrimitives.WriteUInt32BigEndian(positions.AsSpan(8 * i + 4), (uint)curves[i].Length);
            data.AddRange(curves[i]);
        }
        return TIcc_Element("cvst", p ?? curves.Length, q ?? curves.Length, [.. positions, .. data]);
    }

    /// <summary>A linear curve of one formula segment (type 0, γ 1).</summary>
    private static byte[] TIcc_LinearCurf() => TIcc_Curf([], [TIcc_Parf(0, 1, 1, 0, 0)]);

    /// <summary>Three linear curves.</summary>
    private static byte[] TIcc_LinearCvst() => TIcc_Cvst([TIcc_LinearCurf(), TIcc_LinearCurf(), TIcc_LinearCurf()]);

    /// <summary>A matrix element (matf): p × q values row by row, then q offsets.</summary>
    private static byte[] TIcc_Matf(int p, int q, float[] values) => TIcc_Element("matf", p, q, TIcc_F32(values));

    private static byte[] TIcc_IdentityMatf() => TIcc_Matf(3, 3, [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0]);

    /// <summary>A table element (clut): 16 bytes of grid points (first p used), then the values.</summary>
    private static byte[] TIcc_Clut(int p, int q, byte[] grid, float[] values) => TIcc_Element("clut", p, q, [.. grid, .. TIcc_F32(values)]);

    /// <summary>A table 3 → 3 of two points per axis, the identity.</summary>
    private static byte[] TIcc_IdentityClut()
    {
        var values = new List<float>();
        for (var r = 0; r < 2; r++)
            for (var g = 0; g < 2; g++)
                for (var b = 0; b < 2; b++) values.AddRange([r, g, b]);
        return TIcc_Clut(3, 3, [2, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], [.. values]);
    }

    /// <summary>
    /// A multiProcessElementsType: input and output channels, the element count as given (else theirs), the position table from byte 16, the
    /// elements aligned to four bytes (elements given the same array share their data); <paramref name="gap"/> bytes before the first element
    /// leave it unaligned.
    /// </summary>
    private static byte[] TIcc_Mpet(int inputs, int outputs, byte[][] elements, uint? count = null, int gap = 0)
    {
        var positions = new byte[8 * elements.Length];
        var data = new List<byte>(new byte[gap]);
        var start = 16 + positions.Length;
        var placed = new Dictionary<byte[], int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < elements.Length; i++)
        {
            if (!placed.TryGetValue(elements[i], out var at))
            {
                if (i > 0 || gap == 0)
                    while (data.Count % 4 != 0) data.Add(0);
                at = start + data.Count;
                data.AddRange(elements[i]);
                placed[elements[i]] = at;
            }
            BinaryPrimitives.WriteUInt32BigEndian(positions.AsSpan(8 * i), (uint)at);
            BinaryPrimitives.WriteUInt32BigEndian(positions.AsSpan(8 * i + 4), (uint)elements[i].Length);
        }
        return [.. "mpet"u8, 0, 0, 0, 0, .. TIcc_U16(inputs), .. TIcc_U16(outputs), .. TIcc_U32(count ?? (uint)elements.Length), .. positions, .. data];
    }

    /// <summary>A valid chain: three curves, an identity matrix, an identity table.</summary>
    private static byte[] TIcc_ValidMpet() => TIcc_Mpet(3, 3, [TIcc_LinearCvst(), TIcc_IdentityMatf(), TIcc_IdentityClut()]);

    /// <summary>The D2B0 tag of a variant of <see cref="Mpet_NotEvaluated_ColoursFromA2B0"/>.</summary>
    private static byte[] TIcc_MpetVariant(string variant)
    {
        byte[] Patched(byte[] d, int at, byte[] value)
        {
            value.CopyTo(d, at);
            return d;
        }
        var unknown = TIcc_Element("xxxx", 3, 3, [1, 2, 3, 4]);
        var shared = TIcc_IdentityMatf();
        return variant switch
        {
            "valid" => TIcc_ValidMpet(),
            "inputOnBreakPoint" => TIcc_Mpet(3, 3, [TIcc_Cvst([.. Enumerable.Repeat(TIcc_Curf([0.5f], [TIcc_Parf(0, 1, 2, 0, 0), TIcc_Parf(0, 1, 0, 0.5f, 0)]), 3)])]),
            "manySameUnknown" => TIcc_Mpet(3, 3, [.. Enumerable.Repeat(unknown, 1000)]),
            "unknown" => TIcc_Mpet(3, 3, [TIcc_LinearCvst(), unknown]),
            "channelsSwapped" => TIcc_Mpet(4, 3, [TIcc_LinearCvst()]),
            "reserved" => Patched(TIcc_ValidMpet(), 4, TIcc_U32(0xFFFFFFFF)),
            "hugeCounts" => Patched(Patched(TIcc_ValidMpet(), 8, [0xFF, 0xFF, 0xFF, 0xFF]), 12, TIcc_U32(0xFFFFFFFF)),
            "noElements" => TIcc_Mpet(3, 3, [], 0),
            "offsetsBeyond" => Patched(TIcc_ValidMpet(), 16, TIcc_U32(0xFFFFFF00)),
            "unaligned" => TIcc_Mpet(3, 3, [TIcc_LinearCvst()], gap: 1),
            "sharedOffsets" => TIcc_Mpet(3, 3, [shared, shared]),
            "chainMismatch" => TIcc_Mpet(3, 3, [TIcc_LinearCvst(), TIcc_Matf(4, 3, new float[15])]),
            "matfOutOfRange" => TIcc_Mpet(3, 3, [TIcc_Matf(3, 3, [5, 0, 0, 0, 5, 0, 0, 0, 5, -2, -2, -2])]),
            "elementReserved" => TIcc_Mpet(3, 3, [TIcc_Element("matf", 3, 3, TIcc_F32(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0), 0xFFFFFFFF)]),
            "cvstHugeP" => TIcc_Mpet(3, 3, [Patched(TIcc_LinearCvst(), 8, [0xFF, 0xFF, 0xFF, 0xFF])]),
            "cvstQNotP" => TIcc_Mpet(3, 3, [TIcc_Cvst([TIcc_LinearCurf(), TIcc_LinearCurf(), TIcc_LinearCurf()], q: 2)]),
            "decreasingBreakPoints" => TIcc_Mpet(3, 3, [TIcc_Cvst([.. Enumerable.Repeat(
                TIcc_Curf([0.7f, 0.3f], [TIcc_Parf(0, 1, 1, 0, 0), TIcc_Parf(0, 1, 1, 0, 0), TIcc_Parf(0, 1, 1, 0, 0)]), 3)])]),
            "curveOffsetOutside" => TIcc_Mpet(3, 3, [Patched(TIcc_LinearCvst(), 12, TIcc_U32(0x7FFFFFF0))]),
            "curvesShared" => TIcc_Mpet(3, 3, [TIcc_Cvst([TIcc_LinearCurf(), TIcc_LinearCurf(), TIcc_LinearCurf()], shareFirst: true)]),
            "curf65535Segments" => TIcc_Mpet(3, 3, [TIcc_Cvst([.. Enumerable.Repeat(TIcc_Curf([], [TIcc_Parf(0, 1, 1, 0, 0)], count: 65535), 3)])]),
            "curfNoSegments" => TIcc_Mpet(3, 3, [TIcc_Cvst([.. Enumerable.Repeat(TIcc_Curf([], [], count: 0), 3)])]),
            "parfType7" => TIcc_Mpet(3, 3, [TIcc_Cvst([.. Enumerable.Repeat(TIcc_Curf([], [TIcc_Parf(7, 1, 1, 0, 0)]), 3)])]),
            "parfLogNegative" => TIcc_Mpet(3, 3, [TIcc_Cvst([.. Enumerable.Repeat(TIcc_Curf([], [TIcc_Parf(1, 1, -1, -2, 0)]), 3)])]),
            "segmentInfinite" => TIcc_Mpet(3, 3, [TIcc_Cvst([.. Enumerable.Repeat(TIcc_Curf([], [TIcc_Parf(0, float.PositiveInfinity, 1, 0, 0)]), 3)])]),
            "samfHugeN" => TIcc_Mpet(3, 3, [TIcc_Cvst([.. Enumerable.Repeat(
                TIcc_Curf([0.5f], [TIcc_Parf(0, 1, 1, 0, 0), TIcc_Samf([0.6f, 0.8f, 1], 0xFFFFFFFF)]), 3)])]),
            "samfNoValue" => TIcc_Mpet(3, 3, [TIcc_Cvst([.. Enumerable.Repeat(TIcc_Curf([0.5f], [TIcc_Parf(0, 1, 1, 0, 0), TIcc_Samf([], 0)]), 3)])]),
            "samfFirst" => TIcc_Mpet(3, 3, [TIcc_Cvst([.. Enumerable.Repeat(TIcc_Curf([0.5f], [TIcc_Samf([0.1f, 0.3f]), TIcc_Parf(0, 1, 1, 0, 0)]), 3)])]),
            "matfHuge" => TIcc_Mpet(3, 3, [TIcc_Matf(65535, 65535, [1, 0, 0])]),
            "matfShort" => TIcc_Mpet(3, 3, [TIcc_Matf(3, 3, [1, 0, 0])]),
            "matf3e38" => TIcc_Mpet(3, 3, [TIcc_Matf(3, 3, [3e38f, 3e38f, 3e38f, 3e38f, 3e38f, 3e38f, 3e38f, 3e38f, 3e38f, 3e38f, 3e38f, 3e38f])]),
            "clut15x255" => TIcc_Mpet(3, 3, [TIcc_Clut(15, 3, [255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0], [0, 0, 0])]),
            "clutUnusedGridSet" => TIcc_Mpet(3, 3, [Patched(TIcc_IdentityClut(), 12 + 3, [7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7])]),
            "clutOneGridPoint" => TIcc_Mpet(3, 3, [TIcc_Clut(3, 3, [1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], [0.5f, 0.5f, 0.5f])]),
            "acsOnly" => TIcc_Mpet(3, 3, [TIcc_Element("bACS", 3, 3, "test"u8.ToArray()), TIcc_Element("eACS", 3, 3, "test"u8.ToArray())]),
            "bacsPNotQ" => TIcc_Mpet(3, 3, [TIcc_Element("bACS", 3, 4, "test"u8.ToArray()), TIcc_Element("eACS", 4, 3, "test"u8.ToArray())]),
            _ => throw new ArgumentException(variant),
        };
    }

    /// <summary>An RGB table profile (lut16, PCS XYZ, a grid of three points per axis), the D2B0 tag added if given.</summary>
    private static byte[] TIcc_TableProfileWith(byte[]? d2b0)
    {
        var lut = RIcc_Lut16(3, 3, 3, i => [i[0] * 0x4000, i[1] * 0x3000 + i[2] * 0x1000, i[2] * 0x4000]);
        (string, byte[])[] tags = [("wtpt", RIcc_XyzTag(0.9642, 1, 0.8249)), ("A2B0", lut)];
        return RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, d2b0 is null ? tags : [.. tags, ("D2B0", d2b0)]);
    }

    /// <summary>
    /// ICC.1, 10.16.1–10.16.2.5 (every row of the draft t-icc-1014-1016 with T-MPET): PDF-Knead does not evaluate multiProcessElementsType —
    /// it always computes through A2B0 to A2B2 or matrix and curves, the fallback the standard asks for when an element is unknown. A D2B0 next
    /// to A2B0 — valid, with an unknown element, broken in its header, positions, curves, segments, matrix or table, with sizes that would ask
    /// for a lot of memory — leaves the colours exactly those of A2B0, and reading it costs no more memory.
    /// </summary>
    [Theory]
    [InlineData("valid")]                   // 10.16.1 sentences 1–3, 10.16.2.1 sentences 1–4
    [InlineData("manySameUnknown")]         // 10.16.1 sentences 4–6
    [InlineData("unknown")]                 // 10.16.1 sentence 7
    [InlineData("channelsSwapped")]         // 10.16.1 sentences 8–10
    [InlineData("reserved")]                // 10.16.1 sentences 11–18
    [InlineData("hugeCounts")]              // 10.16.1 sentences 19–27
    [InlineData("noElements")]              // 10.16.1 sentences 28–34
    [InlineData("offsetsBeyond")]           // 10.16.1 sentences 35–37
    [InlineData("unaligned")]               // 10.16.1 sentences 40–41
    [InlineData("sharedOffsets")]           // 10.16.1 sentences 42–43
    [InlineData("chainMismatch")]           // 10.16.2.1 sentences 5–7
    [InlineData("matfOutOfRange")]          // 10.16.2.1 sentences 8–10
    [InlineData("elementReserved")]         // 10.16.2.1 sentences 11–15
    [InlineData("cvstHugeP")]               // 10.16.2.2 sentences 1–5, 68
    [InlineData("cvstQNotP")]               // 10.16.2.2 sentences 8–10
    [InlineData("decreasingBreakPoints")]   // 10.16.2.2 sentences 11–12
    [InlineData("curveOffsetOutside")]      // 10.16.2.2 sentences 13–15
    [InlineData("curvesShared")]            // 10.16.2.2 sentences 16–20
    [InlineData("curf65535Segments")]       // 10.16.2.2 sentences 21–26
    [InlineData("curfNoSegments")]          // 10.16.2.2 sentences 27–31
    [InlineData("inputOnBreakPoint")]       // 10.16.2.2 sentences 32–35
    [InlineData("parfType7")]               // 10.16.2.2 sentences 36–40, 43–45
    [InlineData("parfLogNegative")]         // 10.16.2.2 sentences 46–52
    [InlineData("segmentInfinite")]         // 10.16.2.2 sentences 53–54
    [InlineData("samfHugeN")]               // 10.16.2.2 sentences 55–59
    [InlineData("samfNoValue")]             // 10.16.2.2 sentences 60–63
    [InlineData("samfFirst")]               // 10.16.2.2 sentences 65–67
    [InlineData("matfHuge")]                // 10.16.2.3 sentences 1–7
    [InlineData("matfShort")]               // 10.16.2.3 sentences 10–12, 22
    [InlineData("matf3e38")]                // 10.16.2.3 sentences 13–21
    [InlineData("clut15x255")]              // 10.16.2.4 sentences 1–10
    [InlineData("clutUnusedGridSet")]       // 10.16.2.4 sentences 11–25, 33
    [InlineData("clutOneGridPoint")]        // 10.16.2.4 sentences 28–32
    [InlineData("acsOnly")]                 // 10.16.2.5 sentences 1–3
    [InlineData("bacsPNotQ")]               // 10.16.2.5 sentences 4–12
    public void Mpet_NotEvaluated_ColoursFromA2B0(string variant)
    {
        var plain = TIcc_TableProfileWith(null);
        var with = TIcc_TableProfileWith(TIcc_MpetVariant(variant));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var parsed = IccProfile.Parse(with);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotNull(parsed);
        Assert.True(allocated < 1 << 20, $"{allocated} Byte");
        RIcc_AssertSameColours(plain, with);
    }

    /// <summary>
    /// ICC.1, 10.16.1 sentence 7: a profile whose only colour transform is a D2B0 (mpet) is not evaluated — Parse gives null, an ICCBased space
    /// with /Alternate /DeviceGray gives DeviceGray.
    /// </summary>
    [Fact]
    public void Mpet_OnlyD2B0_Unreadable_AlternateApplies()
    {
        var data = RIcc_Profile("mntr", IccProfile.RgbSpace, RIcc_XyzSpace, [("wtpt", RIcc_XyzTag(0.9642, 1, 0.8249)), ("D2B0", TIcc_ValidMpet())]);
        Assert.Null(IccProfile.Parse(data));
        using var doc = PdfDocument.Open(TestPdf.Simple(1).Build());
        Assert.Same(ColorSpace.DeviceGray, RIcc_IccBased(doc, data, 3, "DeviceGray"));
    }
}
